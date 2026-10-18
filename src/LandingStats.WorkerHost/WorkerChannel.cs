using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.WorkerHost;

internal sealed class WorkerChannel : IWorkerTransport
{
    internal const int MaximumPendingMessages = 1024;
    internal const int MaximumPendingBytes = 4 * 1024 * 1024;
    private readonly object _gate = new object();
    private readonly SortedDictionary<long, byte[]> _pending = new SortedDictionary<long, byte[]>();
    private readonly Queue<RuntimeEnvelope> _commands = new Queue<RuntimeEnvelope>();
    private readonly SemaphoreSlim _available = new SemaphoreSlim(0, 1);
    private readonly WorkerIdentity _identity;
    private readonly Stream _pipe;
    private long _sequence, _sending, _acknowledged, _commandSequence;
    private long _firstDropped, _lastDropped, _droppedCapture, _droppedConnection;
    private ControllerCatalogMessage? _latestCatalog;
    private bool _restoreCatalog;
    private SimulatorLifecycleSnapshotMessage? _lifecycle;
    private bool _restoreLifecycle;
    private int _bytes;
    private bool _failed;
    private readonly bool _control;
    public WorkerChannel(Stream pipe, WorkerIdentity identity, bool control = false) { _pipe = pipe; _identity = identity; _control = control; }
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public bool TryPublish(RuntimePayload payload, long connectionGeneration, long captureQpc, Guid correlationId = default,
        long revision = 0, Guid episodeId = default, Guid lookupId = default, LookupStage stage = LookupStage.None)
    {
        lock (_gate)
        {
            if (_failed) return false;
            FlushGap();
            if (payload is ControllerCatalogMessage catalog) _latestCatalog = catalog;
            if (_identity.Role == ComponentRole.Simulator && payload is LifecycleMessage lifecycle)
                _lifecycle = (_lifecycle ?? new SimulatorLifecycleSnapshotMessage()).Observe(lifecycle);
            var envelope = new RuntimeEnvelope(_identity, connectionGeneration, checked(_sequence + 1), captureQpc,
                payload, correlationId, revision, episodeId, lookupId, stage);
            var bytes = RuntimeWire.Encode(envelope);
            if (_firstDropped != 0 || _restoreCatalog || _restoreLifecycle || _pending.Count >= MaximumPendingMessages || bytes.Length > MaximumPendingBytes - _bytes)
            {
                if (_control) { _failed = true; Signal(); return false; }
                if (_firstDropped == 0) _firstDropped = _sequence + 1;
                _lastDropped = ++_sequence; _droppedCapture = captureQpc; _droppedConnection = connectionGeneration;
                // The bounded loss range stays in this owner until space returns.
                // Source callbacks remain alive and continue processing commands.
                Signal(); return true;
            }
            _pending.Add(++_sequence, bytes); _bytes += bytes.Length; Signal(); return true;
        }
    }
    public bool TryTakeCommand(out RuntimeEnvelope? command)
    {
        lock (_gate)
        {
            if (_commands.Count == 0) { command = null; return false; }
            command = _commands.Dequeue(); return true;
        }
    }
    public async Task WritePumpAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            byte[]? bytes = null;
            lock (_gate)
            {
                if (_failed) throw new IOException("Worker output exceeded its unacknowledged bound.");
                var next = _pending.FirstOrDefault(item => item.Key > _sending);
                if (next.Value != null) { _sending = next.Key; bytes = next.Value; }
            }
            if (bytes == null) { await _available.WaitAsync(token).ConfigureAwait(false); continue; }
            await _pipe.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
        }
    }
    public async Task ReadPumpAsync(CancellationToken token)
    {
        while (true)
        {
            var envelope = await RuntimeWire.ReadAsync(_pipe, token).ConfigureAwait(false);
            if (envelope == null) throw new EndOfStreamException("The runtime pipe closed.");
            lock (_gate)
            {
                if (!_identity.Equals(envelope.Worker) || envelope.Sequence != _commandSequence + 1 || envelope.ConnectionGeneration != 0)
                    throw new InvalidDataException("A command belongs to another worker or command sequence.");
                _commandSequence = envelope.Sequence;
                var ackSequence = _control && envelope.Payload is ControlAckMessage controlAck ? controlAck.Sequence :
                    !_control && envelope.Payload is DurableAckMessage durableAck ? durableAck.Sequence : 0;
                if (ackSequence != 0)
                {
                    if (ackSequence < _acknowledged || ackSequence > _sending) throw new InvalidDataException("Invalid acknowledgement.");
                    _acknowledged = ackSequence;
                    while (_pending.Count > 0 && _pending.First().Key <= _acknowledged)
                    { var first = _pending.First(); _bytes -= first.Value.Length; _pending.Remove(first.Key); }
                    FlushGap();
                }
                else
                {
                    if (!_control || !(envelope.Payload is RuntimeCommand) || _commands.Count >= 128)
                        throw new InvalidDataException("Unexpected or excessive worker commands.");
                    _commands.Enqueue(envelope);
                }
            }
        }
    }
    private void FlushGap()
    {
        if (_firstDropped == 0) { RestoreCatalog(); RestoreLifecycle(); return; }
        if (_pending.Count >= MaximumPendingMessages) return;
        var envelope = new RuntimeEnvelope(_identity, _droppedConnection, checked(_sequence + 1), _droppedCapture,
            new SourceGapMessage(SourceGapReason.BufferOverflow, _firstDropped, _lastDropped, true));
        var bytes = RuntimeWire.Encode(envelope);
        if (bytes.Length > MaximumPendingBytes - _bytes) return;
        _pending.Add(++_sequence, bytes); _bytes += bytes.Length;
        _firstDropped = _lastDropped = 0; _restoreCatalog = _latestCatalog != null;
        _restoreLifecycle = _lifecycle != null && _droppedConnection > 0;
        RestoreCatalog(); RestoreLifecycle(); Signal();
    }
    private void RestoreLifecycle()
    {
        if (!_restoreLifecycle) return;
        var envelope = new RuntimeEnvelope(_identity, _droppedConnection, checked(_sequence + 1), _droppedCapture, _lifecycle!);
        var bytes = RuntimeWire.Encode(envelope);
        if (_pending.Count >= MaximumPendingMessages || bytes.Length > MaximumPendingBytes - _bytes) return;
        _pending.Add(++_sequence, bytes); _bytes += bytes.Length; _restoreLifecycle = false; Signal();
    }
    private bool RestoreCatalog()
    {
        if (!_restoreCatalog) return true;
        var envelope = new RuntimeEnvelope(_identity, 0, checked(_sequence + 1), _droppedCapture, _latestCatalog!);
        var bytes = RuntimeWire.Encode(envelope);
        if (_pending.Count >= MaximumPendingMessages || bytes.Length > MaximumPendingBytes - _bytes) return false;
        _pending.Add(++_sequence, bytes); _bytes += bytes.Length; _restoreCatalog = false; Signal(); return true;
    }
    private void Signal() { if (_available.CurrentCount == 0) _available.Release(); }
}

internal sealed class WorkerTransport : IWorkerTransport
{
    private readonly WorkerChannel _source, _control;
    public WorkerTransport(WorkerChannel source, WorkerChannel control) { _source = source; _control = control; }
    public int PendingCount => _source.PendingCount + _control.PendingCount;
    public bool TryTakeCommand(out RuntimeEnvelope? command) => _control.TryTakeCommand(out command);
    public bool TryPublish(RuntimePayload payload, long connectionGeneration, long captureQpc, Guid correlationId = default,
        long revision = 0, Guid episodeId = default, Guid lookupId = default, LookupStage stage = LookupStage.None) =>
        (payload is ComponentHealthMessage || payload is CommandResultMessage ? _control : _source).TryPublish(payload,
            payload is ComponentHealthMessage || payload is CommandResultMessage ? 0 : connectionGeneration,
            captureQpc, correlationId, revision, episodeId, lookupId, stage);
}
