using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class GuardianApplicationRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "app-guardian-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? failure = null;
        try
        {
            await AppChannel(root, false);
            await AppChannel(root, true);
            await RuntimeProcess(root);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            // A failed test is evidence. In particular, never remove a live
            // runtime's journal because its bounded stop assertion failed.
            if (failure != null) Console.WriteLine("Guardian application fixture preserved: " + root);
            else Directory.Delete(root, true);
        }
    }

    // The child constructs the actual App and uses its production handshake,
    // pipe-loss continuation and exit acknowledgement. It never runs OnStartup
    // or opens the real user's history/UI.
    internal static int Probe(string mode, string directory)
    {
        var app = new App();
        return Task.Run(async () =>
        {
            var connect = (Task)typeof(App).GetMethod("ConnectGuardianAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null)!;
            await connect;
            File.WriteAllText(Path.Combine(directory, "connected"), "yes");
            if (mode == "runtime")
            {
                try
                {
                    await RuntimeHealth(Path.Combine(directory, "runtime"));
                    await Defaults();
                }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(directory, "runtime-failure.txt"), error.ToString());
                    await App.AcknowledgeExitAsync(GuardianStopReason.UserExit);
                    return 1;
                }
                await App.AcknowledgeExitAsync(GuardianStopReason.UserExit);
                return 0;
            }
            if (mode == "stop")
            {
                await App.AcknowledgeExitAsync(GuardianStopReason.UserExit);
                File.WriteAllText(Path.Combine(directory, "acknowledged"), "yes");
                return 0;
            }
            await Task.Delay(Timeout.Infinite);
            return 99;
        }).GetAwaiter().GetResult();
    }

    private static async Task AppChannel(string root, bool intentional)
    {
        var directory = Path.Combine(root, intentional ? "intentional" : "lost-channel"); Directory.CreateDirectory(directory);
        using var child = new GuardianGeneration(Guid.NewGuid(), 1);
        Exception? failure = null;
        try
        {
            await child.StartAsync(Assembly.GetExecutingAssembly().Location,
                new[] { "--app-guardian-probe", intentional ? "stop" : "disconnect", directory },
                AppDomain.CurrentDomain.BaseDirectory, null, TimeSpan.FromSeconds(15), visible: false);
            await Until(() => File.Exists(Path.Combine(directory, "connected")), "Actual App did not complete its guardian handshake.", 15);
            if (intentional)
            {
                await Until(() => child.StopIntent == GuardianStopReason.UserExit, "Actual App did not publish intentional shutdown.", 10);
                await Deadline(child.Exit, 10);
                Check(await child.Exit == 0 && File.Exists(Path.Combine(directory, "acknowledged")), "Actual App exited before acknowledging its drain intent.");
            }
            else
            {
                child.Cancel(); await Deadline(child.Exit, 10);
                Check(await child.Exit == 72, "Actual App did not terminate its orphaned generation after guardian channel loss.");
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally { await Retire(child, failure); }
    }

    private static async Task RuntimeProcess(string root)
    {
        using var child = new GuardianGeneration(Guid.NewGuid(), 1);
        Exception? failure = null;
        try
        {
            await child.StartAsync(Assembly.GetExecutingAssembly().Location,
                new[] { "--app-guardian-probe", "runtime", root },
                AppDomain.CurrentDomain.BaseDirectory, null, TimeSpan.FromSeconds(15), visible: false);
            await Deadline(child.Exit, 120);
            var errorPath = Path.Combine(root, "runtime-failure.txt");
            Check(await child.Exit == 0, "Owned runtime probe failed: " +
                (File.Exists(errorPath) ? File.ReadAllText(errorPath) : "exit=" + await child.Exit));
        }
        catch (Exception error) { failure = error; throw; }
        finally { await Retire(child, failure); }
        // RetireAsync confirms the native job's entire process tree is empty,
        // including descendants when the runtime probe itself already exited.
        Console.WriteLine("App guardian runtime probe: exact owned process tree retired before fixture cleanup.");
    }

    private static async Task Retire(GuardianGeneration child, Exception? failure)
    {
        try { await child.RetireAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception retirement)
        {
            if (failure != null) throw new AggregateException("Guardian fixture failed and its process tree did not retire.", failure, retirement);
            throw;
        }
    }

    private static async Task RuntimeHealth(string root)
    {
        var previous = App.RecoveryRequests.ToArray(); App.RecoveryRequests.Clear();
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        using var runtime = new LandingRuntime(root, AppDomain.CurrentDomain.BaseDirectory, Path.Combine(root, "identity"),
            new LandingRepository(Path.Combine(root, "history")), new object(), (_, __) => { blocked.TrySetResult(true); return release.Task; },
            App.RequestRuntimeRecovery, (_, __) => "normal"); // Real production recovery budgets.
        typeof(MainWindow).GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, runtime);
        Exception? failure = null;
        var trace = Path.Combine(Path.GetDirectoryName(root)!, "runtime-trace.txt");
        void Trace(string phase) => File.AppendAllText(trace, DateTime.UtcNow.ToString("O") + " " + phase + Environment.NewLine +
            string.Join(Environment.NewLine, runtime.Health.Select(item => item.Component + " " + item.State + " generation=" + item.Generation + " failures=" + item.Failures + " " + item.Reason)) + Environment.NewLine);
        try
        {
            Trace("starting");
            await runtime.StartAsync(false); await Deadline(blocked.Task, 15);
            Trace("ready and presentation blocked");
            var initial = window.ReadGuardianHealth(Stopwatch.GetTimestamp());
            Check(initial.Ready && initial.SafeToRestart == runtime.SafeToRestart && initial.ControlQpc > 0,
                "Window health lost runtime readiness, observation or safe-restart state.");
            await Until(() => App.RecoveryRequests.ContainsKey("presentation"), "Production presentation timeout did not reach App.RequestRuntimeRecovery.", 45);
            Trace("presentation escalated");
            var health = window.ReadGuardianHealth(Stopwatch.GetTimestamp());
            Check(health.Recovery == GuardianRecovery.Presentation && health.Component == "presentation", "Actual presentation escalation was mapped to destructive runtime recovery.");
            var analysis = runtime.Health.Single(item => item.Component == "analysis.episodes");
            App.RequestRuntimeRecovery(analysis.Component, analysis.Generation);
            Check(window.ReadGuardianHealth(Stopwatch.GetTimestamp()).Recovery == GuardianRecovery.Runtime, "Runtime failure did not take precedence over presentation failure.");
            App.RecoveryRequests.TryRemove(analysis.Component, out _);
            release.TrySetResult(true);
            await Until(() => runtime.Health.Single(item => item.Component == "presentation").Generation > 1, "Presentation owner did not recover after its pending callback drained.", 15);
            Check(window.ReadGuardianHealth(Stopwatch.GetTimestamp()).Recovery == GuardianRecovery.None, "A stale prior-generation App recovery request survived recovery.");
            Check(await window.PrepareGuardianRecoveryAsync(), "Actual window recovery did not drain a safe runtime.");
            Trace("window preparation drained runtime");
        }
        catch (Exception error) { failure = error; Trace("body failure: " + error); throw; }
        finally
        {
            release.TrySetResult(true);
            try
            {
                await Deadline(runtime.StopAsync(), 20);
                foreach (var lease in Directory.EnumerateFiles(Path.Combine(root, ".quota"), "*.lease"))
                { using var released = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                Trace("stop completed; all quota leases released");
            }
            catch (Exception stop)
            {
                Trace("stop failure: " + stop);
                if (failure != null) throw new AggregateException("Runtime body failed and its stop did not complete.", failure, stop);
                throw;
            }
            finally { App.RecoveryRequests.Clear(); foreach (var item in previous) App.RecoveryRequests[item.Key] = item.Value; }
        }
    }

    private static async Task Defaults()
    {
        var expected = new Dictionary<RuntimeComponent, int>
        {
            [RuntimeComponent.Simulator] = 30, [RuntimeComponent.Controllers] = 10,
            [RuntimeComponent.IngressJournal] = 30, [RuntimeComponent.Episodes] = 30,
            [RuntimeComponent.Facilities] = 90, [RuntimeComponent.Analysis] = 180,
            [RuntimeComponent.HistoryArchiveOutbox] = 60, [RuntimeComponent.TelemetryUpload] = 120,
            [RuntimeComponent.CloudSync] = 180, [RuntimeComponent.Presentation] = 30, [RuntimeComponent.IntegrationApi] = 30
        };
        var catalog = RuntimeComponentCatalog.Create();
        Check(catalog.Count == expected.Count && catalog.Select(item => item.Component).Distinct().Count() == expected.Count &&
            catalog.All(item => expected.TryGetValue(item.Component, out var seconds) && item.Options.OperationDeadline == TimeSpan.FromSeconds(seconds)),
            "Production component operation budgets changed without updating the guardian contract.");
        using var owner = new ComponentSupervisor("defaults", _ => throw new IOException("Retry fixture"), _ => Task.CompletedTask,
            (_, __) => { }, jitter: () => 0.5);
        Check(Enumerable.Range(1, 7).Select(i => owner.Backoff(i).TotalSeconds).SequenceEqual(new[] { 1d, 2, 5, 10, 30, 60, 60 }), "Production retry progression changed.");
        owner.Start();
        try
        {
            await Until(() => owner.Snapshot.Failures >= 1, "Default supervisor did not enter backoff.", 5);
            var observation = owner.ObservationQpc;
            await Until(() => owner.ObservationQpc > observation, "Retry wait stopped publishing its own observation.", 2);
            var now = Stopwatch.GetTimestamp();
            Check(new GuardianPolicy().Decide(now, now, new GuardianStatus(true, owner.ObservationQpc, now, true), GuardianStopReason.None, false, false) == GuardianDecision.Continue,
                "Production retry observation caused a healthy guardian to restart the application.");
        }
        finally { await owner.StopAsync(); }
    }
    private static async Task Until(Func<bool> ready, string message, int seconds)
    { var timer = Stopwatch.StartNew(); while (!ready()) { if (timer.Elapsed.TotalSeconds >= seconds) throw new TimeoutException(message); await Task.Delay(25); } }
    private static async Task Deadline(Task task, int seconds)
    { if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task) throw new TimeoutException("Guardian application fixture did not finish."); await task; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
