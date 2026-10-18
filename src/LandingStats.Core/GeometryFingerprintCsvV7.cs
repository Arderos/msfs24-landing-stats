using System.Collections.Generic;
using System.Globalization;

namespace LandingStats.Core;

// Frozen CSV v7 projection used by published snapshot v1. New CSV schemas must not alter it.
public static class GeometryFingerprintCsvV7
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static string Number(double value) => value.ToString("G17", Invariant);
    public static string Format(TelemetrySample sample)
    {
        var values = new List<string>
        {
            sample.Sequence.ToString(Invariant),
            Number(sample.HostElapsedSeconds),
            Number(sample.SimulationTimeSeconds),
            Number(sample.SimulationDeltaSeconds),
            sample.OnGround ? "1" : "0",
            sample.MotionSimulation ? "1" : "0",
            Number(sample.TouchdownNormalVelocityFps),
            Number(sample.VerticalSpeedFps),
            Number(sample.VelocityWorldYFps),
            Number(sample.GForce),
            Number(sample.AboveGroundLevelFeet),
            Number(sample.PitchDegrees),
            Number(sample.BankDegrees),
            Number(sample.LatitudeDegrees),
            Number(sample.LongitudeDegrees),
            Number(sample.IndicatedAirspeedKnots),
            Number(sample.GroundSpeedKnots),
            Number(sample.PlaneAltitudeFeet),
            Number(sample.GroundAltitudeFeet),
            Number(sample.RotationVelocityBodyXRadiansPerSecond),
            Number(sample.RotationVelocityBodyYRadiansPerSecond),
            Number(sample.RotationVelocityBodyZRadiansPerSecond),
            Number(sample.AccelerationBodyXFps2),
            Number(sample.AccelerationBodyZFps2),
            Number(sample.HeadingTrueDegrees),
            Number(sample.AngleOfAttackDegrees),
            Number(sample.SideslipDegrees),
            Number(sample.AmbientWindVelocityKnots),
            Number(sample.AmbientWindDirectionDegrees),
            Number(sample.ElevatorPosition),
            Number(sample.ElevatorTrimRadians),
            Number(sample.AileronPosition),
            Number(sample.RudderPosition),
            Number(sample.SpoilersLeftPosition),
            Number(sample.SpoilersRightPosition),
            Number(sample.FlapsLeftPercent),
            Number(sample.FlapsRightPercent),
            Number(sample.BrakeLeftPosition),
            Number(sample.BrakeRightPosition),
            Number(sample.TotalWeightPounds),
            Number(sample.CgPercent),
            sample.NumberOfEngines.ToString(Invariant),
            Number(sample.PilotRollInputPercent),
            Number(sample.PilotPitchInputPercent),
            Number(sample.RudderPedalInputPercent),
        };

        for (var index = 0; index < 4; index++)
        {
            values.Add(Number(sample.EngineThrottlePercent[index]));
        }

        for (var index = 0; index < 4; index++)
        {
            values.Add(Number(sample.EngineN1Percent[index]));
        }

        for (var index = 0; index < 4; index++)
        {
            values.Add(Number(sample.EngineRpm[index]));
        }

        for (var index = 0; index < 4; index++)
        {
            values.Add(Number(sample.EngineReversePercent[index]));
        }

        for (var index = 0; index < 20; index++)
        {
            values.Add(Number(sample.ContactPointCompression[index]));
        }

        for (var index = 0; index < 20; index++)
        {
            values.Add(Number(sample.ContactPointPosition[index]));
        }

        for (var index = 0; index < 20; index++)
        {
            values.Add(sample.ContactPointOnGround[index] ? "1" : "0");
        }

        values.Add(Number(sample.ElevatorDeflectionPercentOver100));
        values.Add(Number(sample.AileronLeftDeflectionPercentOver100));
        values.Add(Number(sample.AileronRightDeflectionPercentOver100));
        values.Add(Number(sample.RudderDeflectionPercentOver100));
        values.Add(Number(sample.AxisElevatorSetPercent));
        values.Add(sample.AxisElevatorSetValid ? "1" : "0");
        values.Add(Number(sample.AxisElevatorSetAgeSeconds));

        for (var index = 0; index < 8; index++)
        {
            values.Add(sample.RawControllerDeviceId[index].ToString(Invariant));
            values.Add(Number(sample.RawControllerYAxisPercent[index]));
            values.Add(sample.RawControllerYAxisValid[index] ? "1" : "0");
            values.Add(Number(sample.RawControllerYAxisAgeSeconds[index]));
        }

        for (var controller = 0; controller < 8; controller++)
        {
            for (var axis = 0; axis < 6; axis++)
            {
                if (axis == 1) continue; // Y is already in the backward-compatible prefix.
                var offset = controller * 6 + axis;
                values.Add(Number(sample.RawControllerAxesPercent[offset]));
                values.Add(sample.RawControllerAxesValid[offset] ? "1" : "0");
                values.Add(Number(sample.RawControllerAxesAgeSeconds[offset]));
            }
        }

        values.Add(Number(sample.AnalysisTimeSeconds));
        values.Add(((int)sample.SimulatorState).ToString(Invariant));
        values.Add(sample.PauseFlags.ToString(Invariant));
        values.Add(sample.FlightSegment.ToString(Invariant));
        return string.Join(",", values);
    }

}
