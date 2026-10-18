using System;
using System.IO;

namespace LandingStats.Core;

public enum SourceObservationAvailability : byte { Unknown = 0, Pending = 1, Available = 2, Unavailable = 3, Gap = 4 }
public enum SourceObservationReason : byte
{
    None = 0, AwaitingReply = 1, RequestRejected = 2, DefinitionRejected = 3,
    RequestTimeout = 4, InvalidData = 5, ConnectionGap = 6, InitialState = 7,
}
public enum SdkReplayState : sbyte { Unknown = -1, Inactive = 0, Active = 1 }

/// <summary>One optional USER-object observation. Freeze bits are latitude/longitude,
/// altitude and attitude, in that order. Unknown fields are never encoded as zero.</summary>
public sealed class ReplaySourceObservation
{
    public ReplaySourceObservation(SourceObservationAvailability availability, SourceObservationReason reason,
        int freezeMask = -1, int slew = -1, uint requestId = 0, uint sdkException = 0,
        double simulationTime = double.NaN, double latitude = double.NaN, double longitude = double.NaN,
        double altitudeFeet = double.NaN, double pitch = double.NaN, double bank = double.NaN, double heading = double.NaN)
    {
        if (!Enum.IsDefined(typeof(SourceObservationAvailability), availability) || !Enum.IsDefined(typeof(SourceObservationReason), reason) ||
            (availability == SourceObservationAvailability.Available
                ? freezeMask < 0 || freezeMask > 7 || slew < 0 || slew > 1 || reason != SourceObservationReason.None || requestId == 0
                : freezeMask != -1 || slew != -1 || reason == SourceObservationReason.None))
            throw new ArgumentException("Invalid optional simulator observation.");
        Availability = availability; Reason = reason; FreezeMask = freezeMask; Slew = slew;
        RequestId = requestId; SdkException = sdkException; SimulationTime = simulationTime;
        Latitude = latitude; Longitude = longitude; AltitudeFeet = altitudeFeet; Pitch = pitch; Bank = bank; Heading = heading;
    }
    public SourceObservationAvailability Availability { get; }
    public SourceObservationReason Reason { get; }
    public int FreezeMask { get; }
    public int Slew { get; }
    public uint RequestId { get; }
    public uint SdkException { get; }
    public double SimulationTime { get; }
    public double Latitude { get; }
    public double Longitude { get; }
    public double AltitudeFeet { get; }
    public double Pitch { get; }
    public double Bank { get; }
    public double Heading { get; }
}

/// <summary>Immutable observation assigned by the recording owner. QPC times share
/// the recording session's epoch; an SDK lifecycle event never refreshes ObservedQpc.</summary>
public sealed class ReplaySourceEvidence
{
    public ReplaySourceEvidence(Guid workerInstance, long generation, long connection, long observedQpc,
        long frameQpc, long qpcFrequency, SdkReplayState sdkReplay, ReplaySourceObservation observation)
    {
        if (workerInstance == Guid.Empty || generation <= 0 || connection <= 0 || observedQpc < 0 || frameQpc < 0 || qpcFrequency <= 0 ||
            !Enum.IsDefined(typeof(SdkReplayState), sdkReplay)) throw new ArgumentException("Invalid source evidence context.");
        WorkerInstance = workerInstance; Generation = generation; Connection = connection; ObservedQpc = observedQpc;
        FrameQpc = frameQpc; QpcFrequency = qpcFrequency; SdkReplay = sdkReplay;
        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
    }
    public Guid WorkerInstance { get; }
    public long Generation { get; }
    public long Connection { get; }
    public long ObservedQpc { get; }
    public long FrameQpc { get; }
    public long QpcFrequency { get; }
    public SdkReplayState SdkReplay { get; }
    public ReplaySourceObservation Observation { get; }
    public double AgeSeconds => (FrameQpc - (double)ObservedQpc) / QpcFrequency;
    public bool SameSource(ReplaySourceEvidence other) => WorkerInstance == other.WorkerInstance && Generation == other.Generation &&
        Connection == other.Connection && QpcFrequency == other.QpcFrequency;
}

/// <summary>Optional metadata format, deliberately separate from numeric telemetry
/// and its existing fingerprint. All raw IEEE-754 values survive exactly.</summary>
public static class ReplaySourceEvidenceCodec
{
    public const int ObservationSize = 68;
    public const int EvidenceSize = 128;
    public static void WriteObservation(BinaryWriter writer, ReplaySourceObservation value)
    {
        writer.Write((byte)value.Availability); writer.Write((sbyte)value.FreezeMask); writer.Write((sbyte)value.Slew); writer.Write((byte)value.Reason);
        writer.Write(value.RequestId); writer.Write(value.SdkException); writer.Write(value.SimulationTime);
        writer.Write(value.Latitude); writer.Write(value.Longitude); writer.Write(value.AltitudeFeet);
        writer.Write(value.Pitch); writer.Write(value.Bank); writer.Write(value.Heading);
    }
    public static ReplaySourceObservation ReadObservation(BinaryReader reader)
    {
        var availability = (SourceObservationAvailability)reader.ReadByte(); var mask = reader.ReadSByte(); var slew = reader.ReadSByte();
        return new ReplaySourceObservation(availability, (SourceObservationReason)reader.ReadByte(), mask, slew,
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(),
            reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
    }
    public static void Write(BinaryWriter writer, ReplaySourceEvidence value)
    {
        writer.Write((byte)1); writer.Write((sbyte)value.SdkReplay); writer.Write((ushort)0); writer.Write(value.WorkerInstance.ToByteArray());
        writer.Write(value.Generation); writer.Write(value.Connection); writer.Write(value.ObservedQpc); writer.Write(value.FrameQpc); writer.Write(value.QpcFrequency);
        WriteObservation(writer, value.Observation);
    }
    public static ReplaySourceEvidence Read(BinaryReader reader)
    {
        if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported source evidence version.");
        var sdk = (SdkReplayState)reader.ReadSByte();
        if (reader.ReadUInt16() != 0) throw new InvalidDataException("Invalid source evidence flags.");
        var worker = new Guid(reader.ReadBytes(16)); var generation = reader.ReadInt64(); var connection = reader.ReadInt64();
        var observed = reader.ReadInt64(); var frame = reader.ReadInt64(); var frequency = reader.ReadInt64();
        return new ReplaySourceEvidence(worker, generation, connection, observed, frame, frequency, sdk, ReadObservation(reader));
    }
}
