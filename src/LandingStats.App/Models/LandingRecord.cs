using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using LandingStats.Core;

namespace LandingStats.App.Models;

[DataContract]
public sealed class LandingRecord : IExtensibleDataObject
{
    public ExtensionDataObject? ExtensionData { get; set; }
    internal ExtensionDataObject? DetailExtensionData { get; set; }
    public const int CurrentFormatVersion = 7;
    public const double NonFiniteStorageSentinel = -1.7976931348623157E+308;

    public AnalysisGeometrySnapshot? AnalysisGeometry { get; set; }

    // The published 0.9.4 reader validates the original member as v1. Keep it
    // readable after downgrade; newer evidence is an additive detail member.
    [DataMember(Name = "AnalysisGeometry", Order = 70, EmitDefaultValue = false)]
    private AnalysisGeometrySnapshot? LegacyAnalysisGeometry
    {
        get => AnalysisGeometry?.Version == 1 ? AnalysisGeometry : null;
        set { if (value != null) AnalysisGeometry = value; }
    }

    [DataMember(Name = "AnalysisGeometryV2", Order = 73, EmitDefaultValue = false)]
    private AnalysisGeometrySnapshot? ExtendedAnalysisGeometry
    {
        get => AnalysisGeometry?.Version != 1 ? AnalysisGeometry : null;
        set { if (value != null) AnalysisGeometry = value; }
    }

    [DataMember(Order = 71, EmitDefaultValue = false)]
    public ControllerCatalogTimeline? ControllerTimeline { get; set; }

    [DataMember(Order = 72, EmitDefaultValue = false)]
    public string? SourceEvidenceTimeline { get; set; }

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

    [DataMember(Order = 58, EmitDefaultValue = false)]
    public int ImpactAnalysisVersion { get; set; }

    [DataMember(Order = 59, EmitDefaultValue = false)]
    public double? ImpactPeakG { get; set; }

    [DataMember(Order = 60, EmitDefaultValue = false)]
    public double? ImpactPeakOffsetSeconds { get; set; }

    [DataMember(Order = 61, EmitDefaultValue = false)]
    public double? ImpactEndOffsetSeconds { get; set; }

    [DataMember(Order = 62, EmitDefaultValue = false)]
    public string? ImpactStatus { get; set; }

    [DataMember(Order = 63, EmitDefaultValue = false)]
    public double? ImpactWindowSeconds { get; set; }

    [DataMember(Order = 64, EmitDefaultValue = false)]
    public List<int> RawControllerAxesSourceIndices { get; set; } = new List<int>();

    [DataMember(Order = 65, EmitDefaultValue = false)]
    public RunwayFacility? RunwayGeometry { get; set; }

    /// <summary>Original telemetry clock used as this contact's series zero; absent in older records.</summary>
    [DataMember(Order = 66, EmitDefaultValue = false)]
    public double? EstimatedContactTimeSeconds { get; set; }

    /// <summary>Stable identity shared by the contacts of one physical landing episode; absent in older records.</summary>
    [DataMember(Order = 67, EmitDefaultValue = false)]
    public string? RecordingEpisodeId { get; set; }

    [DataMember(Order = 68, EmitDefaultValue = false)]
    public double? ReconstructedRotationFpm { get; set; }

    [DataMember(Order = 69, EmitDefaultValue = false)]
    public int[]? ClosureReconstructionContactPointIndices { get; set; }

    [DataMember(Name = "ControllerAxisCount", Order = 74, EmitDefaultValue = false)]
    private int _controllerAxisCount;
    public int ControllerAxisCount
    {
        get => _controllerAxisCount == 0 ? TelemetrySample.CapturedControllerAxisCount : _controllerAxisCount;
        set { if (value != 6 && value != 8) throw new ArgumentOutOfRangeException(nameof(value)); _controllerAxisCount = value == 6 ? 0 : value; }
    }

    [IgnoreDataMember]
    public bool IsSummaryOnly { get; set; }

    public bool HasSurfaceLatchData => !double.IsNaN(SurfaceFpm);

    public bool HasClosureReconstruction =>
        ClosureReconstructionAvailable &&
        IsFinite(ReconstructedClosureFpm);

    public bool HasTouchdownCoordinates =>
        TouchdownLatitudeDegrees.HasValue && TouchdownLongitudeDegrees.HasValue;

    public bool HasRawPitchInput =>
        FormatVersion >= 6 &&
        RawPitchInputSourceIndex >= 0 &&
        Math.Abs(RawPitchInputCorrelation) >= 0.75;

    internal TouchdownImpactResult ImpactResult =>
        ImpactAnalysisVersion == TouchdownImpactAnalysis.CurrentVersion &&
        Enum.TryParse(ImpactStatus, out TouchdownImpactStatus status)
            ? new TouchdownImpactResult(status, ImpactPeakG ?? double.NaN,
                ImpactPeakOffsetSeconds ?? double.NaN, ImpactEndOffsetSeconds ?? double.NaN)
            : new TouchdownImpactResult(TouchdownImpactStatus.Unavailable);

    private bool UsesLegacyImpact => ImpactAnalysisVersion == 0;
    private TouchdownImpactCategory ImpactCategory => UsesLegacyImpact
        ? !IsFinite(PeakG150Milliseconds) || PeakG150Milliseconds <= 0 ? TouchdownImpactCategory.Unavailable
            : PeakG150Milliseconds < AnalysisNumericPolicy.SmoothImpactG ? TouchdownImpactCategory.Smooth
            : PeakG150Milliseconds < AnalysisNumericPolicy.FirmImpactG ? TouchdownImpactCategory.Normal : TouchdownImpactCategory.Firm
        : ImpactResult.Category;

    public bool HasImpactClassification => ImpactCategory != TouchdownImpactCategory.Unavailable;
    public bool IsSmoothLanding => ImpactCategory == TouchdownImpactCategory.Smooth;
    public bool IsNormalLanding => ImpactCategory == TouchdownImpactCategory.Normal;
    public bool IsFirmLanding => ImpactCategory == TouchdownImpactCategory.Firm;

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        if (_controllerAxisCount != 0 && _controllerAxisCount != 6 && _controllerAxisCount != 8) throw new SerializationException("Unsupported controller axis layout.");
        Series ??= new List<LandingSeriesPoint>();
        Engines ??= new List<LandingEngineSeries>();
        ContactPoints ??= new List<LandingContactSeries>();
        ControlInputSources ??= new List<string>();
        RawControllerSourceIndices ??= new List<int>();
        RawControllerAxesSourceIndices ??= new List<int>();
        ClosureReconstructionModel ??= string.Empty;
    }

    public double PitchInputPercent(LandingSeriesPoint point)
    {
        if (!HasRawPitchInput) return point.PilotPitchPercent;
        // A raw-labelled trace must retain unavailable samples as gaps. Apply
        // the same validity, age and range contract as replay, including legacy files.
        if (!ReplayControlInputs.Raw(point, StoredRawControllerIndex(RawPitchInputSourceIndex), true, out var value))
            return double.NaN;
        var direction = RawPitchInputCorrelation < 0 ? -1.0 : 1.0;
        return direction * value;
    }

    public int StoredRawControllerIndex(int sourceIndex)
    {
        if (sourceIndex < 0 || RawControllerSourceIndices == null || RawControllerSourceIndices.Count == 0)
        {
            return sourceIndex;
        }

        return RawControllerSourceIndices.IndexOf(sourceIndex);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

}
