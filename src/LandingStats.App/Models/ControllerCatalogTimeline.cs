using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using LandingStats.App.Runtime;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Models;

/// <summary>Catalog transitions are keyed by recorded row, not an interpolated
/// simulation time. Empty catalog means unavailable; -1 retains unknown legacy provenance.</summary>
[DataContract]
public sealed class ControllerCatalogTimeline : IExtensibleDataObject
{
    public ExtensionDataObject? ExtensionData { get; set; }
    internal bool IsSupported => Version == 1;
    [DataMember(Name = "version", Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Name = "samples", Order = 2)] public int SampleCount { get; set; }
    [DataMember(Name = "catalogs", Order = 3)] public string[] Catalogs { get; set; } = Array.Empty<string>();
    [DataMember(Name = "transitions", Order = 4)] public int[][] Transitions { get; set; } = Array.Empty<int[]>();

    internal static ControllerCatalogTimeline? Capture(IReadOnlyList<TelemetrySample> samples)
    {
        if (!samples.Any(sample => sample.ControllerCatalog != null)) return null;
        var table = new Dictionary<byte[], int>(ControllerProvenanceCodec.BytesComparer.Instance);
        var references = new Dictionary<byte[], int>();
        var catalogs = new List<string>(); var transitions = new List<int[]>(); var previous = int.MinValue;
        for (var i = 0; i < samples.Count; i++)
        {
            var source = samples[i].ControllerCatalog;
            var index = -1;
            if (source != null && !references.TryGetValue(source, out index))
            {
                var bytes = source.Length == 0 || ControllerProvenanceCodec.IsFutureArchive(source)
                    ? source : ControllerProvenanceCodec.Encode(ControllerProvenanceCodec.Decode(source)!);
                if (!table.TryGetValue(bytes, out index))
                { index = catalogs.Count; table.Add(bytes, index); catalogs.Add(Convert.ToBase64String(bytes)); }
                references.Add(source, index);
            }
            if (previous != index) { transitions.Add(new[] { i, index }); previous = index; }
        }
        var value = new ControllerCatalogTimeline { SampleCount = samples.Count, Catalogs = catalogs.ToArray(), Transitions = transitions.ToArray() };
        value.Validate(samples.Count); return value;
    }
    internal byte[][] Validate(int sampleCount)
    {
        if (Version != 1 || SampleCount != sampleCount || sampleCount < 1 || sampleCount > 65536 ||
            Catalogs == null || Catalogs.Length > 4096 || Transitions == null || Transitions.Length < 1 || Transitions.Length > sampleCount)
            throw new InvalidDataException("Invalid controller timeline envelope.");
        var decoded = new byte[Catalogs.Length][]; var total = 0;
        for (var i = 0; i < Catalogs.Length; i++)
        {
            if (Catalogs[i] == null || Catalogs[i].Length > ((ControllerProvenanceCodec.MaximumCatalogBytes + 2L) / 3L) * 4L) throw new InvalidDataException("Controller catalog text exceeds its bound.");
            try { decoded[i] = Convert.FromBase64String(Catalogs[i]); }
            catch (FormatException error) { throw new InvalidDataException("Invalid controller catalog encoding.", error); }
            ControllerProvenanceCodec.Decode(decoded[i]); total = checked(total + decoded[i].Length);
            if (total > ControllerProvenanceCodec.MaximumTableBytes) throw new InvalidDataException("Controller timeline exceeds its metadata bound.");
        }
        var previous = -1;
        foreach (var transition in Transitions)
        {
            if (transition == null || transition.Length != 2 || transition[0] <= previous || transition[0] >= sampleCount ||
                previous == -1 && transition[0] != 0 || transition[1] < -1 || transition[1] >= Catalogs.Length)
                throw new InvalidDataException("Invalid controller timeline transition.");
            previous = transition[0];
        }
        return decoded;
    }
    internal void Apply(IReadOnlyList<TelemetrySample> samples)
    {
        if (Version > 1) return;
        var decoded = Validate(samples.Count); var transition = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            if (transition + 1 < Transitions.Length && Transitions[transition + 1][0] == i) transition++;
            var index = Transitions[transition][1]; samples[i].ControllerCatalog = index < 0 ? null : decoded[index];
        }
    }
    internal ControllerCatalogTimeline Copy() => new ControllerCatalogTimeline
    { Version = Version, SampleCount = SampleCount, Catalogs = Catalogs == null ? null! : (string[])Catalogs.Clone(),
        Transitions = Transitions == null ? null! : Transitions.Select(value => value == null ? null! : (int[])value.Clone()).ToArray(), ExtensionData = ExtensionData };

    internal static List<string> Labels(RuntimeEnvelope? catalog, bool legacyDescriptions = false)
    {
        var count = (catalog?.Payload as ControllerCatalogMessage)?.DeviceCount ?? TelemetrySample.CapturedControllerCount;
        var labels = Enumerable.Repeat(string.Empty, count).ToList();
        if (catalog?.Payload is ControllerCatalogMessage value)
            foreach (var device in value.Devices)
            {
                var axes = Enumerable.Range(0, device.AxisCount).Count(device.Supports);
                labels[device.Slot] = device.InstanceId != Guid.Empty ? device.Name + " [DirectInput " + device.InstanceId + "]" : legacyDescriptions
                    ? "winmm:" + device.DeviceId + ": " + device.Name + " (" + axes + (axes == 1 ? " axis)" : " axes)")
                    : device.Name + " [WinMM " + device.DeviceId + "]";
            }
        while (labels.Count > 0 && labels[labels.Count - 1].Length == 0) labels.RemoveAt(labels.Count - 1);
        return labels;
    }
}
