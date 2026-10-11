using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;

namespace LandingStats.App.Storage;

internal static class LandingSiteMetadataPersistence
{
    // Called by the report worker, which belongs to the durable shutdown boundary.
    // Update only metadata in the latest saved detail, never an old copy of its
    // telemetry and never a landing deleted while facilities were being fetched.
    public static void Save(LandingRepository repository, object gate, IReadOnlyList<LandingRecord> resolvedRecords)
    {
        lock (gate)
        {
            var summaries = repository.LoadAll().ToDictionary(record => record.Id, StringComparer.Ordinal);
            foreach (var resolved in resolvedRecords)
            {
                if (!summaries.TryGetValue(resolved.Id, out var summary) || !repository.Contains(resolved.Id)) continue;
                var current = repository.LoadDetail(summary);
                if (current == null) continue;
                // A same-record late ARP update is useful when both episode lookups
                // failed. Do not send Unknown after the local copy already resolved.
                if (string.Equals(resolved.Airport, "Unknown airport", StringComparison.OrdinalIgnoreCase))
                {
                    resolved.Airport = current.Airport;
                    resolved.AirportDistanceNauticalMiles = current.AirportDistanceNauticalMiles;
                }
                if (resolved.RunwayGeometry == null && current.RunwayGeometry != null &&
                    string.Equals(resolved.Airport, current.Airport, StringComparison.OrdinalIgnoreCase))
                {
                    resolved.RunwayGeometry = current.RunwayGeometry.Snapshot();
                    resolved.Runway = current.Runway;
                }
                if (current.Airport == resolved.Airport && current.Runway == resolved.Runway &&
                    current.AirportDistanceNauticalMiles == resolved.AirportDistanceNauticalMiles &&
                    current.RunwayGeometry == null && resolved.RunwayGeometry == null) continue;
                current.Airport = resolved.Airport;
                current.AirportDistanceNauticalMiles = resolved.AirportDistanceNauticalMiles;
                current.Runway = resolved.Runway;
                current.RunwayGeometry = resolved.RunwayGeometry?.Snapshot();
                repository.Save(current);
            }
        }
    }
}
