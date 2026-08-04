namespace LandingStats.Core;

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
    /// Allows the analyzer to recover the arm from approach and rollout
    /// telemetry when no passport value was supplied.
    /// </summary>
    public bool RecoverLongitudinalMainGearArmFromTelemetry { get; set; } = true;
}
