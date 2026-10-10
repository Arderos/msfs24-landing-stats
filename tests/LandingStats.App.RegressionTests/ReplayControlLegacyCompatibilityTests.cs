using System;
using System.Globalization;
using System.IO;
using System.Linq;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class ReplayControlLegacyCompatibilityTests
{
    public static void RunAll()
    {
        // Real historical A350 recording: only relative time, ground state,
        // command and physical Y channels. No stored binding, identity or location.
        // Published level correlation chose Y0 (+0.32035646174022819, zero lag);
        // applying the new full-axis movement metric instead chose inverted Y5.
        var record = Read();
        Require(record.RawControllerAxesSourceIndices.Count == 0 && record.RawPitchInputSourceIndex == -1,
            "fixture exercises unlabelled historical Y-only matching, not a stored override");
        CheckHistoricalBinding(record);

        // Compact-column order must not change the historical source identity.
        var order = new[] { 5, 6, 0, 1, 2, 3, 4 };
        record.RawControllerSourceIndices = order.ToList();
        foreach (var point in record.Series)
        {
            point.RawControllerYAxisPercent = order.Select(column => point.RawControllerYAxisPercent[column]).ToArray();
            point.RawControllerYAxisValid = order.Select(column => point.RawControllerYAxisValid[column]).ToArray();
            point.RawControllerYAxisAgeSeconds = order.Select(column => point.RawControllerYAxisAgeSeconds[column]).ToArray();
        }
        CheckHistoricalBinding(record);

        record.RawPitchInputSourceIndex = 5;
        record.RawPitchInputCorrelation = -.76;
        record.RawPitchInputLagSeconds = .3;
        var stored = ReplayControlInputs.Resolve(record).Pitch;
        Require(stored?.SourceIndex == 31 && stored.Legacy && stored.Correlation == -.76 && stored.LagSeconds == .3,
            "valid historical source, polarity and lag metadata still take precedence over recomputed ranking");
    }

    private static void CheckHistoricalBinding(LandingRecord record)
    {
        var inputs = ReplayControlInputs.Resolve(record);
        Require(inputs.Pitch?.SourceIndex == 1 && inputs.Pitch.Legacy &&
            Math.Abs(inputs.Pitch.Correlation - .32035646174022819) < 1e-12 && inputs.Pitch.LagSeconds == 0,
            "historical A350 retains the exact published positive Y0 level-correlation binding");
        Require(inputs.Roll == null && inputs.Yaw == null, "historical Y-only data does not invent roll or pedals");
        var point = record.Series[500];
        var column = record.RawControllerSourceIndices.IndexOf(0);
        Require(inputs.Sample(point.TimeSeconds).PitchPercent == point.RawControllerYAxisPercent[column],
            "historical matching preserves the original physical sample amplitude and time");
    }

    private static LandingRecord Read()
    {
        var record = new LandingRecord { FormatVersion = 7, RawPitchInputSourceIndex = -1 };
        record.RawControllerSourceIndices.AddRange(Enumerable.Range(0, 7));
        foreach (var line in File.ReadLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "physical-legacy-a350.csv")).Skip(1))
        {
            var values = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = values[0], OnGround = values[1] != 0,
                PilotPitchPercent = values[2], RawControllerYAxisPercent = values.Skip(3).Take(7).ToArray(),
                RawControllerYAxisValid = Enumerable.Repeat(values[10] != 0, 7).ToArray(),
                RawControllerYAxisAgeSeconds = Enumerable.Repeat(values[11], 7).ToArray() });
        }
        return record;
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
