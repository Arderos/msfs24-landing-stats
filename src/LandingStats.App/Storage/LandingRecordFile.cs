using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using LandingStats.App.Models;

namespace LandingStats.App.Storage;

[DataContract(Name = "landing-v7")]
internal sealed class LandingRecordFile
{
    [DataMember(Name = "layout", Order = 1)]
    public int LayoutVersion { get; set; } = 7;

    [DataMember(Name = "summary", Order = 2)]
    public LandingRecord Summary { get; set; } = new LandingRecord();

    [DataMember(Name = "series", Order = 3)]
    public LandingSeriesColumns Series { get; set; } = new LandingSeriesColumns();

    [DataMember(Name = "engines", Order = 4)]
    public List<LandingEngineColumns> Engines { get; set; } = new List<LandingEngineColumns>();

    [DataMember(Name = "contacts", Order = 5)]
    public List<LandingContactColumns> Contacts { get; set; } = new List<LandingContactColumns>();

    public static LandingRecordFile FromRecord(LandingRecord record)
    {
        var file = new LandingRecordFile
        {
            Summary = SanitizedSummaryCopy(record),
            Series = LandingSeriesColumns.FromPoints(record.Series),
        };
        file.Summary.IsSummaryOnly = false;
        foreach (var engine in record.Engines)
        {
            file.Engines.Add(LandingEngineColumns.FromSeries(engine));
        }

        foreach (var contact in record.ContactPoints)
        {
            file.Contacts.Add(LandingContactColumns.FromSeries(contact));
        }

        file.Validate();
        return file;
    }

    public LandingRecord ToRecord()
    {
        if (LayoutVersion != 7)
        {
            throw new InvalidDataException($"Unsupported landing detail layout {LayoutVersion}.");
        }

        Validate();
        var record = Summary ?? new LandingRecord();
        RestoreSummary(record);
        record.IsSummaryOnly = false;
        record.Series = (Series ?? new LandingSeriesColumns()).ToPoints();
        record.Engines = new List<LandingEngineSeries>();
        foreach (var engine in Engines ?? new List<LandingEngineColumns>())
        {
            record.Engines.Add(engine.ToSeries());
        }

        record.ContactPoints = new List<LandingContactSeries>();
        foreach (var contact in Contacts ?? new List<LandingContactColumns>())
        {
            record.ContactPoints.Add(contact.ToSeries());
        }

        return record;
    }

    internal static LandingRecord SanitizedSummaryCopy(LandingRecord source)
    {
        var summary = LandingRepository.CreateSummary(source);
        summary.InertialFpm = Store(summary.InertialFpm);
        summary.SurfaceFpm = Store(summary.SurfaceFpm);
        summary.SurfaceDeltaFpm = Store(summary.SurfaceDeltaFpm);
        summary.TerrainFpm = Store(summary.TerrainFpm);
        summary.UnresolvedFpm = Store(summary.UnresolvedFpm);
        summary.PeakG150Milliseconds = Store(summary.PeakG150Milliseconds);
        summary.PeakG2Seconds = Store(summary.PeakG2Seconds);
        summary.PitchDegrees = Store(summary.PitchDegrees);
        summary.BankDegrees = Store(summary.BankDegrees);
        summary.AirspeedKnots = Store(summary.AirspeedKnots);
        summary.FlareStartSeconds = Store(summary.FlareStartSeconds);
        summary.WeightPounds = Store(summary.WeightPounds);
        summary.CgPercent = Store(summary.CgPercent);
        summary.ApproachGateSeconds = Store(summary.ApproachGateSeconds);
        summary.TouchdownLatitudeDegrees = Store(summary.TouchdownLatitudeDegrees);
        summary.TouchdownLongitudeDegrees = Store(summary.TouchdownLongitudeDegrees);
        summary.AirportDistanceNauticalMiles = Store(summary.AirportDistanceNauticalMiles);
        summary.InertialFitDurationSeconds = Store(summary.InertialFitDurationSeconds);
        summary.LatchUpdateOffsetSeconds = Store(summary.LatchUpdateOffsetSeconds);
        summary.GroundSpeedKnots = Store(summary.GroundSpeedKnots);
        summary.AngleOfAttackDegrees = Store(summary.AngleOfAttackDegrees);
        summary.RawPitchInputCorrelation = Store(summary.RawPitchInputCorrelation);
        summary.RawPitchInputLagSeconds = Store(summary.RawPitchInputLagSeconds);
        summary.WindSpeedKnotsAtContact = Store(summary.WindSpeedKnotsAtContact);
        summary.WindDirectionDegreesAtContact = Store(summary.WindDirectionDegreesAtContact);
        summary.ReconstructedClosureFpm = Store(summary.ReconstructedClosureFpm);
        summary.ReconstructedInertialFpm = Store(summary.ReconstructedInertialFpm);
        summary.ReconstructedTerrainFpm = Store(summary.ReconstructedTerrainFpm);
        summary.ReconstructedPitchFpm = Store(summary.ReconstructedPitchFpm);
        summary.ClosureReconstructionResidualFpm = Store(summary.ClosureReconstructionResidualFpm);
        summary.ClosureReconstructionUncertaintyFpm = Store(summary.ClosureReconstructionUncertaintyFpm);
        summary.ClosureReconstructionLongitudinalArmFeet = Store(summary.ClosureReconstructionLongitudinalArmFeet);
        summary.ClosureReconstructionGeometryQuality = Store(summary.ClosureReconstructionGeometryQuality);
        return summary;
    }

