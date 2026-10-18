using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ApplicationRuntimeRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "application-runtime-" + Guid.NewGuid().ToString("N"));
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
        var failed = 0; var captures = 0; var escalations = new ConcurrentQueue<string>(); var transitions = new ConcurrentQueue<string>();
        var options = new ComponentRecoveryOptions { Startup = TimeSpan.FromSeconds(10), OwnerStall = TimeSpan.FromSeconds(2), Probe = TimeSpan.FromSeconds(1),
            Stop = TimeSpan.FromSeconds(3), CheckInterval = TimeSpan.FromMilliseconds(20), InitialBackoff = TimeSpan.FromMilliseconds(10),
            MaximumBackoff = TimeSpan.FromMilliseconds(50), OperationDeadline = TimeSpan.FromSeconds(15) };
        using var runtime = new ApplicationRuntime(Path.Combine(root, "runtime"), identity, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"),
            (input, effects) =>
            {
                foreach (var capture in effects.Captures)
                {
                    store.CommitInputs(capture.Episode, capture.TimestampUtc, Array.Empty<AirportFacility>(), false, 1);
                    Interlocked.Increment(ref captures);
                    if (Interlocked.Exchange(ref failed, 1) == 0) throw new IOException("Crash after committing episode inputs, before command publication/checkpoint.");
                }
                foreach (var final in effects.Facilities) store.CommitFacilities(final);
                return Task.CompletedTask;
            }, (name, _) => escalations.Enqueue(name), (role, _) => role == ComponentRole.Simulator ? "landing" : "normal", options);
        try
        {
            runtime.HealthChanged += state =>
            {
                if (state.State == SupervisionState.Recovering || state.State == SupervisionState.Escalating)
                    transitions.Enqueue(state.Component + ":" + state.Generation + ":" + state.Reason);
            };
            runtime.Start();
            await Until(() => store.Enumerate().Any(id => !string.IsNullOrEmpty(store.Read(id).FacilityHash)), "Runtime did not recover the captured episode and final scenery.");
            Check(captures == 2 && store.Enumerate().Count() == 1 && runtime.Health.Single(item => item.Component == "recording").Generation >= 2 &&
                runtime.Health.Single(item => item.Component == "journal").Generation >= 2, "Composed runtime did not recover managed owners without duplicating the episode.");
            var simulator = runtime.Source(ComponentRole.Simulator); var controllers = runtime.Source(ComponentRole.Controllers);
            var simPid = simulator.CurrentHost!.ProcessId;
            await controllers.CurrentHost!.TerminateAsync(TimeSpan.FromSeconds(3));
            await Until(() => controllers.Snapshot.Generation == 2 && controllers.CurrentHost?.Identity.Generation == 2 && controllers.CurrentHost.LastAcknowledgedSequence >= 1,
                "Controller source did not restart through its checkpoint barrier.");
            Check(simulator.Snapshot.Generation == 1 && simulator.CurrentHost!.ProcessId == simPid, "Controller recovery restarted the independent simulator source.");
            var controllerPid = controllers.CurrentHost!.ProcessId;
            await simulator.CurrentHost!.TerminateAsync(TimeSpan.FromSeconds(3));
            await Until(() => simulator.Snapshot.Generation == 2 && store.Enumerate().Count() == 2 && store.Enumerate().All(id => !string.IsNullOrEmpty(store.Read(id).FacilityHash)),
                "Simulator recovery failed to accept a new independent flight after preserving the previous episode.");
            Check(controllers.Snapshot.Generation == 2 && controllers.CurrentHost!.ProcessId == controllerPid && escalations.IsEmpty,
                "Simulator recovery crossed another source's ownership or escalated unnecessarily. Controllers=" + controllers.Snapshot.Generation + ":" + controllers.CurrentHost?.ProcessId +
                "; expected PID=" + controllerPid + "; escalations=" + string.Join(",", escalations) + "; recovery=" + string.Join(" | ", transitions));
            await Deadline(runtime.StopAsync());
            Check(runtime.Health.All(item => item.State == SupervisionState.Stopped), "Application shutdown left an owner running.");
            Check(escalations.IsEmpty, "Application shutdown unexpectedly escalated: " + string.Join(",", escalations));
            using var journal = RollingRuntimeJournal.Open(Path.Combine(root, "runtime", "journal"), identity);
            var records = journal.RetainedSegments.SelectMany(segment => journal.ReadSegment(segment)).ToArray();
            Check(records.Any(item => item.Record.Kind == JournalRecordKind.CommandResult) && journal.CheckpointCursor != null,
                "Shutdown lost command outcomes or the final recording checkpoint.");
            var retirements = records.Where(item => item.Record.Kind == JournalRecordKind.SourceRetirement)
                .Select(item => RuntimeSourceRetirement.Decode(RuntimeInput.Decode(item.Record).CopyContent())).ToArray();
            Check(retirements.Length == 4 && retirements.Count(item => item.Intentional) == 2, "Shutdown and failure boundaries were not preserved separately.");
        }
        finally
        {
            if (runtime.Health.All(item => item.State == SupervisionState.Stopped)) Directory.Delete(root, true);
        }
    }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(message); await Task.Delay(15); } }
    private static async Task Deadline(Task task)
    { if (await Task.WhenAny(task, Task.Delay(30000)) != task) throw new TimeoutException("Runtime shutdown timed out."); await task; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
