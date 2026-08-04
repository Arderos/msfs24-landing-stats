using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;

namespace LandingStats.App.Models;

[DataContract]
public sealed class LandingRecord
{
    public const int CurrentFormatVersion = 7;
    public const double NonFiniteStorageSentinel = -1.7976931348623157E+308;

    [DataMember(Order = 1)]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    [DataMember(Order = 2)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [DataMember(Order = 3)]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [DataMember(Order = 4)]
    public string Simulator { get; set; } = "MSFS 2024";

    [DataMember(Order = 5)]
    public string AircraftTitle { get; set; } = "Unknown aircraft";

    [DataMember(Order = 6)]
    public string AircraftType { get; set; } = "unknown";

    [DataMember(Order = 7)]
    public string Airport { get; set; } = "Unknown airport";

    [DataMember(Order = 8)]
    public string Runway { get; set; } = "—";

    [DataMember(Order = 9)]
    public int ContactNumber { get; set; } = 1;

    [DataMember(Order = 10)]
    public int ContactCount { get; set; } = 1;

    [DataMember(Order = 11)]
    public double InertialFpm { get; set; }

    [DataMember(Order = 12)]
    public double SurfaceFpm { get; set; }

    [DataMember(Order = 13)]
    public double SurfaceDeltaFpm { get; set; }

    [DataMember(Order = 14)]
    public double TerrainFpm { get; set; }

    [DataMember(Order = 15)]
    public double UnresolvedFpm { get; set; }

    [DataMember(Order = 16)]
    public double PeakG150Milliseconds { get; set; }

    [DataMember(Order = 17)]
    public double PeakG2Seconds { get; set; }

    [DataMember(Order = 18)]
    public double PitchDegrees { get; set; }

    [DataMember(Order = 19)]
    public double BankDegrees { get; set; }

    [DataMember(Order = 20)]
    public double AirspeedKnots { get; set; }

    [DataMember(Order = 21)]
    public List<LandingSeriesPoint> Series { get; set; } = new List<LandingSeriesPoint>();

    [DataMember(Order = 22, EmitDefaultValue = false)]
    public double? FlareStartSeconds { get; set; }

    [DataMember(Order = 23)]
    public List<LandingEngineSeries> Engines { get; set; } = new List<LandingEngineSeries>();

    [DataMember(Order = 24)]
    public List<LandingContactSeries> ContactPoints { get; set; } = new List<LandingContactSeries>();

    [DataMember(Order = 25)]
    public double WeightPounds { get; set; }

    [DataMember(Order = 26)]
    public double CgPercent { get; set; }

    [DataMember(Order = 27)]
    public double ApproachGateSeconds { get; set; } = -15.0;

    [DataMember(Order = 28, EmitDefaultValue = false)]
    public double? TouchdownLatitudeDegrees { get; set; }

    [DataMember(Order = 29, EmitDefaultValue = false)]
    public double? TouchdownLongitudeDegrees { get; set; }

    [DataMember(Order = 30, EmitDefaultValue = false)]
    public double? AirportDistanceNauticalMiles { get; set; }

    [DataMember(Order = 31)]
    public bool InertialExtrapolated { get; set; }

    [DataMember(Order = 32)]
    public double InertialFitDurationSeconds { get; set; }

    [DataMember(Order = 33)]
    public bool LatchUpdateDetected { get; set; }

    [DataMember(Order = 34)]
    public double LatchUpdateOffsetSeconds { get; set; }

    [DataMember(Order = 35)]
    public bool ContactTimeEstimatedFromCompression { get; set; }

    [DataMember(Order = 36)]
    public double GroundSpeedKnots { get; set; }

    [DataMember(Order = 37)]
    public double AngleOfAttackDegrees { get; set; }

    [DataMember(Order = 38)]
    public List<string> ControlInputSources { get; set; } = new List<string>();

    [DataMember(Order = 39)]
    public int RawPitchInputSourceIndex { get; set; } = -1;

    [DataMember(Order = 40)]
    public double RawPitchInputCorrelation { get; set; }

    [DataMember(Order = 41)]
    public double RawPitchInputLagSeconds { get; set; }

    [DataMember(Order = 42)]
    public List<int> RawControllerSourceIndices { get; set; } = new List<int>();

    [DataMember(Order = 43)]
    public double WindSpeedKnotsAtContact { get; set; }

    [DataMember(Order = 44)]
    public double WindDirectionDegreesAtContact { get; set; }

    [IgnoreDataMember]
    public bool IsSummaryOnly { get; set; }

    public string TimestampDisplay => TimestampUtc.ToLocalTime().ToString("dd MMM · HH:mm", CultureInfo.CurrentCulture);

    public string LocationDisplay => Runway == "—" ? Airport : $"{Airport} · RWY {Runway}";

    public string InertialDisplay => $"{-InertialFpm:+0;-0;0}";

    public bool HasSurfaceLatchData => !double.IsNaN(SurfaceFpm);

    public string SurfaceDisplay => HasSurfaceLatchData ? $"{-SurfaceFpm:+0;-0;0} fpm" : "n/a";

    public string SurfaceValueDisplay => HasSurfaceLatchData ? $"{-SurfaceFpm:+0;-0;0}" : "n/a";

    public string SurfaceUnitDisplay => HasSurfaceLatchData ? "fpm" : string.Empty;

    public string DeltaDisplay => !double.IsNaN(SurfaceDeltaFpm) ? $"{-SurfaceDeltaFpm:+0;-0;0} fpm" : "surface delta n/a";

    public string TerrainDisplay =>
        $"{FormatSignedMetric(-TerrainFpm)} terrain · {FormatSignedMetric(-UnresolvedFpm)} unresolved";

    public string G150Display => PeakG150Milliseconds.ToString("F2", CultureInfo.CurrentCulture);

    public string G2SecondsDisplay => $"{PeakG2Seconds:F2} peak / 2s";

    public string AttitudeDisplay => $"{-PitchDegrees:+0.0;-0.0;0.0}° / {BankDegrees:+0.0;-0.0;0.0}°";

    public string AirspeedDisplay => $"{AirspeedKnots:F0} kt";

    public string GroundSpeedDisplay => $"{GroundSpeedKnots:F0} kt";

    public string AngleOfAttackDisplay => $"AoA {AngleOfAttackDegrees:F1}°";

    public string ContactDisplay => ContactCount > 1 ? $"Contact {ContactNumber} of {ContactCount}" : "Single contact";

    public bool HasTouchdownCoordinates =>
        TouchdownLatitudeDegrees.HasValue && TouchdownLongitudeDegrees.HasValue;

    public bool HasRawPitchInput =>
        FormatVersion >= 6 &&
        RawPitchInputSourceIndex >= 0 &&
        Math.Abs(RawPitchInputCorrelation) >= 0.75;

    public string PitchInputSourceDisplay => HasRawPitchInput
        ? $"RAW C{RawPitchInputSourceIndex} · {RawPitchInputLagSeconds * 1000.0:F0} MS"
        : "SIMCONNECT PROCESSED COMMAND";

    public string InertialQualityDisplay => FormatVersion < 4
        ? "LEGACY · QUALITY UNKNOWN"
        : InertialExtrapolated
            ? $"EXTRAPOLATED · {InertialFitDurationSeconds:F2} S FIT"
            : "RAW LAST AIRBORNE FRAME";

    public string LandingFeelDisplay => Math.Abs(InertialFpm) <= 240.0
        ? "SMOOTH"
        : "FIRM";

    public string LandingFeelTooltip =>
        $"App classification from inertial touchdown rate: {LandingFeelDisplay}. " +
        "SMOOTH is up to 240 fpm (4 ft/s); FIRM is above 240 fpm. " +
        $"Measurement: {InertialQualityDisplay}.";

    public string SurfaceQualityDisplay
    {
        get
        {
            if (FormatVersion < 4)
            {
                return "LEGACY · LATCH STATUS UNKNOWN";
            }

            if (!LatchUpdateDetected)
            {
                return "LATCH NOT VERIFIED · MAY BE STALE";
            }

            var milliseconds = LatchUpdateOffsetSeconds * 1000.0;
            return Math.Abs(milliseconds) < 0.5
                ? "LATCH VERIFIED · SAME FRAME"
                : $"LATCH VERIFIED · {milliseconds:+0;-0;0} MS";
        }
    }

    public string WeightDisplay => WeightPounds > 0
        ? $"{WeightPounds * 0.45359237:N0} kg"
        : "n/a";

    public string CgDisplay => CgPercent > 0 ? $"{CgPercent:F1}% CG" : "CG n/a";

    public string WindDisplay
    {
        get
        {
            var speed = WindSpeedKnotsAtContact;
            var direction = WindDirectionDegreesAtContact;
            if ((FormatVersion < 7 || (Math.Abs(speed) < 0.000001 && Math.Abs(direction) < 0.000001)) &&
                Series != null && Series.Count > 0)
            {
                var closest = Series[0];
                for (var index = 1; index < Series.Count; index++)
                {
                    if (Math.Abs(Series[index].TimeSeconds) < Math.Abs(closest.TimeSeconds))
                    {
                        closest = Series[index];
                    }
                }

                speed = closest.WindSpeedKnots;
                direction = closest.WindDirectionDegrees;
            }

            if (double.IsNaN(speed) || double.IsInfinity(speed) ||
                double.IsNaN(direction) || double.IsInfinity(direction))
            {
                return "wind n/a";
            }

            direction = (direction % 360.0 + 360.0) % 360.0;
            return $"wind {direction:000}°/{speed:0} kt";
        }
    }

    public string RecordSummaryDisplay => $"Record v{FormatVersion} · {Series.Count} samples";

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        Series ??= new List<LandingSeriesPoint>();
        Engines ??= new List<LandingEngineSeries>();
        ContactPoints ??= new List<LandingContactSeries>();
        ControlInputSources ??= new List<string>();
        RawControllerSourceIndices ??= new List<int>();
    }

