using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>Parent-only upload scheduling and receipts. HTTP belongs to a finite
/// isolated job; its durable claim is released only after actual tree exit and
/// all accepted result callbacks have drained.</summary>
internal sealed class UploadRuntimeOwner
{
    private readonly DurableOutbox _outbox;
    private readonly string _executable, _identityRoot;
    private readonly Guid _session;
    private readonly RuntimeClockEpoch _clock;
    private readonly Func<long, string?>? _fixture;
    private readonly Dictionary<Guid, Retry> _retry = new Dictionary<Guid, Retry>();
    private readonly object _gate = new object();
    private readonly Action<OutboxEntry> _changed;
    private readonly Action<UploadResultMessage>? _receiverObserved;
    private CancellationTokenSource? _automaticAttempt;
    private TaskCompletionSource<bool>? _automaticStopped;
    private ManagedComponentContext? _owner;
    private WorkerIdentity? _pendingRetirement;
    private Guid _retiringArchive;
    private long _generation;
    private bool _automaticAllowed;
    private int _active;
    public UploadRuntimeOwner(DurableOutbox outbox, string executable, string identityRoot, Guid session, RuntimeClockEpoch clock,
        Action<OutboxEntry> changed, Func<long, string?>? fixture = null, Action<UploadResultMessage>? receiverObserved = null)
    {
        _outbox = outbox; _executable = Path.GetFullPath(executable); _identityRoot = Path.GetFullPath(identityRoot);
        _session = session; _clock = clock; _changed = changed; _fixture = fixture; _automaticAllowed = outbox.Policy.AutomaticEnabled;
        _receiverObserved = receiverObserved;
    }
    internal event Action<ProcessWorkerHost>? WorkerStarted;
    public void Wake() => Volatile.Read(ref _owner)?.SignalWork();
    public Task SetAutomaticAllowed(bool allowed)
    {
        CancellationTokenSource? cancel;
        Task stopped;
        lock (_gate)
        {
            _automaticAllowed = allowed; cancel = allowed ? null : _automaticAttempt;
            stopped = !allowed && _automaticStopped != null ? _automaticStopped.Task : Task.CompletedTask;
        }
        if (cancel != null) { try { cancel.Cancel(); } catch (ObjectDisposedException) { } }
        Wake();
        return stopped;
    }
    public async Task RunAsync(ManagedComponentContext owner)
    {
        if (Interlocked.CompareExchange(ref _owner, owner, null) != null) throw new InvalidOperationException("Upload scheduling already has an owner.");
        try
        {
            owner.MarkReady();
            await RecoverClaimsAsync(owner).ConfigureAwait(false);
            while (true)
            {
                var worked = false;
                if (_pendingRetirement != null)
                {
                    owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: _retiringArchive);
                    FinishRetirement();
                }
                foreach (var id in _outbox.Enumerate())
                {
                    owner.CancellationToken.ThrowIfCancellationRequested(); var now = Stopwatch.GetTimestamp();
                    if (_retry.TryGetValue(id, out var previous) && now < previous.After) continue;
                    try
                    {
                        var entry = _outbox.Read(id);
                        if (entry.State == OutboxState.Delivered || entry.State == OutboxState.QuarantinePending || entry.State == OutboxState.Quarantined)
                        {
                            owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: now, operationId: id);
                            if (_outbox.RecoverTerminalEntry(id)) { worked = true; _changed(_outbox.Read(id)); }
                            _retry.Remove(id); owner.Progress(true); continue;
                        }
                        if (entry.State != OutboxState.Pending && entry.State != OutboxState.Revoked) continue;
                        lock (_gate) if (entry.Automatic && !_automaticAllowed) continue;
                        owner.Progress(false, DependencyWait.Network, oldestPendingQpc: now, operationId: id);
                        var attempted = await AttemptAsync(id, owner.CancellationToken).ConfigureAwait(false); worked |= attempted;
                        var current = _outbox.Read(id);
                        if (current.State == OutboxState.Pending || current.State == OutboxState.Revoked) Defer(id, previous, now);
                        else _retry.Remove(id);
                        owner.Progress(attempted);
                    }
                    catch (OperationCanceledException) when (!owner.CancellationToken.IsCancellationRequested) { }
                    catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is System.Runtime.Serialization.SerializationException)
                    { Defer(id, previous, now); }
                }
                owner.Progress(worked, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _owner, null); }
    }
    private async Task RecoverClaimsAsync(ManagedComponentContext owner)
    {
        foreach (var id in _outbox.Enumerate())
        {
            OutboxEntry entry;
            try { entry = _outbox.Read(id); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is System.Runtime.Serialization.SerializationException)
            { continue; } // A damaged entry cannot take ownership of other archives.
            if (entry.State != OutboxState.Claimed) continue;
            owner.Progress(false, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: id);
            var worker = new WorkerIdentity(entry.AppSession, entry.WorkerInstance, entry.WorkerGeneration, ComponentRole.Upload);
            await OwnedProcess.ConfirmFormerWorkerExitedAsync(worker, TimeSpan.FromSeconds(3), owner.CancellationToken).ConfigureAwait(false);
            _outbox.RetireConfirmedWorker(worker);
            if (_outbox.Read(id).State == OutboxState.Claimed) throw new IOException("Former upload claim is not durably released.");
        }
        _outbox.RecoverTerminalEntries();
    }
    internal async Task<bool> AttemptAsync(Guid id, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new InvalidOperationException("An upload attempt already owns the transport.");
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UploadClaim? claim = null;
        TaskCompletionSource<bool>? stopped = null;
        try
        {
            FinishRetirement();
            var identity = new WorkerIdentity(_session, Guid.NewGuid(), Interlocked.Increment(ref _generation), ComponentRole.Upload);
            lock (_gate)
            {
                var entry = _outbox.Read(id);
                if (entry.Automatic && !_automaticAllowed) return false;
                claim = _outbox.TryClaim(id, identity); if (claim == null) return false;
                if (claim.Automatic)
                {
                    _automaticAttempt = attempt;
                    _automaticStopped = stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            var command = new UploadJobMessage(claim.ArchiveId, claim.AttemptId, claim.ContentHash, claim.Path, _identityRoot, claim.PolicyRevision, claim.Automatic);
            await ProcessJobExecutor.RunAsync(_executable, identity, _clock, command, message =>
            {
                if (!(message.Payload is UploadResultMessage result) || result.Archive != claim.ArchiveId || result.Attempt != claim.AttemptId ||
                    result.Hash != claim.ContentHash || result.PolicyRevision != claim.PolicyRevision) throw new InvalidDataException("Upload reply belongs to another immutable claim.");
                bool changed;
                lock (_gate)
                {
                    if (claim.Automatic && !_automaticAllowed) return false;
                    switch (result.Result)
                    {
                        case UploadResultKind.Uploaded: changed = _outbox.Acknowledge(claim); break;
                        case UploadResultKind.Rejected: changed = _outbox.Reject(claim, result.StatusCode); break;
                        default: changed = _outbox.Retry(claim, result.Reason.Length == 0 ? result.Result.ToString() : result.Reason); break;
                    }
                    if (changed) _receiverObserved?.Invoke(result);
                }
                if (changed) _changed(_outbox.Read(id)); return changed;
            }, attempt.Token, _fixture?.Invoke(identity.Generation), host =>
            {
                try { WorkerStarted?.Invoke(host); } catch { /* Diagnostics cannot own the attempt. */ }
            }, worker => { _pendingRetirement = worker; _retiringArchive = id; FinishRetirement(); }).ConfigureAwait(false);
            return true;
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_automaticAttempt, attempt))
            { _automaticAttempt = null; _automaticStopped = null; }
            Interlocked.Exchange(ref _active, 0);
            // ProcessJobExecutor has already confirmed exit and drained accepted callbacks.
            stopped?.TrySetResult(true);
        }
    }
    private void FinishRetirement()
    {
        if (_pendingRetirement == null) return;
        _outbox.RetireConfirmedWorker(_pendingRetirement);
        var entry = _outbox.Read(_retiringArchive);
        if (entry.State == OutboxState.Claimed && entry.AppSession == _pendingRetirement.AppSessionId && entry.WorkerInstance == _pendingRetirement.InstanceId &&
            entry.WorkerGeneration == _pendingRetirement.Generation) throw new IOException("Upload retirement is waiting for its durable claim release.");
        _pendingRetirement = null; _retiringArchive = Guid.Empty;
    }
    private void Defer(Guid id, Retry? previous, long now)
    {
        var count = Math.Min(20, (previous?.Count ?? 0) + 1);
        _retry[id] = new Retry(count, now + (long)(Math.Min(300, Math.Pow(2, count - 1) * 5) * Stopwatch.Frequency));
    }
    private sealed class Retry
    {
        public Retry(int count, long after) { Count = count; After = after; }
        public readonly int Count; public readonly long After;
    }
}
