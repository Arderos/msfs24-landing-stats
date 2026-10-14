using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ContactPointGeometryRegressionTests
{
    public static void KnownRotatingPointOnSlopedGround()
    {
        foreach (var slope in new[] { -.02, 0, .02 })
        foreach (var point in new[] { 2, 11 })
        foreach (var bankScale in new[] { .025, .4 })
        {
            var samples = Analytic(slope, point, bankScale);
            var model = new TelemetryContactPointReconstruction(samples, 0);
            Assert(model.TryEstimate(0, -.01, out var e, out var decision), "specified point: " + decision);
            // The generator prescribes clearance decreasing at exactly 4 ft/s.
            Near(240, e.Closure, 1, "known physical clearance velocity");
            Near(e.Closure, e.Inertial + e.Terrain + e.Rotation, 1e-8, "component sum");
            Assert(e.Points.SequenceEqual(new[] { point }), "no nose/main or numbering assumption");
            Near(14, e.Arm, .02, "specified point X plus independently recovered datum");
        }
    }

    public static void RecordedNoseAndUnstableC172()
    {
        var vision = TouchdownAnalysis.Analyze(Recorded("13dc14ff"));
        Assert(vision.Count == 3 && vision.All(r => r.ClosureReconstructionAvailable), "three observed nose contacts");
        var expected = new[] { 230.2, 184.4, 61.7 };
        for (var i = 0; i < vision.Count; i++)
        {
            var r = vision[i];
            Assert(r.ClosureReconstructionModel == TelemetryContactPointReconstruction.ModelName, "new model provenance");
            Assert(r.ClosureReconstructionContactPointIndices!.SequenceEqual(new[] { 0 }), "actual contacting point");
            // Cropping the long approach changes the independent datum fit;
            // allow that difference within the minimum 15 FPM reporting band.
            Near(expected[i], r.ReconstructedClosureFpm, 10, "independent full-capture NumPy prediction");
            Assert(r.ClosureReconstructionUncertaintyFpm >= 15 && r.ClosureReconstructionUncertaintyFpm <= 50, "explicit sensitivity band");
            Assert(r.ReconstructedRotationFpm.HasValue && double.IsNaN(r.ReconstructedPitchFpm), "rotation is not mislabeled pitch");
        }
        Assert(vision[2].ClosureReconstructionUncertaintyFpm > vision[0].ClosureReconstructionUncertaintyFpm + 5,
            "short bounce reports its larger uncertainty");
        foreach (var id in new[] { "0b4d614d", "55640bab", "e7948f52", "72f4cf73", "a54ff64e", "ab852b22", "c9c4d438", "3f8c37b6", "8335fc40", "f3e2e48a" })
        {
            var results = TouchdownAnalysis.Analyze(Recorded(id));
            Assert(results[0].ClosureReconstructionAvailable, id + " observed point recovered despite whole-gear ambiguity");
            Assert(results[0].ReconstructedRotationFpm.HasValue, id + " full rotation provenance");
        }
        var bounce = TouchdownAnalysis.Analyze(Recorded("e78d3f80"));
        Assert(bounce[0].ClosureReconstructionAvailable &&
            bounce[0].ClosureReconstructionModel == TelemetryContactPointReconstruction.BoundedModelName,
            "all admissible geometries bound the simultaneous contact");
        Assert(bounce[0].ClosureReconstructionContactPointIndices!.SequenceEqual(new[] { 7, 9 }),
            "the weaker simultaneous point is not dropped");
        Assert(!bounce[1].ClosureReconstructionAvailable, "one-frame contact remains unavailable");
        Assert(bounce[2].ClosureReconstructionAvailable &&
            bounce[2].ClosureReconstructionModel == TelemetryContactPointReconstruction.KinematicModelName,
            "recorded native motion resolves the nose bounce");
        Near(94.2647, bounce[2].ReconstructedClosureFpm, 10, "independent full-capture kinematic prediction");
        Assert(bounce[2].ClosureReconstructionUncertaintyFpm >= 40 && bounce[2].ClosureReconstructionUncertaintyFpm <= 50,
            "remaining geometric uncertainty is still reported");
    }

    public static void RejectsMissingAndInconsistentEvidence()
    {
        foreach (var kind in new[] { "attitude", "extension", "physics", "gap", "short_precontact", "foreign_point", "height_noise", "skid" })
        {
            var samples = Analytic(.02, 11);
            if (kind == "gap") samples.RemoveAll(s => s.SimulationTimeSeconds >= .2 && s.SimulationTimeSeconds < .4);
            if (kind == "short_precontact") samples.RemoveAll(s => s.SimulationTimeSeconds > -.25 && s.SimulationTimeSeconds < -.05);
            foreach (var s in samples)
            {
                if (kind == "attitude") { s.PitchDegrees = 0; s.BankDegrees = 0; }
                if (kind == "extension" && s.SimulationTimeSeconds >= .2) s.ContactPointPosition[11] = 50;
                if (kind == "physics" && s.SimulationTimeSeconds >= .2) s.MotionSimulation = false;
                if (kind == "foreign_point" && s.SimulationTimeSeconds >= 0 && s.SimulationTimeSeconds < .2) s.ContactPointOnGround[19] = true;
                if (kind == "height_noise" && s.OnGround) s.PlaneAltitudeFeet += .5 * Math.Sin(31 * s.SimulationTimeSeconds);
                if (kind == "skid" && s.OnGround) s.HeadingTrueDegrees = 30;
            }
            var model = new TelemetryContactPointReconstruction(samples, 0);
            Assert(!model.TryEstimate(0, -.01, out _, out _), "reject " + kind);
        }
        var lowSpeed = TouchdownAnalysis.Analyze(Recorded("3f8c37b6"));
        Assert(!lowSpeed[1].ClosureReconstructionAvailable, "closing sign remains ambiguous over the observed time interval");
    }

    public static void KinematicMotionChecksIndependentChannels()
    {
        var baseline = TouchdownAnalysis.Analyze(Recorded("e78d3f80"));
        Assert(baseline[2].ClosureReconstructionAvailable, "kinematic control");
        var time = baseline[2].EstimatedContactTimeSeconds;
        foreach (var kind in new[] { "missing_roll_rate", "pitch_rate_bias", "roll_rate_bias", "yaw_rate_bias", "altitude_drift" })
        {
            var samples = Recorded("e78d3f80");
            foreach (var s in samples.Where(s => s.SimulationTimeSeconds >= time - .3 && !s.OnGround && s.SimulationTimeSeconds < time))
            {
                if (kind == "missing_roll_rate") s.RotationVelocityBodyZRadiansPerSecond = double.NaN;
                if (kind == "pitch_rate_bias") s.RotationVelocityBodyXRadiansPerSecond += .5;
                if (kind == "roll_rate_bias") s.RotationVelocityBodyZRadiansPerSecond += .5;
                if (kind == "yaw_rate_bias") s.RotationVelocityBodyYRadiansPerSecond += .5;
                if (kind == "altitude_drift") s.PlaneAltitudeFeet += 5 * (s.SimulationTimeSeconds - time);
            }
            var changed = TouchdownAnalysis.Analyze(samples);
            Assert(!changed[2].ClosureReconstructionAvailable, "native observations must agree: " + kind);
        }
        var missingExtraChannel = Recorded("e78d3f80");
        foreach (var s in missingExtraChannel) s.RotationVelocityBodyZRadiansPerSecond = double.NaN;
        var oldModel = TouchdownAnalysis.Analyze(missingExtraChannel);
        Near(baseline[0].ReconstructedClosureFpm, oldModel[0].ReconstructedClosureFpm, 1e-8, "existing bounds do not require extra channel");
        Assert(!oldModel[2].ClosureReconstructionAvailable, "native fallback requires its roll channel");
    }

    public static void CubicAltitudeMatchesQuadraticVerticalSpeed()
    {
        const double jerk = 500;
        foreach (var slope in new[] { -.02, .02 })
        foreach (var bankScale in new[] { .025, .4 })
        {
            var model = new TelemetryContactPointReconstruction(Analytic(slope, 11, bankScale, jerk), 0);
            Assert(model.TryEstimate(0, -.01, out var e, out var decision), "cubic altitude physics: " + decision);
            Assert(e.KinematicMotion, "strong vertical jerk needs consistent altitude integration");
            Near((4 + jerk * .01 * .01 / 2) * 60, e.Closure, 1, "known physical clearance derivative under jerk");
            Assert(e.AltitudeDisagreement < 5, "cubic height must not create spurious quadratic-fit disagreement");
        }
    }

    public static void BoundedPredictionCoversIndependentVertices()
    {
        var h = new[] { new[] { 1.0, 1, 1, 1 }, new[] { 1.0, -1, 1, -1 },
            new[] { 1.0, 1, -1, -1 }, new[] { 1.0, -1, -1, 1 } };
        var sides = new[] { 1.0, .7, 2, .4 };
        var constraints = new List<double[]>(); var limits = new List<double>();
        for (var i = 0; i < 4; i++)
        foreach (var sign in new[] { -1, 1 })
        foreach (var scale in new[] { 1e-6, 1.0, 1e6 })
        { constraints.Add(h[i].Select(v => sign * scale * v / 2).ToArray()); limits.Add(scale * sides[i]); }
        var model = new BoundedLinearPrediction(constraints, limits, Enumerable.Repeat(sides.Sum() / 2, 4).ToArray());
        foreach (var query in new[] { new[] { 1.0, -2, 3, .1 }, new[] { 0.0, 1, 0, 0 }, new double[4] })
        {
            var vertices = new List<double>();
            for (var mask = 0; mask < 16; mask++)
            {
                var z = Enumerable.Range(0, 4).Select(i => ((mask & (1 << i)) == 0 ? -1 : 1) * sides[i]).ToArray();
                var x = h.Select(row => row.Select((v, j) => v * z[j] / 2).Sum()).ToArray();
                vertices.Add(query.Select((v, j) => v * x[j]).Sum());
            }
            Assert(model.TryInterval(query, out var low, out var high), "bounded prediction certificate");
            Assert(low <= vertices.Min() + 1e-10 && high >= vertices.Max() - 1e-10, "all independently enumerated vertices covered");
            Near(vertices.Min(), low, .0001, "lower bound tightness"); Near(vertices.Max(), high, .0001, "upper bound tightness");
        }
        Assert(!model.TryInterval(new[] { double.NaN, 0, 0, 0 }, out _, out _), "invalid query fails closed");
    }

    public static void LatchIndicesClockAndDeliveryAreIndependent()
    {
        foreach (var capture in new[] { "13dc14ff", "e78d3f80", "4178026c", "838a087e" })
        {
            var samples = Recorded(capture);
            var before = TouchdownAnalysis.Analyze(samples);
            foreach (var s in samples)
            {
                s.SimulationTimeSeconds += 100000;
                s.HostElapsedSeconds = 42;
                s.TouchdownNormalVelocityFps = s.OnGround ? 9999 : -123;
                s.HeadingTrueDegrees += 360;
                Array.Reverse(s.ContactPointOnGround);
                Array.Reverse(s.ContactPointCompression);
                Array.Reverse(s.ContactPointPosition);
            }
            var after = TouchdownAnalysis.Analyze(samples.SelectMany(s => new[] { s, s }).ToList());
            Assert(before.Count == after.Count, "same contact count after duplicate delivery");
            for (var i = 0; i < before.Count; i++)
            {
                Assert(before[i].ClosureReconstructionAvailable == after[i].ClosureReconstructionAvailable, "independent availability");
                if (!before[i].ClosureReconstructionAvailable) continue;
                Near(before[i].ReconstructedClosureFpm, after[i].ReconstructedClosureFpm, .0001, "independent modeled velocity");
                Near(before[i].ClosureReconstructionUncertaintyFpm, after[i].ClosureReconstructionUncertaintyFpm, .0001, "duplicates do not shrink uncertainty");
                Assert(after[i].ClosureReconstructionContactPointIndices!.SequenceEqual(
                    before[i].ClosureReconstructionContactPointIndices!.Select(p => 19 - p).OrderBy(p => p)), "permuted observed points");
            }
        }
    }

    public static void ObservedPointTakesPriorityOnSharedContacts()
    {
        // The Fenix controls agree with independently read cfg geometry at
        // contact time. The 737 and Falcon expose acceleration over the legacy
        // 75 ms offset; local linear fits of native VY confirm the timing effect.
        var cases = new[] { ("4178026c", 0, 326.93), ("d2d27342", 0, 469.77),
            ("838a087e", 0, 427.52), ("2d13e52c", 1, 80.32) };
        foreach (var item in cases)
        {
            var samples = Recorded(item.Item1);
            Assert(TelemetryGeometryCalibration.TryCalibrate(samples, out var arm), "legacy comparison geometry");
            var journal = new TelemetryDecisionJournal();
            var preferred = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal })[item.Item2];
            var supplied = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions {
                LongitudinalMainGearArmFeet = arm.LongitudinalArmFeet,
                LongitudinalMainGearArmSource = TouchdownGeometrySource.Provided })[item.Item2];
            Assert(preferred.ClosureReconstructionAvailable && supplied.ClosureReconstructionAvailable, "both models supported");
            Assert(preferred.ReconstructedRotationFpm.HasValue && supplied.ClosureReconstructionModel == TouchdownClosureReconstruction.ModelName,
                "observed point is preferred automatically; supplied geometry still wins");
            Near(item.Item3, preferred.ReconstructedClosureFpm, 10, "independent full-capture contact-time control");
            Assert(Math.Abs(preferred.ReconstructedClosureFpm - supplied.ReconstructedClosureFpm) > 20, "do not silently retain the earlier estimate");
            Near(supplied.InertialVerticalFpm, preferred.InertialVerticalFpm, 1e-8, "primary rate unchanged");
            Near(supplied.LatchedNormalFpm, preferred.LatchedNormalFpm, 1e-8, "native latch unchanged");
            Near(supplied.PeakG, preferred.PeakG, 1e-8, "native G unchanged");
            Assert(journal.Snapshot().Entries.Any(e => e.Code == "contact_point_fit_accepted" ||
                e.Code == "contact_point_bounds_accepted" || e.Code == "contact_point_kinematic_accepted"), "selected point model is diagnosed");
        }
    }

    public static void LongitudinalModelRemainsAvailableWithoutPointEvidence()
    {
        // This original projection deliberately lacks position/speed channels.
        // Its supported longitudinal estimate remains useful on its own terms.
        var samples = RepeatedContactGeometryRegressionTests.Recorded("c365f369");
        var journal = new TelemetryDecisionJournal();
        var r = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal }).Single();
        Assert(r.ClosureReconstructionAvailable && r.ClosureReconstructionModel == TouchdownClosureReconstruction.ModelName,
            "point-model refusal preserves a supported longitudinal fallback");
        Assert(!r.ReconstructedRotationFpm.HasValue, "fallback retains pitch-only provenance");
        Assert(journal.Snapshot().Entries.Any(e => e.Code == "longitudinal_fallback_selected"), "fallback selection is explicit");
        Assert(!TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { RecoverLongitudinalMainGearArmFromTelemetry = false })
            .Single().ClosureReconstructionAvailable, "recovery opt-out still applies to both models");
    }

    public static void SuppliedGeometryAndRecoveryOptOutKeepPriority()
    {
        foreach (var capture in new[] { "13dc14ff", "e78d3f80" })
        foreach (var options in new[] {
            new TouchdownAnalysisOptions { RecoverLongitudinalMainGearArmFromTelemetry = false },
            new TouchdownAnalysisOptions { LongitudinalMainGearArmFeet = -8, MainGearContactPoints = new[] {
                new TouchdownMainGearContactPoint(1,-8), new TouchdownMainGearContactPoint(2,-8) }, NoseGearContactPointIndices = new[] { 0 } } })
        {
            var results = TouchdownAnalysis.Analyze(Recorded(capture), options);
            Assert(results.All(r => !r.ClosureReconstructionAvailable), "no replacement of supplied geometry or opt-out");
        }
    }

    internal static List<TelemetrySample> Recorded(string id) => RepeatedContactGeometryRegressionTests.Recorded("point-" + id);

    // Physical construction independent of the regression: a specified point
    // clears a planar sloped surface at 4 ft/s, then compresses its suspension.
    // The reference point is 4 ft behind the altitude datum. Ground coordinates,
    // WORLD Y and rotation are generated before the model sees any samples.
    private static List<TelemetrySample> Analytic(double slope, int point, double bankScale = .025, double jerk = 0)
    {
        var samples = new List<TelemetrySample>();
        double Theta(double t) => .12 + .07 * Math.Sin(.8 * t) + .03 * Math.Cos(2.1 * t);
        double Phi(double t) => bankScale * (.7 + .25 * Math.Sin(1.7 * t) + .05 * Math.Cos(.4 * t));
        double Distance(double t) => 100 * t + t * t;
        double Compression(double t) => t < 0 ? 0 : 25 * (1 - Math.Exp(-8 * t)) + 8 * Math.Sin(1.1 * t);
        double Height(double t)
        {
            var th = Theta(t); var ph = Phi(t);
            // Rotate an actual body-space wheel vector, first about the forward
            // axis, then about the right axis. This independently exercises the
            // SDK yaw/pitch/roll matrix, including the high-bank cross terms.
            var right = -1.5; var up = -6 + .008 * Compression(t); var forward = 10.0;
            var rolledUp = right * Math.Sin(ph) + up * Math.Cos(ph);
            var worldUp = rolledUp * Math.Cos(th) - forward * Math.Sin(th);
            var worldNorth = rolledUp * Math.Sin(th) + forward * Math.Cos(th);
            return slope * worldNorth - worldUp + (t < 0 ? -4 * t - jerk * t * t * t / 6 : 0);
        }
        double Alt(double t) => 100 + slope * Distance(t) + Height(t);
        const double step = .00001;
        for (var i = 0; i <= 1400; i++)
        {
            var t = -20 + i * .025; var th = Theta(t); var ph = Phi(t);
            var dTheta = (Theta(t + step) - Theta(t - step)) / (2 * step);
            var dPhi = (Phi(t + step) - Phi(t - step)) / (2 * step);
            var q = dTheta * Math.Cos(ph); var r = -dTheta * Math.Sin(ph);
            var vy = (Alt(t + step) - Alt(t - step)) / (2 * step) + 4 * Math.Cos(th) * dTheta;
            var s = new TelemetrySample { Sequence = i, SimulationTimeSeconds = t, MotionSimulation = true, OnGround = t >= 0,
                PlaneAltitudeFeet = Alt(t), GroundAltitudeFeet = 100 + slope * Distance(t),
                LatitudeDegrees = 45 + Distance(t) / 364812.21, LongitudeDegrees = 25, HeadingTrueDegrees = 0,
                GroundSpeedKnots = (100 + 2 * t) / 1.6878098571, PitchDegrees = th * 180 / Math.PI, BankDegrees = ph * 180 / Math.PI,
                VelocityWorldYFps = vy, RotationVelocityBodyXRadiansPerSecond = q,
                RotationVelocityBodyYRadiansPerSecond = r, RotationVelocityBodyZRadiansPerSecond = dPhi,
                GForce = 1.1, TouchdownNormalVelocityFps = t >= 0 ? 100 : 0 };
            s.ContactPointOnGround[point] = t >= 0; s.ContactPointCompression[point] = Compression(t); s.ContactPointPosition[point] = 100;
            samples.Add(s);
        }
        return samples;
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance, string message) =>
        Assert(!double.IsNaN(actual) && Math.Abs(expected - actual) <= tolerance, message + ": " + actual);
}
