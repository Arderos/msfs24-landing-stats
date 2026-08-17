using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

internal enum LandingGearRole
{
    Generic,
    Nose,
    MainOne,
    MainTwo,
    MainThree,
}

internal sealed class LandingGearSeries
{
    public int Ordinal { get; set; }

    public LandingGearRole Role { get; set; }

    public List<int> ContactPointIndices { get; } = new List<int>();

    public List<LandingContactPoint> Points { get; } = new List<LandingContactPoint>();

    public double FirstContactSeconds => Points
        .Where(point => point.OnGround)
        .Select(point => point.TimeSeconds)
        .DefaultIfEmpty(double.MaxValue)
        .Min();

    public double PeakCompressionPercent => Points
        .Select(point => point.CompressionPercent)
        .DefaultIfEmpty(0.0)
        .Max();
}

internal static class LandingGearSeriesBuilder
{
    private const double MaximumWheelOnsetGapWithinStrutSeconds = 0.35;
    private static readonly ConditionalWeakTable<LandingRecord, CacheEntry> RecordCache = new();

    private sealed class CacheEntry
    {
        public CacheEntry(IReadOnlyList<LandingGearSeries> series)
        {
            Series = series;
        }

        public IReadOnlyList<LandingGearSeries> Series { get; }
    }

    public static IReadOnlyList<LandingGearSeries> Build(LandingRecord? record)
    {
        return record == null
            ? Array.Empty<LandingGearSeries>()
            : RecordCache.GetValue(
                record,
                value => new CacheEntry(BuildCore(value.ContactPoints, IsKnownToLissA340(value)))).Series;
    }

    public static IReadOnlyList<LandingGearSeries> Build(IReadOnlyList<LandingContactSeries> contactPoints)
        => BuildCore(contactPoints, false);

    private static IReadOnlyList<LandingGearSeries> BuildCore(
        IReadOnlyList<LandingContactSeries> contactPoints,
        bool knownA340)
    {
        var active = contactPoints
            .Where(HasContact)
            .OrderBy(series => series.ContactPointIndex)
            .ToList();
        if (active.Count == 0)
        {
            return Array.Empty<LandingGearSeries>();
        }

        if (knownA340 && TryBuildKnownA340(active, out var a340Series))
        {
            return a340Series;
        }

        // Aircraft.cfg convention places wheel contacts first and scrape/helper
        // contacts later. Keep the contiguous wheel block beginning at CP 0;
        // this removes the A340's transient and rollout-only helpers (CP 10+)
        // without deleting them from the stored black-box record.
        var wheels = ContiguousWheelBlock(active);
        if (wheels.Count <= 4)
        {
            return BuildSingletons(wheels);
        }

        var groups = new List<List<LandingContactSeries>>();
        foreach (var wheel in wheels)
        {
            var onset = FirstContact(wheel);
            var group = groups.Count == 0 ? null : groups[groups.Count - 1];
            var previous = group == null ? null : group[group.Count - 1];
            var anchor = group == null ? null : group[0];
            if (previous == null || anchor == null ||
                wheel.ContactPointIndex != previous.ContactPointIndex + 1 ||
                Math.Abs(onset - FirstContact(anchor)) > MaximumWheelOnsetGapWithinStrutSeconds)
            {
                groups.Add(new List<LandingContactSeries>());
            }

            groups[groups.Count - 1].Add(wheel);
        }

        // With no clear timing boundaries, preserving the individual points is
        // safer than guessing that independent left/right struts are one unit.
        if (groups.Count <= 1)
        {
            return BuildSingletons(wheels);
        }

        var result = groups.Select((group, index) => AverageGroup(group, index + 1)).ToList();
        return result;
    }

    public static string DisplayName(LandingGearSeries series)
    {
        var role = series.Role switch
        {
            LandingGearRole.Nose => LocalizationManager.Text("Chart.GearNose"),
            LandingGearRole.MainOne => LocalizationManager.Text("Chart.GearMainOne"),
            LandingGearRole.MainTwo => LocalizationManager.Text("Chart.GearMainTwo"),
            LandingGearRole.MainThree => LocalizationManager.Text("Chart.GearMainThree"),
            _ => LocalizationManager.Format("Chart.GearStrutFormat", series.Ordinal),
        };
        var members = LocalizationManager.Format(
            "Chart.ContactPointGroupFormat",
            FormatContactPointIndices(series.ContactPointIndices));
        return $"{role} · {members}";
    }

    private static List<LandingContactSeries> ContiguousWheelBlock(List<LandingContactSeries> settled)
    {
        var result = new List<LandingContactSeries>();
        var expectedIndex = settled[0].ContactPointIndex;
        foreach (var series in settled)
        {
            if (series.ContactPointIndex != expectedIndex)
            {
                break;
            }

            result.Add(series);
            expectedIndex++;
        }

        return result;
    }

    private static IReadOnlyList<LandingGearSeries> BuildSingletons(IReadOnlyList<LandingContactSeries> wheels)
    {
        var result = new List<LandingGearSeries>(wheels.Count);
        for (var index = 0; index < wheels.Count; index++)
        {
            result.Add(AverageGroup(new[] { wheels[index] }, index + 1));
        }

        return result;
    }

