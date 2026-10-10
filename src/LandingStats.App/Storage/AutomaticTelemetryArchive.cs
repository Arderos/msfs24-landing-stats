using System;
using System.IO;

namespace LandingStats.App.Storage;

internal static class AutomaticTelemetryArchive
{
    internal const long MaximumQueueBytes = 256L * 1024 * 1024;
    internal const int MaximumQueuedFiles = 128;
    private static readonly object Gate = new object();

    // Run on the persistence worker, never the UI thread. Keep every existing
    // report and normal landing file; a full offline queue skips only the new
    // automatic diagnostic copy. Manual reporting remains independent.
    public static string? TryCreate(
        BugReportRepository repository,
        BugReportCandidate candidate,
        DateTime timestampUtc,
        long maximumQueueBytes = MaximumQueueBytes,
        int maximumQueuedFiles = MaximumQueuedFiles)
    {
        if (repository == null) throw new ArgumentNullException(nameof(repository));
        if (maximumQueueBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumQueueBytes));
        if (maximumQueuedFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maximumQueuedFiles));
        lock (Gate)
        {
            try
            {
                var availableBytes = maximumQueueBytes;
                var fileCount = 0;
                if (Directory.Exists(repository.RootPath))
                {
                    // Rejected automatic diagnostics still occupy disk space.
                    // Include their quarantine names in the same retention budget.
                    foreach (var path in Directory.EnumerateFiles(repository.RootPath, "*_auto_raw*.zip", SearchOption.TopDirectoryOnly))
                    {
                        long length;
                        try { length = new FileInfo(path).Length; }
                        catch (FileNotFoundException) { continue; } // The upload worker completed this file.
                        catch (DirectoryNotFoundException) { continue; }
                        if (++fileCount >= maximumQueuedFiles || length >= availableBytes) return null;
                        availableBytes -= length;
                    }
                }
                return repository.TryCreateAutomatic(candidate, timestampUtc, availableBytes);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
