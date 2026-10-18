using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LandingStats.Core;

// Immutable persisted geometry binding. Do not reuse the evolving IPC codec here.
internal static class GeometryFingerprintV2
{
    public static string Fingerprint(IReadOnlyList<TelemetrySample> samples)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        using var sha = SHA256.Create();
        using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("LS-GEOMETRY-INPUT/BINARY64/1\0"));
            writer.Write(samples.Count);
            foreach (var sample in samples) Write(writer, sample, true);
            writer.Flush();
            stream.FlushFinalBlock();
        }
        return BitConverter.ToString(sha.Hash!).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void Write(BinaryWriter writer, TelemetrySample sample, bool canonical)
    {
        writer.Write((ushort)1);
        writer.Write(sample.Sequence);
        WriteDouble(writer, sample.HostElapsedSeconds, canonical);
        WriteDouble(writer, sample.SimulationTimeSeconds, canonical);
        WriteDouble(writer, sample.SimulationDeltaSeconds, canonical);
        WriteDouble(writer, sample.AnalysisTimeSeconds, canonical);
        writer.Write((int)sample.SimulatorState);
        writer.Write(sample.PauseFlags);
        writer.Write(sample.FlightSegment);
        writer.Write(sample.OnGround);
        writer.Write(sample.MotionSimulation);
        WriteDouble(writer, sample.TouchdownNormalVelocityFps, canonical);
        WriteDouble(writer, sample.VerticalSpeedFps, canonical);
        WriteDouble(writer, sample.VelocityWorldYFps, canonical);
        WriteDouble(writer, sample.GForce, canonical);
        WriteDouble(writer, sample.AboveGroundLevelFeet, canonical);
        WriteDouble(writer, sample.PitchDegrees, canonical);
        WriteDouble(writer, sample.BankDegrees, canonical);
        WriteDouble(writer, sample.LatitudeDegrees, canonical);
        WriteDouble(writer, sample.LongitudeDegrees, canonical);
        WriteDouble(writer, sample.IndicatedAirspeedKnots, canonical);
        WriteDouble(writer, sample.GroundSpeedKnots, canonical);
        WriteDouble(writer, sample.PlaneAltitudeFeet, canonical);
        WriteDouble(writer, sample.GroundAltitudeFeet, canonical);
        WriteDouble(writer, sample.RotationVelocityBodyXRadiansPerSecond, canonical);
        WriteDouble(writer, sample.RotationVelocityBodyYRadiansPerSecond, canonical);
        WriteDouble(writer, sample.RotationVelocityBodyZRadiansPerSecond, canonical);
        WriteDouble(writer, sample.AccelerationBodyXFps2, canonical);
        WriteDouble(writer, sample.AccelerationBodyZFps2, canonical);
        WriteDouble(writer, sample.HeadingTrueDegrees, canonical);
        WriteDouble(writer, sample.AngleOfAttackDegrees, canonical);
        WriteDouble(writer, sample.SideslipDegrees, canonical);
        WriteDouble(writer, sample.AmbientWindVelocityKnots, canonical);
        WriteDouble(writer, sample.AmbientWindDirectionDegrees, canonical);
        WriteDouble(writer, sample.ElevatorPosition, canonical);
        WriteDouble(writer, sample.ElevatorTrimRadians, canonical);
        WriteDouble(writer, sample.AileronPosition, canonical);
        WriteDouble(writer, sample.RudderPosition, canonical);
        WriteDouble(writer, sample.ElevatorDeflectionPercentOver100, canonical);
        WriteDouble(writer, sample.AileronLeftDeflectionPercentOver100, canonical);
        WriteDouble(writer, sample.AileronRightDeflectionPercentOver100, canonical);
        WriteDouble(writer, sample.RudderDeflectionPercentOver100, canonical);
        WriteDouble(writer, sample.SpoilersLeftPosition, canonical);
        WriteDouble(writer, sample.SpoilersRightPosition, canonical);
        WriteDouble(writer, sample.FlapsLeftPercent, canonical);
        WriteDouble(writer, sample.FlapsRightPercent, canonical);
        WriteDouble(writer, sample.BrakeLeftPosition, canonical);
        WriteDouble(writer, sample.BrakeRightPosition, canonical);
        WriteDouble(writer, sample.TotalWeightPounds, canonical);
        WriteDouble(writer, sample.CgPercent, canonical);
        writer.Write(sample.NumberOfEngines);
        WriteDouble(writer, sample.PilotRollInputPercent, canonical);
        WriteDouble(writer, sample.PilotPitchInputPercent, canonical);
        WriteDouble(writer, sample.RudderPedalInputPercent, canonical);
        WriteDouble(writer, sample.AxisElevatorSetPercent, canonical);
        writer.Write(sample.AxisElevatorSetValid);
        WriteDouble(writer, sample.AxisElevatorSetAgeSeconds, canonical);
        for (var i = 0; i < 4; i++) WriteDouble(writer, sample.EngineThrottlePercent[i], canonical);
        for (var i = 0; i < 4; i++) WriteDouble(writer, sample.EngineN1Percent[i], canonical);
        for (var i = 0; i < 4; i++) WriteDouble(writer, sample.EngineRpm[i], canonical);
        for (var i = 0; i < 4; i++) WriteDouble(writer, sample.EngineReversePercent[i], canonical);
        for (var i = 0; i < 8; i++) writer.Write(sample.RawControllerDeviceId[i]);
        for (var i = 0; i < 8; i++) WriteDouble(writer, sample.RawControllerYAxisPercent[i], canonical);
        for (var i = 0; i < 8; i++) writer.Write(sample.RawControllerYAxisValid[i]);
        for (var i = 0; i < 8; i++) WriteDouble(writer, sample.RawControllerYAxisAgeSeconds[i], canonical);
        for (var i = 0; i < 48; i++) WriteDouble(writer, canonical && i % 6 == 1 ? sample.RawControllerYAxisPercent[i / 6] : sample.RawControllerAxesPercent[i], canonical);
        for (var i = 0; i < 48; i++) writer.Write(canonical && i % 6 == 1 ? sample.RawControllerYAxisValid[i / 6] : sample.RawControllerAxesValid[i]);
        for (var i = 0; i < 48; i++) WriteDouble(writer, canonical && i % 6 == 1 ? sample.RawControllerYAxisAgeSeconds[i / 6] : sample.RawControllerAxesAgeSeconds[i], canonical);
        for (var i = 0; i < 20; i++) WriteDouble(writer, sample.ContactPointCompression[i], canonical);
        for (var i = 0; i < 20; i++) WriteDouble(writer, sample.ContactPointPosition[i], canonical);
        for (var i = 0; i < 20; i++) writer.Write(sample.ContactPointOnGround[i]);
    }

    private static void WriteDouble(BinaryWriter writer, double value, bool canonical)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        if (canonical)
        {
            if (double.IsNaN(value)) bits = 0x7ff8000000000000L;
            else if (value == 0) bits = 0;
        }
        writer.Write(bits);
    }

}
