using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingStats.Core;

/// <summary>
/// Geometry recovered from the aircraft's own telemetry, without a flight-model
/// configuration file or a touchdown-velocity target.
/// </summary>
public sealed class TelemetryGeometryCalibrationResult
{
    internal TelemetryGeometryCalibrationResult(
        double longitudinalArmFeet,
        double pitchArmFeet,
        double datumOffsetFeet,
        double datumOffsetStandardDeviationFeet,
        int mainOnlySampleCount,
        int datumPhaseCount,
        int datumWindowCount,
        double quality)
    {
        LongitudinalArmFeet = longitudinalArmFeet;
        PitchArmFeet = pitchArmFeet;
        DatumOffsetFeet = datumOffsetFeet;
        DatumOffsetStandardDeviationFeet = datumOffsetStandardDeviationFeet;
        MainOnlySampleCount = mainOnlySampleCount;
        DatumPhaseCount = datumPhaseCount;
        DatumWindowCount = datumWindowCount;
        Quality = quality;
    }

    /// <summary>
    /// Signed longitudinal arm from the velocity reference point to the main-gear axis.
    /// </summary>
    public double LongitudinalArmFeet { get; }

    /// <summary>
    /// Pitch coefficient from the main-gear height regression, in feet per radian.
    /// </summary>
    public double PitchArmFeet { get; }

    /// <summary>
    /// Signed longitudinal offset between the altitude datum and velocity reference point.
    /// </summary>
    public double DatumOffsetFeet { get; }

    /// <summary>
    /// Population standard deviation across four interleaved start-phase estimates.
    /// The estimates use overlapping windows, so this measures numerical agreement,
    /// not an independent statistical sigma.
    /// </summary>
    public double DatumOffsetStandardDeviationFeet { get; }

    public int MainOnlySampleCount { get; }

    public int DatumPhaseCount { get; }

    public int DatumWindowCount { get; }

    /// <summary>
    /// A 0..1 identifiability score, not a statistical probability. It combines
    /// pitch/compression separation, four-phase coverage, and agreement across
    /// interleaved datum-phase estimates.
    /// </summary>
    public double Quality { get; }
}

internal sealed class TelemetryGearTopology
{
    public TelemetryGearTopology(int[] mainContactPointIndices, int[] noseContactPointIndices)
    {
        MainContactPointIndices = mainContactPointIndices;
        NoseContactPointIndices = noseContactPointIndices;
    }

    public int[] MainContactPointIndices { get; }

    public int MainContactPointCount => MainContactPointIndices.Length;

    public int[] NoseContactPointIndices { get; }
}

/// <summary>
/// Recovers the effective longitudinal main-gear arm using only recorded telemetry.
/// </summary>
public static class TelemetryGeometryCalibration
{
    private const double MinimumMainOnlyDurationSeconds = 0.20;
    private const double MinimumSustainedContactDurationSeconds = 0.04;
    private const double MinimumDerotationPitchIncreaseDegrees = 0.50;
    private const double MinimumCompressionEvidence = 0.001;
    private const double IntegrationTargetSeconds = 1.0;
    private const double MinimumIntegrationDurationSeconds = 0.70;
    private const double MaximumIntegrationDurationSeconds = 1.20;
    private const double MaximumFrameGapSeconds = 0.15;
    private const double MaximumAbsoluteArmFeet = 500.0;
    private const double MaximumDatumStandardDeviationFeet = 5.0;
    private const double MinimumDesignIndependence = 0.001;
    private const double MinimumAcceptedQuality = 0.20;
    private const int MinimumPitchRegressionRows = 6;
    private const int MinimumDatumPhases = 3;
    private const int MinimumSustainedContactSamples = 2;

    private sealed class ContactRun
    {
        public int ContactPointIndex { get; set; }

        public double StartTime { get; set; }

        public double EndTime { get; set; }
    }

