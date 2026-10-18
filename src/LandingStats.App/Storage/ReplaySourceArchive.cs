using System.Collections.Generic;
using System.IO;
using LandingStats.Archives;
using LandingStats.Core;

namespace LandingStats.App.Storage;

internal static class ReplaySourceArchive
{
    internal const string EntryName = "source-state.bin";
    internal static void Write(CaptureArchiveWriter archive, IReadOnlyList<TelemetrySample> samples)
    {
        var bytes = ReplaySourceTimelineCodec.Encode(samples); if (bytes == null) return;
        using var output = archive.CreateEntry(EntryName); output.Write(bytes, 0, bytes.Length);
    }
}
