using System;
using System.Linq;
using System.Windows.Controls;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class PhysicalInputStripRegressionTests
{
    public static void RunAll()
    {
        // Before the compact v7 layout, source index was the dense array index.
        var dense = new LandingRecord { FormatVersion = 6 };
        var oldPoint = new LandingSeriesPoint { TimeSeconds = 0 };
        oldPoint.RawControllerYAxisPercent[2] = 42;
        oldPoint.RawControllerYAxisValid[2] = true;
        oldPoint.RawControllerYAxisPercent[7] = -17;
        oldPoint.RawControllerYAxisValid[7] = true;
        dense.Series.Add(oldPoint);
        var strip = new PhysicalInputStrip { Record = dense };
        strip.SetTime(0);
        Require(Texts(strip).Contains("Y +42%") && Texts(strip).Contains("Y -17%"), "dense legacy Y inputs retain device indices");
        Require(Texts(strip).Count(value => value.StartsWith("Y ", StringComparison.Ordinal)) == 2, "unused legacy channels are not invented");

        var compact = new LandingRecord();
        compact.RawControllerSourceIndices.Add(5);
        compact.Series.Add(new LandingSeriesPoint { TimeSeconds = 0,
            RawControllerYAxisPercent = new[] { 31.0 }, RawControllerYAxisValid = new[] { true }, RawControllerYAxisAgeSeconds = new[] { 0.0 } });
        strip.Record = compact;
        Require(Texts(strip).Contains("Y +31%"), "compact legacy source mapping still reads compact array offsets");

        var current = new LandingRecord();
        current.RawControllerAxesSourceIndices.AddRange(new[] { 0, 8, 11 });
        current.Series.Add(Point(0, 1, 2, 3));
        current.Series.Add(Point(5, 42, -13, 27));
        strip.Record = current;
        strip.SetTime(5);
        Require(Texts(strip).Contains("X +42%") && Texts(strip).Contains("Z -13%") && Texts(strip).Contains("V +27%"), "all-axis compact mapping labels and values");
        strip.RefreshLocalization();
        Require(Texts(strip).Contains("X +42%") && Texts(strip).Contains("Z -13%") && Texts(strip).Contains("V +27%"), "language refresh preserves last selected time and values");
        strip.SetTime(2);
        Require(Texts(strip).Contains("X —") && !Texts(strip).Contains("X +42%"), "gap never carries stale controller input");
        current.Series[1].RawControllerAxesAgeSeconds[0] = .3;
        current.Series[1].RawControllerAxesValid[1] = false;
        strip.SetTime(5);
        Require(Texts(strip).Contains("X —") && Texts(strip).Contains("Z —") && Texts(strip).Contains("V +27%"), "stale and invalid axes remain unavailable independently");

        strip.Record = new LandingRecord();
        Require(!Texts(strip).Any(value => value.StartsWith("X ", StringComparison.Ordinal) || value.StartsWith("Y ", StringComparison.Ordinal)), "empty history gains no synthetic axes");
    }

    private static LandingSeriesPoint Point(double time, double x, double z, double v) => new LandingSeriesPoint {
        TimeSeconds = time, RawControllerAxesPercent = new[] { x, z, v },
        RawControllerAxesValid = new[] { true, true, true }, RawControllerAxesAgeSeconds = new[] { 0.0, 0.0, 0.0 },
    };

    private static string[] Texts(PhysicalInputStrip strip) => ((StackPanel)((Grid)strip.Content).Children.OfType<ScrollViewer>().Single().Content).Children
        .OfType<TextBlock>().Select(value => value.Text).ToArray();

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
