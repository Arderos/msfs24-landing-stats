using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Telemetry;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class RuntimeEpisodeNotice
{
    public RuntimeEpisodeNotice(RuntimeEpisodeUpdate value)
    { Episode = value.Episode; TimestampUtc = value.TimestampUtc; Ordinal = value.Ordinal; DiagnosticOnly = value.DiagnosticOnly;
        ReportReady = value.Report != null; ReplayLike = value.Processed.ReplayLike; RecordCount = value.Processed.Records.Count;
        // Match the persisted analysis record-count bound; retain only IDs,
        // never telemetry or mutable landing DTOs in the projection.
        if (RecordCount > 65536) throw new ArgumentException("Episode presentation exceeds the analysis record bound.");
        RecordIds = Array.AsReadOnly(value.Processed.Records.Select(record => record.Id).ToArray()); }
    public Guid Episode { get; }
    public DateTime TimestampUtc { get; }
    public long Ordinal { get; }
    public bool DiagnosticOnly { get; }
    public bool ReportReady { get; }
    public bool ReplayLike { get; }
    public int RecordCount { get; }
    public IReadOnlyList<string> RecordIds { get; }
}
internal sealed class RuntimePresentationSnapshot
{
    public RuntimePresentationSnapshot(long revision, RecorderStatusEventArgs? status, bool? airborne, Guid activeEpisode, long activeOrdinal, bool simulatorExited,
        RuntimeEpisodeNotice[] episodes, SupervisionSnapshot[] health, Guid upload, OutboxState? uploadState, RuntimeCloudNotice? cloud = null, bool analysisPending = false,
        bool analysisFailed = false)
    { Revision = revision; Status = status; Airborne = airborne; ActiveEpisode = activeEpisode; ActiveOrdinal = activeOrdinal; SimulatorExited = simulatorExited;
        Episodes = episodes; Health = health; Upload = upload; UploadState = uploadState; Cloud = cloud; AnalysisPending = analysisPending; AnalysisFailed = analysisFailed; }
    public long Revision { get; }
    public RecorderStatusEventArgs? Status { get; }
    public bool? Airborne { get; }
    public Guid ActiveEpisode { get; }
    public long ActiveOrdinal { get; }
    public bool SimulatorExited { get; }
    public IReadOnlyList<RuntimeEpisodeNotice> Episodes { get; }
    public IReadOnlyList<SupervisionSnapshot> Health { get; }
    public Guid Upload { get; }
    public OutboxState? UploadState { get; }
    public RuntimeCloudNotice? Cloud { get; }
    public bool AnalysisPending { get; }
    public bool AnalysisFailed { get; }
    public RuntimeFailureNotice? Failure { get; internal set; }
}
internal sealed class RuntimeFailureNotice
{
    public RuntimeFailureNotice(long revision, string component, Guid entity, string message)
    { Revision = revision; Component = component; Entity = entity; Message = message.Length <= 1024 ? message : message.Substring(0, 1024); }
    public long Revision { get; }
    public string Component { get; }
    public Guid Entity { get; }
    public string Message { get; }
}