    internal static void RestoreSummary(LandingRecord summary)
    {
        summary.InertialFpm = Restore(summary.InertialFpm);
        summary.SurfaceFpm = Restore(summary.SurfaceFpm);
        summary.SurfaceDeltaFpm = Restore(summary.SurfaceDeltaFpm);
        summary.TerrainFpm = Restore(summary.TerrainFpm);
        summary.UnresolvedFpm = Restore(summary.UnresolvedFpm);
        summary.PeakG150Milliseconds = Restore(summary.PeakG150Milliseconds);
        summary.PeakG2Seconds = Restore(summary.PeakG2Seconds);
        summary.PitchDegrees = Restore(summary.PitchDegrees);
        summary.BankDegrees = Restore(summary.BankDegrees);
        summary.AirspeedKnots = Restore(summary.AirspeedKnots);
        summary.FlareStartSeconds = Restore(summary.FlareStartSeconds);
        summary.WeightPounds = Restore(summary.WeightPounds);
        summary.CgPercent = Restore(summary.CgPercent);
        summary.ApproachGateSeconds = Restore(summary.ApproachGateSeconds);
        summary.TouchdownLatitudeDegrees = Restore(summary.TouchdownLatitudeDegrees);
        summary.TouchdownLongitudeDegrees = Restore(summary.TouchdownLongitudeDegrees);
        summary.AirportDistanceNauticalMiles = Restore(summary.AirportDistanceNauticalMiles);
        summary.InertialFitDurationSeconds = Restore(summary.InertialFitDurationSeconds);
        summary.LatchUpdateOffsetSeconds = Restore(summary.LatchUpdateOffsetSeconds);
        summary.GroundSpeedKnots = Restore(summary.GroundSpeedKnots);
        summary.AngleOfAttackDegrees = Restore(summary.AngleOfAttackDegrees);
        summary.RawPitchInputCorrelation = Restore(summary.RawPitchInputCorrelation);
        summary.RawPitchInputLagSeconds = Restore(summary.RawPitchInputLagSeconds);
        summary.WindSpeedKnotsAtContact = Restore(summary.WindSpeedKnotsAtContact);
        summary.WindDirectionDegreesAtContact = Restore(summary.WindDirectionDegreesAtContact);
        summary.ReconstructedClosureFpm = Restore(summary.ReconstructedClosureFpm);
        summary.ReconstructedInertialFpm = Restore(summary.ReconstructedInertialFpm);
        summary.ReconstructedTerrainFpm = Restore(summary.ReconstructedTerrainFpm);
        summary.ReconstructedPitchFpm = Restore(summary.ReconstructedPitchFpm);
        summary.ClosureReconstructionResidualFpm = Restore(summary.ClosureReconstructionResidualFpm);
        summary.ClosureReconstructionUncertaintyFpm = Restore(summary.ClosureReconstructionUncertaintyFpm);
        summary.ClosureReconstructionLongitudinalArmFeet = Restore(summary.ClosureReconstructionLongitudinalArmFeet);
        summary.ClosureReconstructionGeometryQuality = Restore(summary.ClosureReconstructionGeometryQuality);
    }

