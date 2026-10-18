using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using LandingStats.Core;

namespace LandingStats.App.Runtime;

internal static class RuntimeCheckpointCodec
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public static void WriteText(BinaryWriter writer, string value, int maximumBytes = 4096)
    {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("Checkpoint text exceeds its bound.");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    public static string ReadText(BinaryReader reader, int maximumBytes = 4096) => Utf8.GetString(ReadBytes(reader, ReadCount(reader, maximumBytes)));
    public static byte[] ReadBytes(BinaryReader reader, int length)
    {
        if (length < 0 || (reader.BaseStream.CanSeek && length > reader.BaseStream.Length - reader.BaseStream.Position)) throw new EndOfStreamException();
        var value = reader.ReadBytes(length); if (value.Length != length) throw new EndOfStreamException(); return value;
    }
    public static int ReadCount(BinaryReader reader, int maximum)
    { var count = reader.ReadInt32(); if (count < 0 || count > maximum) throw new InvalidDataException("Checkpoint count exceeds its bound."); return count; }
    public static bool ReadBoolean(BinaryReader reader)
    { var value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid checkpoint boolean."); return value == 1; }
    public static void WriteNullableBool(BinaryWriter writer, bool? value) => writer.Write((sbyte)(value.HasValue ? value.Value ? 1 : 0 : -1));
    public static bool? ReadNullableBool(BinaryReader reader)
    { var value = reader.ReadSByte(); if (value == -1) return null; if (value == 0 || value == 1) return value == 1; throw new InvalidDataException("Invalid nullable checkpoint boolean."); }
    public static void WriteSample(BinaryWriter writer, TelemetrySample? sample)
    {
        writer.Write((byte)(sample == null ? 0 : (sample.ControllerCatalog == null ? 1 : 2) + (sample.SourceEvidence == null ? 0 : 2)));
        if (sample == null) return;
        writer.Write(TelemetryBinaryCodec.Encode(sample));
        if (sample.ControllerCatalog != null) ControllerProvenanceCodec.Write(writer, sample.ControllerCatalog);
        if (sample.SourceEvidence != null) ReplaySourceEvidenceCodec.Write(writer, sample.SourceEvidence);
    }
    public static TelemetrySample? ReadSample(BinaryReader reader)
    {
        var tag = reader.ReadByte(); if (tag == 0) return null;
        if (tag > 4) throw new InvalidDataException("Invalid checkpoint sample tag.");
        var sample = TelemetryBinaryCodec.Decode(ReadBytes(reader, TelemetryBinaryCodec.EncodedSize));
        if (tag == 2 || tag == 4) sample.ControllerCatalog = ControllerProvenanceCodec.Read(reader);
        if (tag >= 3) sample.SourceEvidence = ReplaySourceEvidenceCodec.Read(reader);
        return sample;
    }
    public static void WriteSamples(BinaryWriter writer, IReadOnlyList<TelemetrySample> samples, int maximum = 65536)
    {
        if (samples.Count > maximum) throw new InvalidDataException("Checkpoint samples exceed their bound.");
        var sourceEvidence = samples.Any(sample => sample.SourceEvidence != null);
        if (sourceEvidence) writer.Write(int.MinValue);
        ControllerProvenanceCodec.WriteFrames(writer, samples);
        if (sourceEvidence)
            foreach (var sample in samples)
            { writer.Write(sample.SourceEvidence != null); if (sample.SourceEvidence != null) ReplaySourceEvidenceCodec.Write(writer, sample.SourceEvidence); }
    }
    public static TelemetrySample[] ReadSamples(BinaryReader reader, int maximum = 65536)
    {
        var first = reader.ReadInt32();
        if (first != int.MinValue) return ControllerProvenanceCodec.ReadFrames(reader, maximum, first);
        var samples = ControllerProvenanceCodec.ReadFrames(reader, maximum);
        foreach (var sample in samples) if (ReadBoolean(reader)) sample.SourceEvidence = ReplaySourceEvidenceCodec.Read(reader);
        return samples;
    }
    public static void WriteJournal(BinaryWriter writer, TelemetryDecisionJournal? journal)
    {
        writer.Write(journal != null); if (journal == null) return;
        var snapshot = journal.Snapshot(); writer.Write(snapshot.DroppedEntryCount); writer.Write(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            writer.Write(entry.TimeSeconds); WriteText(writer, entry.Component); WriteText(writer, entry.Code); WriteText(writer, entry.Reason);
            writer.Write(entry.Details.Count); foreach (var pair in entry.Details) { WriteText(writer, pair.Key); writer.Write(pair.Value); }
        }
    }
    public static TelemetryDecisionJournal? ReadJournal(BinaryReader reader)
    {
        if (!ReadBoolean(reader)) return null;
        var dropped = ReadCount(reader, int.MaxValue); var count = ReadCount(reader, TelemetryDecisionJournal.MaximumEntries);
        var journal = new TelemetryDecisionJournal();
        for (var i = 0; i < count; i++)
        {
            var time = reader.ReadDouble(); var component = ReadText(reader); var code = ReadText(reader); var reason = ReadText(reader);
            var details = new Dictionary<string, double>(StringComparer.Ordinal); var width = ReadCount(reader, 24);
            for (var d = 0; d < width; d++) details.Add(ReadText(reader), reader.ReadDouble());
            journal.Record(time, component, code, reason, details);
        }
        if (journal.Snapshot().Entries.Count != count) throw new InvalidDataException("Checkpoint decision journal exceeds its content limit.");
        journal.RestoreDroppedEntryCount(dropped); return journal;
    }
    public static void WriteJson<T>(BinaryWriter writer, T value, int maximum = 8 * 1024 * 1024)
    {
        using var stream = new MemoryStream(); new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        if (stream.Length > maximum) throw new InvalidDataException("Checkpoint metadata exceeds its bound.");
        var bytes = stream.ToArray(); writer.Write(bytes.Length); writer.Write(bytes);
    }
    public static T ReadJson<T>(BinaryReader reader, int maximum = 8 * 1024 * 1024)
    {
        using var stream = new MemoryStream(ReadBytes(reader, ReadCount(reader, maximum)), false);
        return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(stream) ?? throw new InvalidDataException("Checkpoint metadata is empty."));
    }
}
