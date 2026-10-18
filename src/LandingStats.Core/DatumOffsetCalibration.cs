using System;
using System.Collections.Generic;
using System.Linq;
using static LandingStats.Core.GeometryCalibrationPolicy;
using static LandingStats.Core.GeometryCalibrationMath;

namespace LandingStats.Core;

// Pure component extracted without changing the established estimator equations.
internal static class DatumOffsetCalibration
{
    internal static bool TryRecoverDatumOffsetCore(
        IReadOnlyList<TelemetrySample> samples,
        IReadOnlyList<double> times,
        IReadOnlyList<double> planeAltitude,
        out double datumOffset,
        out double datumStandardDeviation,
        out int datumPhaseCount,
        out int datumWindowCount,
        bool useAttitudeDifference = false,
        TelemetryDatumCalibrationVersion datumVersion = TelemetryDatumCalibrationVersion.BankedRotation)
    {
        datumOffset = double.NaN;
        datumStandardDeviation = double.NaN;
        datumPhaseCount = 0;
        datumWindowCount = 0;

        var rigidVerticalPerFoot = new double[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var pitch = DegreesToRadians(sample.PitchDegrees);
            if (useAttitudeDifference)
            {
                // Only finiteness is used by IsValidAirborneWindow on this
                // path; the actual displacement is evaluated at the endpoints.
                rigidVerticalPerFoot[index] = IsFinite(pitch) ? 0.0 : double.NaN;
                continue;
            }
            var bank = DegreesToRadians(sample.BankDegrees);
            var omegaX = sample.RotationVelocityBodyXRadiansPerSecond;
            var omegaY = sample.RotationVelocityBodyYRadiansPerSecond;
            if (!IsFinite(pitch) || !IsFinite(bank) || !IsFinite(omegaX) || !IsFinite(omegaY))
            {
                rigidVerticalPerFoot[index] = double.NaN;
                continue;
            }

            rigidVerticalPerFoot[index] = datumVersion == TelemetryDatumCalibrationVersion.Legacy
                ? WorldVerticalRigidBodyProjection.LegacyAtLongitudinalOffsetFps(omegaX, omegaY, pitch, bank, 1.0)
                : WorldVerticalRigidBodyProjection.AtLongitudinalOffsetFps(omegaX, omegaY, pitch, bank, 1.0);
        }

        var phaseEstimates = new List<double>(4);
        for (var phase = 0; phase < 4; phase++)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            for (var start = phase; start < samples.Count - 2; start += 4)
            {
                var end = LowerBound(times, times[start] + IntegrationTargetSeconds);
                if (end >= samples.Count)
                {
                    continue;
                }

                var actualDuration = times[end] - times[start];
                if (actualDuration > MaximumIntegrationDurationSeconds ||
                    !IsValidAirborneWindow(samples, times, planeAltitude, rigidVerticalPerFoot, start, end))
                {
                    continue;
                }

                var integratedVelocity = 0.0;
                var integratedRigidVertical = 0.0;
                for (var index = start; index < end; index++)
                {
                    var duration = times[index + 1] - times[index];
                    integratedVelocity += 0.5 * duration *
                        (samples[index].VelocityWorldYFps + samples[index + 1].VelocityWorldYFps);
                    integratedRigidVertical += 0.5 * duration *
                        (rigidVerticalPerFoot[index] + rigidVerticalPerFoot[index + 1]);
                }
                if (useAttitudeDifference)
                    integratedRigidVertical = Math.Sin(DegreesToRadians(samples[start].PitchDegrees)) -
                        Math.Sin(DegreesToRadians(samples[end].PitchDegrees));

                var altitudeResidual =
                    planeAltitude[end] - planeAltitude[start] - integratedVelocity;
                var x = integratedRigidVertical / actualDuration;
                var y = altitudeResidual / actualDuration;
                if (IsFinite(x) && IsFinite(y))
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }

            if (xs.Count < 3 || Range(xs) <= 1e-8 ||
                !TryFitLine(xs, ys, out var phaseEstimate) ||
                !IsFinite(phaseEstimate) ||
                Math.Abs(phaseEstimate) > MaximumAbsoluteArmFeet)
            {
                continue;
            }

            phaseEstimates.Add(phaseEstimate);
            datumWindowCount += xs.Count;
        }

        datumPhaseCount = phaseEstimates.Count;
        if (datumPhaseCount < MinimumDatumPhases)
        {
            return false;
        }

        datumOffset = Mean(phaseEstimates);
        var variance = 0.0;
        for (var index = 0; index < phaseEstimates.Count; index++)
        {
            variance += Square(phaseEstimates[index] - datumOffset);
        }

        datumStandardDeviation = Math.Sqrt(variance / phaseEstimates.Count);
        return IsFinite(datumOffset) &&
            Math.Abs(datumOffset) <= MaximumAbsoluteArmFeet &&
            IsFinite(datumStandardDeviation) &&
            datumStandardDeviation <= MaximumDatumStandardDeviationFeet;
    }

    internal static bool IsValidAirborneWindow(
        IReadOnlyList<TelemetrySample> samples,
        IReadOnlyList<double> times,
        IReadOnlyList<double> planeAltitude,
        IReadOnlyList<double> rigidVerticalPerFoot,
        int start,
        int end)
    {
        if (!IsFinite(planeAltitude[start]) || !IsFinite(planeAltitude[end]))
        {
            return false;
        }

        for (var index = start; index <= end; index++)
        {
            if (!samples[index].MotionSimulation || samples[index].OnGround ||
                !IsFinite(samples[index].VelocityWorldYFps) ||
                !IsFinite(rigidVerticalPerFoot[index]))
            {
                return false;
            }

            if (index < end)
            {
                var duration = times[index + 1] - times[index];
                if (duration <= 0.0 || duration > MaximumFrameGapSeconds)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
