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
    public static void GracefulStopDrainsFinalSourceBurst() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "source-graceful-tail-" + Guid.NewGuid().ToString("N"));
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        var arrivals = new ConcurrentQueue<RuntimeEnvelope>();
        var retirements = new ConcurrentQueue<RuntimeSourceRetirement>();
        var failures = new ConcurrentQueue<string>();
        var dispatcher = new DurableRuntimeDispatcher(root, identity, (_, _) => Task.CompletedTask, input =>
        {
            if (input.Input.Kind == JournalRecordKind.SourceMessage) arrivals.Enqueue(RuntimeWire.Decode(input.Input.CopyContent()));
            else if (input.Input.Kind == JournalRecordKind.SourceRetirement) retirements.Enqueue(RuntimeSourceRetirement.Decode(input.Input.CopyContent()));
            return Task.CompletedTask;
        });
        using var writer = new ManagedComponentGeneration(1, dispatcher.RunAsync);
        using var source = new RuntimeWorkerSource("graceful-tail", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"),
            identity.RuntimeId, ComponentRole.Simulator, identity.Clock, dispatcher, _ => Task.CompletedTask,
            (name, _) => failures.Enqueue(name), new ComponentRecoveryOptions { CheckInterval = TimeSpan.FromMilliseconds(20) }, _ => "source-stop-tail");
        var stopped = false;
        try
        {
            await writer.StartAsync(System.Threading.CancellationToken.None);
            source.Start();
            await Until(() => source.CurrentHost?.LastAcknowledgedSequence >= 10, "Source did not establish its journal-backed data stream.");
            var host = source.CurrentHost!;
            await WithDeadline(source.StopAsync());
            await dispatcher.CloseIntakeAsync();
            await writer.StopAsync(TimeSpan.FromSeconds(5));
            stopped = true;
            Check(await host.Exit == 0, "Graceful source shutdown required forced termination or left output unacknowledged.");
            var frames = arrivals.Where(message => message.Payload is TelemetryFrameMessage).ToArray();
            Check(frames.Count(message => ((TelemetryFrameMessage)message.Payload).CopySample().OnGround) == 256,
                "Source messages published while handling Stop were lost before their durable ACK.");
            Check(arrivals.Select(message => message.Sequence).SequenceEqual(Enumerable.Range(1, arrivals.Count).Select(value => (long)value)),
                "Graceful stop did not retain the entire ordered source prefix.");
            Check(retirements.Count == 1 && retirements.Single().Intentional && retirements.Single().LastAccepted == host.LastReceivedSequence &&
                host.LastAcknowledgedSequence == host.LastReceivedSequence && host.LastReceivedSequence == arrivals.Count && failures.IsEmpty,
                "Source retired before its final durable cumulative ACK or restarted during shutdown.");
            using var disk = RollingRuntimeJournal.Open(root, identity);
            Check(disk.RetainedSegments.SelectMany(segment => disk.ReadSegment(segment)).Count() == arrivals.Count + 1,
                "Graceful tail or retirement was missing from the persisted journal.");
        }
        finally
        {
            if (!stopped) { await source.StopAsync(); await writer.StopAsync(TimeSpan.FromSeconds(5)); }
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }).GetAwaiter().GetResult();

    public static void BackpressuredStartupRetiresBeforeStorageRecovers() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "source-startup-retirement-" + Guid.NewGuid().ToString("N"));
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        var engine = ProcessingEngineLease.Capture(Path.Combine(root, "engines"), AppDomain.CurrentDomain.BaseDirectory);
        var executable = Path.Combine(engine.DirectoryPath, "LandingStats.WorkerHost.exe");
        var manifestPath = Path.Combine(engine.DirectoryPath, "engine.json");
        var retired = new ConcurrentQueue<RuntimeSourceRetirement>();
        var dispatcher = new DurableRuntimeDispatcher(Path.Combine(root, "journal"), identity, (_, _) => Task.CompletedTask, input =>
        {
            if (input.Input.Kind == JournalRecordKind.SourceRetirement) retired.Enqueue(RuntimeSourceRetirement.Decode(input.Input.CopyContent()));
            return Task.CompletedTask;
        });
        for (var i = 0; i < DurableRuntimeDispatcher.MaximumPending; i++)
            Check(dispatcher.TrySubmit(JournalRecordKind.SourceMessage, new byte[] { 0 }, Stopwatch.GetTimestamp(), out _), "Could not fill the bounded intake.");
        using var source = new RuntimeWorkerSource("blocked-startup", executable, identity.RuntimeId, ComponentRole.Controllers,
            identity.Clock, dispatcher, _ => Task.CompletedTask, (_, _) => { },
            new ComponentRecoveryOptions { Startup = TimeSpan.FromSeconds(20), CheckInterval = TimeSpan.FromMilliseconds(20) }, _ => "normal");
        using var writer = new ManagedComponentGeneration(1, dispatcher.RunAsync);
        var completed = false;
        try
        {
            source.Start();
            await Until(() => source.CurrentHost?.LastReceivedSequence == 1, "Startup never delivered its hello to the full journal.");
            var host = source.CurrentHost!;
            Check(host.ProcessId > 0 && !host.Exit.IsCompleted, "Fixture has no live retained worker.");
            try { using var blocked = new FileStream(manifestPath, FileMode.Open, FileAccess.Write, FileShare.None); throw new InvalidOperationException("Startup did not pin actual engine bytes."); }
            catch (IOException) { }
            var stop = source.StopAsync();
            await Until(() => host.Exit.IsCompleted, "Source retirement waited for unavailable journal capacity before killing its worker.");
            Check(!stop.IsCompleted && dispatcher.PendingCount == DurableRuntimeDispatcher.MaximumPending,
                "Retirement skipped the journal barrier or consumed unavailable capacity.");
            await writer.StartAsync(System.Threading.CancellationToken.None);
            await WithDeadline(stop);
            await dispatcher.CloseIntakeAsync();
            await writer.StopAsync(TimeSpan.FromSeconds(5));
            Check(retired.Count == 1 && retired.Single().LastAccepted == 0 && retired.Single().Intentional,
                "Unaccepted hello became an accepted source prefix during cancellation.");
            await Until(() => CanWrite(manifestPath), "Confirmed startup retirement retained its engine lease.");
            completed = true;
        }
        finally { if (completed) Directory.Delete(root, true); }
    }).GetAwaiter().GetResult();

    private static bool CanWrite(string path)
    {
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

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
