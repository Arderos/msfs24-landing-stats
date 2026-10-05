using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.RegressionTests;

internal static class ReplayTabRegressionTests
{
    public static void RunAll()
    {
        var replay = new RadioButton { GroupName = "replay-fallback-test", Tag = "Reconstruction" };
        var controls = new RadioButton { GroupName = "replay-fallback-test", Tag = "FlightControls" };
        var rates = new RadioButton { GroupName = "replay-fallback-test", Tag = "VerticalSpeed" };
        var parent = new StackPanel(); parent.Children.Add(replay); parent.Children.Add(controls); parent.Children.Add(rates);
        // Resource changes are broadcast through application windows, as in the
        // real UI. Keep this host offscreen; no simulator or app services run.
        var host = new Window { Content = parent };
        replay.SetResourceReference(ContentControl.ContentProperty, "Chart.TabReconstruction");
        controls.SetResourceReference(ContentControl.ContentProperty, "Chart.TabControls");
        var changes = new List<string>();
        replay.Checked += (_, __) => changes.Add((string)replay.Tag);
        controls.Checked += (_, __) => changes.Add((string)controls.Tag);
        var view = new LandingReplayView();
        var old = new LandingRecord { FormatVersion = 7, TimestampUtc = DateTime.UtcNow.AddYears(1) };
        old.Series.Add(new LandingSeriesPoint { TimeSeconds = -.1 }); old.Series.Add(new LandingSeriesPoint { TimeSeconds = .1 });
        var current = new LandingRecord { FormatVersion = 7, TimestampUtc = DateTime.UtcNow.AddYears(-1) };
        foreach (var time in new[] { -.1, .1 }) current.Series.Add(new LandingSeriesPoint {
            TimeSeconds = time, LatitudeDegrees = 42 + time * .001, LongitudeDegrees = 23,
            PlaneAltitudeFeet = 1010, GroundAltitudeFeet = 1000, HeadingTrueDegrees = 90 });
        void Select(LandingRecord? record)
        {
            view.Record = record;
            MainWindow.UpdateControlTab(view.IsPlaybackAvailable, replay, controls);
        }
        LocalizationManager.Apply("en");
        rates.IsChecked = true; Select(old);
        Require(controls.Visibility == Visibility.Visible && replay.Visibility == Visibility.Collapsed, "v7 without pose data offers Controls.");
        Require(rates.IsChecked == true && changes.Count == 0, "Changing landing does not steal selection from another chart.");
        controls.IsChecked = true; Select(current);
        Require(replay.Visibility == Visibility.Visible && controls.Visibility == Visibility.Collapsed && replay.IsChecked == true && controls.IsChecked == false,
            "Current pose data replaces selected Controls with selected 3D, regardless of date or equal format version.");
        Require(changes[changes.Count - 1] == "Reconstruction", "Switching to 3D invokes the existing mode handler.");
        Select(old);
        Require(controls.IsChecked == true && replay.IsChecked == false && controls.Visibility == Visibility.Visible,
            "Switching back to an old landing selects Controls, not an empty 3D view.");
        Require(changes[changes.Count - 1] == "FlightControls", "Fallback invokes the normal chart handler to restore legend, units and lanes.");
        var count = changes.Count; Select(old); Require(changes.Count == count, "Re-selecting the same kind does not reset the chart.");
        rates.IsChecked = true; Select(current);
        Require(rates.IsChecked == true && replay.IsChecked == false && controls.IsChecked == false, "Changing capability keeps another selected chart.");
        replay.IsChecked = true; Select(null);
        Require(controls.IsChecked == true && replay.IsChecked == false && !view.IsPlaybackAvailable, "Clearing history cannot retain a selected replay.");
        current.Series[0].HeadingTrueDegrees = null; Select(current);
        Require(controls.Visibility == Visibility.Visible && !view.IsPlaybackAvailable, "Incomplete pose data uses real control charts instead of an empty replay.");
        Require((string)controls.Content == "Controls" && (string)replay.Content == "3D reconstruction", "Both English tab labels resolve.");
        LocalizationManager.Apply("ru");
        Require((string)controls.Content == "Управление" && (string)replay.Content == "3D-реконструкция", "Both tab labels follow live language changes.");
        LocalizationManager.Apply("en");
        host.Content = null;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
