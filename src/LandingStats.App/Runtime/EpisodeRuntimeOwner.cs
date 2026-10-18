using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Storage;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class RuntimeEpisodeUpdate
{
    public RuntimeEpisodeUpdate(Guid episode, ProcessedEpisode processed, BugReportCandidate? report, DateTime timestampUtc, long ordinal, bool diagnosticOnly)
    { Episode = episode; Processed = processed; Report = report; TimestampUtc = timestampUtc; Ordinal = ordinal; DiagnosticOnly = diagnosticOnly; }
    public Guid Episode { get; }
    public ProcessedEpisode Processed { get; }
    public BugReportCandidate? Report { get; }
    public DateTime TimestampUtc { get; }
    public long Ordinal { get; }
    public bool DiagnosticOnly { get; }
}

/// <summary>Resumable analysis/history/report/archive pipeline. A per-episode
/// storage failure retains its inputs and receives bounded retries; later
/// episodes remain eligible. The parent alone publishes and registers archives.</summary>
internal sealed class EpisodeRuntimeOwner
{
    private readonly EpisodeTransactionStore _store;
    private readonly EpisodeWorkCoordinator _work;
    private readonly BugReportRepository _archives;
    private readonly DurableOutbox _outbox;
    private readonly Action<RuntimeEpisodeUpdate> _present;
    private ManagedComponentContext? _owner;
    private readonly Dictionary<Guid, Retry> _retry = new Dictionary<Guid, Retry>();
    private readonly HashSet<Guid> _presentedHistory = new HashSet<Guid>();
    private readonly HashSet<Guid> _presentedReport = new HashSet<Guid>();
    private readonly HashSet<Guid> _terminal = new HashSet<Guid>();
    private long _nextScan;
    private int _wakeRequested = 1;
    public event Action<Guid, string>? Failed;
    public EpisodeRuntimeOwner(EpisodeTransactionStore store, EpisodeWorkCoordinator work, BugReportRepository archives, DurableOutbox outbox,
        Action<RuntimeEpisodeUpdate> present)
    { _store = store; _work = work; _archives = archives; _outbox = outbox; _present = present; }
    public void Wake() { Interlocked.Exchange(ref _wakeRequested, 1); Volatile.Read(ref _owner)?.SignalWork(); }
    public async Task RunAsync(ManagedComponentContext owner)
    {
        if (Interlocked.CompareExchange(ref _owner, owner, null) != null) throw new InvalidOperationException("Episode pipeline already has an owner.");
        try
        {
            owner.MarkReady();
            while (true)
            {
                var worked = false;
                if (Interlocked.Exchange(ref _wakeRequested, 0) == 0 && Stopwatch.GetTimestamp() < _nextScan)
                { owner.Progress(false, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); continue; }
                _nextScan = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                foreach (var id in _store.EnumeratePending())
                {
                    owner.CancellationToken.ThrowIfCancellationRequested();
                    var now = Stopwatch.GetTimestamp();
                    if (_terminal.Contains(id)) continue;
                    if (_retry.TryGetValue(id, out var previous) && now < previous.After) continue;
                    try
                    {
                        if (_store.HasFailed(id)) { _terminal.Add(id); continue; }
                        var manifest = _store.Read(id);
                        if (manifest.IsFailed || _store.HasFailed(id) || manifest.Stage == EpisodeWorkStage.Completed) { _terminal.Add(id); continue; }
                        if (manifest.Stage < EpisodeWorkStage.InputsCommitted) continue;
                        owner.Progress(false, DependencyWait.Storage);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(owner.CancellationToken);
                        deadline.CancelAfter(TimeSpan.FromSeconds(120));
                        var operation = AdvanceAsync(id, deadline.Token);
                        while (!operation.IsCompleted)
                        {
                            owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: now, operationId: id);
                            await Task.WhenAny(operation, Task.Delay(250)).ConfigureAwait(false);
                        }
                        worked |= await operation.ConfigureAwait(false);
                        if (_store.Read(id).Stage == EpisodeWorkStage.Completed) _terminal.Add(id);
                        _retry.Remove(id); owner.Progress(true);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException ||
                        error is SerializationException || error is ArgumentException || error is InvalidOperationException ||
                        error is OperationCanceledException && !owner.CancellationToken.IsCancellationRequested)
                    {
                        var count = Math.Min(20, (previous?.Count ?? 0) + 1);
                        _retry[id] = new Retry(count, now + (long)(Math.Min(30, Math.Pow(2, count - 1)) * Stopwatch.Frequency));
                        var terminal = count >= 5 && !(error is UnauthorizedAccessException) &&
                            (!(error is IOException) || error.GetType().Name == "AnalysisJobException");
                        try { _store.Fail(id, error, terminal); }
                        catch (Exception storageError) when (storageError is IOException || storageError is UnauthorizedAccessException)
                        { Failed?.Invoke(id, error.Message + "; diagnostic persistence: " + storageError.Message); }
                        if (terminal) _terminal.Add(id);
                        Failed?.Invoke(id, error.ToString());
                    }
                }
                owner.Progress(worked, DependencyWait.Idle);
                await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _owner, null); }
    }
    internal async Task<bool> AdvanceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var manifest = _store.Read(id);
        if (manifest.Stage == EpisodeWorkStage.Completed) return false;
        if (manifest.Stage == EpisodeWorkStage.HistorySaved && string.IsNullOrEmpty(manifest.FacilityHash) && _presentedHistory.Contains(id)) return false;
        var input = _store.ReadInputs(manifest); var episode = input.Episode;
        if (manifest.Stage < EpisodeWorkStage.HistorySaved) await _work.ResumeAsync(id, cancellationToken).ConfigureAwait(false);
        manifest = _store.Read(id);
        if (!_presentedHistory.Contains(id))
        { _present(new RuntimeEpisodeUpdate(id, _store.ReadAnalysis(manifest), null, manifest.TimestampUtc, episode.EpisodeId, episode.IsDiagnosticOnly)); _presentedHistory.Add(id); }
        var report = await _work.FinalizeReportAsync(id, cancellationToken).ConfigureAwait(false);
        if (report == null) return false;
        manifest = _store.Read(id);
        var candidate = new BugReportCandidate(episode.EpisodeId, episode.Samples, episode.Simulator, episode.AircraftTitle,
            episode.AircraftType, episode.AircraftModel, episode.ControlInputSources, report.ReportRecords, report.Decisions);
        if (!_presentedReport.Contains(id)) { _present(new RuntimeEpisodeUpdate(id, report, candidate, manifest.TimestampUtc, episode.EpisodeId, episode.IsDiagnosticOnly)); _presentedReport.Add(id); }
        if (manifest.Stage == EpisodeWorkStage.ReportReady)
        {
            var policy = _outbox.Policy; string? skip = null;
            // Publication can precede a crash before CommitArchive. Adopt those
            // verified bytes even after opt-out; outbox records them as revoked.
            var path = manifest.AutomaticAllowedAtCapture ? _archives.ExistingAutomatic(manifest.TimestampUtc, manifest.ArchiveId) : null;
            if (path != null) { }
            else if (!manifest.AutomaticAllowedAtCapture) skip = "capture_opted_out";
            else if (!policy.AutomaticEnabled) skip = "current_policy_opted_out";
            else if (episode.Samples.Count == 0) skip = "no_telemetry";
            else
            {
                path = AutomaticTelemetryArchive.TryCreate(_archives, candidate, manifest.TimestampUtc, archiveId: manifest.ArchiveId, throwOnStorageFailure: true);
                if (path == null) skip = "automatic_queue_full";
            }
            _store.CommitArchive(manifest, path, skip, policy.Revision);
        }
        if (manifest.Stage == EpisodeWorkStage.ArchivePublished)
        {
            if (string.IsNullOrEmpty(manifest.ArchiveSkipReason))
                _outbox.Register(manifest.ArchiveId, manifest.ArchivePath, manifest.ArchiveHash, true, manifest.TimestampUtc);
            _store.MarkOutboxEnqueued(manifest);
        }
        if (manifest.Stage == EpisodeWorkStage.OutboxEnqueued) _store.Complete(manifest);
        _presentedHistory.Remove(id); _presentedReport.Remove(id);
        return true;
    }
    private sealed class Retry
    {
        public Retry(int count, long after) { Count = count; After = after; }
        public readonly int Count;
        public readonly long After;
    }
}
