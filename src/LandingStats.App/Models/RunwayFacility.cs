using System;
using System.Runtime.Serialization;

namespace LandingStats.App.Models;

[DataContract]
public sealed class RunwayFacility
{
    internal RunwayFacility Snapshot()
    {
        var copy = (RunwayFacility)MemberwiseClone();
        copy.PrimaryPapi = PrimaryPapi?.Snapshot();
        copy.SecondaryPapi = SecondaryPapi?.Snapshot();
        copy.PrimaryIlsGlideSlope = PrimaryIlsGlideSlope?.Snapshot();
        copy.SecondaryIlsGlideSlope = SecondaryIlsGlideSlope?.Snapshot();
        return copy;
    }
    [DataMember(Order = 1)] public string AirportIdent { get; set; } = string.Empty;
    [DataMember(Order = 2)] public string AirportRegion { get; set; } = string.Empty;
    [DataMember(Order = 3)] public double LatitudeDegrees { get; set; }
    [DataMember(Order = 4)] public double LongitudeDegrees { get; set; }
    [DataMember(Order = 5)] public double AltitudeMeters { get; set; }
    [DataMember(Order = 6)] public double HeadingTrueDegrees { get; set; }
    [DataMember(Order = 7)] public double LengthMeters { get; set; }
    [DataMember(Order = 8)] public double WidthMeters { get; set; }
    [DataMember(Order = 9)] public string PrimaryName { get; set; } = string.Empty;
    [DataMember(Order = 10)] public string SecondaryName { get; set; } = string.Empty;
    [DataMember(Order = 11)] public string Source { get; set; } = "SimConnect";
    [DataMember(Order = 12, EmitDefaultValue = false)] public DateTime? RetrievedUtc { get; set; }
    [DataMember(Order = 13, EmitDefaultValue = false)] public int? SurfaceType { get; set; }
    // Null means not queried (including old records); zero means a verified, undisplaced threshold.
    [DataMember(Order = 14, EmitDefaultValue = false)] public double? PrimaryThresholdOffsetMeters { get; set; }
    [DataMember(Order = 15, EmitDefaultValue = false)] public double? SecondaryThresholdOffsetMeters { get; set; }
    // PAPI installation references, not measured glide-path intersections.
    // SimConnect supplies no optical reference elevation for these lights.
    // Queried + null means both sides were checked and neither contains PAPI;
    // false means legacy/unavailable/conflicting data, never a verified absence.
    [DataMember(Order = 16, EmitDefaultValue = false)] public RunwayPapi? PrimaryPapi { get; set; }
    [DataMember(Order = 17, EmitDefaultValue = false)] public RunwayPapi? SecondaryPapi { get; set; }
    [DataMember(Order = 18, EmitDefaultValue = false)] public bool PrimaryPapiQueried { get; set; }
    [DataMember(Order = 19, EmitDefaultValue = false)] public bool SecondaryPapiQueried { get; set; }

    [DataMember(Order = 20, EmitDefaultValue = false)] public RunwayGlideSlope? PrimaryIlsGlideSlope { get; set; }
    [DataMember(Order = 21, EmitDefaultValue = false)] public RunwayGlideSlope? SecondaryIlsGlideSlope { get; set; }

    public bool HasValidGuidanceMetadata =>
        (PrimaryPapi == null || (PrimaryPapiQueried && PrimaryPapi.HasValidGeometry(LengthMeters))) &&
        (SecondaryPapi == null || (SecondaryPapiQueried && SecondaryPapi.HasValidGeometry(LengthMeters))) &&
        (PrimaryIlsGlideSlope == null || PrimaryIlsGlideSlope.HasValidGeometry) &&
        (SecondaryIlsGlideSlope == null || SecondaryIlsGlideSlope.HasValidGeometry);

    public bool IsWater => SurfaceType == 2 || (SurfaceType >= 26 && SurfaceType <= 31);
    public bool HasPaintedSurface => !SurfaceType.HasValue || SurfaceType == 0 || SurfaceType == 4;

    public bool HasValidThresholds => ValidOffset(PrimaryThresholdOffsetMeters) && ValidOffset(SecondaryThresholdOffsetMeters) &&
        (PrimaryThresholdOffsetMeters ?? 0) + (SecondaryThresholdOffsetMeters ?? 0) < LengthMeters;

    private bool ValidOffset(double? value) => !value.HasValue ||
        (RunwayNumber.IsFinite(value.Value) && value.Value >= 0 && value.Value < LengthMeters);

    public bool HasValidGeometry =>
        !string.IsNullOrWhiteSpace(AirportIdent) &&
        RunwayNumber.IsFinite(LatitudeDegrees) && Math.Abs(LatitudeDegrees) <= 90 &&
        RunwayNumber.IsFinite(LongitudeDegrees) && Math.Abs(LongitudeDegrees) <= 180 &&
        RunwayNumber.IsFinite(AltitudeMeters) &&
        RunwayNumber.IsFinite(HeadingTrueDegrees) && HeadingTrueDegrees >= 0 && HeadingTrueDegrees < 360 &&
        RunwayNumber.IsFinite(LengthMeters) && LengthMeters > 0 && LengthMeters <= 20000 &&
        RunwayNumber.IsFinite(WidthMeters) && WidthMeters > 0 && WidthMeters <= 500 && HasValidThresholds;
}

[DataContract]
public sealed class RunwayPapi
{
    internal RunwayPapi Snapshot() => (RunwayPapi)MemberwiseClone();
    // LENGTH / 2 - BIAS_Z, measured along the landing direction from the
    // physical end. Rendering may use a nominal coplanar approximation only.
    [DataMember(Order = 1)] public double StationFromEndMeters { get; set; }
    [DataMember(Order = 2)] public double AngleDegrees { get; set; }
    public bool HasValidGeometry(double length) => RunwayNumber.IsFinite(StationFromEndMeters) &&
        StationFromEndMeters >= 0 && StationFromEndMeters < length &&
        RunwayNumber.IsFinite(AngleDegrees) && AngleDegrees > 0 && AngleDegrees < 90;
}

[DataContract]
public sealed class RunwayGlideSlope
{
    internal RunwayGlideSlope Snapshot() => (RunwayGlideSlope)MemberwiseClone();
    [DataMember(Order = 1)] public string Ident { get; set; } = string.Empty;
    [DataMember(Order = 2)] public string Region { get; set; } = string.Empty;
    [DataMember(Order = 3)] public double LatitudeDegrees { get; set; }
    [DataMember(Order = 4)] public double LongitudeDegrees { get; set; }
    [DataMember(Order = 5)] public double AltitudeMeters { get; set; }
    [DataMember(Order = 6)] public double AngleDegrees { get; set; }
    public bool HasValidGeometry => !string.IsNullOrWhiteSpace(Ident) &&
        RunwayNumber.IsFinite(LatitudeDegrees) && Math.Abs(LatitudeDegrees) <= 90 &&
        RunwayNumber.IsFinite(LongitudeDegrees) && Math.Abs(LongitudeDegrees) <= 180 &&
        RunwayNumber.IsFinite(AltitudeMeters) && RunwayNumber.IsFinite(AngleDegrees) && AngleDegrees > 0 && AngleDegrees < 90;
}

internal static class RunwayNumber
{
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
