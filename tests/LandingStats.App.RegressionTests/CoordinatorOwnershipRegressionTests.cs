using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class CoordinatorOwnershipRegressionTests
{
    public static void RunAll() => Task.Run(() =>
    {
        // No WPF dispatcher, SimConnect, WinMM or real filesystem is needed by these owners.
        var query = new Query();
        var facilities = new EpisodeFacilityCoordinator(query);
        var now = 10.0;
        var coordinator = new LandingEpisodeCoordinator(facilities, () => now, () => Array.Empty<string>());
        var completed = new List<LandingEpisodeEventArgs>();
        coordinator.EpisodeCompleted += (_, args) => completed.Add(args);
        coordinator.SetSdkSimRunning(true);
        coordinator.ProcessSample(Frame(now, false));
        now += .1;
        coordinator.ProcessSample(Frame(now, false));
        var first = facilities.SnapshotTask!;
        Assert(!first.IsCompleted && facilities.EpisodeId == 1, "approach did not own a pending query");
        var runway = new RunwayFacility { AirportIdent = "APP", WidthMeters = 45, LengthMeters = 2000 };
        query.Progress = new SceneryQuerySnapshot(new[] { runway }, new[] { new AirportFacility { Ident = "APP" } }, true, true, 1, 1);
        facilities.FinishRunwayLookupStage(query.Progress.Runways);
        Assert(!first.IsCompleted, "preliminary stage completed final promise");
        now += .1;
        coordinator.ProcessSample(Frame(now, true));
        coordinator.SetSdkPauseFlags(4);
        now += 30;
        coordinator.ProcessSample(Frame(now, true));
        Assert(coordinator.HasContact && coordinator.IsRecording && completed.Count == 0, "pause lost accepted contact");
        coordinator.Stop();
        Assert(completed.Count == 1 && completed[0].EpisodeId == 1 && first.IsCompleted, "stop lost contact or left facility task pending");
        Assert(first.Result.PreliminaryRunways.Single().WidthMeters == 45 && !first.Result.FinalLookupCompleted, "fallback changed provenance");
        runway.WidthMeters = 999;
        query.Progress = Empty;
        facilities.BeginEpisode(2);
        Assert(first.Result.PreliminaryRunways.Single().WidthMeters == 45, "replacement query mutated completed evidence");
        facilities.CompleteWithoutRefresh("new source unavailable");
        Assert(facilities.SnapshotTask!.IsCompleted && facilities.SnapshotTask.Result.PreliminaryRunways.Count == 0, "new episode borrowed old geometry");
        coordinator.Stop();
        Assert(completed.Count == 1, "duplicate shutdown completion");
        try { coordinator.ProcessSample(Frame(now + 1, false)); throw new Exception("stopped owner accepted data"); }
        catch (ObjectDisposedException) { }

        var noContactFacilities = new EpisodeFacilityCoordinator(new Query());
        var approach = new LandingEpisodeCoordinator(noContactFacilities, () => 1, () => Array.Empty<string>());
        var rejected = 0;
        approach.EpisodeCompleted += (_, _) => rejected++;
        approach.ProcessSample(Frame(1, false));
        approach.Stop();
        Assert(rejected == 0 && noContactFacilities.SnapshotTask!.IsCompleted, "stop of approach uploaded a fabricated landing or leaked a query");
    }).GetAwaiter().GetResult();

    private static TelemetrySample Frame(double time, bool ground) => new TelemetrySample
    {
        Sequence = (long)(time * 100), HostElapsedSeconds = time, SimulationTimeSeconds = time,
        SimulationDeltaSeconds = .1, MotionSimulation = true, OnGround = ground, AboveGroundLevelFeet = ground ? 0 : 100,
        VelocityWorldYFps = ground ? 0 : -3, GForce = 1, LatitudeDegrees = 42, LongitudeDegrees = 23,
    };
    private static SceneryQuerySnapshot Empty => new SceneryQuerySnapshot(Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false, false, 0, 0);
    private sealed class Query : ISceneryQueryPort
    {
        public SceneryQuerySnapshot Progress = Empty;
        public bool CanQuery => true;
        public IReadOnlyList<AirportFacility> CachedAirports => Array.Empty<AirportFacility>();
        public SceneryQuerySnapshot Snapshot => Progress;
        public void Request(double latitude, double longitude) { }
        public void Reset() { Progress = Empty; }
        // Simulate a source which destroys its volatile query cache on cancellation.
        public void Cancel(string reason) { Progress = Empty; }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
