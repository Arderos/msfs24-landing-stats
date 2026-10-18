namespace LandingStats.Core;

// Fixed estimator-specific acceptance limits. Different models retain distinct conditioning policies.
internal static class GeometryCalibrationPolicy
{
    internal const double MinimumMainOnlyDurationSeconds = 0.20;
    internal const double MinimumSustainedContactDurationSeconds = 0.04;
    internal const double MinimumDerotationPitchIncreaseDegrees = 0.50;
    internal const double MinimumClusterDerotationPitchIncreaseDegrees = 0.40;
    internal const double MinimumClusterGapDominanceRatio = 1.50;
    internal const double MaximumAuxiliaryNoseOnsetDelaySeconds = 0.50;
    internal const double MinimumCompressionEvidence = 0.001;
    internal const double IntegrationTargetSeconds = 1.0;
    internal const double MaximumIntegrationDurationSeconds = 1.20;
    internal const double MaximumFrameGapSeconds = 0.15;
    internal const double MaximumAbsoluteArmFeet = 500.0;
    internal const double MaximumDatumStandardDeviationFeet = 5.0;
    internal const double MinimumDesignIndependence = 0.001;
    internal const double MinimumAcceptedQuality = 0.20;
    internal const int MinimumPitchRegressionRows = 6;
    internal const int MinimumDatumPhases = 3;
    internal const int MinimumSustainedContactSamples = 2;
    internal const int MinimumClusteredSettledPointCount = 6;
    internal const int MinimumPointsPerCluster = 2;
    internal const int MinimumStablePitchRegressionRows = 12;
    internal const int ArmStabilityBlockCount = 5;
    internal const double ArmStabilityTrimFraction = 0.30;
    internal const double MaximumPitchArmRefitDeviationFeet = 5.0;
}