    internal static double Store(double value) => double.IsNaN(value) || double.IsInfinity(value) ? LandingRecord.NonFiniteStorageSentinel : value;
    internal static double Restore(double value) => value == LandingRecord.NonFiniteStorageSentinel ? double.NaN : value;
    private static double? Store(double? value) => value.HasValue ? Store(value.Value) : value;
    private static double? Restore(double? value) => value.HasValue ? Restore(value.Value) : value;

    private void Validate()
    {
        if (Summary == null)
        {
            throw new InvalidDataException("Landing detail summary is missing.");
        }

        Series?.Validate(Summary?.RawControllerSourceIndices?.Count ?? 0);
        foreach (var engine in Engines ?? new List<LandingEngineColumns>()) engine.Validate();
        foreach (var contact in Contacts ?? new List<LandingContactColumns>()) contact.Validate();
    }
}

[DataContract]
internal sealed class LandingSeriesColumns
{
    [DataMember(Name = "t", Order = 1)] public double[] Time { get; set; } = Array.Empty<double>();
    [DataMember(Name = "iv", Order = 2)] public double[] Inertial { get; set; } = Array.Empty<double>();
    [DataMember(Name = "vsi", Order = 3)] public double[] Indicated { get; set; } = Array.Empty<double>();
    [DataMember(Name = "g", Order = 4)] public double[] G { get; set; } = Array.Empty<double>();
    [DataMember(Name = "agl", Order = 5)] public double[] Agl { get; set; } = Array.Empty<double>();
    [DataMember(Name = "gs", Order = 6)] public double[] GroundSpeed { get; set; } = Array.Empty<double>();
    [DataMember(Name = "pitch", Order = 7)] public double[] Pitch { get; set; } = Array.Empty<double>();
    [DataMember(Name = "bank", Order = 8)] public double[] Bank { get; set; } = Array.Empty<double>();
    [DataMember(Name = "aoa", Order = 9)] public double[] Aoa { get; set; } = Array.Empty<double>();
    [DataMember(Name = "beta", Order = 10)] public double[] Sideslip { get; set; } = Array.Empty<double>();
    [DataMember(Name = "cmdR", Order = 11)] public double[] PilotRoll { get; set; } = Array.Empty<double>();
    [DataMember(Name = "cmdP", Order = 12)] public double[] PilotPitch { get; set; } = Array.Empty<double>();
    [DataMember(Name = "cmdY", Order = 13)] public double[] PilotYaw { get; set; } = Array.Empty<double>();
    [DataMember(Name = "ail", Order = 14)] public double[] Aileron { get; set; } = Array.Empty<double>();
    [DataMember(Name = "elev", Order = 15)] public double[] Elevator { get; set; } = Array.Empty<double>();
    [DataMember(Name = "rud", Order = 16)] public double[] Rudder { get; set; } = Array.Empty<double>();
    [DataMember(Name = "splL", Order = 17)] public double[] SpoilersLeft { get; set; } = Array.Empty<double>();
    [DataMember(Name = "splR", Order = 18)] public double[] SpoilersRight { get; set; } = Array.Empty<double>();
    [DataMember(Name = "flap", Order = 19)] public double[] Flaps { get; set; } = Array.Empty<double>();
    [DataMember(Name = "brkL", Order = 20)] public double[] BrakeLeft { get; set; } = Array.Empty<double>();
    [DataMember(Name = "brkR", Order = 21)] public double[] BrakeRight { get; set; } = Array.Empty<double>();
    [DataMember(Name = "longG", Order = 22)] public double[] LongitudinalG { get; set; } = Array.Empty<double>();
    [DataMember(Name = "latG", Order = 23)] public double[] LateralG { get; set; } = Array.Empty<double>();
    [DataMember(Name = "rateX", Order = 24)] public double[] RateX { get; set; } = Array.Empty<double>();
    [DataMember(Name = "rateY", Order = 25)] public double[] RateY { get; set; } = Array.Empty<double>();
    [DataMember(Name = "rateZ", Order = 26)] public double[] RateZ { get; set; } = Array.Empty<double>();
    [DataMember(Name = "windV", Order = 27)] public double[] WindSpeed { get; set; } = Array.Empty<double>();
    [DataMember(Name = "windD", Order = 28)] public double[] WindDirection { get; set; } = Array.Empty<double>();
    [DataMember(Name = "ground", Order = 29)] public bool[] OnGround { get; set; } = Array.Empty<bool>();
    [DataMember(Name = "accLat", Order = 30)] public double[] LateralAcceleration { get; set; } = Array.Empty<double>();
    [DataMember(Name = "accLong", Order = 31)] public double[] LongitudinalAcceleration { get; set; } = Array.Empty<double>();
    [DataMember(Name = "defElev", Order = 32)] public double[] ElevatorDeflection { get; set; } = Array.Empty<double>();
    [DataMember(Name = "defAilL", Order = 33)] public double[] AileronLeftDeflection { get; set; } = Array.Empty<double>();
    [DataMember(Name = "defAilR", Order = 34)] public double[] AileronRightDeflection { get; set; } = Array.Empty<double>();
    [DataMember(Name = "defRud", Order = 35)] public double[] RudderDeflection { get; set; } = Array.Empty<double>();
    [DataMember(Name = "axisElev", Order = 36)] public double[] AxisElevator { get; set; } = Array.Empty<double>();
    [DataMember(Name = "axisElevOk", Order = 37)] public bool[] AxisElevatorValid { get; set; } = Array.Empty<bool>();
    [DataMember(Name = "axisElevAge", Order = 38)] public double[] AxisElevatorAge { get; set; } = Array.Empty<double>();
    [DataMember(Name = "rawY", Order = 39)] public double[][] RawY { get; set; } = Array.Empty<double[]>();
    [DataMember(Name = "rawYOk", Order = 40)] public bool[][] RawYValid { get; set; } = Array.Empty<bool[]>();
    [DataMember(Name = "rawYAge", Order = 41)] public double[][] RawYAge { get; set; } = Array.Empty<double[]>();

