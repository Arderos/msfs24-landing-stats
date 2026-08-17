using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingStats.Core;

/// <summary>
/// Detects replay-like captures in which the simulator replays pose and position,
/// but does not reproduce the corresponding velocities and air data.
/// </summary>
public static class ReplayTelemetryDetector
{
    private const double EvidenceWindowSeconds = 15.0;
    private const double EndBeforeContactSeconds = 0.50;
    private const double MinimumPairDurationSeconds = 0.50;
    private const double MaximumPairDurationSeconds = 1.50;
    private const double MinimumDerivedGroundSpeedKnots = 30.0;
    private const double MaximumReportedGroundSpeedKnots = 5.0;
    private const double MaximumReportedAirspeedKnots = 30.0;
    private const double MinimumAltitudeRateFps = 3.0;
    private const double MinimumVerticalRateErrorFps = 5.0;
    private const int MinimumEvidencePairCount = 8;
    private const double EpisodeGapSeconds = 10.0;

    /// <summary>
    /// Returns true only for a strong, compound inconsistency: geographic position
    /// moves at flight speed while both reported horizontal speed and airspeed are
    /// near zero, and altitude motion also disagrees with WORLD Y. This is a fallback
    /// for replay systems that do not emit the MSFS 2024 replay flow events.
    /// </summary>
    public static bool IsReplayLike(IReadOnlyList<TelemetrySample> samples)
    {
        if (samples is null || samples.Count < MinimumEvidencePairCount + 1)
        {
            return false;
        }

        var ordered = samples
            .Where(sample => sample is not null && IsFinite(sample.SimulationTimeSeconds))
            .OrderBy(sample => sample.SimulationTimeSeconds)
            .ToArray();
        var unique = TelemetryDeduplicator.Deduplicate(ordered);
        var contactIndex = TelemetryContactDetector.Find(unique, EpisodeGapSeconds)
            .Select(candidate => candidate.ContactIndex)
            .DefaultIfEmpty(-1)
            .First();
        if (contactIndex <= 0)
        {
            return false;
        }

        var contactTime = unique[contactIndex].SimulationTimeSeconds;
        var windowStart = contactTime - EvidenceWindowSeconds;
        var windowEnd = contactTime - EndBeforeContactSeconds;
        var derivedGroundSpeed = new List<double>();
        var reportedGroundSpeed = new List<double>();
        var reportedAirspeed = new List<double>();
        var derivedAltitudeRate = new List<double>();
        var reportedVerticalRate = new List<double>();

        var nextIndex = 0;
        for (var index = 0; index < contactIndex; index++)
        {
            var first = unique[index];
            if (first.OnGround || first.SimulationTimeSeconds < windowStart ||
                first.SimulationTimeSeconds > windowEnd)
            {
                continue;
            }

            nextIndex = Math.Max(nextIndex, index + 1);
            while (nextIndex < contactIndex &&
                   unique[nextIndex].SimulationTimeSeconds - first.SimulationTimeSeconds <
                       MinimumPairDurationSeconds)
            {
                nextIndex++;
            }

            if (nextIndex >= contactIndex)
            {
                break;
            }

            var second = unique[nextIndex];
            var duration = second.SimulationTimeSeconds - first.SimulationTimeSeconds;
            if (second.OnGround || second.SimulationTimeSeconds > windowEnd ||
                duration <= 0.0 || duration > MaximumPairDurationSeconds ||
                !HasFiniteKinematics(first) || !HasFiniteKinematics(second))
            {
                continue;
            }

            derivedGroundSpeed.Add(
                GeoDistance.GreatCircleNauticalMiles(
                    first.LatitudeDegrees,
                    first.LongitudeDegrees,
                    second.LatitudeDegrees,
                    second.LongitudeDegrees) * 3600.0 / duration);
            reportedGroundSpeed.Add((Math.Abs(first.GroundSpeedKnots) + Math.Abs(second.GroundSpeedKnots)) / 2.0);
            reportedAirspeed.Add((Math.Abs(first.IndicatedAirspeedKnots) + Math.Abs(second.IndicatedAirspeedKnots)) / 2.0);
            derivedAltitudeRate.Add((second.PlaneAltitudeFeet - first.PlaneAltitudeFeet) / duration);
            reportedVerticalRate.Add((first.VelocityWorldYFps + second.VelocityWorldYFps) / 2.0);
        }

        if (derivedGroundSpeed.Count < MinimumEvidencePairCount)
        {
            return false;
        }

        var medianDerivedGroundSpeed = Median(derivedGroundSpeed);
        var medianReportedGroundSpeed = Median(reportedGroundSpeed);
        var medianReportedAirspeed = Median(reportedAirspeed);
        var medianAbsoluteAltitudeRate = Median(derivedAltitudeRate.Select(Math.Abs));
        var medianVerticalRateError = Median(
            derivedAltitudeRate.Zip(
                reportedVerticalRate,
                (derived, reported) => Math.Abs(derived - reported)));

        return medianDerivedGroundSpeed >= MinimumDerivedGroundSpeedKnots &&
               medianReportedGroundSpeed <= MaximumReportedGroundSpeedKnots &&
               medianReportedAirspeed <= MaximumReportedAirspeedKnots &&
               medianDerivedGroundSpeed >= medianReportedGroundSpeed * 4.0 &&
               medianAbsoluteAltitudeRate >= MinimumAltitudeRateFps &&
               medianVerticalRateError >= MinimumVerticalRateErrorFps;
    }

    private static bool HasFiniteKinematics(TelemetrySample sample)
    {
        return IsFinite(sample.LatitudeDegrees) && sample.LatitudeDegrees >= -90.0 && sample.LatitudeDegrees <= 90.0 &&
               IsFinite(sample.LongitudeDegrees) && sample.LongitudeDegrees >= -180.0 && sample.LongitudeDegrees <= 180.0 &&
               IsFinite(sample.PlaneAltitudeFeet) &&
               IsFinite(sample.GroundSpeedKnots) &&
               IsFinite(sample.IndicatedAirspeedKnots) &&
               IsFinite(sample.VelocityWorldYFps);
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Where(IsFinite).OrderBy(value => value).ToArray();
        if (values.Length == 0)
        {
            return double.NaN;
        }

        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2.0
            : values[middle];
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
