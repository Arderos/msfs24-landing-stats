using System;
using System.Collections.Generic;
using System.Linq;
using static LandingStats.Core.GeometryCalibrationPolicy;
using static LandingStats.Core.GeometryCalibrationMath;

namespace LandingStats.Core;

// Pure component extracted without changing the established estimator equations.
internal static class GeometryContactEvidence
{
    internal static bool IsMainOnly(TelemetrySample sample, TelemetryGearTopology topology)
    {
        foreach (var point in topology.MainContactPointIndices)
        {
            if (!HasContactEvidence(sample, point))
            {
                return false;
            }
        }

        foreach (var point in topology.NoseContactPointIndices)
        {
            if (HasContactEvidence(sample, point))
            {
                return false;
            }
        }

        return true;
    }

    internal static double MeanCompression(TelemetrySample sample, IReadOnlyList<int> points)
    {
        var sum = 0.0;
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (sample.ContactPointCompression is null || point >= sample.ContactPointCompression.Length)
            {
                return double.NaN;
            }

            sum += sample.ContactPointCompression[point];
        }

        return sum / points.Count;
    }

    internal static bool HasContactEvidence(TelemetrySample sample, int point)
    {
        return sample.MotionSimulation && (HasOnGroundEvidence(sample, point) || HasCompressionEvidence(sample, point));
    }

    internal static bool HasContactTransition(
        TelemetrySample previous,
        TelemetrySample current,
        int point)
    {
        return previous.MotionSimulation && current.MotionSimulation &&
            ((!HasOnGroundEvidence(previous, point) && HasOnGroundEvidence(current, point)) ||
             (!HasCompressionEvidence(previous, point) && HasCompressionEvidence(current, point)));
    }

    internal static bool HasOnGroundEvidence(TelemetrySample sample, int point)
    {
        return sample.ContactPointOnGround is not null &&
            point < sample.ContactPointOnGround.Length &&
            sample.ContactPointOnGround[point];
    }

    internal static bool HasCompressionEvidence(TelemetrySample sample, int point)
    {
        return sample.ContactPointCompression is not null &&
            point < sample.ContactPointCompression.Length &&
            IsFinite(sample.ContactPointCompression[point]) &&
            sample.ContactPointCompression[point] > MinimumCompressionEvidence;
    }
}
