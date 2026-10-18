using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class RuntimeInput
{
    private readonly byte[] _content;
    public RuntimeInput(Guid id, JournalRecordKind kind, long acceptedQpc, long arrivedQpc, DateTime utc, byte[] content)
    {
        if (id == Guid.Empty || !Enum.IsDefined(typeof(JournalRecordKind), kind) || acceptedQpc < arrivedQpc || arrivedQpc < 0 ||
            content == null || content.Length > RecoveryJournal.MaximumPayloadLength - 40) throw new ArgumentException("Invalid durable runtime input.");
        Id = id; Kind = kind; AcceptedQpc = acceptedQpc; ArrivedQpc = arrivedQpc; TimestampUtc = utc.ToUniversalTime(); _content = (byte[])content.Clone();
    }
    public Guid Id { get; }
    public JournalRecordKind Kind { get; }
    public long AcceptedQpc { get; }
    public long ArrivedQpc { get; }
    public DateTime TimestampUtc { get; }
    public int Size => _content.Length + 40;
    public byte[] CopyContent() => (byte[])_content.Clone();
    internal byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(Id.ToByteArray()); writer.Write(ArrivedQpc); writer.Write(TimestampUtc.Ticks); writer.Write(_content.Length); writer.Write(_content);
        return stream.ToArray();
    }
    internal static RuntimeInput Decode(RecoveryJournalRecord record)
    {
        using var stream = new MemoryStream(record.CopyPayload(), false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported runtime input version.");
        var id = new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)); var arrival = reader.ReadInt64(); var utc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        var payload = RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, RecoveryJournal.MaximumPayloadLength - 40));
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected runtime input suffix.");
        return new RuntimeInput(id, record.Kind, record.ReceiptQpc, arrival, utc, payload);
    }
}
internal sealed class DurableRuntimeInput
{
    public DurableRuntimeInput(RuntimeInput input, JournalCursor cursor) { Input = input; Cursor = cursor; }
    public RuntimeInput Input { get; }
    public JournalCursor Cursor { get; }
}
internal sealed class RuntimeInputReceipt
{
    internal RuntimeInputReceipt(Guid id, Task<JournalCursor> durable) { Id = id; Durable = durable; }
    public Guid Id { get; }
    public Task<JournalCursor> Durable { get; }
    public async Task<JournalCursor> WaitAsync(CancellationToken token = default)
    {
        if (await Task.WhenAny(Durable, Task.Delay(TimeSpan.FromSeconds(30), token)).ConfigureAwait(false) != Durable)
        { token.ThrowIfCancellationRequested(); throw new IOException("Durable storage remains unavailable; the dispatcher retains the pending input."); }
        return await Durable.ConfigureAwait(false);
    }
}

