using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.RegressionTests;

internal static class TwoStageRecorderRegressionTests
{
    public static void RunAll()
    {
        PreliminaryDoesNotCompleteTheFinalPromise();
        FinalStageIsFreshAndKeepsBothResults();
        AirportIdentitySurvivesRunwayFailure();
        MenuExitSalvagesThisApproachBeforeCallback();
        PauseRetainsTheApproachAndItsGeometry();
        FlightChangeCancelsPendingFinalReplies();
        SupersessionCannotBorrowPreviousGeometry();
        OptionalPendingMetadataDoesNotLoseCompleteBaseGeometry();
        LegacyEventConstructorRemainsCompatible();
        InitialSnapshotIsAvailableWithoutCompletingTheFinalTask();
        ApproachSamplesDeduplicateWithoutCrossingPhysicsBarriers();
        FinalRequestUsesLastContactAndFallsBackOnFreshLookupFailure();
        FinalBaseSurvivesExitDuringOptionalMetadata();
        FailedNeighborKeepsCompleteAirportRepliesWithoutClaimingUniqueness();
        FacilityDiagnosticsAreBoundedAndEpisodeScoped();
    }

    private static void PreliminaryDoesNotCompleteTheFinalPromise()
    {
        using var recorder = NewEpisode(10);
        var task = Promise(recorder).Task;
        Publish(recorder, Airport("APP"), Runway("APP"));
        Assert(!task.IsCompleted, "a preliminary response completed the final episode promise");
        Call(recorder, "CompleteRunwayLookupWithoutRefresh", "menu exit");
        var result = task.GetAwaiter().GetResult();
        Assert(!result.FinalLookupCompleted && result.PreliminaryRunways.Single().AirportIdent == "APP" &&
            result.PreliminaryAirports.Single().Ident == "APP" && result.FinalRunways.Count == 0,
            "no-refresh completion lost the preliminary snapshot or presented it as final");
    }

    private static void FinalStageIsFreshAndKeepsBothResults()
    {
        using var recorder = NewEpisode(20);
        Publish(recorder, Airport("APP"), Runway("APP"));
        Call(recorder, "PreservePreliminaryFacilities");
        Set(recorder, "_runwayLookupIsPreliminary", false);
        Call(recorder, "ResetRunwayLookupStage");
        Assert(Cache(recorder).Count == 0 && recorder.RunwayFacilities.Count == 0,
            "the final lookup reused preliminary runway cache entries");
        var final = Runway("ACTUAL");
        Publish(recorder, Airport("ACTUAL"), final);
        var result = Promise(recorder).Task.GetAwaiter().GetResult();
        Assert(result.FinalLookupCompleted && result.PreliminaryRunways.Single().AirportIdent == "APP" &&
            result.FinalRunways.Single().AirportIdent == "ACTUAL" && result.FinalAirports.Single().Ident == "ACTUAL",
            "a contradictory final result replaced or lost the separately retained approach evidence");
        final.WidthMeters = 999;
        Assert(result.FinalRunways.Single().WidthMeters == 45, "a late mutable native result changed a completed snapshot");
    }

    private static void AirportIdentitySurvivesRunwayFailure()
    {
        using var recorder = NewEpisode(30);
        Publish(recorder, Airport("APP"), Runway("APP"));
        Set(recorder, "_runwayLookupIsPreliminary", false);
        Call(recorder, "ResetRunwayLookupStage");
        Set(recorder, "_stageAirports", new[] { Airport("ACTUAL") });
        Set(recorder, "_runwayFreshAirportsReceived", true);
        Call(recorder, "FinishRunwayLookupStage", (object)Array.Empty<RunwayFacility>());
        var result = Promise(recorder).Task.GetAwaiter().GetResult();
        Assert(result.FinalLookupCompleted && result.FinalRunways.Count == 0 &&
            result.FinalAirports.Single().Ident == "ACTUAL" && result.PreliminaryRunways.Count == 1,
            "runway failure discarded a successful fresh airport response");
    }

