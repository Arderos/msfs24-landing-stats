using System;
using System.Collections.Generic;
using System.Linq;
using static LandingStats.Core.GeometryCalibrationPolicy;
using static LandingStats.Core.GeometryCalibrationMath;
using static LandingStats.Core.GeometryContactEvidence;
using static LandingStats.Core.GeometryCalibrationInputs;
using static LandingStats.Core.PitchArmCalibration;

namespace LandingStats.Core;

// Pure component extracted without changing the established estimator equations.
internal static class TelemetryTopologyInference
{
    internal sealed class ContactRun
    {
        public int ContactPointIndex { get; set; }

        public double StartTime { get; set; }

        public double EndTime { get; set; }
    }

    internal static bool TryDetectConventionalTopology(
        IReadOnlyList<TelemetrySample> samples,
        out TelemetryGearTopology topology)
    {
        return TryDetectConventionalTopology(samples, out topology, out _);
    }

    internal static bool TryDetectConventionalTopology(
        IReadOnlyList<TelemetrySample> samples,
        out TelemetryGearTopology topology,
        out TelemetryGeometryFailure failure)
    {
        topology = null!;
        failure = TelemetryGeometryFailure.InsufficientSamples;
        if (samples is null)
        {
            return false;
        }

        return TryDetectConventionalTopology(Normalize(samples), out topology, out failure);
    }

