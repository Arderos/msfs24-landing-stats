namespace LandingStats.Core;

public sealed class TelemetrySample
{
    public const int CapturedContactPointCount = 64;
    public const int CapturedEngineCount = 4;

    public const int CapturedControllerCount = 8;

    public long Sequence { get; set; }

    public double HostElapsedSeconds { get; set; }

    public double SimulationTimeSeconds { get; set; }

    public double SimulationDeltaSeconds { get; set; }

    public bool OnGround { get; set; }

    public bool MotionSimulation { get; set; }

    public double TouchdownNormalVelocityFps { get; set; }

    public double VerticalSpeedFps { get; set; }

    public double VelocityWorldYFps { get; set; }

    public double VelocityBodyYFps { get; set; }

    public double GForce { get; set; }

    public double MaxGForce { get; set; }

    public double SemibodyLoadFactorY { get; set; }

    public double AccelerationBodyYFps2 { get; set; }

    public double AboveGroundLevelFeet { get; set; }

    public double PitchDegrees { get; set; }

    public double BankDegrees { get; set; }

    public double LatitudeDegrees { get; set; }

    public double LongitudeDegrees { get; set; }

    public double IndicatedAirspeedKnots { get; set; }

    public double GroundSpeedKnots { get; set; }

    public double SimulationRate { get; set; }

    public double PlaneAltitudeFeet { get; set; }

    public double GroundAltitudeFeet { get; set; }

    public double AboveGroundMinusCgFeet { get; set; }

    public double AccelerationWorldYFps2 { get; set; }

    public double RotationVelocityBodyXRadiansPerSecond { get; set; }

    public double RotationVelocityBodyYRadiansPerSecond { get; set; }

    public double RotationVelocityBodyZRadiansPerSecond { get; set; }

    public double TouchdownPitchDegrees { get; set; }

    public double TouchdownBankDegrees { get; set; }

    public double VelocityWorldXFps { get; set; }

    public double VelocityWorldZFps { get; set; }

    public double VelocityBodyXFps { get; set; }

    public double VelocityBodyZFps { get; set; }

    public double AccelerationWorldXFps2 { get; set; }

    public double AccelerationWorldZFps2 { get; set; }

    public double AccelerationBodyXFps2 { get; set; }

    public double AccelerationBodyZFps2 { get; set; }

    public double RotationAccelerationBodyXRadiansPerSecond2 { get; set; }

    public double RotationAccelerationBodyYRadiansPerSecond2 { get; set; }

    public double RotationAccelerationBodyZRadiansPerSecond2 { get; set; }

    public double SemibodyLoadFactorX { get; set; }

    public double SemibodyLoadFactorZ { get; set; }

    public double SemibodyLoadFactorYDot { get; set; }

    public double HeadingTrueDegrees { get; set; }

    public double TrueAirspeedKnots { get; set; }

    public double Mach { get; set; }

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

    public double FlapsHandlePercent { get; set; }

    public double FlapsLeftPercent { get; set; }

    public double FlapsRightPercent { get; set; }

    public double BrakeLeftPosition { get; set; }

    public double BrakeRightPosition { get; set; }

    public double GearHandlePosition { get; set; }

    public double GearTotalPercentExtended { get; set; }

    public double GearCenterPosition { get; set; }

    public double GearLeftPosition { get; set; }

    public double GearRightPosition { get; set; }

    public double TotalWeightPounds { get; set; }

    public double CgPercent { get; set; }

    public bool OnAnyRunway { get; set; }

    public int SurfaceType { get; set; }

    public int SurfaceCondition { get; set; }

    public bool SpoilersArmed { get; set; }

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

    public double[] ContactPointCompression { get; } = new double[CapturedContactPointCount];

    public double[] ContactPointPosition { get; } = new double[CapturedContactPointCount];

    public bool[] ContactPointOnGround { get; } = new bool[CapturedContactPointCount];
}
