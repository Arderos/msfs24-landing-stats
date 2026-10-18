using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RuntimeSourceDispatchRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "source-dispatch-" + Guid.NewGuid().ToString("N"));
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        var active = new Dictionary<ComponentRole, WorkerIdentity>(); var sequences = new Dictionary<ComponentRole, long>();
        var processed = new HashSet<Guid>(); var barriers = new ConcurrentDictionary<Guid, Task>();
        var simulatorRetired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSimulator = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var journalReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = new ConcurrentQueue<DurableRuntimeInput>(); var controllerSnapshots = 0;
        var callbackFailures = new ConcurrentQueue<string>();
        void Record(bool success, string reason) { if (!success) callbackFailures.Enqueue(reason); }
        var dispatcher = new DurableRuntimeDispatcher(root, identity, (_, _) => { journalReady.TrySetResult(true); return Task.CompletedTask; }, input =>
        {
            if (!processed.Add(input.Input.Id)) return Task.CompletedTask;
            arrivals.Enqueue(input);
            if (input.Input.Kind == JournalRecordKind.SourceMessage)
            {
                var message = RuntimeWire.Decode(input.Input.CopyContent()); var role = message.Worker.Role;
                if (message.Payload is WorkerHello)
                {
                    Record(!active.ContainsKey(role), "Successor input overtook its predecessor's retirement.");
                    active[role] = message.Worker; sequences[role] = 0;
                }
                Record(active.TryGetValue(role, out var owner) && owner.Equals(message.Worker) && message.Sequence == sequences[role] + 1,
                    "Durable source stream changed identity or order.");
                sequences[role] = message.Sequence;
                if (message.Payload is ControllerSnapshotMessage) System.Threading.Interlocked.Increment(ref controllerSnapshots);
            }
            else if (input.Input.Kind == JournalRecordKind.SourceRetirement)
            {
                var retired = RuntimeSourceRetirement.Decode(input.Input.CopyContent());
                Record(active.TryGetValue(retired.Worker.Role, out var owner) && owner.Equals(retired.Worker) && sequences[retired.Worker.Role] == retired.LastAccepted,
                    "Retirement lost part of the accepted prefix.");
                active.Remove(retired.Worker.Role);
                var held = retired.Worker.Role == ComponentRole.Simulator && retired.Worker.Generation == 1;
                barriers[input.Input.Id] = held ? releaseSimulator.Task : Task.CompletedTask;
                if (held) simulatorRetired.TrySetResult(true);
            }
            else callbackFailures.Enqueue("Unexpected fixture input.");
            return Task.CompletedTask;
        });
        var options = new ComponentRecoveryOptions
        { Startup = TimeSpan.FromSeconds(8), OwnerStall = TimeSpan.FromSeconds(1), Probe = TimeSpan.FromMilliseconds(300), Stop = TimeSpan.FromSeconds(3),
            CheckInterval = TimeSpan.FromMilliseconds(20), InitialBackoff = TimeSpan.FromMilliseconds(10), MaximumBackoff = TimeSpan.FromMilliseconds(50), StableUsefulProgress = TimeSpan.Zero };
        using var journal = new ComponentSupervisor("source-journal", generation => new ManagedComponentGeneration(generation, dispatcher.RunAsync),
            _ => Task.CompletedTask, (_, _) => callbackFailures.Enqueue("Journal fixture escalated."), options);
        var sources = Enum.GetValues(typeof(ComponentRole)).Cast<ComponentRole>().Select(role => new RuntimeWorkerSource(role.ToString(),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), identity.RuntimeId, role, identity.Clock, dispatcher,
            id =>
            {
                if (barriers.TryGetValue(id, out var task)) return task;
                callbackFailures.Enqueue("Retirement was acknowledged before dispatch.");
                return Task.CompletedTask;
            },
            (_, _) => callbackFailures.Enqueue("Source fixture escalated."), options,
            generation => generation == 1 ? "crash" : role == ComponentRole.Controllers ? "controllers-local" : "normal")).ToArray();
        try
        {
            journal.Start(); await WithDeadline(journalReady.Task);
            foreach (var source in sources) source.Start();
            await WithDeadline(simulatorRetired.Task);
            await Until(() => controllerSnapshots >= 10 && sources.Where((_, index) => (ComponentRole)(index + 1) != ComponentRole.Simulator)
                .All(source => source.Snapshot.Generation == 2), "Other source roles did not recover while one retirement checkpoint was held.");
            var simulator = sources[0];
            Check(simulator.Snapshot.Generation == 1 && simulator.CurrentHost!.Exit.Status == TaskStatus.RanToCompletion,
                "A source restarted before the consumer completed its retirement barrier.");
            releaseSimulator.TrySetResult(true);
            await Until(() => sources.All(source => source.Snapshot.Generation == 2 && source.CurrentHost?.LastAcknowledgedSequence > 0), "Recovered sources did not receive durable ACKs.");
            Check(sources[1].CurrentHost!.LastAcknowledgedSequence > 1, "The isolated controller source did not receive a journal-backed data ACK.");
            await WithDeadline(Task.WhenAll(sources.Select(source => source.StopAsync())));
            await WithDeadline(dispatcher.CloseIntakeAsync()); await journal.StopAsync();
            Check(sources.All(source => source.Snapshot.Generation == 2 && source.Snapshot.State == SupervisionState.Stopped), "Shutdown restarted a source.");
            Check(journal.Snapshot.Generation == 1 && callbackFailures.IsEmpty, "Source assertions failed under the supervisor: " + string.Join("; ", callbackFailures));
            var retirements = arrivals.Where(value => value.Input.Kind == JournalRecordKind.SourceRetirement).Select(value => RuntimeSourceRetirement.Decode(value.Input.CopyContent())).ToArray();
            Check(retirements.Length == sources.Length * 2 && retirements.Count(value => value.Intentional) == sources.Length && active.Count == 0 &&
                Enum.GetValues(typeof(ComponentRole)).Cast<ComponentRole>().All(role =>
                    retirements.Count(value => value.Worker.Role == role && value.Worker.Generation == 1 && !value.Intentional) == 1 &&
                    retirements.Count(value => value.Worker.Role == role && value.Worker.Generation == 2 && value.Intentional) == 1),
                "Crash and intentional stop were not retained as distinct source boundaries.");
            using var disk = RollingRuntimeJournal.Open(root, identity);
            Check(disk.RetainedSegments.SelectMany(segment => disk.ReadSegment(segment)).Count() == arrivals.Count, "Published input was missing from the durable journal.");
        }
        finally
        {
            releaseSimulator.TrySetResult(true);
            foreach (var source in sources) source.Dispose(); journal.Dispose();
            // Normal execution already drained every owner. A failing fixture
            // keeps its files rather than deleting under a still-running owner.
            if (journal.Completion.IsCompleted && sources.All(source => source.Snapshot.State == SupervisionState.Stopped)) Directory.Delete(root, true);
        }
    }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(message); await Task.Delay(10); } }
    private static async Task WithDeadline(Task task)
    { if (await Task.WhenAny(task, Task.Delay(20000)) != task) throw new TimeoutException("Runtime source fixture timed out."); await task; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
