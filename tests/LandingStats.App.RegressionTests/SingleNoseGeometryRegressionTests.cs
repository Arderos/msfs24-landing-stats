using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class SingleNoseGeometryRegressionTests
{
    private static readonly int[] Mains = { 17, 4, 8, 11, 2, 6 };
    private static readonly double[] MainOnsets = { 0, .10, .70, 1.25, 1.80, 2.40 };
    private const int Nose = 9;

    public static void StableMultiMainSingleNose()
    {
        var samples = Trace();
        Assert(TelemetryGeometryCalibration.TryDetectConventionalTopology(samples, out var topology), "single-nose topology");
        Assert(topology.MainContactPointIndices.SequenceEqual(Mains), "main points come from timing, not indices");
        Assert(topology.NoseContactPointIndices.SequenceEqual(new[] { Nose }), "one late nose point");
        Assert(topology.UsesClusteredWheelInference, "multi-main uncertainty provenance");
        Assert(TelemetryGeometryCalibration.TryCalibrate(samples, out var calibration), "stable calibration");
        Near(-12, calibration.PitchArmFeet, "analytic pitch arm");
        Near(4, calibration.DatumOffsetFeet, "analytic datum");
        Near(-8, calibration.LongitudinalArmFeet, "analytic final arm");
        var result = TouchdownAnalysis.Analyze(samples).Single();
        Assert(result.ClosureReconstructionAvailable, "end-to-end reconstruction");
        Near(50, result.ClosureReconstructionUncertaintyFpm, "conservative uncertainty");
        Assert(result.ClosureReconstructionGeometrySource == TouchdownGeometrySource.Telemetry, "inferred provenance");
    }

    public static void StabilityRejectsDriftAndDegenerateSubsets()
    {
        var drift = Trace(drift: true);
        Assert(TelemetryGeometryCalibration.TryCalibrate(ConventionalCopy(drift), out _), "full fit alone accepts drift control");
        Assert(!TelemetryGeometryCalibration.TryDetectConventionalTopology(drift, out _), "temporal drift rejects new topology");
        Assert(!TelemetryGeometryCalibration.TryCalibrate(drift, out _), "drifting arm rejected");
        var supplied = new TouchdownAnalysisOptions { LongitudinalMainGearArmFeet = -8, RecoverLongitudinalMainGearArmFromTelemetry = false };
        Assert(!TouchdownAnalysis.Analyze(drift, supplied).Single().ClosureReconstructionAvailable,
            "supplying an arm cannot bypass topology stability");

        var degenerate = Trace(degenerateSubset: true);
        Assert(TelemetryGeometryCalibration.TryCalibrate(ConventionalCopy(degenerate), out _), "full fit identifies degenerate-subset control");
        Assert(!TelemetryGeometryCalibration.TryCalibrate(degenerate, out _), "unidentifiable subset is refusal, not a skipped check");

        var sparse = Trace().Where(s => s.SimulationTimeSeconds < 2.40 - 1e-9 ||
            s.SimulationTimeSeconds >= 4.40 - 1e-9 || s.Sequence % 8 == 0).ToList();
        Assert(!TelemetryGeometryCalibration.TryCalibrate(sparse, out _), "insufficient subset observations");
    }

    public static void ExistingAndConfiguredPathsKeepPriority()
    {
        var samples = Trace(drift: true);
        var options = new TouchdownAnalysisOptions
        {
            LongitudinalMainGearArmFeet = -8,
            LongitudinalMainGearArmSource = TouchdownGeometrySource.FlightModelConfig,
            MainGearContactPoints = Mains.Select(p => new TouchdownMainGearContactPoint(p, -8)).ToArray(),
            NoseGearContactPointIndices = new[] { Nose },
        };
        var configured = TouchdownAnalysis.Analyze(samples, options).Single();
        Assert(configured.ClosureReconstructionAvailable, "verified configured roles bypass telemetry inference");
        Near(15, configured.ClosureReconstructionUncertaintyFpm, "configured uncertainty unchanged");
        Near(-8, configured.ClosureReconstructionLongitudinalArmFeet, "configured arm unchanged");

        // An established clustered topology has two late points. It must remain
        // selected even if the longer single-last-point hypothesis is possible.
        var clustered = Trace();
        foreach (var sample in clustered)
        {
            var t = sample.SimulationTimeSeconds;
            if (t >= 4.45 - 1e-9) sample.ContactPointOnGround[19] = true;
        }
        Assert(TelemetryGeometryCalibration.TryDetectConventionalTopology(clustered, out var topology), "existing clustered path");
        Assert(topology.NoseContactPointIndices.SequenceEqual(new[] { Nose, 19 }), "cluster is not replaced by singleton");
        Assert(TelemetryGeometryCalibration.TryCalibrate(clustered, out var calibration), "existing clustered calibration");
        Near(-8, calibration.LongitudinalArmFeet, "existing clustered result");
    }

    public static void FallbackPreservesContactGuards()
    {
        var extra = Trace();
        foreach (var s in extra.Where(s => s.SimulationTimeSeconds >= .5 && s.SimulationTimeSeconds <= .8))
        {
            s.ContactPointOnGround[18] = true;
            s.ContactPointCompression[18] = 20;
        }
        Assert(!TelemetryGeometryCalibration.TryDetectConventionalTopology(extra, out _), "foreign sustained contact is not silently discarded");

        var flat = Trace();
        foreach (var s in flat) s.PitchDegrees = -4;
        Assert(!TelemetryGeometryCalibration.TryDetectConventionalTopology(flat, out _), "no derotation");

        var brief = Trace();
        foreach (var s in brief)
        {
            s.ContactPointOnGround[Nose] = false;
            s.ContactPointCompression[Nose] = 0;
            // Other mains settle without derotation in this control. Otherwise
            // a different sustained late point could itself be a nose candidate.
            if (s.SimulationTimeSeconds <= MainOnsets[MainOnsets.Length - 1]) s.PitchDegrees = -5;
        }
        brief[brief.Count - 1].ContactPointOnGround[Nose] = true;
        Assert(!TelemetryGeometryCalibration.TryCalibrate(brief, out _), "one-frame late contact does not supply a stable nose phase");
    }

    public static void CalibrationIsIndependentOfLatchAndDelivery()
    {
        var samples = Trace();
        var original = TouchdownAnalysis.Analyze(samples).Single();
        foreach (var s in samples)
        {
            s.TouchdownNormalVelocityFps = s.OnGround ? 9999 : -123;
            s.HostElapsedSeconds = 42;
            s.SimulationTimeSeconds += 100000;
            Array.Reverse(s.ContactPointCompression);
            Array.Reverse(s.ContactPointOnGround);
        }
        var duplicates = samples.SelectMany(s => new[] { s, s }).ToList();
        Assert(TelemetryGeometryCalibration.TryCalibrate(duplicates, out var calibration), "deduplicated shifted/permuted telemetry");
        Near(-8, calibration.LongitudinalArmFeet, "latch/time/indices independent geometry");
        var result = TouchdownAnalysis.Analyze(TelemetryDeduplicator.Deduplicate(duplicates)).Single();
        Assert(result.ClosureReconstructionAvailable, "permuted reconstruction");
        Near(original.ReconstructedClosureFpm, result.ReconstructedClosureFpm, "latch not used as reconstruction target");
    }

    public static void CalibrationRejectsNonMotionTelemetry()
    {
        var samples = Trace();
        Assert(TelemetryGeometryCalibration.TryRecoverDatumOffset(samples, out _), "active datum calibration fixture");
        foreach (var sample in samples) sample.MotionSimulation = false;
        Assert(!TelemetryGeometryCalibration.TryRecoverDatumOffset(samples, out _), "non-motion telemetry calibrated a datum offset");
        Assert(!TelemetryGeometryCalibration.TryCalibrate(samples, out _), "non-motion telemetry calibrated landing gear geometry");
    }

    private static List<TelemetrySample> Trace(bool drift = false, bool degenerateSubset = false)
    {
        var samples = new List<TelemetrySample>();
        var altitude = 0.0;
        var previousVertical = 0.0;
        var contactIntegrated = 0.0;
        for (var index = 0; index <= 360; index++)
        {
            var time = -4 + index * .025;
            var pitch = time < 0 ? 0 : -.10 + .018 * time + .002 * Math.Sin(2.3 * time);
            var compression = degenerateSubset
                ? 30 + 100 * pitch + (time > 3.80 ? 20 * Math.Pow(time - 3.80, 2) : 0)
                : 20 + 5 * Math.Cos(3 * time);
            var q = .01 + .004 * Math.Sin(1.7 * time) + .001 * time;
            var worldY = -4 + .15 * Math.Sin(.9 * time);
            var vertical = worldY - 4 * q;
            if (index > 0) altitude += .0125 * (previousVertical + vertical);
            previousVertical = vertical;
            if (index == 160) contactIntegrated = altitude;
            var sample = new TelemetrySample
            {
                Sequence = index, SimulationTimeSeconds = time, HostElapsedSeconds = time + 4,
                OnGround = time >= 0, MotionSimulation = true, GForce = 1.1,
                TouchdownNormalVelocityFps = time >= 0 ? 280.0 / 60 : 0,
                VelocityWorldYFps = worldY, RotationVelocityBodyXRadiansPerSecond = q,
                PitchDegrees = pitch * 180 / Math.PI, GroundAltitudeFeet = 100,
                PlaneAltitudeFeet = time < 0 ? altitude : 114 - 12 * pitch - .02 * compression,
            };
            if (drift && time >= 0)
                sample.PlaneAltitudeFeet += 80 * Math.Pow(Math.Max(0, time - 3.2), 2) * .02;
            for (var main = 0; main < Mains.Length; main++)
            {
                if (time < MainOnsets[main] - 1e-9) continue;
                sample.ContactPointOnGround[Mains[main]] = true;
                sample.ContactPointCompression[Mains[main]] = compression;
            }
            sample.ContactPointOnGround[Nose] = time >= 4.40 - 1e-9;
            samples.Add(sample);
        }
        var shift = samples[160].PlaneAltitudeFeet - contactIntegrated;
        foreach (var s in samples.Where(s => !s.OnGround)) s.PlaneAltitudeFeet += shift;
        return samples;
    }

    private static List<TelemetrySample> ConventionalCopy(List<TelemetrySample> samples)
    {
        // Keep exactly two main channels and the same delayed nose to demonstrate
        // that the full regression works; only the new stability check refuses.
        return samples.Select(s =>
        {
            Assert(TelemetryCsv.TryParse(TelemetryCsv.Format(s), out var copy), "synthetic sample round-trip");
            foreach (var point in Mains.Skip(2))
            {
                copy.ContactPointOnGround[point] = false;
                copy.ContactPointCompression[point] = 0;
            }
            return copy;
        }).ToList();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double expected, double actual, string message)
    {
        Assert(!double.IsNaN(actual) && Math.Abs(expected - actual) <= 1e-6,
            message + ": expected " + expected + ", got " + actual);
    }
}
