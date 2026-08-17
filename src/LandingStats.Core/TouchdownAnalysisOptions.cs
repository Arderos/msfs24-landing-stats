using System;

namespace LandingStats.Core;

public enum TouchdownGeometrySource
{
    Unavailable = 0,
    Provided = 1,
    FlightModelConfig = 2,
    Telemetry = 3,
}

/// <summary>
/// A configured main-gear contact point and its signed longitudinal arm from
/// the velocity reference point.
/// </summary>
public sealed class TouchdownMainGearContactPoint
{
    public TouchdownMainGearContactPoint(int contactPointIndex, double longitudinalArmFeet)
    {
        ContactPointIndex = contactPointIndex;
        LongitudinalArmFeet = longitudinalArmFeet;
    }

    public int ContactPointIndex { get; }

    public double LongitudinalArmFeet { get; }
}

/// <summary>
/// Optional, target-independent inputs for touchdown reconstruction.
/// </summary>
public sealed class TouchdownAnalysisOptions
{
    /// <summary>
    /// Longitudinal main-gear arm from the velocity reference point, in feet.
    /// Negative values place the main gear behind the reference point.
    /// </summary>
    public double? LongitudinalMainGearArmFeet { get; set; }

    /// <summary>
    /// Provenance of a supplied arm. Ignored when no finite arm was supplied.
    /// </summary>
    public TouchdownGeometrySource LongitudinalMainGearArmSource { get; set; } =
        TouchdownGeometrySource.Provided;

    /// <summary>
    /// Optional 0..1 identifiability score for the supplied arm.
    /// </summary>
    public double? LongitudinalMainGearArmQuality { get; set; }

    /// <summary>
    /// Main-gear contact-point indices and arms from a trusted configuration.
    /// When present, these roles take precedence over topology inferred from
    /// anonymous compression channels.
    /// </summary>
    public TouchdownMainGearContactPoint[] MainGearContactPoints { get; set; } =
        Array.Empty<TouchdownMainGearContactPoint>();

    /// <summary>
    /// Nose-gear contact-point indices from the same trusted configuration.
    /// </summary>
    public int[] NoseGearContactPointIndices { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Allows the analyzer to recover the arm from approach and rollout
    /// telemetry when no external geometry value was supplied.
    /// </summary>
    public bool RecoverLongitudinalMainGearArmFromTelemetry { get; set; } = true;
}