/// <summary>
/// Bounded intake survives managed writer replacement. Disk commit precedes
/// publication/ACK; a replayed input retains its id, original receipt time and bytes.
/// The consumer restores its checkpoint and rejects duplicate cursors itself.
/// Publication only enqueues to the consumer; it must not await a checkpoint
/// that this same storage loop has to write.
/// </summary>
internal sealed class DurableRuntimeDispatcher
{
    internal const int MaximumPending = 2048;
    internal const int MaximumPendingBytes = 16 * 1024 * 1024;
    internal static readonly TimeSpan MaximumCommitDelay = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new object();
    private readonly Queue<Pending> _pending = new Queue<Pending>();
    private readonly string _root;
    private readonly RecoveryJournalIdentity _identity;
    private readonly RuntimeJournalLimits _limits;
    private readonly Func<byte[]?, JournalCursor?, Task> _restoreConsumer;
    private readonly Func<DurableRuntimeInput, Task> _publish;
    private readonly Func<long> _qpc;
    private readonly Func<DateTime> _utc;
    private readonly Func<byte[]?, IRuntimeInputPreparation>? _prepare;
    private TaskCompletionSource<bool> _space = NewSignal();
    private readonly TaskCompletionSource<bool> _drained = NewSignal();
    private ManagedComponentContext? _owner;
    private CheckpointRequest? _checkpoint;
    private int _pendingBytes;
    private bool _closed;
    private Exception? _consumerFailure;
    public DurableRuntimeDispatcher(string root, RecoveryJournalIdentity identity, Func<byte[]?, JournalCursor?, Task> restoreConsumer,
        Func<DurableRuntimeInput, Task> publish, RuntimeJournalLimits? limits = null, Func<long>? qpc = null, Func<DateTime>? utc = null,
        Func<byte[]?, IRuntimeInputPreparation>? prepare = null)
    {
        _root = root; _identity = identity; _restoreConsumer = restoreConsumer; _publish = publish; _limits = limits ?? new RuntimeJournalLimits();
        _qpc = qpc ?? Stopwatch.GetTimestamp; _utc = utc ?? (() => DateTime.UtcNow);
        _prepare = prepare;
    }
    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    public bool RecoveredTail { get; private set; }
    public void InvalidateConsumer(Exception error)
    { lock (_gate) { _consumerFailure = error; _owner?.SignalWork(); } }
    public bool TrySubmit(JournalRecordKind kind, byte[] bytes, long arrivedQpc, out RuntimeInputReceipt? receipt)
    {
        if (bytes == null || bytes.Length > RecoveryJournal.MaximumPayloadLength - 40 || arrivedQpc < _identity.Clock.OriginQpc) throw new ArgumentException("Runtime input exceeds its bounds or clock epoch.");
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("Runtime intake has stopped.");
            if (_pending.Count == MaximumPending || bytes.Length + 40 > MaximumPendingBytes - _pendingBytes) { receipt = null; return false; }
            var input = new RuntimeInput(Guid.NewGuid(), kind, _qpc(), arrivedQpc, _utc(), bytes);
            var pending = new Pending(input); _pending.Enqueue(pending); _pendingBytes += input.Size;
            receipt = new RuntimeInputReceipt(input.Id, pending.Committed.Task); _owner?.SignalWork(); return true;
        }
    }
    public Task WaitForCapacityAsync(int payloadBytes, CancellationToken cancellationToken)
    {
        if (payloadBytes < 0 || payloadBytes > RecoveryJournal.MaximumPayloadLength - 40) throw new ArgumentException("Invalid runtime input size.");
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("Runtime intake has stopped.");
            if (_pending.Count < MaximumPending && payloadBytes + 40 <= MaximumPendingBytes - _pendingBytes) return Task.CompletedTask;
            return WaitWithCancellation(_space.Task, cancellationToken);
        }
    }
    public Task SaveCheckpointAsync(JournalCursor cursor, byte[] snapshot)
    {
        if (snapshot == null || snapshot.Length > _limits.CheckpointBytes) throw new ArgumentException("Runtime checkpoint exceeds its bound.");
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("Runtime intake has stopped.");
            if (_checkpoint != null) throw new InvalidOperationException("The previous runtime checkpoint is still pending.");
            _checkpoint = new CheckpointRequest(cursor, (byte[])snapshot.Clone()); _owner?.SignalWork(); return _checkpoint.Completion.Task;
        }
    }
    public async Task CloseIntakeAsync()
    {
        lock (_gate)
        {
            _closed = true; PulseSpace(); _owner?.SignalWork();
            if (_pending.Count == 0 && _checkpoint == null) _drained.TrySetResult(true);
        }
        if (await Task.WhenAny(_drained.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != _drained.Task)
        {
            var failure = new IOException("Runtime intake closed before pending inputs could become durable.");
            lock (_gate)
            {
                foreach (var pending in _pending) pending.Committed.TrySetException(failure);
                _checkpoint?.Completion.TrySetException(failure); _drained.TrySetException(failure);
            }
        }
        await _drained.Task.ConfigureAwait(false);
    }
    public async Task RunAsync(ManagedComponentContext context)
    {
        lock (_gate) { if (_owner != null) throw new InvalidOperationException("Runtime dispatcher already has an owner."); _owner = context; _consumerFailure = null; }
        try
        {
            using var journal = RollingRuntimeJournal.Open(_root, _identity, _limits); RecoveredTail |= journal.RecoveredTail;
            var known = new Dictionary<Guid, DurableRuntimeInput>();
            HashSet<Guid> pendingIds;
            lock (_gate) pendingIds = new HashSet<Guid>(_pending.Select(value => value.Input.Id));
            context.Progress(false, DependencyWait.Storage, oldestPendingQpc: _qpc(), operationId: Guid.NewGuid());
            var snapshot = journal.ReadCheckpoint(); var preparation = _prepare?.Invoke(snapshot);
            await _restoreConsumer(snapshot, journal.CheckpointCursor).ConfigureAwait(false);
            foreach (var segment in journal.RetainedSegments)
            foreach (var item in journal.ReadSegment(segment))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var input = RuntimeInput.Decode(item.Record); var durable = new DurableRuntimeInput(input, item.Cursor);
                // Only still-pending intake needs deduplication after a failed
                // write/publish boundary. Do not cache an entire flight in RAM.
                if (pendingIds.Contains(input.Id))
                {
                    if (known.ContainsKey(input.Id)) throw new InvalidDataException("Duplicate pending operation id in the runtime journal.");
                    known.Add(input.Id, durable);
                }
                if (journal.CheckpointCursor == null || item.Cursor.CompareTo(journal.CheckpointCursor) > 0)
                {
                    preparation?.Replay(input);
                    await _publish(durable).ConfigureAwait(false);
                }
                context.Progress(true, DependencyWait.Storage);
            }
            context.MarkReady();
            while (true)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                lock (_gate) if (_consumerFailure != null) throw new IOException("Recording consumer requires replay from its durable checkpoint.", _consumerFailure);
                CheckpointRequest? checkpoint; Pending[] batch;
                lock (_gate) { checkpoint = _checkpoint; batch = _pending.Take(256).ToArray(); }
                if (checkpoint != null)
                {
                    context.Progress(false, DependencyWait.Storage, oldestPendingQpc: _qpc(), operationId: checkpoint.Id);
                    // A request may survive a consumer restart while a newer
                    // checkpoint is already durable. Completing an obsolete
                    // request must never move the journal backwards.
                    if (journal.CheckpointCursor == null || checkpoint.Cursor.CompareTo(journal.CheckpointCursor) > 0)
                        journal.SaveCheckpoint(checkpoint.Cursor, checkpoint.Bytes);
                    journal.Collect(_qpc());
                    var first = journal.RetainedSegments.First();
                    foreach (var key in known.Where(pair => pair.Value.Cursor.Segment < first).Select(pair => pair.Key).ToArray()) known.Remove(key);
                    lock (_gate)
                    {
                        _checkpoint = null; checkpoint.Completion.TrySetResult(true);
                        if (_closed && _pending.Count == 0) _drained.TrySetResult(true);
                    }
                }
                if (batch.Length == 0)
                { context.Progress(false, DependencyWait.Idle); await context.WaitForWorkAsync(MaximumCommitDelay).ConfigureAwait(false); continue; }
                var age = (_qpc() - batch[0].Input.AcceptedQpc) / (double)_identity.Clock.Frequency;
                if (batch.Length < 256 && age < MaximumCommitDelay.TotalSeconds)
                {
                    context.Progress(false, DependencyWait.Storage, PendingCount, batch[0].Input.AcceptedQpc, batch[0].Input.Id);
                    await context.WaitForWorkAsync(TimeSpan.FromSeconds(MaximumCommitDelay.TotalSeconds - age)).ConfigureAwait(false); continue;
                }
                var committed = new List<DurableRuntimeInput>();
                foreach (var pending in batch)
                {
                    if (known.TryGetValue(pending.Input.Id, out var existing))
                    {
                        if (!(preparation?.Matches(pending.Input, existing.Input) ?? (existing.Input.Encode().SequenceEqual(pending.Input.Encode()) && existing.Input.AcceptedQpc == pending.Input.AcceptedQpc && existing.Input.Kind == pending.Input.Kind)))
                            throw new InvalidDataException("Runtime operation id was reused with different input.");
                        committed.Add(existing); continue;
                    }
                    context.Progress(false, DependencyWait.Storage, PendingCount, batch[0].Input.AcceptedQpc, pending.Input.Id);
                    var prepared = preparation?.Prepare(pending.Input) ?? pending.Input;
                    var cursor = journal.Append(prepared.Kind, prepared.AcceptedQpc, prepared.Encode());
                    committed.Add(new DurableRuntimeInput(prepared, cursor));
                }
                journal.Commit();
                foreach (var durable in committed) known[durable.Input.Id] = durable;
                for (var i = 0; i < batch.Length; i++)
                {
                    await _publish(committed[i]).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_pending.Peek(), batch[i])) throw new InvalidOperationException("Runtime inbox ownership changed.");
                        _pending.Dequeue(); _pendingBytes -= batch[i].Input.Size;
                        known.Remove(batch[i].Input.Id);
                        batch[i].Committed.TrySetResult(committed[i].Cursor); PulseSpace();
                        if (_closed && _pending.Count == 0 && _checkpoint == null) _drained.TrySetResult(true);
                    }
                    context.Progress(true, DependencyWait.Storage, PendingCount);
                }
            }
        }
        finally { lock (_gate) _owner = null; }
    }
    private void PulseSpace() { var previous = _space; _space = NewSignal(); previous.TrySetResult(true); }
    private static TaskCompletionSource<bool> NewSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitWithCancellation(Task wait, CancellationToken token)
    {
        var cancellation = NewSignal(); using (token.Register(() => cancellation.TrySetCanceled()))
        { await await Task.WhenAny(wait, cancellation.Task).ConfigureAwait(false); }
    }
    private sealed class Pending
    {
        public Pending(RuntimeInput input) { Input = input; }
        public RuntimeInput Input { get; }
        public readonly TaskCompletionSource<JournalCursor> Committed = new TaskCompletionSource<JournalCursor>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class CheckpointRequest
    {
        public CheckpointRequest(JournalCursor cursor, byte[] bytes) { Cursor = cursor; Bytes = bytes; }
        public readonly Guid Id = Guid.NewGuid();
        public readonly JournalCursor Cursor;
        public readonly byte[] Bytes;
        public readonly TaskCompletionSource<bool> Completion = NewSignal();
    }
}
