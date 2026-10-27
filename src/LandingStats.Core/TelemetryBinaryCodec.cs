using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LandingStats.Core;

/// <summary>Versioned little-endian binary64 data. No native layout or runtime formatter is part of the format.</summary>
public static class TelemetryBinaryCodec
{
    public const ushort Version = 1;
    public const int EncodedSize = 1869; // Historical v1 size.
    public const int MaximumEncodedSize = 1024 * 1024; // Shared runtime payload byte budget.
    public const string FingerprintFormat = "telemetry_binary64_v1_csv7";

    public static byte[] Encode(TelemetrySample sample)
    {
        if (sample == null) throw new ArgumentNullException(nameof(sample));
        if (sample.AnalysisDiscontinuity != null)
            throw new InvalidDataException("An analysis sentinel is not a raw source observation.");
        using var stream = new MemoryStream(EncodedSize);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) Write(writer, sample, false);
        if (stream.Length > MaximumEncodedSize) throw new InvalidDataException("Telemetry payload exceeds its byte budget.");
        return stream.ToArray();
    }

    public static TelemetrySample Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 2 || bytes.Length > MaximumEncodedSize) throw new InvalidDataException("Invalid telemetry payload length.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var sample = Read(reader);
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected telemetry payload suffix.");
        return sample;
    }

    public static TelemetrySample Read(BinaryReader reader)
    {
        var version = reader.ReadUInt16();
        if (version != 1 && version != 2) throw new InvalidDataException("Unsupported telemetry payload version.");
        var controllerCount = version == 2 ? reader.ReadInt32() : 8;
        var axisCount = version == 2 ? reader.ReadInt32() : 6;
        var controllerBytes = 21L + axisCount * 17L;
        if (controllerCount < 0 || (axisCount != 6 && axisCount != 8) ||
            controllerCount > (MaximumEncodedSize - 893) / controllerBytes ||
            (reader.BaseStream.CanSeek && controllerCount * controllerBytes + 883 > reader.BaseStream.Length - reader.BaseStream.Position))
            throw new InvalidDataException("Controller measurements exceed their telemetry payload.");
        var sample = new TelemetrySample
        {
            Sequence = reader.ReadInt64(),
            HostElapsedSeconds = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            SimulationTimeSeconds = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            SimulationDeltaSeconds = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AnalysisTimeSeconds = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            SimulatorState = (TelemetrySimulatorState)reader.ReadInt32(),
            PauseFlags = reader.ReadInt32(),
            FlightSegment = reader.ReadInt64(),
            OnGround = ReadBoolean(reader),
            MotionSimulation = ReadBoolean(reader),
            TouchdownNormalVelocityFps = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            VerticalSpeedFps = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            VelocityWorldYFps = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            GForce = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AboveGroundLevelFeet = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            PitchDegrees = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            BankDegrees = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            LatitudeDegrees = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            LongitudeDegrees = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            IndicatedAirspeedKnots = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            GroundSpeedKnots = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            PlaneAltitudeFeet = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            GroundAltitudeFeet = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            RotationVelocityBodyXRadiansPerSecond = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            RotationVelocityBodyYRadiansPerSecond = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            RotationVelocityBodyZRadiansPerSecond = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AccelerationBodyXFps2 = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AccelerationBodyZFps2 = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            HeadingTrueDegrees = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AngleOfAttackDegrees = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            SideslipDegrees = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AmbientWindVelocityKnots = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AmbientWindDirectionDegrees = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            ElevatorPosition = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            ElevatorTrimRadians = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AileronPosition = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            RudderPosition = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            ElevatorDeflectionPercentOver100 = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AileronLeftDeflectionPercentOver100 = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AileronRightDeflectionPercentOver100 = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            RudderDeflectionPercentOver100 = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            SpoilersLeftPosition = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            SpoilersRightPosition = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            FlapsLeftPercent = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            FlapsRightPercent = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            BrakeLeftPosition = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            BrakeRightPosition = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            TotalWeightPounds = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            CgPercent = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            NumberOfEngines = reader.ReadInt32(),
            PilotRollInputPercent = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            PilotPitchInputPercent = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            RudderPedalInputPercent = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AxisElevatorSetPercent = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            AxisElevatorSetValid = ReadBoolean(reader),
            AxisElevatorSetAgeSeconds = BitConverter.Int64BitsToDouble(reader.ReadInt64()),
        };
        if (sample.SimulatorState < TelemetrySimulatorState.Legacy || sample.SimulatorState > TelemetrySimulatorState.Inactive)
            throw new InvalidDataException("Unknown simulator state.");
        if (double.IsInfinity(sample.AnalysisTimeSeconds) || sample.PauseFlags < -1 || sample.FlightSegment < 0)
            throw new InvalidDataException("Invalid lifecycle telemetry values.");
        sample.ResizeControllers(controllerCount, axisCount);
        for (var i = 0; i < 4; i++) sample.EngineThrottlePercent[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < 4; i++) sample.EngineN1Percent[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < 4; i++) sample.EngineRpm[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < 4; i++) sample.EngineReversePercent[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < sample.ControllerCount; i++) sample.RawControllerDeviceId[i] = reader.ReadInt32();
        for (var i = 0; i < sample.ControllerCount; i++) sample.RawControllerYAxisPercent[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < sample.ControllerCount; i++) sample.RawControllerYAxisValid[i] = ReadBoolean(reader);
        for (var i = 0; i < sample.ControllerCount; i++) sample.RawControllerYAxisAgeSeconds[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < sample.RawControllerAxesPercent.Length; i++) sample.RawControllerAxesPercent[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < sample.RawControllerAxesPercent.Length; i++) sample.RawControllerAxesValid[i] = ReadBoolean(reader);
        for (var i = 0; i < sample.RawControllerAxesPercent.Length; i++) sample.RawControllerAxesAgeSeconds[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < 20; i++) sample.ContactPointCompression[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < 20; i++) sample.ContactPointPosition[i] = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        for (var i = 0; i < 20; i++) sample.ContactPointOnGround[i] = ReadBoolean(reader);
        return sample;
    }

    /// <summary>
    /// Binds the numeric CSV v7 projection, independent of G17 formatting and culture.
    /// CSV has one Y axis: its legacy prefix is authoritative, not the duplicate array slot.
    /// Signed zero and NaN payloads are normalized because CSV does not retain those distinctions.
    /// Every other finite value and the sign of infinity retain their exact IEEE-754 bits.
    /// Analysis-only discontinuity annotations are excluded, as with the legacy CSV fingerprint.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<TelemetrySample> samples) => GeometryFingerprintV2.Fingerprint(samples);

    private static void Write(BinaryWriter writer, TelemetrySample sample, bool canonical)
    {
        writer.Write(sample.HasLegacyControllerLayout ? Version : (ushort)2);
        if (!sample.HasLegacyControllerLayout) { writer.Write(sample.ControllerCount); writer.Write(sample.ControllerAxisCount); }
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
        for (var i = 0; i < sample.ControllerCount; i++) writer.Write(sample.RawControllerDeviceId[i]);
        for (var i = 0; i < sample.ControllerCount; i++) WriteDouble(writer, sample.RawControllerYAxisPercent[i], canonical);
        for (var i = 0; i < sample.ControllerCount; i++) writer.Write(sample.RawControllerYAxisValid[i]);
        for (var i = 0; i < sample.ControllerCount; i++) WriteDouble(writer, sample.RawControllerYAxisAgeSeconds[i], canonical);
        for (var i = 0; i < sample.RawControllerAxesPercent.Length; i++) WriteDouble(writer, canonical && i % sample.ControllerAxisCount == 1 ? sample.RawControllerYAxisPercent[i / sample.ControllerAxisCount] : sample.RawControllerAxesPercent[i], canonical);
        for (var i = 0; i < sample.RawControllerAxesPercent.Length; i++) writer.Write(canonical && i % sample.ControllerAxisCount == 1 ? sample.RawControllerYAxisValid[i / sample.ControllerAxisCount] : sample.RawControllerAxesValid[i]);
        for (var i = 0; i < sample.RawControllerAxesPercent.Length; i++) WriteDouble(writer, canonical && i % sample.ControllerAxisCount == 1 ? sample.RawControllerYAxisAgeSeconds[i / sample.ControllerAxisCount] : sample.RawControllerAxesAgeSeconds[i], canonical);
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

    private static bool ReadBoolean(BinaryReader reader)
    {
        var value = reader.ReadByte();
        if (value > 1) throw new InvalidDataException("Invalid telemetry boolean.");
        return value != 0;
    }
}