    internal static bool TryCreateConfiguredTopology(
        IReadOnlyList<TouchdownMainGearContactPoint>? mainPoints,
        IReadOnlyList<int>? nosePoints,
        out TelemetryGearTopology topology)
    {
        topology = null!;
        if (mainPoints is null || nosePoints is null ||
            mainPoints.Count < 2 || nosePoints.Count < 1)
        {
            return false;
        }

        var occupied = new bool[TelemetrySample.CapturedContactPointCount];
        var mains = new int[mainPoints.Count];
        for (var index = 0; index < mainPoints.Count; index++)
        {
            var point = mainPoints[index];
            if (point is null ||
                point.ContactPointIndex < 0 ||
                point.ContactPointIndex >= occupied.Length ||
                !IsFinite(point.LongitudinalArmFeet) ||
                occupied[point.ContactPointIndex])
            {
                return false;
            }

            occupied[point.ContactPointIndex] = true;
            mains[index] = point.ContactPointIndex;
        }

        var noses = new int[nosePoints.Count];
        for (var index = 0; index < nosePoints.Count; index++)
        {
            var point = nosePoints[index];
            if (point < 0 || point >= occupied.Length || occupied[point])
            {
                return false;
            }

            occupied[point] = true;
            noses[index] = point;
        }

        topology = new TelemetryGearTopology(mains, noses);
        return true;
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
                if (!samples[index].MotionSimulation) break;
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

    internal static bool TryDetectConventionalTopology(
        List<TelemetrySample> samples,
        out TelemetryGearTopology topology,
        out TelemetryGeometryFailure failure)
    {
        var timingAccepted = TryDetectTimingTopology(samples, out topology, out failure);
        var points = Enumerable.Range(0, TelemetrySample.CapturedContactPointCount)
            .Where(point => GetSustainedContactRuns(samples, point).Count > 0).ToArray();
        if (!TelemetrySpatialGearTopology.TryInfer(samples, points, out var spatial)) return timingAccepted;
        var agrees = timingAccepted && topology.NoseContactPointIndices.Length == 1 &&
            topology.NoseContactPointIndices[0] == spatial.Nose;
        if (agrees)
        {
            topology = new TelemetryGearTopology(topology.MainContactPointIndices, topology.NoseContactPointIndices,
                topology.UsesClusteredWheelInference, spatialEvidence: spatial);
            return true;
        }
        if (spatial.StableRoles)
        {
            topology = new TelemetryGearTopology(spatial.Mains, new[] { spatial.Nose }, usesSpatialInference: true, spatialEvidence: spatial);
            failure = TelemetryGeometryFailure.None;
            return true;
        }
        if (timingAccepted)
        {
            topology = null!;
            failure = TelemetryGeometryFailure.ConflictingSpatialRoles;
            return false;
        }
        return false;
    }

    internal static bool TryDetectTimingTopology(
        List<TelemetrySample> samples,
        out TelemetryGearTopology topology,
        out TelemetryGeometryFailure failure)
    {
        topology = null!;
        failure = TelemetryGeometryFailure.UnsupportedTopology;
        if (samples.Count < MinimumSustainedContactSamples)
        {
            return false;
        }

        var runsByPoint = new List<ContactRun>[TelemetrySample.CapturedContactPointCount];
        var settledRuns = new List<ContactRun>();
        var lastMotion = samples.LastOrDefault(sample => sample.MotionSimulation);
        if (lastMotion == null) { failure = TelemetryGeometryFailure.IncompleteSettledGear; return false; }
        for (var point = 0; point < TelemetrySample.CapturedContactPointCount; point++)
        {
            var pointRuns = GetSustainedContactRuns(samples, point);
            runsByPoint[point] = pointRuns;
            if (pointRuns.Count == 0 ||
                pointRuns[pointRuns.Count - 1].EndTime != TelemetryAnalysisFrames.TimeOf(lastMotion))
            {
                continue;
            }

            settledRuns.Add(pointRuns[pointRuns.Count - 1]);
        }

        if (settledRuns.Count < 3)
        {
            failure = TelemetryGeometryFailure.IncompleteSettledGear;
            return false;
        }

        settledRuns.Sort((left, right) => left.StartTime.CompareTo(right.StartTime));
        var activePoints = runsByPoint.Count(runs => runs.Count > 0);
        if (activePoints >= 3 && activePoints <= 5 && runsByPoint.Any(runs => runs.Count > 1))
        {
            // Retain the settled-gear and foreign-contact guards. Only the roles
            // are recovered from earlier loading phases, never from index names.
            if (settledRuns.Count != activePoints)
            {
                failure = TelemetryGeometryFailure.IncompleteSettledGear;
                return false;
            }

            return TryCreateRepeatedContactTopology(samples, runsByPoint, settledRuns, out topology, out failure);
        }

        if (settledRuns.Count <= 5)
        {
            var accepted = TryCreateSinglePointNoseTopology(samples, runsByPoint, settledRuns, out topology);
            if (accepted) failure = TelemetryGeometryFailure.None;
            return accepted;
        }

        if (settledRuns.Count >= MinimumClusteredSettledPointCount &&
            TryCreateClusteredWheelTopology(samples, runsByPoint, settledRuns, out topology))
        {
            failure = TelemetryGeometryFailure.None;
            return true;
        }

        // Preserve the established clustered path. Only its refusal may enable
        // a multi-main layout with one late point, never an aircraft-index guess.
        if (!TryCreateSinglePointNoseTopology(samples, runsByPoint, settledRuns, out topology))
        {
            return false;
        }

        var times = samples.Select(sample => TelemetryAnalysisFrames.TimeOf(sample)).ToArray();
        var altitude = samples.Select(sample => sample.PlaneAltitudeFeet).ToArray();
        var ground = samples.Select(sample => sample.GroundAltitudeFeet).ToArray();
        GroundAltitudeFilters.ForCalibration(altitude);
        GroundAltitudeFilters.ForCalibration(ground);
        if (TryRecoverPitchArm(samples, times, altitude, ground, topology,
                out _, out _, out _, requireStableArm: true))
        {
            failure = TelemetryGeometryFailure.None;
            return true;
        }

        topology = null!;
        failure = TelemetryGeometryFailure.UnstablePitchArm;
        return false;
    }

    internal static bool TryCreateRepeatedContactTopology(
        List<TelemetrySample> samples,
        List<ContactRun>[] runsByPoint,
        List<ContactRun> settledRuns,
        out TelemetryGearTopology topology,
        out TelemetryGeometryFailure failure)
    {
        topology = null!;
        failure = TelemetryGeometryFailure.InsufficientDerotation;
        TelemetryGearTopology? supported = null;
        for (var nose = 0; nose < runsByPoint.Length; nose++)
        {
            foreach (var noseRun in runsByPoint[nose])
            {
                var end = samples.FindIndex(sample => TelemetryAnalysisFrames.TimeOf(sample) == noseRun.StartTime);
                var begin = end;
                // Each sustained nose onset closes one loading phase. A previous
                // nose contact (even a short one), a physics stop or a frame gap
                // ends the evidence: derotation must not span that boundary.
                while (begin > 0 && samples[begin - 1].MotionSimulation &&
                       !HasContactEvidence(samples[begin - 1], nose))
                {
                    var gap = TelemetryAnalysisFrames.TimeOf(samples[begin]) - TelemetryAnalysisFrames.TimeOf(samples[begin - 1]);
                    if (gap <= 0.0 || gap > MaximumFrameGapSeconds) break;
                    begin--;
                }

                var phaseStart = TelemetryAnalysisFrames.TimeOf(samples[begin]);
                var mains = new List<ContactRun>();
                for (var point = 0; point < runsByPoint.Length; point++)
                {
                    if (point == nose) continue;
                    var run = runsByPoint[point].FirstOrDefault(candidate =>
                        candidate.EndTime >= phaseStart && candidate.StartTime < noseRun.StartTime);
                    if (run == null) continue;
                    mains.Add(new ContactRun
                    {
                        ContactPointIndex = point,
                        StartTime = Math.Max(run.StartTime, phaseStart),
                        EndTime = run.EndTime,
                    });
                }

                if (mains.Count != settledRuns.Count - 1) continue;
                mains.Sort((left, right) => left.StartTime.CompareTo(right.StartTime));
                var latestMainStart = mains[mains.Count - 1].StartTime;
                if (noseRun.StartTime - latestMainStart < MinimumMainOnlyDurationSeconds ||
                    !HasPositiveDerotation(samples, latestMainStart, noseRun.StartTime)) continue;

                var candidateTopology = new TelemetryGearTopology(ContactPointIndices(mains), new[] { nose });
                var first = samples.FindIndex(begin, end - begin + 1,
                    sample => TelemetryAnalysisFrames.TimeOf(sample) >= latestMainStart);
                var phase = samples.GetRange(first, end - first + 1);
                // A delay between onsets is not evidence that the main gear was
                // carrying the aircraft. Require actual main-only observations
                // inside this phase; a later interval cannot justify it. Role
                // evidence does not itself require fitting an already supplied arm.
                if (!HasSustainedMainOnlyEvidence(phase, candidateTopology))
                {
                    failure = TelemetryGeometryFailure.InsufficientMainOnlyEvidence;
                    continue;
                }

                if (supported != null)
                {
                    failure = TelemetryGeometryFailure.AmbiguousContactRoles;
                    return false;
                }

                supported = candidateTopology;
                break; // Further phases for this same nose are not conflicting roles.
            }
        }

        if (supported == null) return false;
        // Preserve the established fit when the final settled sequence and the
        // independently supported loading phase agree. Additional roles recovered
        // only from earlier phases must also survive temporal arm refits.
        if (TryCreateSinglePointNoseTopology(samples, runsByPoint, settledRuns, out var settledTopology) &&
            settledTopology.NoseContactPointIndices[0] == supported.NoseContactPointIndices[0])
        {
            topology = settledTopology;
        }
        else
        {
            var altitude = samples.Select(sample => sample.MotionSimulation ? sample.PlaneAltitudeFeet : double.NaN).ToArray();
            var ground = samples.Select(sample => sample.MotionSimulation ? sample.GroundAltitudeFeet : double.NaN).ToArray();
            GroundAltitudeFilters.ForCalibration(altitude);
            GroundAltitudeFilters.ForCalibration(ground);
            if (!TryRecoverPitchArm(samples, samples.Select(sample => TelemetryAnalysisFrames.TimeOf(sample)).ToArray(),
                    altitude, ground, supported, out _, out _, out _, requireStableArm: true))
            {
                failure = TelemetryGeometryFailure.UnstablePitchArm;
                return false;
            }

            topology = supported;
        }

        failure = TelemetryGeometryFailure.None;
        return true;
    }

    internal static bool HasSustainedMainOnlyEvidence(
        IReadOnlyList<TelemetrySample> samples,
        TelemetryGearTopology topology)
    {
        var start = -1;
        for (var index = 0; index < samples.Count; index++)
        {
            if (!IsMainOnly(samples[index], topology))
            {
                start = -1;
                continue;
            }

            if (start < 0) start = index;
            if (index - start + 1 >= MinimumPitchRegressionRows &&
                TelemetryAnalysisFrames.TimeOf(samples[index]) - TelemetryAnalysisFrames.TimeOf(samples[start]) >= MinimumMainOnlyDurationSeconds)
                return true;
        }

        return false;
    }

    internal static bool TryCreateSinglePointNoseTopology(
        IReadOnlyList<TelemetrySample> samples,
        List<ContactRun>[] runsByPoint,
        List<ContactRun> settledRuns,
        out TelemetryGearTopology topology)
    {
        topology = null!;
        var mains = settledRuns.GetRange(0, settledRuns.Count - 1);
        var nose = new List<ContactRun> { settledRuns[settledRuns.Count - 1] };
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
            ContactPointIndices(nose),
            usesClusteredWheelInference: settledRuns.Count >= MinimumClusteredSettledPointCount);
        return true;
    }

