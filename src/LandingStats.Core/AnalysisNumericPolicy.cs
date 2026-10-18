namespace LandingStats.Core;

/// <summary>Shared definitions; model-specific observation thresholds remain with their estimators.</summary>
public static class AnalysisNumericPolicy
{
    public const double EpisodeGapSeconds = 10.0;
    public const double SmoothImpactG = 1.2;
    public const double FirmImpactG = 1.5;
    internal const double GroundOutlierFeet = 5.0;
    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
