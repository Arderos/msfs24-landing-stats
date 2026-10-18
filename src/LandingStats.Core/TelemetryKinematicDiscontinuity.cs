using System;
using System.Collections.Generic;

namespace LandingStats.Core;

/// <summary>An unexplained position/clock replacement, not a judgement about landing speed or quality.</summary>
public sealed class TelemetryPositionDiscontinuity
{
    internal TelemetryPositionDiscontinuity(double elapsed, double horizontal, double vertical,
        double horizontalAllowance, double verticalAllowance, string? reason = null)
    {
        ElapsedSeconds = elapsed;
        HorizontalDistanceMeters = horizontal;
        VerticalDistanceMeters = vertical;
        HorizontalAllowanceMeters = horizontalAllowance;
        VerticalAllowanceMeters = verticalAllowance;
        _reason = reason;
    }

    public double ElapsedSeconds { get; }
    public double HorizontalDistanceMeters { get; }
    public double VerticalDistanceMeters { get; }
    public double HorizontalAllowanceMeters { get; }
    public double VerticalAllowanceMeters { get; }
    private readonly string? _reason;
    public string Reason => _reason ?? (HorizontalDistanceMeters > HorizontalAllowanceMeters
        ? VerticalDistanceMeters > VerticalAllowanceMeters ? "position_jump" : "horizontal_position_jump"
        : "altitude_position_jump");

    public Dictionary<string, double> Measurements() => new Dictionary<string, double>
    {
        ["elapsed_seconds"] = ElapsedSeconds,
        ["horizontal_distance_m"] = HorizontalDistanceMeters,
        ["vertical_distance_m"] = VerticalDistanceMeters,
        ["horizontal_allowance_m"] = HorizontalAllowanceMeters,
        ["vertical_allowance_m"] = VerticalAllowanceMeters,
        ["position_uncertainty_m"] = TelemetryKinematicDiscontinuity.PositionUncertaintyMeters,
        ["channel_alignment_seconds"] = TelemetryKinematicDiscontinuity.ChannelAlignmentSeconds,
        ["clock_reversal_tolerance_seconds"] = TelemetryKinematicDiscontinuity.ClockReversalToleranceSeconds,
    };
}

/// <summary>
/// Conservative continuity evidence for active physics. Distance must exceed
/// what the simulator clock, endpoint speeds and acceleration can explain;
/// reversing the simulator clock is independently a physical boundary.
/// These allowances are uncertainty margins, never minimum flight/airtime/GS
/// requirements; a stationary helicopter and a high-speed jet use the same rule.
/// </summary>
public static class TelemetryKinematicDiscontinuity
{
    public const double PositionUncertaintyMeters = 50;
    public const double ChannelAlignmentSeconds = .25;
    public const double ClockReversalToleranceSeconds = .001;
    private const double GravityMetersPerSecondSquared = 9.80665;
    // Four g of unobserved acceleration makes this a conservative relocation
    // detector, not a validator of an aircraft's flight envelope. Measured
    // acceleration above this allowance expands it instead of rejecting flight.
    public const double MinimumAccelerationAllowanceMetersPerSecondSquared = 4 * GravityMetersPerSecondSquared;
    private const double FeetToMeters = .3048;
    private const double KnotsToMetersPerSecond = 1852.0 / 3600.0;
    private const double TimeTolerance = 1e-9;

