using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;

namespace LandingStats.App.Runtime;

internal static class AirportMetadata
{
    public static bool TryResolve(
        LandingRecord record,
        IReadOnlyList<AirportFacility> facilities)
    {
        if (!string.Equals(record.Airport, "Unknown airport", StringComparison.OrdinalIgnoreCase) ||
            !record.HasTouchdownCoordinates)
        {
            return false;
        }

        var airport = AirportResolver.FindNearest(
            record.TouchdownLatitudeDegrees!.Value,
            record.TouchdownLongitudeDegrees!.Value,
            facilities,
            out var distanceNauticalMiles);
        if (airport == null)
        {
            return false;
        }

        record.Airport = airport.Ident;
        record.AirportDistanceNauticalMiles = Math.Round(distanceNauticalMiles, 2);
        return true;
    }

    public static IReadOnlyList<AirportFacility> Merge(
        IEnumerable<AirportFacility> baseline,
        IEnumerable<AirportFacility> updates)
    {
        return (baseline ?? Enumerable.Empty<AirportFacility>())
            .Concat(updates ?? Enumerable.Empty<AirportFacility>())
            .GroupBy(facility => facility.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
    }
}
