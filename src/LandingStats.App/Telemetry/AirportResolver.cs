using System;
using System.Collections.Generic;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

internal static class AirportResolver
{
    private const double MaximumAirportDistanceNauticalMiles = 12.0;

    public static AirportFacility? FindNearest(
        double latitudeDegrees,
        double longitudeDegrees,
        IReadOnlyList<AirportFacility> facilities,
        out double distanceNauticalMiles)
    {
        distanceNauticalMiles = double.PositiveInfinity;
        AirportFacility? nearest = null;
        foreach (var facility in facilities)
        {
            var distance = GeoDistance.GreatCircleNauticalMiles(
                latitudeDegrees,
                longitudeDegrees,
                facility.LatitudeDegrees,
                facility.LongitudeDegrees);
            if (distance < distanceNauticalMiles)
            {
                distanceNauticalMiles = distance;
                nearest = facility;
            }
        }

        if (nearest == null || distanceNauticalMiles > MaximumAirportDistanceNauticalMiles)
        {
            distanceNauticalMiles = double.NaN;
            return null;
        }

        return nearest;
    }
}
