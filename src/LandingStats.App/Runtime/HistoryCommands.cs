using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;

namespace LandingStats.App.Runtime;

// All history refresh/migration commands share the same repository writer gate
// as episode and cloud owners. Presentation receives detached summaries/details.
internal static class HistoryCommands
{
    public static LandingRecord[] LoadSummaries(LandingRepository repository, object gate,
        IReadOnlyList<AirportFacility> facilities, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = repository.LoadAll().Select(LandingRepository.CreateSummary).ToArray();
            repository.UpdateSummaries(records.Where(record => AirportMetadata.TryResolve(record, facilities)).ToArray());
            return records;
        }
    }
    public static IReadOnlyList<LandingRecord> LoadDetails(LandingRepository repository, object gate,
        IReadOnlyList<LandingRecord> summaries, string selectedId, IReadOnlyDictionary<string, LandingRecord> cached,
        ISet<string> loadedFromStorage, CancellationToken cancellationToken)
    {
        var details = new List<LandingRecord>();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = repository.LoadAll().ToDictionary(record => record.Id, StringComparer.Ordinal);
            foreach (var summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cached.TryGetValue(summary.Id, out var saved)) { details.Add(saved); continue; }
                if (!summary.IsSummaryOnly) { details.Add(summary); continue; }
                // Read the latest committed summary inside the writer gate, so
                // migration cannot roll back a newer cloud/scenery change.
                var detail = stored.TryGetValue(summary.Id, out var current) ? repository.LoadDetail(current) : null;
                if (detail == null || detail.IsSummaryOnly)
                { if (summary.Id == selectedId) throw new IOException("The landing detail could not be read."); continue; }
                loadedFromStorage.Add(detail.Id);
                if (detail.FormatVersion >= 6 && LandingRecordFactory.RefreshRawPitchInputSelection(detail) && repository.Contains(detail.Id))
                    repository.Save(detail);
                details.Add(detail);
            }
        }
        return details;
    }
}
