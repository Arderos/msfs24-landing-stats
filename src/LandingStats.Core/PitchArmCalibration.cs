using System;
using System.Collections.Generic;
using System.Linq;
using static LandingStats.Core.GeometryCalibrationPolicy;
using static LandingStats.Core.GeometryCalibrationMath;
using static LandingStats.Core.GeometryContactEvidence;

namespace LandingStats.Core;

// Pure component extracted without changing the established estimator equations.
internal static class PitchArmCalibration
{
    internal static bool TryRecoverPitchArm(
        IReadOnlyList<TelemetrySample> samples,
        IReadOnlyList<double> times,
        IReadOnlyList<double> planeAltitude,
        IReadOnlyList<double> groundAltitude,
        TelemetryGearTopology topology,
        out double pitchArm,
        out int mainOnlySampleCount,
        out double designIndependence,
        bool requireStableArm = false)
    {
        pitchArm = double.NaN;
        mainOnlySampleCount = 0;
        designIndependence = 0.0;
        var pitch = new List<double>();
        var compression = new List<double>();
        var height = new List<double>();
        var observationTimes = requireStableArm ? new List<double>() : null;

        var segmentStart = -1;
        for (var index = 0; index <= samples.Count; index++)
        {
            var isMainOnly = index < samples.Count && IsMainOnly(samples[index], topology);
            if (isMainOnly && segmentStart < 0)
            {
                segmentStart = index;
                continue;
            }

            if (isMainOnly || segmentStart < 0)
            {
                continue;
            }

            var segmentEnd = index - 1;
            if (times[segmentEnd] - times[segmentStart] >= MinimumMainOnlyDurationSeconds)
            {
                for (var take = segmentStart; take <= segmentEnd; take++)
                {
                    var sample = samples[take];
                    var pitchRadians = DegreesToRadians(sample.PitchDegrees);
                    var meanCompression = MeanCompression(sample, topology.MainContactPointIndices);
                    var datumHeight = planeAltitude[take] - groundAltitude[take];
                    if (!IsFinite(pitchRadians) ||
                        !IsFinite(meanCompression) ||
                        !IsFinite(datumHeight))
                    {
                        continue;
                    }

                    pitch.Add(pitchRadians);
                    compression.Add(meanCompression);
                    height.Add(datumHeight);
                    observationTimes?.Add(times[take]);
                }
            }

            segmentStart = -1;
        }

        mainOnlySampleCount = pitch.Count;
        if (pitch.Count < MinimumPitchRegressionRows ||
            !TryFitTwoPredictors(
                pitch,
                compression,
                height,
                out pitchArm,
                out _,
                out designIndependence))
        {
            return false;
        }

        return IsFinite(pitchArm) &&
            Math.Abs(pitchArm) <= MaximumAbsoluteArmFeet &&
            designIndependence >= MinimumDesignIndependence &&
            (!requireStableArm || HasStablePitchArm(observationTimes!, pitch, compression, height, pitchArm));
    }

    internal static bool HasStablePitchArm(
        List<double> times,
        List<double> pitch,
        List<double> compression,
        List<double> height,
        double fullPitchArm)
    {
        if (times.Count < MinimumStablePitchRegressionRows)
        {
            return false;
        }

        var duration = times[times.Count - 1] - times[0];
        if (!IsFinite(duration) || duration <= 0.0)
        {
            return false;
        }

        // First/last 70% of elapsed time, then leave each of five contiguous
        // time blocks out. Random frame splits would hide slow geometry drift.
        // Datum offset is unchanged, so pitch-arm differences equal final-arm
        // differences. Neither the simulator latch nor its residual is used.
        for (var subset = 0; subset < ArmStabilityBlockCount + 2; subset++)
        {
            var selectedPitch = new List<double>();
            var selectedCompression = new List<double>();
            var selectedHeight = new List<double>();
            for (var index = 0; index < times.Count; index++)
            {
                var fraction = (times[index] - times[0]) / duration;
                var block = Math.Min(ArmStabilityBlockCount - 1, (int)(fraction * ArmStabilityBlockCount));
                var include = subset == 0 ? fraction <= 1.0 - ArmStabilityTrimFraction
                    : subset == 1 ? fraction >= ArmStabilityTrimFraction
                    : block != subset - 2;
                if (!include) continue;
                selectedPitch.Add(pitch[index]);
                selectedCompression.Add(compression[index]);
                selectedHeight.Add(height[index]);
            }

            if (selectedPitch.Count < MinimumPitchRegressionRows ||
                selectedPitch.Count == times.Count ||
                !TryFitTwoPredictors(selectedPitch, selectedCompression, selectedHeight,
                    out var subsetArm, out _, out _) ||
                Math.Abs(subsetArm) > MaximumAbsoluteArmFeet ||
                Math.Abs(subsetArm - fullPitchArm) > MaximumPitchArmRefitDeviationFeet)
            {
                return false;
            }
        }

        return true;
    }
}