    internal static bool TryCreateClusteredWheelTopology(
        IReadOnlyList<TelemetrySample> samples,
        List<ContactRun>[] runsByPoint,
        List<ContactRun> settledRuns,
        out TelemetryGearTopology topology)
    {
        topology = null!;
        // Try gaps from largest to smallest, but keep only the contiguous cluster
        // immediately following a candidate split. A short automatic capture can
        // stop while a rollout-only helper point is active; that truncated run then
        // looks settled, but remains an isolated late point rather than a wheel in
        // the main/nose contact sequence.
        var candidateSplits = Enumerable.Range(1, settledRuns.Count - 1)
            .Where(index => index >= MinimumPointsPerCluster)
            .OrderByDescending(index => settledRuns[index].StartTime - settledRuns[index - 1].StartTime);
        foreach (var splitIndex in candidateSplits)
        {
            var clusterGap = settledRuns[splitIndex].StartTime - settledRuns[splitIndex - 1].StartTime;
            if (clusterGap < MinimumMainOnlyDurationSeconds)
            {
                break;
            }

            var noseEndExclusive = splitIndex + 1;
            while (noseEndExclusive < settledRuns.Count &&
                   settledRuns[noseEndExclusive].StartTime - settledRuns[noseEndExclusive - 1].StartTime <=
                   MaximumAuxiliaryNoseOnsetDelaySeconds)
            {
                noseEndExclusive++;
            }

            if (noseEndExclusive - splitIndex < MinimumPointsPerCluster)
            {
                continue;
            }

            var secondLargestGap = double.NegativeInfinity;
            for (var index = 1; index < noseEndExclusive; index++)
            {
                if (index == splitIndex)
                {
                    continue;
                }

                secondLargestGap = Math.Max(
                    secondLargestGap,
                    settledRuns[index].StartTime - settledRuns[index - 1].StartTime);
            }

            if (secondLargestGap > 0.0 &&
                clusterGap < secondLargestGap * MinimumClusterGapDominanceRatio)
            {
                continue;
            }

            var mains = settledRuns.GetRange(0, splitIndex);
            var nose = settledRuns.GetRange(splitIndex, noseEndExclusive - splitIndex);
            var latestMainStart = mains[mains.Count - 1].StartTime;
            var earliestNoseStart = nose[0].StartTime;
            var latestNoseStart = nose[nose.Count - 1].StartTime;
            if (!HasPositiveDerotation(
                    samples,
                    latestMainStart,
                    earliestNoseStart,
                    MinimumClusterDerotationPitchIncreaseDegrees))
            {
                continue;
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

            // Some aircraft expose each wheel and temporary bogie/fuselage helpers as
            // separate contact points. A helper is compatible only when it spans the
            // main-gear phase, accompanies nose settling, or first appears after the
            // landing gear is already fully down. Any other early contact remains an
            // unsupported scrape/exotic sequence rather than being guessed into a gear.
            var compatible = true;
            for (var point = 0; point < runsByPoint.Length && compatible; point++)
            {
                if (acceptedPoint[point])
                {
                    continue;
                }

                foreach (var run in runsByPoint[point])
                {
                    if (run.StartTime > latestNoseStart + MaximumAuxiliaryNoseOnsetDelaySeconds)
                    {
                        continue;
                    }

                    var bridgesGearTransition =
                        run.StartTime <= earliestNoseStart - MinimumSustainedContactDurationSeconds &&
                        run.EndTime >= earliestNoseStart - MinimumSustainedContactDurationSeconds;
                    var accompaniesNose = run.StartTime >= earliestNoseStart - MinimumSustainedContactDurationSeconds &&
                                          run.StartTime <= latestNoseStart + MaximumAuxiliaryNoseOnsetDelaySeconds &&
                                          run.EndTime >= latestNoseStart - MinimumSustainedContactDurationSeconds;
                    if (!bridgesGearTransition && !accompaniesNose)
                    {
                        compatible = false;
                        break;
                    }
                }
            }

            if (!compatible)
            {
                continue;
            }

            topology = new TelemetryGearTopology(
                ContactPointIndices(mains),
                ContactPointIndices(nose),
                usesClusteredWheelInference: true);
            return true;
        }

        return false;
    }

    internal static bool HasPositiveDerotation(
        IReadOnlyList<TelemetrySample> samples,
        double startTime,
        double endTime,
        double minimumPitchIncreaseDegrees = MinimumDerotationPitchIncreaseDegrees)
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
            var time = TelemetryAnalysisFrames.TimeOf(samples[index]);
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
            lastPitch - firstPitch < minimumPitchIncreaseDegrees ||
            !IsFinite(denominator) || denominator <= 0.0)
        {
            return false;
        }

