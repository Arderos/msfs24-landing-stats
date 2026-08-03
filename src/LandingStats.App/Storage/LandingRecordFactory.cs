using System;
using System.Collections.Generic;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Storage;

public static class LandingRecordFactory
{
    private const double FallbackWindowBeforeSeconds = 15.0;
    private const double WindowAfterSeconds = 15.0;
    private const double StoredSampleIntervalSeconds = 0.05;
    private const double ApproachGateFeet = 500.0;
    private const double StandardGravityFps2 = 32.17405;
    private const double MinimumRawPitchRangePercent = 12.0;
    private const double MinimumRawPitchCorrelation = 0.75;
    private const double MaximumRawPitchLagSeconds = 0.8;
    private const double RawPitchLagStepSeconds = 0.025;

    public static LandingRecord Create(
        TouchdownResult result,
        IReadOnlyList<TelemetrySample> samples,
        string aircraftTitle,
        string aircraftType,
        string airport = "Unknown airport",
        string runway = "—",
        int contactCount = 1,
        DateTime? timestampUtc = null)
    {
        var record = new LandingRecord
        {
            TimestampUtc = timestampUtc ?? DateTime.UtcNow,
            AircraftTitle = aircraftTitle,
            AircraftType = aircraftType,
            Airport = airport,
            Runway = runway,
            ContactNumber = result.ContactNumber,
            ContactCount = contactCount,
            InertialFpm = result.InertialVerticalFpm,
            SurfaceFpm = result.LatchedNormalFpm,
            SurfaceDeltaFpm = result.SurfaceRelativeDeltaFpm,
            TerrainFpm = result.TerrainContributionFpm,
            UnresolvedFpm = result.UnresolvedSurfaceDeltaFpm,
            PeakG150Milliseconds = result.PeakG150Milliseconds,
            PeakG2Seconds = result.PeakG,
            PitchDegrees = result.PitchDegrees,
            BankDegrees = result.BankDegrees,
            AirspeedKnots = result.IndicatedAirspeedKnots,
            GroundSpeedKnots = result.GroundSpeedKnots,
            InertialExtrapolated = result.InertialVerticalExtrapolated,
            InertialFitDurationSeconds = result.InertialFitDurationSeconds,
            LatchUpdateDetected = result.LatchUpdateDetected,
            LatchUpdateOffsetSeconds = result.LatchedUpdateOffsetSeconds,
            ContactTimeEstimatedFromCompression = result.ContactTimeEstimatedFromCompression,
        };

        var contactTime = result.EstimatedContactTimeSeconds;
        var windowStart = FindApproachGateRelativeTime(samples, contactTime);
        record.ApproachGateSeconds = Math.Round(windowStart, 4);
        var storedSamples = new List<StoredSample>();
        var nextStoredTime = windowStart;
        foreach (var sample in samples)
        {
            var sampleTime = TimeOf(sample);
            var relativeTime = sampleTime - contactTime;
            if (relativeTime < windowStart || relativeTime > WindowAfterSeconds)
            {
                continue;
            }

            if (relativeTime + 0.000001 < nextStoredTime)
            {
                continue;
            }

            storedSamples.Add(new StoredSample(sample, Math.Round(relativeTime, 4)));
            nextStoredTime = relativeTime + StoredSampleIntervalSeconds;
        }

        foreach (var stored in storedSamples)
        {
            var sample = stored.Sample;
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = stored.RelativeTime,
                InertialFpm = Math.Round(-sample.VelocityWorldYFps * 60.0, 2),
                IndicatedFpm = Math.Round(-sample.VerticalSpeedFps * 60.0, 2),
                GForce = Math.Round(sample.GForce, 4),
                AglFeet = Math.Round(sample.AboveGroundLevelFeet, 2),
                GroundSpeedKnots = Math.Round(sample.GroundSpeedKnots, 2),
                PitchDegrees = Math.Round(sample.PitchDegrees, 3),
                BankDegrees = Math.Round(sample.BankDegrees, 3),
                AngleOfAttackDegrees = Math.Round(sample.AngleOfAttackDegrees, 3),
                SideslipDegrees = Math.Round(sample.SideslipDegrees, 3),
                PilotRollPercent = Math.Round(sample.PilotRollInputPercent, 2),
                PilotPitchPercent = Math.Round(sample.PilotPitchInputPercent, 2),
                PilotYawPercent = Math.Round(sample.RudderPedalInputPercent, 2),
                AileronPercent = Math.Round(sample.AileronPosition * 100.0, 2),
                ElevatorPercent = Math.Round(sample.ElevatorPosition * 100.0, 2),
                RudderPercent = Math.Round(sample.RudderPosition * 100.0, 2),
                SpoilersLeftPercent = Math.Round(sample.SpoilersLeftPosition * 100.0, 2),
                SpoilersRightPercent = Math.Round(sample.SpoilersRightPosition * 100.0, 2),
                FlapsPercent = Math.Round((sample.FlapsLeftPercent + sample.FlapsRightPercent) * 50.0, 2),
                BrakeLeftPercent = Math.Round(sample.BrakeLeftPosition * 100.0, 2),
                BrakeRightPercent = Math.Round(sample.BrakeRightPosition * 100.0, 2),
                LongitudinalLoadG = Math.Round(sample.AccelerationBodyZFps2 / StandardGravityFps2, 4),
                LateralLoadG = Math.Round(sample.AccelerationBodyXFps2 / StandardGravityFps2, 4),
                BodyRateXDegreesPerSecond = Math.Round(sample.RotationVelocityBodyXRadiansPerSecond * 180.0 / Math.PI, 3),
                BodyRateYDegreesPerSecond = Math.Round(sample.RotationVelocityBodyYRadiansPerSecond * 180.0 / Math.PI, 3),
                BodyRateZDegreesPerSecond = Math.Round(sample.RotationVelocityBodyZRadiansPerSecond * 180.0 / Math.PI, 3),
                WindSpeedKnots = Math.Round(sample.AmbientWindVelocityKnots, 2),
                WindDirectionDegrees = Math.Round(sample.AmbientWindDirectionDegrees, 2),
                OnGround = sample.OnGround,
                LateralAccelerationFps2 = Math.Round(sample.AccelerationBodyXFps2, 4),
                LongitudinalAccelerationFps2 = Math.Round(sample.AccelerationBodyZFps2, 4),
                ElevatorDeflectionPercent = Math.Round(sample.ElevatorDeflectionPercentOver100 * 100.0, 2),
                AileronLeftDeflectionPercent = Math.Round(sample.AileronLeftDeflectionPercentOver100 * 100.0, 2),
                AileronRightDeflectionPercent = Math.Round(sample.AileronRightDeflectionPercentOver100 * 100.0, 2),
                RudderDeflectionPercent = Math.Round(sample.RudderDeflectionPercentOver100 * 100.0, 2),
                AxisElevatorSetPercent = Math.Round(sample.AxisElevatorSetPercent, 2),
                AxisElevatorSetValid = sample.AxisElevatorSetValid,
                AxisElevatorSetAgeSeconds = Math.Round(sample.AxisElevatorSetAgeSeconds, 4),
                RawControllerYAxisPercent = RoundedCopy(sample.RawControllerYAxisPercent, 2),
                RawControllerYAxisValid = (bool[])sample.RawControllerYAxisValid.Clone(),
                RawControllerYAxisAgeSeconds = RoundedCopy(sample.RawControllerYAxisAgeSeconds, 4),
            });
        }

        if (storedSamples.Count > 0)
        {
            var contactSample = ClosestToContact(storedSamples).Sample;
            record.WeightPounds = Math.Round(contactSample.TotalWeightPounds, 1);
            record.CgPercent = Math.Round(contactSample.CgPercent, 2);
            record.TouchdownLatitudeDegrees = contactSample.LatitudeDegrees;
            record.TouchdownLongitudeDegrees = contactSample.LongitudeDegrees;
            record.AngleOfAttackDegrees = Math.Round(contactSample.AngleOfAttackDegrees, 2);
        }

        AddEngineSeries(record, storedSamples);
        AddContactSeries(record, storedSamples);
        RefreshRawPitchInputSelection(record);

        return record;
    }

    public static bool RefreshRawPitchInputSelection(LandingRecord record)
    {
        var previousSource = record.RawPitchInputSourceIndex;
        var previousCorrelation = record.RawPitchInputCorrelation;
        var previousLag = record.RawPitchInputLagSeconds;
        record.RawPitchInputSourceIndex = -1;
        record.RawPitchInputCorrelation = 0;
        record.RawPitchInputLagSeconds = 0;

        var bestAbsoluteCorrelation = 0.0;
        var bestCorrelation = 0.0;
        var bestLag = 0.0;
        var bestSource = -1;

        for (var sourceIndex = 0; sourceIndex < TelemetrySample.CapturedControllerCount; sourceIndex++)
        {
            var minimum = double.PositiveInfinity;
            var maximum = double.NegativeInfinity;
            var validCount = 0;
            foreach (var point in record.Series)
            {
                if (point.OnGround || !HasRawControllerValue(point, sourceIndex))
                {
                    continue;
                }

                var value = point.RawControllerYAxisPercent[sourceIndex];
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                validCount++;
            }

            if (validCount < 20 || maximum - minimum < MinimumRawPitchRangePercent)
            {
                continue;
            }

            for (var lag = 0.0; lag <= MaximumRawPitchLagSeconds + 0.000001; lag += RawPitchLagStepSeconds)
            {
                if (!TryCorrelationAtLag(record.Series, sourceIndex, lag, out var correlation))
                {
                    continue;
                }

                var absoluteCorrelation = Math.Abs(correlation);
                if (absoluteCorrelation <= bestAbsoluteCorrelation)
                {
                    continue;
                }

                bestAbsoluteCorrelation = absoluteCorrelation;
                bestCorrelation = correlation;
                bestLag = lag;
                bestSource = sourceIndex;
            }
        }

        if (bestSource < 0 || bestAbsoluteCorrelation < MinimumRawPitchCorrelation)
        {
            return previousSource != record.RawPitchInputSourceIndex ||
                   Math.Abs(previousCorrelation - record.RawPitchInputCorrelation) > 0.000001 ||
                   Math.Abs(previousLag - record.RawPitchInputLagSeconds) > 0.000001;
        }

        record.RawPitchInputSourceIndex = bestSource;
        record.RawPitchInputCorrelation = Math.Round(bestCorrelation, 4);
        record.RawPitchInputLagSeconds = Math.Round(bestLag, 4);
        return previousSource != record.RawPitchInputSourceIndex ||
               Math.Abs(previousCorrelation - record.RawPitchInputCorrelation) > 0.000001 ||
               Math.Abs(previousLag - record.RawPitchInputLagSeconds) > 0.000001;
    }

    private static bool TryCorrelationAtLag(
        IReadOnlyList<LandingSeriesPoint> points,
        int sourceIndex,
        double lagSeconds,
        out double correlation)
    {
        var count = 0;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumXX = 0.0;
        var sumYY = 0.0;
        var sumXY = 0.0;

        foreach (var point in points)
        {
            if (point.OnGround ||
                !HasRawControllerValue(point, sourceIndex) ||
                !TryInterpolateProcessedPitch(points, point.TimeSeconds + lagSeconds, out var processedPitch))
            {
                continue;
            }

            var rawPitch = point.RawControllerYAxisPercent[sourceIndex];
            count++;
            sumX += rawPitch;
            sumY += processedPitch;
            sumXX += rawPitch * rawPitch;
            sumYY += processedPitch * processedPitch;
            sumXY += rawPitch * processedPitch;
        }

        var xVariance = count * sumXX - sumX * sumX;
        var yVariance = count * sumYY - sumY * sumY;
        if (count < 20 || xVariance <= 0.000001 || yVariance <= 0.000001)
        {
            correlation = 0;
            return false;
        }

        correlation = (count * sumXY - sumX * sumY) / Math.Sqrt(xVariance * yVariance);
        return !double.IsNaN(correlation) && !double.IsInfinity(correlation);
    }

    private static bool TryInterpolateProcessedPitch(
        IReadOnlyList<LandingSeriesPoint> points,
        double timeSeconds,
        out double value)
    {
        if (points.Count == 0 || timeSeconds < points[0].TimeSeconds || timeSeconds > points[points.Count - 1].TimeSeconds)
        {
            value = 0;
            return false;
        }

        var low = 0;
        var high = points.Count - 1;
        while (low + 1 < high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].TimeSeconds <= timeSeconds)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        var before = points[low];
        var after = points[high];
        if (timeSeconds > 0 || before.OnGround || after.OnGround)
        {
            value = 0;
            return false;
        }

        var duration = after.TimeSeconds - before.TimeSeconds;
        if (duration <= 0.000001)
        {
            value = before.PilotPitchPercent;
            return true;
        }

        var fraction = Math.Max(0, Math.Min(1, (timeSeconds - before.TimeSeconds) / duration));
        value = before.PilotPitchPercent + fraction * (after.PilotPitchPercent - before.PilotPitchPercent);
        return true;
    }

    private static bool HasRawControllerValue(LandingSeriesPoint point, int sourceIndex)
    {
        return point.RawControllerYAxisPercent != null &&
               point.RawControllerYAxisValid != null &&
               sourceIndex >= 0 &&
               sourceIndex < point.RawControllerYAxisPercent.Length &&
               sourceIndex < point.RawControllerYAxisValid.Length &&
               point.RawControllerYAxisValid[sourceIndex] &&
               !double.IsNaN(point.RawControllerYAxisPercent[sourceIndex]) &&
               !double.IsInfinity(point.RawControllerYAxisPercent[sourceIndex]);
    }

    private static double FindApproachGateRelativeTime(IReadOnlyList<TelemetrySample> samples, double contactTime)
    {
        TelemetrySample? previous = null;
        var previousTime = 0.0;
        double? lastDescendingCrossing = null;
        double? earliestRelativeTime = null;

        foreach (var sample in samples)
        {
            var sampleTime = TimeOf(sample);
            if (sampleTime > contactTime)
            {
                break;
            }

            earliestRelativeTime ??= sampleTime - contactTime;
            if (previous != null &&
                !previous.OnGround &&
                !sample.OnGround &&
                previous.AboveGroundLevelFeet >= ApproachGateFeet &&
                sample.AboveGroundLevelFeet < ApproachGateFeet &&
                sample.VelocityWorldYFps < 0)
            {
                var altitudeChange = previous.AboveGroundLevelFeet - sample.AboveGroundLevelFeet;
                var fraction = altitudeChange <= 0.000001
                    ? 1.0
                    : (previous.AboveGroundLevelFeet - ApproachGateFeet) / altitudeChange;
                fraction = Math.Max(0, Math.Min(1, fraction));
                var crossingTime = previousTime + fraction * (sampleTime - previousTime);
                lastDescendingCrossing = crossingTime - contactTime;
            }

            previous = sample;
            previousTime = sampleTime;
        }

        if (lastDescendingCrossing.HasValue)
        {
            return lastDescendingCrossing.Value;
        }

        return Math.Max(-FallbackWindowBeforeSeconds, earliestRelativeTime ?? -FallbackWindowBeforeSeconds);
    }

    private static double TimeOf(TelemetrySample sample)
    {
        return Math.Abs(sample.SimulationTimeSeconds) > 0.000000001
            ? sample.SimulationTimeSeconds
            : sample.HostElapsedSeconds;
    }

    private static void AddEngineSeries(LandingRecord record, IReadOnlyList<StoredSample> samples)
    {
        var declaredCount = 0;
        foreach (var stored in samples)
        {
            declaredCount = Math.Max(declaredCount, stored.Sample.NumberOfEngines);
        }

        declaredCount = Math.Max(0, Math.Min(TelemetrySample.CapturedEngineCount, declaredCount));
        for (var engineIndex = 0; engineIndex < TelemetrySample.CapturedEngineCount; engineIndex++)
        {
            var hasData = engineIndex < declaredCount;
            if (!hasData)
            {
                foreach (var stored in samples)
                {
                    if (Math.Abs(stored.Sample.EngineThrottlePercent[engineIndex]) > 0.001 ||
                        Math.Abs(stored.Sample.EngineN1Percent[engineIndex]) > 0.001 ||
                        Math.Abs(stored.Sample.EngineRpm[engineIndex]) > 0.001 ||
                        Math.Abs(stored.Sample.EngineReversePercent[engineIndex]) > 0.001)
                    {
                        hasData = true;
                        break;
                    }
                }
            }

            if (!hasData)
            {
                continue;
            }

            var series = new LandingEngineSeries { EngineNumber = engineIndex + 1 };
            foreach (var stored in samples)
            {
                series.Points.Add(new LandingEnginePoint
                {
                    TimeSeconds = stored.RelativeTime,
                    ThrottlePercent = Math.Round(stored.Sample.EngineThrottlePercent[engineIndex], 2),
                    N1Percent = Math.Round(stored.Sample.EngineN1Percent[engineIndex], 2),
                    Rpm = Math.Round(stored.Sample.EngineRpm[engineIndex], 1),
                    ReversePercent = Math.Round(stored.Sample.EngineReversePercent[engineIndex], 2),
                });
            }

            record.Engines.Add(series);
        }
    }

    private static void AddContactSeries(LandingRecord record, IReadOnlyList<StoredSample> samples)
    {
        for (var contactIndex = 0; contactIndex < TelemetrySample.CapturedContactPointCount; contactIndex++)
        {
            var active = false;
            foreach (var stored in samples)
            {
                if (stored.Sample.ContactPointOnGround[contactIndex] ||
                    Math.Abs(stored.Sample.ContactPointCompression[contactIndex]) > 0.001)
                {
                    active = true;
                    break;
                }
            }

            if (!active)
            {
                continue;
            }

            var series = new LandingContactSeries { ContactPointIndex = contactIndex };
            foreach (var stored in samples)
            {
                series.Points.Add(new LandingContactPoint
                {
                    TimeSeconds = stored.RelativeTime,
                    CompressionPercent = Math.Round(stored.Sample.ContactPointCompression[contactIndex], 3),
                    PositionPercent = Math.Round(stored.Sample.ContactPointPosition[contactIndex], 3),
                    OnGround = stored.Sample.ContactPointOnGround[contactIndex],
                });
            }

            record.ContactPoints.Add(series);
        }
    }

    private static StoredSample ClosestToContact(IReadOnlyList<StoredSample> samples)
    {
        var closest = samples[0];
        for (var index = 1; index < samples.Count; index++)
        {
            if (Math.Abs(samples[index].RelativeTime) < Math.Abs(closest.RelativeTime))
            {
                closest = samples[index];
            }
        }

        return closest;
    }

    private static double[] RoundedCopy(double[] values, int digits)
    {
        var result = new double[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            result[index] = Math.Round(values[index], digits);
        }

        return result;
    }

    private sealed class StoredSample
    {
        public StoredSample(TelemetrySample sample, double relativeTime)
        {
            Sample = sample;
            RelativeTime = relativeTime;
        }

        public TelemetrySample Sample { get; }

        public double RelativeTime { get; }
    }
}
