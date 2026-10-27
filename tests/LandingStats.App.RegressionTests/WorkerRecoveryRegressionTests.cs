using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Presentation;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class WorkerRecoveryRegressionTests
{
    public static void RunReadiness() => Task.Run(Readiness).GetAwaiter().GetResult();
    private static async Task Readiness()
    {
        await StartupAndRecovery(false);
        await StartupAndRecovery(true);
        await RecoveryWithinFirstGeneration();
    }
    private static async Task StartupAndRecovery(bool failDuringStartup)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<string>();
        var options = new ComponentRecoveryOptions { CheckInterval = TimeSpan.FromMilliseconds(5),
            InitialBackoff = TimeSpan.Zero, MaximumBackoff = TimeSpan.Zero };
        // Exercise the production ten-second interval once; shorten only the second scenario.
        if (failDuringStartup) options.StableUsefulProgress = TimeSpan.FromMilliseconds(300);
        long recoveryStarted = 0, settledAt = 0; var observedRecovering = 0;
        using var supervisor = new ComponentSupervisor("source.controllers", generation => new ManagedComponentGeneration(generation, async owner =>
        {
            if (generation == 1)
            {
                await ready.Task;
                if (failDuringStartup) throw new IOException("Fixture startup failure.");
            }
            else Interlocked.Exchange(ref recoveryStarted, Stopwatch.GetTimestamp());
            owner.MarkReady();
            while (true)
            {
                if (generation == 1 && fail.Task.IsCompleted) throw new IOException("Fixture runtime failure.");
                owner.Progress(false, DependencyWait.Idle);
                await owner.WaitForWorkAsync(TimeSpan.FromMilliseconds(5));
                owner.CancellationToken.ThrowIfCancellationRequested();
            }
        }), _ => Task.CompletedTask, (_, _) => errors.Enqueue("Readiness escalated to the App."), options);
        supervisor.Changed += snapshot =>
        {
            if (snapshot.Generation != 2 || snapshot.State != SupervisionState.Waiting) return;
            if (!snapshot.Settled) Interlocked.Exchange(ref observedRecovering, 1);
            else Interlocked.CompareExchange(ref settledAt, Stopwatch.GetTimestamp(), 0);
        };
        supervisor.Start();
        try
        {
            await Until(() => supervisor.Snapshot.Generation == 1, "Initial owner did not start.");
            Check(!supervisor.Snapshot.Settled, "An owner became green before reporting readiness.");
            ready.TrySetResult(true);
            if (!failDuringStartup)
            {
                // No useful work is needed: an idle but ready worker must be green immediately.
                await Until(() => supervisor.Snapshot.Settled, "First readiness still waits for the recovery interval.", 3);
                Check(supervisor.Snapshot.Generation == 1 && supervisor.Snapshot.UnavailableSinceQpc == 0,
                    "Initial readiness retained its startup outage.");
                fail.TrySetResult(true);
            }
            await Until(() => Interlocked.Read(ref settledAt) != 0, "Restarted worker did not finish stabilization.", 15);
            var stableSeconds = (Interlocked.Read(ref settledAt) - Interlocked.Read(ref recoveryStarted)) / (double)Stopwatch.Frequency;
            Check(observedRecovering == 1 && stableSeconds >= options.StableUsefulProgress.TotalSeconds,
                "A restart skipped the stability interval after " + (failDuringStartup ? "startup" : "runtime") + " failure.");
            Check(supervisor.Snapshot.UnavailableSinceQpc == 0 && supervisor.Snapshot.Failure == "" && errors.IsEmpty,
                "Recovery retained its failure or escalated: " + string.Join("; ", errors));
        }
        finally { ready.TrySetResult(true); await supervisor.StopAsync(); }
    }
    private static async Task RecoveryWithinFirstGeneration()
    {
        var begin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ComponentRecoveryOptions { CheckInterval = TimeSpan.FromMilliseconds(5),
            StableUsefulProgress = TimeSpan.FromMilliseconds(300) };
        var errors = 0;
        using var supervisor = new ComponentSupervisor("analysis.episodes", generation => new ManagedComponentGeneration(generation, async owner =>
        {
            owner.MarkReady();
            while (!begin.Task.IsCompleted) { owner.Progress(false, DependencyWait.Idle); await Task.Delay(5); }
            using (var child = owner.TrackProcessRetirement())
            {
                child.ReportDelayedExit();
                await release.Task;
            }
            while (true)
            {
                owner.Progress(false, DependencyWait.Idle);
                await owner.WaitForWorkAsync(TimeSpan.FromMilliseconds(5));
                owner.CancellationToken.ThrowIfCancellationRequested();
            }
        }), _ => Task.CompletedTask, (_, _) => Interlocked.Increment(ref errors), options);
        var recoveryProgress = 0; long failureObserved = 0, recoveredAt = 0;
        supervisor.Changed += snapshot =>
        {
            if (snapshot.Reason == "worker_exit_pending") Interlocked.Exchange(ref failureObserved, Stopwatch.GetTimestamp());
            if (Interlocked.Read(ref failureObserved) == 0 || snapshot.State != SupervisionState.Waiting) return;
            if (!snapshot.Settled) Interlocked.Exchange(ref recoveryProgress, 1);
            else Interlocked.CompareExchange(ref recoveredAt, Stopwatch.GetTimestamp(), 0);
        };
        supervisor.Start();
        try
        {
            await Until(() => supervisor.Snapshot.Settled, "First generation did not become ready.");
            begin.TrySetResult(true);
            await Until(() => Interlocked.Read(ref failureObserved) != 0, "Delayed helper did not mark its owner unavailable.");
            release.TrySetResult(true);
            await Until(() => Interlocked.Read(ref recoveredAt) != 0, "Owner did not recover after its helper exited.");
            Check(recoveryProgress == 1 && (recoveredAt - failureObserved) / (double)Stopwatch.Frequency >= options.StableUsefulProgress.TotalSeconds,
                "Recovery within generation one incorrectly used the initial readiness shortcut.");
            Check(supervisor.Snapshot.Generation == 1 && errors == 0, "A helper failure unnecessarily replaced its owning scheduler.");
        }
        finally { begin.TrySetResult(true); release.TrySetResult(true); await supervisor.StopAsync(); }
    }
    public static void RunIsolation() => Task.Run(Isolation).GetAwaiter().GetResult();
    private static async Task Isolation()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<string>(); var created = 0; var retired = 0;
        var options = new ComponentRecoveryOptions { Startup = TimeSpan.FromSeconds(1), OwnerStall = TimeSpan.FromMilliseconds(40),
            Probe = TimeSpan.FromMilliseconds(20), Stop = TimeSpan.FromMilliseconds(25), CheckInterval = TimeSpan.FromMilliseconds(5),
            InitialBackoff = TimeSpan.FromMilliseconds(5), MaximumBackoff = TimeSpan.FromMilliseconds(10), StableUsefulProgress = TimeSpan.FromMilliseconds(40) };
        var pending = new PendingProcess(release.Task);
        using var supervisor = new ComponentSupervisor("source.controllers", generation =>
        {
            Interlocked.Increment(ref created);
            if (generation == 1) return pending;
            if (pending.Exit.Status != TaskStatus.RanToCompletion) errors.Enqueue("Successor preceded actual process exit.");
            return new ManagedComponentGeneration(generation, async owner =>
            {
                owner.MarkReady();
                while (true) { owner.Progress(false, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromMilliseconds(5)); }
            });
        }, _ => { Interlocked.Increment(ref retired); return Task.CompletedTask; }, (_, _) => errors.Enqueue("An isolated process escalated to the App."), options, () => 0.5);
        supervisor.Start();
        try
        {
            await Until(() => supervisor.Snapshot.Reason == "worker_exit_pending", "Worker did not reach delayed termination.");
            var since = supervisor.Snapshot.UnavailableSinceQpc;
            var policy = new GuardianPolicy { ControlStall = TimeSpan.FromMilliseconds(100) };
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(15); var now = Stopwatch.GetTimestamp();
                Check(created == 1 && retired == 0 && !pending.Exit.IsCompleted, "Unconfirmed exit released ownership.");
                Check(supervisor.Snapshot.UnavailableSinceQpc == since && since > 0, "Retirement retries reset the failure age.");
                var health = new GuardianStatus(true, supervisor.ObservationQpc, now, false, GuardianRecovery.None, "");
                Check(policy.Decide(now, now, health, GuardianStopReason.None, false, false) == GuardianDecision.Continue,
                    "Guardian would restart the healthy App while its worker retired.");
            }
            release.TrySetResult(true);
            await Until(() => supervisor.Snapshot.Generation == 2 && supervisor.Snapshot.Settled, "Late exit did not recover/settle an idle successor.");
            Check(supervisor.Snapshot.UnavailableSinceQpc == 0 && supervisor.Snapshot.Failure == "", "Stable recovery retained the outage.");
        }
        finally { release.TrySetResult(true); await supervisor.StopAsync(); }
        Check(errors.IsEmpty && created == 2 && retired == 2, string.Join("; ", errors));
        await ManagedOwnerRetiringChild();
        await RealHungControllerWhileRecording();
    }
    private static async Task ManagedOwnerRetiringChild()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retire = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = 0;
        var options = new ComponentRecoveryOptions { OwnerStall = TimeSpan.FromMilliseconds(100), Stop = TimeSpan.FromMilliseconds(25),
            CheckInterval = TimeSpan.FromMilliseconds(5), StableUsefulProgress = TimeSpan.FromMilliseconds(30) };
        using var supervisor = new ComponentSupervisor("analysis.episodes", generation => new ManagedComponentGeneration(generation, async owner =>
        {
            owner.MarkReady();
            while (!retire.Task.IsCompleted) { owner.Progress(false, DependencyWait.Idle); await Task.Delay(5); }
            // A helper inherits the owner through async calls/Task.Run. A routine
            // native retirement must not turn a healthy service yellow.
            await Task.Run(async () =>
            {
                Check(ReferenceEquals(ManagedComponentContext.Current.Value, owner), "Child cleanup lost its supervised owner.");
                using var scope = ManagedComponentContext.Current.Value!.TrackProcessRetirement();
                owner.Progress(false, DependencyWait.Idle);
                await delayed.Task;
                scope.ReportDelayedExit();
                await release.Task;
            });
            owner.CancellationToken.ThrowIfCancellationRequested();
        }), _ => Task.CompletedTask, (_, _) => Interlocked.Increment(ref errors), options);
        supervisor.Start();
        try
        {
            await Until(() => supervisor.Snapshot.Settled, "Idle managed service did not settle.");
            retire.TrySetResult(true); await Task.Delay(35);
            Check(supervisor.Snapshot.Settled, "Ordinary child cleanup reported a service failure.");
            delayed.TrySetResult(true);
            await Until(() => supervisor.Snapshot.Reason == "worker_exit_pending", "Delayed helper exit was not visible.");
            var stop = supervisor.StopAsync(); await Task.Delay(200);
            Check(!stop.IsCompleted && errors == 0 && supervisor.Snapshot.Generation == 1,
                "Managed scheduler escalated or released a child still holding native resources.");
            release.TrySetResult(true); await stop;
            Check(errors == 0, "Confirmed helper exit escalated its scheduler.");
        }
        finally { retire.TrySetResult(true); delayed.TrySetResult(true); release.TrySetResult(true); await supervisor.StopAsync(); }
    }
    private static async Task RealHungControllerWhileRecording()
    {
        var root = Path.Combine(Path.GetTempPath(), "worker-isolation-" + Guid.NewGuid().ToString("N"));
        var id = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        var effects = 0; var failures = new ConcurrentQueue<string>();
        var options = new ComponentRecoveryOptions { OwnerStall = TimeSpan.FromMilliseconds(500), Probe = TimeSpan.FromMilliseconds(150),
            Stop = TimeSpan.FromMilliseconds(300), CheckInterval = TimeSpan.FromMilliseconds(20), StableUsefulProgress = TimeSpan.FromMilliseconds(150),
            InitialBackoff = TimeSpan.FromMilliseconds(10), MaximumBackoff = TimeSpan.FromMilliseconds(30) };
        using var runtime = new ApplicationRuntime(root, id, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"),
            (_, __) => { Interlocked.Increment(ref effects); return Task.CompletedTask; }, (_, _) => failures.Enqueue("Runtime escalation"),
            (role, generation) => role == ComponentRole.Controllers && generation == 1 ? "owner-stall" : "normal", options);
        try
        {
            runtime.HealthChanged += item => { if (item.State == SupervisionState.Recovering || item.State == SupervisionState.Escalating)
                Console.WriteLine(item.Component + " generation=" + item.Generation + " " + item.Reason); };
            runtime.Start();
            await Until(() => runtime.Source(ComponentRole.Controllers).Snapshot.Generation >= 2 && runtime.Source(ComponentRole.Controllers).Snapshot.Settled,
                "Real hung controller did not recover independently.", 20);
            var before = effects;
            await Until(() => effects > before + 2, "Recording/clock stopped after controller recovery.");
            Check(runtime.Source(ComponentRole.Simulator).Snapshot.Generation == 1 && runtime.Health.Single(item => item.Component == "recording").Generation == 1,
                "Controller failure replaced simulator or recording owner.");
            Check(failures.IsEmpty, string.Join("; ", failures));
        }
        catch (Exception error) { Console.WriteLine(error); throw; }
        finally { await runtime.StopAsync(); }
        Directory.Delete(root, true);
    }
    public static void RunStatus()
    {
        var model = new WorkerHealthPresentation(); var start = Stopwatch.Frequency * 100;
        SupervisionSnapshot State(long generation, bool settled = false, string reason = "creating_owner") =>
            new SupervisionSnapshot("source.controllers", generation, settled ? SupervisionState.Running : SupervisionState.Recovering,
                1, reason, settled, settled ? 0 : start, settled ? "" : "The controller stopped responding.");
        WorkerHealthItem Read(long seconds, SupervisionSnapshot snapshot) => model.Update(new[] { snapshot }, start + Stopwatch.Frequency * seconds, false, false).Single(item => item.Key == "Controllers");
        Check(Read(0, State(1)).Indicator == WorkerIndicator.Recovering, "Startup is not yellow.");
        Check(Read(29, State(8)).Indicator == WorkerIndicator.Recovering, "Failure appeared before its budget.");
        var red = Read(30, State(9));
        Check(red.Indicator == WorkerIndicator.Failed && red.Warning && red.Detail.Contains("stopped responding"), "Restarts reset the budget or lost the cause.");
        model.Acknowledge(new[] { "Controllers" });
        Check(!Read(50, State(15)).Warning && Read(50, State(15)).Indicator == WorkerIndicator.Failed, "OK changed health or repeated the same warning.");
        Check(Read(51, State(15, true)).Indicator == WorkerIndicator.Healthy, "Stable recovery is not green.");
        var later = new SupervisionSnapshot("source.controllers", 16, SupervisionState.Recovering, 1, "creating_owner", false, start + Stopwatch.Frequency * 52, "new fault");
        Check(Read(53, later).Indicator == WorkerIndicator.Recovering && Read(83, later).Warning, "A new incident stayed acknowledged or inherited the previous timeout.");
        var visible = model.Update(Array.Empty<SupervisionSnapshot>(), start, false, false);
        Check(visible.Count == 5 && visible.All(item => item.Key != "Cloud" && item.Key != "Api"), "Disabled optional workers were displayed as broken.");
        Check(model.Update(Array.Empty<SupervisionSnapshot>(), start, true, true).Count == 7, "Enabled workers are missing.");
    }
    private sealed class PendingProcess : IIsolatedComponentGeneration
    {
        private readonly Task _release;
        private readonly TaskCompletionSource<bool> _exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public PendingProcess(Task release) { _release = release; }
        public Task Completion { get; } = new TaskCompletionSource<bool>().Task;
        public Task Exit => _exit.Task;
        public ComponentHealthMessage? Health => null;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<bool> ProbeAsync(CancellationToken token) => Task.FromResult(false);
        public Task StopAsync(TimeSpan deadline) => Task.FromException(new TimeoutException("Native owner did not stop."));
        public Task TerminateAsync(TimeSpan deadline)
        {
            if (!_release.IsCompleted) return Task.FromException(new TimeoutException("Native tree still has active processes."));
            _exit.TrySetResult(true); return Task.CompletedTask;
        }
        public void Dispose() { Check(Exit.IsCompleted, "Disposed the isolated generation before its exit."); }
    }
    private static async Task Until(Func<bool> condition, string message, int seconds = 5)
    { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(10); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
