using System;
using System.Globalization;
using System.Windows.Data;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.Core;

namespace LandingStats.App.Presentation;

// A view of one immutable record. It is never serialized and never replaces
// the record identity used by selection, replay preparation or persistence.
public sealed class LandingPresentation
{
    public LandingPresentation(LandingRecord record) => Record = record ?? throw new ArgumentNullException(nameof(record));
    public LandingRecord Record { get; }
    public string AircraftTitle => Record.AircraftTitle;
    public bool HasClosureReconstruction => Record.HasClosureReconstruction;
    public bool IsSmoothLanding => Record.IsSmoothLanding;
    public bool IsFirmLanding => Record.IsFirmLanding;
    private bool UsesLegacyImpact => Record.ImpactAnalysisVersion == 0;

    public string TimestampDisplay => Record.TimestampUtc.ToLocalTime().ToString("dd MMM · HH:mm", CultureInfo.CurrentCulture);

    public string LocationDisplay => Record.Runway == "—"
        ? Record.Airport
        : LocalizationManager.Format("Model.LocationRunwayFormat", Record.Airport, Record.Runway);

    public string InertialDisplay => $"{-Record.InertialFpm:+0;-0;0}";

    public string ClosureModeledDisplay => Record.HasClosureReconstruction
        ? $"{-Record.ReconstructedClosureFpm:+0;-0;0} fpm"
        : LocalizationManager.Text("Model.NotAvailable");

    public string ClosureResidualDisplay => Record.HasClosureReconstruction && IsFinite(Record.ClosureReconstructionResidualFpm)
        ? $"{-Record.ClosureReconstructionResidualFpm:+0;-0;0} fpm"
        : LocalizationManager.Text("Model.NotAvailable");

    public string ClosureComponentsDisplay => Record.HasClosureReconstruction
        ? LocalizationManager.Format(
            Record.ReconstructedRotationFpm.HasValue ? "Model.ContactComponentsFormat" : "Model.ComponentsFormat",
            FormatSignedMetric(-Record.ReconstructedInertialFpm),
            FormatSignedMetric(-Record.ReconstructedTerrainFpm),
            FormatSignedMetric(-(Record.ReconstructedRotationFpm ?? Record.ReconstructedPitchFpm)))
        : LocalizationManager.Text("Model.ReconstructionUnavailable");

    public string ClosureUncertaintyDisplay => Record.HasClosureReconstruction &&
                                               IsFinite(Record.ClosureReconstructionUncertaintyFpm)
        ? $"±{Math.Abs(Record.ClosureReconstructionUncertaintyFpm):0} fpm"
        : LocalizationManager.Text("Model.NotAvailable");

