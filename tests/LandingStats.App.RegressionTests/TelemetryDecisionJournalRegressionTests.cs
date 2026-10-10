using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class TelemetryDecisionJournalRegressionTests
{
    public static void RunAll()
    {
        BoundedImmutableAndFinite();
        ConcurrentWritersRemainBounded();
        DiagnosticBudgetPreservesTruncationWhenSeeded();
        AnalysisLogsActualBranchesWithoutChangingResults();
        RejectedReconstructionAndAbsentContactsAreExplained();
        RecorderJournalFollowsCaptureAndAggregatesPausedDeliveries();
        RecorderExplainsLateStartAndClockReset();
    }

    private static void BoundedImmutableAndFinite()
    {
        var values = new Dictionary<string, double> { ["valid"] = 3, ["invalid"] = double.NaN };
        var journal = new TelemetryDecisionJournal();
        journal.Record(double.NaN, "component", "code", "reason", values);
        var snapshot = journal.Snapshot();
        values["valid"] = 7;
        journal.Record(1, "later", "later", "later");
        Assert(snapshot.Entries.Count == 1 && snapshot.Entries[0].Details["valid"] == 3,
            "snapshots or details changed after their capture");
        Assert(snapshot.Entries[0].TimeSeconds == 0 && !snapshot.Entries[0].Details.ContainsKey("invalid"),
            "journal retained nonfinite JSON numbers");
        var guarded = (IDictionary<string, double>)snapshot.Entries[0].Details;
        var rejected = false;
        try { guarded["valid"] = 99; }
        catch (NotSupportedException) { rejected = true; }
        Assert(rejected, "snapshot details are mutable");
    }

    private static void ConcurrentWritersRemainBounded()
    {
        var journal = new TelemetryDecisionJournal();
        Parallel.For(0, TelemetryDecisionJournal.MaximumEntries + 100, i => journal.Record(i, "c", "d", "r"));
        var snapshot = journal.Snapshot();
        Assert(snapshot.Entries.Count == TelemetryDecisionJournal.MaximumEntries && snapshot.DroppedEntryCount == 100,
            "concurrent writers exceeded the capture limit or lost the dropped count");
        Assert(snapshot.Truncated, "full journal does not identify truncation");
    }

    private static void DiagnosticBudgetPreservesTruncationWhenSeeded()
    {
        var details = Enumerable.Range(0, 24).ToDictionary(i => i.ToString() + new string('\u0001', 100), i => (double)i);
        var journal = new TelemetryDecisionJournal();
        for (var i = 0; i < 2048; i++)
            journal.Record(i, new string('\u0002', 200), new string('\u0003', 200), new string('\u0004', 2000), details);
        var snapshot = journal.Snapshot();
        Assert(snapshot.Truncated && snapshot.Entries.Count < 2048, "worst-case escaped data did not hit the size budget");
        var estimatedBytes = snapshot.Entries.Sum(entry => 256 +
            6 * (entry.Component.Length + entry.Code.Length + entry.Reason.Length) +
            entry.Details.Sum(item => 48 + 6 * item.Key.Length));
        Assert(estimatedBytes <= 900000, "journal escaped JSON bound exceeds its archive budget");
        var copied = new TelemetryDecisionJournal(snapshot).Snapshot();
        Assert(copied.Entries.Count == snapshot.Entries.Count && copied.DroppedEntryCount == snapshot.DroppedEntryCount,
            "seeding a journal lost earlier decisions or truncation provenance");
    }

    private static void AnalysisLogsActualBranchesWithoutChangingResults()
    {
        var samples = Samples();
        var options = Options();
        var baseline = TouchdownAnalysis.Analyze(samples, options).Single();
        var journal = new TelemetryDecisionJournal();
        options.DecisionJournal = journal;
        var diagnostic = TouchdownAnalysis.Analyze(samples, options).Single();
        Assert(baseline.ClosureReconstructionAvailable && diagnostic.ClosureReconstructionAvailable,
            "synthetic configured landing did not reconstruct");
        Assert(baseline.InertialVerticalFpm.Equals(diagnostic.InertialVerticalFpm) &&
               baseline.EstimatedContactTimeSeconds.Equals(diagnostic.EstimatedContactTimeSeconds) &&
               baseline.ReconstructedClosureFpm.Equals(diagnostic.ReconstructedClosureFpm) &&
               baseline.LatchedNormalFpm.Equals(diagnostic.LatchedNormalFpm) &&
               baseline.Impact.PeakG.Equals(diagnostic.Impact.PeakG) && baseline.Impact.Status == diagnostic.Impact.Status,
            "enabling diagnostics changed calculated results");
        var entries = journal.Snapshot().Entries;
        foreach (var code in new[] { "air_to_ground", "configured_topology_accepted", "arm_selected",
                     "compression_interpolation", "precontact_linear_fit_extrapolated", "fresh_update", "quadratic_fit_accepted" })
            Assert(entries.Any(entry => entry.Code == code), "missing actual decision: " + code);
        Assert(entries.Any(entry => entry.Component == "impact_window" && entry.Code == diagnostic.Impact.Status.ToString()),
            "impact-window result was not recorded");
        Assert(entries.Where(entry => entry.Component == "reconstruction")
            .All(entry => entry.Details["contact_number"] == 1 && entry.Details["episode_number"] == 1),
            "per-contact decisions are not tied to their contact");
    }

    private static void RejectedReconstructionAndAbsentContactsAreExplained()
    {
        var journal = new TelemetryDecisionJournal();
        var samples = Samples();
        foreach (var sample in samples)
        {
            Array.Clear(sample.ContactPointOnGround, 0, sample.ContactPointOnGround.Length);
            Array.Clear(sample.ContactPointCompression, 0, sample.ContactPointCompression.Length);
            sample.TouchdownNormalVelocityFps = 4;
        }
        var options = Options();
        options.DecisionJournal = journal;
        var result = TouchdownAnalysis.Analyze(samples, options).Single();
        Assert(!result.ClosureReconstructionAvailable && !result.LatchUpdateDetected, "invalid fixture failure assumptions");
        foreach (var code in new[] { "configured_topology_rejected", "last_airborne_sample", "no_compression_time_estimate",
                     "unsupported_gear_topology", "no_fresh_update" })
            Assert(journal.Snapshot().Entries.Any(entry => entry.Code == code), "missing fallback explanation: " + code);

        journal = new TelemetryDecisionJournal();
        options.DecisionJournal = journal;
        Assert(TouchdownAnalysis.Analyze(Array.Empty<TelemetrySample>(), options).Count == 0, "empty capture created a contact");
        Assert(journal.Snapshot().Entries.Any(entry => entry.Code == "no_air_to_ground_transition"), "no-contact result is unexplained");

        var sparse = Samples().Where(sample => sample.OnGround || sample.SimulationTimeSeconds >= .96).ToArray();
        Assert(!TouchdownClosureReconstruction.TryEstimate(sparse, 1, .99, -8, out _, out var reason) &&
               reason == "insufficient_distinct_precontact_samples", "quadratic rejection did not identify insufficient samples");
    }

    private static void RecorderJournalFollowsCaptureAndAggregatesPausedDeliveries()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        LandingEpisodeEventArgs? completed = null;
        recorder.EpisodeCompleted += (_, args) => completed = args;
        Feed(recorder, Approach(1));
        for (var i = 0; i < 100; i++)
        {
            var paused = Approach(1);
            paused.MotionSimulation = false;
            Feed(recorder, paused);
        }
        Feed(recorder, Approach(1.5));
        var contact = Approach(2);
        contact.OnGround = true;
        Feed(recorder, contact);
        var rollout = Approach(17);
        rollout.OnGround = true;
        Feed(recorder, rollout);
        Assert(completed?.DecisionJournal != null, "completed capture has no decision journal");
        var journal = completed!.DecisionJournal!;
        var entries = journal.Snapshot().Entries;
        Assert(entries.Any(entry => entry.Code == "capture_started_at_approach_gate") &&
               entries.Any(entry => entry.Code == "contact_flag_transition") &&
               entries.Any(entry => entry.Code == "post_contact_capture_complete"), "recorder lifecycle decisions are absent");
        var end = entries.Single(entry => entry.Code == "capture_completed");
        Assert(end.Details["same_time_frames_coalesced"] == 99 &&
               end.Details["non_motion_frames_observed_after_start"] == 100,
            "paused deliveries were not accurately aggregated");
        Assert(completed.Samples.Count == 5 && entries.Count < 10, "diagnostics lost physics boundaries or spammed one event per paused frame");
        Assert(RecorderJournal(recorder) == null, "completed journal is still attached to recorder");
        Feed(recorder, Approach(18));
        Assert(RecorderJournal(recorder) != null && !ReferenceEquals(RecorderJournal(recorder), journal),
            "next capture reused the previous journal");
        Feed(recorder, Approach(319));
        Assert(RecorderJournal(recorder) == null, "discarded approach retained its journal");
        Assert(journal.Snapshot().Entries.Count == entries.Count, "next capture changed the completed journal");
    }

    private static void RecorderExplainsLateStartAndClockReset()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        LandingEpisodeEventArgs? completed = null;
        recorder.EpisodeCompleted += (_, args) => completed = args;
        var high = Approach(10);
        high.AboveGroundLevelFeet = 800;
        Feed(recorder, high);
        var contact = Approach(11);
        contact.OnGround = true;
        Feed(recorder, contact);
        Feed(recorder, Approach(1));
        var entries = completed?.DecisionJournal?.Snapshot().Entries;
        Assert(entries != null && entries.Any(entry => entry.Code == "capture_started_at_contact") &&
               entries.Any(entry => entry.Code == "simulation_time_reversed"), "late capture or clock reset explanation missing");
    }

    private static TelemetrySample Approach(double time) => new TelemetrySample
    {
        SimulationTimeSeconds = time, HostElapsedSeconds = time, MotionSimulation = true,
        AboveGroundLevelFeet = 400, VelocityWorldYFps = -4, GForce = 1
    };

    private static void Feed(SimConnectLandingRecorder recorder, TelemetrySample sample) =>
        typeof(SimConnectLandingRecorder).GetMethod("ProcessSample", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(recorder, new object[] { sample });

    private static TelemetryDecisionJournal? RecorderJournal(SimConnectLandingRecorder recorder) =>
        (TelemetryDecisionJournal?)typeof(SimConnectLandingRecorder)
            .GetField("_episodeDecisionJournal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder);

    private static TouchdownAnalysisOptions Options() => new TouchdownAnalysisOptions
    {
        LongitudinalMainGearArmFeet = -8,
        LongitudinalMainGearArmSource = TouchdownGeometrySource.FlightModelConfig,
        RecoverLongitudinalMainGearArmFromTelemetry = false,
        MainGearContactPoints = new[] { new TouchdownMainGearContactPoint(1, -8), new TouchdownMainGearContactPoint(2, -8) },
        NoseGearContactPointIndices = new[] { 0 }
    };

    private static List<TelemetrySample> Samples()
    {
        var samples = new List<TelemetrySample>();
        for (var i = 0; i <= 100; i++)
        {
            var t = .5 + i * .02;
            var ground = i >= 25;
            var sample = new TelemetrySample
            {
                Sequence = i, SimulationTimeSeconds = t, HostElapsedSeconds = t, MotionSimulation = true,
                OnGround = ground, GForce = 1, VelocityWorldYFps = ground ? 0 : -4,
                VerticalSpeedFps = -4, TouchdownNormalVelocityFps = ground ? 4 : 0,
                PlaneAltitudeFeet = 105 - 4 * t, GroundAltitudeFeet = 100,
                AboveGroundLevelFeet = 5 - 4 * t, PitchDegrees = 3
            };
            if (ground)
            {
                sample.ContactPointOnGround[1] = sample.ContactPointOnGround[2] = true;
                sample.ContactPointCompression[1] = sample.ContactPointCompression[2] = Math.Min(.5, .1 + (i - 25) * .2);
            }
            if (t >= 1.8) { sample.ContactPointOnGround[0] = true; sample.ContactPointCompression[0] = .5; }
            samples.Add(sample);
        }
        return samples;
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
