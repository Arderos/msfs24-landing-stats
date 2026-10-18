using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RecordingRuntimeOwnerRegressionTests
{
    public static void RunAll() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "recording-owner-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { await FailureAfterDurableEffects(root); await FullMailboxDoesNotBlockCheckpoint(); await PoisonInputRecovery(root); await OrphanBarriersRemainBounded(); }
        finally { Directory.Delete(root, true); }
    }).GetAwaiter().GetResult();
    private static ComponentRecoveryOptions Options() => new ComponentRecoveryOptions
    { Startup = TimeSpan.FromSeconds(5), OwnerStall = TimeSpan.FromSeconds(3), Probe = TimeSpan.FromMilliseconds(200), Stop = TimeSpan.FromSeconds(1),
        CheckInterval = TimeSpan.FromMilliseconds(10), InitialBackoff = TimeSpan.FromMilliseconds(5), MaximumBackoff = TimeSpan.FromMilliseconds(25), StableUsefulProgress = TimeSpan.Zero };
    private static RecoveryJournalIdentity Identity() => new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
    private static async Task FailureAfterDurableEffects(string root)
    {
        var identity = Identity(); var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
        var failed = false; var attempts = 0; RecordingRuntimeOwner? recording = null; DurableRuntimeDispatcher? journal = null;
        recording = new RecordingRuntimeOwner(identity, (input, effects) =>
        {
            foreach (var capture in effects.Captures)
            {
                attempts++;
                store.CommitInputs(capture.Episode, capture.TimestampUtc, Array.Empty<LandingStats.App.Models.AirportFacility>(), !capture.LocalOnly, 1);
                if (!failed) { failed = true; throw new IOException("Failure after effect commit, before recording checkpoint."); }
            }
            return Task.CompletedTask;
        }, (cursor, state) => journal!.SaveCheckpointAsync(cursor, state), error => journal!.InvalidateConsumer(error));
        journal = new DurableRuntimeDispatcher(Path.Combine(root, "journal"), identity, recording.RestoreAsync, recording.PublishAsync,
            prepare: bytes => new ControllerInputPreparation(bytes == null ? new ControllerJoiner(identity.Clock) :
                ControllerJoiner.RestoreCheckpoint(RecordingRuntimeState.RestoreCheckpoint(bytes, identity).CaptureJoinCheckpoint(), identity.Clock)));
        var escalations = new List<string>();
        using var recordSupervisor = new ComponentSupervisor("recording", generation => new ManagedComponentGeneration(generation, recording.RunAsync),
            _ => Task.CompletedTask, (name, _) => { lock (escalations) escalations.Add(name); }, Options());
        using var diskSupervisor = new ComponentSupervisor("journal", generation => new ManagedComponentGeneration(generation, journal.RunAsync),
            _ => Task.CompletedTask, (name, _) => { lock (escalations) escalations.Add(name); }, Options());
        recordSupervisor.Start(); diskSupervisor.Start();
        var worker = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator); long sequence = 0;
        RuntimeInputReceipt Submit(RuntimePayload payload)
        {
            var qpc = Stopwatch.GetTimestamp();
            var envelope = new RuntimeEnvelope(worker, payload is WorkerHello ? 0 : 1, ++sequence, qpc, payload);
            Check(journal.TrySubmit(JournalRecordKind.SourceMessage, RuntimeWire.Encode(envelope), qpc, out var receipt), "Small runtime fixture exhausted intake."); return receipt!;
        }
        Submit(new WorkerHello("fixture", new string('a', 64), 1, identity.Clock.Id, identity.Clock.Frequency));
        Submit(new LifecycleMessage(SourceLifecycle.Connected)); Submit(new AircraftMessage("MSFS", "Aircraft", "type", "model"));
        Submit(new TelemetryFrameMessage(Frame(1, false))); Submit(new TelemetryFrameMessage(Frame(1.1, true)));
        Check(journal.TrySubmit(JournalRecordKind.SourceRetirement, new RuntimeSourceRetirement(worker, 1, sequence, SourceGapReason.ProcessExit, false).Encode(),
            Stopwatch.GetTimestamp(), out var retired), "Retirement did not enter the journal.");
        await Deadline(retired!.Durable); var consumed = recording.WaitForCheckpointAsync(retired.Id); await Deadline(consumed);
        Check(failed && attempts == 2 && recordSupervisor.Snapshot.Generation >= 2 && diskSupervisor.Snapshot.Generation >= 2 && escalations.Count == 0,
            "Recording failure did not automatically recover both state and replay publication.");
        var ids = store.Enumerate().ToArray(); Check(ids.Length == 1, "Recovered effect created a duplicate episode transaction.");
        var saved = store.ReadInputs(store.Read(ids[0]));
        Check(saved.Episode.Samples.Count == 2 && saved.Episode.Samples.Any(value => value.OnGround), "Recovery lost the accepted contact.");
        await Deadline(recording.CloseIntakeAsync()); await Deadline(journal.CloseIntakeAsync());
        await diskSupervisor.StopAsync(); await recordSupervisor.StopAsync();
        lock (escalations) Check(escalations.Count == 0, "Recording shutdown unexpectedly escalated: " + string.Join(",", escalations));
        using var wal = RollingRuntimeJournal.Open(Path.Combine(root, "journal"), identity);
        Check(wal.CheckpointCursor != null && wal.RetainedSegments.SelectMany(segment => wal.ReadSegment(segment)).Count() == 6,
            "Effect recovery changed the accepted journal prefix or acknowledged retirement without a checkpoint.");
    }
    private static async Task FullMailboxDoesNotBlockCheckpoint()
    {
        var identity = Identity(); var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsumer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = 0; var accepted = 0; var checkpoints = 0; var invalidated = false;
        var recording = new RecordingRuntimeOwner(identity, async (_, _) =>
        {
            // Hold a known input until the bounded mailbox is full. Windows
            // timer granularity must not determine whether this test passes.
            if (Interlocked.Increment(ref processed) == 2) await releaseConsumer.Task;
            await Task.Yield();
        }, (_, _) => Interlocked.Increment(ref checkpoints) == 1 ? blocked.Task : Task.CompletedTask,
            _ => invalidated = true, TimeSpan.FromTicks(1));
        using var generation = new ManagedComponentGeneration(1, recording.RunAsync);
        await generation.StartAsync(CancellationToken.None); await recording.RestoreAsync(null, null);
        var producer = Task.Run(async () =>
        {
            for (var i = 1; i <= 2200; i++)
            {
                await recording.PublishAsync(new DurableRuntimeInput(new RuntimeInput(Guid.NewGuid(), JournalRecordKind.RuntimeCommand,
                    Stopwatch.GetTimestamp(), identity.Clock.OriginQpc, DateTime.UtcNow, new RecordingCommand(RecordingCommandKind.Tick).Encode()), new JournalCursor(1, i)));
                Interlocked.Increment(ref accepted);
            }
        });
        await Until(() => Volatile.Read(ref processed) == 2 && Volatile.Read(ref accepted) == 2050,
            "Fixture did not fill the recording mailbox while its consumer was held.");
        Check(!producer.IsCompleted, "A full recording mailbox failed to apply backpressure.");
        releaseConsumer.TrySetResult(true);
        await Deadline(producer); await Until(() => processed == 2200, "A pending checkpoint blocked the full recording mailbox.");
        Check(checkpoints == 1 && !blocked.Task.IsCompleted, "Checkpoint fixture did not hold the writer while recording continued.");
        var worker = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator); var id = Guid.NewGuid();
        await recording.PublishAsync(new DurableRuntimeInput(new RuntimeInput(id, JournalRecordKind.SourceRetirement, Stopwatch.GetTimestamp(), identity.Clock.OriginQpc,
            DateTime.UtcNow, new RuntimeSourceRetirement(worker, 0, 0, SourceGapReason.ProcessExit, false).Encode()), new JournalCursor(1, 2201)));
        var barrier = recording.WaitForCheckpointAsync(id);
        await Until(() => processed == 2201, "Retirement did not reach the recording owner.");
        Check(!barrier.IsCompleted, "Retirement skipped its durable checkpoint."); blocked.TrySetResult(true);
        await Deadline(barrier); Check(checkpoints >= 2, "Retirement was acknowledged by a checkpoint from before its input.");
        await Deadline(recording.CloseIntakeAsync()); await generation.StopAsync(TimeSpan.FromSeconds(1));
        Check(!invalidated, "Intentional shutdown was mistaken for a recording failure.");
    }
    private static TelemetrySample Frame(double time, bool ground) => new TelemetrySample
    { Sequence = ground ? 2 : 1, SimulationTimeSeconds = time, SimulationDeltaSeconds = .1, MotionSimulation = true, OnGround = ground,
        AboveGroundLevelFeet = ground ? 0 : 100, VelocityWorldYFps = ground ? 0 : -3, LatitudeDegrees = 42, LongitudeDegrees = 23, GForce = 1 };
    private static async Task PoisonInputRecovery(string root)
    {
        var area = Path.Combine(root, "poison"); var identity = Identity();
        var store = new EpisodeTransactionStore(Path.Combine(area, "episodes"), new string('a', 64));
        var persistence = new RecordingEffectPersistence(store, () => { }, (_, __) => Task.CompletedTask);
        SceneryCommandMessage? lookup = null; Exception? invalidation = null;
        RecordingRuntimeOwner? recording = null; DurableRuntimeDispatcher? journal = null;
        recording = new RecordingRuntimeOwner(identity, async (input, effects) =>
        {
            foreach (var command in effects.Commands)
                if (command.Command is SceneryCommandMessage scenery && !scenery.Cancel) lookup = scenery;
            await persistence.CommitAsync(input, effects);
        }, (cursor, state) => journal!.SaveCheckpointAsync(cursor, state), error => { invalidation = error; journal!.InvalidateConsumer(error); });
        journal = new DurableRuntimeDispatcher(Path.Combine(area, "journal"), identity, recording.RestoreAsync, recording.PublishAsync,
            prepare: bytes => new ControllerInputPreparation(bytes == null ? new ControllerJoiner(identity.Clock) :
                ControllerJoiner.RestoreCheckpoint(RecordingRuntimeState.RestoreCheckpoint(bytes, identity).CaptureJoinCheckpoint(), identity.Clock)));
        using var owner = new ManagedComponentGeneration(1, recording.RunAsync);
        using var writer = new ManagedComponentGeneration(1, journal.RunAsync);
        await owner.StartAsync(CancellationToken.None); await writer.StartAsync(CancellationToken.None);
        var worker = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator); long sequence = 0;
        RuntimeInputReceipt Submit(RuntimePayload payload, SceneryCommandMessage? context = null)
        {
            var qpc = Stopwatch.GetTimestamp();
            var envelope = new RuntimeEnvelope(worker, payload is WorkerHello ? 0 : 1, ++sequence, qpc, payload,
                episodeId: context?.Episode ?? Guid.Empty, lookupId: context?.Lookup ?? Guid.Empty, lookupStage: context?.Stage ?? LookupStage.None);
            Check(journal.TrySubmit(JournalRecordKind.SourceMessage, RuntimeWire.Encode(envelope), qpc, out var receipt), "Poison fixture exhausted intake."); return receipt!;
        }
        void Connect()
        { Submit(new WorkerHello("fixture", new string('a', 64), 1, identity.Clock.Id, identity.Clock.Frequency)); Submit(new LifecycleMessage(SourceLifecycle.Connected)); }
        Connect(); Submit(new TelemetryFrameMessage(Frame(1, false))); Submit(new TelemetryFrameMessage(Frame(1.1, true)));
        await Until(() => lookup != null, "Approach did not request scenery.");
        var badBytes = new byte[] { 1, 2, 3, 4 };
        var poisoned = Submit(new SceneryChunkMessage(Guid.NewGuid(), 0, badBytes.Length, true, new byte[32], badBytes), lookup);
        await Deadline(poisoned.Durable);
        Check(journal.TrySubmit(JournalRecordKind.SourceRetirement, new RuntimeSourceRetirement(worker, 1, sequence, SourceGapReason.ProcessExit, false).Encode(),
            Stopwatch.GetTimestamp(), out var retirement), "Rejected generation retirement was not accepted.");
        await Deadline(retirement!.Durable); await Deadline(recording.WaitForCheckpointAsync(retirement.Id));
        worker = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), 2, ComponentRole.Simulator); sequence = 0;
        Connect(); Submit(new TelemetryFrameMessage(Frame(2, false))); Submit(new TelemetryFrameMessage(Frame(2.1, true)));
        Check(journal.TrySubmit(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.Stop).Encode(), Stopwatch.GetTimestamp(), out var stop), "Stop failed after poison recovery.");
        await Deadline(stop!.Durable); await Deadline(recording.WaitForCheckpointAsync(stop.Id));
        await Deadline(recording.CloseIntakeAsync()); await Deadline(journal.CloseIntakeAsync());
        await writer.StopAsync(TimeSpan.FromSeconds(1)); await owner.StopAsync(TimeSpan.FromSeconds(1));
        Check(invalidation == null, "Malformed source restarted the recording owner instead of isolating its generation: " + invalidation);
        var captures = store.Enumerate().Select(id => store.ReadInputs(store.Read(id)).Episode).OrderBy(value => value.Samples[0].SimulationTimeSeconds).ToArray();
        Check(captures.Length == 2 && captures.All(value => value.Samples.Any(sample => sample.OnGround)), "Poison input lost the saved contact or stopped the replacement source.");
        Check(captures[0].Samples.Last().Sequence < captures[1].Samples.First().Sequence, "Worker replacement restarted the parent's sample sequence.");
        var quarantine = Path.Combine(area, "input-quarantine", poisoned.Id.ToString("N") + ".bin");
        using var file = File.OpenRead(quarantine); using var reader = new BinaryReader(file);
        Check(reader.ReadUInt16() == (ushort)JournalRecordKind.SourceMessage, "Quarantine lost the original record kind.");
        reader.ReadInt64(); var rejectedSequence = reader.ReadInt64(); var acceptedQpc = reader.ReadInt64(); Check(reader.ReadString().Length > 0, "Quarantine lost the rejection reason.");
        var original = RuntimeInput.Decode(new RecoveryJournalRecord(rejectedSequence, acceptedQpc, JournalRecordKind.SourceMessage, reader.ReadBytes(reader.ReadInt32())));
        Check(original.Id == poisoned.Id && RuntimeWire.Decode(original.CopyContent()).Payload is SceneryChunkMessage chunk && chunk.CopyBytes().SequenceEqual(badBytes), "Quarantine did not retain the exact rejected evidence.");
    }
    private static async Task OrphanBarriersRemainBounded()
    {
        var identity = Identity(); Exception? invalidated = null; long saved = 0; byte[]? checkpoint = null;
        var recording = new RecordingRuntimeOwner(identity, (_, __) => Task.CompletedTask, (cursor, bytes) =>
        { checkpoint = bytes; Interlocked.Exchange(ref saved, cursor.Sequence); return Task.CompletedTask; }, error => invalidated = error);
        using var owner = new ManagedComponentGeneration(1, recording.RunAsync);
        await owner.StartAsync(CancellationToken.None); await recording.RestoreAsync(null, null);
        Guid last = Guid.Empty;
        for (var batch = 0; batch < 3; batch++)
        {
            for (var i = 1; i <= 200; i++)
            {
                var ordinal = batch * 200 + i; last = Guid.NewGuid();
                var worker = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), ordinal, ComponentRole.Controllers);
                await recording.PublishAsync(new DurableRuntimeInput(new RuntimeInput(last, JournalRecordKind.SourceRetirement,
                    Stopwatch.GetTimestamp(), identity.Clock.OriginQpc, DateTime.UtcNow, new RuntimeSourceRetirement(worker, 0, 0, SourceGapReason.ProcessExit, false).Encode()), new JournalCursor(1, ordinal)));
            }
            await Deadline(recording.WaitForCheckpointAsync(last));
            Check(Volatile.Read(ref saved) >= (batch + 1) * 200, "Orphan-barrier batch was acknowledged before its checkpoint.");
            var barriers = (System.Collections.IDictionary)RecorderTestAccess.Get(recording, "_barriers")!;
            Check(barriers.Count <= 128, "Completed barriers without waiters grew beyond their bound.");
            await recording.RestoreAsync(checkpoint!, new JournalCursor(1, Volatile.Read(ref saved)));
        }
        await Deadline(recording.CloseIntakeAsync()); await owner.StopAsync(TimeSpan.FromSeconds(1));
        Check(invalidated == null, "Repeated recovered retirement barriers failed the recording owner.");
    }
    private static async Task Deadline(Task operation)
    { if (await Task.WhenAny(operation, Task.Delay(15000)) != operation) throw new TimeoutException("Recording owner fixture timed out."); await operation; }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(message); await Task.Delay(5); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
