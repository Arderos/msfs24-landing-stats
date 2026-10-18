using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class AnalysisJobException : IOException
{ public AnalysisJobException(string message) : base(message) { } }

/// <summary>One analysis attempt, isolated in its retained binary deployment.
/// Its caller is supervised and retries the durable episode after failure.
/// Cancellation does not release ownership until the entire helper job exits.</summary>
internal sealed class RetainedAnalysisExecutor
{
    private readonly string _engines, _scratch;
    private readonly EpisodeTransactionStore _store;
    private readonly Guid _session;
    private readonly RuntimeClockEpoch _clock;
    private readonly SemaphoreSlim _oneAttempt = new SemaphoreSlim(1, 1);
    private readonly Func<long, string?>? _fixture;
    private readonly Func<string, CancellationToken, Task<bool>>? _preparedCatalog;
    private long _generation;
    private readonly System.Collections.Generic.HashSet<string> _legacyGeometryEngines = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
    private sealed class LegacyGeometryProtocolException : Exception { }
    public RetainedAnalysisExecutor(string engines, string scratch, EpisodeTransactionStore store, Guid session, RuntimeClockEpoch clock, Func<long, string?>? fixture = null,
        Func<string, CancellationToken, Task<bool>>? preparedCatalog = null)
    { _engines = Path.GetFullPath(engines); _scratch = Path.GetFullPath(scratch); _store = store; _session = session; _clock = clock; _fixture = fixture; _preparedCatalog = preparedCatalog; }
    internal event Action<WorkerIdentity>? WorkerStarted;
    public Task RefreshGeometryCatalogAsync(string simulator, CancellationToken cancellationToken) =>
        RunWorkerAsync(_store.Engine, Guid.NewGuid(), _store.Root, AnalysisOperation.RefreshGeometryCatalog, path =>
        {
            var target = FlightModelGeometryResolver.PreparedCatalogPath(Path.Combine(Path.GetDirectoryName(_store.Root)!, "geometry-index"), simulator);
            DurableFile.Write(target, output => { using var source = File.OpenRead(path); source.CopyTo(output); });
            return true;
        }, cancellationToken, simulator);
    public Task<ProcessedEpisode> AnalyzeAsync(EpisodeWorkManifest manifest, CancellationToken cancellationToken) => RunOwnedAsync(manifest, false, null, null, cancellationToken);
    public Task<ProcessedEpisode> FinalizeReportAsync(EpisodeWorkManifest manifest, LandingRepository repository, object repositoryGate, CancellationToken cancellationToken) =>
        RunOwnedAsync(manifest, true, repository, repositoryGate, cancellationToken);
    public async Task<RecordingRecoveryBundle> RecoverRecordingAsync(string sessionRoot, CancellationToken cancellationToken)
    {
        await _oneAttempt.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var ownership = RuntimeSession.AcquireExistingOwnership(sessionRoot);
            if (File.Exists(Path.Combine(sessionRoot, "collected.json"))) throw new IOException("This runtime session has already been collected.");
            var session = RuntimeSession.Read(sessionRoot); var marker = Path.Combine(sessionRoot, "recovered.json");
            if (File.Exists(marker))
            {
                var recovered = DurableFile.ReadJson<RecordingRecoveryBundle>(marker, 64L * 1024 * 1024);
                ColdRecordingRecovery.Validate(recovered, session); return recovered;
            }
            return await RunWorkerAsync(session.Engine, session.Runtime, sessionRoot, AnalysisOperation.RecoverRecording,
                path => ColdRecordingRecovery.Import(sessionRoot, Path.GetDirectoryName(path)!, _store), cancellationToken).ConfigureAwait(false);
        }
        finally { _oneAttempt.Release(); }
    }
    private async Task<ProcessedEpisode> RunOwnedAsync(EpisodeWorkManifest manifest, bool finalReport, LandingRepository? repository, object? repositoryGate, CancellationToken cancellationToken)
    {
        await _oneAttempt.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RunAttemptAsync(manifest, finalReport, repository, repositoryGate, cancellationToken).ConfigureAwait(false); }
        finally { _oneAttempt.Release(); }
    }
    private async Task<ProcessedEpisode> RunAttemptAsync(EpisodeWorkManifest manifest, bool finalReport, LandingRepository? repository, object? repositoryGate, CancellationToken cancellationToken)
    {
        var current = _store.Read(manifest.Id);
        if (current.Stage >= (finalReport ? EpisodeWorkStage.ReportReady : EpisodeWorkStage.Analysed))
            return finalReport ? _store.ReadReport(current) : _store.ReadAnalysis(current);
        // A retained older engine keeps its original protocol and semantics.
        // Only this deployment's jobs opt into the additive geometry stage.
        if (!finalReport && string.IsNullOrEmpty(current.GeometryHash) && !_legacyGeometryEngines.Contains(current.Engine))
        {
            try
            {
                var operation = AnalysisOperation.ResolveGeometry;
                // Older retained engines keep their protocol and offline recovery
                // path. Live episodes wait for the already-running background job.
                if (current.Engine == _store.Engine && _preparedCatalog != null)
                {
                    var input = _store.ReadInputs(current);
                    if (!input.Episode.IsDiagnosticOnly && await _preparedCatalog(input.Episode.Simulator, cancellationToken).ConfigureAwait(false))
                        operation = AnalysisOperation.ResolvePreparedGeometry;
                }
                await RunWorkerAsync(current.Engine, current.Id, _store.Root, operation, path =>
                {
                    var geometry = DurableFile.ReadJson<EpisodeGeometryData>(path, 64L * 1024 * 1024);
                    _store.CommitGeometry(current, geometry); return true;
                }, cancellationToken).ConfigureAwait(false);
                current = _store.Read(current.Id);
            }
            catch (LegacyGeometryProtocolException) { _legacyGeometryEngines.Add(current.Engine); }
        }
        return await RunWorkerAsync(current.Engine, current.Id, _store.Root, finalReport ? AnalysisOperation.FinalizeReport : AnalysisOperation.Analyze, path =>
        {
            var analysis = EpisodeTransactionStore.ReadWorkerAnalysis(current, path, finalReport);
            return finalReport ? _store.CommitReport(current, analysis, repository!, repositoryGate!) : _store.CommitAnalysis(current, analysis, current.Engine);
        }, cancellationToken).ConfigureAwait(false);
    }
    private async Task<T> RunWorkerAsync<T>(string engineIdentity, Guid entity, string inputRoot, AnalysisOperation operation, Func<string, T> commit, CancellationToken cancellationToken, string simulator = "")
    {
        var engine = ProcessingEngineLease.Open(_engines, engineIdentity);
        using var engineUse = engine.AcquireUse();
        var job = Guid.NewGuid(); var output = Path.Combine(_scratch, job.ToString("N"));
        using var scratch = RuntimeScratchLease.Create(_scratch, job);
        var identity = new WorkerIdentity(_session, Guid.NewGuid(), Interlocked.Increment(ref _generation), ComponentRole.Analysis);
        var timelineRoot = Path.GetDirectoryName(_store.Root)!;
        var geometry = operation == AnalysisOperation.ResolveGeometry || operation == AnalysisOperation.ResolvePreparedGeometry;
        var episodeJob = operation != AnalysisOperation.RecoverRecording && operation != AnalysisOperation.RefreshGeometryCatalog;
        if (episodeJob)
            EpisodeTimeline.Mark(timelineRoot, entity, geometry ? EpisodeTimelinePhase.GeometryStarted :
                operation == AnalysisOperation.FinalizeReport ? EpisodeTimelinePhase.ReportStarted : EpisodeTimelinePhase.AnalysisStarted);
        return await ProcessJobExecutor.RunAsync(Path.Combine(engine.DirectoryPath, "LandingStats.WorkerHost.exe"), identity, _clock,
            new AnalysisJobMessage(job, entity, engine.Identity, inputRoot, output, operation, simulator), message =>
            {
                if (!(message.Payload is AnalysisResultMessage result) || result.Job != job) throw new InvalidDataException("Unexpected analysis result context.");
                if (result.Failure.Length != 0) throw new AnalysisJobException("Retained analysis failed: " + result.Failure);
                var path = Path.Combine(output, "result.json");
                if (new FileInfo(path).Length != result.Length) throw new InvalidDataException("Analysis result length changed.");
                EpisodeTransactionStore.Verify(path, result.Hash);
                var committed = commit(path);
                if (episodeJob)
                    EpisodeTimeline.Mark(timelineRoot, entity, geometry ? EpisodeTimelinePhase.GeometryCommitted :
                        operation == AnalysisOperation.FinalizeReport ? EpisodeTimelinePhase.ReportCommitted : EpisodeTimelinePhase.AnalysisCommitted);
                return committed;
            }, cancellationToken, _fixture?.Invoke(identity.Generation), host =>
            {
                if (geometry && (host.Capabilities & RuntimeCapabilities.FrozenGeometry) == 0)
                    throw new LegacyGeometryProtocolException();
                try { WorkerStarted?.Invoke(identity); } catch { /* Diagnostics cannot take ownership of a job. */ }
            }, requiredCapabilities: RuntimeCapabilities.Basic | (RuntimeStorageQuota.RequiresParticipation(_scratch) ? RuntimeCapabilities.SharedStorageQuota : 0)).ConfigureAwait(false);
    }
}