        var slope = (count * sumXY - sumX * sumY) / denominator;
        return IsFinite(slope) && slope > 0.0;
    }

    internal static List<ContactRun> GetSustainedContactRuns(
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
            var duration = TelemetryAnalysisFrames.TimeOf(samples[endIndex]) - TelemetryAnalysisFrames.TimeOf(samples[startIndex]);
            if (sampleCount >= MinimumSustainedContactSamples &&
                duration >= MinimumSustainedContactDurationSeconds &&
                HasLocallySustainedObservation(samples, startIndex, endIndex))
            {
                result.Add(new ContactRun
                {
                    ContactPointIndex = point,
                    StartTime = TelemetryAnalysisFrames.TimeOf(samples[startIndex]),
                    EndTime = TelemetryAnalysisFrames.TimeOf(samples[endIndex]),
                });
            }

            startIndex = -1;
        }

        return result;
    }

    internal static bool HasLocallySustainedObservation(
        IReadOnlyList<TelemetrySample> samples,
        int startIndex,
        int endIndex)
    {
        var localStartIndex = startIndex;
        var localSampleCount = 1;
        for (var index = startIndex + 1; index <= endIndex; index++)
        {
            var gap = TelemetryAnalysisFrames.TimeOf(samples[index]) - TelemetryAnalysisFrames.TimeOf(samples[index - 1]);
            if (gap <= 0.0 || gap > MaximumFrameGapSeconds)
            {
                localStartIndex = index;
                localSampleCount = 1;
                continue;
            }

            localSampleCount++;
            if (localSampleCount >= MinimumSustainedContactSamples &&
                TelemetryAnalysisFrames.TimeOf(samples[index]) - TelemetryAnalysisFrames.TimeOf(samples[localStartIndex]) >=
                    MinimumSustainedContactDurationSeconds)
            {
                return true;
            }
        }

        return false;
    }

    internal static int[] ContactPointIndices(IReadOnlyList<ContactRun> runs)
    {
        var result = new int[runs.Count];
        for (var index = 0; index < runs.Count; index++)
        {
            result[index] = runs[index].ContactPointIndex;
        }

        return result;
    }
}
