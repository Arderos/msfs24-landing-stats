using System;

namespace LandingStats.Core;

public static class GeoDistance
{
    private const double EarthRadiusNauticalMiles = 3440.065;

    public static double GreatCircleNauticalMiles(
        double latitudeOneDegrees,
        double longitudeOneDegrees,
        double latitudeTwoDegrees,
        double longitudeTwoDegrees)
    {
        var latitudeOne = DegreesToRadians(latitudeOneDegrees);
        var latitudeTwo = DegreesToRadians(latitudeTwoDegrees);
        var latitudeDelta = latitudeTwo - latitudeOne;
        var longitudeDelta = DegreesToRadians(longitudeTwoDegrees - longitudeOneDegrees);
        var sinLatitude = Math.Sin(latitudeDelta / 2.0);
        var sinLongitude = Math.Sin(longitudeDelta / 2.0);
        var haversine = sinLatitude * sinLatitude +
                        Math.Cos(latitudeOne) * Math.Cos(latitudeTwo) * sinLongitude * sinLongitude;
        var clamped = Math.Max(0.0, Math.Min(1.0, haversine));
        var angle = 2.0 * Math.Atan2(Math.Sqrt(clamped), Math.Sqrt(1.0 - clamped));
        return EarthRadiusNauticalMiles * angle;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
