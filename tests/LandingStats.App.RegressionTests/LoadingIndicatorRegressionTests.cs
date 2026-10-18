using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class LoadingIndicatorRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        var mode = Application.Current.ShutdownMode;
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources.MergedDictionaries.Add(resources);
        var window = new MainWindow()
            { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        // Render the real XAML, but never start acquisition or user storage.
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, typeof(MainWindow).GetMethod("OnWindowLoaded", Private)!);
        window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
        window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        T Get<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
        void Refresh() => typeof(MainWindow).GetMethod("RefreshHistoryLoadingPresentation", Private)!.Invoke(window, null);
        var history = Get<ProgressBar>("HistoryLoadingProgress");
        var detail = Get<ProgressBar>("DetailLoadingProgress");
        var closing = Get<ProgressBar>("ClosingProgress");
        try
        {
            Set("_historyLoading", false); Refresh(); window.Show();
            Pump(() => !history.IsIndeterminate && !detail.IsIndeterminate && IdleClock(), "Fast loading left a progress animation running.");

            Set("_historyLoading", true); Refresh();
            Check(!history.IsIndeterminate && !detail.IsIndeterminate, "A short load must not flash progress bars.");
            Pump(() => history.IsVisible && history.IsIndeterminate && detail.IsIndeterminate, "A slow initial load did not animate.");
            Set("_historyLoading", false); Refresh();
            Pump(() => !history.IsIndeterminate && !detail.IsIndeterminate && IdleClock(), "Completed loading retained an animation clock.");

            Set("_detailLoading", true); Refresh();
            Pump(() => detail.IsVisible && detail.IsIndeterminate, "A slow detail load did not animate.");
            window.Hide();
            Pump(() => !detail.IsIndeterminate && IdleClock(), "A hidden window retained its loading animation.");
            window.Show();
            Pump(() => detail.IsIndeterminate, "Showing an unfinished load did not resume its indicator.");
            Set("_detailLoading", false); Set("_detailLoadError", "Fixture read failure"); Refresh();
            Pump(() => !detail.IsIndeterminate && IdleClock(), "Failed loading retained its animation.");
            Check(Get<FrameworkElement>("DetailLoadingPanel").IsVisible && Get<Button>("RetryHistoryLoadButton").IsVisible,
                "Stopping animation must preserve the error and retry presentation.");

            Get<FrameworkElement>("ClosingOverlay").Visibility = Visibility.Visible;
            Pump(() => closing.IsIndeterminate, "Visible shutdown progress did not animate.");
            Get<FrameworkElement>("ClosingOverlay").Visibility = Visibility.Collapsed;
            Pump(() => !closing.IsIndeterminate && IdleClock(), "Hidden shutdown progress retained an animation clock.");
        }
        finally
        {
            typeof(MainWindow).GetMethod("StopHistoryOperations", Private)!.Invoke(window, null);
            window.Close();
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            Application.Current.ShutdownMode = mode;
        }
    }

    private static bool IdleClock()
    {
        // Passive inspection: subscribing to Rendering would itself start the
        // presentation clock and conceal the bug this regression guards.
        var type = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext")!;
        var media = type.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { Dispatcher.CurrentDispatcher });
        var manager = type.GetField("_timeManager", Private)!.GetValue(media)!;
        var next = (TimeSpan)manager.GetType().GetMethod("GetNextTickNeeded", Private | BindingFlags.Public)!.Invoke(manager, null)!;
        return next < TimeSpan.Zero;
    }

    private static void Pump(Func<bool> condition, string error)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, __) => { if (condition() || watch.Elapsed.TotalSeconds > 3) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(condition(), error);
    }
    private static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
}
