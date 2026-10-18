using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

/// <summary>Owns preliminary/final episode evidence independently of the source query lifetime.</summary>
internal sealed partial class EpisodeFacilityCoordinator : IEpisodeFacilityPort
{
    private readonly ISceneryQueryPort _query;
    private SceneryQuerySnapshot? _restoredQuerySnapshot;
    private SceneryQuerySnapshot QuerySnapshot => _restoredQuerySnapshot ?? _query.Snapshot;
    public EpisodeFacilityCoordinator(ISceneryQueryPort query) { _query = query; }
    public long EpisodeId => _runwayEpisodeId;
    public bool CanQuery => _query.CanQuery;
    public TelemetryDecisionJournal? DecisionJournal => _runwayDecisionJournal;
    public double DecisionTime => _runwayDecisionTime;
    public void SetDecisionContext(TelemetryDecisionJournal? journal, double time)
    { _runwayDecisionJournal = journal; _runwayDecisionTime = time; }
    public Task<IReadOnlyList<RunwayFacility>>? RunwayTask => _runwaySnapshotCompletion?.Task;
    public Task<EpisodeFacilitySnapshot>? SnapshotTask => _episodeFacilityCompletion?.Task;
    public IReadOnlyList<AirportFacility> CachedAirports => _query.CachedAirports;
    internal bool IsPreliminary => _runwayLookupIsPreliminary;
    public void BeginEpisode(long id) => BeginRunwayEpisode(id);
    public void RequestPreliminary(TelemetrySample trigger, IReadOnlyList<TelemetrySample> samples) => RequestPreliminaryRunways(trigger, samples);
    public void RequestFinal(IReadOnlyList<TelemetrySample> samples) => RequestEpisodeRunways(samples);
    public void CompleteWithoutRefresh(string reason) => CompleteRunwayLookupWithoutRefresh(reason);
    public EpisodeFacilitySnapshot CurrentSnapshot() => CurrentEpisodeFacilitySnapshot();

    public void DiscardEpisode(long episodeId)
    {
        if (_runwayEpisodeId != episodeId) return;
        ResetRunwayLookupStage();
        _runwayEpisodeId = 0; _runwayDecisionJournal = null;
    }

    internal void ResetRunwayLookupStage()
    {
        _restoredQuerySnapshot = null;
        _query.Reset();
        _runwayStageCompleted = false;
    }

    internal void ResetConnection()
    {
        CompleteWithoutRefresh("Simulator connection closed; preserve only scenery gathered for this episode.");
        _runwayDecisionJournal = null;
        _runwayEpisodeId = 0;
        _preliminaryRunways = Array.Empty<RunwayFacility>();
        _preliminaryAirports = Array.Empty<AirportFacility>();
        _preliminaryRunwayCandidatesComplete = false;
        _approachSamples = Array.Empty<TelemetrySample>();
        _runwayLookupIsPreliminary = false;
    }

    private TelemetryDecisionJournal? _runwayDecisionJournal;
    private double _runwayDecisionTime;
    private long _runwayEpisodeId;
    private TaskCompletionSource<IReadOnlyList<RunwayFacility>>? _runwaySnapshotCompletion;
    private TaskCompletionSource<EpisodeFacilitySnapshot>? _episodeFacilityCompletion;
    private IReadOnlyList<RunwayFacility> _preliminaryRunways = Array.Empty<RunwayFacility>();
    private IReadOnlyList<AirportFacility> _preliminaryAirports = Array.Empty<AirportFacility>();
    private IReadOnlyList<TelemetrySample> _approachSamples = Array.Empty<TelemetrySample>();
    private bool _preliminaryRunwayCandidatesComplete;
    private bool _runwayLookupIsPreliminary, _runwayStageCompleted;

    internal void BeginRunwayEpisode(long episodeId)
    {
        CompleteRunwayLookupWithoutRefresh("A new episode superseded this episode's pending scenery lookup.");
        _runwaySnapshotCompletion = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _episodeFacilityCompletion = new TaskCompletionSource<EpisodeFacilitySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _preliminaryRunways = Array.Empty<RunwayFacility>();
        _preliminaryAirports = Array.Empty<AirportFacility>();
        _preliminaryRunwayCandidatesComplete = false;
        _approachSamples = Array.Empty<TelemetrySample>();
        _runwayLookupIsPreliminary = false;
        ResetRunwayLookupStage();
        _runwayDecisionJournal = null;
        _runwayEpisodeId = episodeId;
    }

