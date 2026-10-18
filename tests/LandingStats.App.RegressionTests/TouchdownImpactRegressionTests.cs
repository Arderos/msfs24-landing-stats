using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class TouchdownImpactRegressionTests
{
    public static void RunAll()
    {
        ThresholdsUseUnroundedCompletePeak();
        ZeroOrInvalidSignalsNeverBecomeSmooth();
        ContinuingMotionKeepsTheSecondPulse();
        ASettledContactExcludesLaterRolloutLoads();
        LiftoffKeepsContactsIndependent();
        DuplicateDeliveryKeepsTheFinalFrameAndState();
        OptionalChannelsCanBeAbsentButNotDropOut();
        GapsReversalsAndSlewCannotCompleteAnImpact();
        UnevenCadencePreservesNativePeak();
        SurfaceSlopeDoesNotBecomeVerticalMotion();
        RawAndSavedInputsUseTheSameEngine();
        IncompleteAndNonContactsAreNotClassified();
    }

    public static void ThresholdsUseUnroundedCompletePeak()
    {
        Equal(TouchdownImpactCategory.Smooth, Complete(1.1999999).Category, "below smooth boundary");
        Equal(TouchdownImpactCategory.Normal, Complete(1.2).Category, "exactly 1.2");
        Equal(TouchdownImpactCategory.Normal, Complete(1.4999999).Category, "below firm boundary");
        Equal(TouchdownImpactCategory.Firm, Complete(1.5).Category, "exactly 1.5");
        Equal(TouchdownImpactCategory.Smooth, Complete(.8).Category, "no invented 1G lower clip");
        Equal(TouchdownImpactCategory.Firm, Complete(9).Category, "no arbitrary upper clip");
        foreach (var g in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            Equal(TouchdownImpactCategory.Unavailable, Complete(g).Category, "invalid complete peak");
        Equal(TouchdownImpactCategory.Unavailable,
            new TouchdownImpactResult(TouchdownImpactStatus.Incomplete, 1.7, .2).Category, "known high peak with incomplete window");
        Equal(TouchdownImpactCategory.Unavailable,
            new TouchdownImpactResult(TouchdownImpactStatus.Settled, 1.1, .5, .4).Category, "peak outside reported endpoint");
    }

    public static void ZeroOrInvalidSignalsNeverBecomeSmooth()
    {
        var zero = Trace();
        foreach (var s in zero.Where(s => s.OnGround)) s.GForce = 0;
        Equal(TouchdownImpactStatus.InvalidTelemetry, Analyze(zero).Status, "zero sensor stream");
        zero.RemoveRange(Contact(zero) + 2, zero.Count - Contact(zero) - 2);
        zero.Add(Sample(.03, 1, 0, false));
        Assert(!Analyze(zero).IsComplete, "zero stream became a complete short contact");
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity })
        {
            var rows = Trace();
            rows[Contact(rows) + 1].GForce = bad;
            Equal(TouchdownImpactStatus.InvalidTelemetry, Analyze(rows).Status, "invalid G before separator");
            rows = Trace();
            rows[Contact(rows) + 1].VelocityWorldYFps = bad;
            Equal(TouchdownImpactStatus.InvalidTelemetry, Analyze(rows).Status, "invalid velocity before separator");
        }
    }

    public static void ContinuingMotionKeepsTheSecondPulse()
    {
        var rows = Trace();
        var first = Contact(rows);
        for (var i = first; i < rows.Count; i++)
        {
            var t = rows[i].TimeSeconds;
            rows[i].GForce = 1 + .15 * Gaussian(t, .15, .025) + .5 * Gaussian(t, .9, .05);
        }
        var acceleration = rows.Skip(first).Select(s => (s.GForce - 1) * 32.174).ToArray();
        var vy = 0.0;
        for (var i = 1; i < acceleration.Length; i++) vy -= (acceleration[i - 1] + acceleration[i]) * .005;
        for (var i = 0; i < first; i++) rows[i].VelocityWorldYFps = vy;
        for (var i = first; i < rows.Count; i++)
        {
            if (i > first) vy += (acceleration[i - first - 1] + acceleration[i - first]) * .005;
            rows[i].VelocityWorldYFps = vy;
        }
        var result = Analyze(rows);
        Assert(result.IsComplete && result.EndOffsetSeconds > .9, "still-descending aircraft was declared settled");
        Near(1.5, result.PeakG, 1e-10, "second pulse must belong to the same response");
    }

    public static void ASettledContactExcludesLaterRolloutLoads()
    {
        var rows = Trace();
        var baseline = Analyze(rows);
        foreach (var s in rows.Where(s => s.TimeSeconds > 2))
            s.GForce += 4 * Gaussian(s.TimeSeconds, 3, .1);
        var result = Analyze(rows);
        Assert(result.IsComplete, "normal response did not settle");
        Near(baseline.EndOffsetSeconds, result.EndOffsetSeconds, 1e-12, "late load changed finalized separator");
        Near(baseline.PeakG, result.PeakG, 1e-12, "late load contaminated touchdown peak");
        var prefix = rows.Where(s => s.TimeSeconds <= result.EndOffsetSeconds).ToList();
        Near(result.PeakG, Analyze(prefix).PeakG, 1e-12, "result is not prefix invariant");

        foreach (var s in rows) s.GForce = s.TimeSeconds < 2 ? 1 : 2;
        var noPulse = Analyze(rows);
        Equal(TouchdownImpactStatus.NoDistinctImpulse, noPulse.Status, "quiet touchdown searched for a later pulse");
        Near(1, noPulse.PeakG, 1e-12, "late pulse was retroactively assigned to quiet contact");
    }

    public static void LiftoffKeepsContactsIndependent()
    {
        var rows = new List<TouchdownImpactSample>();
        for (var i = -50; i <= 130; i++)
        {
            var t = i / 100.0;
            var ground = t >= 0 && t < .2 || t >= .4 && t < .6 || t >= .8 && t < 1.0;
            var g = t < .4 ? 1.1 : t < .8 ? 1.3 : 1.6;
            rows.Add(Sample(t, ground ? g : 99, -3, ground));
        }
        var indices = new[] { 50, 90, 130 };
        var peaks = new[] { 1.1, 1.3, 1.6 };
        for (var i = 0; i < indices.Length; i++)
        {
            var r = TouchdownImpactAnalysis.Analyze(rows, indices[i]);
            Equal(TouchdownImpactStatus.Liftoff, r.Status, "bounce must close only its own contact");
            Near(peaks[i], r.PeakG, 1e-12, "airborne/later G overwrote contact peak");
            Near(.2, r.EndOffsetSeconds, 1e-12, "wrong bounce boundary");
        }
    }

    public static void DuplicateDeliveryKeepsTheFinalFrameAndState()
    {
        var rows = Trace();
        var contact = Contact(rows);
        var valid = rows[contact];
        rows.Insert(contact, Sample(valid.TimeSeconds, 99, valid.VelocityWorldYFps, true));
        var r = TouchdownImpactAnalysis.Analyze(rows, contact);
        Near(1.5, r.PeakG, 1e-12, "stale same-time G survived deduplication");
        rows.Insert(contact + 2, Sample(valid.TimeSeconds, 1, -2, false));
        Equal(TouchdownImpactStatus.Unavailable, TouchdownImpactAnalysis.Analyze(rows, contact).Status,
            "overwritten contact state was not discarded");
    }

    public static void OptionalChannelsCanBeAbsentButNotDropOut()
    {
        var full = Trace();
        var result = Analyze(full);
        var absent = Trace();
        foreach (var s in absent)
        {
            s.GroundAltitudeFeet = double.NaN;
            s.RotationVelocityBodyXRadiansPerSecond = double.NaN;
            s.RotationVelocityBodyZRadiansPerSecond = double.NaN;
        }
        Assert(Analyze(absent).IsComplete, "optional absent channels prevented a valid response");
        Near(result.PeakG, Analyze(absent).PeakG, 1e-12, "optional absence changed raw peak");
        var mixed = Trace();
        mixed[Contact(mixed) + 10].GroundAltitudeFeet = double.NaN;
        Equal(TouchdownImpactStatus.InvalidTelemetry, Analyze(mixed).Status, "partial terrain dropout changed frames silently");
        mixed = Trace();
        mixed[Contact(mixed) + 10].RotationVelocityBodyXRadiansPerSecond = double.NaN;
        Equal(TouchdownImpactStatus.InvalidTelemetry, Analyze(mixed).Status, "partial rate dropout certified quiet");
    }

    public static void GapsReversalsAndSlewCannotCompleteAnImpact()
    {
        var rows = Trace();
        foreach (var s in rows.Where(s => s.TimeSeconds >= .2)) s.TimeSeconds += .2;
        Equal(TouchdownImpactStatus.Incomplete, Analyze(rows).Status, "gap was interpolated through");
        rows = Trace();
        rows[Contact(rows) + 10].TimeSeconds -= .03;
        Equal(TouchdownImpactStatus.InvalidTelemetry, Analyze(rows).Status, "clock reversal was ignored");
        rows = Trace();
        rows[Contact(rows) + 10].MotionSimulation = false;
        Equal(TouchdownImpactStatus.InvalidTelemetry, Analyze(rows).Status, "slew was treated as landing motion");
        rows = Trace();
        rows.RemoveAll(s => s.TimeSeconds < 0 && s.TimeSeconds > -.3);
        Equal(TouchdownImpactStatus.Incomplete, Analyze(rows).Status, "pre-contact gap was ignored");
    }

    public static void UnevenCadencePreservesNativePeak()
    {
        foreach (var hz in new[] { 10, 20, 40, 100 })
        {
            var rows = Trace(hz);
            var r = Analyze(rows);
            Assert(r.IsComplete, "supported cadence did not settle: " + hz);
            Near(rows.Where(s => s.OnGround && s.TimeSeconds <= r.EndOffsetSeconds).Max(s => s.GForce),
                r.PeakG, 1e-12, "peak is not an actual sample at " + hz);
        }
        var irregular = Trace(100);
        irregular.RemoveAll(s => s.TimeSeconds > 0 && ((int)Math.Round(s.TimeSeconds * 100)) % 3 == 1);
        var result = Analyze(irregular);
        Assert(result.IsComplete, "nonuniform cadence did not settle");
        Near(irregular.Where(s => s.OnGround && s.TimeSeconds <= result.EndOffsetSeconds).Max(s => s.GForce),
            result.PeakG, 1e-12, "irregular peak was resampled");
    }

    public static void SurfaceSlopeDoesNotBecomeVerticalMotion()
    {
        var rows = Trace();
        var flat = Analyze(rows);
        foreach (var s in rows)
        {
            s.GroundAltitudeFeet = s.TimeSeconds * 2;
            s.VelocityWorldYFps += 2;
        }
        var slope = Analyze(rows);
        Assert(slope.IsComplete, "linear surface motion was treated as continuing impact");
        Near(flat.PeakG, slope.PeakG, 1e-12, "slope changed observed G");
        Near(flat.EndOffsetSeconds, slope.EndOffsetSeconds, .011, "surface-relative separator changed on a linear slope");
    }

    public static void RawAndSavedInputsUseTheSameEngine()
    {
        var saved = Trace();
        var raw = saved.Select(s => new TelemetrySample
        {
            SimulationTimeSeconds = s.TimeSeconds, HostElapsedSeconds = s.TimeSeconds * .01,
            OnGround = s.OnGround, MotionSimulation = s.MotionSimulation, GForce = s.GForce,
            VelocityWorldYFps = s.VelocityWorldYFps, GroundAltitudeFeet = s.GroundAltitudeFeet,
            RotationVelocityBodyXRadiansPerSecond = s.RotationVelocityBodyXRadiansPerSecond,
            RotationVelocityBodyZRadiansPerSecond = s.RotationVelocityBodyZRadiansPerSecond
        }).ToList();
        var a = Analyze(saved);
        var b = TouchdownImpactAnalysis.Analyze(raw, Contact(saved));
        Equal(a.Status, b.Status, "raw/saved status mismatch");
        Near(a.PeakG, b.PeakG, 1e-12, "raw/saved G mismatch");
        Near(a.EndOffsetSeconds, b.EndOffsetSeconds, 1e-12, "delivery-clock timing affected result");
        foreach (var sample in raw)
        { sample.AnalysisTimeSeconds = sample.SimulationTimeSeconds; if (sample.OnGround) sample.SimulationTimeSeconds += 100; }
        var activeTime = TouchdownImpactAnalysis.Analyze(raw, Contact(saved));
        Equal(a.Status, activeTime.Status, "public raw impact API ignored the recorder's active physics clock");
        Near(a.EndOffsetSeconds, activeTime.EndOffsetSeconds, 1e-12, "confirmed pause changed the impact window");
        foreach (var sample in raw)
        {
            sample.HostElapsedSeconds = sample.AnalysisTimeSeconds;
            sample.AnalysisTimeSeconds = double.NaN;
            sample.SimulationTimeSeconds = double.NaN;
        }
        Equal(TouchdownImpactStatus.Unavailable, TouchdownImpactAnalysis.Analyze(raw, Contact(saved)).Status,
            "missing simulator clock silently used delivery time");
    }

    public static void IncompleteAndNonContactsAreNotClassified()
    {
        var rows = Trace();
        var contact = Contact(rows);
        Equal(TouchdownImpactStatus.Unavailable, TouchdownImpactAnalysis.Analyze(rows, contact + 1).Status,
            "already-grounded frame became a new contact");
        rows.RemoveAll(s => s.TimeSeconds > .4);
        var incomplete = Analyze(rows);
        Equal(TouchdownImpactStatus.Incomplete, incomplete.Status, "EOF became settled automatically");
        Assert(!incomplete.IsComplete && incomplete.Category == TouchdownImpactCategory.Unavailable,
            "partial observed peak received a final category");
        Assert(double.IsNaN(incomplete.EndOffsetSeconds) && incomplete.PeakG > 1, "partial peak provenance lost");
    }

    private static TouchdownImpactResult Complete(double g) => new TouchdownImpactResult(TouchdownImpactStatus.Settled, g, .2, .8);
    private static TouchdownImpactResult Analyze(List<TouchdownImpactSample> samples) => TouchdownImpactAnalysis.Analyze(samples, Contact(samples));
    private static int Contact(List<TouchdownImpactSample> samples) => samples.FindIndex(s => s.OnGround);
    private static double Gaussian(double t, double center, double width) => Math.Exp(-Math.Pow((t - center) / width, 2));

    private static List<TouchdownImpactSample> Trace(int hz = 100)
    {
        var result = new List<TouchdownImpactSample>();
        for (var i = -hz / 2; i <= 4 * hz; i++)
        {
            var t = i / (double)hz;
            result.Add(Sample(t, t < 0 ? 1 : 1 + .5 * Gaussian(t, .35, .08),
                t < 0 ? -2 : -.5 * Gaussian(t, .15, .12), t >= 0));
        }
        return result;
    }

    private static TouchdownImpactSample Sample(double t, double g, double vy, bool ground) => new TouchdownImpactSample
    {
        TimeSeconds = t, GForce = g, VelocityWorldYFps = vy, OnGround = ground,
        GroundAltitudeFeet = 0, RotationVelocityBodyXRadiansPerSecond = 0, RotationVelocityBodyZRadiansPerSecond = 0
    };

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static void Equal<T>(T expected, T actual, string message) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), message + ": " + actual);
    private static void Near(double expected, double actual, double tolerance, string message) => Assert(Math.Abs(expected - actual) <= tolerance, message + ": " + actual);
}
