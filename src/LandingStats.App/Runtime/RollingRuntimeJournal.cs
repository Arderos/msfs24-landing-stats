using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class JournalCursor : IComparable<JournalCursor>
{
    public JournalCursor(long segment, long sequence) { Segment = segment; Sequence = sequence; }
    [DataMember(Order = 1)] public long Segment { get; private set; }
    [DataMember(Order = 2)] public long Sequence { get; private set; }
    public int CompareTo(JournalCursor? other) => other == null ? 1 : Segment == other.Segment ? Sequence.CompareTo(other.Sequence) : Segment.CompareTo(other.Segment);
    public override string ToString() => Segment.ToString(CultureInfo.InvariantCulture) + ":" + Sequence.ToString(CultureInfo.InvariantCulture);
}

internal sealed class JournalItem
{
    public JournalItem(JournalCursor cursor, RecoveryJournalRecord record) { Cursor = cursor; Record = record; }
    public JournalCursor Cursor { get; }
    public RecoveryJournalRecord Record { get; }
}

internal sealed class RuntimeJournalLimits
{
    public long SegmentBytes { get; set; } = 2 * 1024 * 1024;
    public TimeSpan SegmentTime { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan RollingWindow { get; set; } = TimeSpan.FromSeconds(15);
    public long DiskBytes { get; set; } = 512L * 1024 * 1024;
    public int CheckpointBytes { get; set; } = 192 * 1024 * 1024;
    public void Validate()
    {
        if (SegmentBytes < RecoveryJournal.HeaderLength + RecoveryJournal.RecordHeaderLength + 1 || SegmentBytes > RecoveryJournal.MaximumSegmentBytes ||
            SegmentTime <= TimeSpan.Zero || RollingWindow < TimeSpan.Zero || CheckpointBytes < 1 || DiskBytes < SegmentBytes * 2 ||
            CheckpointBytes > DiskBytes) throw new ArgumentException("Invalid runtime journal limits.");
    }
}

[DataContract]
internal sealed class RuntimeJournalState
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Runtime { get; set; }
    [DataMember(Order = 3)] public Guid Capture { get; set; }
    [DataMember(Order = 4)] public Guid Clock { get; set; }
    [DataMember(Order = 5)] public long Origin { get; set; }
    [DataMember(Order = 6)] public long Frequency { get; set; }
    [DataMember(Order = 7)] public long FirstSegment { get; set; } = 1;
    [DataMember(Order = 8)] public long ActiveSegment { get; set; } = 1;
    [DataMember(Order = 9)] public JournalCursor? CheckpointCursor { get; set; }
    [DataMember(Order = 10)] public string CheckpointHash { get; set; } = "";
    [DataMember(Order = 11)] public Dictionary<string, JournalCursor> Pins { get; set; } = new Dictionary<string, JournalCursor>(StringComparer.Ordinal);
    [DataMember(Order = 12)] public bool IntegrityGap { get; set; }
    [DataMember(Order = 13)] public bool Initialized { get; set; }
    [DataMember(Order = 14)] public List<string>? RepairedActiveTails { get; set; }
}