    public static LandingSeriesColumns FromPoints(IReadOnlyList<LandingSeriesPoint> points)
    {
        var value = new LandingSeriesColumns();
        value.Allocate(points.Count);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            value.Time[index] = point.TimeSeconds;
            value.Inertial[index] = point.InertialFpm;
            value.Indicated[index] = point.IndicatedFpm;
            value.G[index] = point.GForce;
            value.Agl[index] = point.AglFeet;
            value.GroundSpeed[index] = point.GroundSpeedKnots;
            value.Pitch[index] = point.PitchDegrees;
            value.Bank[index] = point.BankDegrees;
            value.Aoa[index] = point.AngleOfAttackDegrees;
            value.Sideslip[index] = point.SideslipDegrees;
            value.PilotRoll[index] = point.PilotRollPercent;
            value.PilotPitch[index] = point.PilotPitchPercent;
            value.PilotYaw[index] = point.PilotYawPercent;
            value.Aileron[index] = point.AileronPercent;
            value.Elevator[index] = point.ElevatorPercent;
            value.Rudder[index] = point.RudderPercent;
            value.SpoilersLeft[index] = point.SpoilersLeftPercent;
            value.SpoilersRight[index] = point.SpoilersRightPercent;
            value.Flaps[index] = point.FlapsPercent;
            value.BrakeLeft[index] = point.BrakeLeftPercent;
            value.BrakeRight[index] = point.BrakeRightPercent;
            value.LongitudinalG[index] = point.LongitudinalLoadG;
            value.LateralG[index] = point.LateralLoadG;
            value.RateX[index] = point.BodyRateXDegreesPerSecond;
            value.RateY[index] = point.BodyRateYDegreesPerSecond;
            value.RateZ[index] = point.BodyRateZDegreesPerSecond;
            value.WindSpeed[index] = point.WindSpeedKnots;
            value.WindDirection[index] = point.WindDirectionDegrees;
            value.OnGround[index] = point.OnGround;
            value.LateralAcceleration[index] = point.LateralAccelerationFps2;
            value.LongitudinalAcceleration[index] = point.LongitudinalAccelerationFps2;
            value.ElevatorDeflection[index] = point.ElevatorDeflectionPercent;
            value.AileronLeftDeflection[index] = point.AileronLeftDeflectionPercent;
            value.AileronRightDeflection[index] = point.AileronRightDeflectionPercent;
            value.RudderDeflection[index] = point.RudderDeflectionPercent;
            value.AxisElevator[index] = point.AxisElevatorSetPercent;
            value.AxisElevatorValid[index] = point.AxisElevatorSetValid;
            value.AxisElevatorAge[index] = point.AxisElevatorSetAgeSeconds;
            value.RawY[index] = SanitizedCopy(point.RawControllerYAxisPercent);
            value.RawYValid[index] = point.RawControllerYAxisValid ?? Array.Empty<bool>();
            value.RawYAge[index] = SanitizedCopy(point.RawControllerYAxisAgeSeconds);
        }

