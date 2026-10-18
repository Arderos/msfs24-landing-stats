using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class ReplayPresentationRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(LandingRecord record)
    {
        var view = new LandingReplayView { Record = record };
        var mode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new Window { Content = view, Width = 800, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        var type = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext")!;
        var media = type.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { view.Dispatcher });
        var manager = type.GetField("_timeManager", Private)!.GetValue(media)!;
        bool Ticking() => (TimeSpan)manager.GetType().GetMethod("GetNextTickNeeded", Private | BindingFlags.Public)!.Invoke(manager, null)! >= TimeSpan.Zero;
        try
        {
            window.Show(); Pump(Ticking, "Visible replay did not enter native animation/presentation mode.");
            Require(type.GetField("Rendering", Private)!.GetValue(media) == null,
                "Restored input must not depend on a custom Rendering callback or deferred input queue.");
            var slider = (Slider)typeof(LandingReplayView).GetField("_slider", Private)!.GetValue(view)!;
            var position = (TranslateTransform3D)typeof(LandingReplayView).GetField("_translation", Private)!.GetValue(view)!;
            var start = position.OffsetZ; var events = 0;
            view.TimeChanged += (_, __) => events++;
            for (var i = 0; i < 100; i++)
            {
                slider.Value = i % 2 == 0 ? slider.Maximum : slider.Minimum;
                Require((position.OffsetZ == start) == (i % 2 != 0) && events == i + 1,
                    "Slider pose and linked readouts must update immediately, as in guardian.");
            }
            window.WindowState = WindowState.Minimized;
            Pump(() => !Ticking(), "Minimized replay retained its native clock.");
            window.WindowState = WindowState.Normal;
            Pump(Ticking, "Restored window did not resume native presentation mode.");
            view.Visibility = Visibility.Collapsed;
            Pump(() => !Ticking(), "Hidden replay retained its native clock.");
            view.Visibility = Visibility.Visible;
            Pump(Ticking, "Showing replay did not restore native presentation mode.");
            view.Record = null;
            Pump(() => !Ticking(), "Unavailable replay retained its native clock.");
            view.Record = record; Pump(Ticking, "Replacing a valid record did not restore presentation mode.");
            window.Content = null;
            Pump(() => !Ticking(), "Unloaded replay retained its native clock.");
        }
        finally { window.Close(); Application.Current.ShutdownMode = mode; }
    }
    private static void Pump(Func<bool> condition, string error)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, __) => { if (condition() || watch.Elapsed.TotalSeconds > 3) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Require(condition(), error);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