    /// <summary>
    /// Attempts a target-independent calibration. Contact-point indices have no semantic
    /// meaning here: three or four settled points, a distinct last point, and positive
    /// pitch derotation before that last point are the evidence for supported topology.
    /// </summary>
    public static bool TryCalibrate(
        IReadOnlyList<TelemetrySample> samples,
        out TelemetryGeometryCalibrationResult result)
    {
        result = null!;
        if (samples is null || samples.Count < MinimumPitchRegressionRows)
        {
            return false;
        }

        // CSV schema 5 can contain repeated simulation times. The research pipeline
        // keeps the last row at a time and sorts by simulation time; do the same here.
        var ordered = Normalize(samples);
        if (ordered.Count < MinimumPitchRegressionRows)
        {
            return false;
        }

        if (!TryDetectConventionalTopology(ordered, out var topology))
        {
            return false;
        }

        var times = new double[ordered.Count];
        var planeAltitude = new double[ordered.Count];
        var groundAltitude = new double[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
        {
            times[index] = ordered[index].SimulationTimeSeconds;
            planeAltitude[index] = ordered[index].PlaneAltitudeFeet;
            groundAltitude[index] = ordered[index].GroundAltitudeFeet;
        }

        SanitizeIsolatedSpikes(planeAltitude);
        SanitizeIsolatedSpikes(groundAltitude);

        if (!TryRecoverPitchArm(
                ordered,
                times,
                planeAltitude,
                groundAltitude,
                topology,
                out var pitchArm,
                out var mainOnlySampleCount,
                out var designIndependence))
        {
            return false;
        }

        if (!TryRecoverDatumOffset(
                ordered,
                times,
                planeAltitude,
                out var datumOffset,
                out var datumStandardDeviation,
                out var datumPhaseCount,
                out var datumWindowCount))
        {
            return false;
        }

        var longitudinalArm = pitchArm + datumOffset;
        if (!IsFinite(longitudinalArm) || Math.Abs(longitudinalArm) > MaximumAbsoluteArmFeet)
        {
            return false;
        }

        // 0.05 is not a fitted aircraft constant. It merely marks a comfortably
        // non-collinear two-predictor design; values below it are degraded smoothly.
        var designQuality = Math.Sqrt(Clamp01(designIndependence / 0.05));
        var phaseCoverage = datumPhaseCount / 4.0;
        var interleavedPhaseAgreement = 1.0 /
            (1.0 + Square(datumStandardDeviation / 0.5));
        var quality = Clamp01(designQuality * phaseCoverage * interleavedPhaseAgreement);
        if (quality < MinimumAcceptedQuality)
        {
            return false;
        }

        result = new TelemetryGeometryCalibrationResult(
            longitudinalArm,
            pitchArm,
            datumOffset,
            datumStandardDeviation,
            mainOnlySampleCount,
            datumPhaseCount,
            datumWindowCount,
            quality);
        return true;
    }

    internal static bool TryDetectConventionalTopology(
        IReadOnlyList<TelemetrySample> samples,
        out TelemetryGearTopology topology)
    {
        topology = null!;
        if (samples is null)
        {
            return false;
        }

        return TryDetectConventionalTopology(Normalize(samples), out topology);
    }

    internal static bool IsSustainedMainContact(
        IReadOnlyList<TelemetrySample> samples,
        int contactIndex,
        int previousAirborneIndex,
        TelemetryGearTopology topology)
    {
        if (contactIndex < 0 || contactIndex >= samples.Count ||
            previousAirborneIndex < 0 || previousAirborneIndex >= contactIndex)
        {
            return false;
        }

        foreach (var point in topology.NoseContactPointIndices)
        {
            if (HasContactEvidence(samples[contactIndex], point))
            {
                return false;
            }
        }

        foreach (var point in topology.MainContactPointIndices)
        {
            if (!HasContactTransition(samples[previousAirborneIndex], samples[contactIndex], point) ||
                !HasContactEvidence(samples[contactIndex], point))
            {
                continue;
            }

            var startTime = TouchdownAnalysis.TimeOf(samples[contactIndex]);
            var previousTime = startTime;
            var sampleCount = 1;
            for (var index = contactIndex + 1; index < samples.Count; index++)
            {
                var time = TouchdownAnalysis.TimeOf(samples[index]);
                foreach (var nosePoint in topology.NoseContactPointIndices)
                {
                    if (HasContactEvidence(samples[index], nosePoint))
                    {
                        return false;
                    }
                }

                if (!HasContactEvidence(samples[index], point) ||
                    time - previousTime <= 0.0 ||
                    time - previousTime > MaximumFrameGapSeconds)
                {
                    break;
                }

                sampleCount++;
                previousTime = time;
                if (sampleCount >= MinimumSustainedContactSamples &&
                    time - startTime >= MinimumSustainedContactDurationSeconds)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryDetectConventionalTopology(
        List<TelemetrySample> samples,
        out TelemetryGearTopology topology)
    {
        topology = null!;
        if (samples.Count < MinimumSustainedContactSamples)
        {
            return false;
        }

        var runsByPoint = new List<ContactRun>[TelemetrySample.CapturedContactPointCount];
        var settledRuns = new List<ContactRun>();
        for (var point = 0; point < TelemetrySample.CapturedContactPointCount; point++)
        {
            var pointRuns = GetSustainedContactRuns(samples, point);
            runsByPoint[point] = pointRuns;
            if (pointRuns.Count == 0 ||
                pointRuns[pointRuns.Count - 1].EndTime != samples[samples.Count - 1].SimulationTimeSeconds)
            {
                continue;
            }

            settledRuns.Add(pointRuns[pointRuns.Count - 1]);
        }

        if (settledRuns.Count < 3 || settledRuns.Count > 4)
        {
            return false;
        }

        settledRuns.Sort((left, right) => left.StartTime.CompareTo(right.StartTime));
        var mains = settledRuns.GetRange(0, settledRuns.Count - 1);
        var nose = new List<ContactRun> { settledRuns[settledRuns.Count - 1] };
        // v1 supports conventional three-point gear and the A340-style center main.
        // A hard or bounced landing may separate the final main contacts by seconds,
        // so the independent evidence for the last point being the nose is derotation.
        var latestMainStart = mains[mains.Count - 1].StartTime;
        if (nose[0].StartTime - latestMainStart < MinimumMainOnlyDurationSeconds ||
            !HasPositiveDerotation(samples, latestMainStart, nose[0].StartTime))
        {
            return false;
        }

        var acceptedPoint = new bool[TelemetrySample.CapturedContactPointCount];
        foreach (var run in mains)
        {
            acceptedPoint[run.ContactPointIndex] = true;
        }

        foreach (var run in nose)
        {
            acceptedPoint[run.ContactPointIndex] = true;
        }

        // Earlier runs on the inferred gear points are ordinary bounces. A sustained
        // contact on any other point is a scrape/exotic sequence, so do not guess.
        for (var point = 0; point < runsByPoint.Length; point++)
        {
            if (!acceptedPoint[point] && runsByPoint[point].Count > 0)
            {
                return false;
            }
        }

        topology = new TelemetryGearTopology(
            ContactPointIndices(mains),
            ContactPointIndices(nose));
        return true;
    }

    private static bool HasPositiveDerotation(
        IReadOnlyList<TelemetrySample> samples,
        double startTime,
        double endTime)
    {
        var count = 0;
        var firstTime = double.NaN;
        var lastTime = double.NaN;
        var firstPitch = double.NaN;
        var lastPitch = double.NaN;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumXX = 0.0;
        var sumXY = 0.0;
        for (var index = 0; index < samples.Count; index++)
        {
            var time = samples[index].SimulationTimeSeconds;
            var pitch = samples[index].PitchDegrees;
            if (time < startTime || time > endTime || !IsFinite(pitch))
            {
                continue;
            }

            if (count == 0)
            {
                firstTime = time;
                firstPitch = pitch;
            }

            var x = time - startTime;
            lastTime = time;
            lastPitch = pitch;
            sumX += x;
            sumY += pitch;
            sumXX += x * x;
            sumXY += x * pitch;
            count++;
        }

        var denominator = count * sumXX - sumX * sumX;
        if (count < 3 || !IsFinite(firstTime) || !IsFinite(lastTime) ||
            lastTime <= firstTime ||
            lastPitch - firstPitch < MinimumDerotationPitchIncreaseDegrees ||
            !IsFinite(denominator) || denominator <= 0.0)
        {
            return false;
        }

        var slope = (count * sumXY - sumX * sumY) / denominator;
        return IsFinite(slope) && slope > 0.0;
    }

    private static List<ContactRun> GetSustainedContactRuns(
        IReadOnlyList<TelemetrySample> samples,
        int point)
    {
        var result = new List<ContactRun>();
        var startIndex = -1;

        for (var index = 0; index <= samples.Count; index++)
        {
            var active = index < samples.Count && HasContactEvidence(samples[index], point);
            if (active)
            {
                if (startIndex < 0)
                {
                    startIndex = index;
                }

                if (index + 1 < samples.Count &&
                    HasContactEvidence(samples[index + 1], point))
                {
                    continue;
                }
            }

            if (startIndex < 0)
            {
                continue;
            }

            var endIndex = active ? index : index - 1;
            var sampleCount = endIndex - startIndex + 1;
            var duration = samples[endIndex].SimulationTimeSeconds - samples[startIndex].SimulationTimeSeconds;
            if (sampleCount >= MinimumSustainedContactSamples &&
                duration >= MinimumSustainedContactDurationSeconds &&
                HasLocallySustainedObservation(samples, startIndex, endIndex))
            {
                result.Add(new ContactRun
                {
                    ContactPointIndex = point,
                    StartTime = samples[startIndex].SimulationTimeSeconds,
                    EndTime = samples[endIndex].SimulationTimeSeconds,
                });
            }

            startIndex = -1;
        }

        return result;
    }

    private static bool HasLocallySustainedObservation(
        IReadOnlyList<TelemetrySample> samples,
        int startIndex,
        int endIndex)
    {
        var localStartIndex = startIndex;
        var localSampleCount = 1;
        for (var index = startIndex + 1; index <= endIndex; index++)
        {
            var gap = samples[index].SimulationTimeSeconds - samples[index - 1].SimulationTimeSeconds;
            if (gap <= 0.0 || gap > MaximumFrameGapSeconds)
            {
                localStartIndex = index;
                localSampleCount = 1;
                continue;
            }

            localSampleCount++;
            if (localSampleCount >= MinimumSustainedContactSamples &&
                samples[index].SimulationTimeSeconds - samples[localStartIndex].SimulationTimeSeconds >=
                    MinimumSustainedContactDurationSeconds)
            {
                return true;
            }
        }

        return false;
    }

    private static int[] ContactPointIndices(IReadOnlyList<ContactRun> runs)
    {
        var result = new int[runs.Count];
        for (var index = 0; index < runs.Count; index++)
        {
            result[index] = runs[index].ContactPointIndex;
        }

        return result;
    }

    private static List<TelemetrySample> Normalize(IReadOnlyList<TelemetrySample> samples)
    {
        var ordered = samples
            .Where(sample => sample is not null && IsFinite(sample.SimulationTimeSeconds))
            .OrderBy(sample => sample.SimulationTimeSeconds)
            .ToArray();
        return new List<TelemetrySample>(TelemetryDeduplicator.Deduplicate(ordered));
    }

    private static bool TryRecoverPitchArm(
        IReadOnlyList<TelemetrySample> samples,
        IReadOnlyList<double> times,
        IReadOnlyList<double> planeAltitude,
        IReadOnlyList<double> groundAltitude,
        TelemetryGearTopology topology,
        out double pitchArm,
        out int mainOnlySampleCount,
        out double designIndependence)
    {
        pitchArm = double.NaN;
        mainOnlySampleCount = 0;
        designIndependence = 0.0;
        var pitch = new List<double>();
        var compression = new List<double>();
        var height = new List<double>();

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
            designIndependence >= MinimumDesignIndependence;
    }

    private static bool TryRecoverDatumOffset(
        IReadOnlyList<TelemetrySample> samples,
        IReadOnlyList<double> times,
        IReadOnlyList<double> planeAltitude,
        out double datumOffset,
        out double datumStandardDeviation,
        out int datumPhaseCount,
        out int datumWindowCount)
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
            var bank = DegreesToRadians(sample.BankDegrees);
            var omegaX = sample.RotationVelocityBodyXRadiansPerSecond;
            var omegaY = sample.RotationVelocityBodyYRadiansPerSecond;
            if (!IsFinite(pitch) || !IsFinite(bank) || !IsFinite(omegaX) || !IsFinite(omegaY))
            {
                rigidVerticalPerFoot[index] = double.NaN;
                continue;
            }

            rigidVerticalPerFoot[index] =
                WorldVerticalRigidBodyProjection.AtLongitudinalOffsetFps(
                    omegaX,
                    omegaY,
                    pitch,
                    bank,
                    1.0);
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
                if (actualDuration < MinimumIntegrationDurationSeconds ||
                    actualDuration > MaximumIntegrationDurationSeconds ||
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

    private static bool IsValidAirborneWindow(
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
            if (samples[index].OnGround ||
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

    private static bool TryFitTwoPredictors(
        IReadOnlyList<double> first,
        IReadOnlyList<double> second,
        IReadOnlyList<double> response,
        out double firstSlope,
        out double secondSlope,
        out double normalizedDeterminant)
    {
        firstSlope = double.NaN;
        secondSlope = double.NaN;
        normalizedDeterminant = 0.0;
        if (first.Count != second.Count || first.Count != response.Count || first.Count < 3)
        {
            return false;
        }

        var firstMean = Mean(first);
        var secondMean = Mean(second);
        var responseMean = Mean(response);
        var firstSquare = 0.0;
        var secondSquare = 0.0;
        var cross = 0.0;
        var firstResponse = 0.0;
        var secondResponse = 0.0;
        for (var index = 0; index < first.Count; index++)
        {
            var x = first[index] - firstMean;
            var z = second[index] - secondMean;
            var y = response[index] - responseMean;
            firstSquare += x * x;
            secondSquare += z * z;
            cross += x * z;
            firstResponse += x * y;
            secondResponse += z * y;
        }

        if (firstSquare <= 0.0 || secondSquare <= 0.0)
        {
            return false;
        }

        var product = firstSquare * secondSquare;
        var determinant = product - (cross * cross);
        if (!IsFinite(product) || !IsFinite(determinant) || determinant <= 0.0)
        {
            return false;
        }

        normalizedDeterminant = Clamp01(determinant / product);
        if (normalizedDeterminant < MinimumDesignIndependence)
        {
            return false;
        }

        firstSlope = ((firstResponse * secondSquare) - (secondResponse * cross)) / determinant;
        secondSlope = ((secondResponse * firstSquare) - (firstResponse * cross)) / determinant;
        return IsFinite(firstSlope) && IsFinite(secondSlope);
    }

    private static bool TryFitLine(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        out double slope)
    {
        slope = double.NaN;
        if (x.Count != y.Count || x.Count < 2)
        {
            return false;
        }

        var xMean = Mean(x);
        var yMean = Mean(y);
        var square = 0.0;
        var cross = 0.0;
        for (var index = 0; index < x.Count; index++)
        {
            var centeredX = x[index] - xMean;
            square += centeredX * centeredX;
            cross += centeredX * (y[index] - yMean);
        }

        if (!IsFinite(square) || square <= 0.0)
        {
            return false;
        }

        slope = cross / square;
        return IsFinite(slope);
    }

    private static bool IsMainOnly(TelemetrySample sample, TelemetryGearTopology topology)
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

    private static double MeanCompression(TelemetrySample sample, IReadOnlyList<int> points)
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

    private static bool HasContactEvidence(TelemetrySample sample, int point)
    {
        return HasOnGroundEvidence(sample, point) || HasCompressionEvidence(sample, point);
    }

    private static bool HasContactTransition(
        TelemetrySample previous,
        TelemetrySample current,
        int point)
    {
        return (!HasOnGroundEvidence(previous, point) && HasOnGroundEvidence(current, point)) ||
            (!HasCompressionEvidence(previous, point) && HasCompressionEvidence(current, point));
    }

    private static bool HasOnGroundEvidence(TelemetrySample sample, int point)
    {
        return sample.ContactPointOnGround is not null &&
            point < sample.ContactPointOnGround.Length &&
            sample.ContactPointOnGround[point];
    }

    private static bool HasCompressionEvidence(TelemetrySample sample, int point)
    {
        return sample.ContactPointCompression is not null &&
            point < sample.ContactPointCompression.Length &&
            IsFinite(sample.ContactPointCompression[point]) &&
            sample.ContactPointCompression[point] > MinimumCompressionEvidence;
    }

    private static void SanitizeIsolatedSpikes(double[] values)
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
            if (Math.Abs(original[index] - local[2]) > 5.0)
            {
                // Match the sequential research sanitizer: the left neighbor may
                // already have been repaired, while the right one is still original.
                values[index] = (values[index - 1] + original[index + 1]) / 2.0;
            }
        }
    }

    private static int LowerBound(IReadOnlyList<double> sorted, double value)
    {
        var low = 0;
        var high = sorted.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (sorted[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static double Mean(IReadOnlyList<double> values)
    {
        var sum = 0.0;
        for (var index = 0; index < values.Count; index++)
        {
            sum += values[index];
        }

        return sum / values.Count;
    }

    private static double Range(IReadOnlyList<double> values)
    {
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        for (var index = 0; index < values.Count; index++)
        {
            minimum = Math.Min(minimum, values[index]);
            maximum = Math.Max(maximum, values[index]);
        }

        return maximum - minimum;
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static double Clamp01(double value)
    {
        return Math.Max(0.0, Math.Min(1.0, value));
    }

    private static double Square(double value)
    {
        return value * value;
    }
}