    private static void MenuExitSalvagesThisApproachBeforeCallback()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var air = Frame(10, false);
        var trigger = Frame(10.1, false);
        Call(recorder, "AddToPreRoll", air);
        Call(recorder, "AddToPreRoll", trigger);
        Call(recorder, "StartEpisode", trigger, false, false);
        Assert((long)Get(recorder, "_runwayEpisodeId") == (long)Get(recorder, "_activeEpisodeId"),
            "preliminary transaction was not created at the approach gate");
        Call(recorder, "ResetRunwayLookupStage");
        Publish(recorder, Airport("LBSF"), Runway("LBSF"));
        var contact = Frame(11, true);
        Call(recorder, "AppendEpisodeSample", contact);
        Set(recorder, "_lastContactTime", (double?)11);
        LandingEpisodeEventArgs? saved = null;
        recorder.EpisodeCompleted += (_, args) =>
        {
            saved = args;
            Assert(args.FacilitiesTask.IsCompleted, "menu completion invoked analysis with an unfinishable scenery wait");
        };
        Call(recorder, "HandleSdkFlowEvent", SIMCONNECT_FLOW_EVENT.BACK_TO_MAIN_MENU);
        Assert(saved != null && saved.FacilitiesTask.Result.PreliminaryRunways.Single().AirportIdent == "LBSF" &&
            saved.FacilitiesTask.Result.ApproachSamples.Count == 2 &&
            saved.FacilitiesTask.Result.ApproachSamples.All(sample => !sample.OnGround),
            "menu exit lost this approach or put contact/rollout frames into frozen intent evidence");
        Assert(saved!.RunwayFacilitiesTask.IsCompleted && saved.RunwayFacilitiesTask.Result.Single().AirportIdent == "LBSF",
            "legacy runway task did not expose this episode's fallback");
    }

    private static void PauseRetainsTheApproachAndItsGeometry()
    {
        using var recorder = NewEpisode(40);
        Publish(recorder, Airport("APP"), Runway("APP"));
        var promise = Promise(recorder);
        Call(recorder, "SetSdkPauseFlags", 4);
        Call(recorder, "SetSdkPauseFlags", 0);
        Call(recorder, "SetSdkPauseFlags", 8);
        Assert(!promise.Task.IsCompleted && (long)Get(recorder, "_runwayEpisodeId") == 40,
            "confirmed pauses completed or replaced the approach transaction");
        Call(recorder, "CompleteRunwayLookupWithoutRefresh", "flight ended while paused");
        Assert(promise.Task.Result.PreliminaryRunways.Single().AirportIdent == "APP", "pause discarded approach geometry");
    }

    private static void FlightChangeCancelsPendingFinalReplies()
    {
        using var recorder = NewEpisode(50);
        Publish(recorder, Airport("APP"), Runway("APP"));
        Set(recorder, "_runwayLookupIsPreliminary", false);
        Call(recorder, "ResetRunwayLookupStage");
        Set(recorder, "_runwayAwaitingAirportFacilities", true);
        Set(recorder, "_airportFacilityRequestPending", true);
        Set(recorder, "_airportFacilityRequestId", 500u);
        var task = Promise(recorder).Task;
        Call(recorder, "HandleSdkFlowEvent", SIMCONNECT_FLOW_EVENT.BACK_TO_MAIN_MENU);
        Assert(task.IsCompleted && task.Result.PreliminaryRunways.Count == 1 && !task.Result.FinalLookupCompleted,
            "ending a flight after capture left the final lookup alive");
        Reply(recorder, 500, Airport("WRONGFLIGHT"));
        Assert(task.Result.FinalAirports.Count == 0 &&
            !((Dictionary<string, AirportFacility>)Get(recorder, "_airportFacilities")).Values.Any(value => value.Ident == "WRONGFLIGHT"),
            "a late airport response from the replacement flight changed the completed episode");
    }

    private static void SupersessionCannotBorrowPreviousGeometry()
    {
        using var recorder = NewEpisode(60);
        Publish(recorder, Airport("OLD"), Runway("OLD"));
        var oldTask = Promise(recorder).Task;
        Call(recorder, "BeginRunwayEpisode", 61L);
        Assert(oldTask.IsCompleted && oldTask.Result.PreliminaryRunways.Single().AirportIdent == "OLD",
            "new capture abandoned an older episode's task");
        Call(recorder, "CompleteRunwayLookupWithoutRefresh", "no fresh scenery");
        var newer = Promise(recorder).Task.Result;
        Assert(newer.EpisodeId == 61 && newer.PreliminaryRunways.Count == 0 && newer.FinalRunways.Count == 0 &&
            newer.PreliminaryAirports.Count == 0, "new episode borrowed old approach facilities");
    }

    private static void OptionalPendingMetadataDoesNotLoseCompleteBaseGeometry()
    {
        using var recorder = NewEpisode(70);
        var airport = Airport("BASE");
        Cache(recorder)[airport.Key] = new[] { Runway("BASE") };
        Set(recorder, "_stageAirports", new[] { airport });
        Set(recorder, "_runwayBatchKeys", new[] { airport.Key });
        Set(recorder, "_runwayBatchActive", true);
        var requests = (Dictionary<uint, RunwayFacilityRequest>)Get(recorder, "_runwayRequests");
        requests.Add(700, new RunwayFacilityRequest(airport, double.MaxValue, true));
        Call(recorder, "CompleteRunwayLookupWithoutRefresh", "menu exit during threshold query");
        Assert(Promise(recorder).Task.Result.PreliminaryRunways.Single().AirportIdent == "BASE" && requests.Count == 0,
            "pending optional threshold metadata discarded complete base runway geometry");
    }

    private static void LegacyEventConstructorRemainsCompatible()
    {
        var source = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
        var args = new LandingEpisodeEventArgs(80, Array.Empty<TelemetrySample>(), "sim", "title", "type", "model",
            new[] { Airport("LEGACY") }, Array.Empty<string>(), runwayFacilitiesTask: source.Task, persistentId: System.Guid.NewGuid());
        Assert(!args.FacilitiesTask.IsCompleted, "legacy event adaptation ignored the supplied runway promise");
        source.SetResult(new[] { Runway("LEGACY") });
        var result = args.FacilitiesTask.GetAwaiter().GetResult();
        Assert(result.EpisodeId == 80 && result.FinalAirports.Single().Ident == "LEGACY" &&
            result.FinalRunways.Single().AirportIdent == "LEGACY", "legacy event adaptation lost airport/runway metadata");
    }

    private static void InitialSnapshotIsAvailableWithoutCompletingTheFinalTask()
    {
        using var recorder = NewEpisode(90);
        var runway = Runway("APP");
        Publish(recorder, Airport("APP"), runway);
        var initial = (EpisodeFacilitySnapshot)RecorderTestAccess.Call(recorder, "CurrentEpisodeFacilitySnapshot", Array.Empty<object>())!;
        Assert(!Promise(recorder).Task.IsCompleted && initial.PreliminaryRunways.Single().AirportIdent == "APP",
            "timeout fallback requires prematurely completing the final promise");
        runway.WidthMeters = 99;
        Call(recorder, "BeginRunwayEpisode", 91L);
        Assert(initial.EpisodeId == 90 && initial.PreliminaryRunways.Single().WidthMeters == 45,
            "initial timeout fallback was mutated by a later native response or episode");
    }

    private static void ApproachSamplesDeduplicateWithoutCrossingPhysicsBarriers()
    {
        using var recorder = NewEpisode(100);
        var old = Frame(1, false);
        var barrier = Frame(2, false);
        barrier.MotionSimulation = false;
        barrier.SimulatorState = TelemetrySimulatorState.Inactive;
        var first = Frame(3, false);
        var duplicate = Frame(3, false);
        duplicate.HeadingTrueDegrees = 95;
        var paused = Frame(3, false);
        paused.SimulatorState = TelemetrySimulatorState.Paused;
        var last = Frame(3.1, false);
        Call(recorder, "RequestPreliminaryRunways", last, new[] { old, barrier, first, duplicate, paused, last });
        var initial = (EpisodeFacilitySnapshot)RecorderTestAccess.Call(recorder, "CurrentEpisodeFacilitySnapshot", Array.Empty<object>())!;
        Assert(initial.ApproachSamples.Count == 2 && initial.ApproachSamples[0].SimulationTimeSeconds == 3 &&
            initial.ApproachSamples[0].HeadingTrueDegrees == 95 && initial.ApproachSamples.All(sample => sample.MotionSimulation),
            "approach intent retained duplicate deliveries, paused frames or a previous physics segment");
        duplicate.HeadingTrueDegrees = 200;
        Assert(initial.ApproachSamples[0].HeadingTrueDegrees == 95, "approach intent did not freeze frame values");
    }

    private static void FinalRequestUsesLastContactAndFallsBackOnFreshLookupFailure()
    {
        using var recorder = NewEpisode(110);
        Publish(recorder, Airport("APP"), Runway("APP"));
        var frames = new[] { Frame(10, false), Frame(10.1, true), Frame(10.2, false), Frame(10.3, true) };
        // A normal moving bounce, not a tens-of-kilometres teleport between
        // samples. Keep the final coordinate distinct to test last-contact use.
        for (var index = 0; index < frames.Length; index++)
        {
            frames[index].LongitudeDegrees += index * .00008;
            frames[index].GroundSpeedKnots = 130;
        }
        Call(recorder, "RequestEpisodeRunways", (object)frames);
        var result = Promise(recorder).Task.GetAwaiter().GetResult();
        Assert((double)Get(recorder, "_runwayLookupLatitude") == frames[3].LatitudeDegrees &&
            (double)Get(recorder, "_runwayLookupLongitude") == frames[3].LongitudeDegrees,
            "post-contact lookup did not use the actual last contact");
        Assert(result.PreliminaryRunways.Single().AirportIdent == "APP" && result.FinalRunways.Count == 0 &&
            !result.FinalLookupCompleted && Cache(recorder).Count == 0,
            "failed fresh lookup erased the approach or pretended the preliminary geometry was final");
    }

    private static void FinalBaseSurvivesExitDuringOptionalMetadata()
    {
        using var recorder = NewEpisode(120);
        Publish(recorder, Airport("APP"), Runway("APP"));
        Set(recorder, "_runwayLookupIsPreliminary", false);
        Call(recorder, "ResetRunwayLookupStage");
        var actualAirport = Airport("ACTUAL");
        var actualRunway = Runway("ACTUAL");
        Cache(recorder)[actualAirport.Key] = new[] { actualRunway };
        Set(recorder, "_stageAirports", new[] { actualAirport });
        Set(recorder, "_runwayFreshAirportsReceived", true);
        Set(recorder, "_runwayBatchKeys", new[] { actualAirport.Key });
        Set(recorder, "_runwayBatchActive", true);
        var pending = (Dictionary<uint, RunwayFacilityRequest>)Get(recorder, "_runwayRequests");
        pending.Add(1200, new RunwayFacilityRequest(actualAirport, double.MaxValue, papiSide: 0));
        Call(recorder, "CompleteRunwayLookupWithoutRefresh", "menu during optional PAPI reply");
        var result = Promise(recorder).Task.Result;
        Assert(result.FinalRunways.Single().AirportIdent == "ACTUAL" && result.FinalRunwayCandidatesComplete &&
            result.PreliminaryRunways.Single().AirportIdent == "APP" && result.PreliminaryRunwayCandidatesComplete &&
            result.FinalLookupCompleted && pending.Count == 0,
            "complete final base geometry was lost or downgraded to approach geometry on exit");
        actualRunway.WidthMeters = 999;
        Assert(result.FinalRunways.Single().WidthMeters == 45, "salvaged final geometry was not immutable");
    }

    private static void FailedNeighborKeepsCompleteAirportRepliesWithoutClaimingUniqueness()
    {
        foreach (var preliminary in new[] { false, true })
        {
            using var recorder = NewEpisode(preliminary ? 131 : 130);
            Set(recorder, "_runwayLookupIsPreliminary", preliminary);
            var good = Airport("GOOD"); var bad = Airport("BAD");
            var journal = new TelemetryDecisionJournal();
            Set(recorder, "_runwayDecisionJournal", journal);
            Cache(recorder)[good.Key] = new[] { Runway("GOOD") };
            Set(recorder, "_stageAirports", new[] { good, bad });
            Set(recorder, "_runwayFreshAirportsReceived", true);
            Set(recorder, "_runwayBatchKeys", new[] { good.Key, bad.Key });
            Set(recorder, "_runwayBatchActive", true);
            var pending = (Dictionary<uint, RunwayFacilityRequest>)Get(recorder, "_runwayRequests");
            pending.Add(1300, new RunwayFacilityRequest(bad, -1));
            Call(recorder, "ExpireRunwayRequests");
            if (preliminary) Call(recorder, "CompleteRunwayLookupWithoutRefresh", "end after partial approach response");
            var result = Promise(recorder).Task.Result;
            var retained = preliminary ? result.PreliminaryRunways : result.FinalRunways;
            var complete = preliminary ? result.PreliminaryRunwayCandidatesComplete : result.FinalRunwayCandidatesComplete;
            Assert(retained.Single().AirportIdent == "GOOD" && !complete,
                "one failed neighbor erased complete airport geometry or falsely completed the candidate set");
            Assert(recorder.RunwayFacilities.Count == 0 &&
                ((TaskCompletionSource<System.Collections.Generic.IReadOnlyList<RunwayFacility>>)Get(recorder, "_runwaySnapshotCompletion")).Task.Result.Count == 0,
                "legacy consumers without completeness metadata received a falsely unique candidate set");
            Assert(journal.Snapshot().Entries.Any(entry => entry.Code == "request_timeout" &&
                entry.Reason.Contains("BAD") && entry.Reason.Contains("base") && entry.Details["request_id"] == 1300),
                "failure diagnostics did not identify the failed airport, request and request kind");
        }
    }

    private static void FacilityDiagnosticsAreBoundedAndEpisodeScoped()
    {
        using var recorder = NewEpisode(140);
        var journal = new TelemetryDecisionJournal();
        Set(recorder, "_runwayDecisionJournal", journal);
        var runway = Runway("DETAIL");
        Call(recorder, "RecordRunwayGeometry", runway);
        var geometry = journal.Snapshot().Entries.Single();
        Assert(geometry.Code == "base_geometry" && geometry.Reason.Contains("DETAIL") &&
            geometry.Details["width_m"] == 45 && geometry.Details["length_m"] == 3600 &&
            geometry.Details["latitude"] == runway.LatitudeDegrees && geometry.Details["preliminary"] == 1 &&
            geometry.Details["episode_id"] == 140, "essential geometry or stage provenance was omitted");
        for (var index = 0; index < 1000; index++) Call(recorder, "RecordRunwayGeometry", runway);
        Assert(journal.Snapshot().Entries.Count == 257 &&
            journal.Snapshot().Entries.Count(entry => entry.Code == "diagnostics_limited") == 1,
            "facility diagnostics were unbounded or hid their truncation");
    }

    private static SimConnectLandingRecorder NewEpisode(long id)
    {
        var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        Call(recorder, "BeginRunwayEpisode", id);
        Set(recorder, "_runwayLookupIsPreliminary", true);
        return recorder;
    }

    private static void Publish(SimConnectLandingRecorder recorder, AirportFacility airport, RunwayFacility runway)
    {
        Set(recorder, "_stageAirports", new[] { airport });
        Set(recorder, "_runwayFreshAirportsReceived", true);
        Cache(recorder)[airport.Key] = new[] { runway };
        Set(recorder, "_runwayBatchKeys", new[] { airport.Key });
        Set(recorder, "_runwayBatchActive", true);
        Call(recorder, "TryPublishRunwayBatch");
    }

    private static TelemetrySample Frame(double time, bool ground) => new TelemetrySample
    {
        SimulationTimeSeconds = time, AnalysisTimeSeconds = time, SimulatorState = TelemetrySimulatorState.Running,
        MotionSimulation = true, OnGround = ground, LatitudeDegrees = 42.695, LongitudeDegrees = 23.406,
        AboveGroundLevelFeet = ground ? 0 : 400, VelocityWorldYFps = -5
    };
    private static AirportFacility Airport(string name) => new AirportFacility { Ident = name, LatitudeDegrees = 42.695, LongitudeDegrees = 23.406 };
    private static RunwayFacility Runway(string name) => new RunwayFacility { AirportIdent = name, PrimaryName = "09", SecondaryName = "27",
        LatitudeDegrees = 42.695, LongitudeDegrees = 23.406, LengthMeters = 3600, WidthMeters = 45, HeadingTrueDegrees = 90 };
    private static Dictionary<string, IReadOnlyList<RunwayFacility>> Cache(SimConnectLandingRecorder recorder) =>
        (Dictionary<string, IReadOnlyList<RunwayFacility>>)Get(recorder, "_runways");
    private static TaskCompletionSource<EpisodeFacilitySnapshot> Promise(SimConnectLandingRecorder recorder) =>
        (TaskCompletionSource<EpisodeFacilitySnapshot>)Get(recorder, "_episodeFacilityCompletion");
    private static void Reply(SimConnectLandingRecorder recorder, uint id, AirportFacility airport) => Call(recorder,
        "OnRecvAirportList", null!, new SIMCONNECT_RECV_AIRPORT_LIST { dwRequestID = id, dwEntryNumber = 0, dwOutOf = 1,
            rgData = new object[] { new SIMCONNECT_DATA_FACILITY_AIRPORT { Ident = airport.Ident, Region = airport.Region,
                Latitude = airport.LatitudeDegrees, Longitude = airport.LongitudeDegrees, Altitude = airport.AltitudeMeters } } });
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Get(object target, string name) => RecorderTestAccess.Get(target, name)!;
    private static void Set(object target, string name, object? value) => RecorderTestAccess.Set(target, name, value);
    private static void Call(object target, string name, params object[] args)
    {
        target = RecorderTestAccess.Owner(target, name);
        var method = target.GetType().GetMethod(name, Flags) ?? throw new MissingMethodException(target.GetType().FullName, name);
        method.Invoke(target, args);
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
