using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.RegressionTests;

internal static class PausePipelineRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        MainPipelineStitchesPausesWithoutChangingRaw();
        PausedOnlyCaptureDoesNotLoadConfigurationOrHistory();
        RecordFactoryHonorsZeroActiveTime();
        RunwayDetectionUsesTheSameStitchedContacts();
        NoRefreshCompletionSettlesRunwayTaskBeforeCallback();
        LateCancelledFlowDoesNotDiscardTheNextApproach();
    }

    private static void MainPipelineStitchesPausesWithoutChangingRaw()
    {
        var baseline = Landing(false);
        var paused = Landing(true);
        var originalRawTimes = paused.Select(sample => sample.SimulationTimeSeconds).ToArray();
        var expected = Process(baseline).Single();
        var actual = Process(paused).Single();
        Assert(actual.EstimatedContactTimeSeconds == expected.EstimatedContactTimeSeconds &&
               actual.InertialFpm == expected.InertialFpm && actual.SurfaceFpm == expected.SurfaceFpm,
            "the main pipeline did not use the same stitched contact/fit timeline");
        Assert(actual.HasImpactClassification && actual.ImpactStatus == expected.ImpactStatus &&
               actual.ImpactPeakG == expected.ImpactPeakG && actual.ImpactWindowSeconds == expected.ImpactWindowSeconds,
            "a confirmed pause interrupted or contaminated the adaptive impact measurement");
        Assert(actual.Series.Count == expected.Series.Count && actual.Series.Zip(expected.Series, (a, b) =>
                a.TimeSeconds == b.TimeSeconds && a.GForce == b.GForce && a.PitchDegrees == b.PitchDegrees).All(equal => equal),
            "stored charts/3D still contain frozen measurements or pause duration");
        Assert(paused.Select(sample => sample.SimulationTimeSeconds).SequenceEqual(originalRawTimes) &&
               paused.Count(sample => sample.SimulatorState == TelemetrySimulatorState.Paused) == 4,
            "analysis changed the original diagnostic capture");
    }

    private static void PausedOnlyCaptureDoesNotLoadConfigurationOrHistory()
    {
        var processor = new EpisodeProcessor(null!, null!, null!);
        var sample = Frame(10, false);
        sample.SimulatorState = TelemetrySimulatorState.Paused;
        sample.MotionSimulation = false;
        Assert(Process(processor, new[] { sample }).Count == 0,
            "a paused-only capture was saved or accessed uninitialized configuration/history services");
    }

    private static void RecordFactoryHonorsZeroActiveTime()
    {
        var sample = Frame(100, true);
        sample.AnalysisTimeSeconds = 0;
        sample.HostElapsedSeconds = 700;
        var record = LandingRecordFactory.Create(new TouchdownResult
        {
            SimulationTimeSeconds = 0, EstimatedContactTimeSeconds = 0,
        }, new[] { sample }, "TEST", "TEST");
        Assert(record.Series.Count == 1 && record.Series[0].TimeSeconds == 0,
            "zero active time incorrectly fell back to raw or host time");
    }

    private static void RunwayDetectionUsesTheSameStitchedContacts()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        Invoke(recorder, "BeginRunwayEpisode", 88L);
        var air = Frame(10, false);
        var paused = Frame(70, false);
        paused.SimulatorState = TelemetrySimulatorState.Paused;
        paused.MotionSimulation = false;
        paused.AnalysisTimeSeconds = 10;
        var contact = Frame(70.01, true);
        contact.AnalysisTimeSeconds = 10.01;
        contact.LatitudeDegrees = 43.25;
        contact.LongitudeDegrees = 27.75;
        Invoke(recorder, "RequestEpisodeRunways", (object)new[] { air, paused, contact });
        Assert((double)Get(recorder, "_runwayLookupLatitude")! == contact.LatitudeDegrees &&
               (double)Get(recorder, "_runwayLookupLongitude")! == contact.LongitudeDegrees,
            "runway detection lost the contact stitched across a confirmed pause");
    }

    private static void NoRefreshCompletionSettlesRunwayTaskBeforeCallback()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var previousRunways = new[] { new RunwayFacility { AirportIdent = "OLD", PrimaryName = "09" } };
        Set(recorder, "_runwayEpisodeId", 76L);
        Set(recorder, "_activeEpisodeId", 77L);
        Set(recorder, "_activePersistentId", Guid.NewGuid());
        Set(recorder, "_episodeSamples", new List<TelemetrySample> { Frame(9.99, false), Frame(10, true) });
        Set(recorder, "_lastContactTime", (double?)10);
        Set(recorder, "_episodeDecisionJournal", new TelemetryDecisionJournal());
        var callbacks = 0;
        recorder.EpisodeCompleted += (_, episode) =>
        {
            callbacks++;
            Assert(episode.RunwayFacilitiesTask.IsCompleted && episode.RunwayFacilitiesTask.Result.Count == 0,
                "salvaged landing waits for a scenery lookup that will never run or inherits unrelated geometry");
        };
        Invoke(recorder, "CompleteEpisode", false);
        Assert(callbacks == 1, "salvaged landing was not completed exactly once");
        Invoke(recorder, "CompleteEpisode", false);
        Assert(callbacks == 1, "repeated lifecycle completion duplicated the landing");
    }

    private static void LateCancelledFlowDoesNotDiscardTheNextApproach()
    {
        var flows = new[]
        {
            new[] { SIMCONNECT_FLOW_EVENT.REPLAY_START, SIMCONNECT_FLOW_EVENT.REPLAY_END },
            new[] { SIMCONNECT_FLOW_EVENT.RTC_START, SIMCONNECT_FLOW_EVENT.RTC_END },
            new[] { SIMCONNECT_FLOW_EVENT.TELEPORT_START, SIMCONNECT_FLOW_EVENT.TELEPORT_DONE },
            new[] { SIMCONNECT_FLOW_EVENT.SKIP_START, SIMCONNECT_FLOW_EVENT.SKIP_DONE },
        };
        foreach (var flow in flows)
        {
            using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
            var captures = new List<LandingEpisodeEventArgs>();
            recorder.EpisodeCompleted += (_, episode) => captures.Add(episode);
            Invoke(recorder, "SetSdkSimRunning", true);
            Invoke(recorder, "SetSdkPauseFlags", 0);
            Invoke(recorder, "ProcessSample", Frame(1, false));
            Invoke(recorder, "HandleSdkFlowEvent", flow[0]);
            Invoke(recorder, "HandleSdkFlowEvent", SIMCONNECT_FLOW_EVENT.BACK_TO_MAIN_MENU);
            Invoke(recorder, "HandleSdkFlowEvent", SIMCONNECT_FLOW_EVENT.FLT_LOAD);
            Invoke(recorder, "HandleSdkFlowEvent", SIMCONNECT_FLOW_EVENT.FLT_LOADED);
            Invoke(recorder, "HandleSdkFlowEvent", SIMCONNECT_FLOW_EVENT.FLIGHT_START);
            Invoke(recorder, "SetSdkSimRunning", true);
            Invoke(recorder, "ProcessSample", Frame(10, false));
            Invoke(recorder, "HandleSdkFlowEvent", flow[1]);
            Invoke(recorder, "ProcessSample", Frame(10.01, true));
            Invoke(recorder, "CompleteEpisode", false);
            Assert(captures.Count(episode => !episode.IsDiagnosticOnly) == 1 &&
                   captures.SelectMany(episode => TouchdownAnalysis.Analyze(episode.Samples)).Count() == 1,
                "late cancelled flow completion discarded the new flight's airborne witness: " + flow[1]);
        }
    }

    private static List<TelemetrySample> Landing(bool withPauses)
    {
        var samples = new List<TelemetrySample>();
        var pauseOffset = 0.0;
        for (var i = -50; i <= 200; i++)
        {
            var activeTime = 10 + i * .01;
            if (withPauses && (i == -10 || i == 10))
            {
                foreach (var duration in new[] { .001, 60.0 })
                {
                    var frozen = Frame(activeTime - .01 + pauseOffset + duration, i > 0);
                    frozen.AnalysisTimeSeconds = activeTime - .01;
                    frozen.SimulatorState = TelemetrySimulatorState.Paused;
                    frozen.MotionSimulation = false;
                    frozen.GForce = 9;
                    frozen.PitchDegrees = 99;
                    samples.Add(frozen);
                }
                pauseOffset += 60;
            }
            var sample = Frame(activeTime + pauseOffset, i >= 0);
            sample.AnalysisTimeSeconds = activeTime;
            sample.VelocityWorldYFps = i < 0 ? -2 : -.5 * Math.Exp(-Math.Pow((i * .01 - .15) / .12, 2));
            sample.GForce = i < 0 ? 1 : 1 + .5 * Math.Exp(-Math.Pow((i * .01 - .35) / .08, 2));
            sample.PlaneAltitudeFeet = i < 0 ? 1000 - i * .02 : 1000;
            samples.Add(sample);
        }
        return samples;
    }

    private static TelemetrySample Frame(double time, bool ground) => new TelemetrySample
    {
        SimulatorState = TelemetrySimulatorState.Running, FlightSegment = 1,
        AnalysisTimeSeconds = time, SimulationTimeSeconds = time, HostElapsedSeconds = time,
        MotionSimulation = true, OnGround = ground, AboveGroundLevelFeet = ground ? 3 : 20,
        VelocityWorldYFps = ground ? 0 : -2, VerticalSpeedFps = ground ? 0 : -2,
        GForce = 1, PlaneAltitudeFeet = 1000, GroundAltitudeFeet = 997,
        GroundSpeedKnots = 120, IndicatedAirspeedKnots = 120, TouchdownNormalVelocityFps = ground ? 2 : 0,
    };

    private static IReadOnlyList<LandingRecord> Process(IReadOnlyList<TelemetrySample> samples)
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-stats-pause-pipeline-" + Guid.NewGuid().ToString("N"));
        var processor = new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), new LandingRepository(directory), new object());
        try { return Process(processor, samples); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static IReadOnlyList<LandingRecord> Process(EpisodeProcessor processor, IReadOnlyList<TelemetrySample> samples)
    {
        var episode = new LandingEpisodeEventArgs(1, samples, "MSFS", "TEST", "TEST", "TEST",
            Array.Empty<AirportFacility>(), Array.Empty<string>(), persistentId: System.Guid.NewGuid());
        return ReportPipelineFixture.Complete(processor.ProcessAsync(episode, Array.Empty<AirportFacility>(), DateTime.UtcNow)).Records;
    }

    private static object? Invoke(object instance, string name, params object[] args) =>
        RecorderTestAccess.Call(instance, name, args);
    private static object? Get(object instance, string name) => RecorderTestAccess.Get(instance, name);
    private static void Set(object instance, string name, object? value) => RecorderTestAccess.Set(instance, name, value);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
