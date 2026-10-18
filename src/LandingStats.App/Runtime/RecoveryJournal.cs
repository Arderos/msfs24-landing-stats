using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal enum JournalRecordKind : ushort { SourceMessage = 1, SourceRetirement = 2, RuntimeCommand = 3, Checkpoint = 4, JoinedFrame = 5, ControllerJoin = 6, CommandResult = 7 }

internal sealed class RecoveryJournalIdentity
{
    public RecoveryJournalIdentity(Guid runtimeId, Guid captureSessionId, RuntimeClockEpoch clock)
    {
        if (runtimeId == Guid.Empty || captureSessionId == Guid.Empty) throw new ArgumentException("Invalid journal identity.");
        RuntimeId = runtimeId; CaptureSessionId = captureSessionId; Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }
    public Guid RuntimeId { get; }
    public Guid CaptureSessionId { get; }
    public RuntimeClockEpoch Clock { get; }
}

internal sealed class RecoveryJournalRecord
{
    private readonly byte[] _payload;
    internal RecoveryJournalRecord(long sequence, long receiptQpc, JournalRecordKind kind, byte[] payload)
    { Sequence = sequence; ReceiptQpc = receiptQpc; Kind = kind; _payload = payload; }
    public long Sequence { get; }
    public long ReceiptQpc { get; }
    public JournalRecordKind Kind { get; }
    public byte[] CopyPayload() => (byte[])_payload.Clone();
}

internal readonly struct JournalCommit
{
    internal JournalCommit(long sequence, long length) { Sequence = sequence; Length = length; }
    public long Sequence { get; }
    public long Length { get; }
}

/// <summary>
/// One bounded WAL segment, owned by the storage component. Appending is not a durable ACK.
/// Commit returns only after Flush(true). Recovery keeps the valid prefix and preserves every
/// discarded tail byte before truncation. A failed writer must be retired before another opens it.
/// </summary>
internal sealed class RecoveryJournal : IDisposable
{
    public const int HeaderLength = 84;
    public const int RecordHeaderLength = 36;
    public const int MaximumPayloadLength = RuntimeWire.MaximumPayloadLength + RuntimeWire.HeaderLength + 64;
    public const long MaximumSegmentBytes = 16 * 1024 * 1024;
    public const int MaximumSegmentRecords = 65536;
    private const uint RecordMagic = 0x524a534c; // LSJR
    private static readonly byte[] FileMagic = Encoding.ASCII.GetBytes("LSJNL001");
    private readonly object _gate = new object();
    private readonly Stream _stream;
    private readonly Action _flushDurable;
    private readonly bool _leaveOpen;
    private long _writtenSequence;
    private long _durableSequence;
    private long _writtenLength;
    private long _durableLength;
    private bool _faulted;
    private bool _disposed;

    public RecoveryJournalIdentity Identity { get; }
    public bool RecoveredTail { get; }
    public string? PreservedTailPath { get; }
    public long WrittenSequence { get { lock (_gate) return _writtenSequence; } }
    public long DurableSequence { get { lock (_gate) return _durableSequence; } }
    public long WrittenLength { get { lock (_gate) return _writtenLength; } }