        value.SanitizeColumns();
        value.Validate(points.Count == 0 ? 0 : value.RawY[0].Length);
        return value;
    }

    public List<LandingSeriesPoint> ToPoints()
    {
        var count = Time?.Length ?? 0;
        var points = new List<LandingSeriesPoint>(count);
        for (var index = 0; index < count; index++)
        {
            points.Add(new LandingSeriesPoint
            {
                TimeSeconds = Get(Time, index), InertialFpm = Get(Inertial, index), IndicatedFpm = Get(Indicated, index),
                GForce = Get(G, index), AglFeet = Get(Agl, index), GroundSpeedKnots = Get(GroundSpeed, index),
                PitchDegrees = Get(Pitch, index), BankDegrees = Get(Bank, index), AngleOfAttackDegrees = Get(Aoa, index),
                SideslipDegrees = Get(Sideslip, index), PilotRollPercent = Get(PilotRoll, index), PilotPitchPercent = Get(PilotPitch, index),
                PilotYawPercent = Get(PilotYaw, index), AileronPercent = Get(Aileron, index), ElevatorPercent = Get(Elevator, index),
                RudderPercent = Get(Rudder, index), SpoilersLeftPercent = Get(SpoilersLeft, index), SpoilersRightPercent = Get(SpoilersRight, index),
                FlapsPercent = Get(Flaps, index), BrakeLeftPercent = Get(BrakeLeft, index), BrakeRightPercent = Get(BrakeRight, index),
                LongitudinalLoadG = Get(LongitudinalG, index), LateralLoadG = Get(LateralG, index),
                BodyRateXDegreesPerSecond = Get(RateX, index), BodyRateYDegreesPerSecond = Get(RateY, index), BodyRateZDegreesPerSecond = Get(RateZ, index),
                WindSpeedKnots = Get(WindSpeed, index), WindDirectionDegrees = Get(WindDirection, index), OnGround = Get(OnGround, index),
                LateralAccelerationFps2 = Get(LateralAcceleration, index), LongitudinalAccelerationFps2 = Get(LongitudinalAcceleration, index),
                ElevatorDeflectionPercent = Get(ElevatorDeflection, index), AileronLeftDeflectionPercent = Get(AileronLeftDeflection, index),
                AileronRightDeflectionPercent = Get(AileronRightDeflection, index), RudderDeflectionPercent = Get(RudderDeflection, index),
                AxisElevatorSetPercent = Get(AxisElevator, index), AxisElevatorSetValid = Get(AxisElevatorValid, index),
                AxisElevatorSetAgeSeconds = Get(AxisElevatorAge, index), RawControllerYAxisPercent = RestoredCopy(Get(RawY, index)),
                RawControllerYAxisValid = Get(RawYValid, index), RawControllerYAxisAgeSeconds = RestoredCopy(Get(RawYAge, index)),
            });
        }

        return points;
    }

    private void Allocate(int count)
    {
        Time = new double[count]; Inertial = new double[count]; Indicated = new double[count]; G = new double[count]; Agl = new double[count];
        GroundSpeed = new double[count]; Pitch = new double[count]; Bank = new double[count]; Aoa = new double[count]; Sideslip = new double[count];
        PilotRoll = new double[count]; PilotPitch = new double[count]; PilotYaw = new double[count]; Aileron = new double[count]; Elevator = new double[count];
        Rudder = new double[count]; SpoilersLeft = new double[count]; SpoilersRight = new double[count]; Flaps = new double[count]; BrakeLeft = new double[count];
        BrakeRight = new double[count]; LongitudinalG = new double[count]; LateralG = new double[count]; RateX = new double[count]; RateY = new double[count];
        RateZ = new double[count]; WindSpeed = new double[count]; WindDirection = new double[count]; OnGround = new bool[count];
        LateralAcceleration = new double[count]; LongitudinalAcceleration = new double[count]; ElevatorDeflection = new double[count];
        AileronLeftDeflection = new double[count]; AileronRightDeflection = new double[count]; RudderDeflection = new double[count];
        AxisElevator = new double[count]; AxisElevatorValid = new bool[count]; AxisElevatorAge = new double[count];
        RawY = new double[count][]; RawYValid = new bool[count][]; RawYAge = new double[count][];
    }

    public void Validate(int expectedRawWidth = 0)
    {
        var count = Time?.Length ?? 0;
        foreach (var length in ColumnLengths())
        {
            if (length != count)
            {
                throw new InvalidDataException($"Landing series column length {length} does not match time column length {count}.");
            }
        }

        for (var index = 0; index < count; index++)
        {
            var values = RawY[index] ?? Array.Empty<double>();
            var valid = RawYValid[index] ?? Array.Empty<bool>();
            var ages = RawYAge[index] ?? Array.Empty<double>();
            if (values.Length != valid.Length || values.Length != ages.Length || values.Length != expectedRawWidth)
            {
                throw new InvalidDataException($"Raw-controller columns at series index {index} do not match the declared width {expectedRawWidth}.");
            }
        }
    }

    private IEnumerable<int> ColumnLengths()
    {
        foreach (var values in DoubleColumns()) yield return values?.Length ?? 0;
        yield return OnGround?.Length ?? 0;
        yield return AxisElevatorValid?.Length ?? 0;
        yield return RawY?.Length ?? 0;
        yield return RawYValid?.Length ?? 0;
        yield return RawYAge?.Length ?? 0;
    }

    private IEnumerable<double[]> DoubleColumns()
    {
        yield return Time; yield return Inertial; yield return Indicated; yield return G; yield return Agl; yield return GroundSpeed;
        yield return Pitch; yield return Bank; yield return Aoa; yield return Sideslip; yield return PilotRoll; yield return PilotPitch;
        yield return PilotYaw; yield return Aileron; yield return Elevator; yield return Rudder; yield return SpoilersLeft; yield return SpoilersRight;
        yield return Flaps; yield return BrakeLeft; yield return BrakeRight; yield return LongitudinalG; yield return LateralG; yield return RateX;
        yield return RateY; yield return RateZ; yield return WindSpeed; yield return WindDirection; yield return LateralAcceleration;
        yield return LongitudinalAcceleration; yield return ElevatorDeflection; yield return AileronLeftDeflection; yield return AileronRightDeflection;
        yield return RudderDeflection; yield return AxisElevator; yield return AxisElevatorAge;
    }

    private void SanitizeColumns()
    {
        foreach (var values in DoubleColumns())
        {
            for (var index = 0; index < values.Length; index++) values[index] = LandingRecordFile.Store(values[index]);
        }
    }

    private static double[] SanitizedCopy(double[]? values)
    {
        if (values == null) return Array.Empty<double>();
        var copy = new double[values.Length];
        for (var index = 0; index < values.Length; index++) copy[index] = LandingRecordFile.Store(values[index]);
        return copy;
    }

    private static double[] RestoredCopy(double[] values)
    {
        var copy = new double[values.Length];
        for (var index = 0; index < values.Length; index++) copy[index] = LandingRecordFile.Restore(values[index]);
        return copy;
    }

    private static double Get(double[]? values, int index) => values != null && index < values.Length ? LandingRecordFile.Restore(values[index]) : 0;
    private static bool Get(bool[]? values, int index) => values != null && index < values.Length && values[index];
    private static double[] Get(double[][]? values, int index) => values != null && index < values.Length ? values[index] ?? Array.Empty<double>() : Array.Empty<double>();
    private static bool[] Get(bool[][]? values, int index) => values != null && index < values.Length ? values[index] ?? Array.Empty<bool>() : Array.Empty<bool>();
}

