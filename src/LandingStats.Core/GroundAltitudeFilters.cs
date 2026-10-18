using System;
using System.Collections.Generic;
namespace LandingStats.Core;
// Distinct published repair policies share one implementation home, not one changed algorithm.
internal static class GroundAltitudeFilters
{
    private const double GroundOutlierThresholdFeet = AnalysisNumericPolicy.GroundOutlierFeet;
    private static bool IsFinite(double value) => AnalysisNumericPolicy.IsFinite(value);
    internal static double AtContact(
        IReadOnlyList<TelemetrySample> samples,
        int targetIndex)
    {
        var target = samples[targetIndex];

        var values = new List<double>(5);
        var first = targetIndex;
        var last = targetIndex;
        while (first > Math.Max(0, targetIndex - 2) && samples[first - 1].MotionSimulation) first--;
        while (last < Math.Min(samples.Count - 1, targetIndex + 2) && samples[last + 1].MotionSimulation) last++;
        for (var index = first; index <= last; index++)
        {
            values.Add(samples[index].GroundAltitudeFeet);
        }

        values.Sort();
        var median = values[values.Count / 2];
        if (Math.Abs(target.GroundAltitudeFeet - median) <= GroundOutlierThresholdFeet ||
            targetIndex == first || targetIndex == last)
        {
            return target.GroundAltitudeFeet;
        }

        var previous = samples[targetIndex - 1];
        var next = samples[targetIndex + 1];
        var duration = TelemetryAnalysisFrames.TimeOf(next) - TelemetryAnalysisFrames.TimeOf(previous);
        if (duration <= 0.000001)
        {
            return median;
        }

        var fraction = (TelemetryAnalysisFrames.TimeOf(target) - TelemetryAnalysisFrames.TimeOf(previous)) / duration;
        return previous.GroundAltitudeFeet +
               (next.GroundAltitudeFeet - previous.GroundAltitudeFeet) * fraction;
    }

    internal static double[] ForClosure(IReadOnlyList<TelemetrySample> samples)
    {
        var view = samples as AnalysisFrameList;
        if (view?.SanitizedGround != null) return view.SanitizedGround;
        var result = new double[samples.Count];
        var original = new double[samples.Count];
        var outliers = new bool[samples.Count];
        var localMedians = new double[samples.Count];
        // A ground-height repair must not interpolate across a physics restart
        // either, even when its target sample belongs to a valid fitting window.
        var segmentStarts = new int[samples.Count];
        var segmentEnds = new int[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            result[index] = samples[index].GroundAltitudeFeet;
            original[index] = result[index];
            localMedians[index] = double.NaN;
            segmentStarts[index] = index == 0 || !samples[index - 1].MotionSimulation || !samples[index].MotionSimulation
                ? index : segmentStarts[index - 1];
        }
        for (var index = samples.Count - 1; index >= 0; index--)
            segmentEnds[index] = index == samples.Count - 1 || !samples[index + 1].MotionSimulation || !samples[index].MotionSimulation
                ? index : segmentEnds[index + 1];

        for (var index = 0; index < samples.Count; index++)
        {
            var local = new List<double>(5);
            var first = Math.Max(segmentStarts[index], Math.Min(index - 2, segmentEnds[index] - 4));
            var last = Math.Min(segmentEnds[index], first + 4);
            first = Math.Max(segmentStarts[index], last - 4);
            for (var neighbor = first; neighbor <= last; neighbor++)
            {
                if (IsFinite(original[neighbor]))
                {
                    local.Add(original[neighbor]);
                }
            }

            if (local.Count < 3 || !IsFinite(original[index]))
            {
                continue;
            }

            local.Sort();
            var median = local[local.Count / 2];
            localMedians[index] = median;
            outliers[index] = Math.Abs(original[index] - median) > GroundOutlierThresholdFeet;
        }

        for (var index = 0; index < samples.Count; index++)
        {
            if (!outliers[index])
            {
                continue;
            }

            var previousIndex = index - 1;
            while (previousIndex >= segmentStarts[index] && (outliers[previousIndex] || !IsFinite(result[previousIndex])))
            {
                previousIndex--;
            }

            var nextIndex = index + 1;
            while (nextIndex <= segmentEnds[index] && (outliers[nextIndex] || !IsFinite(original[nextIndex])))
            {
                nextIndex++;
            }

            if (previousIndex >= segmentStarts[index] && nextIndex <= segmentEnds[index])
            {
                var previousTime = TouchdownAnalysis.TimeOf(samples[previousIndex]);
                var targetTime = TouchdownAnalysis.TimeOf(samples[index]);
                var nextTime = TouchdownAnalysis.TimeOf(samples[nextIndex]);
                var duration = nextTime - previousTime;
                if (duration > 0.000001)
                {
                    var fraction = (targetTime - previousTime) / duration;
                    result[index] = result[previousIndex] +
                                    (original[nextIndex] - result[previousIndex]) * fraction;
                }
                else
                {
                    result[index] = localMedians[index];
                }
            }
            else if (previousIndex >= segmentStarts[index])
            {
                result[index] = result[previousIndex];
            }
            else if (nextIndex <= segmentEnds[index])
            {
                result[index] = original[nextIndex];
            }
            else
            {
                result[index] = localMedians[index];
            }
        }

        if (view != null) view.SanitizedGround = result;
        return result;
    }

    internal static void ForCalibration(double[] values)
    {
        var original = (double[])values.Clone();
        for (var index = 2; index < values.Length - 2; index++)
        {
            var local = new[]
            {
                original[index - 2],
                original[index - 1],
                original[index],
                original[index + 1],
                original[index + 2],
            };
            var allFinite = true;
            for (var localIndex = 0; localIndex < local.Length; localIndex++)
            {
                if (!IsFinite(local[localIndex]))
                {
                    allFinite = false;
                    break;
                }
            }

            if (!allFinite)
            {
                continue;
            }

            Array.Sort(local);
            if (Math.Abs(original[index] - local[2]) > GroundOutlierThresholdFeet)
            {
                // Match the sequential research sanitizer: the left neighbor may
                // already have been repaired, while the right one is still original.
                values[index] = (values[index - 1] + original[index + 1]) / 2.0;
            }
        }
    }
}
