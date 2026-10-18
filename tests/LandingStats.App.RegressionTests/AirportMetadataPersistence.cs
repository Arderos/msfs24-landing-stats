using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;

// Historical fixture only. Production persistence is owned by the runtime.
namespace LandingStats.App.Storage;

internal static class AirportMetadataPersistence
{
    public static Task SaveAfterAsync(
        Task predecessor,
        AirportFacilityRepository airportRepository,
        object airportGate,
        LandingRepository landingRepository,
        object landingGate,
        IReadOnlyList<AirportFacility> facilities,
        IReadOnlyList<LandingRecord> resolvedSummaries)
    {
        // Take value snapshots before leaving the caller: history and selection
        // can change while this write waits behind an earlier facility event.
        var facilitySnapshot = facilities.Select(facility => new AirportFacility
        {
            Ident = facility.Ident,
            Region = facility.Region,
            LatitudeDegrees = facility.LatitudeDegrees,
            LongitudeDegrees = facility.LongitudeDegrees,
            AltitudeMeters = facility.AltitudeMeters,
        }).ToArray();
        var summarySnapshot = resolvedSummaries.Select(LandingRepository.CreateSummary).ToArray();
        return Task.Run(async () =>
        {
            try
            {
                await predecessor.ConfigureAwait(false);
            }
            catch
            {
                // A failed storage operation must not poison the next refresh.
            }

            try
            {
                lock (airportGate)
                {
                    airportRepository.MergeAndSave(facilitySnapshot);
                }
            }
            catch
            {
                // The in-memory cache remains useful when its optional on-disk
                // cache is unavailable, as it did for synchronous refreshes.
            }

            if (summarySnapshot.Length == 0) return;
            lock (landingGate)
            {
                var updates = summarySnapshot.ToDictionary(record => record.Id, StringComparer.Ordinal);
                var changed = new List<LandingRecord>();
                foreach (var current in landingRepository.LoadAll())
                {
                    if (!updates.TryGetValue(current.Id, out var update)) continue;
                    // A landing may have been deleted or acquired more precise
                    // runway metadata since this event. Only update still-present
                    // summaries, and never replace that newer airport identity.
                    var unresolved = string.Equals(current.Airport, "Unknown airport", StringComparison.OrdinalIgnoreCase);
                    var alreadyApplied = string.Equals(current.Airport, update.Airport, StringComparison.Ordinal) &&
                                         current.AirportDistanceNauticalMiles == update.AirportDistanceNauticalMiles;
                    if (!unresolved && !alreadyApplied) continue;
                    var summary = LandingRepository.CreateSummary(current);
                    summary.Airport = update.Airport;
                    summary.AirportDistanceNauticalMiles = update.AirportDistanceNauticalMiles;
                    changed.Add(summary);
                }
                // One index replacement for the entire event, not one per landing.
                landingRepository.UpdateSummaries(changed);
            }
        });
    }
}
