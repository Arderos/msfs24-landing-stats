using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using LandingStats.App.Settings;
using LandingStats.Core;

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

    [DataMember(Order = 45)]
    public string ClosureReconstructionModel { get; set; } = string.Empty;

    [DataMember(Order = 46)]
    public bool ClosureReconstructionAvailable { get; set; }

    [DataMember(Order = 47)]
    public double ReconstructedClosureFpm { get; set; } = double.NaN;

    [DataMember(Order = 48)]
    public double ReconstructedInertialFpm { get; set; } = double.NaN;

    [DataMember(Order = 49)]
    public double ReconstructedTerrainFpm { get; set; } = double.NaN;

    [DataMember(Order = 50)]
    public double ReconstructedPitchFpm { get; set; } = double.NaN;

    [DataMember(Order = 51)]
    public double ClosureReconstructionResidualFpm { get; set; } = double.NaN;

    [DataMember(Order = 52)]
    public double ClosureReconstructionUncertaintyFpm { get; set; } = double.NaN;

    [DataMember(Order = 53)]
    public int ClosureReconstructionFitPointCount { get; set; }

    [DataMember(Order = 54)]
    public double ClosureReconstructionLongitudinalArmFeet { get; set; } = double.NaN;

    [DataMember(Order = 55)]
    public double ClosureReconstructionGeometryQuality { get; set; } = double.NaN;

    [DataMember(Order = 56)]
    public bool ClosureReconstructionArmRecoveredFromTelemetry { get; set; }

    [DataMember(Order = 57, EmitDefaultValue = false)]
    public string ClosureReconstructionGeometrySource { get; set; } = string.Empty;

    [IgnoreDataMember]
    public bool IsSummaryOnly { get; set; }

    public string TimestampDisplay => TimestampUtc.ToLocalTime().ToString("dd MMM · HH:mm", CultureInfo.CurrentCulture);

    public string LocationDisplay => Runway == "—"
        ? Airport
        : LocalizationManager.Format("Model.LocationRunwayFormat", Airport, Runway);

    public string InertialDisplay => $"{-InertialFpm:+0;-0;0}";

    public bool HasSurfaceLatchData => !double.IsNaN(SurfaceFpm);

    public bool HasClosureReconstruction =>
        ClosureReconstructionAvailable &&
        IsFinite(ReconstructedClosureFpm);

    public string ClosureModeledDisplay => HasClosureReconstruction
        ? $"{-ReconstructedClosureFpm:+0;-0;0} fpm"
        : LocalizationManager.Text("Model.NotAvailable");

    public string ClosureResidualDisplay => HasClosureReconstruction && IsFinite(ClosureReconstructionResidualFpm)
        ? $"{-ClosureReconstructionResidualFpm:+0;-0;0} fpm"
        : LocalizationManager.Text("Model.NotAvailable");

    public string ClosureComponentsDisplay => HasClosureReconstruction
        ? LocalizationManager.Format(
            "Model.ComponentsFormat",
            FormatSignedMetric(-ReconstructedInertialFpm),
            FormatSignedMetric(-ReconstructedTerrainFpm),
            FormatSignedMetric(-ReconstructedPitchFpm))
        : LocalizationManager.Text("Model.ReconstructionUnavailable");

    public string ClosureUncertaintyDisplay => HasClosureReconstruction &&
                                               IsFinite(ClosureReconstructionUncertaintyFpm)
        ? $"±{Math.Abs(ClosureReconstructionUncertaintyFpm):0} fpm"
        : LocalizationManager.Text("Model.NotAvailable");

    public string ClosureGeometryDisplay
    {
        get
        {
            if (!HasClosureReconstruction || !IsFinite(ClosureReconstructionLongitudinalArmFeet))
            {
                return LocalizationManager.Text("Model.GeometryUnavailable");
            }

            var sourceKey = ClosureReconstructionGeometrySource;
            if (string.IsNullOrWhiteSpace(sourceKey) ||
                string.Equals(
                    sourceKey,
                    nameof(TouchdownGeometrySource.Unavailable),
                    StringComparison.Ordinal))
            {
                sourceKey = ClosureReconstructionArmRecoveredFromTelemetry
                    ? nameof(TouchdownGeometrySource.Telemetry)
                    : nameof(TouchdownGeometrySource.Provided);
            }

            var source = LocalizationManager.Text(
                string.Equals(sourceKey, nameof(TouchdownGeometrySource.FlightModelConfig), StringComparison.Ordinal)
                    ? "Model.FlightModelConfig"
                    : string.Equals(sourceKey, nameof(TouchdownGeometrySource.Telemetry), StringComparison.Ordinal)
                        ? "Model.Telemetry"
                        : "Model.Provided");
            var quality = string.Equals(
                              sourceKey,
                              nameof(TouchdownGeometrySource.Telemetry),
                              StringComparison.Ordinal) &&
                          IsFinite(ClosureReconstructionGeometryQuality)
                ? LocalizationManager.Format("Model.GeometryQualityFormat", ClosureReconstructionGeometryQuality)
                : string.Empty;
            return LocalizationManager.Format(
                "Model.GeometryFormat",
                ClosureReconstructionLongitudinalArmFeet,
                source,
                quality);
        }
    }

    public string ClosureModelDisplay => HasClosureReconstruction &&
                                         !string.IsNullOrWhiteSpace(ClosureReconstructionModel)
        ? ClosureReconstructionModel
        : LocalizationManager.Text("Model.ReconstructionUnavailable");

    public string SurfaceDisplay => HasSurfaceLatchData
        ? $"{-SurfaceFpm:+0;-0;0} fpm"
        : LocalizationManager.Text("Model.NotAvailable");

    public string SurfaceValueDisplay => HasSurfaceLatchData
        ? $"{-SurfaceFpm:+0;-0;0}"
        : LocalizationManager.Text("Model.NotAvailable");

    public string SurfaceUnitDisplay => HasSurfaceLatchData ? "fpm" : string.Empty;

    public string DeltaDisplay => !double.IsNaN(SurfaceDeltaFpm)
        ? $"{-SurfaceDeltaFpm:+0;-0;0} fpm"
        : LocalizationManager.Text("Model.SurfaceDeltaNa");

    public string TerrainMetricDisplay => LocalizationManager.Format("Model.TerrainFormat", FormatSignedMetric(-TerrainFpm));

    public string UnresolvedMetricDisplay => LocalizationManager.Format("Model.UnresolvedFormat", FormatSignedMetric(-UnresolvedFpm));

    public string G150Display => PeakG150Milliseconds.ToString("F2", CultureInfo.CurrentCulture);

    public string G2SecondsDisplay => LocalizationManager.Format("Model.Peak2Format", PeakG2Seconds);

    public string AttitudeDisplay => $"{-PitchDegrees:+0.0;-0.0;0.0}° / {BankDegrees:+0.0;-0.0;0.0}°";

    public string AirspeedDisplay => LocalizationManager.Format("Model.SpeedKnotsFormat", AirspeedKnots);

    public string GroundSpeedDisplay => LocalizationManager.Format("Model.SpeedKnotsFormat", GroundSpeedKnots);

    public string AngleOfAttackDisplay => LocalizationManager.Format("Model.AoaFormat", AngleOfAttackDegrees);

    public string ContactDisplay => ContactCount > 1
        ? LocalizationManager.Format("Model.ContactMultipleFormat", ContactNumber, ContactCount)
        : LocalizationManager.Text("Model.ContactSingle");

    public bool HasTouchdownCoordinates =>
        TouchdownLatitudeDegrees.HasValue && TouchdownLongitudeDegrees.HasValue;

    public bool HasRawPitchInput =>
        FormatVersion >= 6 &&
        RawPitchInputSourceIndex >= 0 &&
        Math.Abs(RawPitchInputCorrelation) >= 0.75;

    public string PitchInputSourceDisplay => HasRawPitchInput
        ? LocalizationManager.Format("Model.RawPitchFormat", RawPitchInputSourceIndex, RawPitchInputLagSeconds * 1000.0)
        : LocalizationManager.Text("Model.ProcessedCommand");

    public string InertialQualityDisplay => FormatVersion < 4
        ? LocalizationManager.Text("Model.LegacyQuality")
        : InertialExtrapolated
            ? LocalizationManager.Format("Model.ExtrapolatedFormat", InertialFitDurationSeconds)
            : LocalizationManager.Text("Model.LastAirborne");

    public string LandingFeelDisplay => Math.Abs(InertialFpm) <= 240.0
        ? LocalizationManager.Text("Model.Smooth")
        : LocalizationManager.Text("Model.Firm");

    public bool IsFirmLanding => Math.Abs(InertialFpm) > 240.0;

    public string LandingFeelTooltip => LocalizationManager.Format(
        "Model.FeelHelpFormat",
        LandingFeelDisplay,
        InertialQualityDisplay);

    public string SurfaceQualityDisplay
    {
        get
        {
            if (FormatVersion < 4)
            {
                return LocalizationManager.Text("Model.LegacyLatch");
            }

            if (!LatchUpdateDetected)
            {
                return LocalizationManager.Text("Model.LatchNotVerified");
            }

            var milliseconds = LatchUpdateOffsetSeconds * 1000.0;
            return Math.Abs(milliseconds) < 0.5
                ? LocalizationManager.Text("Model.LatchSameFrame")
                : LocalizationManager.Format("Model.LatchOffsetFormat", milliseconds);
        }
    }

    public string WeightDisplay => WeightPounds > 0
        ? LocalizationManager.Format("Unit.KilogramsFormat", WeightPounds * 0.45359237)
        : LocalizationManager.Text("Model.NotAvailable");

    public string CgDisplay => CgPercent > 0
        ? LocalizationManager.Format("Model.CgFormat", CgPercent)
        : LocalizationManager.Text("Model.CgNa");

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
                return LocalizationManager.Text("Model.WindNa");
            }

            direction = (direction % 360.0 + 360.0) % 360.0;
            return LocalizationManager.Format("Model.WindFormat", direction, speed);
        }
    }

    public string RecordSummaryDisplay => LocalizationManager.Format("Model.RecordSummaryFormat", FormatVersion, Series.Count);

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        Series ??= new List<LandingSeriesPoint>();
        Engines ??= new List<LandingEngineSeries>();
        ContactPoints ??= new List<LandingContactSeries>();
        ControlInputSources ??= new List<string>();
        RawControllerSourceIndices ??= new List<int>();
        ClosureReconstructionModel ??= string.Empty;
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
        !IsFinite(value)
            ? LocalizationManager.Text("Model.NotAvailable")
            : value.ToString("+0;-0;0", CultureInfo.CurrentCulture);

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

}
