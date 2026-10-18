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

internal static class FreshAirportLookupRegressionTests
{
    internal static void RunAll()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var stale = new AirportFacility { Ident = "LMX43", LatitudeDegrees = 35.895673, LongitudeDegrees = 14.495853 };
        var fresh = new AirportFacility { Ident = "LMML", Region = "LM", LatitudeDegrees = 35.857542, LongitudeDegrees = 14.477439 };
        recorder.SeedAirportFacilities(new[] { stale });
        Call(recorder, "BeginRunwayEpisode", 1L);
        var journal = new TelemetryDecisionJournal();
        Set(recorder, "_runwayDecisionJournal", journal);
        Set(recorder, "_runwayAwaitingAirportFacilities", true);
        Set(recorder, "_airportFacilityRequestPending", true);
        Set(recorder, "_airportFacilityRequestId", 0x30000002u);
        Set(recorder, "_airportFacilityRequestDeadline", double.MaxValue);
        Set(recorder, "_runwayLookupExpires", double.MaxValue);
        var promise = (TaskCompletionSource<IReadOnlyList<RunwayFacility>>)Get(recorder, "_runwaySnapshotCompletion");
        var cache = (Dictionary<string, AirportFacility>)Get(recorder, "_airportFacilities");
        var record = Landing(35.842, 14.491);
        Require(AirportResolver.FindNearest(record.TouchdownLatitudeDegrees!.Value, record.TouchdownLongitudeDegrees!.Value,
            new[] { stale }, out _) == stale, "A nearby stale ARP is a valid nearest guess even when the real airport is missing.");

        // A late completion from an older request must not unlock the new lookup.
        Reply(recorder, 0x30000001, 0, 1, stale);
        Require((bool)Get(recorder, "_runwayAwaitingAirportFacilities") && cache.Count == 1 && !promise.Task.IsCompleted,
            "Late previous airport-list replies cannot unblock a fresh episode request.");
        Reply(recorder, 0x30000002, 0, 2, fresh);
        Require((bool)Get(recorder, "_runwayAwaitingAirportFacilities") && !cache.ContainsKey(fresh.Key) && !promise.Task.IsCompleted,
            "Partial fresh airport pages cannot start a stale/mixed runway batch.");
        Reply(recorder, 0x30000002, 1, 2, stale);
        Require(!(bool)Get(recorder, "_runwayAwaitingAirportFacilities") && !(bool)Get(recorder, "_airportFacilityRequestPending") && cache.ContainsKey(fresh.Key),
            "Only a complete fresh list replaces the candidate cache and unlocks runway selection.");
        Require(journal.Snapshot().Entries.Any(e => e.Code == "fresh_list_ready"), "Fresh-list completion is diagnosable.");
        var actual = new RunwayFacility { AirportIdent = fresh.Ident, AirportRegion = fresh.Region,
            LatitudeDegrees = 35.842, LongitudeDegrees = 14.491, AltitudeMeters = 75,
            HeadingTrueDegrees = 310, LengthMeters = 3500, WidthMeters = 58, PrimaryName = "31", SecondaryName = "13" };
        record.Airport = "LMX43";
        Require(RunwayResolver.TryApply(record, new[] { actual }) && record.Airport == "LMML" && record.Runway == "31",
            "A fresh containing runway corrects the earlier cached ARP guess and supplies 3D geometry.");

        Call(recorder, "BeginRunwayEpisode", 2L);
        journal = new TelemetryDecisionJournal(); Set(recorder, "_runwayDecisionJournal", journal);
        Set(recorder, "_runwayAwaitingAirportFacilities", true);
        Set(recorder, "_airportFacilityRequestPending", true);
        Set(recorder, "_airportFacilityRequestId", 0x30000003u);
        Set(recorder, "_airportFacilityRequestDeadline", -1d);
        promise = (TaskCompletionSource<IReadOnlyList<RunwayFacility>>)Get(recorder, "_runwaySnapshotCompletion");
        Call(recorder, "ExpireRunwayRequests");
        Require(promise.Task.IsCompleted && promise.Task.Result.Count == 0 && !(bool)Get(recorder, "_airportFacilityRequestPending") &&
                !(bool)Get(recorder, "_runwayAwaitingAirportFacilities"), "Fresh-list timeout completes the task without using the old cache or waiting forever.");
        Require(journal.Snapshot().Entries.Any(e => e.Code == "fresh_list_unavailable"), "Timeout reason is retained before completion.");
        Reply(recorder, 0x30000003, 0, 1, stale);
        Require(cache.ContainsKey(fresh.Key), "Timed-out list cannot replace the cache after its pending request was cancelled.");

        Call(recorder, "BeginRunwayEpisode", 3L);
        Require(!(bool)Get(recorder, "_runwayLookupStarted"), "The next episode can retry after failed freshness lookup.");
        Set(recorder, "_runwayAwaitingAirportFacilities", true);
        Set(recorder, "_airportFacilityRequestPending", true);
        promise = (TaskCompletionSource<IReadOnlyList<RunwayFacility>>)Get(recorder, "_runwaySnapshotCompletion");
        Call(recorder, "ResetRunwayConnection");
        Require(promise.Task.IsCompleted && promise.Task.Result.Count == 0, "Disconnect finishes a pending fresh-list wait without a live timer.");
    }

    private static LandingRecord Landing(double latitude, double longitude)
    {
        var record = new LandingRecord { TouchdownLatitudeDegrees = latitude, TouchdownLongitudeDegrees = longitude };
        var heading = 310 * Math.PI / 180;
        foreach (var time in new[] { -1d, -.5, 0, .5 })
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = time,
                LatitudeDegrees = latitude + time * 50 * Math.Cos(heading) / 6371008.8 * 180 / Math.PI,
                LongitudeDegrees = longitude + time * 50 * Math.Sin(heading) / (6371008.8 * Math.Cos(latitude * Math.PI / 180)) * 180 / Math.PI });
        return record;
    }
    private static void Reply(SimConnectLandingRecorder recorder, uint id, uint page, uint pages, AirportFacility airport) =>
        Call(recorder, "OnRecvAirportList", null!, new SIMCONNECT_RECV_AIRPORT_LIST {
            dwRequestID = id, dwEntryNumber = page, dwOutOf = pages,
            rgData = new object[] { new SIMCONNECT_DATA_FACILITY_AIRPORT { Ident = airport.Ident, Region = airport.Region,
                Latitude = airport.LatitudeDegrees, Longitude = airport.LongitudeDegrees, Altitude = airport.AltitudeMeters } } });
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Get(object target, string name) => RecorderTestAccess.Get(target, name)!;
    private static void Set(object target, string name, object value) => RecorderTestAccess.Set(target, name, value);
    private static void Call(object target, string name, params object[] args) => RecorderTestAccess.Call(target, name, args);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
