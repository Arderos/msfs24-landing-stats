using System;
using System.Collections.Generic;

namespace LandingStats.Core;

public static class TelemetryDeduplicator
{
    private const double TimeToleranceSeconds = 0.000000001;

    public static IReadOnlyList<TelemetrySample> Deduplicate(IReadOnlyList<TelemetrySample> samples)
    {
        var view = samples as AnalysisFrameList;
        if (view?.Deduplicated == true) return samples;
        if (view?.DeduplicatedView != null) return view.DeduplicatedView;
        var result = new AnalysisFrameList(samples.Count) { Prepared = view?.Prepared == true, Deduplicated = true };

        foreach (var sample in samples)
        {
            if (result.Count > 0 &&
                SameDiscreteState(result[result.Count - 1], sample) &&
                Math.Abs(TouchdownAnalysis.TimeOf(result[result.Count - 1]) - TouchdownAnalysis.TimeOf(sample)) <=
                TimeToleranceSeconds)
            {
                // Numeric updates at one physics instant keep the latest value.
                // Discrete ground/source changes are evidence already consumed
                // by live recording; offline analysis must retain the same edges.
                result[result.Count - 1] = sample;
            }
            else
            {
                result.Add(sample);
            }
        }

        if (view != null) view.DeduplicatedView = result;
        return result;
    }

    /// <summary>Whether numeric delivery updates can share one retained row without losing a discrete physics/source boundary.</summary>
    public static bool SameDiscreteState(TelemetrySample previous, TelemetrySample current) =>
        previous.MotionSimulation == current.MotionSimulation && previous.OnGround == current.OnGround &&
        previous.SimulatorState == current.SimulatorState && previous.FlightSegment == current.FlightSegment &&
        SameSourceControlState(previous, current);

    /// <summary>Whether source control, availability and source identity carry the same contact-continuity evidence.</summary>
    public static bool SameSourceControlState(TelemetrySample previous, TelemetrySample current) =>
        TelemetryContactPhysics.IsBlocked(previous) == TelemetryContactPhysics.IsBlocked(current) &&
        TelemetryContactPhysics.IsFreshReleased(previous) == TelemetryContactPhysics.IsFreshReleased(current) &&
        (previous.SourceEvidence == null || current.SourceEvidence == null ||
            previous.SourceEvidence.SameSource(current.SourceEvidence));
}
