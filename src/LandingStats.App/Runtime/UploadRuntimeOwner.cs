using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;
using LandingStats.App.TelemetryUpload;

namespace LandingStats.App.Runtime;

/// <summary>Parent-only upload scheduling and receipts. HTTP belongs to a finite
/// isolated job; its durable claim is released only after actual tree exit and
/// all accepted result callbacks have drained.</summary>
internal sealed class UploadRuntimeOwner
{
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(3);
    private readonly TimeSpan _attemptTimeout;
    private readonly DurableOutbox _outbox;
    private readonly string _executable, _identityRoot;
    private readonly Guid _session;
    private readonly RuntimeClockEpoch _clock;
    private readonly Func<long, string?>? _fixture;
    private readonly Dictionary<Guid, Retry> _retry = new Dictionary<Guid, Retry>();
    private readonly object _gate = new object();
    private readonly Action<OutboxEntry> _changed;
    private readonly Action<UploadResultMessage>? _receiverObserved;
    private readonly SemaphoreSlim _identityChange = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _claimRecovery = new SemaphoreSlim(1, 1);
    private CancellationTokenSource? _attempt;
    private TaskCompletionSource<bool>? _attemptStopped;
    private bool _suspended, _resetRetries;
    private CancellationTokenSource? _automaticAttempt;
    private TaskCompletionSource<bool>? _automaticStopped;
    private ManagedComponentContext? _owner;
    private long _generation;
    private bool _automaticAllowed;
    private int _active;
    public UploadRuntimeOwner(DurableOutbox outbox, string executable, string identityRoot, Guid session, RuntimeClockEpoch clock,
        Action<OutboxEntry> changed, Func<long, string?>? fixture = null, Action<UploadResultMessage>? receiverObserved = null, TimeSpan? attemptTimeout = null)
    {
        _outbox = outbox; _executable = Path.GetFullPath(executable); _identityRoot = Path.GetFullPath(identityRoot);
        _session = session; _clock = clock; _changed = changed; _fixture = fixture; _automaticAllowed = outbox.Policy.AutomaticEnabled;
        _receiverObserved = receiverObserved;
        _attemptTimeout = attemptTimeout ?? AttemptTimeout;
        if (_attemptTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
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
    public async Task<TelemetryIdentityRecoveryResult> RecoverUnavailableIdentityAsync(bool replacementConfirmed,
        Action? identityRecovered = null, CancellationToken cancellationToken = default)
    {
        if (!replacementConfirmed) throw new InvalidOperationException("Identity replacement requires explicit confirmation.");
        await _identityChange.WaitAsync(cancellationToken).ConfigureAwait(false);
        var suspended = false;
        try
        {
            var store = new TelemetryUploadIdentityStore(_identityRoot);
            var usable = await Task.Run(() => TryRecoverUsableIdentity(() => store.IsCurrentIdentityUsable(cancellationToken), identityRecovered)).ConfigureAwait(false);
            if (usable != null) return usable;
            CancellationTokenSource? cancel; Task stopped;
            lock (_gate)
            {
                _suspended = suspended = true; cancel = _attempt;
                stopped = _attemptStopped?.Task ?? Task.CompletedTask;
            }
            if (cancel != null) { try { cancel.Cancel(); } catch (ObjectDisposedException) { } }
            await WaitForRecoveryRetirementAsync(stopped, cancellationToken).ConfigureAwait(false);
            await EnsureClaimsRecoveredAsync(null, cancellationToken).ConfigureAwait(false);
            // No old process or accepted result callback can write credentials now.
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return store.RecoverUnavailableIdentity(true, cancellationToken);
            }).ConfigureAwait(false);
            return RefreshIdentityStatus(result, identityRecovered);
        }
        finally
        {
            if (suspended) lock (_gate) { _suspended = false; _resetRetries = true; }
            _identityChange.Release(); Wake();
        }
    }
    internal TelemetryIdentityRecoveryResult? TryRecoverUsableIdentity(Func<bool> probe, Action? identityRecovered = null)
    {
        // Result acceptance uses this same gate. A fresh failure cannot commit
        // between the usable-key proof and cleanup of the earlier failures.
        lock (_gate)
        {
            if (!probe()) return null;
            return RefreshIdentityStatus(new TelemetryIdentityRecoveryResult(false, null), identityRecovered);
        }
    }
    private TelemetryIdentityRecoveryResult RefreshIdentityStatus(TelemetryIdentityRecoveryResult result, Action? identityRecovered)
    {
        // The key is usable and any replacement is committed. Only its own
        // deferred cleanup may qualify the successful identity outcome.
        string? refreshFailure = null;
        try
        {
            _outbox.ClearIdentityFailures(_changed);
            if (_outbox.HasIdentityRecoveryFailures) refreshFailure = "Identity failure cleanup remains pending.";
        }
        catch (Exception error)
        {
            refreshFailure = error.GetType().Name;
            Trace.TraceError("Telemetry identity is available, but delivery status refresh failed: {0}", error);
        }
        try { identityRecovered?.Invoke(); }
        catch (Exception error)
        {
            refreshFailure ??= error.GetType().Name;
            Trace.TraceError("Telemetry identity is available, but its presentation refresh failed: {0}", error);
        }
        return new TelemetryIdentityRecoveryResult(result.Replaced, result.BackupPath, refreshFailure, result.Created);
    }
    private static async Task WaitForRecoveryRetirementAsync(Task stopped, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetCanceled()))
        {
            if (await Task.WhenAny(stopped, cancelled.Task).ConfigureAwait(false) != stopped)
                cancellationToken.ThrowIfCancellationRequested();
            await stopped.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        // Cancellation releases only this caller. ProcessJobExecutor continues
        // supervising the native tree and retains its claim until confirmed exit.
    }
    public async Task RunAsync(ManagedComponentContext owner)
    {
        if (Interlocked.CompareExchange(ref _owner, owner, null) != null) throw new InvalidOperationException("Upload scheduling already has an owner.");
        try
        {
            await EnsureClaimsRecoveredAsync(owner, owner.CancellationToken).ConfigureAwait(false);
            owner.MarkReady();
            while (true)
            {
                lock (_gate) if (_resetRetries) { _retry.Clear(); _resetRetries = false; }
                var worked = false;
                _outbox.RetryRecoveryFailures();
                foreach (var id in _outbox.Enumerate())
                {
                    owner.CancellationToken.ThrowIfCancellationRequested(); var now = Stopwatch.GetTimestamp();
                    if (_retry.TryGetValue(id, out var previous) && now < previous.After) continue;
                    try
                    {
                        var entry = _outbox.TryRead(id);
                        if (entry == null) continue;
                        if (entry.State == OutboxState.Claimed)
                        {
                            await RecoverClaimAsync(entry, owner, owner.CancellationToken).ConfigureAwait(false);
                            entry = _outbox.Read(id);
                        }
                        if (entry.State == OutboxState.Delivered || entry.State == OutboxState.QuarantinePending || entry.State == OutboxState.Quarantined)
                        {
                            owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: now, operationId: id);
                            if (_outbox.RecoverTerminalEntry(id)) { worked = true; _changed(_outbox.Read(id)); }
                            _retry.Remove(id); owner.Progress(true); continue;
                        }
                        if (entry.State != OutboxState.Pending && entry.State != OutboxState.Revoked) continue;
                        lock (_gate) if (_suspended || entry.Automatic && (!_automaticAllowed || !_outbox.Policy.AutomaticEnabled)) continue;
                        owner.Progress(false, DependencyWait.Network, oldestPendingQpc: now, operationId: id);
                        var attempted = await AttemptAsync(id, owner.CancellationToken).ConfigureAwait(false); worked |= attempted;
                        var current = _outbox.Read(id);
                        if (attempted && (current.State == OutboxState.Pending || current.State == OutboxState.Revoked)) Defer(id, previous, Stopwatch.GetTimestamp());
                        else _retry.Remove(id);
                        owner.Progress(attempted);
                    }
                    catch (OperationCanceledException)
                    {
                        Defer(id, previous, Stopwatch.GetTimestamp());
                        if (owner.CancellationToken.IsCancellationRequested) throw;
                    }
                    catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is System.Runtime.Serialization.SerializationException)
                    { Defer(id, previous, Stopwatch.GetTimestamp()); }
                }
                owner.Progress(worked, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _owner, null); }
    }
    private async Task EnsureClaimsRecoveredAsync(ManagedComponentContext? owner, CancellationToken cancellationToken)
    {
        await _claimRecovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverClaimsAsync(owner, cancellationToken).ConfigureAwait(false);
        }
        finally { _claimRecovery.Release(); }
    }
    private async Task RecoverClaimsAsync(ManagedComponentContext? owner, CancellationToken cancellationToken)
    {
        foreach (var id in _outbox.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Admission needs a current inspection, including entries repaired
            // since the previous generation or an earlier identity recovery.
            var entry = _outbox.TryRead(id, refresh: true);
            if (entry == null) continue; // A damaged entry cannot take ownership of other archives.
            if (entry.State != OutboxState.Claimed) continue;
            await RecoverClaimAsync(entry, owner, cancellationToken).ConfigureAwait(false);
        }
        _outbox.RecoverTerminalEntries();
    }
    private async Task RecoverClaimAsync(OutboxEntry entry, ManagedComponentContext? owner, CancellationToken cancellationToken)
    {
        owner?.Progress(false, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: entry.Id);
        var worker = new WorkerIdentity(entry.AppSession, entry.WorkerInstance, entry.WorkerGeneration, ComponentRole.Upload);
        await OwnedProcess.ConfirmFormerWorkerExitedAsync(worker, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        _outbox.RetireConfirmedWorker(worker, entry.Id);
        if (_outbox.Read(entry.Id).State == OutboxState.Claimed) throw new IOException("Former upload claim is not durably released.");
    }
    internal async Task<bool> AttemptAsync(Guid id, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new InvalidOperationException("An upload attempt already owns the transport.");
        using var policyCancellation = new CancellationTokenSource();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, policyCancellation.Token);
        attempt.CancelAfter(_attemptTimeout);
        UploadClaim? claim = null;
        TaskCompletionSource<bool>? stopped = null;
        try
        {
            var identity = new WorkerIdentity(_session, Guid.NewGuid(), Interlocked.Increment(ref _generation), ComponentRole.Upload);
            lock (_gate)
            {
                var entry = _outbox.Read(id);
                if (_suspended || entry.Automatic && !_automaticAllowed) return false;
                claim = _outbox.TryClaim(id, identity); if (claim == null) return false;
                _attempt = policyCancellation;
                _attemptStopped = stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (claim.Automatic)
                {
                    _automaticAttempt = policyCancellation;
                    _automaticStopped = stopped;
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
                    if (_suspended || claim.Automatic && !_automaticAllowed) return false;
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
            }, worker =>
            {
                // Native exit and callback drain are already proved. The outbox
                // retains this exact retirement if its metadata is unavailable;
                // that entry cannot acquire another claim before recovery, while
                // independent archives can now use the retired transport slot.
                _outbox.RetireConfirmedWorker(worker, id);
            }).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (policyCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // An explicit policy change is not a transport failure. ProcessJobExecutor
            // has retired the helper; the outbox owns any deferred claim release.
            return false;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_automaticAttempt, policyCancellation)) { _automaticAttempt = null; _automaticStopped = null; }
                if (ReferenceEquals(_attempt, policyCancellation)) { _attempt = null; _attemptStopped = null; }
            }
            Interlocked.Exchange(ref _active, 0);
            // ProcessJobExecutor has already confirmed exit and drained accepted callbacks.
            stopped?.TrySetResult(true);
        }
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