    public string ClosureGeometryDisplay
    {
        get
        {
            if (!Record.HasClosureReconstruction || !IsFinite(Record.ClosureReconstructionLongitudinalArmFeet))
            {
                return LocalizationManager.Text("Model.GeometryUnavailable");
            }

            var sourceKey = Record.ClosureReconstructionGeometrySource;
            if (string.IsNullOrWhiteSpace(sourceKey) ||
                string.Equals(
                    sourceKey,
                    nameof(TouchdownGeometrySource.Unavailable),
                    StringComparison.Ordinal))
            {
                sourceKey = Record.ClosureReconstructionArmRecoveredFromTelemetry
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
                          IsFinite(Record.ClosureReconstructionGeometryQuality)
                ? LocalizationManager.Format("Model.GeometryQualityFormat", Record.ClosureReconstructionGeometryQuality)
                : string.Empty;
            if (Record.ReconstructedRotationFpm.HasValue)
                return LocalizationManager.Format("Model.ContactGeometryFormat", source, quality);
            return LocalizationManager.Format(
                "Model.GeometryFormat",
                Record.ClosureReconstructionLongitudinalArmFeet,
                source,
                quality);
        }
    }

    public string ClosureModelDisplay => Record.HasClosureReconstruction &&
                                         !string.IsNullOrWhiteSpace(Record.ClosureReconstructionModel)
        ? Record.ClosureReconstructionModel
        : LocalizationManager.Text("Model.ReconstructionUnavailable");

    public string SurfaceDisplay => Record.HasSurfaceLatchData
        ? $"{-Record.SurfaceFpm:+0;-0;0} fpm"
        : LocalizationManager.Text("Model.NotAvailable");

    public string SurfaceValueDisplay => Record.HasSurfaceLatchData
        ? $"{-Record.SurfaceFpm:+0;-0;0}"
        : LocalizationManager.Text("Model.NotAvailable");

    public string SurfaceUnitDisplay => Record.HasSurfaceLatchData ? "fpm" : string.Empty;

    public string DeltaDisplay => !double.IsNaN(Record.SurfaceDeltaFpm)
        ? $"{-Record.SurfaceDeltaFpm:+0;-0;0} fpm"
        : LocalizationManager.Text("Model.SurfaceDeltaNa");

    public string TerrainMetricDisplay => LocalizationManager.Format("Model.TerrainFormat", FormatSignedMetric(-Record.TerrainFpm));

    public string UnresolvedMetricDisplay => LocalizationManager.Format("Model.UnresolvedFormat", FormatSignedMetric(-Record.UnresolvedFpm));

    public string G150Display => Record.PeakG150Milliseconds.ToString("F2", CultureInfo.CurrentCulture);

    public string G2SecondsDisplay => LocalizationManager.Format("Model.Peak2Format", Record.PeakG2Seconds);

    public string AttitudeDisplay => $"{-Record.PitchDegrees:+0.0;-0.0;0.0}° / {Record.BankDegrees:+0.0;-0.0;0.0}°";

    public string AirspeedDisplay => LocalizationManager.Format("Model.SpeedKnotsFormat", Record.AirspeedKnots);

    public string GroundSpeedDisplay => LocalizationManager.Format("Model.SpeedKnotsFormat", Record.GroundSpeedKnots);

    public string AngleOfAttackDisplay => LocalizationManager.Format("Model.AoaFormat", Record.AngleOfAttackDegrees);

    public string ContactDisplay => Record.ContactCount > 1
        ? LocalizationManager.Format("Model.ContactMultipleFormat", Record.ContactNumber, Record.ContactCount)
        : LocalizationManager.Text("Model.ContactSingle");

    public string PitchInputSourceDisplay => Record.HasRawPitchInput
        ? LocalizationManager.Format("Model.RawPitchFormat", Record.RawPitchInputSourceIndex, Record.RawPitchInputLagSeconds * 1000.0)
        : LocalizationManager.Text("Model.ProcessedCommand");

    public string InertialQualityDisplay => Record.FormatVersion < 4
        ? LocalizationManager.Text("Model.LegacyQuality")
        : Record.InertialExtrapolated
            ? LocalizationManager.Format("Model.ExtrapolatedFormat", Record.InertialFitDurationSeconds)
            : LocalizationManager.Text("Model.LastAirborne");

    private double DisplayImpactPeak => UsesLegacyImpact ? Record.PeakG150Milliseconds : Record.ImpactResult.PeakG;
    public string LandingFeelDisplay => LocalizationManager.Text(Record.IsSmoothLanding ? "Model.Smooth" :
        Record.IsNormalLanding ? "Model.Normal" : Record.IsFirmLanding ? "Model.Firm" : "Model.NotAvailable");

    public string ImpactPeakDisplay => IsFinite(DisplayImpactPeak) && DisplayImpactPeak > 0 &&
        (UsesLegacyImpact || (Record.ImpactResult.Status != TouchdownImpactStatus.InvalidTelemetry &&
                             Record.ImpactResult.Status != TouchdownImpactStatus.Unavailable))
            ? DisplayImpactPeak.ToString("F2", CultureInfo.CurrentCulture)
            : LocalizationManager.Text("Model.NotAvailable");

    public string GDetailDisplay => UsesLegacyImpact ? G2SecondsDisplay :
        Record.ImpactResult.IsComplete && Record.ImpactWindowSeconds.HasValue &&
        IsFinite(Record.ImpactWindowSeconds.Value) && Record.ImpactWindowSeconds.Value > 0
            ? LocalizationManager.Format("Model.ImpactWindowFormat", Record.ImpactWindowSeconds.Value)
            : LocalizationManager.Text(Record.ImpactResult.Status == TouchdownImpactStatus.Incomplete
                ? "Model.ImpactWindowIncomplete" : "Model.NotAvailable");

    public string GDetailTooltip => LocalizationManager.Text(UsesLegacyImpact
        ? "Help.PeakG2" : "Help.ImpactWindow");

    public string LandingFeelTooltip => LocalizationManager.Format("Model.ImpactFeelHelpFormat",
        LandingFeelDisplay,
        IsFinite(DisplayImpactPeak) && DisplayImpactPeak > 0
            ? DisplayImpactPeak.ToString("R", CultureInfo.CurrentCulture) + " G"
            : LocalizationManager.Text("Model.NotAvailable"));

    public string SurfaceQualityDisplay
    {
        get
        {
            if (Record.FormatVersion < 4)
            {
                return LocalizationManager.Text("Model.LegacyLatch");
            }

            if (!Record.LatchUpdateDetected)
            {
                return LocalizationManager.Text("Model.LatchNotVerified");
            }

            var milliseconds = Record.LatchUpdateOffsetSeconds * 1000.0;
            return Math.Abs(milliseconds) < 0.5
                ? LocalizationManager.Text("Model.LatchSameFrame")
                : LocalizationManager.Format("Model.LatchOffsetFormat", milliseconds);
        }
    }

    public string WeightDisplay => Record.WeightPounds > 0
        ? LocalizationManager.Format("Unit.KilogramsFormat", Record.WeightPounds * 0.45359237)
        : LocalizationManager.Text("Model.NotAvailable");

    public string CgDisplay => Record.CgPercent > 0
        ? LocalizationManager.Format("Model.CgFormat", Record.CgPercent)
        : LocalizationManager.Text("Model.CgNa");

    public string WindDisplay
    {
        get
        {
            var speed = Record.WindSpeedKnotsAtContact;
            var direction = Record.WindDirectionDegreesAtContact;
            if ((Record.FormatVersion < 7 || (Math.Abs(speed) < 0.000001 && Math.Abs(direction) < 0.000001)) &&
                Record.Series != null && Record.Series.Count > 0)
            {
                var closest = Record.Series[0];
                for (var index = 1; index < Record.Series.Count; index++)
                {
                    if (Math.Abs(Record.Series[index].TimeSeconds) < Math.Abs(closest.TimeSeconds))
                    {
                        closest = Record.Series[index];
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

    public string RecordSummaryDisplay => LocalizationManager.Format("Model.RecordSummaryFormat", Record.FormatVersion, Record.Series.Count);

    private static string FormatSignedMetric(double value) =>
        !IsFinite(value)
            ? LocalizationManager.Text("Model.NotAvailable")
            : value.ToString("+0;-0;0", CultureInfo.CurrentCulture);

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

}

public sealed class LandingPresentationConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is LandingRecord record ? new LandingPresentation(record) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
