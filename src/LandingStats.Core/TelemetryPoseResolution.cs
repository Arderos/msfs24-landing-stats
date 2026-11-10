namespace LandingStats.Core;

/// <summary>Existing pose-comparison resolution, shared by source and motion evidence.</summary>
internal static class TelemetryPoseResolution
{
    // These are analysis tolerances for comparing simulator observations, not
    // double roundoff or a claim about the sensor's absolute accuracy.
    internal const double CoordinateDegrees = 1e-8;
    internal const double AltitudeFeet = 1e-3;
    internal const double AngleDegrees = 1e-4;
    internal const double AltitudeMeters = AltitudeFeet * .3048;

    // Both coordinate components may differ within their comparison resolution.
    // The equatorial diagonal bounds that unresolved distance at every latitude.
    internal static readonly double HorizontalMeters = GeoDistance.GreatCircleNauticalMiles(
        0, 0, CoordinateDegrees, CoordinateDegrees) * 1852;
}