/// <summary>A bounded projection, never a recording queue. It keeps IDs of
/// committed records instead of raw frames or mutable history objects. The UI
/// can rebuild from storage after skipped revisions or dispatcher recovery.</summary>
internal sealed class RuntimePresentation
{
    internal const int EpisodeNoticeCapacity = 32;
    private readonly object _gate = new object();
    private readonly Dictionary<Guid, RuntimeEpisodeNotice> _episodes = new Dictionary<Guid, RuntimeEpisodeNotice>();
    private readonly Dictionary<string, SupervisionSnapshot> _health = new Dictionary<string, SupervisionSnapshot>();
    private readonly Func<RuntimePresentationSnapshot, CancellationToken, Task> _apply;
    private ManagedComponentContext? _owner;
    private long _revision, _activeOrdinal;
    private RecorderStatusEventArgs? _status;
    private bool? _airborne;
    private Guid _activeEpisode, _upload, _pendingAnalysis, _failedAnalysis;
    private OutboxState? _uploadState;
    private bool _simulatorExited;
    private RuntimeCloudNotice? _cloud;
    private RuntimeFailureNotice? _failure;
    public void Failure(string component, Guid entity, string message)
    {
        lock (_gate)
        {
            _failure = new RuntimeFailureNotice(_revision + 1, component, entity, message);
            if (component == "analysis.episodes" && entity == _activeEpisode) _failedAnalysis = entity;
            Changed();
        }
    }
    public RuntimePresentation(Func<RuntimePresentationSnapshot, CancellationToken, Task> apply) { _apply = apply; }
    public void Recording(RecoveryJournalIdentity identity, RecordingRuntimeEffects effects)
    {
        lock (_gate)
        {
            if (effects.Presentation.Count == 0 && effects.Captures.Count == 0 && !effects.SimulatorExited) return;
            foreach (var item in effects.Presentation)
            {
                if (item is RecorderStatusEventArgs status) _status = new RecorderStatusEventArgs(status.State, status.MessageKey, status.MessageArguments.ToArray());
                else if (item is AircraftGroundStateEventArgs ground) _airborne = !ground.OnGround;
                else if (item is LandingEpisodeStartedEventArgs started)
                {
                    var episode = StableRuntimeIdentity.Episode(identity.CaptureSessionId, started.EpisodeId);
                    if (episode != _activeEpisode) _failedAnalysis = Guid.Empty;
                    _activeOrdinal = started.EpisodeId; _activeEpisode = episode;
                }
            }
            // Recording effects reach this projection only after their inputs
            // are durable. History may already have won the race to publish.
            foreach (var capture in effects.Captures)
                if (!capture.Episode.IsDiagnosticOnly && capture.Episode.PersistentId == _activeEpisode && !_episodes.ContainsKey(_activeEpisode))
                {
                    _pendingAnalysis = _activeEpisode;
                    if (_failure?.Component == "analysis.episodes" && _failure.Entity == _activeEpisode) _failedAnalysis = _activeEpisode;
                }
            _simulatorExited |= effects.SimulatorExited; Changed();
        }
    }
    public void Episode(RuntimeEpisodeUpdate update)
    {
        lock (_gate)
        {
            if (_episodes.TryGetValue(update.Episode, out var previous) && previous.ReportReady && update.Report == null) return;
            _episodes[update.Episode] = new RuntimeEpisodeNotice(update);
            if (_pendingAnalysis == update.Episode) _pendingAnalysis = Guid.Empty;
            if (_failedAnalysis == update.Episode) _failedAnalysis = Guid.Empty;
            if (_episodes.Count > EpisodeNoticeCapacity) _episodes.Remove(_episodes.Values.OrderBy(value => value.TimestampUtc).ThenBy(value => value.Episode).First().Episode);
            Changed();
        }
    }
    public void Upload(OutboxEntry value)
    { lock (_gate) { _upload = value.Id; _uploadState = value.State; Changed(); } }
    public void Cloud(RuntimeCloudNotice value)
    {
        lock (_gate)
        {
            if (_cloud != null && value.Revision < _cloud.Revision) return;
            _cloud = new RuntimeCloudNotice(value.Revision, value.CompletedUtc, Math.Max(_cloud?.HistoryRevision ?? 0, value.HistoryRevision), value.Summary, value.Phase, value.Error);
            Changed();
        }
    }
    public void Health(SupervisionSnapshot value)
    {
        lock (_gate)
        {
            _health.TryGetValue(value.Component, out var previous);
            if (_health.Count >= 64 && previous == null) throw new InvalidOperationException("Runtime component registry exceeds its presentation bound.");
            _health[value.Component] = value;
            bool Healthy(SupervisionState state) => state == SupervisionState.Running || state == SupervisionState.Waiting;
            if (previous != null && previous.Generation == value.Generation && previous.Failures == value.Failures && Healthy(previous.State) && Healthy(value.State)) return;
            Changed();
        }
    }
    public RuntimePresentationSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new RuntimePresentationSnapshot(_revision,
                _status == null ? null : new RecorderStatusEventArgs(_status.State, _status.MessageKey, _status.MessageArguments.ToArray()),
                _airborne, _activeEpisode, _activeOrdinal, _simulatorExited,
                _episodes.Values.OrderBy(value => value.TimestampUtc).ToArray(), _health.Values.ToArray(), _upload, _uploadState, _cloud,
                _activeEpisode != Guid.Empty && _pendingAnalysis == _activeEpisode,
                _activeEpisode != Guid.Empty && _pendingAnalysis == _activeEpisode && _failedAnalysis == _activeEpisode) { Failure = _failure };
        }
    }
    public async Task RunAsync(ManagedComponentContext owner)
    {
        if (Interlocked.CompareExchange(ref _owner, owner, null) != null) throw new InvalidOperationException("Presentation already has an owner.");
        try
        {
            owner.MarkReady(); long applied = -1;
            while (true)
            {
                owner.CancellationToken.ThrowIfCancellationRequested(); var snapshot = Snapshot;
                if (snapshot.Revision != applied)
                {
                    owner.Progress(false, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.NewGuid());
                    await _apply(snapshot, owner.CancellationToken).ConfigureAwait(false); applied = snapshot.Revision; owner.Progress(true);
                }
                else owner.Progress(false, DependencyWait.Idle);
                await owner.WaitForWorkAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _owner, null); }
    }
    private void Changed() { _revision++; Volatile.Read(ref _owner)?.SignalWork(); }
}
