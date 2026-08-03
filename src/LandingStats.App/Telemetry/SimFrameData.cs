using System.Runtime.InteropServices;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SimFrameData
{
    public double SimulationTimeSeconds;
    public double SimulationDeltaSeconds;
    public int OnGround;
    public int MotionSimulation;
    public double TouchdownNormalVelocityFps;
    public double VerticalSpeedFps;
    public double VelocityWorldYFps;
    public double VelocityBodyYFps;
    public double GForce;
    public double MaxGForce;
    public double SemibodyLoadFactorY;
    public double AccelerationBodyYFps2;
    public double AboveGroundLevelFeet;
    public double PitchDegrees;
    public double BankDegrees;
    public double LatitudeDegrees;
    public double LongitudeDegrees;
    public double IndicatedAirspeedKnots;
    public double GroundSpeedKnots;
    public double SimulationRate;
    public double PlaneAltitudeFeet;
    public double GroundAltitudeFeet;
    public double AboveGroundMinusCgFeet;
    public double AccelerationWorldYFps2;
    public double RotationVelocityBodyXRadiansPerSecond;
    public double RotationVelocityBodyYRadiansPerSecond;
    public double RotationVelocityBodyZRadiansPerSecond;
    public double TouchdownPitchDegrees;
    public double TouchdownBankDegrees;
    public double VelocityWorldXFps;
    public double VelocityWorldZFps;
    public double VelocityBodyXFps;
    public double VelocityBodyZFps;
    public double AccelerationWorldXFps2;
    public double AccelerationWorldZFps2;
    public double AccelerationBodyXFps2;
    public double AccelerationBodyZFps2;
    public double RotationAccelerationBodyXRadiansPerSecond2;
    public double RotationAccelerationBodyYRadiansPerSecond2;
    public double RotationAccelerationBodyZRadiansPerSecond2;
    public double SemibodyLoadFactorX;
    public double SemibodyLoadFactorZ;
    public double SemibodyLoadFactorYDot;
    public double HeadingTrueDegrees;
    public double TrueAirspeedKnots;
    public double Mach;
    public double AngleOfAttackDegrees;
    public double SideslipDegrees;
    public double AmbientWindVelocityKnots;
    public double AmbientWindDirectionDegrees;
    public double ElevatorPosition;
    public double ElevatorTrimRadians;
    public double AileronPosition;
    public double RudderPosition;
    public double SpoilersLeftPosition;
    public double SpoilersRightPosition;
    public double FlapsHandlePercent;
    public double FlapsLeftPercent;
    public double FlapsRightPercent;
    public double BrakeLeftPosition;
    public double BrakeRightPosition;
    public double GearHandlePosition;
    public double GearTotalPercentExtended;
    public double GearCenterPosition;
    public double GearLeftPosition;
    public double GearRightPosition;
    public double TotalWeightPounds;
    public double CgPercent;
    public int OnAnyRunway;
    public int SurfaceType;
    public int SurfaceCondition;
    public int SpoilersArmed;
    public int NumberOfEngines;
    public double PilotRollInputPercent;
    public double PilotPitchInputPercent;
    public double RudderPedalInputPercent;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = TelemetrySample.CapturedEngineCount, ArraySubType = UnmanagedType.R8)]
    public double[]? EngineThrottlePercent;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = TelemetrySample.CapturedEngineCount, ArraySubType = UnmanagedType.R8)]
    public double[]? EngineN1Percent;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = TelemetrySample.CapturedEngineCount, ArraySubType = UnmanagedType.R8)]
    public double[]? EngineRpm;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = TelemetrySample.CapturedEngineCount, ArraySubType = UnmanagedType.R8)]
    public double[]? EngineReversePercent;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = TelemetrySample.CapturedContactPointCount, ArraySubType = UnmanagedType.R8)]
    public double[]? ContactPointCompression;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = TelemetrySample.CapturedContactPointCount, ArraySubType = UnmanagedType.R8)]
    public double[]? ContactPointPosition;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = TelemetrySample.CapturedContactPointCount, ArraySubType = UnmanagedType.I4)]
    public int[]? ContactPointOnGround;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
internal struct AircraftMetadata
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string? Title;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string? AtcType;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string? AtcModel;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string? Category;
}
