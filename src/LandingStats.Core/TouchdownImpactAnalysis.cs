using System;
using System.Collections.Generic;

namespace LandingStats.Core;

public enum TouchdownImpactStatus
{
    Unavailable,
    Settled,
    NoDistinctImpulse,
    Liftoff,
    Incomplete,
    InvalidTelemetry
}

public enum TouchdownImpactCategory
{
    Unavailable,
    Smooth,
    Normal,
    Firm
}

/// <summary>The channels shared by raw telemetry and saved landing series.</summary>
public sealed class TouchdownImpactSample
{
    public double TimeSeconds { get; set; }
    public bool OnGround { get; set; }
    public bool MotionSimulation { get; set; } = true;
    public double GForce { get; set; }
    public double VelocityWorldYFps { get; set; }
    public double GroundAltitudeFeet { get; set; } = double.NaN;
    public double RotationVelocityBodyXRadiansPerSecond { get; set; } = double.NaN;
    public double RotationVelocityBodyZRadiansPerSecond { get; set; } = double.NaN;
}

public sealed class TouchdownImpactResult
{
    public TouchdownImpactResult(
        TouchdownImpactStatus status = TouchdownImpactStatus.Unavailable,
        double peakG = double.NaN,
        double peakOffsetSeconds = double.NaN,
        double endOffsetSeconds = double.NaN)
    {
        Status = status;
        PeakG = peakG;
        PeakOffsetSeconds = peakOffsetSeconds;
        EndOffsetSeconds = endOffsetSeconds;
    }

    public TouchdownImpactStatus Status { get; }
    public double PeakG { get; }
    public double PeakOffsetSeconds { get; }
    public double EndOffsetSeconds { get; }

    public bool IsComplete =>
        (Status == TouchdownImpactStatus.Settled ||
         Status == TouchdownImpactStatus.NoDistinctImpulse ||
         Status == TouchdownImpactStatus.Liftoff) &&
        TouchdownImpactAnalysis.IsFinite(PeakG) && PeakG > 0 &&
        TouchdownImpactAnalysis.IsFinite(PeakOffsetSeconds) && PeakOffsetSeconds >= 0 &&
        TouchdownImpactAnalysis.IsFinite(EndOffsetSeconds) && EndOffsetSeconds >= PeakOffsetSeconds;

    public TouchdownImpactCategory Category => !IsComplete ? TouchdownImpactCategory.Unavailable :
        PeakG < 1.2 ? TouchdownImpactCategory.Smooth :
        PeakG < 1.5 ? TouchdownImpactCategory.Normal : TouchdownImpactCategory.Firm;
}

/// <summary>
/// Separates a contact's initial load response from steady motion. The short fit
/// and confirmation intervals resolve a state; neither is a deadline after TD.
/// G is always an original sample, not a derivative, average or vector magnitude.
/// No gear geometry, wheel contact or aircraft-specific correction is required.
/// </summary>
public static class TouchdownImpactAnalysis
{
    public const int CurrentVersion = 1;
    private const double DuplicateTolerance = 1e-9;
    private const double MaximumFrameGap = .15;
    private const double FitSpan = .30;
    private const double ConfirmationSpan = .15;