[DataContract]
internal sealed class LandingEngineColumns
{
    [DataMember(Name = "n", Order = 1)] public int Number { get; set; }
    [DataMember(Name = "t", Order = 2)] public double[] Time { get; set; } = Array.Empty<double>();
    [DataMember(Name = "thr", Order = 3)] public double[] Throttle { get; set; } = Array.Empty<double>();
    [DataMember(Name = "n1", Order = 4)] public double[] N1 { get; set; } = Array.Empty<double>();
    [DataMember(Name = "rpm", Order = 5)] public double[] Rpm { get; set; } = Array.Empty<double>();
    [DataMember(Name = "rev", Order = 6)] public double[] Reverse { get; set; } = Array.Empty<double>();

    public static LandingEngineColumns FromSeries(LandingEngineSeries series)
    {
        var value = new LandingEngineColumns { Number = series.EngineNumber };
        var count = series.Points.Count;
        value.Time = new double[count]; value.Throttle = new double[count]; value.N1 = new double[count]; value.Rpm = new double[count]; value.Reverse = new double[count];
        for (var index = 0; index < count; index++)
        {
            var point = series.Points[index];
            value.Time[index] = point.TimeSeconds; value.Throttle[index] = point.ThrottlePercent; value.N1[index] = point.N1Percent;
            value.Rpm[index] = point.Rpm; value.Reverse[index] = point.ReversePercent;
        }
        Sanitize(value.Time); Sanitize(value.Throttle); Sanitize(value.N1); Sanitize(value.Rpm); Sanitize(value.Reverse);
        value.Validate();
        return value;
    }