    public static RecoveryJournal Open(string path, RecoveryJournalIdentity identity)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var file = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.SequentialScan);
        string? preserved = null;
        try
        {
            var journal = new RecoveryJournal(RuntimeStorageQuota.Protect(file, fullPath), identity, () => file.Flush(true), offset =>
            {
                preserved = fullPath + ".tail-" + Guid.NewGuid().ToString("N") + ".bin";
                using var tail = new FileStream(preserved, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
                file.Position = offset;
                using var boundedTail = RuntimeStorageQuota.Protect(tail, preserved, true);
                file.CopyTo(boundedTail);
                tail.Flush(true); // Preserve first; a full disk must leave the original file intact.
            }, false, () => preserved);
            return journal;
        }
        catch { file.Dispose(); throw; }
    }

    internal RecoveryJournal(Stream stream, RecoveryJournalIdentity identity, Action flushDurable, Action<long> preserveTail,
        bool leaveOpen = true, Func<string?>? preservedPath = null)
    {
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) throw new ArgumentException("Journal requires a seekable read/write stream.");
        _stream = stream; _flushDurable = flushDurable; _leaveOpen = leaveOpen;
        Identity = identity;
        if (_stream.Length > 0 && _stream.Length < HeaderLength)
        {
            var expected = EncodeHeader(identity); _stream.Position = 0;
            for (var index = 0; index < _stream.Length; index++)
                if (_stream.ReadByte() != expected[index]) throw new InvalidDataException("Partial journal header differs from its session.");
            preserveTail(0); PreservedTailPath = preservedPath?.Invoke();
            _stream.SetLength(0); _stream.Position = 0; RecoveredTail = true;
        }
        if (_stream.Length == 0)
        {
            var header = EncodeHeader(identity);
            _stream.Write(header, 0, header.Length);
            _flushDurable();
            _writtenLength = _durableLength = HeaderLength;
            return;
        }
        ValidateHeader(identity);
        if (_stream.Length > MaximumSegmentBytes) throw new InvalidDataException("Oversized journal segment; original bytes retained.");
        var validLength = (long)HeaderLength;
        var sequence = 0L;
        _stream.Position = HeaderLength;
        while (_stream.Position < _stream.Length)
        {
            if (!TryReadRecord(_stream.Length, sequence + 1, out _))
            {
                preserveTail(validLength);
                PreservedTailPath = preservedPath?.Invoke();
                _stream.SetLength(validLength);
                RecoveredTail = true;
                break;
            }
            sequence++;
            validLength = _stream.Position;
            if (sequence > MaximumSegmentRecords || validLength > MaximumSegmentBytes)
                throw new InvalidDataException("Journal segment exceeds its bound.");
        }
        _flushDurable(); // Recovered complete records are committed before their ACK can be exposed.
        _writtenSequence = _durableSequence = sequence;
        _writtenLength = _durableLength = validLength;
        _stream.Position = validLength;
    }

    public bool CanAppend(int payloadLength)
    {
        lock (_gate) return !_disposed && !_faulted && payloadLength >= 0 && payloadLength <= MaximumPayloadLength &&
            _writtenSequence < MaximumSegmentRecords && _writtenLength + RecordHeaderLength + payloadLength <= MaximumSegmentBytes;
    }

    public long Append(JournalRecordKind kind, long receiptQpc, byte[] payload)
    {
        if (!Enum.IsDefined(typeof(JournalRecordKind), kind) || receiptQpc < Identity.Clock.OriginQpc || payload == null || payload.Length > MaximumPayloadLength)
            throw new ArgumentException("Invalid journal record.");
        lock (_gate)
        {
            RequireWriter();
            if (!CanAppend(payload.Length)) throw new InvalidOperationException("Rotate the journal segment before appending.");
            // The producer owns its input; copy it once before CRC/write so a later mutation cannot invalidate an ACK.
            var owned = (byte[])payload.Clone();
            var sequence = _writtenSequence + 1;
            var header = new byte[RecordHeaderLength];
            using (var memory = new MemoryStream(header, true))
            using (var writer = new BinaryWriter(memory, Encoding.UTF8))
            {
                writer.Write(RecordMagic); writer.Write((ushort)1); writer.Write((ushort)kind);
                writer.Write(sequence); writer.Write(receiptQpc); writer.Write(owned.Length);
                writer.Write(JournalCrc32.Compute(owned, 0, owned.Length));
                writer.Write(JournalCrc32.Compute(header, 0, RecordHeaderLength - 4));
            }
            try
            {
                _stream.Position = _writtenLength;
                var record = new byte[header.Length + owned.Length];
                Buffer.BlockCopy(header, 0, record, 0, header.Length); Buffer.BlockCopy(owned, 0, record, header.Length, owned.Length);
                _stream.Write(record, 0, record.Length);
                _writtenLength += header.Length + owned.Length;
                _writtenSequence = sequence;
                return sequence;
            }
            catch { _faulted = true; throw; }
        }
    }

    public JournalCommit Commit()
    {
        lock (_gate)
        {
            RequireWriter();
            try { _flushDurable(); }
            catch { _faulted = true; throw; }
            _durableSequence = _writtenSequence; _durableLength = _writtenLength;
            return new JournalCommit(_durableSequence, _durableLength);
        }
    }

    public IReadOnlyList<RecoveryJournalRecord> ReadCommitted()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecoveryJournal));
            var records = new List<RecoveryJournalRecord>();
            var position = _stream.Position;
            try
            {
                _stream.Position = HeaderLength;
                while (_stream.Position < _durableLength)
                {
                    if (!TryReadRecord(_durableLength, records.Count + 1, out var record))
                        throw new InvalidDataException("Committed journal prefix changed.");
                    records.Add(record!);
                }
            }
            finally { _stream.Position = position; }
            return records.AsReadOnly();
        }
    }

    private bool TryReadRecord(long end, long expectedSequence, out RecoveryJournalRecord? record)
    {
        record = null;
        if (end - _stream.Position < RecordHeaderLength) return false;
        var header = new byte[RecordHeaderLength];
        ReadExactly(header);
        using var memory = new MemoryStream(header, false);
        using var reader = new BinaryReader(memory);
        if (reader.ReadUInt32() != RecordMagic || reader.ReadUInt16() != 1) return false;
        var kind = (JournalRecordKind)reader.ReadUInt16();
        var sequence = reader.ReadInt64(); var receipt = reader.ReadInt64(); var length = reader.ReadInt32();
        var payloadCrc = reader.ReadUInt32(); var headerCrc = reader.ReadUInt32();
        if (headerCrc != JournalCrc32.Compute(header, 0, RecordHeaderLength - 4) ||
            !Enum.IsDefined(typeof(JournalRecordKind), kind) || sequence != expectedSequence || receipt < Identity.Clock.OriginQpc ||
            length < 0 || length > MaximumPayloadLength || length > end - _stream.Position) return false;
        var payload = new byte[length];
        ReadExactly(payload);
        if (payloadCrc != JournalCrc32.Compute(payload, 0, payload.Length)) return false;
        record = new RecoveryJournalRecord(sequence, receipt, kind, payload);
        return true;
    }

    private static byte[] EncodeHeader(RecoveryJournalIdentity identity)
    {
        var header = new byte[HeaderLength];
        using var memory = new MemoryStream(header, true);
        using var writer = new BinaryWriter(memory, Encoding.UTF8);
        writer.Write(FileMagic); writer.Write(1); writer.Write(HeaderLength);
        writer.Write(identity.RuntimeId.ToByteArray()); writer.Write(identity.CaptureSessionId.ToByteArray()); writer.Write(identity.Clock.Id.ToByteArray());
        writer.Write(identity.Clock.OriginQpc); writer.Write(identity.Clock.Frequency);
        writer.Write(JournalCrc32.Compute(header, 0, HeaderLength - 4));
        return header;
    }

    private void ValidateHeader(RecoveryJournalIdentity identity)
    {
        if (_stream.Length < HeaderLength) throw new InvalidDataException("Incomplete journal header; original bytes retained.");
        var expected = EncodeHeader(identity); var actual = new byte[HeaderLength];
        _stream.Position = 0; ReadExactly(actual);
        for (var i = 0; i < actual.Length; i++)
            if (actual[i] != expected[i]) throw new InvalidDataException("Journal identity, clock epoch or format does not match; original bytes retained.");
    }

    private void ReadExactly(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = _stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
    }

    private void RequireWriter()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RecoveryJournal));
        if (_faulted) throw new InvalidOperationException("The failed journal owner must be closed and recovered before writing again.");
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_leaveOpen) _stream.Dispose();
        }
    }
}
