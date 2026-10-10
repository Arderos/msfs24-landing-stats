using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

internal static class RunwayResolver
{
    internal static bool TryApply(LandingRecord record, IReadOnlyList<RunwayFacility> facilities, TelemetryDecisionJournal? journal = null)
    {
        if (record.RunwayGeometry != null || record.IsSummaryOnly)
        {
            journal?.Record(0, "runway", "resolution_skipped", record.IsSummaryOnly ? "Record contains summary only." : "Runway already assigned.");
            return false;
        }
        var runway = Select(record, facilities, journal);
        if (runway == null) return false;
        record.RunwayGeometry = runway;
        record.Runway = NameForDirection(record, runway);
        if (!string.Equals(record.Airport, runway.AirportIdent, StringComparison.OrdinalIgnoreCase))
            record.AirportDistanceNauticalMiles = null;
        record.Airport = runway.AirportIdent;
        journal?.Record(0, "runway", "runway_selected", runway.AirportIdent + " / " + record.Runway,
            new Dictionary<string, double> { ["length_m"] = runway.LengthMeters, ["width_m"] = runway.WidthMeters,
                ["heading_true_deg"] = runway.HeadingTrueDegrees, ["contact_number"] = record.ContactNumber });
        return true;
    }

    // This is a conservative visual association, not a runway-incursion detector.
    public static RunwayFacility? Select(LandingRecord record, IEnumerable<RunwayFacility> runways, TelemetryDecisionJournal? journal = null)
    {
        if (!TryTrackHeading(record, out var trackHeading) ||
            !record.TouchdownLatitudeDegrees.HasValue || !record.TouchdownLongitudeDegrees.HasValue)
        {
            journal?.Record(0, "runway", "track_heading_unavailable", "No reliable recorded track heading or touchdown position.");
            return null;
        }
        var latitude = record.TouchdownLatitudeDegrees.Value;
        var longitude = record.TouchdownLongitudeDegrees.Value;
        if (!ValidPosition(latitude, longitude))
        {
            journal?.Record(0, "runway", "touchdown_position_invalid", "Touchdown coordinates are invalid.");
            return null;
        }

        RunwayFacility? match = null;
        foreach (var runway in runways)
        {
            if (runway == null || !runway.HasValidGeometry || runway.IsWater)
            {
                journal?.Record(0, "runway", "candidate_ineligible", "Missing/invalid geometry or water runway.");
                continue;
            }
            var headingDifference = Math.Abs(NormalizeAngle(trackHeading - runway.HeadingTrueDegrees));
            if (Math.Min(headingDifference, 180 - headingDifference) > 20)
            {
                journal?.Record(0, "runway", "candidate_heading_rejected", runway.AirportIdent + " / " + runway.PrimaryName,
                    new Dictionary<string, double> { ["axis_difference_deg"] = Math.Min(headingDifference, 180 - headingDifference) });
                continue;
            }
            OffsetMeters(runway.LatitudeDegrees, runway.LongitudeDegrees, latitude, longitude, out var east, out var north);
            var heading = runway.HeadingTrueDegrees * Math.PI / 180;
            var along = east * Math.Sin(heading) + north * Math.Cos(heading);
            var across = east * Math.Cos(heading) - north * Math.Sin(heading);
            var withinBounds = Math.Abs(along) <= runway.LengthMeters / 2 + 30 && Math.Abs(across) <= runway.WidthMeters / 2 + 10;
            journal?.Record(0, "runway", withinBounds ? "candidate_within_bounds" : "candidate_position_rejected",
                runway.AirportIdent + " / " + runway.PrimaryName,
                new Dictionary<string, double> { ["along_m"] = along, ["across_m"] = across });
            if (!withinBounds) continue;
            // Parallel or crossing runways can genuinely be ambiguous. Do not pick one by ARP distance.
            if (match != null)
            {
                journal?.Record(0, "runway", "ambiguous_runways", "Multiple candidates satisfy heading and bounds; no runway is selected.");
                return null;
            }
            match = runway;
        }
        if (match == null) journal?.Record(0, "runway", "no_matching_runway", "No facility candidate satisfies the recorded track and position.");
        return match;
    }

    public static string NameForDirection(LandingRecord record, RunwayFacility runway) =>
        TryTrackHeading(record, out var heading) && Math.Abs(NormalizeAngle(heading - runway.HeadingTrueDegrees)) > 90
            ? runway.SecondaryName : runway.PrimaryName;

    private static bool TryTrackHeading(LandingRecord record, out double heading)
    {
        heading = 0;
        var points = record.Series.Where(point => point.TimeSeconds >= -2 && point.TimeSeconds <= .5).ToArray();
        if (points.Length < 2) return false;
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            if (!point.LatitudeDegrees.HasValue || !point.LongitudeDegrees.HasValue ||
                !ValidPosition(point.LatitudeDegrees.Value, point.LongitudeDegrees.Value) ||
                (index > 0 && (point.TimeSeconds <= points[index - 1].TimeSeconds ||
                               point.TimeSeconds - points[index - 1].TimeSeconds > .5))) return false;
        }
        var first = points[0];
        var last = points[points.Length - 1];
        var elapsed = last.TimeSeconds - first.TimeSeconds;
        if (elapsed < .2) return false;
        OffsetMeters(first.LatitudeDegrees!.Value, first.LongitudeDegrees!.Value,
            last.LatitudeDegrees!.Value, last.LongitudeDegrees!.Value, out var east, out var north);
        var distance = Math.Sqrt(east * east + north * north);
        if (distance < 5 || distance / elapsed > 200) return false;
        heading = (Math.Atan2(east, north) * 180 / Math.PI + 360) % 360;
        return true;
    }

    internal static void OffsetMeters(double latitude, double longitude, double otherLatitude, double otherLongitude,
        out double east, out double north)
    {
        const double radiusMeters = 6371008.8;
        north = (otherLatitude - latitude) * Math.PI / 180 * radiusMeters;
        east = NormalizeAngle(otherLongitude - longitude) * Math.PI / 180 * radiusMeters *
               Math.Cos((latitude + otherLatitude) * Math.PI / 360);
    }

    private static bool ValidPosition(double latitude, double longitude) =>
        RunwayNumber.IsFinite(latitude) && Math.Abs(latitude) <= 90 &&
        RunwayNumber.IsFinite(longitude) && Math.Abs(longitude) <= 180;

    private static double NormalizeAngle(double degrees) => ((degrees + 180) % 360 + 360) % 360 - 180;
}
