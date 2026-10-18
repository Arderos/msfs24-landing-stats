using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class BacklogDiagnosticRegressionTests
{
    public static void SdkPacketsHaveConnectionScopedOperationContext()
    {
        var operations = new SimConnectOperationJournal();
        operations.BeginConnection(new Dictionary<string, double> { ["sdk_version_major"] = 12 });
        operations.Remember(10, "Frame definition: VELOCITY WORLD Y", false, definition: 1);
        operations.Remember(11, "Subscribe Pause_EX1", true, eventId: 256);
        operations.Remember(12, "Request runway facility", true, request: 1000, definition: 3);
        operations.Remember(13, "map AXIS_ELEVATOR_SET", true, eventId: 4);
        var journal = new TelemetryDecisionJournal();
        foreach (var packet in new uint[] { 10, 11, 12, 13, 999 }) operations.RecordRejection(journal, 1, 3, packet, uint.MaxValue);
        var entries = journal.Snapshot().Entries;
        Assert(entries[0].Reason.Contains("VELOCITY WORLD Y") && entries[0].Details["optional_capability"] == 0 && entries[0].Details["definition_id"] == 1,
            "required telemetry failure lacks channel identity");
        Assert(entries[1].Details["event_id"] == 256 && entries[1].Details["optional_capability"] == 1, "lifecycle capability identity lost");
        Assert(entries[2].Details["request_id"] == 1000 && entries[2].Details["definition_id"] == 3, "facility request identity lost");
        Assert(entries[3].Details["event_id"] == 4, "control event identity lost");
        Assert(entries[4].Details["operation_known"] == 0 && !entries[4].Details.ContainsKey("optional_capability"), "unknown operation declared safe");
        Assert(entries.All(e => e.Details["sdk_version_major"] == 12), "SDK version missing");
        operations.BeginConnection();
        var second = new TelemetryDecisionJournal();
        operations.RecordRejection(second, 2, 3, 10, 0);
        Assert(second.Snapshot().Entries.Single().Details["operation_known"] == 0, "packet from another connection was reused");
        operations.Remember(10, "new connection operation", true);
        Assert(operations.Name(10) == "new connection operation", "new packet association not recorded");
        for (uint i = 20; i < 4200; i++) operations.Remember(i, "bounded", true);
        Assert(operations.Name(10) == null && operations.Name(4199) == "bounded", "operation journal is not bounded");
    }

    public static void RejectedTransitionsAreBatchedWithoutLosingRaw()
    {
        var batcher = new RejectedContactBatcher();
        var now = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
        var input = Enumerable.Range(1, 100).Select(i => Episode(i)).ToArray();
        var output = new List<LandingEpisodeEventArgs>();
        foreach (var e in input) output.AddRange(batcher.Accept(e, now));
        output.Add(batcher.Flush()!);
        Assert(output.Count == 7 && output.All(e => e.IsDiagnosticOnly), "short diagnostic burst was not bounded");
        Assert(output.SelectMany(e => e.Samples).Select(TelemetryCsv.Format).SequenceEqual(input.SelectMany(e => e.Samples).Select(TelemetryCsv.Format)),
            "batching discarded or rewrote raw telemetry");
        Assert(output.Sum(e => e.DecisionJournal!.Snapshot().Entries.Single(x => x.Code == "rejected_contact_batch").Details["capture_count"]) == 100,
            "batching lost original event count");
        batcher.Accept(Episode(101), now);
        Assert(batcher.FlushExpired(now.AddSeconds(29)) == null && batcher.FlushExpired(now.AddSeconds(30)) != null, "pending diagnostic timeout");
        foreach (var change in new[] { "aircraft", "segment", "reason" })
        {
            batcher.Accept(Episode(102), now);
            var next = Episode(103, change);
            Assert(batcher.Accept(next, now).Count == 1, "new context merged with old diagnostics: " + change);
            Assert(batcher.Flush() != null, "new context was suppressed");
        }
        batcher.Accept(Episode(104), now);
        var normal = new LandingEpisodeEventArgs(105, new[] { Sample(210, true, true) }, "MSFS", "aircraft", "type", "model", Array.Empty<AirportFacility>(), Array.Empty<string>(), persistentId: System.Guid.NewGuid());
        var completed = batcher.Accept(normal, now);
        Assert(completed.Count == 2 && ReferenceEquals(completed[1], normal), "real landing was delayed or rewritten");
        var fullDetails = Episode(106);
        fullDetails.DecisionJournal!.Record(1, "test", "detail_capacity", "Preserve every original field.",
            Enumerable.Range(0, 24).ToDictionary(i => "detail_" + i, i => (double)i));
        var unchanged = batcher.Accept(fullDetails, now);
        Assert(unchanged.Count == 1 && ReferenceEquals(unchanged[0], fullDetails), "batch linkage displaced original diagnostic fields");
        var dense = Episode(107);
        for (var i = 0; i < 1000; i++) dense.DecisionJournal!.Record(i, "test", "dense", new string('x', 256));
        var denseOutput = new List<LandingEpisodeEventArgs>();
        denseOutput.AddRange(batcher.Accept(dense, now));
        denseOutput.AddRange(batcher.Accept(dense, now));
        var tail = batcher.Flush(); if (tail != null) denseOutput.Add(tail);
        Assert(denseOutput.All(e => !e.DecisionJournal!.Snapshot().Truncated) &&
            denseOutput.Sum(e => e.DecisionJournal!.Snapshot().Entries.Count(x => x.Code == "dense")) == 2000,
            "aggregation silently exhausted the diagnostic budget");
    }

    public static void RecorderIgnoresGroundChatterBeforeRealLanding()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var episodes = new List<LandingEpisodeEventArgs>();
        recorder.EpisodeCompleted += (_, e) => episodes.Add(e);
        var feed = typeof(SimConnectLandingRecorder).GetMethod("ProcessSample", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var i = 0; i < 40; i++)
        {
            feed.Invoke(recorder, new object[] { Sample(i * .2, false, false) });
            feed.Invoke(recorder, new object[] { Sample(i * .2 + .1, true, false) });
        }
        var air = Sample(8.1, false, true); air.AboveGroundLevelFeet = 400; air.VelocityWorldYFps = -4;
        feed.Invoke(recorder, new object[] { air });
        feed.Invoke(recorder, new object[] { Sample(8.2, true, true) });
        feed.Invoke(recorder, new object[] { Sample(24, true, true) });
        Assert(episodes.Count(e => !e.IsDiagnosticOnly) == 1, "first real touchdown after burst lost");
        Assert(episodes.All(e => !e.IsDiagnosticOnly), "non-motion chatter created automatic diagnostics");
    }

    private static LandingEpisodeEventArgs Episode(int id, string change = "")
    {
        var samples = new[] { Sample(id * 2, false, false), Sample(id * 2 + 1, true, false) };
        if (change == "segment") foreach (var s in samples) s.FlightSegment = 2;
        var journal = new TelemetryDecisionJournal();
        journal.Record(id * 2, "recorder", "physics_discontinuity_rejected", change == "reason" ? "changed rejection" : "no active witness");
        journal.Record(id * 2 + 1, "recorder", "rejected_contact_diagnostics", "diagnostic completion");
        return new LandingEpisodeEventArgs(id, samples, "MSFS", change == "aircraft" ? "other" : "aircraft", "type", "model",
            Array.Empty<AirportFacility>(), Array.Empty<string>(), journal, isDiagnosticOnly: true, persistentId: System.Guid.NewGuid());
    }

    private static TelemetrySample Sample(double t, bool ground, bool motion) => new TelemetrySample
    {
        Sequence = (long)(t * 10), SimulationTimeSeconds = t, HostElapsedSeconds = t, OnGround = ground,
        MotionSimulation = motion, FlightSegment = 1, AboveGroundLevelFeet = 5000,
    };
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
