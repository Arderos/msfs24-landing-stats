using System;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ReplayDecisionJournalTests
{
    internal static void RunAll()
    {
        var record = Fixture();
        var before = record.Series.Select(p => (p.TimeSeconds, p.PlaneAltitudeFeet, p.PitchDegrees)).ToArray();
        var original = ReplayControlInputs.Resolve(record);
        var journal = new TelemetryDecisionJournal();
        // No WPF window or 3D tab exists. Production finalization runs on this same worker path.
        Task.Run(() => LandingReplayDiagnostics.Record(record, new[] { record }, journal)).GetAwaiter().GetResult();
        var entries = journal.Snapshot().Entries;
        foreach (var code in new[] { "track_created", "axis_candidate", "axis_selected", "joint_assignment",
                     "named_end_selected", "observed_runway_profile", "papi_coplanar_fallback", "threshold_reconstructed",
                     "tdz_pattern", "layout_created", "visual_datum_registered", "contact_registered", "frame_decision_counts", "marker_selected" })
            Require(entries.Any(e => e.Code == code), "Missing replay decision " + code);
        Require(entries.All(e => e.Details["relative_contact_time"] == 1 && e.Details["contact_number"] == record.ContactNumber),
            "Replay times explicitly identify the relative-contact clock and selected contact");
        Require(!journal.Snapshot().Truncated, "Ordinary replay diagnostics fit journal bounds");
        var suspension = entries.Single(e => e.Code == "frame_decision_counts");
        Require(suspension.Details["airborne_frames"] + suspension.Details["grounded_frames"] == record.Series.Count,
            "Dynamic suspension decisions cover every recorded frame without per-frame journal spam");
        Require(before.SequenceEqual(record.Series.Select(p => (p.TimeSeconds, p.PlaneAltitudeFeet, p.PitchDegrees))), "Diagnostics never mutate recorded pose");
        var instrumented = ReplayControlInputs.Resolve(record, new TelemetryDecisionJournal());
        Require(original.Pitch?.SourceIndex == instrumented.Pitch?.SourceIndex && original.Roll?.SourceIndex == instrumented.Roll?.SourceIndex,
            "Instrumentation does not change selected physical axes");

        var baselinePaint = RunwayMarkingLayout.Build(record.RunwayGeometry!).Shapes;
        var recordedPaint = RunwayMarkingLayout.Build(record.RunwayGeometry!, new TelemetryDecisionJournal()).Shapes;
        Require(baselinePaint.Count == recordedPaint.Count && baselinePaint.Zip(recordedPaint, (a, b) => a.Kind == b.Kind && a.Points.SequenceEqual(b.Points)).All(equal => equal),
            "Journal does not change runway geometry");

        var legacy = new LandingRecord();
        legacy.Series.Add(new LandingSeriesPoint()); legacy.Series.Add(new LandingSeriesPoint { TimeSeconds = .1 });
        var missing = new TelemetryDecisionJournal();
        Task.Run(() => LandingReplayDiagnostics.Record(legacy, Array.Empty<LandingRecord>(), missing)).GetAwaiter().GetResult();
        Require(missing.Snapshot().Entries.Any(e => e.Code == "invalid_pose"), "Missing pose gets an explicit unavailable decision");

        record = Fixture(); record.Series.RemoveAll(p => p.TimeSeconds > -.9 && p.TimeSeconds < -.1);
        var gap = new TelemetryDecisionJournal();
        Require(LandingReplayTrack.TryCreate(record, out _, gap) && gap.Snapshot().Entries.Any(e => e.Code == "interpolation_gap"), "Motion gap decision retained");
        record.RunwayGeometry!.PrimaryIlsGlideSlope = new RunwayGlideSlope
        {
            Ident = "TEST", LatitudeDegrees = 42, LongitudeDegrees = 23, AltitudeMeters = 30.48, AngleDegrees = 3,
        };
        var ils = new TelemetryDecisionJournal();
        RunwayMarkingLayout.Build(record.RunwayGeometry, ils);
        Require(ils.Snapshot().Entries.Any(e => e.Code == "ils_selected") && !ils.Snapshot().Entries.Any(e => e.Code == "papi_coplanar_fallback"),
            "ILS selection is logged at the actual precedence branch");
    }

    private static LandingRecord Fixture()
    {
        var record = new LandingRecord { Runway = "36", FormatVersion = 8 };
        record.RawControllerAxesSourceIndices.AddRange(new[] { 0, 1 });
        record.ControlInputSources.Add("Test stick");
        record.RunwayGeometry = new RunwayFacility
        {
            AirportIdent = "EDDM", LatitudeDegrees = 42, LongitudeDegrees = 23, AltitudeMeters = 30.48,
            LengthMeters = 4000, WidthMeters = 45, HeadingTrueDegrees = 0, PrimaryName = "36", SecondaryName = "18",
            PrimaryPapiQueried = true, PrimaryPapi = new RunwayPapi { StationFromEndMeters = 400, AngleDegrees = 3 },
        };
        for (var i = 0; i <= 40; i++)
        {
            var time = (i - 20) * .1;
            var pitch = 25 * Math.Sin(i * .8); var roll = 35 * Math.Cos(i * 1.2);
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = time, LatitudeDegrees = 42 + time * .0003, LongitudeDegrees = 23,
                GroundAltitudeFeet = 100, PlaneAltitudeFeet = 100 + (3.2 + Math.Max(0, -time)) / .3048,
                HeadingTrueDegrees = 0, PitchDegrees = time < .2 ? -5 : 0, OnGround = time >= 0,
                PilotPitchPercent = pitch, PilotRollPercent = roll,
                RawControllerAxesPercent = new[] { pitch, roll }, RawControllerAxesValid = new[] { true, true },
                RawControllerAxesAgeSeconds = new[] { 0.0, 0.0 },
            });
        }
        return record;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
