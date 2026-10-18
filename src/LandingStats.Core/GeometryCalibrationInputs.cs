using System;
using System.Collections.Generic;
using System.Linq;
using static LandingStats.Core.GeometryCalibrationMath;

namespace LandingStats.Core;

// Pure component extracted without changing the established estimator equations.
internal static class GeometryCalibrationInputs
{
    internal static List<TelemetrySample> Normalize(IReadOnlyList<TelemetrySample> samples)
    {
        var view = samples as AnalysisFrameList;
        if (view?.NormalizedView != null) return view.NormalizedView;
        var sdkAware = samples.Any(sample => sample != null && sample.SimulatorState != TelemetrySimulatorState.Legacy);
        var prepared = sdkAware ? TelemetryAnalysisFrames.Prepare(samples) : samples;
        var finite = prepared.Where(sample => sample is not null && IsFinite(TelemetryAnalysisFrames.TimeOf(sample)));
        var ordered = sdkAware ? finite.ToArray() : finite.OrderBy(sample => TelemetryAnalysisFrames.TimeOf(sample)).ToArray();
        var normalized = new AnalysisFrameList(TelemetryDeduplicator.Deduplicate(ordered)) { Prepared = true, Deduplicated = true };
        normalized.NormalizedView = normalized;
        if (view != null) view.NormalizedView = normalized;
        return normalized;
    }
}
