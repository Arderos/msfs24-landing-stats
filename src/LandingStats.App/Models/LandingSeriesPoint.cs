using System.Runtime.Serialization;
using LandingStats.Core;

namespace LandingStats.App.Models;

[DataContract]
public sealed class LandingSeriesPoint
{
    [DataMember(Order = 1)]
    public double TimeSeconds { get; set; }

    [DataMember(Order = 2)]
    public double InertialFpm { get; set; }

    [DataMember(Order = 3)]
    public double IndicatedFpm { get; set; }

    [DataMember(Order = 4)]
    public double GForce { get; set; }

    [DataMember(Order = 5)]
    public double AglFeet { get; set; }

    [DataMember(Order = 6)]
    public double GroundSpeedKnots { get; set; }

    [DataMember(Order = 7)]
    public double PitchDegrees { get; set; }

    [DataMember(Order = 8)]
    public double BankDegrees { get; set; }

    [DataMember(Order = 9)]
    public double AngleOfAttackDegrees { get; set; }

    [DataMember(Order = 10)]
    public double SideslipDegrees { get; set; }

    [DataMember(Order = 11)]
    public double PilotRollPercent { get; set; }

    [DataMember(Order = 12)]
    public double PilotPitchPercent { get; set; }

    [DataMember(Order = 13)]
    public double PilotYawPercent { get; set; }

    [DataMember(Order = 14)]
    public double AileronPercent { get; set; }

    [DataMember(Order = 15)]
    public double ElevatorPercent { get; set; }

    [DataMember(Order = 16)]
    public double RudderPercent { get; set; }

    [DataMember(Order = 17)]
    public double SpoilersLeftPercent { get; set; }

    [DataMember(Order = 18)]
    public double SpoilersRightPercent { get; set; }

    [DataMember(Order = 19)]
    public double FlapsPercent { get; set; }

    [DataMember(Order = 20)]
    public double BrakeLeftPercent { get; set; }

    [DataMember(Order = 21)]
    public double BrakeRightPercent { get; set; }

    [DataMember(Order = 22)]
    public double LongitudinalLoadG { get; set; }

    [DataMember(Order = 23)]
    public double LateralLoadG { get; set; }

    [DataMember(Order = 24)]
    public double BodyRateXDegreesPerSecond { get; set; }

    [DataMember(Order = 25)]
    public double BodyRateYDegreesPerSecond { get; set; }

    [DataMember(Order = 26)]
    public double BodyRateZDegreesPerSecond { get; set; }

    [DataMember(Order = 27)]
    public double WindSpeedKnots { get; set; }

    [DataMember(Order = 28)]
    public double WindDirectionDegrees { get; set; }

    [DataMember(Order = 29)]
    public bool OnGround { get; set; }

    [DataMember(Order = 30)]
    public double LateralAccelerationFps2 { get; set; }

    [DataMember(Order = 31)]
    public double LongitudinalAccelerationFps2 { get; set; }

    [DataMember(Order = 32)]
    public double ElevatorDeflectionPercent { get; set; }

    [DataMember(Order = 33)]
    public double AileronLeftDeflectionPercent { get; set; }

    [DataMember(Order = 34)]
    public double AileronRightDeflectionPercent { get; set; }

    [DataMember(Order = 35)]
    public double RudderDeflectionPercent { get; set; }

    [DataMember(Order = 36)]
    public double AxisElevatorSetPercent { get; set; }

    [DataMember(Order = 37)]
    public bool AxisElevatorSetValid { get; set; }

    [DataMember(Order = 38)]
    public double AxisElevatorSetAgeSeconds { get; set; }

    [DataMember(Order = 39)]
    public double[] RawControllerYAxisPercent { get; set; } = new double[TelemetrySample.CapturedControllerCount];

    [DataMember(Order = 40)]
    public bool[] RawControllerYAxisValid { get; set; } = new bool[TelemetrySample.CapturedControllerCount];

    [DataMember(Order = 41)]
    public double[] RawControllerYAxisAgeSeconds { get; set; } = new double[TelemetrySample.CapturedControllerCount];

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        RawControllerYAxisPercent ??= new double[TelemetrySample.CapturedControllerCount];
        RawControllerYAxisValid ??= new bool[TelemetrySample.CapturedControllerCount];
        RawControllerYAxisAgeSeconds ??= new double[TelemetrySample.CapturedControllerCount];
    }
}
