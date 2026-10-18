using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace LandingStats.Runtime.Contracts;

public sealed class SceneryCommandMessage : RuntimeCommand
{
    public SceneryCommandMessage(long connection, Guid episode, long episodeOrdinal, Guid lookup, LookupStage stage, double latitude, double longitude,
        bool cancel = false, long witnessQpc = 0, bool resumeOnly = false)
    {
        if (connection <= 0 || episode == Guid.Empty || episodeOrdinal <= 0 || lookup == Guid.Empty ||
            (stage != LookupStage.Preliminary && stage != LookupStage.Final) || double.IsNaN(latitude) || double.IsNaN(longitude) ||
            Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180 || witnessQpc < 0 || (cancel && resumeOnly)) throw new ArgumentException("Invalid scenery command context.");
        Connection = connection; Episode = episode; EpisodeOrdinal = episodeOrdinal; Lookup = lookup; Stage = stage;
        Latitude = latitude; Longitude = longitude; Cancel = cancel; WitnessQpc = witnessQpc; ResumeOnly = resumeOnly;
    }
    public long Connection { get; }
    public Guid Episode { get; }
    public long EpisodeOrdinal { get; }
    public Guid Lookup { get; }
    public LookupStage Stage { get; }
    public double Latitude { get; }
    public double Longitude { get; }
    public bool Cancel { get; }
    public long WitnessQpc { get; }
    public bool ResumeOnly { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.SceneryCommand;
    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Connection); writer.Write(Episode.ToByteArray()); writer.Write(EpisodeOrdinal); writer.Write(Lookup.ToByteArray());
        writer.Write((byte)Stage); writer.Write(Latitude); writer.Write(Longitude); writer.Write(Cancel); writer.Write(WitnessQpc); writer.Write(ResumeOnly);
    }
    internal static SceneryCommandMessage Read(BinaryReader reader) => new SceneryCommandMessage(reader.ReadInt64(), new Guid(reader.ReadBytes(16)), reader.ReadInt64(),
        new Guid(reader.ReadBytes(16)), (LookupStage)reader.ReadByte(), reader.ReadDouble(), reader.ReadDouble(), RuntimeWire.ReadBoolean(reader), reader.ReadInt64(), RuntimeWire.ReadBoolean(reader));
}

// A large fresh airport list is paced in small messages. No partially received
// transfer is interpreted as a complete scenery snapshot.
public sealed class SceneryChunkMessage : RuntimePayload
{
    public const int ChunkBytes = 32768;
    public const int MaximumTransferBytes = 8 * 1024 * 1024 + 4;
    private readonly byte[] _bytes, _hash;
    public SceneryChunkMessage(Guid transfer, int index, int length, bool completed, byte[] hash, byte[] bytes)
    {
        if (transfer == Guid.Empty || length <= 0 || length > MaximumTransferBytes || index < 0 || index > (length - 1) / ChunkBytes ||
            hash == null || hash.Length != 32 || bytes == null || bytes.Length != Math.Min(ChunkBytes, length - index * ChunkBytes))
            throw new ArgumentException("Invalid scenery transfer chunk.");
        Transfer = transfer; Index = index; Length = length; Completed = completed; _hash = (byte[])hash.Clone(); _bytes = (byte[])bytes.Clone();
    }
    public Guid Transfer { get; }
    public int Index { get; }
    public int Length { get; }
    public bool Completed { get; }
    public byte[] CopyBytes() => (byte[])_bytes.Clone();
    public byte[] CopyHash() => (byte[])_hash.Clone();
    public override RuntimeMessageKind Kind => RuntimeMessageKind.SceneryChunk;
    internal override void Write(BinaryWriter writer)
    { writer.Write(Transfer.ToByteArray()); writer.Write(Index); writer.Write(Length); writer.Write(Completed); writer.Write(_hash); writer.Write(_bytes.Length); writer.Write(_bytes); }
    internal static SceneryChunkMessage Read(BinaryReader reader)
    {
        var id = new Guid(reader.ReadBytes(16)); var index = reader.ReadInt32(); var length = reader.ReadInt32(); var complete = RuntimeWire.ReadBoolean(reader);
        var hash = reader.ReadBytes(32); var size = reader.ReadInt32();
        if (size < 0 || size > ChunkBytes) throw new InvalidDataException("Scenery chunk exceeds its bound.");
        return new SceneryChunkMessage(id, index, length, complete, hash, reader.ReadBytes(size));
    }
}