    public LandingEngineSeries ToSeries()
    {
        var series = new LandingEngineSeries { EngineNumber = Number };
        for (var index = 0; index < (Time?.Length ?? 0); index++)
        {
            series.Points.Add(new LandingEnginePoint
            {
                TimeSeconds = Get(Time, index), ThrottlePercent = Get(Throttle, index), N1Percent = Get(N1, index),
                Rpm = Get(Rpm, index), ReversePercent = Get(Reverse, index),
            });
        }
        return series;
    }

    public void Validate()
    {
        var count = Time?.Length ?? 0;
        if ((Throttle?.Length ?? 0) != count || (N1?.Length ?? 0) != count || (Rpm?.Length ?? 0) != count || (Reverse?.Length ?? 0) != count)
            throw new InvalidDataException("Engine series columns have different lengths.");
    }

    private static void Sanitize(double[] values) { for (var index = 0; index < values.Length; index++) values[index] = LandingRecordFile.Store(values[index]); }
    private static double Get(double[]? values, int index) => values != null && index < values.Length ? LandingRecordFile.Restore(values[index]) : 0;
}

[DataContract]
internal sealed class LandingContactColumns
{
    [DataMember(Name = "n", Order = 1)] public int Index { get; set; }
    [DataMember(Name = "t", Order = 2)] public double[] Time { get; set; } = Array.Empty<double>();
    [DataMember(Name = "c", Order = 3)] public double[] Compression { get; set; } = Array.Empty<double>();
    [DataMember(Name = "p", Order = 4)] public double[] Position { get; set; } = Array.Empty<double>();
    [DataMember(Name = "g", Order = 5)] public bool[] OnGround { get; set; } = Array.Empty<bool>();

    public static LandingContactColumns FromSeries(LandingContactSeries series)
    {
        var value = new LandingContactColumns { Index = series.ContactPointIndex };
        var count = series.Points.Count;
        value.Time = new double[count]; value.Compression = new double[count]; value.Position = new double[count]; value.OnGround = new bool[count];
        for (var index = 0; index < count; index++)
        {
            var point = series.Points[index];
            value.Time[index] = point.TimeSeconds; value.Compression[index] = point.CompressionPercent;
            value.Position[index] = point.PositionPercent; value.OnGround[index] = point.OnGround;
        }
        Sanitize(value.Time); Sanitize(value.Compression); Sanitize(value.Position);
        value.Validate();
        return value;
    }

    public LandingContactSeries ToSeries()
    {
        var series = new LandingContactSeries { ContactPointIndex = Index };
        for (var index = 0; index < (Time?.Length ?? 0); index++)
        {
            series.Points.Add(new LandingContactPoint
            {
                TimeSeconds = Get(Time, index), CompressionPercent = Get(Compression, index),
                PositionPercent = Get(Position, index), OnGround = Get(OnGround, index),
            });
        }
        return series;
    }

    public void Validate()
    {
        var count = Time?.Length ?? 0;
        if ((Compression?.Length ?? 0) != count || (Position?.Length ?? 0) != count || (OnGround?.Length ?? 0) != count)
            throw new InvalidDataException("Contact series columns have different lengths.");
    }

    private static void Sanitize(double[] values) { for (var index = 0; index < values.Length; index++) values[index] = LandingRecordFile.Store(values[index]); }
    private static double Get(double[]? values, int index) => values != null && index < values.Length ? LandingRecordFile.Restore(values[index]) : 0;
    private static bool Get(bool[]? values, int index) => values != null && index < values.Length && values[index];
}
