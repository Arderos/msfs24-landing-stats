namespace LandingStats.Core;

public sealed class TouchdownResult
{
    public int EpisodeNumber { get; set; }

    public int ContactNumber { get; set; }

    public long Sequence { get; set; }

    public double SimulationTimeSeconds { get; set; }

    public double EstimatedContactTimeSeconds { get; set; }

    public double EstimatedContactOffsetSeconds { get; set; }

    public bool ContactTimeEstimatedFromCompression { get; set; }

    public int ContactTimeEstimatePointCount { get; set; }

    public double ContactTimeEstimateSpreadSeconds { get; set; }

    public double InertialVerticalFpm { get; set; }

    public bool InertialVerticalExtrapolated { get; set; }

    public double InertialFitDurationSeconds { get; set; }

    public bool LatchUpdateDetected { get; set; }

    public double LatchedNormalFpm { get; set; }

    public double LatchedUpdateOffsetSeconds { get; set; }

    public double SurfaceRelativeDeltaFpm { get; set; }

    public double TerrainContributionFpm { get; set; }

    public double UnresolvedSurfaceDeltaFpm { get; set; }

    public double LastAirToFirstGroundSeconds { get; set; }

    public double FirstGroundToNextFrameSeconds { get; set; }

    public double LastAirborneIndicatedFpm { get; set; }

    public double FirstGroundIndicatedFpm { get; set; }

    public double LastAirborneWorldFpm { get; set; }

    public double FirstGroundWorldFpm { get; set; }

    public double LastAirborneBodyFpm { get; set; }

    public double FirstGroundBodyFpm { get; set; }

    public double AglAverage100MsFpm { get; set; }

    public double AglAverage150MsFpm { get; set; }

    public double GAtFirstGroundSample { get; set; }

    public double PeakG { get; set; }

    public double PeakGOffsetSeconds { get; set; }

    public double PeakG50Milliseconds { get; set; }

    public double PeakG100Milliseconds { get; set; }

    public double PeakG150Milliseconds { get; set; }

    public double PeakG250Milliseconds { get; set; }

    public double PeakG500Milliseconds { get; set; }

    public double PitchDegrees { get; set; }

    public double BankDegrees { get; set; }

    public double IndicatedAirspeedKnots { get; set; }

    public double GroundSpeedKnots { get; set; }
}
