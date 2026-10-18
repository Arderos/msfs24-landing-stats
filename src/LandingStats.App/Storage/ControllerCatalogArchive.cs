using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using LandingStats.App.Models;
using LandingStats.Core;
using LandingStats.Archives;

namespace LandingStats.App.Storage;

internal static class ControllerCatalogArchive
{
    internal const string EntryName = "controller-catalogs.json";
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal static void Write(CaptureArchiveWriter archive, IReadOnlyList<TelemetrySample> samples) => Write(name => archive.CreateEntry(name), samples);
    private static void Write(Func<string, Stream> create, IReadOnlyList<TelemetrySample> samples)
    {
        var timeline = ControllerCatalogTimeline.Capture(samples); if (timeline == null) return;
        using var buffer = new MemoryStream();
        new DataContractJsonSerializer(typeof(ControllerCatalogTimeline)).WriteObject(buffer, timeline);
        if (buffer.Length > MaximumBytes) throw new InvalidDataException("Controller archive metadata exceeds its bound.");
        using var output = create(EntryName); buffer.Position = 0; buffer.CopyTo(output);
    }
}
