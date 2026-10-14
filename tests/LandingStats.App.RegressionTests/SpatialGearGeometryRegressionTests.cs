using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class SpatialGearGeometryRegressionTests
{
    public static void AnalyticRolesAndArm()
    {
        foreach (var noseFirst in new[] { false, true })
        {
            var samples = Analytic(noseFirst);
            Assert(TelemetrySpatialGearTopology.TryInfer(samples, new[] { 0, 1, 2 }, out var evidence), "spatial evidence");
            Assert(evidence.StableRoles && evidence.Nose == 0 && evidence.StableMainArm, "analytic tricycle roles");
            Near(-2, evidence.PitchArmFeet, "specified physical main axis");
            Assert(TelemetryGeometryCalibration.TryCalibrate(samples, out var calibration), "loaded-wheel calibration");
            Assert(calibration.UsesSpatialArmInference && calibration.MainOnlySampleCount == 0, "no invented main-only interval");
            Near(4, calibration.DatumOffsetFeet, "independent airborne datum");
            Near(2, calibration.LongitudinalArmFeet, "main axis plus datum");
            var result = TouchdownAnalysis.Analyze(samples).Single();
            Assert(result.ClosureReconstructionAvailable == !noseFirst, "nose-first guard survives known geometry");
            if (!noseFirst)
            {
                Near(15, result.ClosureReconstructionUncertaintyFpm, "spatial fallback reporting band");
                Assert(result.ClosureReconstructionGeometrySource == TouchdownGeometrySource.Telemetry, "telemetry provenance");
            }
        }
    }

    public static void UnobservableAndInvalidGeometry()
    {
        foreach (var kind in new[] { "no_bank", "noise", "compression_sign", "taildragger", "missing_wheel", "collinear" })
        {
            var samples = Analytic(false, kind == "taildragger", kind == "compression_sign");
            foreach (var s in samples)
            {
                if (kind == "no_bank") s.BankDegrees = 0;
                if (kind == "noise" && s.OnGround) s.PlaneAltitudeFeet += .4 * Math.Sin(31 * s.SimulationTimeSeconds);
                if (kind == "missing_wheel") { s.ContactPointOnGround[2] = false; s.ContactPointCompression[2] = 0; }
                if (kind == "collinear") s.BankDegrees = Math.Atan(2 * Math.Tan(s.PitchDegrees * Math.PI / 180) * Math.Cos(s.PitchDegrees * Math.PI / 180)) * 180 / Math.PI;
            }
            Assert(!TelemetrySpatialGearTopology.TryInfer(samples, new[] { 0, 1, 2 }, out _), "reject " + kind);
        }
    }

    public static void PhysicsAndGearBoundaries()
    {
        foreach (var kind in new[] { "motion", "gap", "flight", "extension" })
        {
            var samples = Analytic(false);
            if (kind == "gap") samples.RemoveAll(s => s.SimulationTimeSeconds >= .20 && s.SimulationTimeSeconds <= .40);
            else foreach (var s in samples.Where(s => s.SimulationTimeSeconds >= .20))
            {
                if (kind == "motion") s.MotionSimulation = false;
                if (kind == "flight") s.FlightSegment = 1;
                if (kind == "extension") s.ContactPointPosition[0] = .5;
            }
            Assert(!TelemetrySpatialGearTopology.TryInfer(samples, new[] { 0, 1, 2 }, out _), "cannot pool across " + kind);
        }
    }

    public static void RecordedArmAndConflicts()
    {
        var samples = RepeatedContactGeometryRegressionTests.Recorded("c365f369");
        Assert(TelemetryGeometryCalibration.TryCalibrate(samples, out var fit), "C172 loaded-wheel fit");
        Assert(fit.UsesSpatialArmInference && fit.LoadedWheelSampleCount >= 40 && fit.MainOnlySampleCount == 0, "C172 calibration source");
        Assert(fit.PitchArmFeet > -1.7 && fit.PitchArmFeet < -1.4, "C172 independent main axis");
        var journal = new TelemetryDecisionJournal();
        var result = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal }).Single();
        Assert(result.ClosureReconstructionAvailable, "C172 reconstruction recovered");
        Near(15, result.ClosureReconstructionUncertaintyFpm, "new inference uses fallback band");
        Assert(journal.Snapshot().Entries.Any(e => e.Code == "spatial_arm_calibrated"), "spatial arm diagnosis");

        var conflict = RepeatedContactGeometryRegressionTests.Recorded("0b4d614d");
        Assert(!TelemetryGeometryCalibration.TryDetectConventionalTopology(conflict, out _, out var failure) &&
            failure == TelemetryGeometryFailure.ConflictingSpatialRoles, "unstable spatial evidence vetoes contradictory timing");
        var unstable = RepeatedContactGeometryRegressionTests.Recorded("a54ff64e");
        Assert(TelemetryGeometryCalibration.TryDetectConventionalTopology(unstable, out var topology) &&
            topology.NoseContactPointIndices.SequenceEqual(new[] { 7 }), "roles identifiable despite arm refusal");
        Assert(!TelemetryGeometryCalibration.TryCalibrate(unstable, out _, out var armFailure) &&
            armFailure == TelemetryGeometryFailure.UnstableSpatialArm, "compression sign fails on temporal subsets");
        Assert(!TouchdownAnalysis.Analyze(unstable).Single().ClosureReconstructionAvailable, "no numeric arm from stable roles alone");
    }

    public static void NumberingAndLatchIndependence()
    {
        foreach (var id in new[] { "13dc14ff", "e78d3f80", "c365f369" })
        {
            var samples = RepeatedContactGeometryRegressionTests.Recorded(id);
            Assert(TelemetryGeometryCalibration.TryDetectConventionalTopology(samples, out var before), "recorded roles before");
            var calibrated = TelemetryGeometryCalibration.TryCalibrate(samples, out var arm);
            foreach (var s in samples)
            {
                s.SimulationTimeSeconds += 100000;
                s.HostElapsedSeconds = 42;
                s.TouchdownNormalVelocityFps = 9999;
                Array.Reverse(s.ContactPointCompression);
                Array.Reverse(s.ContactPointOnGround);
                Array.Reverse(s.ContactPointPosition);
            }
            var duplicate = samples.SelectMany(s => new[] { s, s }).ToList();
            Assert(TelemetryGeometryCalibration.TryDetectConventionalTopology(duplicate, out var after), "recorded roles after");
            Assert(after.NoseContactPointIndices.Single() == 19 - before.NoseContactPointIndices.Single(), "anonymous indices");
            Assert(TelemetryGeometryCalibration.TryCalibrate(duplicate, out var changed) == calibrated, "same calibration availability");
            if (calibrated) Near(arm.LongitudinalArmFeet, changed.LongitudinalArmFeet, "no latch or delivery dependence");
        }
    }

    // Independent rigid-body generator: contact positions are specified first;
    // solve compression from the wheel's zero-height constraint. Airborne altitude
    // separately integrates WORLD Y plus a known four-foot datum offset.
    private static List<TelemetrySample> Analytic(bool noseFirst, bool taildragger = false, bool positiveCompression = false)
    {
        var result = new List<TelemetrySample>();
        var altitude = 0.0; var previousVertical = 0.0; var atContact = 0.0;
        for (var index = 0; index <= 800; index++)
        {
            var t = -4 + index * .025;
            var theta = .03 * Math.Sin(.8 * t) + .012 * Math.Cos(2.1 * t);
            var phi = .022 * Math.Sin(1.7 * t) + .007 * Math.Cos(.4 * t);
            var height = 5.5 + .1 * Math.Sin(.53 * t) + .08 * Math.Cos(2.3 * t);
            var q = .024 * Math.Cos(.8 * t) - .0252 * Math.Sin(2.1 * t);
            var vy = -4 + .2 * Math.Sin(.9 * t);
            var vertical = vy - 4 * Math.Cos(theta) * Math.Cos(phi) * q;
            if (index > 0) altitude += .0125 * (previousVertical + vertical);
            previousVertical = vertical;
            if (index == 160) atContact = altitude;
            var s = new TelemetrySample
            {
                Sequence = index, SimulationTimeSeconds = t, MotionSimulation = true, OnGround = t >= 0,
                PlaneAltitudeFeet = t < 0 ? altitude : 100 + height, GroundAltitudeFeet = 100,
                PitchDegrees = theta * 180 / Math.PI, BankDegrees = phi * 180 / Math.PI,
                VelocityWorldYFps = vy, RotationVelocityBodyXRadiansPerSecond = q, GForce = 1.1,
                TouchdownNormalVelocityFps = t >= 0 ? 4 : 0,
            };
            for (var p = 0; p < 3; p++)
            {
                var contactTime = noseFirst ? (p == 0 ? 0 : p == 1 ? .15 : .35) : (p == 0 ? .1 : 0);
                if (t + 1e-9 < contactTime) continue;
                var x = taildragger ? (p == 0 ? -6 : 2) : (p == 0 ? 6 : -2);
                var y = p == 0 ? 0 : p == 1 ? 4 : -4;
                var c = (6 + x * Math.Tan(theta) + y * Math.Tan(phi) / Math.Cos(theta) - height / (Math.Cos(theta) * Math.Cos(phi))) / .01;
                s.ContactPointOnGround[p] = true;
                s.ContactPointCompression[p] = positiveCompression ? 100 - c : c;
            }
            result.Add(s);
        }
        var shift = result[160].PlaneAltitudeFeet - atContact;
        foreach (var s in result.Where(s => !s.OnGround)) s.PlaneAltitudeFeet += shift;
        return result;
    }

    private static void Near(double expected, double actual, string message)
        => Assert(!double.IsNaN(actual) && Math.Abs(expected - actual) < 1e-5, message + ": " + actual);
    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
