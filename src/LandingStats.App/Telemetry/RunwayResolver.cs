using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;

namespace LandingStats.App.Telemetry;

internal static class RunwayResolver
{
    // This is a conservative visual association, not a runway-incursion detector.
    public static RunwayFacility? Select(LandingRecord record, IEnumerable<RunwayFacility> runways)
    {
        if (!TryTrackHeading(record, out var trackHeading) ||
            !record.TouchdownLatitudeDegrees.HasValue || !record.TouchdownLongitudeDegrees.HasValue)
            return null;
        var latitude = record.TouchdownLatitudeDegrees.Value;
        var longitude = record.TouchdownLongitudeDegrees.Value;
        if (!ValidPosition(latitude, longitude)) return null;

        RunwayFacility? match = null;
        foreach (var runway in runways.Where(value => value != null && value.HasValidGeometry && !value.IsWater))
        {
            var headingDifference = Math.Abs(NormalizeAngle(trackHeading - runway.HeadingTrueDegrees));
            if (Math.Min(headingDifference, 180 - headingDifference) > 20) continue;
            OffsetMeters(runway.LatitudeDegrees, runway.LongitudeDegrees, latitude, longitude, out var east, out var north);
            var heading = runway.HeadingTrueDegrees * Math.PI / 180;
            var along = east * Math.Sin(heading) + north * Math.Cos(heading);
            var across = east * Math.Cos(heading) - north * Math.Sin(heading);
            if (Math.Abs(along) > runway.LengthMeters / 2 + 30 || Math.Abs(across) > runway.WidthMeters / 2 + 10) continue;
            // Parallel or crossing runways can genuinely be ambiguous. Do not pick one by ARP distance.
            if (match != null) return null;
            match = runway;
        }
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
