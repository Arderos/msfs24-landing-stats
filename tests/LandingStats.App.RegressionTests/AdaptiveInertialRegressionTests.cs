using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class AdaptiveInertialRegressionTests
{
    private static readonly MethodInfo Estimate = typeof(TouchdownAnalysis).GetMethod("EstimateInertialVerticalFpm",
        BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly double[] RegularTimes = Enumerable.Range(0, 11).Select(i => -.2 + i * .02).ToArray();
    private static readonly double[] IrregularTimes = { -.2, -.183, -.156, -.13, -.099, -.084, -.052, -.029, -.017, 0 };

    public static void RunAll()
    {
        LinearMotionKeepsItsTrend();
        CurvedMotionUsesQuadraticPrediction();
        RecentPlateauUsesTheLastObservation();
        EqualPastErrorsPreferTheLastObservation();
        MissingComparablePastTargetsRetainsTheAdmittedLinearFit();
        OlderHistoryCannotParticipateInModelSelection();
        FutureFramesCannotChangeModelSelection();
        ExistingAdmissionChecksStillControlEveryCandidate();
        ClockOriginVelocityScaleAndClimbDoNotChangeTheModel();
        PublicAnalysisUsesTheSelectionAndRecordsItsReason();
    }

    private static double CurvedVelocity(double time) => -300 + 200 * time + 3000 * time * time;

    private static void LinearMotionKeepsItsTrend()
    {
        foreach (var times in new[] { RegularTimes, IrregularTimes })
        {
            var result = Run(Samples(times, time => -300 + 200 * time), .02);
            Equal(result.Fpm, 296, "Analytic linear prediction");
            Selected(result, "precontact_linear_fit_extrapolated", true);
            Equal(result.Support, .2, "Retained 200 ms support");
        }
    }

    private static void CurvedMotionUsesQuadraticPrediction()
    {
        foreach (var times in new[] { RegularTimes, IrregularTimes })
        {
            var result = Run(Samples(times, CurvedVelocity), .02);
            Equal(result.Fpm, -CurvedVelocity(.02), "Analytic quadratic prediction");
            Selected(result, "precontact_quadratic_fit_extrapolated", true);
        }
    }

    private static void RecentPlateauUsesTheLastObservation()
    {
        var samples = Samples(RegularTimes, time => time < -.06 ? -300 + 200 * time : -310);
        var result = Run(samples, .02);
        Equal(result.Fpm, 310, "Plateau must not continue the earlier trend");
        Selected(result, "precontact_adaptive_last_observation", false);
        Equal(result.Support, .2, "Selecting the last observation preserves admission evidence");
    }

    private static void EqualPastErrorsPreferTheLastObservation()
    {
        var result = Run(Samples(RegularTimes, time => -300), .02);
        Equal(result.Fpm, 300, "Constant motion");
        Selected(result, "precontact_adaptive_last_observation", false);
    }

    private static void MissingComparablePastTargetsRetainsTheAdmittedLinearFit()
    {
        // Four current observations support a fit, but removing any future target
        // leaves no common four-point, 150 ms history for comparing all three models.
        var samples = Samples(new[] { -.2, -.13, -.05, 0 }, CurvedVelocity);
        var result = Run(samples, .02);
        Equal(result.Fpm, 320.731330472103, "Sparse validation retains the admitted least-squares line");
        Selected(result, "precontact_linear_fit_extrapolated", true);
        Check(Math.Abs(result.Fpm + CurvedVelocity(.02)) > 20,
            "The fixture no longer distinguishes the linear fallback from an unvalidated parabola.");
    }

    private static void OlderHistoryCannotParticipateInModelSelection()
    {
        var current = Samples(new[] { -.2, -.13, -.05, 0 }, CurvedVelocity);
        var oldTimes = Enumerable.Range(0, 20).Select(i => -.6 + i * .02).ToArray();
        var baseline = Run(current, .02);
        // Extending validation beyond the current 200 ms would make the smooth
        // historical parabola win; strict selection must retain the same fallback.
        foreach (var history in new[]
        {
            Samples(oldTimes, CurvedVelocity),
            Samples(oldTimes, time => 10000 + 5000 * Math.Sin(time * 100)),
        })
        {
            var extended = Run(history.Concat(current).ToArray(), .02);
            Same(extended, baseline, "History outside the 200 ms window changed the selected model");
        }
    }

    private static void FutureFramesCannotChangeModelSelection()
    {
        var samples = Samples(RegularTimes, CurvedVelocity);
        var expected = Run(samples, .02);
        var suffix = Samples(new[] { .01, .02, .04, .08 }, time => time * 100000);
        foreach (var frame in suffix) frame.OnGround = true;
        suffix[1].VelocityWorldYFps = double.NaN;
        var extended = samples.Concat(suffix).ToArray();
        var result = Run(extended, .02, lastAirborneIndex: samples.Length - 1);
        Same(result, expected, "Post-origin observations leaked into model selection");
    }

    private static void ExistingAdmissionChecksStillControlEveryCandidate()
    {
        var curved = Samples(RegularTimes, CurvedVelocity);
        Rejected(Run(curved, .02, false), 300, "no_compression_time_estimate");
        Rejected(Run(curved, .25), 300, "precontact_fit_horizon_unsupported");
        Rejected(Run(Samples(new[] { -.14, -.1, -.05, 0 }, CurvedVelocity), .02),
            300, "insufficient_precontact_fit_support");

        // This parabola predicts prior targets exactly, but its symmetric curve
        // fails the existing linear admission check. Adaptive selection may not
        // turn that previously rejected input into an accepted extrapolation.
        var rejectedCurve = Samples(RegularTimes, time => -300 + 3000 * (time + .1) * (time + .1));
        Rejected(Run(rejectedCurve, .02), 270, "precontact_fit_prediction_unreliable");

        var unavailable = Samples(RegularTimes, CurvedVelocity);
        unavailable[unavailable.Length - 1].SimulatorState = TelemetrySimulatorState.Paused;
        Rejected(Run(unavailable, .02), 300, "precontact_fit_sample_unavailable");
    }

    private static void ClockOriginVelocityScaleAndClimbDoNotChangeTheModel()
    {
        foreach (var origin in new[] { 0.0, 1000000.0 })
        foreach (var scale in new[] { .3048, 1.0, 5.0 })
        foreach (var offset in new[] { 0.0, 400.0 })
        {
            var samples = Samples(IrregularTimes, time => scale * CurvedVelocity(time) + offset);
            foreach (var sample in samples)
            {
                sample.SimulationTimeSeconds += origin;
                sample.HostElapsedSeconds += origin;
            }
            var result = Run(samples, origin + .02);
            Equal(result.Fpm, -(scale * CurvedVelocity(.02) + offset), "Clock/scale/offset invariant quadratic prediction", 1e-5);
            Selected(result, "precontact_quadratic_fit_extrapolated", true);
        }
    }

    private static void PublicAnalysisUsesTheSelectionAndRecordsItsReason()
    {
        foreach (var usePlateau in new[] { false, true })
        {
            var samples = Samples(RegularTimes,
                time => usePlateau ? (time < -.06 ? -300 + 200 * time : -310) : CurvedVelocity(time)).ToList();
            for (var i = 1; i <= 2; i++)
            {
                var ground = Sample(.02 + .02 * i, 0);
                ground.OnGround = true;
                ground.ContactPointOnGround[0] = true;
                ground.ContactPointCompression[0] = .1 * i;
                samples.Add(ground);
            }
            var journal = new TelemetryDecisionJournal();
            var contacts = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal });
            Check(contacts.Count == 1, "Adaptive selection changed the physical contact count.");
            var contact = contacts[0];
            Check(contact.ContactTimeEstimatedFromCompression, "Public fixture lost compression-derived contact timing.");
            Equal(contact.InertialVerticalFpm, usePlateau ? 310 : 294.8, "Public adaptive inertial velocity");
            Check(contact.InertialVerticalExtrapolated == !usePlateau, "Public extrapolation flag disagrees with the selected estimator.");
            var reason = usePlateau ? "precontact_adaptive_last_observation" : "precontact_quadratic_fit_extrapolated";
            Check(journal.Snapshot().Entries.Any(entry => entry.Code == reason), "Decision journal omitted the selected estimator: " + reason);
        }
    }

    private static TelemetrySample[] Samples(double[] times, Func<double, double> signedFpm) =>
        times.Select(time => Sample(time, signedFpm(time))).ToArray();

    private static TelemetrySample Sample(double time, double signedFpm) => new TelemetrySample
    {
        SimulationTimeSeconds = time, HostElapsedSeconds = time, MotionSimulation = true,
        VelocityWorldYFps = signedFpm / 60, GForce = 1, PlaneAltitudeFeet = 100, GroundAltitudeFeet = 95,
        AboveGroundLevelFeet = 5, GroundSpeedKnots = 100,
    };

    private static Result Run(IReadOnlyList<TelemetrySample> samples, double contactTime,
        bool compression = true, int? lastAirborneIndex = null)
    {
        var arguments = new object[] { samples, lastAirborneIndex ?? samples.Count - 1, contactTime, compression, false, 0.0, "" };
        var fpm = (double)Estimate.Invoke(null, arguments)!;
        return new Result(fpm, (bool)arguments[4], (double)arguments[5], (string)arguments[6]);
    }

    private static void Rejected(Result result, double expectedFpm, string reason)
    {
        Selected(result, reason, false);
        Equal(result.Fpm, expectedFpm, "Unchanged rejection fallback");
    }

    private static void Selected(Result result, string reason, bool extrapolated) =>
        Check(result.Decision == reason && result.Extrapolated == extrapolated,
            "Unexpected selected estimator: " + result.Decision + ", extrapolated=" + result.Extrapolated + "; expected " + reason);

    private static void Same(Result actual, Result expected, string name)
    {
        Equal(actual.Fpm, expected.Fpm, name);
        Equal(actual.Support, expected.Support, name + " (support)");
        Check(actual.Decision == expected.Decision && actual.Extrapolated == expected.Extrapolated, name + " (decision)");
    }

    private sealed class Result
    {
        public Result(double fpm, bool extrapolated, double support, string decision)
        { Fpm = fpm; Extrapolated = extrapolated; Support = support; Decision = decision; }
        public double Fpm { get; }
        public bool Extrapolated { get; }
        public double Support { get; }
        public string Decision { get; }
    }

    private static void Equal(double actual, double expected, string name, double tolerance = 1e-8) =>
        Check(!double.IsNaN(actual) && Math.Abs(actual - expected) <= tolerance, name + ": " + actual + " != " + expected);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
