using System;
using System.Collections.Generic;

namespace LandingStats.Core;

public static class TelemetryDeduplicator
{
    private const double TimeToleranceSeconds = 0.000000001;

    public static IReadOnlyList<TelemetrySample> Deduplicate(IReadOnlyList<TelemetrySample> samples)
    {
        var result = new List<TelemetrySample>(samples.Count);

        foreach (var sample in samples)
        {
            if (result.Count > 0 &&
                Math.Abs(TouchdownAnalysis.TimeOf(result[result.Count - 1]) - TouchdownAnalysis.TimeOf(sample)) <=
                TimeToleranceSeconds)
            {
                // Keep the latest message for a simulation instant. It can contain a latch or state update
                // that arrived without advancing simulation time.
                result[result.Count - 1] = sample;
            }
            else
            {
                result.Add(sample);
            }
        }

        return result;
    }
}