    private static LandingGearSeries AverageGroup(IReadOnlyList<LandingContactSeries> members, int ordinal)
    {
        var result = new LandingGearSeries { Ordinal = ordinal };
        result.ContactPointIndices.AddRange(members.Select(member => member.ContactPointIndex));
        var reference = members.OrderByDescending(member => member.Points.Count).First();
        foreach (var referencePoint in reference.Points)
        {
            var memberPoints = members
                .Select(member => PointAt(member.Points, referencePoint.TimeSeconds))
                .Where(point => point != null)
                .Cast<LandingContactPoint>()
                .ToList();
            var loadedPoints = memberPoints
                .Where(point => point.OnGround || Math.Abs(point.CompressionPercent) > 0.001)
                .ToList();
            result.Points.Add(new LandingContactPoint
            {
                TimeSeconds = referencePoint.TimeSeconds,
                CompressionPercent = loadedPoints.Count == 0
                    ? 0.0
                    : loadedPoints.Average(point => point.CompressionPercent),
                PositionPercent = memberPoints.Count == 0
                    ? 0.0
                    : memberPoints.Average(point => point.PositionPercent),
                OnGround = loadedPoints.Count > 0,
            });
        }

        return result;
    }

    private static bool TryBuildKnownA340(
        IReadOnlyList<LandingContactSeries> active,
        out IReadOnlyList<LandingGearSeries> result)
    {
        var byIndex = active
            .Where(series => series.ContactPointIndex >= 0 && series.ContactPointIndex <= 8)
            .ToDictionary(series => series.ContactPointIndex);
        if (Enumerable.Range(1, 8).Any(index => !byIndex.ContainsKey(index)))
        {
            result = Array.Empty<LandingGearSeries>();
            return false;
        }

        var groups = new List<LandingGearSeries>(4);
        AddKnownGroup(groups, byIndex, LandingGearRole.Nose, 0);
        AddKnownGroup(groups, byIndex, LandingGearRole.MainOne, 1, 2, 3);
        AddKnownGroup(groups, byIndex, LandingGearRole.MainTwo, 4, 5, 6);
        // These four CP sets are verified for the ToLiss A340, but telemetry does
        // not expose enough geometry to distinguish its two wing bogies from the
        // center bogie. Keep the three load-bearing groups deliberately neutral
        // instead of attaching a confident but potentially false Center label.
        AddKnownGroup(groups, byIndex, LandingGearRole.MainThree, 7, 8);
        result = groups;
        return true;
    }

    private static void AddKnownGroup(
        ICollection<LandingGearSeries> groups,
        IReadOnlyDictionary<int, LandingContactSeries> byIndex,
        LandingGearRole role,
        params int[] indices)
    {
        var members = indices.Where(byIndex.ContainsKey).Select(index => byIndex[index]).ToArray();
        if (members.Length == 0)
        {
            return;
        }

        var group = AverageGroup(members, groups.Count + 1);
        group.Role = role;
        groups.Add(group);
    }

    private static bool HasContact(LandingContactSeries series)
    {
        return series.Points.Any(point => point.OnGround || Math.Abs(point.CompressionPercent) > 0.001);
    }

    private static bool IsKnownToLissA340(LandingRecord record)
    {
        return (ContainsToLiss(record.AircraftTitle) || ContainsToLiss(record.AircraftType)) &&
               (ContainsA340(record.AircraftTitle) || ContainsA340(record.AircraftType));
    }

    private static bool ContainsToLiss(string? value) =>
        value != null && value.IndexOf("ToLiss", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool ContainsA340(string? value) =>
        value != null && value.IndexOf("A34", StringComparison.OrdinalIgnoreCase) >= 0;

    private static double FirstContact(LandingContactSeries series) => series.Points
        .Where(point => point.OnGround)
        .Select(point => point.TimeSeconds)
        .DefaultIfEmpty(double.MaxValue)
        .Min();

    private static LandingContactPoint? PointAt(IReadOnlyList<LandingContactPoint> points, double timeSeconds)
    {
        if (points.Count == 0)
        {
            return null;
        }
        if (points.Count == 1 || timeSeconds <= points[0].TimeSeconds)
        {
            return points[0];
        }
        if (timeSeconds >= points[points.Count - 1].TimeSeconds)
        {
            return points[points.Count - 1];
        }

        var low = 0;
        var high = points.Count - 1;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].TimeSeconds < timeSeconds)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return Math.Abs(points[low].TimeSeconds - timeSeconds) <= Math.Abs(points[high].TimeSeconds - timeSeconds)
            ? points[low]
            : points[high];
    }

    private static string FormatContactPointIndices(IReadOnlyList<int> indices)
    {
        if (indices.Count == 1)
        {
            return indices[0].ToString();
        }

        var consecutive = indices[indices.Count - 1] - indices[0] == indices.Count - 1;
        return consecutive
            ? $"{indices[0]}–{indices[indices.Count - 1]}"
            : string.Join("/", indices);
    }
}
