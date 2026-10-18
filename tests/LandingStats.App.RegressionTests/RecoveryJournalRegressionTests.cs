using System;
using System.IO;
using System.Linq;
using System.Text;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RecoveryJournalRegressionTests
{
    private static readonly RecoveryJournalIdentity Identity = new RecoveryJournalIdentity(
        Guid.Parse("032bec83-1f59-41d7-bf39-4ca43b42731b"), Guid.Parse("132bec83-1f59-41d7-bf39-4ca43b42731b"),
        new RuntimeClockEpoch(Guid.Parse("232bec83-1f59-41d7-bf39-4ca43b42731b"), 100, 10000000));
    private static readonly byte[] Payload = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
    public static void RunAll()
    {
        Assert(JournalCrc32.Compute(Encoding.ASCII.GetBytes("123456789"), 0, 9) == 0xcbf43926, "CRC standard vector mismatch");
        TornTailKeepsExactlyTheValidPrefix();
        CorruptionDoesNotSkipAnUnknownStateChange();
        FaultedWritesNeverAcknowledgeOrContinue();
        IdentityAndBoundsAreStrict();
        ActualFileOwnershipAndPreservation();
    }

    private static byte[] ThreeRecords()
    {
        using var memory = new MemoryStream();
        using var journal = new RecoveryJournal(memory, Identity, () => { }, _ => throw new Exception("unexpected repair"));
        for (var i = 0; i < 3; i++) journal.Append(JournalRecordKind.SourceMessage, 101 + i, Payload);
        Assert(journal.DurableSequence == 0 && journal.ReadCommitted().Count == 0, "append claimed durability");
        Assert(journal.Commit().Sequence == 3 && journal.ReadCommitted().Count == 3, "commit missing prefix");
        return memory.ToArray();
    }

    private static void TornTailKeepsExactlyTheValidPrefix()
    {
        var valid = ThreeRecords();
        var prefix = RecoveryJournal.HeaderLength + 2 * (RecoveryJournal.RecordHeaderLength + Payload.Length);
        // Every byte boundary in the final frame, not just a truncated payload at one convenient offset.
        for (var cut = prefix; cut < valid.Length; cut++)
        {
            using var memory = new MemoryStream(); memory.Write(valid, 0, cut);
            byte[]? tail = null;
            using var journal = new RecoveryJournal(memory, Identity, () => { }, offset =>
            {
                Assert(memory.Length == cut, "tail truncated before preservation");
                tail = memory.ToArray().Skip((int)offset).ToArray();
            });
            Assert(journal.DurableSequence == 2 && journal.ReadCommitted().Count == 2, "torn record invented or erased committed data");
            Assert(memory.ToArray().SequenceEqual(valid.Take(prefix)), "repaired prefix changed bytes");
            Assert(cut == prefix ? tail == null : tail!.SequenceEqual(valid.Skip(prefix).Take(cut - prefix)), "tail bytes not preserved exactly");
            journal.Append(JournalRecordKind.SourceMessage, 104, Payload);
            Assert(journal.Commit().Sequence == 3, "recovery did not resume the sequence");
        }
    }

    private static void CorruptionDoesNotSkipAnUnknownStateChange()
    {
        var valid = ThreeRecords();
        var prefix = RecoveryJournal.HeaderLength + RecoveryJournal.RecordHeaderLength + Payload.Length;
        foreach (var delta in new[] { 0, 8, 16, 24, 28, 32, RecoveryJournal.RecordHeaderLength, RecoveryJournal.RecordHeaderLength + 85 })
        {
            var corrupted = (byte[])valid.Clone(); corrupted[prefix + delta] ^= 0xff;
            using var memory = new MemoryStream(); memory.Write(corrupted, 0, corrupted.Length);
            byte[]? tail = null;
            using var journal = new RecoveryJournal(memory, Identity, () => { }, offset => tail = memory.ToArray().Skip((int)offset).ToArray());
            Assert(journal.RecoveredTail && journal.DurableSequence == 1 && journal.ReadCommitted().Count == 1, "reader skipped corruption and applied later state");
            Assert(tail!.SequenceEqual(corrupted.Skip(prefix)), "corrupted evidence or later complete record discarded");
        }

        var bytes = (byte[])valid.Clone(); bytes[prefix + RecoveryJournal.RecordHeaderLength] ^= 1;
        using var refused = new MemoryStream(); refused.Write(bytes, 0, bytes.Length);
        Throws<IOException>(() => new RecoveryJournal(refused, Identity, () => { }, _ => throw new IOException("preservation disk full")));
        Assert(refused.ToArray().SequenceEqual(bytes), "failed preservation changed original journal");
    }

    private static void FaultedWritesNeverAcknowledgeOrContinue()
    {
        using var memory = new FaultStream();
        var failFlush = false;
        var journal = new RecoveryJournal(memory, Identity, () => { if (failFlush) throw new IOException("flush failed"); }, _ => { });
        var mutable = (byte[])Payload.Clone();
        journal.Append(JournalRecordKind.SourceMessage, 101, mutable); mutable[0] = 255;
        Assert(journal.DurableSequence == 0, "append was ACKed before flush");
        journal.Commit();
        Assert(journal.ReadCommitted().Single().CopyPayload().SequenceEqual(Payload), "producer mutated persisted payload");
        var prefix = memory.ToArray();
        memory.BytesBeforeFailure = RecoveryJournal.RecordHeaderLength + 7;
        Throws<IOException>(() => journal.Append(JournalRecordKind.SourceMessage, 102, Payload));
        Assert(journal.WrittenSequence == 1 && journal.DurableSequence == 1, "partial write advanced the acknowledged sequence");
        Assert(journal.ReadCommitted().Single().CopyPayload().SequenceEqual(Payload), "partial write poisoned committed prefix");
        Throws<InvalidOperationException>(() => journal.Commit());
        Throws<InvalidOperationException>(() => journal.Append(JournalRecordKind.SourceMessage, 103, Payload));
        journal.Dispose(); memory.BytesBeforeFailure = null;
        byte[]? preserved = null;
        using (var recovered = new RecoveryJournal(memory, Identity, () => { }, offset => preserved = memory.ToArray().Skip((int)offset).ToArray()))
        {
            Assert(recovered.DurableSequence == 1 && preserved!.Length == RecoveryJournal.RecordHeaderLength + 7, "partial bytes not retained on recovery");
            Assert(memory.ToArray().SequenceEqual(prefix), "recovery changed the durable prefix");
        }
        using var second = new MemoryStream();
        using var flush = new RecoveryJournal(second, Identity, () => { if (failFlush) throw new IOException("flush failed"); }, _ => { });
        flush.Append(JournalRecordKind.SourceMessage, 101, Payload);
        failFlush = true;
        Throws<IOException>(() => flush.Commit());
        Assert(flush.DurableSequence == 0 && flush.ReadCommitted().Count == 0, "failed flush produced durable ACK");
        Throws<InvalidOperationException>(() => flush.Append(JournalRecordKind.SourceMessage, 102, Payload));
    }

    private static void IdentityAndBoundsAreStrict()
    {
        var valid = ThreeRecords();
        using var mismatch = new MemoryStream(); mismatch.Write(valid, 0, valid.Length);
        var other = new RecoveryJournalIdentity(Guid.NewGuid(), Identity.CaptureSessionId, Identity.Clock);
        Throws<InvalidDataException>(() => new RecoveryJournal(mismatch, other, () => { }, _ => throw new Exception("identity mismatch must not repair")));
        Assert(mismatch.ToArray().SequenceEqual(valid), "identity mismatch modified input");
        using var memory = new MemoryStream();
        using var journal = new RecoveryJournal(memory, Identity, () => { }, _ => { });
        Throws<ArgumentException>(() => journal.Append(JournalRecordKind.SourceMessage, 99, Payload));
        Throws<ArgumentException>(() => journal.Append((JournalRecordKind)999, 101, Payload));
        Throws<ArgumentException>(() => journal.Append(JournalRecordKind.SourceMessage, 101, new byte[RecoveryJournal.MaximumPayloadLength + 1]));
        var large = new byte[RecoveryJournal.MaximumPayloadLength];
        while (journal.CanAppend(large.Length)) journal.Append(JournalRecordKind.SourceMessage, 102, large);
        var length = memory.Length;
        Throws<InvalidOperationException>(() => journal.Append(JournalRecordKind.SourceMessage, 103, large));
        Assert(memory.Length == length && length <= RecoveryJournal.MaximumSegmentBytes, "segment grew past its bound");
        journal.Commit();
    }

    private static void ActualFileOwnershipAndPreservation()
    {
        var root = Path.Combine(Path.GetTempPath(), "journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "input.wal");
        try
        {
            using (var first = RecoveryJournal.Open(path, Identity))
            {
                first.Append(JournalRecordKind.SourceMessage, 101, Payload); first.Commit();
                Throws<IOException>(() => RecoveryJournal.Open(path, Identity));
            }
            var prefix = File.ReadAllBytes(path);
            var tail = new byte[] { 1, 2, 3, 4, 5 };
            using (var append = new FileStream(path, FileMode.Append, FileAccess.Write)) { append.Write(tail, 0, tail.Length); append.Flush(true); }
            using var recovered = RecoveryJournal.Open(path, Identity);
            Assert(recovered.RecoveredTail && recovered.DurableSequence == 1, "file recovery did not preserve one committed record");
            Assert(File.ReadAllBytes(recovered.PreservedTailPath!).SequenceEqual(tail), "real preserved file does not contain exact tail");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var actual = new byte[file.Length]; Assert(file.Read(actual, 0, actual.Length) == actual.Length && actual.SequenceEqual(prefix), "real file prefix changed");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    private sealed class FaultStream : MemoryStream
    {
        public int? BytesBeforeFailure;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!BytesBeforeFailure.HasValue) { base.Write(buffer, offset, count); return; }
            var written = Math.Min(count, BytesBeforeFailure.Value);
            base.Write(buffer, offset, written); BytesBeforeFailure -= written;
            if (written != count) throw new IOException("injected partial write");
        }
    }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
