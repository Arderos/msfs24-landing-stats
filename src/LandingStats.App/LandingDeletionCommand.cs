using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;

namespace LandingStats.App;

internal sealed class LandingDeletionResult
{
    public List<string> DeletedIds { get; } = new List<string>();
    public List<Exception> LocalErrors { get; } = new List<Exception>();
    public List<Exception> QueueErrors { get; } = new List<Exception>();
}

internal static class LandingDeletionCommand
{
    public static Task<LandingDeletionResult> ExecuteAsync(GoogleDriveBackupService backup, IEnumerable<string> ids)
    {
        var snapshot = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("A landing id is required.", nameof(ids));
        return Task.Run(async () =>
        {
            var result = new LandingDeletionResult();
            foreach (var id in snapshot)
            {
                try { backup.DeleteLandingLocally(id); }
                catch (Exception error) { result.LocalErrors.Add(error); continue; }
                // Each tombstone commits independently. A later failure must not
                // restore earlier contacts or cancel their cloud deletion intent.
                result.DeletedIds.Add(id);
                try { await backup.QueueLandingDeletionAsync(id, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { result.QueueErrors.Add(error); }
            }
            return result;
        });
    }
}
