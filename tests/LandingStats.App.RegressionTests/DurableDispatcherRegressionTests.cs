using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class DurableDispatcherRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static ComponentRecoveryOptions Options() => new ComponentRecoveryOptions
    { Startup = TimeSpan.FromSeconds(5), OwnerStall = TimeSpan.FromSeconds(1), Probe = TimeSpan.FromMilliseconds(200), Stop = TimeSpan.FromSeconds(1),
        CheckInterval = TimeSpan.FromMilliseconds(10), InitialBackoff = TimeSpan.FromMilliseconds(5), MaximumBackoff = TimeSpan.FromMilliseconds(25), StableUsefulProgress = TimeSpan.Zero };
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "dispatch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await FailedPublicationAndRecovery(Path.Combine(root, "retry"));
            await BoundedIntakeAndBlockedStorage(Path.Combine(root, "bounded"));
            await AtomicControllerJoin(Path.Combine(root, "joined"));
        }
        finally { Directory.Delete(root, true); }
    }
    private static RecoveryJournalIdentity Identity() => new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
    private static async Task FailedPublicationAndRecovery(string root)
    {
        var identity = Identity(); var firstFailure = true; var effects = 0; var total = 0; var restored = 0; JournalCursor? applied = null;
        var emittedIds = new HashSet<Guid>();
        var callbackFailures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Record(bool success, string reason) { if (!success) callbackFailures.Enqueue(reason); }
        Task Restore(byte[]? bytes, JournalCursor? cursor)
        {
            restored++; applied = cursor; emittedIds.Clear();
            effects = bytes == null ? 0 : BitConverter.ToInt32(bytes, 0); total = bytes == null ? 0 : BitConverter.ToInt32(bytes, 4); return Task.CompletedTask;
        }
        Task Publish(DurableRuntimeInput input)
        {
            if (firstFailure) { firstFailure = false; throw new IOException("Consumer failed after disk commit, before ACK."); }
            if (applied == null || input.Cursor.CompareTo(applied) > 0)
            {
                Record(emittedIds.Add(input.Input.Id), "Replay duplicated a logical operation.");
                var payload = input.Input.CopyContent(); effects++; total += payload[0]; applied = input.Cursor;
                Record(input.Input.TimestampUtc.Kind == DateTimeKind.Utc && input.Input.ArrivedQpc <= input.Input.AcceptedQpc, "Original input timing was lost.");
            }
            return Task.CompletedTask;
        }
        var dispatcher = new DurableRuntimeDispatcher(root, identity, Restore, Publish);
        using (var supervisor = new ComponentSupervisor("journal", generation => new ManagedComponentGeneration(generation, dispatcher.RunAsync),
            _ => Task.CompletedTask, (_, _) => callbackFailures.Enqueue("Journal fixture unexpectedly escalated."), Options()))
        {
            var receipts = new List<RuntimeInputReceipt>();
            for (var i = 0; i < 100; i++)
            {
                var bytes = new[] { (byte)i };
                Check(dispatcher.TrySubmit(JournalRecordKind.SourceMessage, bytes, Stopwatch.GetTimestamp(), out var receipt), "Small batch overflowed bounded intake.");
                bytes[0] = 255; receipts.Add(receipt!);
            }
            Check(receipts.All(value => !value.Durable.IsCompleted), "Intake fabricated a durable ACK before its writer existed.");
            supervisor.Start(); await WithDeadline(Task.WhenAll(receipts.Select(value => value.Durable)));
            Check(effects == 100 && total == 4950 && restored == 2 && dispatcher.PendingCount == 0, "Writer replacement changed, duplicated or lost accepted input.");
            var checkpoint = BitConverter.GetBytes(effects).Concat(BitConverter.GetBytes(total)).ToArray();
            await WithDeadline(dispatcher.SaveCheckpointAsync(applied!, checkpoint)); checkpoint[0] = 0;
            await WithDeadline(dispatcher.SaveCheckpointAsync(new JournalCursor(applied!.Segment, 1), new byte[] { 255 }));
            await WithDeadline(dispatcher.SaveCheckpointAsync(applied, new byte[] { 255 }));
            for (var i = 0; i < 10; i++) Check(dispatcher.TrySubmit(JournalRecordKind.RuntimeCommand, new byte[] { 1 }, Stopwatch.GetTimestamp(), out _), "Post-checkpoint intake failed.");
            await WithDeadline(dispatcher.CloseIntakeAsync()); await supervisor.StopAsync();
            Check(effects == 110 && total == 4960, "Drain did not preserve accepted local writes.");
            Throws<InvalidOperationException>(() => dispatcher.SaveCheckpointAsync(applied!, new byte[8]));
        }
        using (var journal = RollingRuntimeJournal.Open(root, identity))
        {
            var stored = journal.RetainedSegments.SelectMany(segment => journal.ReadSegment(segment)).Select(item => RuntimeInput.Decode(item.Record)).ToArray();
            Check(stored.Length == 110 && stored.Select(value => value.Id).Distinct().Count() == 110, "Recovery appended an already committed operation twice.");
            Check(BitConverter.ToInt32(journal.ReadCheckpoint()!, 0) == 100, "Producer mutated the persisted checkpoint.");
        }
        var recovered = new DurableRuntimeDispatcher(root, identity, Restore, Publish);
        using var newSupervisor = new ComponentSupervisor("journal-reopen", generation => new ManagedComponentGeneration(generation, recovered.RunAsync),
            _ => Task.CompletedTask, (_, _) => callbackFailures.Enqueue("Recovery fixture unexpectedly escalated."), Options());
        newSupervisor.Start(); await Until(() => effects == 110 && restored == 3, "Reopened dispatcher did not replay only the tail after its checkpoint.");
        Check(total == 4960, "Reopened runtime recomputed different effects."); await recovered.CloseIntakeAsync(); await newSupervisor.StopAsync();
        Check(callbackFailures.IsEmpty, string.Join("; ", callbackFailures));
    }
    private static async Task BoundedIntakeAndBlockedStorage(string root)
    {
        var callbackFailures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var identity = Identity(); using (var initial = RollingRuntimeJournal.Open(root, identity)) { }
        var dispatcher = new DurableRuntimeDispatcher(root, identity, (_, _) => Task.CompletedTask, _ => Task.CompletedTask);
        var input = new byte[1024 * 1024]; var count = 0; RuntimeInputReceipt? last = null;
        while (dispatcher.TrySubmit(JournalRecordKind.SourceMessage, input, Stopwatch.GetTimestamp(), out var receipt))
        { count++; last = receipt; if (count > 20) throw new InvalidOperationException("Runtime byte bound was ignored."); }
        Check(count > 0 && count < DurableRuntimeDispatcher.MaximumPending && dispatcher.PendingCount == count, "Intake was not limited by bytes.");
        var space = dispatcher.WaitForCapacityAsync(input.Length, System.Threading.CancellationToken.None); Check(!space.IsCompleted, "Full inbox did not apply backpressure.");
        using var locked = new FileStream(Path.Combine(root, "session.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var supervisor = new ComponentSupervisor("blocked-journal", generation => new ManagedComponentGeneration(generation, dispatcher.RunAsync),
            _ => Task.CompletedTask, (_, _) => callbackFailures.Enqueue("Storage fixture unexpectedly escalated."), Options());
        supervisor.Start(); await Until(() => supervisor.Snapshot.Generation >= 3, "Unavailable storage did not retry through the common lifecycle.");
        Check(!last!.Durable.IsCompleted && dispatcher.PendingCount == count, "Storage failure acknowledged or removed uncommitted input.");
        locked.Dispose(); await WithDeadline(last.Durable); await WithDeadline(space);
        await dispatcher.CloseIntakeAsync(); await supervisor.StopAsync();
        Throws<InvalidOperationException>(() => dispatcher.TrySubmit(JournalRecordKind.SourceMessage, new byte[1], Stopwatch.GetTimestamp(), out _));
        Check(callbackFailures.IsEmpty, string.Join("; ", callbackFailures));
    }
    private static async Task AtomicControllerJoin(string root)
    {
        var callbackFailures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var identity = Identity(); var epoch = identity.Clock;
        var controllers = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Controllers);
        var simulator = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator);
        ControllerInputPreparation? consumer = null; JournalCursor? applied = null; var fail = true; var restores = 0;
        var joined = new List<byte[]>();
        ControllerInputPreparation Preparation(byte[]? state) => new ControllerInputPreparation(state == null ? new ControllerJoiner(epoch) : ControllerJoiner.RestoreCheckpoint(state, epoch));
        Task Restore(byte[]? state, JournalCursor? cursor)
        { consumer = Preparation(state); applied = cursor; joined.Clear(); restores++; return Task.CompletedTask; }
        Task Publish(DurableRuntimeInput input)
        {
            if (input.Input.Kind == JournalRecordKind.JoinedFrame && fail) { fail = false; throw new IOException("Crash after committing a joined frame."); }
            if (applied == null || input.Cursor.CompareTo(applied) > 0)
            {
                consumer!.Replay(input.Input); applied = input.Cursor;
                if (input.Input.Kind == JournalRecordKind.JoinedFrame) joined.Add(input.Input.CopyContent());
            }
            return Task.CompletedTask;
        }
        var dispatcher = new DurableRuntimeDispatcher(root, identity, Restore, Publish, prepare: Preparation);
        using var supervisor = new ComponentSupervisor("joined-journal", generation => new ManagedComponentGeneration(generation, dispatcher.RunAsync),
            _ => Task.CompletedTask, (_, _) => callbackFailures.Enqueue("Joined writer unexpectedly escalated."), Options());
        RuntimeEnvelope Control(long sequence, long capture, RuntimePayload payload) => new RuntimeEnvelope(controllers, 0, sequence, epoch.OriginQpc + capture, payload);
        ControllerSnapshotMessage Axes(long capture, double value)
        { var clocks = new long[8]; clocks[0] = epoch.OriginQpc + capture; var values = new double[48]; values[1] = value; var valid = new bool[48]; valid[1] = true; return new ControllerSnapshotMessage(1, clocks, values, valid); }
        var messages = new[]
        {
            Control(1, 1, new WorkerHello("test", new string('a', 64), 1, epoch.Id, epoch.Frequency)),
            Control(2, 2, new ControllerCatalogMessage(1, new[] { new ControllerDescriptor(0, 0, "Stick", "product", 2, new uint[6], Enumerable.Repeat(65535u, 6).ToArray()) })),
            Control(3, 20, Axes(19, -25)), Control(4, 40, Axes(39, 75)),
            new RuntimeEnvelope(simulator, 1, 1, epoch.OriginQpc + 30, new TelemetryFrameMessage(new TelemetrySample { Sequence = 1 })),
        };
        RuntimeInputReceipt? last = null;
        foreach (var message in messages) Check(dispatcher.TrySubmit(JournalRecordKind.SourceMessage, RuntimeWire.Encode(message), Stopwatch.GetTimestamp(), out last), "Joined intake refused a small batch.");
        supervisor.Start(); await WithDeadline(last!.Durable);
        Check(restores == 2 && joined.Count == 1, "Recovery duplicated or lost the committed join.");
        var first = JoinedRuntimeFrame.Decode(joined[0]); var sample = first.Source.Payload as TelemetryFrameMessage;
        var appliedSample = sample!.CopySample(); first.Controllers.Apply(appliedSample);
        Check(appliedSample.RawControllerAxesPercent[1] == -25 && appliedSample.RawControllerAxesValid[1], "Journal joined a future measurement after recovery.");
        await dispatcher.SaveCheckpointAsync(applied!, consumer!.CaptureCheckpoint());
        await dispatcher.CloseIntakeAsync(); await supervisor.StopAsync();
        using (var journal = RollingRuntimeJournal.Open(root, identity))
        {
            var inputs = journal.RetainedSegments.SelectMany(segment => journal.ReadSegment(segment)).Select(item => RuntimeInput.Decode(item.Record)).ToArray();
            Check(inputs.Length == messages.Length && inputs.Count(input => input.Kind == JournalRecordKind.JoinedFrame) == 1 &&
                inputs.Last().CopyContent().SequenceEqual(joined[0]), "Durable ACK was detached from the exact joined frame or retried as a duplicate.");
        }
        dispatcher = new DurableRuntimeDispatcher(root, identity, Restore, Publish, prepare: Preparation);
        using var reopened = new ComponentSupervisor("joined-reopen", generation => new ManagedComponentGeneration(generation, dispatcher.RunAsync),
            _ => Task.CompletedTask, (_, _) => callbackFailures.Enqueue("Reopened joined writer unexpectedly escalated."), Options());
        reopened.Start();
        var second = new RuntimeEnvelope(simulator, 1, 2, epoch.OriginQpc + 60, new TelemetryFrameMessage(new TelemetrySample { Sequence = 2 }));
        Check(dispatcher.TrySubmit(JournalRecordKind.SourceMessage, RuntimeWire.Encode(second), Stopwatch.GetTimestamp(), out last), "Post-checkpoint frame was not accepted.");
        await WithDeadline(last!.Durable);
        Check(joined.Count == 1 && restores == 3, "Checkpoint replay changed the committed prefix.");
        var recovered = JoinedRuntimeFrame.Decode(joined[0]); var next = ((TelemetryFrameMessage)recovered.Source.Payload).CopySample(); recovered.Controllers.Apply(next);
        Check(next.RawControllerAxesPercent[1] == 75 && next.RawControllerAxesValid[1], "Joiner restart lost its current controller catalog or measurement.");
        await dispatcher.CloseIntakeAsync(); await reopened.StopAsync();
        Check(callbackFailures.IsEmpty, string.Join("; ", callbackFailures));
    }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(message); await Task.Delay(5); } }
    private static async Task WithDeadline(Task operation)
    { if (await Task.WhenAny(operation, Task.Delay(12000)) != operation) throw new TimeoutException("Runtime dispatcher fixture timed out."); await operation; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
