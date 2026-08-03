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
            });
        }

        if (storedSamples.Count > 0)
        {
            var contactSample = ClosestToContact(storedSamples).Sample;
            record.WeightPounds = Math.Round(contactSample.TotalWeightPounds, 1);
            record.CgPercent = Math.Round(contactSample.CgPercent, 2);
        }

        AddEngineSeries(record, storedSamples);
        AddContactSeries(record, storedSamples);

        return record;
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