public sealed class SourceDiagnosticMessage : RuntimePayload
{
    public SourceDiagnosticMessage(double time, string component, string code, string reason, IReadOnlyDictionary<string, double> details)
    {
        if (double.IsNaN(time) || double.IsInfinity(time) || details == null || details.Count > 24) throw new ArgumentException("Invalid source diagnostic.");
        RuntimeWire.ValidateText(component, 192); RuntimeWire.ValidateText(code, 320); RuntimeWire.ValidateText(reason, 1024);
        var copy = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in details)
        {
            RuntimeWire.ValidateText(pair.Key, 256);
            if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value)) throw new ArgumentException("Invalid diagnostic value.");
            copy.Add(pair.Key, pair.Value);
        }
        Time = time; Component = component; Code = code; Reason = reason; Details = new ReadOnlyDictionary<string, double>(copy);
    }
    public double Time { get; }
    public string Component { get; }
    public string Code { get; }
    public string Reason { get; }
    public IReadOnlyDictionary<string, double> Details { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.SourceDiagnostic;
    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Time); RuntimeWire.WriteText(writer, Component, 192); RuntimeWire.WriteText(writer, Code, 320); RuntimeWire.WriteText(writer, Reason, 1024);
        writer.Write(Details.Count); foreach (var pair in Details) { RuntimeWire.WriteText(writer, pair.Key, 256); writer.Write(pair.Value); }
    }
    internal static SourceDiagnosticMessage Read(BinaryReader reader)
    {
        var time = reader.ReadDouble(); var component = RuntimeWire.ReadText(reader, 192); var code = RuntimeWire.ReadText(reader, 320); var reason = RuntimeWire.ReadText(reader, 1024);
        var count = reader.ReadInt32(); if (count < 0 || count > 24) throw new InvalidDataException("Diagnostic exceeds its bound.");
        var details = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++) details.Add(RuntimeWire.ReadText(reader, 256), reader.ReadDouble());
        return new SourceDiagnosticMessage(time, component, code, reason, details);
    }
}

public sealed class SourceStatusMessage : RuntimePayload
{
    public SourceStatusMessage(byte state, string key, IReadOnlyList<string> arguments)
    {
        if (state > 3 || arguments == null || arguments.Count > 8) throw new ArgumentException("Invalid source status.");
        RuntimeWire.ValidateText(key, 256);
        var copy = new string[arguments.Count];
        for (var i = 0; i < copy.Length; i++) { RuntimeWire.ValidateText(arguments[i], 1024); copy[i] = arguments[i]; }
        State = state; Key = key; Arguments = Array.AsReadOnly(copy);
    }
    public byte State { get; }
    public string Key { get; }
    public IReadOnlyList<string> Arguments { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.SourceStatus;
    internal override void Write(BinaryWriter writer)
    { writer.Write(State); RuntimeWire.WriteText(writer, Key, 256); writer.Write(Arguments.Count); foreach (var value in Arguments) RuntimeWire.WriteText(writer, value, 1024); }
    internal static SourceStatusMessage Read(BinaryReader reader)
    {
        var state = reader.ReadByte(); var key = RuntimeWire.ReadText(reader, 256); var count = reader.ReadInt32();
        if (count < 0 || count > 8) throw new InvalidDataException("Status exceeds its bound.");
        var arguments = new string[count]; for (var i = 0; i < count; i++) arguments[i] = RuntimeWire.ReadText(reader, 1024);
        return new SourceStatusMessage(state, key, arguments);
    }
}
