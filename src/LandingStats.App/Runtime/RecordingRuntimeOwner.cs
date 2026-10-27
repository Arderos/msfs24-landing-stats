using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>Bounded recording mailbox. Effects are committed before a checkpoint;
/// checkpoint writes never block dequeue, so a full inbox cannot deadlock the
/// journal that owns both publication and checkpoint I/O.</summary>
internal sealed class RecordingRuntimeOwner
{
    private const int MaximumMessages = 2048;
    private const int MaximumBytes = 16 * 1024 * 1024;
    private readonly object _gate = new object();
    private readonly RecoveryJournalIdentity _identity;
    private readonly Func<DurableRuntimeInput, RecordingRuntimeEffects, Task> _commitEffects;
    private readonly Func<JournalCursor, byte[], Task> _saveCheckpoint;
    private readonly Action<Exception> _invalidateJournal;
    private readonly Queue<DurableRuntimeInput> _pending = new Queue<DurableRuntimeInput>();
    private readonly Dictionary<Guid, Barrier> _barriers = new Dictionary<Guid, Barrier>();
    private readonly TimeSpan _checkpointInterval;
    private RestoreRequest? _restore;
    private ManagedComponentContext? _owner;
    private Exception? _failure;
    private TaskCompletionSource<bool> _space = Signal();
    private readonly TaskCompletionSource<bool> _drained = Signal();
    private int _pendingBytes;
    private bool _closed;
    private PendingCheckpoint? _checkpoint;
    private JournalCursor? _lastCheckpoint;
    private int _safeToRestart;
    private TaskCompletionSource<bool> _restartStateChanged = Signal();
    public bool SafeToRestart => Volatile.Read(ref _safeToRestart) != 0;
    public RecordingRuntimeOwner(RecoveryJournalIdentity identity, Func<DurableRuntimeInput, RecordingRuntimeEffects, Task> commitEffects,
        Func<JournalCursor, byte[], Task> saveCheckpoint, Action<Exception> invalidateJournal, TimeSpan? checkpointInterval = null)
    {
        _identity = identity; _commitEffects = commitEffects; _saveCheckpoint = saveCheckpoint; _invalidateJournal = invalidateJournal;
        _checkpointInterval = checkpointInterval ?? TimeSpan.FromSeconds(30);
        if (_checkpointInterval <= TimeSpan.Zero) throw new ArgumentException("Invalid recording checkpoint interval.");
    }
    public Task RestoreAsync(byte[]? snapshot, JournalCursor? cursor)
    {
        if (snapshot != null && snapshot.Length > 192 * 1024 * 1024) throw new ArgumentException("Recording checkpoint exceeds its bound.");
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("Recording intake has stopped.");
            if (_restore != null) throw new InvalidOperationException("Recording restore already pending.");
            SetRestartSafety(false);
            _restore = new RestoreRequest(snapshot, cursor); _owner?.SignalWork(); return _restore.Completion.Task;
        }
    }
    public async Task PublishAsync(DurableRuntimeInput input)
    {
        if (input.Input.Size > MaximumBytes) throw new ArgumentException("Recording input exceeds its byte bound.");
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_closed) throw new InvalidOperationException("Recording intake has stopped.");
                if (_failure != null) throw new IOException("Recording owner needs recovery.", _failure);
                if (_pending.Count < MaximumMessages && input.Input.Size <= MaximumBytes - _pendingBytes)
                { SetRestartSafety(false); _pending.Enqueue(input); _pendingBytes += input.Input.Size; _owner?.SignalWork(); return; }
                wait = _space.Task;
            }
            await wait.ConfigureAwait(false);
        }
    }
    // A signal means the owner changed state, never an irrevocable permission.
    // Checking and subscribing under the mailbox gate prevents lost wakeups.
    public async Task WaitForSafeRestartAsync(CancellationToken cancellationToken)
    {
        var cancelled = Signal();
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_closed) throw new InvalidOperationException("Recording intake has stopped.");
                    if (SafeToRestart) return;
                    if (_restartStateChanged.Task.IsCompleted) _restartStateChanged = Signal();
                    changed = _restartStateChanged.Task;
                }
                await Task.WhenAny(changed, cancelled.Task).ConfigureAwait(false);
            }
        }
    }
    public async Task WaitForCheckpointAsync(Guid inputId, CancellationToken cancellationToken = default)
    {
        Barrier barrier;
        lock (_gate)
        {
            if (!_barriers.TryGetValue(inputId, out barrier!))
            {
                if (_barriers.Values.Count(value => value.Waiters > 0) >= 128) throw new InvalidOperationException("Recording checkpoint waiter limit reached.");
                _barriers.Add(inputId, barrier = new Barrier());
            }
            barrier.Waiters++;
        }
        try
        {
            if (await Task.WhenAny(barrier.Completion.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false) != barrier.Completion.Task)
            { cancellationToken.ThrowIfCancellationRequested(); throw new IOException("Recording checkpoint remains pending in storage."); }
            await barrier.Completion.Task.ConfigureAwait(false);
        }
        finally { lock (_gate) { barrier.Waiters--; if (barrier.Waiters == 0 && barrier.Completion.Task.IsCompleted) _barriers.Remove(inputId); } }
    }
    public async Task CloseIntakeAsync()
    {
        lock (_gate) { _closed = true; SetRestartSafety(false); PulseRestartState(); PulseSpace(); _owner?.SignalWork(); }
        if (await Task.WhenAny(_drained.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != _drained.Task)
        {
            var error = new IOException("Recording closed before its checkpoint became durable; journal retained for replay.");
            lock (_gate)
            {
                _restore?.Completion.TrySetException(error);
                foreach (var barrier in _barriers.Values) barrier.Completion.TrySetException(error);
                _drained.TrySetException(error);
            }
        }
        await _drained.Task.ConfigureAwait(false);
    }
    public async Task RunAsync(ManagedComponentContext context)
    {
        lock (_gate)
        {
            if (_owner != null) throw new InvalidOperationException("Recording already has an owner.");
            _owner = context;
        }
        RecordingRuntimeState? state = null; var dirty = false; var lastCheckpointAt = Stopwatch.GetTimestamp(); long groundedSince = 0;
        Exception? failure = null;
        try
        {
            context.MarkReady();
            while (true)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                CompleteCheckpoint();
                RestoreRequest? restore; DurableRuntimeInput? input = null;
                lock (_gate)
                {
                    restore = _restore;
                    if (restore != null)
                    {
                        _restore = null;
                        // A new journal generation replays every committed input
                        // after its checkpoint. The old mailbox is only a cache.
                        _pending.Clear(); _pendingBytes = 0; PulseSpace();
                    }
                    else if (state != null && _pending.Count != 0)
                    { input = _pending.Dequeue(); _pendingBytes -= input.Input.Size; PulseSpace(); }
                }
                if (restore != null)
                {
                    try
                    {
                        context.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: restore.Id);
                        state = restore.Snapshot == null ? new RecordingRuntimeState(_identity) : RecordingRuntimeState.RestoreCheckpoint(restore.Snapshot, _identity);
                        if ((state.Cursor == null) != (restore.Cursor == null) || (state.Cursor != null && state.Cursor.CompareTo(restore.Cursor!) != 0))
                            throw new InvalidDataException("Recording checkpoint cursor does not match the journal marker.");
                        dirty = false; lastCheckpointAt = Stopwatch.GetTimestamp(); groundedSince = 0;
                        lock (_gate)
                        {
                            _failure = null;
                            if (restore.Cursor != null) AdvanceCheckpoint(restore.Cursor);
                            restore.Completion.TrySetResult(true);
                        }
                    }
                    catch (Exception error) { restore.Completion.TrySetException(error); throw; }
                }
                if (input != null)
                {
                    context.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: input.Input.Id);
                    var effects = state!.Apply(input);
                    await _commitEffects(input, effects).ConfigureAwait(false);
                    lock (_gate)
                        foreach (var id in effects.Retirements)
                        {
                            if (!_barriers.TryGetValue(id, out var barrier))
                            {
                                _barriers.Add(id, barrier = new Barrier());
                            }
                            barrier.Cursor = input.Cursor;
                            if (_lastCheckpoint != null && input.Cursor.CompareTo(_lastCheckpoint) <= 0) barrier.Completion.TrySetResult(true);
                        }
                    dirty = true; context.Progress(true);
                }
                var grounded = state?.SafeForPresentationRestart == true;
                if (!grounded) groundedSince = 0;
                else if (groundedSince == 0) groundedSince = Stopwatch.GetTimestamp();
                bool barrierPending, closed, empty;
                lock (_gate)
                {
                    barrierPending = _barriers.Values.Any(value => value.Cursor != null && !value.Completion.Task.IsCompleted);
                    closed = _closed; empty = _pending.Count == 0 && _restore == null;
                    SetRestartSafety(!closed && _failure == null && grounded && empty &&
                        Stopwatch.GetTimestamp() - groundedSince >= Stopwatch.Frequency * 5);
                }
                if (state?.Cursor != null && _lastCheckpoint != null && state.Cursor.CompareTo(_lastCheckpoint) == 0) dirty = false;
                if (state?.Cursor != null && dirty && _checkpoint == null &&
                    (_lastCheckpoint == null || state.Cursor.CompareTo(_lastCheckpoint) > 0) &&
                    (barrierPending || closed || (!state.IsRecording && (Stopwatch.GetTimestamp() - lastCheckpointAt) / (double)Stopwatch.Frequency >= _checkpointInterval.TotalSeconds)))
                {
                    context.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.NewGuid());
                    var bytes = state.CaptureCheckpoint();
                    _checkpoint = new PendingCheckpoint(state.Cursor, _saveCheckpoint(state.Cursor, bytes));
                    dirty = false; lastCheckpointAt = Stopwatch.GetTimestamp();
                }
                if (closed && empty && !dirty && _checkpoint == null) _drained.TrySetResult(true);
                if (input == null && restore == null)
                { context.Progress(false, state == null ? DependencyWait.Storage : DependencyWait.Idle); await context.WaitForWorkAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false); }
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            lock (_gate)
            {
                SetRestartSafety(false);
                _owner = null;
                if (!_closed)
                { _failure = failure ?? new IOException("Recording owner ended unexpectedly."); PulseSpace(); }
            }
            if (_failure != null) _invalidateJournal(_failure);
        }
    }
    private void CompleteCheckpoint()
    {
        var checkpoint = _checkpoint;
        if (checkpoint == null || !checkpoint.Completion.IsCompleted) return;
        _checkpoint = null;
        checkpoint.Completion.GetAwaiter().GetResult();
        lock (_gate) AdvanceCheckpoint(checkpoint.Cursor);
    }
    private void AdvanceCheckpoint(JournalCursor cursor)
    {
        if (_lastCheckpoint == null || cursor.CompareTo(_lastCheckpoint) > 0) _lastCheckpoint = cursor;
        foreach (var barrier in _barriers.Values)
            if (barrier.Cursor != null && barrier.Cursor.CompareTo(_lastCheckpoint) <= 0) barrier.Completion.TrySetResult(true);
        foreach (var old in _barriers.Where(pair => pair.Value.Waiters == 0 && pair.Value.Completion.Task.IsCompleted)
            .OrderByDescending(pair => pair.Value.Cursor?.Segment ?? 0).ThenByDescending(pair => pair.Value.Cursor?.Sequence ?? 0).Skip(128).Select(pair => pair.Key).ToArray())
            _barriers.Remove(old);
    }
    // Caller holds _gate. Continuations run outside the recording owner.
    private void SetRestartSafety(bool safe)
    {
        var next = safe ? 1 : 0;
        if (_safeToRestart == next) return;
        Volatile.Write(ref _safeToRestart, next);
        PulseRestartState();
    }
    private void PulseRestartState()
    { _restartStateChanged.TrySetResult(true); }
    private void PulseSpace() { var previous = _space; _space = Signal(); previous.TrySetResult(true); }
    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Barrier
    {
        public JournalCursor? Cursor;
        public int Waiters;
        public readonly TaskCompletionSource<bool> Completion = Signal();
    }
    private sealed class RestoreRequest
    {
        public RestoreRequest(byte[]? snapshot, JournalCursor? cursor) { Snapshot = snapshot == null ? null : (byte[])snapshot.Clone(); Cursor = cursor; }
        public readonly Guid Id = Guid.NewGuid();
        public readonly byte[]? Snapshot;
        public readonly JournalCursor? Cursor;
        public readonly TaskCompletionSource<bool> Completion = Signal();
    }
    private sealed class PendingCheckpoint
    {
        public PendingCheckpoint(JournalCursor cursor, Task completion) { Cursor = cursor; Completion = completion; }
        public readonly JournalCursor Cursor;
        public readonly Task Completion;
    }
}