    public static bool TryDetect(TelemetrySample previous, TelemetrySample current,
        out TelemetryPositionDiscontinuity? discontinuity)
    {
        discontinuity = null;
        if (previous == null || current == null || !Active(previous) || !Active(current)) return false;
        var elapsed = TelemetryAnalysisFrames.TimeOf(current) - TelemetryAnalysisFrames.TimeOf(previous);
        if (!Finite(elapsed)) return false;
        if (elapsed < -ClockReversalToleranceSeconds)
        {
            discontinuity = new TelemetryPositionDiscontinuity(elapsed, double.NaN, double.NaN,
                double.NaN, double.NaN, "simulation_time_reversed");
            return true;
        }
        if (elapsed < -TimeTolerance) return false;
        var interval = Math.Max(0, elapsed) + ChannelAlignmentSeconds;
        var speed = MaximumNonnegative(previous.GroundSpeedKnots, current.GroundSpeedKnots) * KnotsToMetersPerSecond;
        var acceleration = Math.Max(MinimumAccelerationAllowanceMetersPerSecondSquared,
            Math.Max(AccelerationAllowance(previous), AccelerationAllowance(current)));
        var accelerationDistance = .5 * acceleration * interval * interval;

        var horizontal = double.NaN;
        var horizontalAllowance = double.NaN;
        if (PositionAvailable(previous) && PositionAvailable(current) && Finite(speed))
        {
            horizontal = GeoDistance.GreatCircleNauticalMiles(previous.LatitudeDegrees, previous.LongitudeDegrees,
                current.LatitudeDegrees, current.LongitudeDegrees) * 1852;
            horizontalAllowance = PositionUncertaintyMeters + speed * interval + accelerationDistance;
        }

        var vertical = double.NaN;
        var verticalAllowance = double.NaN;
        if (Finite(previous.PlaneAltitudeFeet) && Finite(current.PlaneAltitudeFeet) &&
            Finite(previous.VelocityWorldYFps) && Finite(current.VelocityWorldYFps))
        {
            vertical = Math.Abs(current.PlaneAltitudeFeet - previous.PlaneAltitudeFeet) * FeetToMeters;
            var verticalSpeed = Math.Max(Math.Abs(previous.VelocityWorldYFps), Math.Abs(current.VelocityWorldYFps)) * FeetToMeters;
            // Include horizontal speed too: pose/velocity reference points and
            // async channels can disagree during rotations or a sloping rollout.
            verticalAllowance = PositionUncertaintyMeters + (verticalSpeed + (Finite(speed) ? speed : 0)) * interval + accelerationDistance;
        }

        if (!(horizontal > horizontalAllowance) && !(vertical > verticalAllowance)) return false;
        discontinuity = new TelemetryPositionDiscontinuity(elapsed, horizontal, vertical, horizontalAllowance, verticalAllowance);
        return true;
    }

