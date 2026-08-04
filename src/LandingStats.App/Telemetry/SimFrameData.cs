using System.Runtime.InteropServices;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SimGuardData
{
    public double SimulationTimeSeconds;
    public double AboveGroundLevelFeet;
    public int OnGround;
    public double VelocityWorldYFps;
    public int MotionSimulation;
}

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
    public double GForce;
    public double AboveGroundLevelFeet;
    public double PitchDegrees;
    public double BankDegrees;
    public double LatitudeDegrees;
    public double LongitudeDegrees;
    public double IndicatedAirspeedKnots;
    public double GroundSpeedKnots;
    public double PlaneAltitudeFeet;
    public double GroundAltitudeFeet;
    public double RotationVelocityBodyXRadiansPerSecond;
    public double RotationVelocityBodyYRadiansPerSecond;
    public double RotationVelocityBodyZRadiansPerSecond;
    public double AccelerationBodyXFps2;
    public double AccelerationBodyZFps2;
    public double HeadingTrueDegrees;
    public double AngleOfAttackDegrees;
    public double SideslipDegrees;
    public double AmbientWindVelocityKnots;
    public double AmbientWindDirectionDegrees;
    public double ElevatorPosition;
    public double ElevatorTrimRadians;
    public double AileronPosition;
    public double RudderPosition;
    public double ElevatorDeflectionPercentOver100;
    public double AileronLeftDeflectionPercentOver100;
    public double AileronRightDeflectionPercentOver100;
    public double RudderDeflectionPercentOver100;
    public double SpoilersLeftPosition;
    public double SpoilersRightPosition;
    public double FlapsLeftPercent;
    public double FlapsRightPercent;
    public double BrakeLeftPosition;
    public double BrakeRightPosition;
    public double TotalWeightPounds;
    public double CgPercent;
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
