using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using LandingStats.Core;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class RunwayFacilityRegressionTests
{
    public static void RunAll()
    {
        var runway = Runway();
        Assert(runway.HasValidGeometry, "Zero latitude/longitude must be valid.");
        Assert(Marshal.SizeOf(typeof(RunwayAirportData)) == 4 && Marshal.SizeOf(typeof(RunwayFacilityData)) == 88,
            "Native runway payload must match the mixed FLOAT64/FLOAT32/INT32 definition.");
        var native = new RunwayFacilityData { Latitude = 0, Longitude = 0, Altitude = 100, Heading = 360, Length = 3000,
            Width = 45, PrimaryNumber = 36, PrimaryDesignator = 1, SecondaryNumber = 18, SecondaryDesignator = 2 };
        var converted = native.ToFacility(Airport());
        Assert(converted.PrimaryName == "36L" && converted.SecondaryName == "18R" && converted.HeadingTrueDegrees == 0,
            "Native names and north heading must be preserved.");

        var completed = new RunwayFacilityRequest(Airport(), 10);
        completed.AddRunway(1, 123, 2, Runway("09R", "27L"));
        completed.AddAirport(123, 2);
        completed.AddRunway(0, 123, 2, runway);
        Assert(completed.TryComplete(out var rows) && rows.Count == 2 && rows[0].PrimaryName == "09L",
            "Out-of-order complete responses must produce a stable list.");
        var incomplete = new RunwayFacilityRequest(Airport(), 10);
        incomplete.AddAirport(123, 2);
        incomplete.AddRunway(0, 123, 2, runway);
        Assert(!incomplete.TryComplete(out _), "An END after a partial runway list must not publish data.");
        var wrongParent = new RunwayFacilityRequest(Airport(), 10);
        wrongParent.AddAirport(123, 1);
        wrongParent.AddRunway(0, 124, 1, runway);
        Assert(!wrongParent.TryComplete(out _), "A runway belonging to another parent must be rejected.");
        var wrongSize = new RunwayFacilityRequest(Airport(), 10);
        wrongSize.AddAirport(123, 1);
        wrongSize.AddRunway(0, 123, 2, runway);
        Assert(!wrongSize.TryComplete(out _), "An inconsistent native list size must be rejected.");
        var duplicate = new RunwayFacilityRequest(Airport(), 10);
        duplicate.AddAirport(123, 1);
        duplicate.AddRunway(0, 123, 1, runway);
        duplicate.AddRunway(0, 123, 1, runway);
        Assert(!duplicate.TryComplete(out _), "Duplicate list slots must not overwrite native evidence.");
        var tooLarge = new RunwayFacilityRequest(Airport(), 10);
        tooLarge.AddAirport(123, int.MaxValue);
        Assert(!tooLarge.TryComplete(out _), "Native counts must remain bounded.");
        var empty = new RunwayFacilityRequest(Airport(), 10);
        empty.AddAirport(123, 0);
        Assert(empty.TryComplete(out rows) && rows.Count == 0, "An explicitly empty completed airport is valid.");
        Assert(!new RunwayFacilityRequest(Airport(), 10).TryComplete(out _), "No airport header is not an empty airport.");

        var record = Record(90);
        Assert(ReferenceEquals(RunwayResolver.Select(record, new[] { runway }), runway), "Measured eastbound track must match eastbound runway.");
        Assert(RunwayResolver.NameForDirection(record, runway) == "09L", "Primary direction name must match ground track.");
        // Heading intentionally differs: a visual runway association must use actual motion, not aircraft nose direction.
        foreach (var point in record.Series) point.HeadingTrueDegrees = 0;
        Assert(RunwayResolver.Select(record, new[] { runway }) != null, "Crab/nose heading must not change the ground-track match.");
        var reciprocal = Record(270);
        Assert(RunwayResolver.NameForDirection(reciprocal, runway) == "27R", "Reciprocal approach must select the other name.");
        var second = Runway("09R", "27L");
        second.LatitudeDegrees = .0001;
        Assert(RunwayResolver.Select(record, new[] { runway, second }) == null, "Two overlapping candidate strips are ambiguous.");
        second.LatitudeDegrees = .01;
        Assert(ReferenceEquals(RunwayResolver.Select(record, new[] { runway, second }), runway), "A distant parallel runway must not win.");
        record.TouchdownLatitudeDegrees = .005;
        Assert(RunwayResolver.Select(record, new[] { runway }) == null, "A nearby airport is not evidence of landing on its runway.");
        record = Record(0);
        Assert(RunwayResolver.Select(record, new[] { runway }) == null, "Cross-track motion must not select the crossed runway.");
        record = Record(90);
        record.Series[2].LatitudeDegrees = null;
        Assert(RunwayResolver.Select(record, new[] { runway }) == null, "Missing track samples cannot be silently bridged for association.");
        record.Series.Clear();
        Assert(RunwayResolver.Select(record, new[] { runway }) == null, "Old records with only touchdown coordinates must not invent a track.");
        runway.WidthMeters = double.NaN;
        Assert(!runway.HasValidGeometry, "Nonfinite facility data must not reach rendering.");
        runway = Runway();
        runway.SurfaceType = 27;
        Assert(runway.IsWater && RunwayResolver.Select(Record(90), new[] { runway }) == null,
            "A named water runway must not be rendered as a land strip.");
        RunwayResolver.OffsetMeters(0, 179.999, 0, -179.999, out var east, out var north);
        Assert(east > 200 && east < 230 && north == 0, "Local projection must cross the date line by the short distance.");
        BatchCompletionIsAtomic();
    }

    private static void BatchCompletionIsAtomic()
    {
        using (var recorder = new SimConnectLandingRecorder(IntPtr.Zero))
        {
            var notifications = 0;
            recorder.RunwayFacilitiesUpdated += (_, args) => { notifications++; Assert(args.Runways.Count == 2, "Only a complete candidate batch may be published."); };
            var cache = (Dictionary<string, IReadOnlyList<RunwayFacility>>)Field(recorder, "_runways");
            var pending = (Dictionary<uint, RunwayFacilityRequest>)Field(recorder, "_runwayRequests");
            Set(recorder, "_runwayBatchKeys", new[] { "A", "B" });
            Set(recorder, "_runwayBatchActive", true);
            cache["A"] = new[] { Runway() };
            pending[100] = new RunwayFacilityRequest(Airport(), double.MaxValue);
            Call(recorder, "TryPublishRunwayBatch");
            Assert(notifications == 0 && recorder.RunwayFacilities.Count == 0,
                "The first airport reply must not freeze a possibly ambiguous runway choice.");
            cache["B"] = new[] { Runway("09R", "27L") };
            pending.Clear();
            Call(recorder, "TryPublishRunwayBatch");
            Assert(notifications == 1 && recorder.RunwayFacilities.Count == 2, "All airport replies must publish together.");
            Call(recorder, "TryPublishRunwayBatch");
            Assert(notifications == 1, "A completed batch must not emit duplicate completion events.");

            Set(recorder, "_nextRunwayRequestId", (uint)123456);
            Call(recorder, "ResetRunwayConnection");
            Assert(recorder.RunwayFacilities.Count == 0 && cache.Count == 0 && pending.Count == 0 &&
                   (uint)Field(recorder, "_nextRunwayRequestId") == 123456,
                "Reconnect must clear native transactions and published geometry without reusing request IDs.");
            Set(recorder, "_runwayBatchKeys", new[] { "A", "B" });
            Set(recorder, "_runwayBatchActive", true);
            cache["A"] = new[] { Runway() };
            pending[101] = new RunwayFacilityRequest(Airport(), -1);
            Call(recorder, "ExpireRunwayRequests");
            Assert(notifications == 1 && recorder.RunwayFacilities.Count == 0 && pending.Count == 0,
                "A timeout must discard the incomplete batch, not publish the one successful airport.");
        }
    }

    public static void RefreshIsScopedToEachEpisode()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var cache = (Dictionary<string, IReadOnlyList<RunwayFacility>>)Field(recorder, "_runways");
        var pending = (Dictionary<uint, RunwayFacilityRequest>)Field(recorder, "_runwayRequests");
        var packets = (Dictionary<uint, uint>)Field(recorder, "_runwayPackets");
        var old = Runway(); old.PrimaryThresholdOffsetMeters = 308;
        var saved = Record(90); saved.RunwayGeometry = old;
        cache[Airport().Key] = new[] { old };
        Set(recorder, "_runwayEpisodeId", 101L);
        Set(recorder, "_runwayLookupStarted", true);
        Set(recorder, "_runwayDefinitionReady", true);
        Set(recorder, "_runwayDefinitionAttempted", true);
        var ready = (bool[])Field(recorder, "_thresholdDefinitionReady"); ready[0] = ready[1] = true;
        pending[100] = new RunwayFacilityRequest(Airport(), double.MaxValue, true);
        packets[7] = 100; packets[8] = 0;
        Set(recorder, "_nextRunwayRequestId", 1000u);

        var completions = 0;
        recorder.EpisodeCompleted += (_, args) =>
        {
            completions++;
            Assert(recorder.RunwayFacilities.Count == 0 && cache.Count == 0 && pending.Count == 0,
                "Previous snapshot and pending replies must be cleared before analysis observes completion.");
            Assert(recorder.GetRunwayFacilities(args.EpisodeId - 1).Count == 0,
                "Delayed previous analysis must not see another episode's snapshot.");
            Assert(args.RunwayFacilitiesTask.IsCompleted && args.RunwayFacilitiesTask.Result.Count == 0,
                "A disconnected capture must settle immediately without old scenery.");
        };
        void Complete(long id)
        {
            Set(recorder, "_activeEpisodeId", id);
            Set(recorder, "_activePersistentId", Guid.NewGuid());
            Set(recorder, "_lastContactTime", 13.0);
            // Two contacts are still one completed recording / one refresh.
            Set(recorder, "_episodeSamples", new List<TelemetrySample> {
                new TelemetrySample { SimulationTimeSeconds = 10, MotionSimulation = true, OnGround = false },
                new TelemetrySample { SimulationTimeSeconds = 11, MotionSimulation = true, OnGround = true },
                new TelemetrySample { SimulationTimeSeconds = 12, MotionSimulation = true, OnGround = false },
                new TelemetrySample { SimulationTimeSeconds = 13, MotionSimulation = true, OnGround = true } });
            RecorderTestAccess.Call(recorder, "CompleteEpisode", new object[] { false });
        }
        Complete(102);
        Assert(completions == 1 && !packets.ContainsKey(7) && packets.ContainsKey(8), "Cancel old requests, not connection definitions.");
        Assert((bool)Field(recorder, "_runwayDefinitionReady") && ready[0] && ready[1] &&
            (uint)Field(recorder, "_nextRunwayRequestId") == 1000, "Keep valid definitions and never recycle native request IDs.");
        Assert((bool)Field(recorder, "_runwayLookupStarted"), "Disconnected salvage must not leave a deferred lookup behind.");
        // Start the refresh transaction independently: this fixture has no native
        // connection, so completion above deliberately settled the unavailable
        // lookup. A live episode starts a fresh transaction before requesting replies.
        RecorderTestAccess.Call(recorder, "BeginRunwayEpisode", new object[] { 102L });
        Assert(!(bool)Field(recorder, "_runwayLookupStarted"), "A fresh batch is allowed even at the same airport.");

        // A fresh base reply with a timed-out optional query must not borrow
        // the previous landing's threshold, and must allow a retry next time.
        var current = Runway(); current.WidthMeters = 60;
        cache[Airport().Key] = new[] { current };
        Set(recorder, "_runwayBatchKeys", new[] { Airport().Key });
        Set(recorder, "_runwayBatchActive", true);
        Set(recorder, "_runwayLookupStarted", true);
        pending[1001] = new RunwayFacilityRequest(Airport(), -1, true);
        long publishedId = 0; var publications = 0;
        recorder.RunwayFacilitiesUpdated += (_, args) => { publishedId = args.EpisodeId; publications++; };
        Call(recorder, "ExpireRunwayRequests");
        Assert(publishedId == 102 && recorder.GetRunwayFacilities(102).Count == 1 &&
            !current.PrimaryThresholdOffsetMeters.HasValue, "Only this landing's measured base geometry survives a threshold timeout.");
        Assert(recorder.GetRunwayFacilities(101).Count == 0 && saved.RunwayGeometry.PrimaryThresholdOffsetMeters == 308 &&
            saved.RunwayGeometry.WidthMeters == 45, "Saved older snapshot is unchanged.");
        Call(recorder, "TryPublishRunwayBatch");
        Assert(publications == 1 && (bool)Field(recorder, "_runwayLookupStarted"), "One completed batch, not a query per contact or airport-list callback.");

        Complete(103);
        Assert(completions == 2 && (bool)Field(recorder, "_runwayLookupStarted"), "Second disconnected capture must also settle its lookup.");
        RecorderTestAccess.Call(recorder, "BeginRunwayEpisode", new object[] { 103L });
        Assert(!(bool)Field(recorder, "_runwayLookupStarted"), "Optional failure does not suppress the next landing's query.");
        var newest = Runway(); newest.WidthMeters = 60;
        cache[Airport().Key] = new[] { newest };
        var threshold = Runway(); threshold.WidthMeters = 60; threshold.PrimaryThresholdOffsetMeters = 100;
        RecorderTestAccess.Call(recorder, "MergeRunwayThresholds", new object[] { Airport().Key, new[] { threshold }, true });
        Set(recorder, "_runwayBatchKeys", new[] { Airport().Key }); Set(recorder, "_runwayBatchActive", true);
        Call(recorder, "TryPublishRunwayBatch");
        Assert(publishedId == 103 && recorder.GetRunwayFacilities(103)[0].PrimaryThresholdOffsetMeters == 100 &&
            recorder.GetRunwayFacilities(102).Count == 0 && !current.PrimaryThresholdOffsetMeters.HasValue &&
            saved.RunwayGeometry.PrimaryThresholdOffsetMeters == 308, "Fresh threshold belongs only to the new episode.");

        // Current report persistence is scoped by the committed episode identity.
        EpisodeReportLifecycleRegressionTests.FacilitiesCannotEnrichAnotherEpisode();
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object owner, string name) => RecorderTestAccess.Get(owner, name)!;
    private static void Set(object owner, string name, object value) => RecorderTestAccess.Set(owner, name, value);
    private static void Call(object owner, string name) => RecorderTestAccess.Call(owner, name, null);

    private static AirportFacility Airport() => new AirportFacility { Ident = "TEST", Region = "TT" };
    private static RunwayFacility Runway(string primary = "09L", string secondary = "27R") => new RunwayFacility
    {
        AirportIdent = "TEST", AirportRegion = "TT", LatitudeDegrees = 0, LongitudeDegrees = 0,
        AltitudeMeters = 100, HeadingTrueDegrees = 90, LengthMeters = 3000, WidthMeters = 45,
        PrimaryName = primary, SecondaryName = secondary,
    };
    private static LandingRecord Record(double heading)
    {
        var result = new LandingRecord { TouchdownLatitudeDegrees = 0, TouchdownLongitudeDegrees = 0 };
        for (var index = 0; index <= 6; index++)
        {
            var time = -1 + index * .25;
            result.Series.Add(new LandingSeriesPoint { TimeSeconds = time,
                LatitudeDegrees = time * 60 * Math.Cos(heading * Math.PI / 180) / 111195,
                LongitudeDegrees = time * 60 * Math.Sin(heading * Math.PI / 180) / 111195 });
        }
        return result;
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