    public double PitchInputPercent(LandingSeriesPoint point)
    {
        var storedSourceIndex = StoredRawControllerIndex(RawPitchInputSourceIndex);
        if (!HasRawPitchInput ||
            storedSourceIndex < 0 ||
            point.RawControllerYAxisPercent == null ||
            point.RawControllerYAxisValid == null ||
            storedSourceIndex >= point.RawControllerYAxisPercent.Length ||
            storedSourceIndex >= point.RawControllerYAxisValid.Length ||
            !point.RawControllerYAxisValid[storedSourceIndex])
        {
            return point.PilotPitchPercent;
        }

        var direction = RawPitchInputCorrelation < 0 ? -1.0 : 1.0;
        return direction * point.RawControllerYAxisPercent[storedSourceIndex];
    }

    public int StoredRawControllerIndex(int sourceIndex)
    {
        if (sourceIndex < 0 || RawControllerSourceIndices == null || RawControllerSourceIndices.Count == 0)
        {
            return sourceIndex;
        }

        return RawControllerSourceIndices.IndexOf(sourceIndex);
    }

    private static string FormatSignedMetric(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("+0;-0;0", CultureInfo.CurrentCulture);

    public static IReadOnlyList<LandingRecord> CreateDemoHistory()
    {
        var latest = CreateDemo(
            DateTime.UtcNow.AddMinutes(-18),
            "A350-900",
            "A359",
            "LBSF",
            "09",
            164.0,
            256.8,
            98.6,
            -5.8,
            1.212,
            1.230,
            5.2,
            0.1,
            141);
        return new[]
        {
            latest,
            CreateDemo(DateTime.UtcNow.AddHours(-2), "Fenix A320", "A320", "UUEE", "24R", 293.4, 253.8, -57.5, 17.9, 1.207, 1.271, 6.3, -0.2, 139),
            CreateDemo(DateTime.UtcNow.AddHours(-3), "Fenix A320", "A320", "UUEE", "24R", 504.0, 511.6, 6.3, 1.3, 1.313, 1.504, 7.2, 0.4, 145),
            CreateDemo(DateTime.UtcNow.AddDays(-1), "Fenix A320", "A320", "LBSF", "09", 143.2, 108.0, -41.9, 6.7, 1.047, 1.117, 5.7, 0.1, 137),
        };
    }

    private static LandingRecord CreateDemo(
        DateTime timestamp,
        string aircraft,
        string type,
        string airport,
        string runway,
        double inertial,
        double surface,
        double terrain,
        double unresolved,
        double g150,
        double g2,
        double pitch,
        double bank,
        double airspeed)
    {
        var record = new LandingRecord
        {
            Id = "demo-" + Guid.NewGuid().ToString("N"),
            TimestampUtc = timestamp,
            AircraftTitle = aircraft,
            AircraftType = type,
            Airport = airport,
            Runway = runway,
            InertialFpm = inertial,
            SurfaceFpm = surface,
            SurfaceDeltaFpm = surface - inertial,
            TerrainFpm = terrain,
            UnresolvedFpm = unresolved,
            PeakG150Milliseconds = g150,
            PeakG2Seconds = g2,
            PitchDegrees = pitch,
            BankDegrees = bank,
            AirspeedKnots = airspeed,
            FlareStartSeconds = -6.0,
            WeightPounds = type == "A359" ? 430000 : 136000,
            CgPercent = type == "A359" ? 30.2 : 28.4,
            ApproachGateSeconds = -45.0,
        };

        var engineOne = new LandingEngineSeries { EngineNumber = 1 };
        var engineTwo = new LandingEngineSeries { EngineNumber = 2 };
        var noseGear = new LandingContactSeries { ContactPointIndex = 0 };
        var leftMainGear = new LandingContactSeries { ContactPointIndex = 1 };
        var rightMainGear = new LandingContactSeries { ContactPointIndex = 2 };

        for (var index = 0; index <= 1200; index++)
        {
            var time = -45.0 + index * 0.05;
            var before = Math.Max(0, -time);
            var approach = Math.Max(0, Math.Min(1, before / 8.0));
            var world = time < 0
                ? inertial + (690 - inertial) * approach + 8 * Math.Sin(time * 1.8)
                : Math.Max(0, inertial * Math.Exp(-time * 7.0));
            var indicatedAtContact = inertial + (690 - inertial) / 9.0;
            var indicated = time < 0
                ? inertial + (690 - inertial) * Math.Min(1, (before + 1.0) / 9.0)
                : Math.Max(0, indicatedAtContact * Math.Exp(-time * 1.1));

            var g = 1.0 + (g2 - 1.0) * Math.Exp(-Math.Pow((time - 0.23) / 0.18, 2));
            var agl = time < 0 ? 500.0 * Math.Pow(-time / 45.0, 1.12) : 0;
            var flareProgress = time < -6 ? 0 : Math.Max(0, Math.Min(1, (time + 6) / 6.0));
            var rolloutProgress = Math.Max(0, Math.Min(1, time / 5.0));
            var demoPitch = time < 0 ? 2.2 + (pitch - 2.2) * flareProgress : Math.Max(0.5, pitch - time * 0.9);
            var demoBank = bank + 0.25 * Math.Sin(time * 0.7);
            var pilotPitch = time < 0 ? 4 + 27 * Math.Sin(flareProgress * Math.PI / 2) : Math.Max(-8, 22 - time * 7);
            var pilotRoll = 5 * Math.Sin(time * 1.2);
            var pilotYaw = 3 * Math.Sin(time * 0.8 + 0.5);
            var spoiler = time < 0.15 ? 0 : Math.Min(100, (time - 0.15) * 180);
            var braking = time < 1.5 ? 0 : Math.Min(68, (time - 1.5) * 20);
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = time,
                InertialFpm = world,
                IndicatedFpm = indicated,
                GForce = g,
                AglFeet = agl,
                GroundSpeedKnots = Math.Max(35, airspeed - rolloutProgress * 72),
                PitchDegrees = demoPitch,
                BankDegrees = demoBank,
                AngleOfAttackDegrees = demoPitch + 2.1,
                SideslipDegrees = 0.3 * Math.Sin(time * 0.9),
                PilotRollPercent = pilotRoll,
                PilotPitchPercent = pilotPitch,
                PilotYawPercent = pilotYaw,
                AileronPercent = pilotRoll * 0.78,
                ElevatorPercent = pilotPitch * 0.72,
                RudderPercent = pilotYaw * 0.85,
                SpoilersLeftPercent = spoiler,
                SpoilersRightPercent = spoiler * 0.97,
                FlapsPercent = type == "A359" ? 75 : 100,
                BrakeLeftPercent = braking,
                BrakeRightPercent = braking * 0.98,
                LongitudinalLoadG = time < 1.5 ? 0 : -0.24 * Math.Min(1, (time - 1.5) / 1.2),
                LateralLoadG = 0.025 * Math.Sin(time * 1.1),
                BodyRateXDegreesPerSecond = time < 0 ? 1.4 * Math.Exp(-Math.Pow((time + 2.0) / 2.0, 2)) : -2.0 * Math.Exp(-time),
                BodyRateYDegreesPerSecond = 0.15 * Math.Sin(time),
                BodyRateZDegreesPerSecond = 0.25 * Math.Sin(time * 0.7),
                WindSpeedKnots = 9,
                WindDirectionDegrees = 70,
                OnGround = time >= 0,
            });

            var throttle = time < -3 ? 28 : Math.Max(0, 28 * (-time / 3.0));
            var reverse = time < 1.0 ? 0 : Math.Min(72, (time - 1.0) * 50);
            var n1 = time < 0 ? 31 + throttle * 0.45 : 25 + reverse * 0.65;
            engineOne.Points.Add(new LandingEnginePoint { TimeSeconds = time, ThrottlePercent = throttle, N1Percent = n1, Rpm = n1 * 58, ReversePercent = reverse });
            engineTwo.Points.Add(new LandingEnginePoint { TimeSeconds = time, ThrottlePercent = throttle * 0.99, N1Percent = n1 * 1.01, Rpm = n1 * 59, ReversePercent = reverse * 0.98 });

            var mainCompression = time < 0 ? 0 : Math.Min(38, 8 + 30 * (1 - Math.Exp(-time * 3.5)));
            var noseOnGround = time >= 3.2;
            var noseCompression = noseOnGround ? Math.Min(24, (time - 3.2) * 28) : 0;
            leftMainGear.Points.Add(new LandingContactPoint { TimeSeconds = time, CompressionPercent = mainCompression, PositionPercent = 100, OnGround = time >= 0 });
            rightMainGear.Points.Add(new LandingContactPoint { TimeSeconds = time, CompressionPercent = mainCompression * 0.96, PositionPercent = 100, OnGround = time >= 0 });
            noseGear.Points.Add(new LandingContactPoint { TimeSeconds = time, CompressionPercent = noseCompression, PositionPercent = 100, OnGround = noseOnGround });
        }

        record.Engines.Add(engineOne);
        record.Engines.Add(engineTwo);
        record.ContactPoints.Add(noseGear);
        record.ContactPoints.Add(leftMainGear);
        record.ContactPoints.Add(rightMainGear);

        return record;
    }
}
