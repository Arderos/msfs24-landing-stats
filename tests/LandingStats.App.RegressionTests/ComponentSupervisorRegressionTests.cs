using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ComponentSupervisorRegressionTests
{
    public static void RunManaged() => Task.Run(ManagedMatrix).GetAwaiter().GetResult();
    public static void RunProcesses() => Task.Run(ProcessMatrix).GetAwaiter().GetResult();
    private static ComponentRecoveryOptions Options() => new ComponentRecoveryOptions
    {
        Startup = TimeSpan.FromSeconds(3), OwnerStall = TimeSpan.FromMilliseconds(80), Probe = TimeSpan.FromMilliseconds(50),
        Stop = TimeSpan.FromMilliseconds(100), CheckInterval = TimeSpan.FromMilliseconds(5),
        InitialBackoff = TimeSpan.FromMilliseconds(1), MaximumBackoff = TimeSpan.FromMilliseconds(4),
        StableUsefulProgress = TimeSpan.FromMilliseconds(25), OperationDeadline = TimeSpan.FromSeconds(1)
    };
    private static async Task ManagedMatrix()
    {
        var definitions = RuntimeComponentCatalog.Create();
        Check(definitions.Count == Enum.GetValues(typeof(RuntimeComponent)).Length && definitions.Select(value => value.Component).Distinct().Count() == definitions.Count,
            "The recovery catalog omitted or duplicated a component.");
        foreach (var definition in definitions)
        {
            definition.Options.Validate();
            var active = 0; var created = 0; var maximumActive = 0; var retired = 0; var escalations = 0; var prematureRetirement = 0;
            using var supervisor = new ComponentSupervisor(definition.Component.ToString(), generation =>
            {
                Interlocked.Increment(ref created);
                return new ManagedComponentGeneration(generation, async context =>
                {
                    var owners = Interlocked.Increment(ref active); maximumActive = Math.Max(maximumActive, owners);
                    try
                    {
                        if (generation <= 20) throw new IOException("Repeated fixture failure.");
                        context.MarkReady();
                        while (true)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            context.Progress(true); await context.WaitForWorkAsync(TimeSpan.FromMilliseconds(5));
                        }
                    }
                    finally { Interlocked.Decrement(ref active); }
                });
            }, _ => { if (active != 0) Interlocked.Increment(ref prematureRetirement); retired++; return Task.CompletedTask; },
                (_, _) => escalations++, Options(), () => 0.5);
            supervisor.Changed += _ => throw new InvalidOperationException("Broken observer must not own recovery.");
            supervisor.Start();
            await Until(() => supervisor.Snapshot.Generation == 21 && supervisor.Snapshot.Failures == 0 && supervisor.Snapshot.State == SupervisionState.Running,
                "Repeated failures permanently disabled " + definition.Component.ToString());
            await supervisor.StopAsync();
            Check(prematureRetirement == 0, "Retirement preceded owner exit.");
            Check(created == 21 && retired == 21 && maximumActive == 1 && active == 0 && escalations == 0 && supervisor.Snapshot.State == SupervisionState.Stopped,
                "Recovery overlapped owners, lost a boundary, escalated a finished task or restarted after stop.");
            Throws<InvalidOperationException>(supervisor.Start);
        }
        await UnconfirmedExitIsFenced();
        await RetirementMustComplete();
        await ActiveOperationBudget();
    }
    private static async Task UnconfirmedExitIsFenced()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var escalated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var created = 0;
        using var supervisor = new ComponentSupervisor("managed-hang", generation =>
        {
            created++;
            return new ManagedComponentGeneration(generation, async context =>
            { context.MarkReady(); context.Progress(false, DependencyWait.Idle); await release.Task.ConfigureAwait(false); });
        }, _ => Task.CompletedTask, (_, _) => escalated.TrySetResult(true), Options());
        supervisor.Start();
        await Until(() => escalated.Task.IsCompleted, "Noncooperative managed owner did not reach its process boundary.");
        await Task.Delay(100);
        Check(created == 1 && supervisor.Snapshot.State == SupervisionState.Escalating, "A second managed owner started over a hung predecessor.");
        var stopped = supervisor.StopAsync();
        Check(!stopped.IsCompleted, "Shutdown incorrectly confirmed the hung owner's exit.");
        release.TrySetResult(true); await stopped;
        Check(created == 1 && supervisor.Snapshot.State == SupervisionState.Stopped, "Shutdown restarted or retained the released owner.");
    }
    private static async Task RetirementMustComplete()
    {
        var callbackFailures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var retired = 0; var created = 0;
        using var supervisor = new ComponentSupervisor("checkpoint-barrier", generation =>
        {
            created++;
            return new ManagedComponentGeneration(generation, async context =>
            {
                if (generation == 1) throw new IOException("Fixture failure.");
                context.MarkReady();
                while (true) { context.Progress(true); await context.WaitForWorkAsync(TimeSpan.FromMilliseconds(5)); context.CancellationToken.ThrowIfCancellationRequested(); }
            });
        }, async generation =>
        {
            if (generation != 1) return;
            if (++retired <= 3) throw new IOException("Checkpoint disk pressure.");
            entered.TrySetResult(true); await release.Task;
        }, (_, _) => callbackFailures.Enqueue("Unexpected escalation."), Options());
        supervisor.Start(); await Until(() => entered.Task.IsCompleted, "Retirement checkpoint did not retry.");
        await Task.Delay(50); Check(created == 1, "Recovery skipped its retirement checkpoint.");
        release.TrySetResult(true);
        await Until(() => supervisor.Snapshot.Generation == 2 && supervisor.Snapshot.State == SupervisionState.Running, "Recovery did not resume after the checkpoint became available.");
        await supervisor.StopAsync(); Check(retired == 4 && created == 2, "Checkpoint retries duplicated generation creation.");
        Check(callbackFailures.IsEmpty, string.Join("; ", callbackFailures));
    }
    private static async Task ActiveOperationBudget()
    {
        var options = Options(); options.OperationDeadline = TimeSpan.FromMilliseconds(400);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var created = 0; var escalations = 0;
        using (var supervisor = new ComponentSupervisor("bounded-operation", generation =>
        {
            created++;
            return new ManagedComponentGeneration(generation, async context =>
            {
                context.MarkReady(); context.Progress(false, queueDepth: 1, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.NewGuid());
                await release.Task;
            });
        }, _ => Task.CompletedTask, (_, _) => escalations++, options))
        {
            supervisor.Start(); await Until(() => created == 1, "Bounded operation did not start.");
            await Task.Delay(200); Check(created == 1 && escalations == 0, "A valid long operation was mistaken for an idle owner hang.");
            var stop = supervisor.StopAsync(); release.TrySetResult(true); await stop;
        }
        options = Options(); options.OperationDeadline = TimeSpan.FromMilliseconds(80); created = 0;
        using (var supervisor = new ComponentSupervisor("expired-operation", generation =>
        {
            created++;
            return new ManagedComponentGeneration(generation, async context =>
            {
                context.MarkReady(); var started = Stopwatch.GetTimestamp(); var operation = Guid.NewGuid();
                while (true)
                {
                    context.Progress(generation > 1, queueDepth: generation == 1 ? 1 : 0,
                        oldestPendingQpc: generation == 1 ? started : 0, operationId: generation == 1 ? operation : Guid.Empty);
                    await context.WaitForWorkAsync(TimeSpan.FromMilliseconds(5)); context.CancellationToken.ThrowIfCancellationRequested();
                }
            });
        }, _ => Task.CompletedTask, (_, _) => escalations++, options))
        {
            supervisor.Start(); await Until(() => supervisor.Snapshot.Generation == 2 && supervisor.Snapshot.State == SupervisionState.Running,
                "Healthy heartbeats hid an expired operation.");
            await supervisor.StopAsync(); Check(created == 2, "Expired operation recovery created extra owners.");
        }
        Check(escalations == 0, "Bounded or expired operation unexpectedly escalated during shutdown.");
    }
    private static async Task ProcessMatrix()
    {
        var executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe");
        foreach (ComponentRole role in Enum.GetValues(typeof(ComponentRole)))
        {
            var callbackFailures = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var session = Guid.NewGuid(); var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            ProcessWorkerHost? previous = null; var created = 0; var retired = 0;
            var options = Options(); options.Startup = TimeSpan.FromSeconds(5); options.OwnerStall = TimeSpan.FromMilliseconds(500);
            options.Probe = TimeSpan.FromMilliseconds(300); options.Stop = TimeSpan.FromSeconds(1);
            using var supervisor = new ComponentSupervisor(role.ToString(), generation =>
            {
                if (previous != null && previous.Exit.Status != TaskStatus.RanToCompletion)
                    callbackFailures.Enqueue("Replacement preceded process exit.");
                ProcessWorkerHost? host = null;
                host = new ProcessWorkerHost(executable, new WorkerIdentity(session, Guid.NewGuid(), generation, role), clock,
                    (message, _) => host!.AcknowledgeAsync(message.Sequence));
                previous = host; created++;
                return new ProcessComponentGeneration(host, options.Startup, generation <= 20 ? "crash" : "normal");
            }, async _ =>
            {
                await previous!.SourceCompletion.ConfigureAwait(false); retired++;
            }, (_, _) => callbackFailures.Enqueue("A fixture process could not be terminated."), options, () => 0.5);
            supervisor.Start();
            await Until(() => supervisor.Snapshot.Generation == 21 && supervisor.Snapshot.Failures == 0 && supervisor.Snapshot.State == SupervisionState.Running,
                "Process recovery did not resume useful work for " + role, 30);
            await supervisor.StopAsync();
            Check(created == 21 && retired == 21 && supervisor.Snapshot.State == SupervisionState.Stopped, "Process recovery lost its boundary or restarted after shutdown.");
            Check(callbackFailures.IsEmpty, string.Join("; ", callbackFailures));
        }
    }
    private static async Task Until(Func<bool> condition, string message, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(5); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
