using System;
using System.Collections.Generic;

namespace LandingStats.Core;

/// <summary>Creates an analysis view without changing source telemetry or inventing elapsed physics time.</summary>
public static class TelemetryAnalysisFrames
{
    private const double SameTimeToleranceSeconds = 0.000000001;

    public static double TimeOf(TelemetrySample sample) =>
        Finite(sample.AnalysisTimeSeconds) ? sample.AnalysisTimeSeconds :
        Finite(sample.SimulationTimeSeconds) ? sample.SimulationTimeSeconds : sample.HostElapsedSeconds;

    public static IReadOnlyList<TelemetrySample> Prepare(IReadOnlyList<TelemetrySample> samples,
        TelemetryDecisionJournal? journal = null)
    {
        if (samples is AnalysisFrameList view && view.Prepared) return view;
        var prepared = new AnalysisFrameList(samples.Count) { Prepared = true };
        TelemetrySample? previous = null;
        var paused = false;
        foreach (var source in samples)
        {
            if (source.SimulatorState == TelemetrySimulatorState.Paused)
            {
                paused = true;
                continue;
            }

            var sample = source.SimulatorState == TelemetrySimulatorState.Legacy
                ? source : source.CopyForAnalysis();
            if (sample.SimulatorState == TelemetrySimulatorState.Inactive)
                sample.MotionSimulation = false;
            if (sample.AnalysisDiscontinuity != null)
                journal?.Record(TimeOf(sample), "analysis", "kinematic_discontinuity",
                    "Retain the previously identified position-replacement boundary in the analysis view.",
                    sample.AnalysisDiscontinuity.Measurements());

            var differentSegment = previous != null &&
                previous.SimulatorState != TelemetrySimulatorState.Legacy &&
                sample.SimulatorState != TelemetrySimulatorState.Legacy &&
                previous.FlightSegment != sample.FlightSegment;
            var sameResumeTime = paused && previous != null && !differentSegment &&
                sample.SimulatorState == TelemetrySimulatorState.Running && sample.MotionSimulation &&
                previous.MotionSimulation &&
                Math.Abs(TimeOf(sample) - TimeOf(previous)) <= SameTimeToleranceSeconds;

            // A frozen-time resume is not a new physical measurement. In particular,
            // it must not replace a genuine contact or G peak during deduplication.
            if (sameResumeTime && sample.OnGround == previous!.OnGround)
                continue;

            TelemetryPositionDiscontinuity? discontinuity = null;
            if (previous != null && !differentSegment)
                TelemetryKinematicDiscontinuity.TryDetect(previous, sample, out discontinuity);

            if (previous != null && previous.MotionSimulation && sample.MotionSimulation &&
                (differentSegment || sameResumeTime || discontinuity != null))
            {
                var barrier = sample.CopyForAnalysis();
                barrier.SimulatorState = TelemetrySimulatorState.Inactive;
                barrier.MotionSimulation = false;
                barrier.AnalysisDiscontinuity = discontinuity;
                if (discontinuity?.Reason == "simulation_time_reversed")
                {
                    // This sentinel terminates the OLD timeline. Carrying the
                    // replacement flight's reversed clock into it would poison
                    // otherwise valid geometry from the completed first flight.
                    barrier.AnalysisTimeSeconds = TimeOf(previous);
                    barrier.SimulationTimeSeconds = barrier.AnalysisTimeSeconds;
                }
                prepared.Add(barrier);
                if (discontinuity != null)
                    journal?.Record(TimeOf(sample), "analysis", "kinematic_discontinuity",
                        discontinuity.Reason == "simulation_time_reversed"
                            ? "Simulator time moved backwards; preserve the previous contact but do not join the replacement flight's physics or geometry."
                            : "Position changed beyond the distance explained by active time, speeds, acceleration and explicit uncertainty margins; preserve the previous contact but isolate its physics and geometry.",
                        discontinuity.Measurements());
            }

            prepared.Add(sample);
            previous = sample;
            paused = false;
        }
        return prepared;
    }

    private static bool Finite(double value) => AnalysisNumericPolicy.IsFinite(value);
}

// Analysis-owned views are never modified after publication. Reusing their
// preparation and fits avoids cloning every frame at each geometry stage.
internal sealed class AnalysisFrameList : List<TelemetrySample>
{
    internal AnalysisFrameList(int capacity) : base(capacity) { }
    internal AnalysisFrameList(IEnumerable<TelemetrySample> values) : base(values) { }
    internal bool Prepared, Deduplicated;
    internal IReadOnlyList<TelemetrySample>? DeduplicatedView;
    internal List<TelemetrySample>? NormalizedView;
    internal double[]? SanitizedGround;
}
