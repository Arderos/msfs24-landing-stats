using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LandingStats.Core;

public static class ReplaySourceTimelineCodec
{
    public const int MaximumSamples = 65536;
    public const int MaximumBytes = 8 + MaximumSamples * (1 + ReplaySourceEvidenceCodec.EvidenceSize);
    private const uint Magic = 0x3152534c; // LSR1
    public static byte[]? Encode(IReadOnlyList<TelemetrySample> samples)
    {
        if (samples.Count > MaximumSamples) throw new InvalidDataException("Source timeline exceeds its sample bound.");
        if (!samples.Any(sample => sample.SourceEvidence != null)) return null;
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(Magic); writer.Write(samples.Count);
        foreach (var sample in samples)
        { writer.Write(sample.SourceEvidence != null); if (sample.SourceEvidence != null) ReplaySourceEvidenceCodec.Write(writer, sample.SourceEvidence); }
        return memory.ToArray();
    }
    public static string? EncodeText(IReadOnlyList<TelemetrySample> samples)
    { var bytes = Encode(samples); return bytes == null ? null : Convert.ToBase64String(bytes); }
    public static ReplaySourceEvidence?[] Decode(byte[] bytes, int expectedSamples)
    {
        try { return DecodeCore(bytes, expectedSamples); }
        catch (Exception error) when (error is ArgumentException || error is EndOfStreamException)
        { throw new InvalidDataException("Invalid source timeline data.", error); }
    }
    private static ReplaySourceEvidence?[] DecodeCore(byte[] bytes, int expectedSamples)
    {
        if (bytes == null || bytes.Length < 8 || bytes.Length > MaximumBytes || expectedSamples < 1 || expectedSamples > MaximumSamples)
            throw new InvalidDataException("Source timeline exceeds its bound.");
        using var memory = new MemoryStream(bytes, false); using var reader = new BinaryReader(memory);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != expectedSamples) throw new InvalidDataException("Source timeline does not match its telemetry rows.");
        var evidence = new ReplaySourceEvidence?[expectedSamples];
        for (var i = 0; i < evidence.Length; i++)
        {
            var tag = reader.ReadByte(); if (tag > 1) throw new InvalidDataException("Invalid source evidence presence tag.");
            if (tag == 1) evidence[i] = ReplaySourceEvidenceCodec.Read(reader);
        }
        if (memory.Position != memory.Length) throw new InvalidDataException("Unexpected source timeline tail.");
        return evidence;
    }
    public static ReplaySourceEvidence?[] DecodeText(string text, int expectedSamples)
    {
        if (text == null || text.Length > ((MaximumBytes + 2) / 3) * 4) throw new InvalidDataException("Source timeline text exceeds its bound.");
        try { return Decode(Convert.FromBase64String(text), expectedSamples); }
        catch (FormatException error) { throw new InvalidDataException("Invalid source timeline encoding.", error); }
    }
    public static void Apply(byte[] bytes, IReadOnlyList<TelemetrySample> samples)
    {
        var evidence = Decode(bytes, samples.Count);
        for (var i = 0; i < samples.Count; i++) samples[i].SourceEvidence = evidence[i];
    }
}
