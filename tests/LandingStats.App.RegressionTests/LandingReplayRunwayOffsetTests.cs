using System;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class LandingReplayRunwayOffsetTests
{
    public static void RunAll()
    {
        foreach (var heading in new[] { 0d, 90d, 180d, 270d, 75.1, 240 })
        {
            var record = Record(heading, 12);
            Near(Offset(record, heading), 12, .1, "Primary direction right must follow the runway, not world east.");
            record.Runway = "secondary";
            Near(Offset(record, (heading + 180) % 360), -12, .1, "Reciprocal direction must reverse left/right.");
            record.Runway = " primary ";
            Near(Offset(record), 12, .1, "Selected runway end names are case/whitespace tolerant.");
            record = Record(heading, -8);
            Near(Offset(record), -8, .1, "Primary direction left must be negative.");
            record = Record(heading, 0);
            Near(Offset(record), 0, .01, "On-centreline must read zero.");
        }

        var inferred = Record(90, 10);
        inferred.Runway = "—";
        foreach (var sample in inferred.Series) sample.HeadingTrueDegrees = 270;
        Near(Offset(inferred), 10, .1, "Ground motion, not the nose heading, must resolve a missing end name.");
        var reverse = Record(90, 10, reverse: true);
        reverse.Runway = "—";
        Near(Offset(reverse), -10, .1, "Reciprocal approach must work without a stored end name.");

        var changing = Record(90, 15);
        Require(LandingReplayTrack.TryCreate(changing, out var track), "Fixture track.");
        Require(LandingReplayRunwayOffset.TryCreate(changing, track!, out var helper), "Fixture helper.");
        Require(track!.TrySample(0, out var pose), "Fixture pose.");
        Require(helper!.TryGetMeters(pose, out var before), "Fixture offset.");
        changing.Series[changing.Series.Count - 1].HeadingTrueDegrees = 270;
        changing.Series[changing.Series.Count - 1].PlaneAltitudeFeet += 1000;
        Require(LandingReplayTrack.TryCreate(changing, out track) && track!.TrySample(0, out pose), "Changed attitude fixture.");
        Require(helper!.TryGetMeters(pose, out var after), "Changed attitude offset.");
        Near(after, before, .000001, "Altitude and aircraft rotation cannot change lateral distance.");

        var water = Record(90, 10); water.RunwayGeometry!.SurfaceType = 2; Unavailable(water, "Water runway is not a paved centreline.");
        var missing = Record(90, 10); missing.RunwayGeometry = null; Unavailable(missing, "No invented track-as-runway fallback.");
        var invalid = Record(90, 10); invalid.RunwayGeometry!.WidthMeters = double.NaN; Unavailable(invalid, "Invalid geometry unavailable.");
        var ambiguous = Record(90, 10); ambiguous.RunwayGeometry!.SecondaryName = "primary"; Unavailable(ambiguous, "Ambiguous end name unavailable.");
        var stationary = Record(90, 10); stationary.Runway = "—";
        foreach (var point in stationary.Series) point.LongitudeDegrees = 0;
        Unavailable(stationary, "Stationary motion cannot select an unknown runway direction.");
        var gap = Record(90, 10); gap.Runway = "—"; gap.Series.RemoveRange(1, 3);
        Unavailable(gap, "No direction inference across missing telemetry.");
        var cross = Record(90, 10); cross.Runway = "—"; cross.RunwayGeometry!.HeadingTrueDegrees = 0;
        Unavailable(cross, "Crossing a runway cannot establish its landing direction.");
    }

    private static double Offset(LandingRecord record, double? expectedHeading = null)
    {
        Require(LandingReplayTrack.TryCreate(record, out var track), "Valid fixture track.");
        Require(LandingReplayRunwayOffset.TryCreate(record, track!, out var offset), "Valid runway direction.");
        Require(track!.TrySample(0, out var pose), "Valid pose.");
        Require(offset!.TryGetMeters(pose, out var result), "Finite offset.");
        if (expectedHeading.HasValue) Near(offset.HeadingTrueDegrees, expectedHeading.Value, .000001, "Yaw reference uses the selected landing direction.");
        return result;
    }

    private static void Unavailable(LandingRecord record, string message)
    {
        Require(LandingReplayTrack.TryCreate(record, out var track), "Unavailable fixture track.");
        Require(!LandingReplayRunwayOffset.TryCreate(record, track!, out _), message);
    }

    private static LandingRecord Record(double heading, double lateral, bool reverse = false)
    {
        var record = new LandingRecord { Runway = "primary", RunwayGeometry = new RunwayFacility
        {
            AirportIdent = "TEST", HeadingTrueDegrees = heading, LatitudeDegrees = 0, LongitudeDegrees = 0,
            LengthMeters = 3000, WidthMeters = 45, AltitudeMeters = 100, PrimaryName = "primary", SecondaryName = "secondary",
        }};
        var angle = heading * Math.PI / 180;
        for (var i = 0; i <= 6; i++)
        {
            var time = -1 + i * .25;
            var along = time * 60 * (reverse ? -1 : 1);
            var east = Math.Sin(angle) * along + Math.Cos(angle) * lateral;
            var north = Math.Cos(angle) * along - Math.Sin(angle) * lateral;
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = time, LatitudeDegrees = north / 110574.2758, LongitudeDegrees = east / 111319.4908,
                PlaneAltitudeFeet = 350, GroundAltitudeFeet = 328, HeadingTrueDegrees = heading,
            });
        }
        return record;
    }

    private static void Near(double actual, double expected, double tolerance, string message) =>
        Require(Math.Abs(actual - expected) <= tolerance, message + $" Expected {expected}, got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
