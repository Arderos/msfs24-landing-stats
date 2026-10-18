using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.Archives;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

// Inspection helpers for production archives. CaptureArchiveReader exercises
// both ZIP and format 2, unlike the removed production-dead ZIP-only reader.
internal static class CaptureEvidenceFixture
{
    internal static bool ReadControllers(CaptureArchiveReader archive, IReadOnlyList<TelemetrySample> samples)
    {
        var bytes = Read(archive, ControllerCatalogArchive.EntryName, ControllerCatalogArchive.MaximumBytes);
        if (bytes == null) return false;
        using var input = new MemoryStream(bytes, false);
        var timeline = (ControllerCatalogTimeline?)new DataContractJsonSerializer(typeof(ControllerCatalogTimeline)).ReadObject(input)
            ?? throw new InvalidDataException("Missing controller timeline.");
        timeline.Apply(samples); return true;
    }
    internal static bool ReadSource(CaptureArchiveReader archive, IReadOnlyList<TelemetrySample> samples)
    {
        var bytes = Read(archive, ReplaySourceArchive.EntryName, ReplaySourceTimelineCodec.MaximumBytes);
        if (bytes == null) return false;
        ReplaySourceTimelineCodec.Apply(bytes, samples); return true;
    }
    private static byte[]? Read(CaptureArchiveReader archive, string name, int maximum)
    {
        var entry = archive.GetEntry(name); if (entry == null) return null;
        if (entry.Length > maximum) throw new InvalidDataException("Archive metadata exceeds its bound.");
        using var input = archive.Open(name); using var output = new MemoryStream();
        var block = new byte[8192]; int read;
        while ((read = input.Read(block, 0, block.Length)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Archive metadata exceeds its bound.");
            output.Write(block, 0, read);
        }
        return output.ToArray();
    }
}
