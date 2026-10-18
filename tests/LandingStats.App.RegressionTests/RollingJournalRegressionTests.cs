using System;
using System.IO;
using System.Linq;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RollingJournalRegressionTests
{
    private static RecoveryJournalIdentity Identity() => new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 100, 1000));
    private static RuntimeJournalLimits Limits() => new RuntimeJournalLimits
    { SegmentBytes = 512, SegmentTime = TimeSpan.FromSeconds(1), RollingWindow = TimeSpan.FromSeconds(2), DiskBytes = 65536, CheckpointBytes = 2048 };
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "rolling-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            CheckpointsAndPins(Path.Combine(root, "pins"));
            InterruptedMarkers(Path.Combine(root, "markers"));
            TornSegments(Path.Combine(root, "torn"), false);
            TornSegments(Path.Combine(root, "sealed"), true);
            RepairedActiveTailCanBeSealed(Path.Combine(root, "repaired"));
            QuotaRetainsInputs(Path.Combine(root, "quota"));
            FirstSegmentOwnership(Path.Combine(root, "initial"));
        }
        finally { Directory.Delete(root, true); }
    }
    private static void CheckpointsAndPins(string root)
    {
        var identity = Identity(); var pin = Guid.NewGuid(); JournalCursor? first = null, last = null;
        var checkpoint = new byte[] { 1, 2, 3, 4 }; byte[]? originalSegment = null;
        using (var journal = RollingRuntimeJournal.Open(root, identity, Limits()))
        {
            Throws<IOException>(() => { using var second = RollingRuntimeJournal.Open(root, identity, Limits()); });
            for (var i = 0; i < 20; i++)
            {
                last = journal.Append(JournalRecordKind.SourceMessage, 101 + i * 1000, new[] { (byte)i });
                if (i == 0) first = last;
            }
            Check(journal.DurableCursor.Sequence == 0, "Append in a new segment claimed durability.");
            Throws<InvalidOperationException>(() => journal.SaveCheckpoint(last!, checkpoint));
            journal.Commit(); journal.Pin(pin, first!); journal.Pin(pin, first!);
            journal.SaveCheckpoint(last!, checkpoint); journal.Collect(100000);
            Check(journal.SegmentCount == 20 && journal.ReadCheckpoint()!.SequenceEqual(checkpoint), "Pinned episode was collected or checkpoint changed.");
            var values = journal.RetainedSegments.SelectMany(segment => journal.ReadSegment(segment)).Select(item => item.Record.CopyPayload()[0]).ToArray();
            Check(values.SequenceEqual(Enumerable.Range(0, 20).Select(i => (byte)i)), "Rotation lost or reordered accepted records.");
            originalSegment = File.ReadAllBytes(Path.Combine(root, "0000000000000001.wal"));
        }
        using (var recovered = RollingRuntimeJournal.Open(root, null, Limits()))
        {
            Check(recovered.CheckpointCursor!.CompareTo(last) == 0 && recovered.SegmentCount == 20, "Checkpoint/pin was not restored.");
            recovered.Collect(100000); Check(recovered.SegmentCount == 20, "Recovery forgot the retention pin.");
            recovered.Release(pin); recovered.Collect(100000); Check(recovered.SegmentCount == 1, "Completed inputs prevented bounded collection.");
            Throws<InvalidOperationException>(() => recovered.Pin(Guid.NewGuid(), first!));
            var cursor = recovered.Append(JournalRecordKind.ControllerJoin, 100001, new byte[] { 21 }); recovered.Commit();
            recovered.SaveCheckpoint(cursor, new byte[] { 9 });
            Check(Directory.EnumerateFiles(root, "checkpoint-*.bin").Count() == 1, "Obsolete checkpoints accumulated.");
        }
        // Collection marker persisted, but one old file remained at a crash boundary.
        File.WriteAllBytes(Path.Combine(root, "0000000000000001.wal"), originalSegment!);
        using var finished = RollingRuntimeJournal.Open(root, null, Limits());
        Check(!File.Exists(Path.Combine(root, "0000000000000001.wal")) && finished.ReadCheckpoint()![0] == 9,
            "Interrupted collection was not completed from the validated checkpoint.");
    }
    private static void InterruptedMarkers(string root)
    {
        var identity = Identity(); byte[] oldState;
        using (var journal = RollingRuntimeJournal.Open(root, identity, Limits()))
        {
            journal.Append(JournalRecordKind.SourceMessage, 101, new byte[] { 1 }); journal.Commit();
            oldState = File.ReadAllBytes(Path.Combine(root, "session.json"));
            journal.Append(JournalRecordKind.SourceMessage, 1101, new byte[] { 2 }); journal.Commit();
        }
        File.WriteAllBytes(Path.Combine(root, "session.json"), oldState);
        using (var recovered = RollingRuntimeJournal.Open(root, null, Limits()))
        {
            Check(recovered.DurableCursor.Segment == 2 && recovered.DurableCursor.Sequence == 1, "A complete segment ahead of the rotation marker was lost.");
            Check(recovered.RetainedSegments.SelectMany(number => recovered.ReadSegment(number)).Count() == 2, "Adopted rotation skipped input.");
        }
        File.Delete(Path.Combine(root, "0000000000000001.wal"));
        Throws<InvalidDataException>(() => { using var invalid = RollingRuntimeJournal.Open(root, null, Limits()); });
    }
    private static void TornSegments(string root, bool sealedSegment)
    {
        using (var journal = RollingRuntimeJournal.Open(root, Identity(), Limits()))
        {
            journal.Append(JournalRecordKind.SourceMessage, 101, new byte[] { 1, 2 });
            journal.Append(JournalRecordKind.SourceMessage, 1101, new byte[] { 3, 4 }); journal.Commit();
        }
        var path = Path.Combine(root, sealedSegment ? "0000000000000001.wal" : "0000000000000002.wal");
        var bytes = File.ReadAllBytes(path); File.WriteAllBytes(path, bytes.Take(bytes.Length - 1).ToArray());
        var second = File.ReadAllBytes(Path.Combine(root, "0000000000000002.wal"));
        if (sealedSegment)
        {
            for (var i = 0; i < 2; i++) Throws<InvalidDataException>(() => { using var invalid = RollingRuntimeJournal.Open(root, null, Limits()); });
            Check(File.ReadAllBytes(Path.Combine(root, "0000000000000002.wal")).SequenceEqual(second), "Sealed-segment corruption erased later evidence.");
        }
        else
        {
            using var recovered = RollingRuntimeJournal.Open(root, null, Limits());
            Check(recovered.RecoveredTail && recovered.DurableCursor.Sequence == 0, "Torn tail was treated as a committed record.");
            recovered.Append(JournalRecordKind.SourceRetirement, 1200, new byte[] { 5 }); recovered.Commit();
        }
        Check(Directory.EnumerateFiles(root, "*.tail-*.bin").Single() is string tail &&
            File.ReadAllBytes(tail).SequenceEqual(bytes.Skip(RecoveryJournal.HeaderLength).Take(bytes.Length - RecoveryJournal.HeaderLength - 1)),
            "Torn journal bytes were not retained exactly.");
    }
    private static void QuotaRetainsInputs(string root)
    {
        var limits = Limits(); limits.DiskBytes = 16384; var pin = Guid.NewGuid(); var count = 0; long before;
        using (var journal = RollingRuntimeJournal.Open(root, Identity(), limits))
        {
            var first = journal.Append(JournalRecordKind.SourceMessage, 101, new byte[200]); journal.Commit(); journal.Pin(pin, first);
            try
            {
                for (; count < 1000; count++) { journal.Append(JournalRecordKind.SourceMessage, 102 + count, new byte[200]); journal.Commit(); }
                throw new InvalidOperationException("Journal ignored its disk quota.");
            }
            catch (IOException) { }
            before = journal.AllocatedBytes; Check(before <= limits.DiskBytes, "Journal crossed its disk bound.");
            journal.Commit();
            var checkpoint = Enumerable.Repeat((byte)7, 1024).ToArray();
            journal.SaveCheckpoint(journal.DurableCursor, checkpoint);
            Check(journal.ReadCheckpoint()!.SequenceEqual(checkpoint), "Data quota prevented the nonempty recovery checkpoint.");
        }
        using var recovered = RollingRuntimeJournal.Open(root, null, limits);
        var items = recovered.RetainedSegments.SelectMany(segment => recovered.ReadSegment(segment)).ToArray();
        Check(items.Length >= count + 1 && File.Exists(Path.Combine(root, "0000000000000001.wal")), "Quota recovery erased pinned data.");
        recovered.SaveCheckpoint(recovered.DurableCursor, Array.Empty<byte>()); recovered.Release(pin); recovered.Collect(100000);
        Check(recovered.AllocatedBytes < before && recovered.SegmentCount == 1, "Recovery could not collect completed data after quota pressure.");
        recovered.Append(JournalRecordKind.SourceMessage, 100001, new byte[200]); recovered.Commit();
    }
    private static void RepairedActiveTailCanBeSealed(string root)
    {
        using (var journal = RollingRuntimeJournal.Open(root, Identity(), Limits()))
        { journal.Append(JournalRecordKind.SourceMessage, 101, new byte[] { 1 }); journal.Commit(); }
        var firstPath = Path.Combine(root, "0000000000000001.wal");
        using (var file = new FileStream(firstPath, FileMode.Append, FileAccess.Write)) file.Write(new byte[] { 2, 3, 4 }, 0, 3);
        using (var recovered = RollingRuntimeJournal.Open(root, null, Limits()))
        {
            Check(recovered.RecoveredTail, "Expected the interrupted append to be recovered.");
            recovered.Append(JournalRecordKind.SourceMessage, 102, new byte[] { 2 }); recovered.Commit();
        }
        using (var reopened = RollingRuntimeJournal.Open(root, null, Limits()))
        { reopened.Append(JournalRecordKind.SourceMessage, 1102, new byte[] { 3 }); reopened.Commit(); }
        using (var rotated = RollingRuntimeJournal.Open(root, null, Limits()))
            Check(rotated.RetainedSegments.SelectMany(number => rotated.ReadSegment(number)).Select(item => item.Record.CopyPayload()[0]).SequenceEqual(new byte[] { 1, 2, 3 }),
                "A repaired active tail became an integrity gap after rotation.");
        var bytes = File.ReadAllBytes(firstPath); File.WriteAllBytes(firstPath, bytes.Take(bytes.Length - 1).ToArray());
        for (var attempt = 0; attempt < 2; attempt++)
            Throws<InvalidDataException>(() => { using var damaged = RollingRuntimeJournal.Open(root, null, Limits()); });
        Check(Directory.EnumerateFiles(root, "*.tail-*.bin").Count() == 2, "A later sealed-segment corruption lost its own evidence.");
    }
    private static void FirstSegmentOwnership(string root)
    {
        Directory.CreateDirectory(root); var identity = Identity();
        DurableFile.WriteJson(Path.Combine(root, "session.json"), new RuntimeJournalState
        { Runtime = identity.RuntimeId, Capture = identity.CaptureSessionId, Clock = identity.Clock.Id, Origin = identity.Clock.OriginQpc, Frequency = identity.Clock.Frequency });
        using (var journal = RollingRuntimeJournal.Open(root, null, Limits())) Check(journal.DurableCursor.Sequence == 0, "Interrupted initial creation did not recover.");
        File.Delete(Path.Combine(root, "0000000000000001.wal"));
        Throws<InvalidDataException>(() => { using var invalid = RollingRuntimeJournal.Open(root, null, Limits()); });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
