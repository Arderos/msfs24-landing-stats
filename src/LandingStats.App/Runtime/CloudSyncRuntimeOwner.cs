using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class RuntimeCloudServices
{
    public RuntimeCloudServices(GoogleOAuthTokenStore tokens, GoogleDriveBackupService backup, DeferredGoogleDriveApi binding,
        Func<GoogleDriveSyncResult, Task>? completed = null)
    { Tokens = tokens; Backup = backup; Binding = binding; Completed = completed ?? (_ => Task.CompletedTask); }
    public GoogleOAuthTokenStore Tokens { get; }
    public GoogleDriveBackupService Backup { get; }
    public DeferredGoogleDriveApi Binding { get; }
    public Func<GoogleDriveSyncResult, Task> Completed { get; }
}
[DataContract]
internal sealed class CloudSyncIntent
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public long Requested { get; set; }
    [DataMember(Order = 3)] public long Completed { get; set; }
    [DataMember(Order = 4)] public long CompletedUtcTicks { get; set; }
    [DataMember(Order = 5)] public CloudSyncSummary? Summary { get; set; }
}
[DataContract]
internal sealed class CloudSyncSummary
{
    [DataMember(Order = 1)] public int LandingConflicts { get; set; }
    [DataMember(Order = 2)] public bool SettingsConflict { get; set; }
    [DataMember(Order = 3)] public bool SettingsChanged { get; set; }
    [DataMember(Order = 4)] public int? UnsupportedSettingsVersion { get; set; }
    [DataMember(Order = 5)] public string[] FailedLandingIds { get; set; } = Array.Empty<string>();
    public static CloudSyncSummary From(GoogleDriveSyncResult result) => new CloudSyncSummary
    { LandingConflicts = result.ConflictedLandingIds.Count, SettingsConflict = result.SettingsConflict,
        SettingsChanged = result.SettingsChangedDuringSync, UnsupportedSettingsVersion = result.UnsupportedSettingsFormatVersion,
        FailedLandingIds = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(result.FailedLandings.Keys, 16), id => id.Length <= 128 ? id : id.Substring(0, 128))) };
    public CloudSyncSummary Copy() => new CloudSyncSummary { LandingConflicts = LandingConflicts, SettingsConflict = SettingsConflict,
        SettingsChanged = SettingsChanged, UnsupportedSettingsVersion = UnsupportedSettingsVersion, FailedLandingIds = (string[])(FailedLandingIds ?? Array.Empty<string>()).Clone() };
}
internal enum CloudSyncPhase { Idle, Syncing, Retrying, AuthorizationRequired, Paused }
internal sealed class RuntimeCloudNotice
{
    public RuntimeCloudNotice(long revision, DateTime completedUtc, bool historyChanged, CloudSyncSummary? summary = null)
        : this(revision, completedUtc, historyChanged ? revision : 0, summary) { }
    public RuntimeCloudNotice(long revision, DateTime completedUtc, long historyRevision, CloudSyncSummary? summary,
        CloudSyncPhase phase = CloudSyncPhase.Idle, string? error = null)
    { Revision = revision; CompletedUtc = completedUtc; HistoryRevision = historyRevision; _summary = summary?.Copy(); Phase = phase;
        Error = error == null || error.Length <= 1024 ? error : error.Substring(0, 1024); }
    public long Revision { get; }
    public DateTime CompletedUtc { get; }
    public long HistoryRevision { get; }
    public CloudSyncPhase Phase { get; }
    public string? Error { get; }
    private readonly CloudSyncSummary? _summary;
    public CloudSyncSummary? Summary => _summary?.Copy();
}

