using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Core;

namespace LandingStats.Runtime.Contracts;

public static class RuntimeWire
{
    public const ushort ProtocolVersion = 1;
    public const int MaximumPayloadLength = 1024 * 1024;
    public const int HeaderLength = 134;
    private const uint Magic = 0x5452534c; // LSRT, little endian
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static byte[] Encode(RuntimeEnvelope envelope)
    {
        if (envelope == null) throw new ArgumentNullException(nameof(envelope));
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Utf8, true)) envelope.Payload.Write(writer);
        if (body.Length > MaximumPayloadLength) throw new InvalidDataException("Runtime payload exceeds the limit.");
        using var output = new MemoryStream(HeaderLength + (int)body.Length);
        using (var writer = new BinaryWriter(output, Utf8, true))
        {
            writer.Write(Magic); writer.Write(ProtocolVersion); writer.Write((ushort)envelope.Payload.Kind); writer.Write((int)body.Length);
            writer.Write(envelope.Worker.AppSessionId.ToByteArray()); writer.Write(envelope.Worker.InstanceId.ToByteArray());
            writer.Write(envelope.Worker.Generation); writer.Write((byte)envelope.Worker.Role);
            writer.Write(envelope.ConnectionGeneration); writer.Write(envelope.Sequence); writer.Write(envelope.CaptureQpc);
            writer.Write(envelope.CorrelationId.ToByteArray()); writer.Write(envelope.CommandRevision);
            writer.Write(envelope.EpisodeId.ToByteArray()); writer.Write(envelope.LookupId.ToByteArray()); writer.Write((byte)envelope.LookupStage);
            body.Position = 0; body.CopyTo(output);
        }
        return output.ToArray();
    }

    public static RuntimeEnvelope Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < HeaderLength || bytes.Length > HeaderLength + MaximumPayloadLength)
            throw new InvalidDataException("Invalid runtime message length.");
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream, Utf8, true);
            ReadPrefix(reader, out var kind, out var length);
            if (bytes.Length != HeaderLength + length) throw new InvalidDataException("Runtime message length mismatch.");
            var worker = new WorkerIdentity(ReadGuid(reader), ReadGuid(reader), reader.ReadInt64(), (ComponentRole)reader.ReadByte());
            var connection = reader.ReadInt64(); var sequence = reader.ReadInt64(); var captureQpc = reader.ReadInt64();
            var correlation = ReadGuid(reader); var revision = reader.ReadInt64(); var episode = ReadGuid(reader); var lookup = ReadGuid(reader);
            var stage = (LookupStage)reader.ReadByte();
            var payload = ReadPayload(reader, kind);
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected runtime payload tail.");
            return new RuntimeEnvelope(worker, connection, sequence, captureQpc, payload, correlation, revision, episode, lookup, stage);
        }
        catch (EndOfStreamException exception) { throw new InvalidDataException("Truncated runtime message.", exception); }
        catch (ArgumentException exception) { throw new InvalidDataException("Malformed runtime message.", exception); }
    }

    public static async Task<RuntimeEnvelope?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[12];
        var first = await stream.ReadAsync(prefix, 0, prefix.Length, cancellationToken).ConfigureAwait(false);
        if (first == 0) return null; // EOF between messages is different from a partially written message.
        await ReadExactlyAsync(stream, prefix, first, prefix.Length - first, cancellationToken).ConfigureAwait(false);
        int length;
        using (var memory = new MemoryStream(prefix, false))
        using (var reader = new BinaryReader(memory, Utf8)) ReadPrefix(reader, out _, out length);
        var bytes = new byte[HeaderLength + length]; // Prefix validation precedes allocation.
        Array.Copy(prefix, bytes, prefix.Length);
        await ReadExactlyAsync(stream, bytes, prefix.Length, bytes.Length - prefix.Length, cancellationToken).ConfigureAwait(false);
        return Decode(bytes);
    }

    public static async Task WriteAsync(Stream stream, RuntimeEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = Encode(envelope);
        // The caller owns the single output pump. Concurrent writes on one stream are not supported.
        await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, int offset, int count, CancellationToken cancellationToken)
    {
        while (count != 0)
        {
            var read = await stream.ReadAsync(bytes, offset, count, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Truncated runtime message.");
            offset += read; count -= read;
        }
    }

    private static void ReadPrefix(BinaryReader reader, out RuntimeMessageKind kind, out int length)
    {
        if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != ProtocolVersion) throw new InvalidDataException("Unsupported runtime protocol.");
        kind = (RuntimeMessageKind)reader.ReadUInt16(); length = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(RuntimeMessageKind), kind) || length < 0 || length > MaximumPayloadLength)
            throw new InvalidDataException("Unknown or oversized runtime message.");
    }

    private static RuntimePayload ReadPayload(BinaryReader reader, RuntimeMessageKind kind)
    {
        switch (kind)
        {
            case RuntimeMessageKind.SimulatorLifecycleSnapshot: return SimulatorLifecycleSnapshotMessage.Read(reader);
            case RuntimeMessageKind.AnalysisJob: return AnalysisJobMessage.Read(reader);
            case RuntimeMessageKind.AnalysisResult: return AnalysisResultMessage.Read(reader);
            case RuntimeMessageKind.UploadJob: return UploadJobMessage.Read(reader);
            case RuntimeMessageKind.UploadResult: return UploadResultMessage.Read(reader);
            case RuntimeMessageKind.CloudJob: return CloudJobMessage.Read(reader);
            case RuntimeMessageKind.CloudResult: return CloudResultMessage.Read(reader);
            case RuntimeMessageKind.IntegrationRequest: return IntegrationRequestMessage.Read(reader);
            case RuntimeMessageKind.IntegrationResponse: return IntegrationResponseCommand.Read(reader);
            case RuntimeMessageKind.SceneryCommand: return SceneryCommandMessage.Read(reader);
            case RuntimeMessageKind.SceneryChunk: return SceneryChunkMessage.Read(reader);
            case RuntimeMessageKind.SourceDiagnostic: return SourceDiagnosticMessage.Read(reader);
            case RuntimeMessageKind.SourceStatus: return SourceStatusMessage.Read(reader);
            case RuntimeMessageKind.SourceObservation: return new SourceObservationMessage(ReplaySourceEvidenceCodec.ReadObservation(reader));
            case RuntimeMessageKind.ControllerCatalog: return ControllerCatalogMessage.Read(reader);
            case RuntimeMessageKind.AircraftControls: return AircraftControlsMessage.Read(reader);
            case RuntimeMessageKind.ControllerSnapshot: return ControllerSnapshotMessage.Read(reader);
            case RuntimeMessageKind.DurableAck: return new DurableAckMessage(reader.ReadInt64());
            case RuntimeMessageKind.ControlAck: return new ControlAckMessage(reader.ReadInt64());
            case RuntimeMessageKind.Hello:
                return new WorkerHello(ReadText(reader, 128), ReadText(reader, 64), reader.ReadUInt64(), ReadGuid(reader), reader.ReadInt64());
            case RuntimeMessageKind.Frame:
                return new TelemetryFrameMessage(TelemetryBinaryCodec.Read(reader));
            case RuntimeMessageKind.GuardFrame:
                return new GuardFrameMessage(reader.ReadDouble(), reader.ReadDouble(), ReadBoolean(reader), reader.ReadDouble(), ReadBoolean(reader));
            case RuntimeMessageKind.Lifecycle:
                return new LifecycleMessage((SourceLifecycle)reader.ReadByte(), reader.ReadInt32());
            case RuntimeMessageKind.Aircraft:
                return new AircraftMessage(ReadText(reader, 128), ReadText(reader, 512), ReadText(reader, 256), ReadText(reader, 256));
            case RuntimeMessageKind.Health:
                return new ComponentHealthMessage((ComponentPhase)reader.ReadByte(), (DependencyWait)reader.ReadByte(), reader.ReadInt64(), reader.ReadInt64(),
                    reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32(), ReadGuid(reader));
            case RuntimeMessageKind.Gap:
                return new SourceGapMessage((SourceGapReason)reader.ReadByte(), reader.ReadInt64(), reader.ReadInt64(), ReadBoolean(reader));
            case RuntimeMessageKind.Command:
                return new WorkerCommandMessage((WorkerCommand)reader.ReadByte(), ReadBoolean(reader));
            case RuntimeMessageKind.CommandResult:
                return new CommandResultMessage((CommandOutcome)reader.ReadByte(), ReadText(reader, 1024));
            default: throw new InvalidDataException("Unsupported runtime payload.");
        }
    }

    internal static void ValidateText(string value, int maximumBytes)
    {
        if (value == null || Utf8.GetByteCount(value) > maximumBytes || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid runtime text.");
    }
    internal static void WriteText(BinaryWriter writer, string value, int maximumBytes)
    { ValidateText(value, maximumBytes); var bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    internal static string ReadText(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximumBytes) throw new InvalidDataException("Invalid runtime text length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Utf8.GetString(bytes);
    }
    private static Guid ReadGuid(BinaryReader reader)
    { var bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new EndOfStreamException(); return new Guid(bytes); }
    internal static bool ReadBoolean(BinaryReader reader)
    { var value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid boolean."); return value == 1; }
}
