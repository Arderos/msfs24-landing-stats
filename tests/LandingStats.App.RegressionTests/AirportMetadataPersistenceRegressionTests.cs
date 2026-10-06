using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class AirportMetadataPersistenceRegressionTests
{
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "airport-metadata-test-" + Guid.NewGuid().ToString("N"));
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new List<Task>();
        try
        {
            var airports = new AirportFacilityRepository(Path.Combine(root, "airports.json.gz"));
            var landings = new LandingRepository(Path.Combine(root, "landings"));
            var airportGate = new object();
            var landingGate = new object();
            var firstRecord = new LandingRecord();
            var deletedRecord = new LandingRecord();
            var runwayRecord = new LandingRecord();
            landings.Save(firstRecord);
            landings.Save(deletedRecord);
            landings.Save(runwayRecord);

            // Mirror the UI: these summaries are already resolved in memory, but
            // their index has not been written yet. Persist them once as a batch.
            var resolved = landings.LoadAll().ToArray();
            foreach (var record in resolved)
            {
                record.Airport = "LBSF";
                record.AirportDistanceNauticalMiles = 2;
            }
            var oldFacility = new AirportFacility { Ident = "LBSF", LatitudeDegrees = 42.5 };
            var first = AirportMetadataPersistence.SaveAfterAsync(
                release.Task, airports, airportGate, landings, landingGate,
                new[] { oldFacility }, resolved);
            pending.Add(first);
            Assert(!first.IsCompleted, "queued persistence does not block its caller waiting for a predecessor");
            oldFacility.LatitudeDegrees = -1;
            foreach (var record in resolved) record.Airport = "Caller changed after enqueue";

            // The delayed write must respect local deletion and newer runway
            // metadata, rather than repopulating an outdated summary snapshot.
            landings.Delete(deletedRecord.Id);
            runwayRecord.Airport = "NEWER";
            runwayRecord.Runway = "09";
            runwayRecord.RunwayGeometry = new RunwayFacility
            {
                AirportIdent = "NEWER", PrimaryName = "09", SecondaryName = "27",
                LengthMeters = 2500, WidthMeters = 45, HeadingTrueDegrees = 90,
            };
            landings.Save(runwayRecord);
            var firstSummary = landings.LoadAll().Single(record => record.Id == firstRecord.Id);
            firstSummary.Airport = "Unknown airport";
            firstSummary.AirportDistanceNauticalMiles = null;
            release.SetResult(true);
            Assert(first.Wait(TimeSpan.FromSeconds(10)), "queued persistence finishes without a dispatcher pump");

            var restored = new LandingRepository(landings.RootPath).LoadAll();
            Assert(restored.Count == 2 && restored.All(record => record.Id != deletedRecord.Id), "deleted landing is not recreated");
            Assert(restored.Single(record => record.Id == firstRecord.Id).Airport == "LBSF", "snapshot survives later caller mutation");
            var newer = restored.Single(record => record.Id == runwayRecord.Id);
            Assert(newer.Airport == "NEWER" && newer.Runway == "09" && newer.RunwayGeometry != null,
                "newer runway-derived metadata survives a delayed airport update");
            Assert(airports.Load().Single().LatitudeDegrees == 42.5, "facility data is snapshotted before queuing");

            var older = AirportMetadataPersistence.SaveAfterAsync(
                queueRelease.Task, airports, airportGate, landings, landingGate,
                new[] { new AirportFacility { Ident = "LBSF", LatitudeDegrees = 43 } }, Array.Empty<LandingRecord>());
            pending.Add(older);
            var latest = AirportMetadataPersistence.SaveAfterAsync(
                older, airports, airportGate, landings, landingGate,
                new[] { new AirportFacility { Ident = "LBSF", LatitudeDegrees = 44 } }, Array.Empty<LandingRecord>());
            pending.Add(latest);
            queueRelease.SetResult(true);
            Assert(latest.Wait(TimeSpan.FromSeconds(10)), "ordered queue finishes");
            Assert(airports.Load().Single().LatitudeDegrees == 44, "newest queued facility wins");

            var failure = Task.FromException(new IOException("Injected predecessor failure"));
            var recovered = AirportMetadataPersistence.SaveAfterAsync(
                failure, airports, airportGate, landings, landingGate,
                new[] { new AirportFacility { Ident = "LBSF", LatitudeDegrees = 45 } }, Array.Empty<LandingRecord>());
            pending.Add(recovered);
            Assert(recovered.Wait(TimeSpan.FromSeconds(10)), "failed predecessor does not poison later persistence");
            Assert(airports.Load().Single().LatitudeDegrees == 45, "later metadata is durable after predecessor failure");

            // Also cover the common shared-summary case: updating memory before
            // queuing must still result in a durable index replacement.
            var shared = landings.LoadAll().Single(record => record.Id == firstRecord.Id);
            shared.Airport = "DURABLE";
            shared.AirportDistanceNauticalMiles = 3;
            var alreadyApplied = AirportMetadataPersistence.SaveAfterAsync(
                Task.CompletedTask, airports, airportGate, landings, landingGate,
                Array.Empty<AirportFacility>(), new[] { shared });
            pending.Add(alreadyApplied);
            Assert(alreadyApplied.Wait(TimeSpan.FromSeconds(10)), "shared-summary persistence finishes");
            Assert(new LandingRepository(landings.RootPath).LoadAll().Single(record => record.Id == firstRecord.Id).Airport == "DURABLE",
                "already-applied in-memory airport is written to the index");
        }
        finally
        {
            release.TrySetResult(true);
            queueRelease.TrySetResult(true);
            try { Task.WhenAll(pending).GetAwaiter().GetResult(); }
            catch { /* Preserve the original assertion or persistence failure. */ }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
