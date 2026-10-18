using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>Optional recording metadata, independent of the fixed numeric
/// telemetry format. Old positive-count arrays and 0/1 sample tags remain readable.</summary>
internal static class ControllerProvenanceCodec
{
    internal const int MaximumCatalogBytes = 16384;
    internal const int MaximumTableBytes = 4 * 1024 * 1024;
    private const uint CatalogMagic = 0x3143434c; // LCC1, immutable archive contract, not an IPC envelope
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    internal static byte[] Encode(RuntimeEnvelope value)
    {
        if (value.Worker.Role != ComponentRole.Controllers || !(value.Payload is ControllerCatalogMessage catalog))
            throw new InvalidDataException("Recording provenance must contain a controller catalog.");
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Utf8, true);
        writer.Write(CatalogMagic); writer.Write(value.Worker.AppSessionId.ToByteArray()); writer.Write(value.Worker.InstanceId.ToByteArray());
        writer.Write(value.Worker.Generation); writer.Write(value.Sequence); writer.Write(value.CaptureQpc); writer.Write(catalog.Revision); writer.Write(catalog.Devices.Count);
        foreach (var device in catalog.Devices)
        {
            writer.Write(device.Slot); writer.Write(device.DeviceId); WriteText(writer, device.Name); WriteText(writer, device.ProductKey); writer.Write(device.Axes);
            for (var axis = 0; axis < 6; axis++) { writer.Write(device.Minimum(axis)); writer.Write(device.Maximum(axis)); }
        }
        writer.Flush(); return memory.ToArray();
    }
    private static void WriteText(BinaryWriter writer, string text)
    { var bytes = Utf8.GetBytes(text); if (bytes.Length > 512 || text.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid controller catalog text."); writer.Write(bytes.Length); writer.Write(bytes); }
    private static string ReadText(BinaryReader reader)
    { return Utf8.GetString(RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, 512))); }
    internal static RuntimeEnvelope? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        if (bytes.Length > MaximumCatalogBytes) throw new InvalidDataException("Controller catalog exceeds its bound.");
        // Optional metadata from a newer archive remains byte-preserved, but
        // cannot identify devices until its version is understood.
        if (IsFutureArchive(bytes)) return null;
        RuntimeEnvelope value;
        try
        {
            using var memory = new MemoryStream(bytes, false); using var reader = new BinaryReader(memory, Utf8, true);
            if (bytes.Length >= 4 && reader.ReadUInt32() == CatalogMagic)
            {
                var worker = new WorkerIdentity(new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)), new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)), reader.ReadInt64(), ComponentRole.Controllers);
                var sequence = reader.ReadInt64(); var capture = reader.ReadInt64(); var revision = reader.ReadInt64();
                var devices = new ControllerDescriptor[RuntimeCheckpointCodec.ReadCount(reader, 8)];
                for (var i = 0; i < devices.Length; i++)
                {
                    var slot = reader.ReadInt32(); var device = reader.ReadInt32(); var name = ReadText(reader); var product = ReadText(reader); var axes = reader.ReadByte();
                    var minimum = new uint[6]; var maximum = new uint[6];
                    for (var axis = 0; axis < 6; axis++) { minimum[axis] = reader.ReadUInt32(); maximum[axis] = reader.ReadUInt32(); }
                    devices[i] = new ControllerDescriptor(slot, device, name, product, axes, minimum, maximum);
                }
                if (memory.Position != memory.Length) throw new InvalidDataException("Unexpected catalog archive suffix.");
                value = new RuntimeEnvelope(worker, 0, sequence, capture, new ControllerCatalogMessage(revision, devices));
            }
            else value = RuntimeWire.Decode(bytes); // Original pre-release archive format remains readable.
        }
        catch (Exception error) when (error is ArgumentException || error is EndOfStreamException)
        { throw new InvalidDataException("Invalid controller archive metadata.", error); }
        if (value.Worker.Role != ComponentRole.Controllers || !(value.Payload is ControllerCatalogMessage))
            throw new InvalidDataException("Recording provenance must contain a controller catalog.");
        return value;
    }
    internal static bool IsFutureArchive(byte[] bytes) => bytes.Length >= 4 && bytes[0] == (byte)'L' && bytes[1] == (byte)'C' &&
        bytes[2] == (byte)'C' && bytes[3] > (byte)'1';
    internal static void Write(BinaryWriter writer, byte[] bytes)
    { Decode(bytes); writer.Write(bytes.Length); writer.Write(bytes); }
    internal static byte[] Read(BinaryReader reader)
    {
        var bytes = RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, MaximumCatalogBytes));
        Decode(bytes); return bytes;
    }
    internal static void WriteFrames(BinaryWriter writer, IReadOnlyList<TelemetrySample> samples)
    {
        var table = new Dictionary<byte[], int>(BytesComparer.Instance);
        var contexts = new List<byte[]>(); var total = 0;
        foreach (var sample in samples)
        {
            var bytes = sample.ControllerCatalog;
            if (bytes == null || table.ContainsKey(bytes)) continue;
            Decode(bytes); total = checked(total + bytes.Length);
            if (total > MaximumTableBytes || contexts.Count >= 4096) throw new InvalidDataException("Controller provenance table exceeds its bound.");
            table.Add(bytes, contexts.Count); contexts.Add(bytes);
        }
        if (contexts.Count == 0)
        { writer.Write(samples.Count); foreach (var sample in samples) writer.Write(TelemetryBinaryCodec.Encode(sample)); return; }
        writer.Write(-samples.Count - 1); writer.Write(contexts.Count);
        foreach (var bytes in contexts) Write(writer, bytes);
        foreach (var sample in samples)
        { writer.Write(TelemetryBinaryCodec.Encode(sample)); writer.Write(sample.ControllerCatalog == null ? -1 : table[sample.ControllerCatalog]); }
    }
    internal static TelemetrySample[] ReadFrames(BinaryReader reader, int maximum, int? firstCount = null)
    {
        var encodedCount = firstCount ?? reader.ReadInt32(); var annotated = encodedCount < 0;
        var count = annotated ? -(long)encodedCount - 1 : encodedCount;
        if (count < 0 || count > maximum) throw new InvalidDataException("Checkpoint samples exceed their bound.");
        var contexts = Array.Empty<byte[]>();
        if (annotated)
        {
            contexts = new byte[RuntimeCheckpointCodec.ReadCount(reader, 4096)][]; var total = 0;
            for (var i = 0; i < contexts.Length; i++)
            {
                contexts[i] = Read(reader); total = checked(total + contexts[i].Length);
                if (total > MaximumTableBytes) throw new InvalidDataException("Controller provenance table exceeds its bound.");
            }
        }
        var samples = new TelemetrySample[(int)count];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = TelemetryBinaryCodec.Decode(RuntimeCheckpointCodec.ReadBytes(reader, TelemetryBinaryCodec.EncodedSize));
            if (!annotated) continue;
            var index = reader.ReadInt32();
            if (index < -1 || index >= contexts.Length) throw new InvalidDataException("Unknown controller catalog reference.");
            samples[i].ControllerCatalog = index < 0 ? null : contexts[index];
        }
        return samples;
    }
    internal sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        internal static readonly BytesComparer Instance = new BytesComparer();
        public bool Equals(byte[]? x, byte[]? y) => ReferenceEquals(x, y) || x != null && y != null && x.SequenceEqual(y);
        public int GetHashCode(byte[] bytes) { unchecked { var hash = 17; foreach (var value in bytes) hash = hash * 31 + value; return hash; } }
    }
}
