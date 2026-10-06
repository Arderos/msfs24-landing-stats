using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class RunwayMetadataPersistenceRegressionTests
{
    public static void RunAll()
    {
        SnapshotAndReloadPreserveAcceptedAndNewerValues();
        FailedPredecessorDoesNotPoisonLaterMetadata();
        RepositoryGateNeverBlocksTheDispatcher();
    }

    private static void SnapshotAndReloadPreserveAcceptedAndNewerValues()
    {
        using var fixture = new Fixture();
        var record = Record();
        var deleted = Record();
        var newerRunway = Record();
        fixture.Repository.Save(record);
        fixture.Repository.Save(deleted);
        fixture.Repository.Save(newerRunway);
        var runway = Runway();
        var facilities = new[] { runway };
        var ids = new[] { record.Id, deleted.Id, newerRunway.Id };
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = RunwayMetadataPersistence.SaveAfterAsync(release.Task,
            fixture.Repository, fixture.Gate, ids, facilities);
        fixture.Pending.Add(save);
        try
        {
            Assert(!save.IsCompleted, "Queuing metadata returns before a delayed predecessor completes.");
            ids[0] = "changed-after-enqueue";
            runway.WidthMeters = 120;
            runway.PrimaryName = "CHANGED";
            runway.PrimaryPapi!.AngleDegrees = 8;
            runway.SecondaryPapi!.StationFromEndMeters = 800;
            runway.PrimaryIlsGlideSlope!.Ident = "CHANGED";
            runway.SecondaryIlsGlideSlope!.AngleDegrees = 8;
            facilities[0] = new RunwayFacility();

            lock (fixture.Gate)
            {
                // Simulate a newer cloud/detail save while this optional update
                // waits. The worker must enrich that version, not its old DTO.
                record.AircraftTitle = "Newer aircraft value";
                record.InertialFpm = -987;
                record.Series[0].PitchDegrees = 7.5;
                fixture.Repository.Save(record);
                fixture.Repository.Delete(deleted.Id);
                newerRunway.Airport = "NEWER";
                newerRunway.Runway = "NEW";
                newerRunway.RunwayGeometry = Runway();
                newerRunway.RunwayGeometry.AirportIdent = "NEWER";
                newerRunway.RunwayGeometry.WidthMeters = 66;
                newerRunway.RunwayGeometry.PrimaryPapi!.AngleDegrees = 3.25;
                fixture.Repository.Save(newerRunway);
            }
            release.SetResult(true);
            Assert(save.Wait(TimeSpan.FromSeconds(10)), "Queued runway metadata completes.");
            Assert(save.Result.Count == 1 && save.Result[0].Id == record.Id,
                "Only the undeleted record without newer geometry is enriched.");

            var freshRepository = new LandingRepository(fixture.Repository.RootPath);
            var summaries = freshRepository.LoadAll();
            Assert(summaries.Count == 2 && summaries.All(value => value.Id != deleted.Id),
                "A record deleted while waiting is not recreated in detail or index.");
            Assert(!freshRepository.Contains(deleted.Id), "Deleted detail remains absent.");
            var stored = freshRepository.LoadDetail(summaries.Single(value => value.Id == record.Id))!;
            Assert(stored.AircraftTitle == "Newer aircraft value" && stored.InertialFpm == -987 &&
                stored.Series[0].PitchDegrees == 7.5, "Reloading preserves newer non-runway detail fields.");
            Assert(stored.Runway == "09" && stored.RunwayGeometry!.WidthMeters == 45,
                "Record ids, facility list and scalar geometry are captured before queuing.");
            Assert(stored.RunwayGeometry!.PrimaryPapi!.AngleDegrees == 3 &&
                stored.RunwayGeometry.SecondaryPapi!.StationFromEndMeters == 320 &&
                stored.RunwayGeometry.PrimaryIlsGlideSlope!.Ident == "ITST" &&
                stored.RunwayGeometry.SecondaryIlsGlideSlope!.AngleDegrees == 3.1,
                "Nested PAPI and ILS metadata use an independent enqueue-time snapshot.");
            var kept = freshRepository.LoadDetail(summaries.Single(value => value.Id == newerRunway.Id))!;
            Assert(kept.Airport == "NEWER" && kept.Runway == "NEW" && kept.RunwayGeometry!.WidthMeters == 66 &&
                kept.RunwayGeometry.PrimaryPapi!.AngleDegrees == 3.25,
                "A delayed update cannot replace newer stored runway geometry or derived airport fields.");
        }
        finally { release.TrySetResult(true); }
    }

    private static void FailedPredecessorDoesNotPoisonLaterMetadata()
    {
        using var fixture = new Fixture();
        var record = Record();
        fixture.Repository.Save(record);
        var save = RunwayMetadataPersistence.SaveAfterAsync(
            Task.FromException(new IOException("Synthetic predecessor failure.")),
            fixture.Repository, fixture.Gate, new[] { record.Id }, new[] { Runway() });
        fixture.Pending.Add(save);
        Assert(save.Wait(TimeSpan.FromSeconds(10)) && save.Result.Count == 1,
            "A faulted predecessor does not poison the next runway save.");
        Assert(new LandingRepository(fixture.Repository.RootPath).LoadAll().Single().Runway == "09",
            "Recovered metadata reaches durable storage.");
    }

    private static void RepositoryGateNeverBlocksTheDispatcher()
    {
        using var fixture = new Fixture();
        var record = Record();
        fixture.Repository.Save(record);
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        var blocker = Task.Run(() =>
        {
            lock (fixture.Gate)
            {
                gateHeld.Set();
                Assert(releaseGate.Wait(TimeSpan.FromSeconds(10)), "Test gate must be explicitly released.");
            }
        });
        fixture.Pending.Add(blocker);
        try
        {
            Assert(gateHeld.Wait(TimeSpan.FromSeconds(5)), "Synthetic writer holds the repository gate.");
            var clock = Stopwatch.StartNew();
            var save = RunwayMetadataPersistence.SaveAfterAsync(Task.CompletedTask,
                fixture.Repository, fixture.Gate, new[] { record.Id }, new[] { Runway() });
            fixture.Pending.Add(save);
            Assert(clock.Elapsed < TimeSpan.FromSeconds(1) && !save.IsCompleted,
                "The UI-side call returns without acquiring the busy repository gate.");
            var heartbeat = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => heartbeat = true));
            PumpUntil(() => heartbeat);
            Assert(!save.IsCompleted, "Dispatcher heartbeat runs while storage remains blocked.");
            releaseGate.Set();
            Assert(save.Wait(TimeSpan.FromSeconds(10)) && save.Result.Count == 1,
                "Persistence completes on its worker without requiring dispatcher continuation.");
        }
        finally { releaseGate.Set(); }
    }

    private static RunwayFacility Runway() => new RunwayFacility
    {
        AirportIdent = "TEST", AirportRegion = "TT", LatitudeDegrees = 0, LongitudeDegrees = 0,
        AltitudeMeters = 100, HeadingTrueDegrees = 90, LengthMeters = 3000, WidthMeters = 45,
        PrimaryName = "09", SecondaryName = "27",
        PrimaryPapiQueried = true, SecondaryPapiQueried = true,
        PrimaryPapi = new RunwayPapi { StationFromEndMeters = 300, AngleDegrees = 3 },
        SecondaryPapi = new RunwayPapi { StationFromEndMeters = 320, AngleDegrees = 3.1 },
        PrimaryIlsGlideSlope = new RunwayGlideSlope { Ident = "ITST", Region = "TT", AngleDegrees = 3, AltitudeMeters = 100 },
        SecondaryIlsGlideSlope = new RunwayGlideSlope { Ident = "ITSR", Region = "TT", AngleDegrees = 3.1, AltitudeMeters = 101 },
    };

    private static LandingRecord Record()
    {
        var record = new LandingRecord { TouchdownLatitudeDegrees = 0, TouchdownLongitudeDegrees = 0 };
        for (var index = 0; index <= 6; index++)
        {
            var time = -1 + index * .25;
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = time, LatitudeDegrees = 0,
                LongitudeDegrees = time * 60 / 111195,
                RawControllerYAxisPercent = Array.Empty<double>(),
                RawControllerYAxisValid = Array.Empty<bool>(),
                RawControllerYAxisAgeSeconds = Array.Empty<double>() });
        }
        return record;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, __) => { if (condition() || clock.Elapsed.TotalSeconds > 5) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert(condition(), "Dispatcher heartbeat timed out.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "runway-metadata-test-" + Guid.NewGuid().ToString("N"));
            Repository = new LandingRepository(Path.Combine(Root, "landings"));
        }
        public string Root { get; }
        public LandingRepository Repository { get; }
        public object Gate { get; } = new object();
        public List<Task> Pending { get; } = new List<Task>();
        public void Dispose()
        {
            try { Task.WhenAll(Pending).GetAwaiter().GetResult(); }
            catch { /* Preserve the original assertion or persistence failure. */ }
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