/// <summary>One durable runtime stream. A checkpoint and all retention pins precede segment collection.</summary>
internal sealed class RollingRuntimeJournal : IDisposable
{
    private readonly RuntimeJournalLimits _limits;
    private readonly RuntimeJournalState _state;
    private readonly SortedDictionary<long, Segment> _segments = new SortedDictionary<long, Segment>();
    private readonly FileStream _ownership;
    private RecoveryJournal? _active;
    private bool _faulted, _disposed;
    private long _lastReceipt;
    private long _allocatedBytes;
    private readonly string _root;
    private string StatePath => Path.Combine(_root, "session.json");
    public RecoveryJournalIdentity Identity { get; }
    public JournalCursor DurableCursor => new JournalCursor(_state.ActiveSegment, _active!.DurableSequence);
    public JournalCursor? CheckpointCursor => _state.CheckpointCursor;
    public int SegmentCount => _segments.Count;
    public long AllocatedBytes => EnumerateBytes();
    public bool RecoveredTail { get; private set; }
    public static RollingRuntimeJournal Open(string root, RecoveryJournalIdentity? identity = null, RuntimeJournalLimits? limits = null) => new RollingRuntimeJournal(root, identity, limits ?? new RuntimeJournalLimits());
    private RollingRuntimeJournal(string root, RecoveryJournalIdentity? identity, RuntimeJournalLimits limits)
    {
        _limits = limits; limits.Validate(); _root = Path.GetFullPath(root); Directory.CreateDirectory(_root);
        _ownership = new FileStream(Path.Combine(_root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _allocatedBytes = EnumerateBytes();
            var existing = File.Exists(StatePath);
            if (existing) _state = DurableFile.ReadJson<RuntimeJournalState>(StatePath, 1024 * 1024);
            else
            {
                if (identity == null) throw new InvalidDataException("Journal session identity is missing.");
                if (Directory.EnumerateFiles(_root, "*.wal").Any()) throw new InvalidDataException("Journal segments have no session metadata; original bytes retained.");
                _state = new RuntimeJournalState { Runtime = identity.RuntimeId, Capture = identity.CaptureSessionId, Clock = identity.Clock.Id, Origin = identity.Clock.OriginQpc, Frequency = identity.Clock.Frequency };
            }
            ValidateState();
            _state.RepairedActiveTails ??= new List<string>();
            Identity = new RecoveryJournalIdentity(_state.Runtime, _state.Capture, new RuntimeClockEpoch(_state.Clock, _state.Origin, _state.Frequency));
            if (identity != null && (identity.RuntimeId != Identity.RuntimeId || identity.CaptureSessionId != Identity.CaptureSessionId || identity.Clock.Id != Identity.Clock.Id ||
                identity.Clock.OriginQpc != Identity.Clock.OriginQpc || identity.Clock.Frequency != Identity.Clock.Frequency)) throw new InvalidDataException("Journal belongs to another runtime or clock.");
            if (!existing) SaveState();
            if (_state.IntegrityGap) throw new InvalidDataException("Journal has an interrupted sealed segment; its original prefix and later segments are retained for recovery.");
            _lastReceipt = Identity.Clock.OriginQpc;
            var numbers = Directory.EnumerateFiles(_root, "*.wal").Select(ParseSegment).OrderBy(value => value).ToArray();
            if (numbers.Length > 16384) throw new InvalidDataException("Journal segment count exceeds its bound.");
            var activeNumber = numbers.Length == 0 ? _state.ActiveSegment : Math.Max(_state.ActiveSegment, numbers[numbers.Length - 1]);
            if (activeNumber - _state.FirstSegment > 16384) throw new InvalidDataException("Journal segment range exceeds its bound.");
            for (var number = _state.FirstSegment; number <= activeNumber; number++)
            {
                var path = SegmentPath(number);
                if (!File.Exists(path) && !(!_state.Initialized && number == 1 && activeNumber == 1 && numbers.Length == 0 && _state.CheckpointCursor == null && _state.Pins.Count == 0))
                    throw new InvalidDataException("Journal contains a missing segment; refusing to skip data.");
                var journal = RecoveryJournal.Open(path, Identity);
                try
                {
                    var records = journal.ReadCommitted();
                    foreach (var record in records)
                    {
                        if (record.ReceiptQpc < _lastReceipt) throw new InvalidDataException("Journal receipt clock reversed.");
                        _lastReceipt = record.ReceiptQpc;
                    }
                    var segment = new Segment(number, records.Count == 0 ? _lastReceipt : records[0].ReceiptQpc, _lastReceipt, journal.WrittenLength, journal.DurableSequence);
                    _segments.Add(number, segment); RecoveredTail |= journal.RecoveredTail;
                    var tails = Directory.EnumerateFiles(_root, Path.GetFileName(path) + ".tail-*.bin").Select(Path.GetFileName).ToArray();
                    if (number < activeNumber && (journal.RecoveredTail || tails.Any(tail => !_state.RepairedActiveTails.Contains(tail))))
                    {
                        _state.IntegrityGap = true; SaveState();
                        throw new InvalidDataException("Corrupt sealed segment cannot be joined to a later segment.");
                    }
                    if (number == activeNumber)
                    {
                        // Persist each exact evidence filename before this segment may
                        // rotate. A later corruption produces different evidence and
                        // must still fail closed, including after another crash.
                        foreach (var tail in tails)
                            if (!_state.RepairedActiveTails.Contains(tail)) _state.RepairedActiveTails.Add(tail);
                        _active = journal; journal = null!;
                    }
                }
                finally { journal?.Dispose(); }
            }
            _state.ActiveSegment = activeNumber;
            if (_state.CheckpointCursor != null) { RequireDurable(_state.CheckpointCursor); ReadCheckpoint(); }
            foreach (var pin in _state.Pins.Values) RequireDurable(pin);
            _state.Initialized = true;
            SaveState();
            // A prior collection may have committed its range but crashed before
            // deleting all files. Validate its checkpoint/pins before finishing it.
            foreach (var old in numbers.Where(number => number < _state.FirstSegment)) File.Delete(SegmentPath(old));
            _allocatedBytes = EnumerateBytes();
        }
        catch { _active?.Dispose(); _ownership.Dispose(); throw; }
    }
    public JournalCursor Append(JournalRecordKind kind, long receiptQpc, byte[] payload)
    {
        RequireOwner();
        if (receiptQpc < _lastReceipt || payload == null || payload.Length > RecoveryJournal.MaximumPayloadLength) throw new ArgumentException("Invalid journal event.");
        var required = RecoveryJournal.RecordHeaderLength + payload.Length;
        var current = _segments[_state.ActiveSegment];
        var rotate = _active!.WrittenSequence != 0 && (_active.WrittenLength + required > _limits.SegmentBytes || !_active.CanAppend(payload.Length) ||
            (receiptQpc - current.FirstQpc) / (double)Identity.Clock.Frequency >= _limits.SegmentTime.TotalSeconds);
        RequireDisk(required + (rotate ? RecoveryJournal.HeaderLength : 0));
        try
        {
            var segment = _segments[_state.ActiveSegment];
            if (rotate) Rotate();
            var sequence = _active!.Append(kind, receiptQpc, payload);
            _allocatedBytes += required;
            segment = _segments[_state.ActiveSegment];
            if (sequence == 1) segment.FirstQpc = receiptQpc;
            segment.LastQpc = receiptQpc; segment.Bytes = _active.WrittenLength; _lastReceipt = receiptQpc;
            return new JournalCursor(_state.ActiveSegment, sequence);
        }
        catch { _faulted = true; throw; }
    }
    public JournalCursor Commit()
    {
        RequireOwner();
        try { var commit = _active!.Commit(); _segments[_state.ActiveSegment].DurableSequence = commit.Sequence; _allocatedBytes = EnumerateBytes(); return DurableCursor; }
        catch { _faulted = true; throw; }
    }
    private void Rotate()
    {
        Commit(); RequireDisk(RecoveryJournal.HeaderLength); _active!.Dispose(); _active = null;
        var next = checked(_state.ActiveSegment + 1);
        _active = RecoveryJournal.Open(SegmentPath(next), Identity);
        _allocatedBytes += RecoveryJournal.HeaderLength;
        _segments.Add(next, new Segment(next, _lastReceipt, _lastReceipt, RecoveryJournal.HeaderLength, 0));
        _state.ActiveSegment = next; SaveState(); // A complete extra segment is safely adopted if this marker is interrupted.
    }
    public IReadOnlyList<JournalItem> ReadSegment(long segment, JournalCursor? after = null)
    {
        RequireOwner();
        if (!_segments.ContainsKey(segment)) throw new InvalidOperationException("Journal segment is no longer retained.");
        IReadOnlyList<RecoveryJournalRecord> records;
        if (segment == _state.ActiveSegment) records = _active!.ReadCommitted();
        else
        {
            using var journal = RecoveryJournal.Open(SegmentPath(segment), Identity); records = journal.ReadCommitted();
            if (journal.RecoveredTail)
            {
                _faulted = true; _state.IntegrityGap = true; SaveState();
                throw new InvalidDataException("Sealed journal changed after startup.");
            }
        }
        return records.Select(record => new JournalItem(new JournalCursor(segment, record.Sequence), record)).Where(item => after == null || item.Cursor.CompareTo(after) > 0).ToArray();
    }
    public IEnumerable<long> RetainedSegments => _segments.Keys.ToArray();
    public void Pin(Guid owner, JournalCursor first)
    {
        RequireOwner(); if (owner == Guid.Empty) throw new ArgumentException("A retention owner is required."); RequireDurable(first);
        var key = owner.ToString("N");
        if (_state.Pins.TryGetValue(key, out var previous))
        { if (previous.CompareTo(first) != 0) throw new InvalidOperationException("An existing retention pin cannot silently move."); return; }
        _state.Pins.Add(key, first); MutateState();
    }
    public void Release(Guid owner)
    { RequireOwner(); if (_state.Pins.Remove(owner.ToString("N"))) MutateState(); }
    public void SaveCheckpoint(JournalCursor cursor, byte[] snapshot)
    {
        RequireOwner(); RequireDurable(cursor);
        if (_state.CheckpointCursor != null && cursor.CompareTo(_state.CheckpointCursor) < 0) throw new InvalidOperationException("Checkpoint cannot move backwards.");
        if (snapshot == null || snapshot.Length > _limits.CheckpointBytes) throw new ArgumentException("Checkpoint exceeds its bound.");
        var hash = HashBytes(snapshot); var path = CheckpointPath(hash);
        if (!File.Exists(path)) RequireDisk(snapshot.Length, checkpoint: true);
        try
        {
            if (!File.Exists(path)) { DurableFile.Write(path, output => output.Write(snapshot, 0, snapshot.Length)); _allocatedBytes += snapshot.Length; }
            EpisodeTransactionStore.Verify(path, hash);
            _state.CheckpointCursor = cursor; _state.CheckpointHash = hash; SaveState();
            // Only obsolete checkpoints are removed. The state file points to a
            // complete new snapshot before any old snapshot may be collected.
            foreach (var stale in Directory.EnumerateFiles(_root, "checkpoint-*.bin")) if (!string.Equals(stale, path, StringComparison.OrdinalIgnoreCase)) File.Delete(stale);
            _allocatedBytes = EnumerateBytes();
        }
        catch { _faulted = true; throw; }
    }
    public byte[]? ReadCheckpoint()
    {
        if (_state.CheckpointCursor == null) return null;
        var path = CheckpointPath(_state.CheckpointHash);
        if (new FileInfo(path).Length > _limits.CheckpointBytes) throw new InvalidDataException("Checkpoint exceeds its bound.");
        EpisodeTransactionStore.Verify(path, _state.CheckpointHash); return File.ReadAllBytes(path);
    }
    public void Collect(long nowQpc)
    {
        RequireOwner(); if (_state.CheckpointCursor == null) return;
        var cutoff = nowQpc - (long)(_limits.RollingWindow.TotalSeconds * Identity.Clock.Frequency);
        var firstRequired = _state.CheckpointCursor.Segment;
        foreach (var pin in _state.Pins.Values) firstRequired = Math.Min(firstRequired, pin.Segment);
        var removable = _segments.Values.TakeWhile(segment => segment.Number < firstRequired && segment.Number < _state.ActiveSegment && segment.LastQpc < cutoff).ToArray();
        if (removable.Length == 0) return;
        try
        {
            _state.FirstSegment = removable[removable.Length - 1].Number + 1; SaveState();
            foreach (var segment in removable) { File.Delete(SegmentPath(segment.Number)); _segments.Remove(segment.Number); }
            _allocatedBytes = EnumerateBytes();
        }
        catch { _faulted = true; throw; }
    }
    private void RequireDurable(JournalCursor cursor)
    {
        if (cursor == null || cursor.Sequence < 0 || !_segments.TryGetValue(cursor.Segment, out var segment) || cursor.Sequence > segment.DurableSequence)
            throw new InvalidOperationException("Checkpoint or pin exceeds the durable prefix.");
    }
    private void ValidateState()
    {
        if (_state.Version != 1 || _state.FirstSegment < 1 || _state.ActiveSegment < _state.FirstSegment || _state.ActiveSegment - _state.FirstSegment > 16384 ||
            _state.Pins == null || _state.Pins.Count > 4096 || (_state.CheckpointCursor == null) != string.IsNullOrEmpty(_state.CheckpointHash) ||
            (_state.FirstSegment > 1 && _state.CheckpointCursor == null)) throw new InvalidDataException("Invalid journal session metadata.");
        if (!string.IsNullOrEmpty(_state.CheckpointHash) && !EpisodeTransactionStore.ValidHash(_state.CheckpointHash)) throw new InvalidDataException("Invalid checkpoint hash.");
        foreach (var key in _state.Pins.Keys) if (!Guid.TryParseExact(key, "N", out var id) || id == Guid.Empty) throw new InvalidDataException("Invalid retention owner.");
        if (_state.RepairedActiveTails != null && (_state.RepairedActiveTails.Count > 16384 ||
            _state.RepairedActiveTails.Any(name => name != Path.GetFileName(name) || !name.Contains(".wal.tail-") || !name.EndsWith(".bin", StringComparison.Ordinal))))
            throw new InvalidDataException("Invalid recovered-tail evidence list.");
    }
    private void MutateState() { try { SaveState(); } catch { _faulted = true; throw; } }
    private void SaveState()
    {
        using var buffer = new MemoryStream(); new DataContractJsonSerializer(typeof(RuntimeJournalState)).WriteObject(buffer, _state);
        var bytes = buffer.ToArray();
        if (bytes.Length > MetadataReserve / 2) throw new InvalidDataException("Journal retention metadata exceeds its reserved space.");
        var oldLength = File.Exists(StatePath) ? new FileInfo(StatePath).Length : 0;
        DurableFile.Write(StatePath, output => output.Write(bytes, 0, bytes.Length));
        _allocatedBytes += new FileInfo(StatePath).Length - oldLength;
    }
    private string SegmentPath(long number) => Path.Combine(_root, number.ToString("D16", CultureInfo.InvariantCulture) + ".wal");
    private static long ParseSegment(string path)
    { var name = Path.GetFileNameWithoutExtension(path); if (!long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0 || name != value.ToString("D16", CultureInfo.InvariantCulture)) throw new InvalidDataException("Invalid journal segment filename."); return value; }
    private string CheckpointPath(string hash) => Path.Combine(_root, "checkpoint-" + hash + ".bin");
    private long MetadataReserve => Math.Min(2 * 1024 * 1024, _limits.DiskBytes / 8);
    private void RequireDisk(long extra, bool checkpoint = false)
    { if (_allocatedBytes > _limits.DiskBytes - MetadataReserve - extra - (checkpoint ? 0 : _limits.CheckpointBytes)) throw new IOException("Runtime journal quota is full; checkpoint reserve and unconsumed data are retained."); }
    private long EnumerateBytes() => Directory.EnumerateFiles(_root).Aggregate(0L, (sum, path) => checked(sum + new FileInfo(path).Length));
    private static string HashBytes(byte[] bytes)
    { using var sha = System.Security.Cryptography.SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private void RequireOwner()
    { if (_disposed) throw new ObjectDisposedException(nameof(RollingRuntimeJournal)); if (_faulted) throw new InvalidOperationException("Retire and reopen the failed journal owner before continuing."); }
    public void Dispose() { if (_disposed) return; _disposed = true; _active?.Dispose(); _ownership.Dispose(); }
    private sealed class Segment
    {
        public Segment(long number, long firstQpc, long lastQpc, long bytes, long durableSequence)
        { Number = number; FirstQpc = firstQpc; LastQpc = lastQpc; Bytes = bytes; DurableSequence = durableSequence; }
        public readonly long Number;
        public long FirstQpc, LastQpc, Bytes, DurableSequence;
    }
}
