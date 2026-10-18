using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.Runtime;

/// <summary>Replayable analysis/storage stages. Scheduling and retry belong to the runtime supervisor.</summary>
internal sealed class EpisodeWorkCoordinator
{
    private readonly EpisodeTransactionStore _store;
    private readonly EpisodeProcessor? _processor;
    private readonly LandingRepository _repository;
    private readonly object _repositoryGate;
    private readonly RetainedAnalysisExecutor? _isolatedAnalysis;
    private readonly Action<Guid, ProcessedEpisode, bool>? _resultCommitted;
    public EpisodeWorkCoordinator(EpisodeTransactionStore store, EpisodeProcessor? processor, LandingRepository repository, object repositoryGate,
        RetainedAnalysisExecutor? isolatedAnalysis = null, Action<Guid, ProcessedEpisode, bool>? resultCommitted = null)
    {
        if (processor == null && isolatedAnalysis == null) throw new ArgumentNullException(nameof(processor));
        _store = store; _processor = processor; _repository = repository; _repositoryGate = repositoryGate; _isolatedAnalysis = isolatedAnalysis;
        _resultCommitted = resultCommitted;
    }

    public async Task<ProcessedEpisode> ProcessAsync(LandingEpisodeEventArgs episode, DateTime timestamp,
        IReadOnlyList<AirportFacility> known, bool automaticAllowed, long policyRevision)
    {
        var manifest = _store.CommitInputs(episode, timestamp, known, automaticAllowed, policyRevision);
        return await ResumeAsync(manifest.Id).ConfigureAwait(false);
    }

    public async Task<ProcessedEpisode> ResumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var manifest = _store.Read(id);
        {
            if (manifest.Stage < EpisodeWorkStage.Analysed)
            {
                if (_isolatedAnalysis != null)
                {
                    await _isolatedAnalysis.AnalyzeAsync(manifest, cancellationToken).ConfigureAwait(false);
                    manifest = _store.Read(id);
                }
                else
                {
                    _store.RequireEngine(manifest);
                    var input = _store.ReadInputs(manifest);
                    if (string.IsNullOrEmpty(manifest.GeometryHash))
                    {
                        _store.CommitGeometry(manifest, await _processor!.ResolveGeometryAsync(input, manifest).ConfigureAwait(false));
                        manifest = _store.Read(id);
                    }
                    var result = await _processor!.ProcessAsync(input.Episode, input.Metadata.KnownAirports,
                        input.Metadata.TimestampUtc, persistHistory: false, geometryInputs: _store.ReadGeometry(manifest)).ConfigureAwait(false);
                    _store.CommitAnalysis(manifest, result);
                }
            }
            // The immutable analysis is already durable. Integrations receive
            // its complete episode before history gzip or diagnostic ZIP work.
            // Their durable projection can be repaired from this same manifest.
            _resultCommitted?.Invoke(id, _store.ReadAnalysis(manifest), false);
            var saved = _store.SaveHistory(manifest, _repository, _repositoryGate);
            EpisodeTimeline.Mark(System.IO.Path.GetDirectoryName(_store.Root)!, id, EpisodeTimelinePhase.HistoryCommitted, saved.Records.Count);
            return saved;
        }
    }

    public async Task<ProcessedEpisode?> FinalizeReportAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var manifest = _store.Read(id);
        if (manifest.Stage >= EpisodeWorkStage.ReportReady)
        {
            var existing = _store.ReadReport(manifest); _resultCommitted?.Invoke(id, existing, true); return existing;
        }
        if (manifest.Stage < EpisodeWorkStage.HistorySaved || string.IsNullOrEmpty(manifest.FacilityHash)) return null;
        if (_isolatedAnalysis != null)
        {
            var finalReport = await _isolatedAnalysis.FinalizeReportAsync(manifest, _repository, _repositoryGate, cancellationToken).ConfigureAwait(false);
            _resultCommitted?.Invoke(id, finalReport, true); return finalReport;
        }
        var input = _store.ReadInputs(manifest); var processed = _store.ReadAnalysis(manifest); var final = _store.ReadFacilities(manifest);
        var committed = _store.CommitReport(manifest, BuildReport(input, processed, final), _repository, _repositoryGate);
        _resultCommitted?.Invoke(id, committed, true); return committed;
    }
    internal static ProcessedEpisode BuildReport(StoredEpisodeInput input, ProcessedEpisode processed, RuntimeFinalFacilities final)
    {
        var journal = new TelemetryDecisionJournal(processed.Decisions); var records = processed.ReportRecords;
        var previous = processed.Decisions?.Entries;
        if (final.Decisions != null)
            foreach (var entry in final.Decisions.Entries.Where(entry => entry.Component.StartsWith("runway.", StringComparison.Ordinal)))
            {
                if (previous?.Any(old => old.TimeSeconds.Equals(entry.TimeSeconds) && old.Component == entry.Component && old.Code == entry.Code &&
                    old.Reason == entry.Reason && old.Details.Count == entry.Details.Count &&
                    old.Details.All(pair => entry.Details.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value))) == true) continue;
                journal.Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, entry.Details);
            }
        var site = final.Snapshot; var time = input.Episode.Samples.Count == 0 ? 0 : input.Episode.Samples.Last().SimulationTimeSeconds;
        var candidates = site.FinalRunways.Count + site.PreliminaryRunways.Count;
        journal.Record(time, "runway", input.Episode.IsDiagnosticOnly ? "lookup_not_required" : candidates > 0 ? "facilities_received" : "facilities_unavailable",
            input.Episode.IsDiagnosticOnly ? "Diagnostic capture requires no landing or runway lookup." :
            "Finalized using only this episode's durably accepted preliminary and final scenery.", new Dictionary<string, double> { ["candidates"] = candidates });
        foreach (var record in records)
        {
            var runway = new TelemetryDecisionJournal(); LandingSiteResolver.Apply(record, site, runway);
            foreach (var entry in runway.Snapshot().Entries)
            {
                var details = entry.Details.ToDictionary(pair => pair.Key, pair => pair.Value);
                details["contact_number"] = record.ContactNumber; details["relative_contact_time"] = 1;
                journal.Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, details);
            }
        }
        LandingReplayDiagnostics.RecordAll(records, journal);
        var result = processed.ReplayLike ? ProcessedEpisode.Replay(journal.Snapshot()) : ProcessedEpisode.Completed(
            AirportMetadata.Merge(AirportMetadata.Merge(processed.AirportFacilities, site.PreliminaryAirports), site.FinalAirports), records, journal.Snapshot());
        return result;
    }
}
