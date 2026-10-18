using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class RunwayGuidanceRegressionTests
{
    public static void RunAll()
    {
        Require(Marshal.SizeOf(typeof(RunwayVasiData)) == 20 && Marshal.SizeOf(typeof(RunwayGlideSlopeData)) == 32,
            "native VASI/VOR layout");
        foreach (var side in new[] { 0, 1, 2, 3 })
        {
            var result = Papi(side, 8, 1410.18542f, 3);
            var papi = side < 2 ? result.PrimaryPapi : result.SecondaryPapi;
            Require(papi != null && Math.Abs(papi.StationFromEndMeters - 367.6455078125) < .001 && papi.AngleDegrees == 3,
                "live UUEE 24C PAPI BiasZ convention, both ends");
            Require((side < 2 ? result.PrimaryPapiQueried : result.SecondaryPapiQueried) &&
                !(side < 2 ? result.SecondaryPapiQueried : result.PrimaryPapiQueried), "side identity preserved");
            Require(Papi(side, 0, float.NaN, float.NaN).HasValidGuidanceMetadata, "unused NONE fields are not PAPI evidence");
            Require(Papi(side, 1, 0, 3).PrimaryPapi == null, "VASI is not PAPI");
        }
        foreach (var scenario in new[] { "missing", "parent", "duplicate", "index", "type", "bias", "angle", "outside" })
        {
            var request = Request(0);
            var value = new RunwayVasiData { Type = scenario == "type" ? 99 : 8, BiasZ = scenario == "bias" ? float.NaN : scenario == "outside" ? 9999 : 1410,
                Angle = scenario == "angle" ? 0 : 3 };
            if (scenario != "missing") request.AddVasi(scenario == "parent" ? 99u : 21u, scenario == "index" ? 1u : 0u, 1, value);
            if (scenario == "duplicate") request.AddVasi(21, 0, 1, value);
            Require(!request.TryComplete(out _), "malformed optional PAPI: " + scenario);
        }
        var ils = new RunwayIlsRequest("IAD", "UU", 10);
        var sample = new RunwayGlideSlopeData { Latitude = 55.976083427667618, Longitude = 37.436666786670685,
            Altitude = 189.28001403808594, HasGlideSlope = 1, Angle = 3 };
        ils.Add(0, uint.MaxValue, uint.MaxValue, sample);
        Require(ils.TryComplete(out var slope) && slope != null && slope.Ident == "IAD" && slope.AltitudeMeters == sample.Altitude,
            "live UUEE ILS fixture retains native GS elevation, not runway elevation");
        ils.Add(0, 0, 1, sample);
        Require(!ils.TryComplete(out _), "duplicate ILS payload rejected");
        foreach (var scenario in new[] { "missing", "parent", "index", "hasgs", "position", "angle", "none" })
        {
            ils = new RunwayIlsRequest("IAD", "UU", 10);
            var value = sample;
            if (scenario == "hasgs") value.HasGlideSlope = 2;
            if (scenario == "position") value.Latitude = double.NaN;
            if (scenario == "angle") value.Angle = float.PositiveInfinity;
            if (scenario == "none") { value.HasGlideSlope = 0; value.Angle = float.NaN; }
            if (scenario != "missing") ils.Add(scenario == "parent" ? 1u : 0u, scenario == "index" ? 2u : 0u, 1, value);
            Require(ils.TryComplete(out slope) == (scenario == "none") && slope == null, "ILS validation: " + scenario);
        }
        MergeAndLifecycle();
    }

    private static void MergeAndLifecycle()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var cache = Field<Dictionary<string, IReadOnlyList<RunwayFacility>>>(recorder, "_runways");
        var row = Runway(); cache["UUEE|UU"] = new[] { row };
        Merge(0, Papi(0, 8, 1410.18542f, 3));
        Require(!row.PrimaryPapiQueried && row.PrimaryPapi == null, "one side does not imply complete PAPI query");
        Merge(1, Papi(1, 0, 0, 0));
        Require(row.PrimaryPapiQueried && row.PrimaryPapi != null, "PAPI on one side, verified none on other");
        Merge(2, Papi(2, 0, 0, 0)); Merge(3, Papi(3, 0, 0, 0));
        Require(row.SecondaryPapiQueried && row.SecondaryPapi == null, "verified absence independent by runway end");
        Merge(1, Papi(1, 7, 1400, 3));
        Require(!row.PrimaryPapiQueried && row.PrimaryPapi == null, "conflicting left/right PAPI stays unknown");
        var changed = Papi(1, 8, 1410.18542f, 3); changed.WidthMeters = 45;
        Merge(1, changed);
        Require(!row.PrimaryPapiQueried, "different scenery cannot enrich this geometry");

        var requests = Field<Dictionary<uint, RunwayFacilityRequest>>(recorder, "_runwayRequests");
        var ilsRequests = Field<Dictionary<uint, RunwayIlsRequest>>(recorder, "_ilsRequests");
        requests[100] = new RunwayFacilityRequest(new AirportFacility { Ident = "UUEE", Region = "UU" }, -1, papiSide: 0);
        ilsRequests[101] = new RunwayIlsRequest("IAD", "UU", -1);
        Set(recorder, "_runwayBatchKeys", new[] { "UUEE|UU" }); Set(recorder, "_runwayBatchActive", true);
        Call(recorder, "ExpireRunwayRequests");
        Require(recorder.RunwayFacilities.Count == 1 && requests.Count == 0 && ilsRequests.Count == 0,
            "optional timeouts retain and publish exact geometry");
        var definitionReady = Field<bool[]>(recorder, "_papiDefinitionReady"); definitionReady[0] = true;
        requests[102] = Request(0); ilsRequests[103] = new RunwayIlsRequest("IAD", "UU", 10);
        Call(recorder, "BeginRunwayEpisode", 2L);
        Require(requests.Count == 0 && ilsRequests.Count == 0 && cache.Count == 0 && recorder.RunwayFacilities.Count == 0 && definitionReady[0],
            "new episode discards prior data/pending requests, retains connection definitions");
        Require(Field<Dictionary<string, IReadOnlyList<RunwayFacility>?[]>>(recorder, "_papiReplies").Count == 0, "new episode discards partial PAPI joins");
        Call(recorder, "ResetRunwayConnection"); Require(!definitionReady[0], "reconnect rebuilds optional definitions");
        row.PrimaryPapi = new RunwayPapi { StationFromEndMeters = double.NaN, AngleDegrees = 3 };
        Require(row.HasValidGeometry && !row.HasValidGuidanceMetadata, "bad guidance never invalidates runway geometry");
        void Merge(int side, RunwayFacility incoming) => Call(recorder, "MergeRunwayPapi", "UUEE|UU", new[] { incoming }, side);
    }

    private static RunwayFacilityRequest Request(int side)
    {
        var request = new RunwayFacilityRequest(new AirportFacility { Ident = "UUEE", Region = "UU" }, 10, papiSide: side);
        request.AddRunway(0, 8, 1, Runway(), 21); request.AddAirport(8, 1); return request;
    }
    private static RunwayFacility Papi(int side, int type, float bias, float angle)
    {
        var request = Request(side);
        request.AddVasi(21, uint.MaxValue, uint.MaxValue, new RunwayVasiData { Type = type, BiasZ = bias, Angle = angle });
        Require(request.TryComplete(out var rows), "complete PAPI fixture"); return rows[0];
    }
    private static RunwayFacility Runway() => new RunwayFacility { AirportIdent = "UUEE", AirportRegion = "UU", PrimaryName = "06C", SecondaryName = "24C",
        LengthMeters = 3555.661865234375, WidthMeters = 60, SurfaceType = 4 };
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)RecorderTestAccess.Get(target, name)!;
    private static void Set(object target, string name, object value) => RecorderTestAccess.Set(target, name, value);
    private static void Call(object target, string name, params object[] args) => RecorderTestAccess.Call(target, name, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
