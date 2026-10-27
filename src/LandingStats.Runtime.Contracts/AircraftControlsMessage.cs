using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace LandingStats.Runtime.Contracts;

/// <summary>A saved simulator binding observation. Replays use this observation, never today's profiles.</summary>
public sealed class AircraftControlsMessage : RuntimePayload
{
    public AircraftControlsMessage(string aircraftPath, string context, IEnumerable<ControllerBinding> bindings, string profileEvidence = "")
    {
        RuntimeWire.ValidateText(aircraftPath, 4096); RuntimeWire.ValidateText(context, 4096);
        RuntimeWire.ValidateText(profileEvidence, RuntimeWire.MaximumPayloadLength);
        var copy = bindings.ToArray();
        if (copy.Any(value => value == null)) throw new ArgumentException("Invalid aircraft control binding.");
        var bytes = 12L + Encoding.UTF8.GetByteCount(aircraftPath) + Encoding.UTF8.GetByteCount(context) +
            copy.Sum(value => 30L + Encoding.UTF8.GetByteCount(value.Source)) +
            (profileEvidence.Length == 0 ? 0 : 8L + Encoding.UTF8.GetByteCount(profileEvidence));
        if (bytes > RuntimeWire.MaximumPayloadLength) throw new ArgumentException("Aircraft controls and profile evidence exceed the runtime payload limit.");
        AircraftPath = aircraftPath; Context = context; Bindings = new ReadOnlyCollection<ControllerBinding>(copy); ProfileEvidence = profileEvidence;
    }
    public string AircraftPath { get; }
    public string Context { get; }
    public string ProfileEvidence { get; }
    public IReadOnlyList<ControllerBinding> Bindings { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.AircraftControls;
    internal override void Write(BinaryWriter writer)
    {
        RuntimeWire.WriteText(writer, AircraftPath, 4096); RuntimeWire.WriteText(writer, Context, 4096);
        writer.Write(Bindings.Count); foreach (var binding in Bindings) binding.Write(writer);
        ProfileEvidenceCodec.WriteOptional(writer, ProfileEvidence);
    }
    internal static AircraftControlsMessage Read(BinaryReader reader)
    {
        var path = RuntimeWire.ReadText(reader, 4096); var context = RuntimeWire.ReadText(reader, 4096);
        var count = reader.ReadInt32();
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 28)
            throw new InvalidDataException("Invalid aircraft binding count.");
        var bindings = new ControllerBinding[count];
        for (var i = 0; i < count; i++) bindings[i] = ControllerBinding.Read(reader);
        return new AircraftControlsMessage(path, context, bindings, ProfileEvidenceCodec.ReadOptional(reader));
    }
}

// The footer is optional so pre-evidence source messages and LCC2 catalogs remain readable.
internal static class ProfileEvidenceCodec
{
    private const uint Magic = 0x31464550; // PEF1: UTF-8 profile diagnostic evidence.
    internal static void WriteOptional(BinaryWriter writer, string evidence)
    {
        if (evidence.Length == 0) return;
        writer.Write(Magic); RuntimeWire.WriteText(writer, evidence, RuntimeWire.MaximumPayloadLength);
    }
    internal static string ReadOptional(BinaryReader reader)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length) return "";
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Unknown profile evidence extension.");
        return RuntimeWire.ReadText(reader, RuntimeWire.MaximumPayloadLength);
    }
}