/// <summary>Coalesced durable requests; at most an active and a following batch
/// own completion sources. The existing sync algorithm retains its checkpoints,
/// conflict rules and parent-only logbook/settings mutation authority.</summary>
internal sealed class CloudSyncRuntimeOwner
{
    private readonly object _gate = new object();
    private readonly string _path;
    private readonly IsolatedGoogleDriveApi _api;
    private readonly GoogleDriveBackupService _backup;
    private readonly Action<RuntimeCloudNotice> _changed;
    private readonly Func<GoogleDriveSyncResult, Task> _completed;
    private CloudSyncIntent _state;
    private TaskCompletionSource<GoogleDriveSyncResult>? _pending;
    private ManagedComponentContext? _owner;
    private bool _stopping;
    private int _backgroundRequested = 1;
    private int _presentationWait;
    // Presentation transitions must not advance durable batch completion or
    // pretend that progress/error notices changed the local history.
    private long _noticeRevision, _historyRevision;
    private RuntimeCloudNotice? _lastNotice;
    public bool WaitingForPresentation => Volatile.Read(ref _presentationWait) != 0;
    public CloudSyncRuntimeOwner(string path, IsolatedGoogleDriveApi api, GoogleDriveBackupService backup, Action<RuntimeCloudNotice> changed,
        Func<GoogleDriveSyncResult, Task>? completed = null)
    {
        _path = Path.GetFullPath(path); _api = api; _backup = backup; _changed = changed;
        _completed = completed ?? (_ => Task.CompletedTask);
        _state = File.Exists(_path) ? DurableFile.ReadJson<CloudSyncIntent>(_path, 4096) : new CloudSyncIntent();
        if (_state.Version != 1 || _state.Requested < 0 || _state.Completed < 0 || _state.Completed > _state.Requested ||
            _state.CompletedUtcTicks < 0 || _state.CompletedUtcTicks > DateTime.MaxValue.Ticks || _state.Summary != null &&
                (_state.Summary.LandingConflicts < 0 || _state.Summary.LandingConflicts > 65536 || _state.Summary.UnsupportedSettingsVersion < 0))
            throw new InvalidDataException("Invalid cloud sync intent.");
        if (_state.CompletedUtcTicks != 0) _historyRevision = _state.Completed;
    }
    public Task<GoogleDriveSyncResult> Request()
    {
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException("Cloud sync intake is closing.");
            if (!_api.Enabled) throw new GoogleDriveNotSignedInException();
            var next = new CloudSyncIntent { Requested = checked(_state.Requested + 1), Completed = _state.Completed,
                CompletedUtcTicks = _state.CompletedUtcTicks, Summary = _state.Summary };
            DurableFile.WriteJson(_path, next); _state = next;
            _pending ??= Completion(); Volatile.Read(ref _owner)?.SignalWork(); return _pending.Task;
        }
    }
    public void RequestBackground()
    {
        // A report commit never performs cloud storage or waits for the cloud
        // owner. On restart the local logbook is reconciled again regardless
        // of whether this bounded hint reached the persisted request counter.
        if (Volatile.Read(ref _stopping)) return;
        Interlocked.Exchange(ref _backgroundRequested, 1); Volatile.Read(ref _owner)?.SignalWork();
    }
    public void Wake() => Volatile.Read(ref _owner)?.SignalWork();
    public void StopIntake()
    { lock (_gate) { _stopping = true; _pending?.TrySetCanceled(); _pending = null; } }
    public async Task RunAsync(ManagedComponentContext owner)
    {
        if (Interlocked.CompareExchange(ref _owner, owner, null) != null) throw new InvalidOperationException("Cloud sync already has an owner.");
        var progress = new System.Collections.Concurrent.ConcurrentQueue<Tuple<Guid, bool, DependencyWait, long>>();
        void Progress(Guid operation, bool completed) => progress.Enqueue(Tuple.Create(operation, completed, DependencyWait.Network, Stopwatch.GetTimestamp()));
        void LocalProgress(Guid operation, bool completed) => progress.Enqueue(Tuple.Create(operation, completed, DependencyWait.Storage, Stopwatch.GetTimestamp()));
        _api.Progress += Progress;
        _backup.Progress += LocalProgress;
        try
        {
            owner.MarkReady();
            if (_lastNotice == null && _state.CompletedUtcTicks != 0) Publish(CloudSyncPhase.Idle);
            while (true)
            {
                owner.CancellationToken.ThrowIfCancellationRequested();
                if (!_api.Enabled)
                {
                    PublishUnavailable();
                    lock (_gate) { _pending?.TrySetException(new GoogleDriveNotSignedInException()); _pending = null; }
                    owner.Progress(false, DependencyWait.Authorization); await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); continue;
                }
                lock (_gate)
                {
                    if (Volatile.Read(ref _backgroundRequested) != 0 && !_stopping)
                    {
                        var task = Request(); Interlocked.Exchange(ref _backgroundRequested, 0);
                        _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
                long target; TaskCompletionSource<GoogleDriveSyncResult>? batch = null;
                lock (_gate)
                {
                    target = _state.Requested;
                    if (target > _state.Completed) { batch = _pending; _pending = null; }
                }
                if (target <= _state.Completed)
                { Publish(CloudSyncPhase.Idle); owner.Progress(false, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); continue; }
                try
                {
                    Publish(CloudSyncPhase.Syncing);
                    using var scope = _api.BeginSync();
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.CancellationToken, scope.Cancellation);
                    using var propagation = new CloudOperationCancellation(cancellation.Token, waiting => Volatile.Write(ref _presentationWait, waiting ? 1 : 0));
                    owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.NewGuid());
                    var syncing = Task.Run(() => _backup.SyncAsync(cancellation.Token));
                    // Foreign API/storage callbacks only enqueue observations.
                    // This owner alone publishes progress and answers probes.
                    while (!syncing.IsCompleted)
                    {
                        while (progress.TryDequeue(out var observed))
                            owner.Progress(observed.Item2, observed.Item3, oldestPendingQpc: observed.Item2 ? 0 : observed.Item4,
                                operationId: observed.Item2 ? Guid.Empty : observed.Item1);
                        await Task.WhenAny(syncing, Task.Delay(25)).ConfigureAwait(false);
                    }
                    while (progress.TryDequeue(out _)) { }
                    var result = await syncing.ConfigureAwait(false);
                    await _completed(result).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    CloudSyncIntent next;
                    lock (_gate)
                    {
                        next = new CloudSyncIntent { Requested = _state.Requested, Completed = target, CompletedUtcTicks = DateTime.UtcNow.Ticks, Summary = CloudSyncSummary.From(result) };
                        DurableFile.WriteJson(_path, next); _state = next;
                        if (result.HistoryChanged) _historyRevision = target;
                    }
                    Publish(CloudSyncPhase.Idle);
                    batch?.TrySetResult(result); owner.Progress(true);
                }
                catch (GoogleDriveNotSignedInException error)
                { PublishUnavailable(); batch?.TrySetException(error); owner.Progress(false, DependencyWait.Authorization); await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (OperationCanceledException)
                { batch?.TrySetCanceled(); if (owner.CancellationToken.IsCancellationRequested) throw; PublishUnavailable(); }
                catch (Exception error) { batch?.TrySetException(error); throw; }
            }
        }
        catch (OperationCanceledException) when (owner.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { Publish(CloudSyncPhase.Retrying, error.Message); throw; }
        finally { _api.Progress -= Progress; _backup.Progress -= LocalProgress; Volatile.Write(ref _owner, null); }
    }
    private void PublishUnavailable() => Publish(_api.AuthorizationRequired ? CloudSyncPhase.AuthorizationRequired :
        _api.Enabled ? CloudSyncPhase.Retrying : CloudSyncPhase.Paused);
    private void Publish(CloudSyncPhase phase, string? error = null)
    {
        RuntimeCloudNotice notice;
        lock (_gate)
        {
            var completed = _state.CompletedUtcTicks == 0 ? DateTime.MinValue : new DateTime(_state.CompletedUtcTicks, DateTimeKind.Utc);
            if (_lastNotice != null && _lastNotice.Phase == phase && _lastNotice.Error == error &&
                _lastNotice.CompletedUtc == completed && _lastNotice.HistoryRevision == _historyRevision) return;
            notice = new RuntimeCloudNotice(++_noticeRevision, completed, _historyRevision, _state.Summary, phase, error);
            _lastNotice = notice;
        }
        _changed(notice);
    }
    private static TaskCompletionSource<GoogleDriveSyncResult> Completion() => new TaskCompletionSource<GoogleDriveSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
}
