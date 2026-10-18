using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.Runtime.Contracts;

/// <summary>The external protocol is versioned independently of RuntimeWire and recording formats.</summary>
public static class IntegrationApiProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 512 * 1024;
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumClients = 8;
    public const string PipePrefix = "LandingStats.Api.v1.";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static string PipeNameForUser(string sid)
    {
        if (string.IsNullOrEmpty(sid) || sid.Length > 184 || !sid.StartsWith("S-1-", StringComparison.Ordinal))
            throw new ArgumentException("A Windows user SID is required.", nameof(sid));
        foreach (var character in sid.Substring(4))
            if (character != '-' && (character < '0' || character > '9')) throw new ArgumentException("Invalid Windows user SID.", nameof(sid));
        return PipePrefix + sid;
    }

    public static async Task<string?> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0 || maximumBytes > MaximumMessageBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var prefix = new byte[4];
        var first = await stream.ReadAsync(prefix, 0, 4, cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await ReadExactlyAsync(stream, prefix, first, 4 - first, cancellationToken).ConfigureAwait(false);
        var length = (uint)(prefix[0] | prefix[1] << 8 | prefix[2] << 16 | prefix[3] << 24);
        if (length == 0 || length > maximumBytes) throw new InvalidDataException("Invalid API frame length.");
        var bytes = new byte[(int)length];
        await ReadExactlyAsync(stream, bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        return Utf8.GetString(bytes);
    }

    public static async Task WriteAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        var length = Utf8.GetByteCount(json);
        if (length == 0 || length > MaximumMessageBytes) throw new InvalidDataException("API response exceeds the frame limit.");
        var frame = new byte[4 + length];
        frame[0] = (byte)length; frame[1] = (byte)(length >> 8); frame[2] = (byte)(length >> 16); frame[3] = (byte)(length >> 24);
        Utf8.GetBytes(json, 0, json.Length, frame, 4);
        await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, int offset, int count, CancellationToken token)
    {
        while (count != 0)
        {
            var read = await stream.ReadAsync(bytes, offset, count, token).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Truncated API frame.");
            offset += read; count -= read;
        }
    }
}

public sealed class IntegrationRequestMessage : RuntimePayload
{
    public IntegrationRequestMessage(Guid requestId, string json)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Missing API request identity.", nameof(requestId));
        RuntimeWire.ValidateText(json, IntegrationApiProtocol.MaximumMessageBytes);
        RequestId = requestId; Json = json;
    }
    public Guid RequestId { get; }
    public string Json { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.IntegrationRequest;
    internal override void Write(BinaryWriter writer)
    { writer.Write(RequestId.ToByteArray()); RuntimeWire.WriteText(writer, Json, IntegrationApiProtocol.MaximumMessageBytes); }
    internal static IntegrationRequestMessage Read(BinaryReader reader) => new IntegrationRequestMessage(new Guid(reader.ReadBytes(16)),
        RuntimeWire.ReadText(reader, IntegrationApiProtocol.MaximumMessageBytes));
}

public sealed class IntegrationResponseCommand : RuntimeCommand
{
    public IntegrationResponseCommand(Guid requestId, string json)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Missing API request identity.", nameof(requestId));
        RuntimeWire.ValidateText(json, IntegrationApiProtocol.MaximumMessageBytes);
        RequestId = requestId; Json = json;
    }
    public Guid RequestId { get; }
    public string Json { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.IntegrationResponse;
    internal override void Write(BinaryWriter writer)
    { writer.Write(RequestId.ToByteArray()); RuntimeWire.WriteText(writer, Json, IntegrationApiProtocol.MaximumMessageBytes); }
    internal static IntegrationResponseCommand Read(BinaryReader reader) => new IntegrationResponseCommand(new Guid(reader.ReadBytes(16)),
        RuntimeWire.ReadText(reader, IntegrationApiProtocol.MaximumMessageBytes));
}
