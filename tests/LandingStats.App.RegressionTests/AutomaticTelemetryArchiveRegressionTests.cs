using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class AutomaticTelemetryArchiveRegressionTests
{
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-bounded-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new BugReportRepository(root);
            var candidate = new BugReportCandidate(1,
                new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 } },
                "MSFS", "Fixture", "Airplane", "Fixture", Array.Empty<string>(), Array.Empty<LandingRecord>());
            var legacy = Path.Combine(root, "legacy_raw.zip");
            var manual = Path.Combine(root, "manual_bug_raw.zip");
            File.WriteAllBytes(legacy, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(manual, new byte[] { 4, 5, 6 });
            var first = AutomaticTelemetryArchive.TryCreate(repository, candidate, DateTime.UtcNow, maximumQueuedFiles: 1);
            Require(first != null && File.Exists(first), "First automatic diagnostic copy fits despite independent manual and legacy files.");
            var original = File.ReadAllBytes(first!);
            Require(AutomaticTelemetryArchive.TryCreate(repository, candidate, DateTime.UtcNow, maximumQueuedFiles: 1) == null,
                "Full count budget skips only the new automatic copy.");
            Require(File.ReadAllBytes(first!).SequenceEqual(original), "Budget must not alter an existing archive.");

            var exactLimit = original.LongLength + 1;
            Require(AutomaticTelemetryArchive.TryCreate(repository, candidate, DateTime.UtcNow, maximumQueueBytes: exactLimit) == null,
                "Compressed output must fit remaining bytes, not merely pass a pre-write count check.");
            Require(Directory.GetFiles(root, "*.tmp").Length == 0, "Oversized unpublished temporary archive is cleaned up.");
            Require(Directory.GetFiles(root, "*_auto_raw.zip").Length == 1, "Oversized new automatic archive is never published.");
            Require(File.ReadAllBytes(legacy).SequenceEqual(new byte[] { 1, 2, 3 }) &&
                    File.ReadAllBytes(manual).SequenceEqual(new byte[] { 4, 5, 6 }), "Manual and legacy files are untouched.");

            Parallel.For(0, 8, _ => AutomaticTelemetryArchive.TryCreate(repository, candidate, DateTime.UtcNow, maximumQueuedFiles: 3));
            Require(Directory.GetFiles(root, "*_auto_raw.zip").Length == 3, "Concurrent persistence cannot overrun automatic file-count budget.");
            File.Move(first!, first!.Substring(0, first.Length - 4) + ".rejected-422.zip");
            Require(AutomaticTelemetryArchive.TryCreate(repository, candidate, DateTime.UtcNow, maximumQueuedFiles: 3) == null,
                "Quarantined automatic diagnostics count towards retention budget too.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
