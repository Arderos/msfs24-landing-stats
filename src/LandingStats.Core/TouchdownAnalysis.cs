using System;
using System.Collections.Generic;

namespace LandingStats.Core;

public static class TouchdownAnalysis
{
    private sealed class ContactCandidate
    {
        public int ContactIndex { get; set; }

        public int PreviousAirborneIndex { get; set; }

        public int EpisodeNumber { get; set; }

        public int ContactNumber { get; set; }
    }

    private const double EpisodeGapSeconds = 10.0;
    private const double GWindowAfterSeconds = 2.0;
    private const double LatchUpdateWindowSeconds = 2.0;
    private const double InertialFitWindowSeconds = 0.2;
    private const double MinimumAirborneFitSeconds = 0.15;
    private const double GroundFitWindowSeconds = 0.2;
    private const double GroundOutlierThresholdFeet = 5.0;

    public static IReadOnlyList<TouchdownResult> Analyze(IReadOnlyList<TelemetrySample> samples) =>
        Analyze(samples, null);

    public static IReadOnlyList<TouchdownResult> Analyze(
        IReadOnlyList<TelemetrySample> samples,
        TouchdownAnalysisOptions? options)
    {
        var candidates = new List<ContactCandidate>();
        var hasBeenAirborne = false;
        var previousOnGround = samples.Count > 0 && samples[0].OnGround;
        var previousAirborneIndex = -1;
        var previousContactTime = double.NegativeInfinity;
        var episodeNumber = 0;
        var contactNumber = 0;

        for (var index = 0; index < samples.Count; index++)
        {
            var current = samples[index];
            if (!current.OnGround)
            {
                hasBeenAirborne = true;
                previousAirborneIndex = index;
            }

            var isContact = hasBeenAirborne && current.OnGround && !previousOnGround && previousAirborneIndex >= 0;
            if (isContact)
            {
                var contactTime = TimeOf(current);
                if (contactTime - previousContactTime > EpisodeGapSeconds)
                {
                    episodeNumber++;
                    contactNumber = 1;
                }
                else
                {
                    contactNumber++;
                }

                candidates.Add(new ContactCandidate
                {
                    ContactIndex = index,
                    PreviousAirborneIndex = previousAirborneIndex,
                    EpisodeNumber = episodeNumber,
                    ContactNumber = contactNumber,
                });
                previousContactTime = contactTime;
            }

            previousOnGround = current.OnGround;
        }

        var longitudinalArmFeet = double.NaN;
        var geometryQuality = double.NaN;
        var armRecoveredFromTelemetry = false;
        TelemetryGearTopology? gearTopology = null;
        if (candidates.Count > 0 &&
            TelemetryGeometryCalibration.TryDetectConventionalTopology(samples, out var detectedTopology))
        {
            gearTopology = detectedTopology;
        }

        if (gearTopology != null &&
            options?.LongitudinalMainGearArmFeet is double passportArm &&
            IsFinite(passportArm))
        {
            longitudinalArmFeet = passportArm;
        }
        else if (gearTopology != null &&
                 (options == null || options.RecoverLongitudinalMainGearArmFromTelemetry) &&
                 TelemetryGeometryCalibration.TryCalibrate(samples, out var calibration))
        {
            longitudinalArmFeet = calibration.LongitudinalArmFeet;
            geometryQuality = calibration.Quality;
            armRecoveredFromTelemetry = true;
        }

        var results = new List<TouchdownResult>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var exclusiveEndTime = double.PositiveInfinity;
            if (index + 1 < candidates.Count && candidates[index + 1].EpisodeNumber == candidate.EpisodeNumber)
            {
                exclusiveEndTime = TimeOf(samples[candidates[index + 1].ContactIndex]);
            }

            results.Add(CreateResult(
                samples,
                candidate.ContactIndex,
                candidate.PreviousAirborneIndex,
                candidate.EpisodeNumber,
                candidate.ContactNumber,
                exclusiveEndTime,
                longitudinalArmFeet,
                geometryQuality,
                armRecoveredFromTelemetry,
                gearTopology));
        }