    /// <summary>
    /// Split raw capture rows before deriving aircraft geometry or record data.
    /// Original pause/inactive evidence remains with its physical segment;
    /// analysis-only sentinels are omitted and no source objects are rewritten.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TelemetrySample>> SplitPhysicalSegments(
        IReadOnlyList<TelemetrySample> samples)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        if (samples.Count == 0) return Array.Empty<IReadOnlyList<TelemetrySample>>();
        var starts = FindSegmentStarts(samples);
        var result = new List<IReadOnlyList<TelemetrySample>>(starts.Count);
        for (var segment = 0; segment < starts.Count; segment++)
        {
            var end = segment + 1 < starts.Count ? starts[segment + 1] : samples.Count;
            var rows = new List<TelemetrySample>(end - starts[segment]);
            for (var index = starts[segment]; index < end; index++)
                if (samples[index].AnalysisDiscontinuity == null) rows.Add(samples[index]);
            if (rows.Count > 0) result.Add(rows);
        }
        return result;
    }

    /// <summary>
    /// Return the original rows belonging to this contact's physical segment.
    /// Paused rows remain intact; synthetic analysis barriers are never returned.
    /// No arbitrary first match is used when legacy sequences are all zero.
    /// </summary>
    public static IReadOnlyList<TelemetrySample> SelectContactSegment(IReadOnlyList<TelemetrySample> samples,
        long contactSequence, double contactTimeSeconds)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        var starts = FindSegmentStarts(samples);
        var hasSyntheticBarrier = false;
        for (var index = 0; index < samples.Count; index++)
            hasSyntheticBarrier |= samples[index].AnalysisDiscontinuity != null;
        // Existing import/legacy callers can supply an estimated contact time
        // without an exact raw row. With one physical segment there is nothing
        // ambiguous to resolve, so retain that original input unchanged.
        if (starts.Count == 1 && !hasSyntheticBarrier) return samples;
        var exact = -1;
        var exactSegment = -1;
        var ambiguousExact = false;
        var timeMatch = -1;
        var timeSegment = -1;
        var ambiguousTime = false;
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (sample.AnalysisDiscontinuity != null || !Active(sample) || !sample.OnGround ||
                Math.Abs(TelemetryAnalysisFrames.TimeOf(sample) - contactTimeSeconds) > TimeTolerance) continue;
            var segment = SegmentOf(index);
            if (timeSegment >= 0 && timeSegment != segment) ambiguousTime = true;
            timeSegment = segment;
            timeMatch = index;
            if (sample.Sequence == contactSequence)
            {
                if (exactSegment >= 0 && exactSegment != segment) ambiguousExact = true;
                exactSegment = segment;
                exact = index;
            }
        }
        var contactIndex = exact >= 0 ? ambiguousExact ? -1 : exact : ambiguousTime ? -1 : timeMatch;
        if (contactIndex < 0) return Array.Empty<TelemetrySample>();

        var start = 0;
        var end = samples.Count;
        foreach (var candidate in starts)
        {
            if (candidate <= contactIndex) start = candidate;
            else { end = candidate; break; }
        }
        var selected = new List<TelemetrySample>(end - start);
        for (var index = start; index < end; index++)
            if (samples[index].AnalysisDiscontinuity == null) selected.Add(samples[index]);
        return selected;

        int SegmentOf(int index)
        {
            var segment = 0;
            while (segment + 1 < starts.Count && starts[segment + 1] <= index) segment++;
            return segment;
        }
    }

    internal static List<int> FindSegmentStarts(IReadOnlyList<TelemetrySample> samples)
    {
        var starts = new List<int> { 0 };
        TelemetrySample? previousActive = null;
        long? sdkSegment = null;
        var hasActiveEvidence = false;
        var pendingPhysicsBreak = false;
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (sample.AnalysisDiscontinuity != null)
            {
                // The inactive sentinel terminates the preceding impact window;
                // the subsequent actual frame belongs to the replacement flight.
                Add(index + 1);
                previousActive = null;
                pendingPhysicsBreak = false;
                hasActiveEvidence = false;
                continue;
            }
            if (sample.SimulatorState != TelemetrySimulatorState.Legacy)
            {
                if (sdkSegment.HasValue && sdkSegment.Value != sample.FlightSegment)
                {
                    if (hasActiveEvidence)
                    {
                        // The SDK change already starts a replacement segment,
                        // including its initial inactive barrier. Do not attach
                        // that new segment's reset clock to the old calibration.
                        Add(index);
                        pendingPhysicsBreak = false;
                        hasActiveEvidence = false;
                    }
                    previousActive = null;
                }
                sdkSegment = sample.FlightSegment;
            }
            if (sample.SimulatorState == TelemetrySimulatorState.Paused) continue;
            if (!Active(sample))
            {
                // Without an explicit SDK pause this is not evidence of one
                // continuous flight. Retain these original diagnostic rows in
                // the old segment, then resume calibration/storage afresh.
                pendingPhysicsBreak |= hasActiveEvidence;
                previousActive = null;
                continue;
            }
            if (pendingPhysicsBreak) { Add(index); pendingPhysicsBreak = false; }
            if (previousActive != null && TryDetect(previousActive, sample, out _)) Add(index);
            previousActive = sample;
            hasActiveEvidence = true;
        }
        return starts;

        void Add(int start)
        {
            if (start > starts[starts.Count - 1] && start < samples.Count) starts.Add(start);
        }
    }

    private static bool Active(TelemetrySample sample) => sample.MotionSimulation &&
        sample.SimulatorState != TelemetrySimulatorState.Paused && sample.SimulatorState != TelemetrySimulatorState.Inactive;

    private static bool PositionAvailable(TelemetrySample sample) => Finite(sample.LatitudeDegrees) &&
        Finite(sample.LongitudeDegrees) && Math.Abs(sample.LatitudeDegrees) <= 90 && Math.Abs(sample.LongitudeDegrees) <= 180 &&
        (sample.LatitudeDegrees != 0 || sample.LongitudeDegrees != 0);

    private static double MaximumNonnegative(double first, double second) =>
        Finite(first) && first >= 0 && Finite(second) && second >= 0 ? Math.Max(first, second) : double.NaN;

    private static double AccelerationAllowance(TelemetrySample sample)
    {
        var x = Finite(sample.AccelerationBodyXFps2) ? Math.Abs(sample.AccelerationBodyXFps2) * FeetToMeters : 0;
        var z = Finite(sample.AccelerationBodyZFps2) ? Math.Abs(sample.AccelerationBodyZFps2) * FeetToMeters : 0;
        var normal = Finite(sample.GForce) ? (Math.Abs(sample.GForce) + 1) * GravityMetersPerSecondSquared : 0;
        return x + z + normal;
    }

    private static bool Finite(double value) => AnalysisNumericPolicy.IsFinite(value);
}
