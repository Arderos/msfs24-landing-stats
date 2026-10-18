using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;

// Historical fixture only. Production persistence is owned by the runtime.
namespace LandingStats.App.Storage;

internal static class RunwayMetadataPersistence
{
    public static Task<IReadOnlyList<LandingRecord>> SaveAfterAsync(Task predecessor,
        LandingRepository repository, object gate, IEnumerable<string> recordIds,
        IReadOnlyList<RunwayFacility> facilities)
    {
        var ids = new HashSet<string>(recordIds, StringComparer.Ordinal);
        var snapshot = facilities.Select(runway => runway.Snapshot()).ToArray();
        return Task.Run<IReadOnlyList<LandingRecord>>(async () =>
        {
            try { await predecessor.ConfigureAwait(false); }
            catch { /* An optional metadata failure must not poison the queue. */ }
            if (snapshot.Length == 0 || ids.Count == 0) return Array.Empty<LandingRecord>();
            var updated = new List<LandingRecord>();
            lock (gate)
            {
                // Reload under the same gate as deletion/cloud writes. Never save
                // a stale UI object or recreate a landing deleted while waiting.
                foreach (var summary in repository.LoadAll().Where(record => ids.Contains(record.Id)))
                {
                    if (summary.RunwayGeometry != null || !repository.Contains(summary.Id)) continue;
                    var detail = repository.LoadDetail(summary);
                    if (detail == null || !RunwayResolver.TryApply(detail, snapshot)) continue;
                    repository.Save(detail);
                    updated.Add(detail);
                }
            }
            return updated;
        });
    }
}