    public static TouchdownImpactResult Analyze(IReadOnlyList<TelemetrySample> samples, int contactIndex)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        if (contactIndex < 0 || contactIndex >= samples.Count || !IsFinite(samples[contactIndex].SimulationTimeSeconds))
            return new TouchdownImpactResult();
        var input = new List<TouchdownImpactSample>(samples.Count);
        foreach (var sample in samples)
        {
            input.Add(new TouchdownImpactSample
            {
                // Receipt time can change under a delivery backlog. Missing
                // simulator time is unavailable here, not a new physical clock.
                TimeSeconds = sample.SimulationTimeSeconds,
                OnGround = sample.OnGround,
                MotionSimulation = sample.MotionSimulation,
                GForce = sample.GForce,
                VelocityWorldYFps = sample.VelocityWorldYFps,
                GroundAltitudeFeet = sample.GroundAltitudeFeet,
                RotationVelocityBodyXRadiansPerSecond = sample.RotationVelocityBodyXRadiansPerSecond,
                RotationVelocityBodyZRadiansPerSecond = sample.RotationVelocityBodyZRadiansPerSecond
            });
        }
        return Analyze(input, contactIndex);
    }

    public static TouchdownImpactResult Analyze(IReadOnlyList<TouchdownImpactSample> samples, int contactIndex)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        if (contactIndex < 0 || contactIndex >= samples.Count) return new TouchdownImpactResult();

        // Latest delivery owns a simulator instant, including its final on-ground
        // state. Remap the caller's contact index rather than selecting a later TD.
        var unique = new List<TouchdownImpactSample>(samples.Count);
        var contact = -1;
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (unique.Count > 0 && Math.Abs(s.TimeSeconds - unique[unique.Count - 1].TimeSeconds) <= DuplicateTolerance)
                unique[unique.Count - 1] = s;
            else
                unique.Add(s);
            if (i == contactIndex) contact = unique.Count - 1;
        }

        var first = unique[contact];
        if (!first.OnGround) return new TouchdownImpactResult();
        if (!IsFinite(first.TimeSeconds) || !first.MotionSimulation ||
            !IsFinite(first.GForce) || !IsFinite(first.VelocityWorldYFps))
            return new TouchdownImpactResult(TouchdownImpactStatus.InvalidTelemetry);
        if (contact > 0)
        {
            var previous = unique[contact - 1];
            if (previous.OnGround) return new TouchdownImpactResult();
            if (!IsFinite(previous.TimeSeconds) || previous.TimeSeconds >= first.TimeSeconds || !previous.MotionSimulation)
                return new TouchdownImpactResult(TouchdownImpactStatus.InvalidTelemetry);
            if (first.TimeSeconds - previous.TimeSeconds > MaximumFrameGap + DuplicateTolerance)
                return new TouchdownImpactResult(TouchdownImpactStatus.Incomplete, first.GForce, 0);
        }

        var incoming = ApproachVelocity(unique, contact);
        var velocityBand = Math.Max(.35, Math.Min(.75, .20 * (IsFinite(incoming) ? Math.Abs(incoming) : 0)));
        var peak = double.NegativeInfinity;
        var peakTime = double.NaN;
        var quietStart = double.NaN;
        var terrainState = 0;
        var rotationXState = 0;
        var rotationZState = 0;
        var recentSteps = new double[11];
        for (var i = contact; i < unique.Count; i++)
        {
            var s = unique[i];
            if (!IsFinite(s.TimeSeconds) || !s.MotionSimulation)
                return Result(TouchdownImpactStatus.InvalidTelemetry, peak, peakTime);
            if (i > contact)
            {
                var dt = s.TimeSeconds - unique[i - 1].TimeSeconds;
                if (dt <= 0) return Result(TouchdownImpactStatus.InvalidTelemetry, peak, peakTime);
                if (dt > MaximumFrameGap + DuplicateTolerance)
                    return Result(TouchdownImpactStatus.Incomplete, peak, peakTime);
            }
            // First airborne sample is an exclusive ownership boundary. Its G
            // belongs to flight/the next contact and cannot overwrite this peak.
            if (!s.OnGround)
                return Complete(TouchdownImpactStatus.Liftoff, peak, peakTime, s.TimeSeconds - first.TimeSeconds);
            if (!IsFinite(s.GForce) || !IsFinite(s.VelocityWorldYFps) ||
                !ObserveOptional(s.GroundAltitudeFeet, ref terrainState) ||
                !ObserveOptional(s.RotationVelocityBodyXRadiansPerSecond, ref rotationXState) ||
                !ObserveOptional(s.RotationVelocityBodyZRadiansPerSecond, ref rotationZState))
                return Result(TouchdownImpactStatus.InvalidTelemetry, peak, peakTime);
            if (s.GForce > peak)
            {
                peak = s.GForce;
                peakTime = s.TimeSeconds - first.TimeSeconds;
            }

            var count = 0;
            for (var k = Math.Max(contact + 1, i - 10); k <= i; k++)
                recentSteps[count++] = unique[k].TimeSeconds - unique[k - 1].TimeSeconds;
            Array.Sort(recentSteps, 0, count);
            var cadence = count == 0 ? 0 : count % 2 == 1 ? recentSteps[count / 2] :
                (recentSteps[count / 2 - 1] + recentSteps[count / 2]) / 2;
            var span = Math.Max(FitSpan, 3.1 * cadence);
            var from = i;
            while (from > contact && unique[from - 1].TimeSeconds >= s.TimeSeconds - span - 1e-10) from--;
            if (i - from + 1 < 4 || s.TimeSeconds - unique[from].TimeSeconds < .8 * span)
            {
                quietStart = double.NaN;
                continue;
            }

            var g = Fit(unique, from, i, sample => sample.GForce);
            var velocity = Fit(unique, from, i, sample => sample.VelocityWorldYFps);
            var allZero = true;
            for (var k = from; k <= i; k++) allZero &= Math.Abs(unique[k].GForce) < 1e-12;
            if (allZero) return Result(TouchdownImpactStatus.InvalidTelemetry, peak, peakTime);
            var terrain = terrainState == 1 ? Fit(unique, from, i, sample => sample.GroundAltitudeFeet) : default;
            if (!g.Valid || !velocity.Valid || (terrainState == 1 && !terrain.Valid))
                return Result(TouchdownImpactStatus.InvalidTelemetry, peak, peakTime);
            if (terrain.Valid && terrain.Residual > .10)
            {
                quietStart = double.NaN;
                continue;
            }

            var surfaceRate = terrain.Valid ? terrain.Slope : 0;
            var motionLevel = 0.0;
            for (var k = from; k <= i; k++)
                motionLevel = Math.Max(motionLevel, Math.Abs(unique[k].VelocityWorldYFps - surfaceRate));
            var quiet = Math.Abs(g.Slope) <= .10 && g.Residual <= .015 && g.Range <= .05 &&
                        motionLevel <= velocityBand && velocity.Residual <= .12;
            if (rotationXState == 1)
            {
                var rotation = Fit(unique, from, i, sample => sample.RotationVelocityBodyXRadiansPerSecond);
                if (!rotation.Valid) return Result(TouchdownImpactStatus.InvalidTelemetry, peak, peakTime);
                quiet &= rotation.Residual <= .003;
            }
            if (rotationZState == 1)
            {
                var rotation = Fit(unique, from, i, sample => sample.RotationVelocityBodyZRadiansPerSecond);
                if (!rotation.Valid) return Result(TouchdownImpactStatus.InvalidTelemetry, peak, peakTime);
                quiet &= rotation.Residual <= .003;
            }
            if (!quiet)
            {
                quietStart = double.NaN;
                continue;
            }
            if (double.IsNaN(quietStart)) quietStart = s.TimeSeconds;
            if (s.TimeSeconds - quietStart >= ConfirmationSpan - DuplicateTolerance)
                return Complete(peak - g.Mean >= .03 ? TouchdownImpactStatus.Settled : TouchdownImpactStatus.NoDistinctImpulse,
                    peak, peakTime, s.TimeSeconds - first.TimeSeconds);
        }
        return Result(peak <= 0 ? TouchdownImpactStatus.InvalidTelemetry : TouchdownImpactStatus.Incomplete, peak, peakTime);
    }

    private static double ApproachVelocity(IReadOnlyList<TouchdownImpactSample> samples, int contact)
    {
        var last = contact - 1;
        if (last < 0) return double.NaN;
        var from = last;
        for (var i = last; i >= 0; i--)
        {
            var s = samples[i];
            if (s.OnGround || !s.MotionSimulation || !IsFinite(s.TimeSeconds) ||
                samples[contact].TimeSeconds - s.TimeSeconds > .5 ||
                (i < last && (samples[i + 1].TimeSeconds <= s.TimeSeconds ||
                             samples[i + 1].TimeSeconds - s.TimeSeconds > MaximumFrameGap + DuplicateTolerance))) break;
            from = i;
        }
        var velocity = Fit(samples, from, last, s => s.VelocityWorldYFps);
        if (!velocity.Valid) return double.NaN;
        var incoming = velocity.Mean + velocity.Slope * (samples[last].TimeSeconds - velocity.TimeMean);
        var terrain = Fit(samples, from, last, s => s.GroundAltitudeFeet);
        return terrain.Valid && terrain.Residual <= .10 ? incoming - terrain.Slope : incoming;
    }

    // Optional columns may be absent in a legacy series (all NaN), but a partial
    // dropout is not permission to silently switch coordinate systems mid-impact.
    private static bool ObserveOptional(double value, ref int state)
    {
        if (double.IsInfinity(value)) return false;
        state |= double.IsNaN(value) ? 2 : 1;
        return state != 3;
    }

    private static TouchdownImpactResult Complete(TouchdownImpactStatus status, double peak, double peakTime, double end) =>
        peak > 0 && IsFinite(peak)
            ? new TouchdownImpactResult(status, peak, peakTime, end)
            : Result(TouchdownImpactStatus.InvalidTelemetry, peak, peakTime);

    private static TouchdownImpactResult Result(TouchdownImpactStatus status, double peak, double peakTime) =>
        new TouchdownImpactResult(status, IsFinite(peak) ? peak : double.NaN, peakTime);

    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private readonly struct LinearFit
    {
        public LinearFit(double slope, double residual, double mean, double range, double timeMean)
        {
            Slope = slope; Residual = residual; Mean = mean; Range = range; TimeMean = timeMean; Valid = true;
        }
        public bool Valid { get; }
        public double Slope { get; }
        public double Residual { get; }
        public double Mean { get; }
        public double Range { get; }
        public double TimeMean { get; }
    }

    private static LinearFit Fit(IReadOnlyList<TouchdownImpactSample> samples, int first, int last, Func<TouchdownImpactSample, double> value)
    {
        if (last - first + 1 < 4) return default;
        // Trapezoidal time weights preserve irregular simulator cadence without
        // manufacturing uniformly spaced measurements or over-weighting bursts.
        var origin = samples[first].TimeSeconds;
        double total = 0, tx = 0, vy = 0, low = double.PositiveInfinity, high = double.NegativeInfinity;
        for (var i = first; i <= last; i++)
        {
            var v = value(samples[i]);
            if (!IsFinite(v)) return default;
            var weight = Weight(samples, first, last, i);
            total += weight;
            tx += (samples[i].TimeSeconds - origin) * weight;
            vy += v * weight;
            low = Math.Min(low, v); high = Math.Max(high, v);
        }
        if (total <= 0) return default;
        tx /= total; vy /= total;
        double xx = 0, xy = 0;
        for (var i = first; i <= last; i++)
        {
            var t = samples[i].TimeSeconds - origin - tx;
            var weight = Weight(samples, first, last, i);
            xx += weight * t * t;
            xy += weight * t * (value(samples[i]) - vy);
        }
        if (xx <= 1e-14) return default;
        var slope = xy / xx;
        var residual = 0.0;
        for (var i = first; i <= last; i++)
            residual = Math.Max(residual, Math.Abs(value(samples[i]) - (vy + slope * (samples[i].TimeSeconds - origin - tx))));
        return IsFinite(slope) && IsFinite(residual) && IsFinite(vy)
            ? new LinearFit(slope, residual, vy, high - low, origin + tx) : default;
    }

    private static double Weight(IReadOnlyList<TouchdownImpactSample> samples, int first, int last, int i) =>
        i == first ? (samples[first + 1].TimeSeconds - samples[first].TimeSeconds) / 2 :
        i == last ? (samples[last].TimeSeconds - samples[last - 1].TimeSeconds) / 2 :
        (samples[i + 1].TimeSeconds - samples[i - 1].TimeSeconds) / 2;
}
