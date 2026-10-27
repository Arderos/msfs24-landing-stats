using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Storage;

// Additive v7 blocks: absent in old files; a partially present or mismatched block is corruption.
[DataContract]
internal sealed class LandingPoseColumns
{
    [DataMember(Name = "lat", Order = 1)] public double?[] Latitude { get; set; } = Array.Empty<double?>();
    [DataMember(Name = "lon", Order = 2)] public double?[] Longitude { get; set; } = Array.Empty<double?>();
    [DataMember(Name = "altFt", Order = 3)] public double?[] Altitude { get; set; } = Array.Empty<double?>();
    [DataMember(Name = "groundFt", Order = 4)] public double?[] GroundAltitude { get; set; } = Array.Empty<double?>();
    [DataMember(Name = "headingTrueDeg", Order = 5)] public double?[] Heading { get; set; } = Array.Empty<double?>();

    public static LandingPoseColumns? FromPoints(IReadOnlyList<LandingSeriesPoint> points)
    {
        if (!points.Any(p => p.LatitudeDegrees.HasValue || p.LongitudeDegrees.HasValue || p.PlaneAltitudeFeet.HasValue ||
                             p.GroundAltitudeFeet.HasValue || p.HeadingTrueDegrees.HasValue)) return null;
        return new LandingPoseColumns
        {
            Latitude = points.Select(p => Finite(p.LatitudeDegrees)).ToArray(),
            Longitude = points.Select(p => Finite(p.LongitudeDegrees)).ToArray(),
            Altitude = points.Select(p => Finite(p.PlaneAltitudeFeet)).ToArray(),
            GroundAltitude = points.Select(p => Finite(p.GroundAltitudeFeet)).ToArray(),
            Heading = points.Select(p => Finite(p.HeadingTrueDegrees)).ToArray(),
        };
    }

    public void Validate(int count)
    {
        if (Latitude == null || Longitude == null || Altitude == null || GroundAltitude == null || Heading == null ||
            Latitude.Length != count || Longitude.Length != count || Altitude.Length != count ||
            GroundAltitude.Length != count || Heading.Length != count)
            throw new InvalidDataException("Replay pose columns do not match the time column.");
    }

    public void ApplyTo(IReadOnlyList<LandingSeriesPoint> points)
    {
        Validate(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            points[i].LatitudeDegrees = Finite(Latitude[i]);
            points[i].LongitudeDegrees = Finite(Longitude[i]);
            points[i].PlaneAltitudeFeet = Finite(Altitude[i]);
            points[i].GroundAltitudeFeet = Finite(GroundAltitude[i]);
            points[i].HeadingTrueDegrees = Finite(Heading[i]);
        }
    }

    internal static double? Finite(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
        ? value : null;
}

[DataContract]
internal sealed class LandingPhysicalInputColumns
{
    [DataMember(Name = "value", Order = 1)] public double?[][] Values { get; set; } = Array.Empty<double?[]>();
    [DataMember(Name = "valid", Order = 2)] public bool[][] Valid { get; set; } = Array.Empty<bool[]>();
    [DataMember(Name = "age", Order = 3)] public double?[][] Ages { get; set; } = Array.Empty<double?[]>();

    public static LandingPhysicalInputColumns? FromRecord(LandingRecord record)
    {
        ValidateMapping(record.RawControllerAxesSourceIndices);
        var width = record.RawControllerAxesSourceIndices.Count;
        if (width == 0)
        {
            if (record.Series.Any(p => p.RawControllerAxesPercent.Length != 0 || p.RawControllerAxesValid.Length != 0 || p.RawControllerAxesAgeSeconds.Length != 0))
                throw new InvalidDataException("Physical input columns have no source mapping.");
            return null;
        }

        foreach (var point in record.Series)
        {
            if (point.RawControllerAxesPercent.Length != width || point.RawControllerAxesValid.Length != width || point.RawControllerAxesAgeSeconds.Length != width)
                throw new InvalidDataException("Physical input columns do not match their source mapping.");
        }
        var value = new LandingPhysicalInputColumns
        {
            Values = record.Series.Select(p => p.RawControllerAxesPercent.Select(v => LandingPoseColumns.Finite(v)).ToArray()).ToArray(),
            Valid = record.Series.Select(p => (bool[])p.RawControllerAxesValid.Clone()).ToArray(),
            Ages = record.Series.Select(p => p.RawControllerAxesAgeSeconds.Select(v => LandingPoseColumns.Finite(v)).ToArray()).ToArray(),
        };
        value.Validate(record.Series.Count, width);
        return value;
    }

    public static void ValidateMapping(IReadOnlyList<int> indices)
    {
        if (indices == null || indices.Any(i => i < 0) || indices.Distinct().Count() != indices.Count)
            throw new InvalidDataException("Physical input source mapping is invalid.");
    }

    public void Validate(int count, int width)
    {
        if (width == 0 || Values == null || Valid == null || Ages == null || Values.Length != count || Valid.Length != count || Ages.Length != count)
            throw new InvalidDataException("Physical input columns do not match the time column or source mapping.");
        for (var i = 0; i < count; i++)
            if (Values[i] == null || Valid[i] == null || Ages[i] == null || Values[i].Length != width || Valid[i].Length != width || Ages[i].Length != width)
                throw new InvalidDataException("Physical input rows have inconsistent widths.");
    }

    public void ApplyTo(IReadOnlyList<LandingSeriesPoint> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            points[i].RawControllerAxesPercent = Values[i].Select(v => LandingPoseColumns.Finite(v) ?? double.NaN).ToArray();
            points[i].RawControllerAxesAgeSeconds = Ages[i].Select(v => LandingPoseColumns.Finite(v) ?? double.NaN).ToArray();
            points[i].RawControllerAxesValid = Valid[i].Select((v, axis) => v && LandingPoseColumns.Finite(Values[i][axis]).HasValue && LandingPoseColumns.Finite(Ages[i][axis]).HasValue).ToArray();
        }
    }
}
