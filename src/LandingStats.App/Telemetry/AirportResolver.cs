using System;
using System.Collections.Generic;
using LandingStats.App.Models;

namespace LandingStats.App.Telemetry;

internal static class AirportResolver
{
    private const double EarthRadiusNauticalMiles = 3440.065;
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
            var distance = GreatCircleDistanceNauticalMiles(
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

    private static double GreatCircleDistanceNauticalMiles(
        double latitudeOneDegrees,
        double longitudeOneDegrees,
        double latitudeTwoDegrees,
        double longitudeTwoDegrees)
    {
        var latitudeOne = latitudeOneDegrees * Math.PI / 180.0;
        var latitudeTwo = latitudeTwoDegrees * Math.PI / 180.0;
        var latitudeDelta = latitudeTwo - latitudeOne;
        var longitudeDelta = (longitudeTwoDegrees - longitudeOneDegrees) * Math.PI / 180.0;
        var sinLatitude = Math.Sin(latitudeDelta / 2.0);
        var sinLongitude = Math.Sin(longitudeDelta / 2.0);
        var haversine = sinLatitude * sinLatitude +
                        Math.Cos(latitudeOne) * Math.Cos(latitudeTwo) * sinLongitude * sinLongitude;
        var angle = 2.0 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(Math.Max(0, 1.0 - haversine)));
        return EarthRadiusNauticalMiles * angle;
    }
}