    internal void RequestPreliminaryRunways(TelemetrySample trigger, IReadOnlyList<TelemetrySample> samples)
    {
        _runwayLookupIsPreliminary = true;
        // The pre-roll at the approach gate represents intent, not the later rollout.
        var prepared = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(samples));
        var approach = new List<TelemetrySample>();
        var end = prepared.Count - 1;
        // A contact-triggered late start may end with the contact itself.
        while (end >= 0 && prepared[end].OnGround) end--;
        for (var index = end; index >= 0; index--)
        {
            var sample = prepared[index];
            if (!sample.MotionSimulation || sample.OnGround || sample.SimulatorState == TelemetrySimulatorState.Inactive) break;
            approach.Add(sample.CopyForAnalysis());
        }
        approach.Reverse();
        _approachSamples = approach.ToArray();
        _query.Request(trigger.LatitudeDegrees, trigger.LongitudeDegrees);
    }

    internal void RequestEpisodeRunways(IReadOnlyList<TelemetrySample> samples)
    {
        PreservePreliminaryFacilities();
        _runwayLookupIsPreliminary = false;
        ResetRunwayLookupStage();
        // The episode may contain several bounces, all near the same landing site.
        TelemetrySample? contact = null;
        var continuity = new TelemetryContactContinuity();
        foreach (var sample in TelemetryAnalysisFrames.Prepare(samples))
            if (continuity.Observe(sample)) contact = sample;
        if (contact == null)
        {
            CompleteRunwayLookupWithoutRefresh("No physical contact coordinate is available for the final scenery lookup.");
            return;
        }
        _query.Request(contact.LatitudeDegrees, contact.LongitudeDegrees);
    }

    internal void CompleteRunwayLookupWithoutRefresh(string reason)
    {
        // Main-menu/load/end events may leave SimConnect connected, unlike Quit.
        // Resolve this episode's task now; a later flight's scenery cannot fill it.
        var pending = (_episodeFacilityCompletion != null && !_episodeFacilityCompletion.Task.IsCompleted) ||
            (_runwaySnapshotCompletion != null && !_runwaySnapshotCompletion.Task.IsCompleted);
        PreservePreliminaryFacilities();
        var progress = QuerySnapshot;
        var retainedFinal = _runwayLookupIsPreliminary ? Array.Empty<RunwayFacility>() : progress.Runways;
        var finalCandidatesComplete = !_runwayLookupIsPreliminary && progress.CandidatesComplete;
        _query.Cancel(reason);
        _runwayStageCompleted = true;
        if (!pending) return;
        _runwayDecisionJournal?.Record(_runwayDecisionTime, "runway.airports", "fresh_list_unavailable", reason);
        // A completed final stage is immutable. Cancellation only settles a still
        // pending transaction and cannot replace its final result with fallback.
        CompleteEpisodeFacilitySnapshot(retainedFinal,
            _runwayLookupIsPreliminary ? Array.Empty<AirportFacility>() : progress.Airports,
            !_runwayLookupIsPreliminary && progress.FreshAirportsReceived, finalCandidatesComplete);
    }

    internal void PreservePreliminaryFacilities()
    {
        if (!_runwayLookupIsPreliminary || _runwayStageCompleted) return;
        var progress = QuerySnapshot;
        _preliminaryAirports = progress.Airports;
        // Each cached airport has a validated complete base reply. Missing
        // neighbors affect uniqueness, not the integrity of successful replies.
        _preliminaryRunways = progress.Runways;
        _preliminaryRunwayCandidatesComplete = progress.CandidatesComplete;
    }

    internal void FinishRunwayLookupStage(IReadOnlyList<RunwayFacility> facilities)
    {
        if (_runwayStageCompleted) return;
        _restoredQuerySnapshot = null; // The current query owner delivered a new, context-checked response.
        _runwayStageCompleted = true;
        var snapshot = facilities.Select(value => value.Snapshot()).ToArray();
        var progress = QuerySnapshot;
        var candidatesComplete = progress.CandidatesComplete;
        _runwayDecisionJournal?.Record(_runwayDecisionTime, "runway.episode", _runwayLookupIsPreliminary ? "preliminary_completed" : "final_completed",
            "Keep fresh airport identities independently of whether a runway geometry batch could be completed.",
            new Dictionary<string, double> { ["airports"] = progress.Airports.Count, ["runways"] = snapshot.Length,
                ["fresh_airports_received"] = progress.FreshAirportsReceived ? 1 : 0,
                ["base_candidates_complete"] = candidatesComplete ? 1 : 0,
                ["requested_airports"] = progress.RequestedAirports, ["completed_airports"] = progress.CompletedAirports });
        if (_runwayLookupIsPreliminary)
        {
            _preliminaryRunways = snapshot;
            _preliminaryAirports = progress.Airports;
            _preliminaryRunwayCandidatesComplete = candidatesComplete;
            return;
        }
        CompleteEpisodeFacilitySnapshot(snapshot, progress.Airports, progress.FreshAirportsReceived, candidatesComplete);
    }

    internal void FailCurrentLookupStage(string reason)
    {
        if (_runwayStageCompleted) return;
        var progress = QuerySnapshot;
        _query.Cancel(reason);
        _runwayDecisionJournal?.Record(_runwayDecisionTime, "runway.episode", "lookup_stage_interrupted", reason);
        // Preliminary failure ends only that query. The post-contact final
        // lookup still gets its own opportunity to obtain fresh scenery.
        FinishRunwayLookupStage(progress.Runways);
    }

    internal void CompleteEpisodeFacilitySnapshot(IReadOnlyList<RunwayFacility> finalRunways,
        IReadOnlyList<AirportFacility> finalAirports, bool finalLookupCompleted, bool finalCandidatesComplete)
    {
        var snapshot = new EpisodeFacilitySnapshot(_runwayEpisodeId, _preliminaryRunways, _preliminaryAirports,
            _approachSamples, finalRunways, finalAirports, finalLookupCompleted,
            _preliminaryRunwayCandidatesComplete, finalCandidatesComplete);
        _runwayDecisionJournal?.Record(_runwayDecisionTime, "runway.episode", "snapshot_completed",
            "Freeze retained per-airport geometry and independent candidate completeness for this episode.",
            new Dictionary<string, double> { ["preliminary_runways"] = _preliminaryRunways.Count,
                ["final_runways"] = finalRunways.Count, ["preliminary_airports"] = _preliminaryAirports.Count,
                ["final_airports"] = finalAirports.Count,
                ["preliminary_candidates_complete"] = _preliminaryRunwayCandidatesComplete ? 1 : 0,
                ["final_candidates_complete"] = finalCandidatesComplete ? 1 : 0 });
        _episodeFacilityCompletion?.TrySetResult(snapshot);
        _runwaySnapshotCompletion?.TrySetResult(finalCandidatesComplete && finalRunways.Count > 0 ? finalRunways :
            _preliminaryRunwayCandidatesComplete ? _preliminaryRunways : Array.Empty<RunwayFacility>());
    }

    internal EpisodeFacilitySnapshot CurrentEpisodeFacilitySnapshot()
    {
        if (_episodeFacilityCompletion?.Task.Status == TaskStatus.RanToCompletion)
            return _episodeFacilityCompletion.Task.Result;
        PreservePreliminaryFacilities();
        var progress = QuerySnapshot;
        return new EpisodeFacilitySnapshot(_runwayEpisodeId, _preliminaryRunways, _preliminaryAirports,
            _approachSamples, _runwayLookupIsPreliminary ? Array.Empty<RunwayFacility>() : progress.Runways,
            _runwayLookupIsPreliminary ? Array.Empty<AirportFacility>() : progress.Airports,
            !_runwayLookupIsPreliminary && progress.FreshAirportsReceived,
            _preliminaryRunwayCandidatesComplete, !_runwayLookupIsPreliminary && progress.CandidatesComplete);
    }
}
