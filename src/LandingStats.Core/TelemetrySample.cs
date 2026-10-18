namespace LandingStats.Core;

public enum TelemetrySimulatorState
{
    Legacy = 0,
    Running = 1,
    Paused = 2,
    Inactive = 3,
}

public sealed class TelemetrySample
{
    // SimConnect documents numeric contact-point indices in the inclusive 0-19 range.
    public const int CapturedContactPointCount = 20;
    public const int CapturedEngineCount = 4;

    public const int CapturedControllerCount = 8;
    // Device-major order: X, Y, Z, R, U, V. These are physical axes, not simulator bindings.
    public const int CapturedControllerAxisCount = 6;

    public long Sequence { get; set; }

    public double HostElapsedSeconds { get; set; }

    public double SimulationTimeSeconds { get; set; }

    public double SimulationDeltaSeconds { get; set; }

    // Raw simulator clocks remain unchanged. The recorder supplies this clock
    // only after removing SDK-confirmed pause time within a flight segment.
    public double AnalysisTimeSeconds { get; set; } = double.NaN;

    public TelemetrySimulatorState SimulatorState { get; set; }

    public int PauseFlags { get; set; } = -1;

    public long FlightSegment { get; set; }

    // Recording-owned catalog observation. Null means a legacy capture without
    // provenance; empty means the controller source had no catalog. Numeric
    // telemetry fingerprints intentionally retain their existing definition.
    // Like the measured arrays, this buffer is read-only after publication.
    public byte[]? ControllerCatalog { get; set; }

    // Null in older captures. This optional observation is not a measured numeric
    // channel and does not change existing telemetry fingerprints or CSV columns.
    public ReplaySourceEvidence? SourceEvidence { get; set; }

    // Only an analysis-owned inactive sentinel sets this marker. It is not a
    // simulator observation and is deliberately absent from the raw CSV schema.
    public TelemetryPositionDiscontinuity? AnalysisDiscontinuity { get; internal set; }

    /// <summary>Analysis owns the scalar copy; its shared telemetry arrays are read-only.</summary>
    public TelemetrySample CopyForAnalysis()
    {
        var copy = (TelemetrySample)MemberwiseClone();
        if (!double.IsNaN(AnalysisTimeSeconds) && !double.IsInfinity(AnalysisTimeSeconds))
            copy.SimulationTimeSeconds = AnalysisTimeSeconds;
        return copy;
    }

    public bool OnGround { get; set; }

    public bool MotionSimulation { get; set; }

    public double TouchdownNormalVelocityFps { get; set; }

    public double VerticalSpeedFps { get; set; }

    public double VelocityWorldYFps { get; set; }

    public double GForce { get; set; }

    public double AboveGroundLevelFeet { get; set; }

    public double PitchDegrees { get; set; }

    public double BankDegrees { get; set; }

    public double LatitudeDegrees { get; set; }

    public double LongitudeDegrees { get; set; }

    public double IndicatedAirspeedKnots { get; set; }

    public double GroundSpeedKnots { get; set; }

    public double PlaneAltitudeFeet { get; set; }

    public double GroundAltitudeFeet { get; set; }

    public double RotationVelocityBodyXRadiansPerSecond { get; set; }

    public double RotationVelocityBodyYRadiansPerSecond { get; set; }

    public double RotationVelocityBodyZRadiansPerSecond { get; set; }

    public double AccelerationBodyXFps2 { get; set; }

    public double AccelerationBodyZFps2 { get; set; }

    public double HeadingTrueDegrees { get; set; }

    public double AngleOfAttackDegrees { get; set; }

    public double SideslipDegrees { get; set; }

    public double AmbientWindVelocityKnots { get; set; }

    public double AmbientWindDirectionDegrees { get; set; }

    public double ElevatorPosition { get; set; }

    public double ElevatorTrimRadians { get; set; }

    public double AileronPosition { get; set; }

    public double RudderPosition { get; set; }

    public double ElevatorDeflectionPercentOver100 { get; set; }

    public double AileronLeftDeflectionPercentOver100 { get; set; }

    public double AileronRightDeflectionPercentOver100 { get; set; }

    public double RudderDeflectionPercentOver100 { get; set; }

    public double SpoilersLeftPosition { get; set; }

    public double SpoilersRightPosition { get; set; }

    public double FlapsLeftPercent { get; set; }

    public double FlapsRightPercent { get; set; }

    public double BrakeLeftPosition { get; set; }

    public double BrakeRightPosition { get; set; }

    public double TotalWeightPounds { get; set; }

    public double CgPercent { get; set; }

    public int NumberOfEngines { get; set; }

    public double PilotRollInputPercent { get; set; }

    public double PilotPitchInputPercent { get; set; }

    public double RudderPedalInputPercent { get; set; }

    public double AxisElevatorSetPercent { get; set; }

    public bool AxisElevatorSetValid { get; set; }

    public double AxisElevatorSetAgeSeconds { get; set; }

    public double[] EngineThrottlePercent { get; } = new double[CapturedEngineCount];

    public double[] EngineN1Percent { get; } = new double[CapturedEngineCount];

    public double[] EngineRpm { get; } = new double[CapturedEngineCount];

    public double[] EngineReversePercent { get; } = new double[CapturedEngineCount];

    public int[] RawControllerDeviceId { get; } = new int[CapturedControllerCount];

    public double[] RawControllerYAxisPercent { get; } = new double[CapturedControllerCount];

    public bool[] RawControllerYAxisValid { get; } = new bool[CapturedControllerCount];

    public double[] RawControllerYAxisAgeSeconds { get; } = new double[CapturedControllerCount];

    public double[] RawControllerAxesPercent { get; } = new double[CapturedControllerCount * CapturedControllerAxisCount];

    public bool[] RawControllerAxesValid { get; } = new bool[CapturedControllerCount * CapturedControllerAxisCount];

    public double[] RawControllerAxesAgeSeconds { get; } = new double[CapturedControllerCount * CapturedControllerAxisCount];

    public double[] ContactPointCompression { get; } = new double[CapturedContactPointCount];

    public double[] ContactPointPosition { get; } = new double[CapturedContactPointCount];

    public bool[] ContactPointOnGround { get; } = new bool[CapturedContactPointCount];
}
