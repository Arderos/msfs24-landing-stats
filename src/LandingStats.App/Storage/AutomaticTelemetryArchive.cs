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
        int maximumQueuedFiles = MaximumQueuedFiles,
        Guid archiveId = default,
        bool throwOnStorageFailure = false)
    {
        if (repository == null) throw new ArgumentNullException(nameof(repository));
        if (maximumQueueBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumQueueBytes));
        if (maximumQueuedFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maximumQueuedFiles));
        lock (Gate)
        {
            try
            {
                if (archiveId != Guid.Empty)
                {
                    var existing = repository.ExistingAutomatic(timestampUtc, archiveId);
                    if (existing != null) return existing;
                }
                var availableBytes = maximumQueueBytes;
                var fileCount = 0;
                if (Directory.Exists(repository.RootPath))
                {
                    // Quarantined evidence remains on disk, but no longer
                    // consumes the queue's slots for new deliverable captures.
                    foreach (var path in Directory.EnumerateFiles(repository.RootPath, "*_auto_raw*.zip", SearchOption.TopDirectoryOnly))
                    {
                        if (Path.GetFileName(path).IndexOf(".rejected-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        long length;
                        try { length = new FileInfo(path).Length; }
                        catch (FileNotFoundException) { continue; } // The upload worker completed this file.
                        catch (DirectoryNotFoundException) { continue; }
                        if (++fileCount >= maximumQueuedFiles || length >= availableBytes) return null;
                        availableBytes -= length;
                    }
                }
                return repository.TryCreateAutomatic(candidate, timestampUtc, availableBytes, archiveId);
            }
            catch (IOException) when (!throwOnStorageFailure) { return null; }
            catch (UnauthorizedAccessException) when (!throwOnStorageFailure) { return null; }
        }
    }
}
