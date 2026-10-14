using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class RepeatedContactGeometryRegressionTests
{
    // Frozen pre-change full-capture results: time, inertial FPM, latch FPM, peak G.
    private static readonly Dictionary<string, double[][]> OriginalMetrics = new Dictionary<string, double[][]>
    {
        ["13dc14ff"] = new[]
        {
            new[] { 782.8211497677257, 117.68813754932727, 260.57464599609375, 1.4696526529962524 },
            new[] { 783.6693163648015, 159.31990595437324, 126.42568588256836, 1.1073389615921374 },
            new[] { 784.3900808620965, 51.6247693377661, 23.214397430419922, 1.0971412506658715 },
        },
        ["cb48a7b6"] = new[] { new[] { 747.6819473368814, 365.8650402397976, 306.39132499694824, 1.3404523816637797 } },
        ["e78d3f80"] = new[]
        {
            new[] { 610.3312060747994, 386.15289940642646, 326.31285667419434, 1.4726561806785217 },
            new[] { 610.992088924977, 40.57573771786469, 0.11839788872748613, 1.1021185506073554 },
            new[] { 611.4343407748966, 118.9945345235198, 81.30869150161743, 1.0952964822896818 },
        },
    };

    public static void RecordedMainUnloadsRecoverEarlierRoles()
    {
        foreach (var capture in new[] { "0ca2ec0e", "790d1cd7" })
        {
            var samples = Recorded(capture);
            Assert(TelemetryGeometryCalibration.TryDetectConventionalTopology(samples, out var topology), capture + " topology");
            Assert(topology.NoseContactPointIndices.SequenceEqual(new[] { 0 }), capture + " nose role");
            Assert(topology.MainContactPointIndices.OrderBy(p => p).SequenceEqual(new[] { 1, 2 }), capture + " mains");
            Assert(TelemetryGeometryCalibration.TryCalibrate(samples, out var calibration), capture + " calibration");
            Assert(calibration.MainOnlySampleCount >= 12, capture + " independent main-only observations");
            var result = TouchdownAnalysis.Analyze(samples)[0];
            Assert(result.ClosureReconstructionAvailable, capture + " reconstruction");
            Assert(result.ClosureReconstructionGeometrySource == TouchdownGeometrySource.Telemetry, capture + " provenance");
            Assert(result.ClosureReconstructionUncertaintyFpm > 0, capture + " uncertainty");
        }
    }

    public static void RecordedSpatialRolesPreserveContactGuards()
    {
        foreach (var capture in new[] { "13dc14ff", "e78d3f80", "cb48a7b6" })
        {
            var samples = Recorded(capture);
            Assert(TelemetryGeometryCalibration.TryDetectConventionalTopology(samples, out var topology), capture + " spatial roles");
            var nose = capture == "13dc14ff" ? 0 : 7;
            Assert(topology.UsesSpatialInference && topology.NoseContactPointIndices.SequenceEqual(new[] { nose }), capture + " forward point");
            Assert(topology.MainContactPointIndices.SequenceEqual(new[] { nose + 1, nose + 2 }), capture + " lateral mains");
            var journal = new TelemetryDecisionJournal();
            var results = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal });
            Assert(results.Count == (capture == "cb48a7b6" ? 1 : 3), capture + " contacts preserved");
            Assert(results.All(r => !r.ClosureReconstructionAvailable && double.IsNaN(r.ClosureReconstructionUncertaintyFpm)), capture + " unavailable is not zero uncertainty");
            Assert(journal.Snapshot().Entries.Any(e => e.Component == "reconstruction" && e.Code == "nose_contact_present"), capture + " contact diagnostic");
            var original = OriginalMetrics[capture];
            for (var index = 0; index < results.Count; index++)
            {
                Near(original[index][0], results[index].SimulationTimeSeconds, "unchanged contact time");
                Near(original[index][1], results[index].InertialVerticalFpm, "unchanged inertial");
                Near(original[index][2], results[index].LatchedNormalFpm, "unchanged latch");
                Near(original[index][3], results[index].PeakG, "unchanged G");
            }
        }
    }

    public static void EarlierPhaseNeedsStableGeometryAndContinuousPhysics()
    {
        var samples = AnalyticTrace();
        Assert(TelemetryGeometryCalibration.TryCalibrate(samples, out var calibration), "analytic repeated-contact calibration");
        Near(-12, calibration.PitchArmFeet, "analytic pitch arm");
        Near(4, calibration.DatumOffsetFeet, "analytic datum");
        Near(-8, calibration.LongitudinalArmFeet, "analytic arm");

        var drift = AnalyticTrace();
        foreach (var s in drift.Where(s => s.SimulationTimeSeconds >= 0))
            s.PlaneAltitudeFeet += 10 * Math.Pow(Math.Max(0, s.SimulationTimeSeconds - .5), 2);
        Assert(!TelemetryGeometryCalibration.TryDetectConventionalTopology(drift, out _, out var driftFailure) &&
            driftFailure == TelemetryGeometryFailure.UnstablePitchArm, "new roles require stable arm");
        var suppliedArm = new TouchdownAnalysisOptions { LongitudinalMainGearArmFeet = -8, RecoverLongitudinalMainGearArmFromTelemetry = false };
        Assert(!TouchdownAnalysis.Analyze(drift, suppliedArm)[0].ClosureReconstructionAvailable, "a supplied scalar arm cannot establish new roles");

        foreach (var kind in new[] { "motion", "gap", "brief_nose" })
        {
            var broken = AnalyticTrace();
            if (kind == "gap") broken.RemoveAll(s => s.SimulationTimeSeconds >= 1.0 && s.SimulationTimeSeconds <= 1.4);
            else foreach (var s in broken.Where(s => Math.Abs(s.SimulationTimeSeconds - 1.35) < .001))
            {
                if (kind == "motion") s.MotionSimulation = false;
                else s.ContactPointOnGround[8] = true;
            }
            Assert(!TelemetryGeometryCalibration.TryDetectConventionalTopology(broken, out _), "derotation cannot cross " + kind);
        }

        var scrape = AnalyticTrace();
        foreach (var s in scrape.Where(s => s.SimulationTimeSeconds >= .5 && s.SimulationTimeSeconds <= .8))
            s.ContactPointOnGround[18] = true;
        Assert(!TelemetryGeometryCalibration.TryDetectConventionalTopology(scrape, out _), "sustained foreign contact is not ignored");
    }

    public static void ConfiguredRolesAndProvenanceKeepPriority()
    {
        var samples = Recorded("e78d3f80");
        var options = new TouchdownAnalysisOptions
        {
            LongitudinalMainGearArmFeet = -8,
            LongitudinalMainGearArmSource = TouchdownGeometrySource.FlightModelConfig,
            MainGearContactPoints = new[] { new TouchdownMainGearContactPoint(7, -8), new TouchdownMainGearContactPoint(9, -8) },
            NoseGearContactPointIndices = new[] { 8 },
        };
        // These injected roles exercise priority, not an assertion about the
        // encrypted C172 configuration. Only the Fenix fixture has cfg evidence.
        var results = TouchdownAnalysis.Analyze(samples, options);
        Assert(results[0].ClosureReconstructionAvailable && results[2].ClosureReconstructionAvailable, "configured roles bypass ambiguous telemetry");
        Assert(!results[1].ClosureReconstructionAvailable, "configured geometry retains contact guards");
        foreach (var result in results.Where(r => r.ClosureReconstructionAvailable))
        {
            Assert(result.ClosureReconstructionGeometrySource == TouchdownGeometrySource.FlightModelConfig, "configured provenance");
            Near(15, result.ClosureReconstructionUncertaintyFpm, "configured uncertainty");
        }
    }

    public static void RepeatedContactInferenceIgnoresLatchIndicesAndDelivery()
    {
        var samples = Recorded("0ca2ec0e");
        Assert(TelemetryGeometryCalibration.TryCalibrate(samples, out var baseline), "recorded calibration control");
        var original = TouchdownAnalysis.Analyze(samples)[0];
        foreach (var s in samples)
        {
            s.SimulationTimeSeconds += 100000;
            s.HostElapsedSeconds = 42;
            s.TouchdownNormalVelocityFps = s.OnGround ? 9999 : -123;
            Array.Reverse(s.ContactPointOnGround);
            Array.Reverse(s.ContactPointCompression);
        }
        var duplicates = samples.SelectMany(s => new[] { s, s }).ToList();
        Assert(TelemetryGeometryCalibration.TryCalibrate(duplicates, out var changed), "permuted calibration");
        Near(baseline.LongitudinalArmFeet, changed.LongitudinalArmFeet, "independent arm");
        Assert(TelemetryGeometryCalibration.TryDetectConventionalTopology(duplicates, out var topology) &&
            topology.NoseContactPointIndices.SequenceEqual(new[] { 19 }), "indices are anonymous");
        var result = TouchdownAnalysis.Analyze(TelemetryDeduplicator.Deduplicate(duplicates))[0];
        Near(original.ReconstructedClosureFpm, result.ReconstructedClosureFpm, "independent reconstruction", 1e-5);
    }

    private static List<TelemetrySample> AnalyticTrace()
    {
        var samples = new List<TelemetrySample>();
        var altitude = 0.0;
        var previousVertical = 0.0;
        var atContact = 0.0;
        for (var index = 0; index <= 240; index++)
        {
            var time = -3 + index * .025;
            var pitch = (-4 + .4 * time) * Math.PI / 180;
            var compression = 20 + 5 * Math.Cos(3 * time);
            var q = .01 + .004 * Math.Sin(1.7 * time) + .001 * time;
            var worldY = -4 + .15 * Math.Sin(.9 * time);
            var vertical = worldY - 4 * Math.Cos(pitch) * q;
            if (index > 0) altitude += .0125 * (previousVertical + vertical);
            previousVertical = vertical;
            if (index == 120) atContact = altitude;
            var sample = new TelemetrySample
            {
                Sequence = index, SimulationTimeSeconds = time, OnGround = time >= 0, MotionSimulation = true,
                PitchDegrees = pitch * 180 / Math.PI, VelocityWorldYFps = worldY,
                RotationVelocityBodyXRadiansPerSecond = q, GroundAltitudeFeet = 100,
                PlaneAltitudeFeet = time < 0 ? altitude : 114 - 12 * pitch - .02 * compression,
                GForce = 1.1, TouchdownNormalVelocityFps = time >= 0 ? 280.0 / 60 : 0,
            };
            if (time >= 0)
            {
                sample.ContactPointOnGround[7] = true;
                sample.ContactPointCompression[7] = compression;
                if (time < 1.30 - 1e-9 || time >= 1.70 - 1e-9)
                {
                    sample.ContactPointOnGround[9] = true;
                    sample.ContactPointCompression[9] = compression;
                }
                sample.ContactPointOnGround[8] = time >= 2 - 1e-9;
            }
            samples.Add(sample);
        }
        var shift = samples[120].PlaneAltitudeFeet - atContact;
        foreach (var s in samples.Where(s => !s.OnGround)) s.PlaneAltitudeFeet += shift;
        return samples;
    }

    internal static List<TelemetrySample> Recorded(string id)
    {
        var lines = File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "geometry-ls001-" + id + ".csv"));
        var points = lines[1].Split(new[] { "points=" }, StringSplitOptions.None)[1].Split(',').Select(int.Parse).ToArray();
        var result = new List<TelemetrySample>();
        foreach (var line in lines.Skip(3))
        {
            var v = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            var s = new TelemetrySample
            {
                Sequence = result.Count, SimulationTimeSeconds = v[0], OnGround = v[1] != 0, MotionSimulation = v[2] != 0,
                PitchDegrees = v[3], BankDegrees = v[4], PlaneAltitudeFeet = v[5], GroundAltitudeFeet = v[6],
                VelocityWorldYFps = v[7], RotationVelocityBodyXRadiansPerSecond = v[8], RotationVelocityBodyYRadiansPerSecond = v[9],
                TouchdownNormalVelocityFps = v[10], GForce = v[11],
            };
            for (var p = 0; p < points.Length; p++)
            {
                s.ContactPointOnGround[points[p]] = ((int)v[12] & (1 << p)) != 0;
                s.ContactPointCompression[points[p]] = v[13 + p];
                if (v.Length >= 24) s.ContactPointPosition[points[p]] = v[21 + p];
            }
            if (v.Length >= 24)
            {
                s.GroundSpeedKnots = v[16]; s.HeadingTrueDegrees = v[17];
                s.LatitudeDegrees = v[18]; s.LongitudeDegrees = v[19];
                s.RotationVelocityBodyZRadiansPerSecond = v[20];
            }
            result.Add(s);
        }
        return result;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double expected, double actual, string message, double tolerance = 1e-6)
    {
        Assert(!double.IsNaN(actual) && Math.Abs(expected - actual) <= tolerance,
            message + ": expected " + expected + ", got " + actual);
    }
}
