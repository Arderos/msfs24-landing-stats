using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class InertialFitSupportRegressionTests
{
    private static readonly MethodInfo Estimate = typeof(TouchdownAnalysis).GetMethod("EstimateInertialVerticalFpm",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    public static void RunAll()
    {
        UnusedHistoryCannotAuthorizeAWeakFit();
        NoisyTrendUsesLastObservation();
        SupportedTrendCanContainMeasurementNoise();
        OneInfluentialPointCannotEstablishAVelocityTrend();
        PredictionHorizonNeedsObservedSupport();
        SmoothSignalsRetainTheirMeasuredTrendAtDifferentCadences();
        RealClimbAndSignCrossingRemainRepresentable();
        PhysicsAndClockBoundariesCannotSupplyFitSupport();
        NoCompressionEstimateKeepsTheLastObservation();
        PublicAnalysisRetainsTheContactWhenTheFitIsUnavailable();
        ShortBounceRemainsAContactWithLastObservedVelocity();
        FrozenFramesCannotAlterEarlierContactMeasurements();
    }

    private static void UnusedHistoryCannotAuthorizeAWeakFit()
    {
        var shortWindow = Samples(new[] { -.06, -.03, 0 }, new[] { -10.0, -1, -1 });
        var withPrefix = Samples(new[] { -.4, -.3 }, new[] { -1.0, -1 }).Concat(shortWindow).ToArray();
        var shortResult = Run(shortWindow, .13);
        var prefixedResult = Run(withPrefix, .13);
        Check(!shortResult.Extrapolated && !prefixedResult.Extrapolated, "Unused samples authorized a 60 ms velocity fit.");
        Equal(shortResult.Fpm, 60, "Short-window fallback");
        Equal(prefixedResult.Fpm, shortResult.Fpm, "Unused-prefix invariance");
        Check(shortResult.Decision == prefixedResult.Decision, "Unused history changed the fit's quality decision.");
    }

    private static void NoisyTrendUsesLastObservation()
    {
        var result = Run(Samples(new[] { -.2, -.15, -.1, -.05, 0 }, new[] { -5.0, -1, -5, -1, -5 }), .08);
        Check(!result.Extrapolated && result.Decision == "precontact_fit_prediction_unreliable",
            "A supported but noisy velocity sequence was accepted without a model-quality check.");
        Equal(result.Fpm, 300, "Noisy-model fallback");
    }

    private static void PredictionHorizonNeedsObservedSupport()
    {
        var samples = Samples(new[] { -.2, -.15, -.1, -.05, 0 }, time => -2 + time);
        var result = Run(samples, .25);
        Check(!result.Extrapolated && result.Decision == "precontact_fit_horizon_unsupported",
            "Prediction extended farther than the observed trend interval.");
        Equal(result.Fpm, 120, "Unsupported-horizon fallback");
    }

    private static void SupportedTrendCanContainMeasurementNoise()
    {
        var result = Run(Samples(new[] { -.2, -.15, -.1, -.05, 0 }, new[] { -2.6, -2.44, -2.31, -2.14, -2 }), .025);
        Check(result.Extrapolated, "Ordinary measurement noise suppressed a supported trend.");
        Equal(result.Fpm, 115.38, "Noisy linear prediction");
    }

    private static void OneInfluentialPointCannotEstablishAVelocityTrend()
    {
        foreach (var velocities in new[]
        {
            new[] { -10.0, -1, -1, -1, -1 },
            new[] { -1.0, -1, -1, -1, -10 },
            new[] { -1.0, -1, -10, -1, -1 },
        })
        {
            var result = Run(Samples(new[] { -.2, -.15, -.1, -.05, 0 }, velocities), .01);
            Check(!result.Extrapolated && result.Decision == "precontact_fit_prediction_unreliable",
                "A single influential velocity sample established an unsupported trend.");
            Equal(result.Fpm, -60 * velocities[velocities.Length - 1], "Influential-point fallback");
        }
    }

    private static void SmoothSignalsRetainTheirMeasuredTrendAtDifferentCadences()
    {
        foreach (var times in new[]
        {
            Enumerable.Range(0, 21).Select(i => -.2 + i * .01).ToArray(),
            new[] { -.2, -.16, -.12, -.08, -.04, 0 },
            new[] { -.2, -.15, -.1, -.05, 0 },
            new[] { -.2, -.1, 0 },
            new[] { -.2, -.13, -.05, 0 },
            new[] { -.15, -.1, -.05, 0 },
        })
        {
            foreach (var origin in new[] { 0.0, 1000000.0 })
            {
                var linear = Samples(times, time => -2 + 3 * time);
                foreach (var sample in linear) { sample.SimulationTimeSeconds += origin; sample.HostElapsedSeconds += origin; }
                var trend = Run(linear, origin + .025);
                Check(trend.Extrapolated, "A smooth lower-rate trend lost valid support.");
                Equal(trend.Fpm, 115.5, "Linear prediction", 1e-6);
                var constant = Run(Samples(times, time => -2), .025);
                Check(constant.Extrapolated, "Constant descent was rejected as a noisy trend.");
                Equal(constant.Fpm, 120, "Constant descent");
            }
        }
    }

    private static void RealClimbAndSignCrossingRemainRepresentable()
    {
        var times = new[] { -.2, -.15, -.1, -.05, 0 };
        var climb = Run(Samples(times, time => 2 + time), .08);
        Check(climb.Extrapolated, "A true climb was excluded by sign.");
        Equal(climb.Fpm, -124.8, "Climbing prediction");
        var crossing = Run(Samples(times, time => -1 + 20 * time), .08);
        Check(crossing.Extrapolated, "A well-supported trend was prohibited from crossing zero.");
        Equal(crossing.Fpm, -36, "Sign-crossing prediction");
    }

    private static void PhysicsAndClockBoundariesCannotSupplyFitSupport()
    {
        foreach (var boundary in new Action<TelemetrySample>[]
        {
            sample => sample.MotionSimulation = false,
            sample => sample.OnGround = true,
            sample => sample.FlightSegment = 1,
            sample => sample.SimulatorState = TelemetrySimulatorState.Paused,
            sample => sample.SimulatorState = TelemetrySimulatorState.Inactive,
            sample => sample.SourceEvidence = Observation(7, 0, SdkReplayState.Unknown),
            sample => sample.SourceEvidence = Observation(2, 0, SdkReplayState.Unknown),
            sample => sample.SourceEvidence = Observation(0, 1, SdkReplayState.Unknown),
            sample => sample.SourceEvidence = Observation(0, 0, SdkReplayState.Active),
            sample => sample.VelocityWorldYFps = double.NaN,
            sample => sample.SimulationTimeSeconds = .01,
            sample => sample.SimulationTimeSeconds = -.05,
            sample => { sample.SimulationTimeSeconds = double.NaN; sample.HostElapsedSeconds = double.NaN; },
        })
        {
            var samples = Samples(new[] { -.2, -.15, -.1, -.05, 0 }, time => -2 + time);
            boundary(samples[2]);
            var result = Run(samples, .025);
            Check(!result.Extrapolated, "A physical, unavailable-data or clock boundary contributed old fit support.");
            Equal(result.Fpm, 120, "Discontinuous-history fallback");
        }
    }

    private static void NoCompressionEstimateKeepsTheLastObservation()
    {
        var result = Run(Samples(new[] { -.2, -.15, -.1, -.05, 0 }, time => -2 + time), .05, false);
        Check(!result.Extrapolated && result.Decision == "no_compression_time_estimate", "Absent contact timing created a prediction.");
        Equal(result.Fpm, 120, "No-compression fallback");
    }

    private static void PublicAnalysisRetainsTheContactWhenTheFitIsUnavailable()
    {
        var samples = Samples(new[] { -.4, -.3, -.06, -.03, 0 }, new[] { -1.0, -1, -10, -1, -1 }).ToList();
        AddGroundContact(samples, .13, -1);
        var journal = new TelemetryDecisionJournal();
        var contacts = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal });
        Check(contacts.Count == 1, "Fit rejection deleted the physical contact.");
        Check(contacts[0].ContactTimeEstimatedFromCompression && !contacts[0].InertialVerticalExtrapolated,
            "The public analysis path bypassed fit support validation.");
        Equal(contacts[0].InertialVerticalFpm, 60, "Public LastAirborne result");
        Check(journal.Snapshot().Entries.Any(entry => entry.Code == "insufficient_precontact_fit_support"),
            "The fallback reason was not retained in the decision journal.");
    }

    private static void ShortBounceRemainsAContactWithLastObservedVelocity()
    {
        var samples = Samples(new[] { -.2, -.15, -.1, -.05, 0 }, time => -2).ToList();
        AddGroundContact(samples, .025, -2);
        samples.AddRange(Samples(new[] { .1, .12, .14 }, time => -1));
        AddGroundContact(samples, .15, -1);
        var contacts = TouchdownAnalysis.Analyze(samples);
        Check(contacts.Count == 2 && contacts[1].ContactNumber == 2, "Short bounce was rejected or regrouped.");
        Check(!contacts[1].InertialVerticalExtrapolated, "Short bounce borrowed pre-bounce fit support.");
        Equal(contacts[1].InertialVerticalFpm, 60, "Bounce last observation");
    }

    private static void FrozenFramesCannotAlterEarlierContactMeasurements()
    {
        var samples = Samples(new[] { -.2, -.15, -.1, -.05, 0 }, time => -2).ToList();
        AddGroundContact(samples, .025, -2);
        samples[5].GForce = 1.2;
        var frozen = samples[6];
        frozen.SourceEvidence = Observation(7, 0, SdkReplayState.Unknown);
        frozen.GForce = 999;
        frozen.TouchdownNormalVelocityFps = 999;
        var released = Sample(.085, -2);
        released.OnGround = true;
        released.SourceEvidence = Observation(0, 0, SdkReplayState.Unknown);
        released.GForce = 888;
        released.TouchdownNormalVelocityFps = 888;
        samples.Add(released);
        using var hash = SHA256.Create();
        var original = hash.ComputeHash(samples.SelectMany(TelemetryBinaryCodec.Encode).ToArray());
        var source = ReplaySourceTimelineCodec.Encode(samples)!;
        var journal = new TelemetryDecisionJournal();
        var contacts = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal });
        Check(contacts.Count == 1, "A later freeze removed the already accepted physical contact.");
        var contact = contacts[0];
        Equal(contact.PeakG, 1.2, "Peak G across source-control boundary");
        Equal(contact.PeakG50Milliseconds, 1.2, "Short G window across source-control boundary");
        Equal(contact.Impact.PeakG, 1.2, "Impact peak across source-control boundary");
        Check(!contact.Impact.IsComplete, "An interrupted physical impact was certified complete.");
        Check(!contact.LatchUpdateDetected && double.IsNaN(contact.LatchedNormalFpm), "Latch update crossed frozen physics.");
        Check(!contact.ContactTimeEstimatedFromCompression, "Frozen compression supplied contact timing.");
        Check(frozen.MotionSimulation && original.SequenceEqual(hash.ComputeHash(samples.SelectMany(TelemetryBinaryCodec.Encode).ToArray())) &&
              source.SequenceEqual(ReplaySourceTimelineCodec.Encode(samples)!), "Measurement barriers changed recorded input bytes.");
        Check(journal.Snapshot().Entries.Count(entry => entry.Code == "source_physics_measurement_boundary") == 1,
            "The source-control measurement boundary was not recorded once.");
    }

    private static void AddGroundContact(List<TelemetrySample> samples, double estimatedTime, double velocity)
    {
        for (var i = 1; i <= 2; i++)
        {
            var sample = Sample(estimatedTime + .02 * i, velocity);
            sample.OnGround = true;
            sample.ContactPointOnGround[0] = true;
            sample.ContactPointCompression[0] = .1 * i;
            samples.Add(sample);
        }
    }

    private static TelemetrySample[] Samples(double[] times, Func<double, double> velocity) =>
        times.Select(time => Sample(time, velocity(time))).ToArray();

    private static TelemetrySample[] Samples(double[] times, double[] velocities) =>
        times.Select((time, index) => Sample(time, velocities[index])).ToArray();

    private static TelemetrySample Sample(double time, double velocity) => new TelemetrySample
    {
        SimulationTimeSeconds = time, HostElapsedSeconds = time, MotionSimulation = true,
        VelocityWorldYFps = velocity, GForce = 1, PlaneAltitudeFeet = 100, GroundAltitudeFeet = 95,
        AboveGroundLevelFeet = 5, GroundSpeedKnots = 100,
    };

    private static ReplaySourceEvidence Observation(int freeze, int slew, SdkReplayState replay) =>
        new ReplaySourceEvidence(new Guid("11111111-1111-1111-1111-111111111111"), 1, 1, 1000, 1000, 1000, replay,
            new ReplaySourceObservation(SourceObservationAvailability.Available, SourceObservationReason.None,
                freeze, slew, 1, simulationTime: -.1, latitude: 0, longitude: 0, altitudeFeet: 100, pitch: 0, bank: 0, heading: 0));

    private static Result Run(IReadOnlyList<TelemetrySample> samples, double contactTime, bool compression = true)
    {
        var arguments = new object[] { samples, samples.Count - 1, contactTime, compression, false, 0.0, "" };
        var fpm = (double)Estimate.Invoke(null, arguments)!;
        return new Result(fpm, (bool)arguments[4], (string)arguments[6]);
    }

    private sealed class Result
    {
        public Result(double fpm, bool extrapolated, string decision) { Fpm = fpm; Extrapolated = extrapolated; Decision = decision; }
        public double Fpm { get; }
        public bool Extrapolated { get; }
        public string Decision { get; }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal(double actual, double expected, string name, double tolerance = 1e-8) =>
        Check(!double.IsNaN(actual) && Math.Abs(actual - expected) <= tolerance, name + ": " + actual + " != " + expected);
}
