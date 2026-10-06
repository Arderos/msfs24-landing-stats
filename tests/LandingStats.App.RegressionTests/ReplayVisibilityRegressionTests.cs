using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class ReplayVisibilityRegressionTests
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        // A native presentation source supplies real IsVisibleChanged events,
        // without showing a window or starting the application's services.
        using var source = new HwndSource(new HwndSourceParameters("Replay visibility regression")
        {
            Width = 1, Height = 1, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000),
        });
        var parent = new Grid();
        var view = new LandingReplayView();
        parent.Children.Add(view); source.RootVisual = parent;
        Require(view.IsVisible, "The isolated presentation source exposes visibility changes.");
        view.Record = Record();
        var notifications = 0;
        view.TimeChanged += (_, __) => notifications++;
        var translation = Get<TranslateTransform3D>(view, "_translation");
        var camera = Get<PerspectiveCamera>(view, "_camera");
        var metrics = Get<ReplayMetricsOverlay>(view, "_metrics");
        var slider = Get<Slider>(view, "_slider");
        var beforeTranslation = translation.Value;
        var beforeCamera = camera.Position;

        parent.Visibility = Visibility.Collapsed;
        Require(!view.IsVisible, "The replay follows its collapsed tab.");
        foreach (var time in new[] { -.25, .25, .35 }) view.SetTime(time);
        Near(view.CurrentTimeSeconds, .35, "Hidden chart hover retains the latest selection.");
        Near(Get<LandingReplayPose>(view, "_pose").TimeSeconds, 0, "Hidden hover does not resample the visual pose.");
        Require(translation.Value == beforeTranslation && camera.Position == beforeCamera,
            "Hidden hover does not move the aircraft or camera.");
        Near(Get<double>(metrics, "_time"), 0, "Hidden hover does not update overlays.");
        Near(slider.Value, 0, "Hidden hover does not update the playback slider.");
        Require(Get<bool>(view, "_visualUpdatePending"), "Hidden selections are pending rather than discarded.");

        parent.Visibility = Visibility.Visible;
        Near(Get<LandingReplayPose>(view, "_pose").TimeSeconds, .35, "Showing the tab applies its latest time immediately.");
        Near(Get<double>(metrics, "_time"), .35, "Showing the tab refreshes overlays.");
        Near(slider.Value, .35, "Showing the tab refreshes its slider.");
        Require(translation.Value != beforeTranslation && camera.Position != beforeCamera && !Get<bool>(view, "_visualUpdatePending"),
            "Showing the tab refreshes the pose and camera once.");
        Require(notifications == 0, "Deferred synchronization never emits a playback callback.");

        // Repeating a selected time must not redo the suspension/visual work.
        translation.OffsetZ = 123456;
        view.SetTime(.35);
        Near(translation.OffsetZ, 123456, "An unchanged time does not reapply its pose.");
        view.SetTime(.4);
        Require(translation.OffsetZ != 123456, "A changed visible time still updates immediately.");

        view.Visibility = Visibility.Hidden;
        view.SetTime(999);
        Near(view.CurrentTimeSeconds, 2, "Hidden selections are still clamped to the recorded range.");
        Near(Get<LandingReplayPose>(view, "_pose").TimeSeconds, .4, "Explicitly hidden replay also defers pose work.");
        view.Visibility = Visibility.Visible;
        Near(Get<LandingReplayPose>(view, "_pose").TimeSeconds, 2, "Unhiding the view applies the clamped time.");

        parent.Visibility = Visibility.Collapsed;
        view.SetTime(.75);
        parent.Visibility = Visibility.Visible;
        Require(!Get<bool>(view, "_hasPose") && Get<ScaleTransform3D>(view, "_poseVisibility").ScaleX == 0,
            "A deferred time inside a telemetry gap hides the old aircraft.");
        Require(GetNullable(metrics, "_pose") == null, "A deferred gap clears the overlay pose too.");

        view.SetTime(.25);
        typeof(LandingReplayView).GetMethod("TogglePlayback", InstancePrivate)!.Invoke(view, null);
        Require(Get<bool>(view, "_playing"), "Playback starts in the visible test presentation.");
        parent.Visibility = Visibility.Collapsed;
        Require(!Get<bool>(view, "_playing") && !Get<DispatcherTimer>(view, "_timer").IsEnabled,
            "Hiding replay still stops its timer.");
        view.SetTime(.4);
        view.Record = Record();
        Near(view.CurrentTimeSeconds, 0, "A new hidden record resets selection to its contact.");
        parent.Visibility = Visibility.Visible;
        Near(Get<LandingReplayPose>(view, "_pose").TimeSeconds, 0, "Showing a replaced record cannot apply the previous record's time.");
        Require(!Get<bool>(view, "_playing") && !Get<bool>(view, "_resetCameraPending"),
            "Showing a replaced record resets its camera without restarting playback.");

        parent.Visibility = Visibility.Collapsed;
        view.SetTime(.4); view.Record = null;
        parent.Visibility = Visibility.Visible;
        Require(!view.IsPlaybackAvailable && !Get<bool>(view, "_visualUpdatePending") && Get<ModelVisual3D>(view, "_scene").Content == null,
            "Clearing a pending record cannot restore a stale scene on reveal.");
        source.RootVisual = null;

        var detached = new LandingReplayView { Record = Record() };
        Require(!detached.IsVisible, "Offscreen fixture has no presentation source.");
        detached.SetTime(.25);
        Near(Get<LandingReplayPose>(detached, "_pose").TimeSeconds, .25,
            "Detached visible controls still update for offscreen rendering.");
        detached.SetTime(double.NaN); detached.SetTime(double.PositiveInfinity);
        Near(detached.CurrentTimeSeconds, .25, "Invalid selections do not disturb the current time.");
    }

    private static LandingRecord Record()
    {
        var record = new LandingRecord();
        foreach (var time in new[] { -1d, -.5, 0, .25, .5, 1.5, 2 })
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = time, LatitudeDegrees = 42 + time * .0005, LongitudeDegrees = 23,
                PlaneAltitudeFeet = 1000 + (time < 0 ? 3.8 - time : 3.2) / .3048,
                GroundAltitudeFeet = 1000, HeadingTrueDegrees = 0, OnGround = time >= 0,
            });
        return record;
    }

    private static T Get<T>(object target, string field) => (T)GetNullable(target, field)!;
    private static object? GetNullable(object target, string field) => target.GetType().GetField(field, InstancePrivate)!.GetValue(target);
    private static void Near(double actual, double expected, string message) => Require(Math.Abs(actual - expected) < 1e-10, message);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