        return results;
    }

    private static TouchdownResult CreateResult(
        IReadOnlyList<TelemetrySample> samples,
        int contactIndex,
        int previousAirborneIndex,
        int episodeNumber,
        int contactNumber,
        double exclusiveEndTime,
        double longitudinalArmFeet,
        double geometryQuality,
        bool armRecoveredFromTelemetry,
        TelemetryGearTopology? gearTopology)
    {
        var contact = samples[contactIndex];
        var lastAirborne = samples[previousAirborneIndex];
        var contactTime = TimeOf(contact);
        var estimatedContactTime = EstimateContactTime(
            samples,
            contactIndex,
            previousAirborneIndex,
            out var contactTimeEstimatedFromCompression,
            out var contactTimeEstimatePointCount,
            out var contactTimeEstimateSpreadSeconds);
        var inertialVerticalFpm = EstimateInertialVerticalFpm(
            samples,
            previousAirborneIndex,
            estimatedContactTime,
            contactTimeEstimatedFromCompression,
            out var inertialVerticalExtrapolated,
            out var inertialFitDurationSeconds);
        var latchedVelocity = FindUpdatedLatch(
            samples,
            contactIndex,
            lastAirborne.TouchdownNormalVelocityFps,
            exclusiveEndTime,
            out var latchTime,
            out var latchUpdateDetected);
        var latchedNormalFpm = latchUpdateDetected ? ToNormalDescentFpm(latchedVelocity) : double.NaN;
        var surfaceRelativeDeltaFpm = latchUpdateDetected
            ? latchedNormalFpm - inertialVerticalFpm
            : double.NaN;
        var terrainContributionFpm = EstimateTerrainContributionFpm(samples, previousAirborneIndex);
        var peakG = FindPeakG(samples, contactIndex, GWindowAfterSeconds, exclusiveEndTime, out var peakGTime);
        var reconstruction = new TouchdownClosureEstimate();
        var reconstructed = gearTopology != null &&
                            TelemetryGeometryCalibration.IsSustainedMainContact(
                                samples,
                                contactIndex,
                                previousAirborneIndex,
                                gearTopology) &&
                            IsFinite(longitudinalArmFeet) &&
                            TouchdownClosureReconstruction.TryEstimate(
                                samples,
                                previousAirborneIndex,
                                estimatedContactTime,
                                longitudinalArmFeet,
                                out reconstruction);

        return new TouchdownResult
        {
            ClosureReconstructionModel = TouchdownClosureReconstruction.ModelName,
            EpisodeNumber = episodeNumber,
            ContactNumber = contactNumber,
            Sequence = contact.Sequence,
            SimulationTimeSeconds = contact.SimulationTimeSeconds,
            EstimatedContactTimeSeconds = estimatedContactTime,
            EstimatedContactOffsetSeconds = estimatedContactTime - contactTime,
            ContactTimeEstimatedFromCompression = contactTimeEstimatedFromCompression,
            ContactTimeEstimatePointCount = contactTimeEstimatePointCount,
            ContactTimeEstimateSpreadSeconds = contactTimeEstimateSpreadSeconds,
            InertialVerticalFpm = inertialVerticalFpm,
            InertialVerticalExtrapolated = inertialVerticalExtrapolated,
            InertialFitDurationSeconds = inertialFitDurationSeconds,
            LatchUpdateDetected = latchUpdateDetected,
            LatchedNormalFpm = latchedNormalFpm,
            LatchedUpdateOffsetSeconds = latchUpdateDetected ? latchTime - contactTime : double.NaN,
            SurfaceRelativeDeltaFpm = surfaceRelativeDeltaFpm,
            TerrainContributionFpm = terrainContributionFpm,
            UnresolvedSurfaceDeltaFpm = double.IsNaN(terrainContributionFpm)
                ? double.NaN
                : surfaceRelativeDeltaFpm - terrainContributionFpm,
            ClosureReconstructionAvailable = reconstructed,
            ReconstructedClosureFpm = reconstructed ? reconstruction.ClosureFpm : double.NaN,
            ReconstructedInertialFpm = reconstructed ? reconstruction.InertialFpm : double.NaN,
            ReconstructedTerrainFpm = reconstructed ? reconstruction.TerrainFpm : double.NaN,
            ReconstructedPitchFpm = reconstructed ? reconstruction.PitchFpm : double.NaN,
            ClosureReconstructionResidualFpm = reconstructed && latchUpdateDetected
                ? latchedNormalFpm - reconstruction.ClosureFpm
                : double.NaN,
            ClosureReconstructionUncertaintyFpm = reconstructed
                ? contactTimeEstimatedFromCompression &&
                  contactNumber == 1 &&
                  double.IsPositiveInfinity(exclusiveEndTime) &&
                  gearTopology!.MainContactPointCount == 2
                    ? TouchdownClosureReconstruction.PrimaryUncertaintyFpm
                    : TouchdownClosureReconstruction.FallbackUncertaintyFpm
                : double.NaN,
            ClosureReconstructionFitPointCount = reconstructed ? reconstruction.FitPointCount : 0,
            ClosureReconstructionLongitudinalArmFeet = reconstructed ? longitudinalArmFeet : double.NaN,
            ClosureReconstructionGeometryQuality = reconstructed ? geometryQuality : double.NaN,
            ClosureReconstructionArmRecoveredFromTelemetry = reconstructed && armRecoveredFromTelemetry,
            LastAirToFirstGroundSeconds = contactTime - TimeOf(lastAirborne),
            FirstGroundToNextFrameSeconds = FindNextFrameDuration(samples, contactIndex),
            LastAirborneIndicatedFpm = ToDescentFpm(lastAirborne.VerticalSpeedFps),
            FirstGroundIndicatedFpm = ToDescentFpm(contact.VerticalSpeedFps),
            LastAirborneWorldFpm = ToDescentFpm(lastAirborne.VelocityWorldYFps),
            FirstGroundWorldFpm = ToDescentFpm(contact.VelocityWorldYFps),
            AglAverage100MsFpm = CalculateAglRate(samples, previousAirborneIndex, contactTime, 0.1),
            AglAverage150MsFpm = CalculateAglRate(samples, previousAirborneIndex, contactTime, 0.15),
            GAtFirstGroundSample = contact.GForce,
            PeakG = peakG,
            PeakGOffsetSeconds = peakGTime - contactTime,
            PeakG50Milliseconds = FindPeakG(samples, contactIndex, 0.05, exclusiveEndTime, out _),
            PeakG100Milliseconds = FindPeakG(samples, contactIndex, 0.1, exclusiveEndTime, out _),
            PeakG150Milliseconds = FindPeakG(samples, contactIndex, 0.15, exclusiveEndTime, out _),
            PeakG250Milliseconds = FindPeakG(samples, contactIndex, 0.25, exclusiveEndTime, out _),
            PeakG500Milliseconds = FindPeakG(samples, contactIndex, 0.5, exclusiveEndTime, out _),
            PitchDegrees = contact.PitchDegrees,
            BankDegrees = contact.BankDegrees,
            IndicatedAirspeedKnots = contact.IndicatedAirspeedKnots,
            GroundSpeedKnots = contact.GroundSpeedKnots,
        };
    }

    private static double EstimateContactTime(
        IReadOnlyList<TelemetrySample> samples,
        int contactIndex,
        int previousAirborneIndex,
        out bool estimatedFromCompression,
        out int estimatePointCount,
        out double estimateSpreadSeconds)
    {
        var firstGround = samples[contactIndex];
        var firstGroundTime = TimeOf(firstGround);
        var lastAirborneTime = TimeOf(samples[previousAirborneIndex]);
        var estimates = new List<double>();

        for (var point = 0; point < TelemetrySample.CapturedContactPointCount; point++)
        {
            if (!firstGround.ContactPointOnGround[point] || firstGround.ContactPointCompression[point] <= 0)
            {
                continue;
            }

            for (var index = contactIndex + 1; index < samples.Count; index++)
            {
                var next = samples[index];
                var nextTime = TimeOf(next);
                var duration = nextTime - firstGroundTime;
                if (duration <= 0)
                {
                    continue;
                }

                if (duration > 0.1 || !next.ContactPointOnGround[point])
                {
                    break;
                }

                var compressionRate =
                    (next.ContactPointCompression[point] - firstGround.ContactPointCompression[point]) / duration;
                if (compressionRate > 0.000001)
                {
                    var estimate = firstGroundTime - firstGround.ContactPointCompression[point] / compressionRate;
                    if (estimate >= lastAirborneTime && estimate <= firstGroundTime)
                    {
                        estimates.Add(estimate);
                    }
                }

                break;
            }
        }

        if (estimates.Count == 0)
        {
            estimatedFromCompression = false;
            estimatePointCount = 0;
            estimateSpreadSeconds = double.NaN;
            return lastAirborneTime;
        }

        estimates.Sort();
        estimatedFromCompression = true;
        estimatePointCount = estimates.Count;
        estimateSpreadSeconds = estimates[estimates.Count - 1] - estimates[0];
        var middle = estimates.Count / 2;
        return estimates.Count % 2 == 0
            ? (estimates[middle - 1] + estimates[middle]) / 2.0
            : estimates[middle];
    }

    private static double EstimateInertialVerticalFpm(
        IReadOnlyList<TelemetrySample> samples,
        int lastAirborneIndex,
        double estimatedContactTime,
        bool extrapolateToContact,
        out bool extrapolated,
        out double fitDurationSeconds)
    {
        var last = samples[lastAirborneIndex];
        extrapolated = false;
        fitDurationSeconds = 0;
        if (!extrapolateToContact)
        {
            return ToDescentFpm(last.VelocityWorldYFps);
        }

        var continuousAirStartIndex = lastAirborneIndex;
        for (var index = lastAirborneIndex - 1; index >= 0; index--)
        {
            if (samples[index].OnGround)
            {
                break;
            }

            continuousAirStartIndex = index;
        }

        var continuousAirDuration = TimeOf(last) - TimeOf(samples[continuousAirStartIndex]);
        if (continuousAirDuration < MinimumAirborneFitSeconds)
        {
            return ToDescentFpm(last.VelocityWorldYFps);
        }

        var fitStartTime = TimeOf(last) - InertialFitWindowSeconds;
        var fitStartIndex = lastAirborneIndex;
        for (var index = lastAirborneIndex - 1; index >= continuousAirStartIndex; index--)
        {
            if (TimeOf(samples[index]) < fitStartTime)
            {
                break;
            }

            fitStartIndex = index;
        }

        if (!TryLinearFit(
                samples,
                fitStartIndex,
                lastAirborneIndex,
                sample => sample.VelocityWorldYFps,
                out var interceptAtLast,
                out var slope,
                out fitDurationSeconds))
        {
            return ToDescentFpm(last.VelocityWorldYFps);
        }

        var estimatedVelocity = interceptAtLast + slope * (estimatedContactTime - TimeOf(last));
        extrapolated = true;
        return ToDescentFpm(estimatedVelocity);
    }

    private static double EstimateTerrainContributionFpm(
        IReadOnlyList<TelemetrySample> samples,
        int lastAirborneIndex)
    {
        var last = samples[lastAirborneIndex];
        if (Math.Abs(last.PlaneAltitudeFeet) < 0.000001 && Math.Abs(last.GroundAltitudeFeet) < 0.000001)
        {
            return double.NaN;
        }

        var fitStartTime = TimeOf(last) - GroundFitWindowSeconds;
        var fitStartIndex = lastAirborneIndex;
        for (var index = lastAirborneIndex - 1; index >= 0; index--)
        {
            if (samples[index].OnGround || TimeOf(samples[index]) < fitStartTime)
            {
                break;
            }

            fitStartIndex = index;
        }

        if (!TryLinearFit(
                samples,
                fitStartIndex,
                lastAirborneIndex,
                sample => FilteredGroundAltitude(samples, sample),
                out _,
                out var slope,
                out _))
        {
            return double.NaN;
        }

        return slope * 60.0;
    }

    private static double FilteredGroundAltitude(
        IReadOnlyList<TelemetrySample> samples,
        TelemetrySample target)
    {
        var targetIndex = -1;
        for (var index = 0; index < samples.Count; index++)
        {
            if (ReferenceEquals(samples[index], target))
            {
                targetIndex = index;
                break;
            }
        }

        if (targetIndex < 0)
        {
            return target.GroundAltitudeFeet;
        }

        var values = new List<double>(5);
        for (var index = Math.Max(0, targetIndex - 2); index <= Math.Min(samples.Count - 1, targetIndex + 2); index++)
        {
            values.Add(samples[index].GroundAltitudeFeet);
        }

        values.Sort();
        var median = values[values.Count / 2];
        if (Math.Abs(target.GroundAltitudeFeet - median) <= GroundOutlierThresholdFeet ||
            targetIndex == 0 || targetIndex == samples.Count - 1)
        {
            return target.GroundAltitudeFeet;
        }

        var previous = samples[targetIndex - 1];
        var next = samples[targetIndex + 1];
        var duration = TimeOf(next) - TimeOf(previous);
        if (duration <= 0.000001)
        {
            return median;
        }

        var fraction = (TimeOf(target) - TimeOf(previous)) / duration;
        return previous.GroundAltitudeFeet +
               (next.GroundAltitudeFeet - previous.GroundAltitudeFeet) * fraction;
    }

    private static bool TryLinearFit(
        IReadOnlyList<TelemetrySample> samples,
        int startIndex,
        int endIndex,
        Func<TelemetrySample, double> selectValue,
        out double interceptAtEnd,
        out double slope,
        out double durationSeconds)
    {
        var endTime = TimeOf(samples[endIndex]);
        var count = 0;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumXX = 0.0;
        var sumXY = 0.0;
        var earliestTime = endTime;

        for (var index = startIndex; index <= endIndex; index++)
        {
            var x = TimeOf(samples[index]) - endTime;
            var y = selectValue(samples[index]);
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
            earliestTime = Math.Min(earliestTime, TimeOf(samples[index]));
            count++;
        }

        durationSeconds = endTime - earliestTime;
        var denominator = count * sumXX - sumX * sumX;
        if (count < 3 || Math.Abs(denominator) <= 0.000000000001)
        {
            interceptAtEnd = double.NaN;
            slope = double.NaN;
            return false;
        }

        slope = (count * sumXY - sumX * sumY) / denominator;
        interceptAtEnd = (sumY - slope * sumX) / count;
        return true;
    }

    private static double FindNextFrameDuration(IReadOnlyList<TelemetrySample> samples, int contactIndex)
    {
        var contactTime = TimeOf(samples[contactIndex]);
        for (var index = contactIndex + 1; index < samples.Count; index++)
        {
            var duration = TimeOf(samples[index]) - contactTime;
            if (duration > 0.000001)
            {
                return duration;
            }
        }

        return double.NaN;
    }

    private static double FindUpdatedLatch(
        IReadOnlyList<TelemetrySample> samples,
        int contactIndex,
        double previousValue,
        double exclusiveEndTime,
        out double latchTime,
        out bool updateDetected)
    {
        var contactTime = TimeOf(samples[contactIndex]);
        updateDetected = false;

        for (var index = contactIndex; index < samples.Count; index++)
        {
            var sample = samples[index];
            var sampleTime = TimeOf(sample);
            if (sampleTime >= exclusiveEndTime || sampleTime - contactTime > LatchUpdateWindowSeconds)
            {
                break;
            }

            if (Math.Abs(sample.TouchdownNormalVelocityFps - previousValue) > 0.000001)
            {
                latchTime = sampleTime;
                updateDetected = true;
                return sample.TouchdownNormalVelocityFps;
            }
        }

        latchTime = double.NaN;
        return double.NaN;
    }

    private static double FindPeakG(
        IReadOnlyList<TelemetrySample> samples,
        int contactIndex,
        double windowAfterSeconds,
        double exclusiveEndTime,
        out double peakGTime)
    {
        var contactTime = TimeOf(samples[contactIndex]);
        var peakG = double.NegativeInfinity;
        peakGTime = contactTime;

        for (var index = contactIndex; index < samples.Count; index++)
        {
            var sample = samples[index];
            var sampleTime = TimeOf(sample);
            if (sampleTime >= exclusiveEndTime || sampleTime - contactTime > windowAfterSeconds)
            {
                break;
            }

            UpdatePeak(sample, ref peakG, ref peakGTime);
        }

        return double.IsNegativeInfinity(peakG) ? samples[contactIndex].GForce : peakG;
    }

    private static double CalculateAglRate(
        IReadOnlyList<TelemetrySample> samples,
        int lastAirborneIndex,
        double contactTime,
        double windowSeconds)
    {
        var targetTime = contactTime - windowSeconds;
        var startIndex = lastAirborneIndex;
        var bestDistance = Math.Abs(TimeOf(samples[startIndex]) - targetTime);

        for (var index = lastAirborneIndex - 1; index >= 0; index--)
        {
            var sample = samples[index];
            if (sample.OnGround || contactTime - TimeOf(sample) > 0.25)
            {
                break;
            }

            var distance = Math.Abs(TimeOf(sample) - targetTime);
            if (distance < bestDistance)
            {
                startIndex = index;
                bestDistance = distance;
            }
        }

        var start = samples[startIndex];
        var end = samples[lastAirborneIndex];
        var duration = TimeOf(end) - TimeOf(start);
        if (duration <= 0.000001)
        {
            return double.NaN;
        }

        return (start.AboveGroundLevelFeet - end.AboveGroundLevelFeet) / duration * 60.0;
    }

    private static void UpdatePeak(TelemetrySample sample, ref double peakG, ref double peakGTime)
    {
        if (sample.GForce > peakG)
        {
            peakG = sample.GForce;
            peakGTime = TimeOf(sample);
        }
    }

    private static double ToDescentFpm(double velocityFeetPerSecond)
    {
        return -60.0 * velocityFeetPerSecond;
    }

    private static double ToNormalDescentFpm(double velocityFeetPerSecond)
    {
        return Math.Abs(60.0 * velocityFeetPerSecond);
    }

    internal static double TimeOf(TelemetrySample sample)
    {
        return double.IsNaN(sample.SimulationTimeSeconds) || double.IsInfinity(sample.SimulationTimeSeconds)
            ? sample.HostElapsedSeconds
            : sample.SimulationTimeSeconds;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
