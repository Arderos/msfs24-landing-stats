using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class EpisodeProcessor
{
    private readonly FlightModelGeometryResolver _flightModelGeometryResolver;
    private readonly LandingRepository _repository;
    private readonly object _landingRepositoryGate;
    public EpisodeProcessor(FlightModelGeometryResolver resolver, LandingRepository repository, object repositoryGate)
    { _flightModelGeometryResolver = resolver; _repository = repository; _landingRepositoryGate = repositoryGate; }

    internal async Task<EpisodeGeometryData> ResolveGeometryAsync(StoredEpisodeInput input, EpisodeWorkManifest manifest)
    {
        var journal = new TelemetryDecisionJournal(); var snapshots = new List<AnalysisGeometrySnapshot?>();
        if (!input.Episode.IsDiagnosticOnly)
            foreach (var rawSegment in TelemetryKinematicDiscontinuity.SplitPhysicalSegments(input.Episode.Samples))
            {
                var segment = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(rawSegment));
                if (segment.Count == 0 || ReplayTelemetryDetector.IsReplayLike(segment)) { snapshots.Add(null); continue; }
                var resolution = await _flightModelGeometryResolver.ResolveAnalysisAsync(input.Episode.AircraftTitle, input.Episode.AircraftType,
                    input.Episode.AircraftModel, segment, journal).ConfigureAwait(false);
                snapshots.Add(AnalysisGeometrySnapshot.Capture(segment, resolution.Options ?? new TouchdownAnalysisOptions(), resolution.Outcome));
            }
        return new EpisodeGeometryData { Id = manifest.Id, Engine = manifest.Engine, InputHash = manifest.InputHash,
            Segments = snapshots.ToArray(), Decisions = DecisionPersistenceData.Capture(journal.Snapshot()) };
    }

    internal async Task<ProcessedEpisode> ProcessAsync(
        LandingEpisodeEventArgs eventArgs,
        IReadOnlyList<AirportFacility> knownFacilities,
        DateTime episodeTimestampUtc,
        bool persistHistory = true, EpisodeGeometryData? geometryInputs = null)
    {
        // Keep the capture's raw frames untouched for diagnostics. Every consumer
        // below (geometry, contact/G analysis, controls and 3D storage) must use
        // the same active-time sequence with confirmed SDK pauses removed.
        var prepared = TelemetryAnalysisFrames.Prepare(eventArgs.Samples);
        var samples = TelemetryDeduplicator.Deduplicate(prepared);
        var journal = eventArgs.DecisionJournal ?? new TelemetryDecisionJournal();
        if (geometryInputs != null)
            foreach (var entry in geometryInputs.Decisions.Restore().Snapshot().Entries)
                journal.Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, entry.Details);
        var time = samples.Count == 0 ? 0 : samples[samples.Count - 1].SimulationTimeSeconds;
        journal.Record(time, "analysis", "frames_deduplicated", "Excluded confirmed SDK pauses from the analysis timeline, then kept the latest same-time frame without erasing unknown physics interruptions.",
            new Dictionary<string, double> { ["received"] = eventArgs.Samples.Count, ["analysis_frames"] = prepared.Count, ["retained"] = samples.Count });
        if (eventArgs.IsDiagnosticOnly || samples.Count == 0)
        {
            // Record the rejection without loading aircraft configuration or touching landing history.
            TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal });
            journal.Record(time, "analysis", "diagnostic_only", "Retained raw and decisions; no landing is saved.");
            return ProcessedEpisode.Completed(knownFacilities, Array.Empty<LandingRecord>(), journal.Snapshot());
        }
        // The episode carries a point-in-time copy of the recorder's facility
        // list. Use it for this analysis, but never write it back to the shared
        // cache: a newer facilities event may already have been persisted while
        // geometry analysis was running, and this stale snapshot must not win.
        var facilities = AirportMetadata.Merge(knownFacilities, eventArgs.AirportFacilities);

        var savedRecords = new List<LandingRecord>();
        var anyReplay = false;
        var anyAnalyzed = false;
        var segmentOrdinal = 0;
        foreach (var rawSegment in TelemetryKinematicDiscontinuity.SplitPhysicalSegments(eventArgs.Samples))
        {
            var segmentId = StableRuntimeIdentity.Segment(eventArgs.PersistentId,
                rawSegment.Count == 0 ? 0 : rawSegment[0].Sequence, segmentOrdinal++);
            var segment = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(rawSegment, journal));
            if (segment.Count == 0) continue;
            var replayLike = ReplayTelemetryDetector.IsReplayLike(segment);
            RecordSourceObservations(rawSegment, journal, replayLike);
            if (replayLike)
            {
                journal.Record(TelemetryAnalysisFrames.TimeOf(segment[0]), "analysis", "replay_rejected",
                    "Kinematic consistency check rejected this physical segment as replay-like; other segments remain independent.");
                anyReplay = true;
                continue;
            }
            anyAnalyzed = true;
            // Datum recovery from telemetry is also segment-local: not just the
            // contact detector, but the geometry options themselves must never
            // be calibrated using another flight's positions or rotations.
            AnalysisGeometrySnapshot geometrySnapshot; TouchdownAnalysisOptions analysisOptions;
            if (geometryInputs != null)
            {
                if (segmentOrdinal > geometryInputs.Segments.Length || geometryInputs.Segments[segmentOrdinal - 1] == null)
                    throw new System.IO.InvalidDataException("Frozen geometry is missing this physical segment.");
                geometrySnapshot = geometryInputs.Segments[segmentOrdinal - 1]!;
                analysisOptions = geometrySnapshot.Restore(segment);
            }
            else
            {
                var resolution = await _flightModelGeometryResolver.ResolveAnalysisAsync(
                    eventArgs.AircraftTitle, eventArgs.AircraftType, eventArgs.AircraftModel, segment, journal).ConfigureAwait(false);
                analysisOptions = resolution.Options ?? new TouchdownAnalysisOptions();
                geometrySnapshot = AnalysisGeometrySnapshot.Capture(segment, analysisOptions, resolution.Outcome);
            }
            analysisOptions.DecisionJournal = journal;
            var touchdowns = TouchdownAnalysis.Analyze(segment, analysisOptions);
            var segmentIdentity = segmentId.ToString("N");
            foreach (var touchdown in touchdowns)
            {
                var aircraftType = eventArgs.AircraftType == "unknown" ? eventArgs.AircraftModel : eventArgs.AircraftType;
                var record = LandingRecordFactory.Create(
                    touchdown, rawSegment, eventArgs.AircraftTitle, aircraftType,
                    contactCount: touchdowns.Count(contact => contact.EpisodeNumber == touchdown.EpisodeNumber),
                    timestampUtc: episodeTimestampUtc,
                    episodeEndSimulationTimeSeconds: TelemetryAnalysisFrames.TimeOf(segment[segment.Count - 1]));
                record.RecordingEpisodeId = segmentIdentity + ":" + touchdown.EpisodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                record.Id = StableRuntimeIdentity.Contact(segmentId, touchdown.Sequence, touchdown.ContactNumber).ToString("N");
                record.AnalysisGeometry = geometrySnapshot.Copy();
                record.Simulator = eventArgs.Simulator;
                record.ControlInputSources = eventArgs.ControlInputSources.ToList();
                AirportMetadata.TryResolve(record, facilities);
                if (persistHistory)
                    lock (_landingRepositoryGate)
                        if (!_repository.SaveRecovered(record)) continue;
                savedRecords.Add(record);
            }
        }

        if (anyReplay && !anyAnalyzed) return ProcessedEpisode.Replay(journal.Snapshot());

        journal.Record(time, "analysis", "episode_analyzed", "Finished analysis of the retained landing episode.",
            new Dictionary<string, double> { ["contacts"] = savedRecords.Count });
        var decisions = journal.Snapshot();
        return ProcessedEpisode.Completed(facilities, savedRecords, decisions);
    }

    internal static void RecordSourceObservations(IReadOnlyList<TelemetrySample> samples, TelemetryDecisionJournal journal, bool replayLike)
    {
        // Missing metadata in an old capture retains the previous processing path.
        if (!samples.Any(sample => sample.SourceEvidence != null)) return;
        var assessor = new ReplaySourceAssessor();
        var counts = new Dictionary<string, double>
        {
            ["frames"] = samples.Count, ["confirmed_sdk_replay"] = 0, ["externally_driven_pose"] = 0,
            ["suspicious_kinematics"] = 0, ["unknown"] = 0, ["stale_observation"] = 0, ["unavailable_observation"] = 0,
        };
        foreach (var sample in samples)
        {
            var assessment = assessor.Observe(sample.SourceEvidence, replayLike);
            var key = assessment.Kind == ReplaySourceKind.ConfirmedSdkReplay ? "confirmed_sdk_replay" :
                assessment.Kind == ReplaySourceKind.ExternallyDrivenPose ? "externally_driven_pose" :
                assessment.Kind == ReplaySourceKind.SuspiciousKinematics ? "suspicious_kinematics" : "unknown";
            counts[key]++;
            if (assessment.Reason == ReplaySourceAssessmentReason.ObservationStale) counts["stale_observation"]++;
            if (sample.SourceEvidence == null || sample.SourceEvidence.Observation.Availability != SourceObservationAvailability.Available)
                counts["unavailable_observation"]++;
        }
        journal.Record(TelemetryAnalysisFrames.TimeOf(samples[samples.Count - 1]), "analysis.source", "source_observations",
            "Observed USER source control and evidence availability. External pose control remains diagnostic until controlled live validation; existing landing acceptance is preserved.", counts);
    }
}
