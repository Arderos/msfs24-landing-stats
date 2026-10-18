using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class Review089UiRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-review-ui-" + Guid.NewGuid().ToString("N"));
        var resources = LoadApplicationResources();
        var previousContext = SynchronizationContext.Current;
        var previousShutdownMode = Application.Current.ShutdownMode;
        Application.Current.Resources.MergedDictionaries.Add(resources);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null;
        try
        {
            // Never show this window: no simulator, application startup, cloud,
            // updater or real user settings may run in this isolated fixture.
            window = new MainWindow();
            VerifySettingsOverlay(window);
            var repository = new LandingRepository(directory);
            var record = new LandingRecord { AircraftTitle = "Synthetic review landing" };
            repository.Save(record);
            Set(window, "_repository", repository);
            Set(window, "_airportFacilityRepository", new AirportFacilityRepository(Path.Combine(directory, "airports.gz")));
            Set(window, "_landings", new[] { record });
            Set(window, "_historyLoading", false);
            Set(window, "_startupReady", true);
            Invoke(window, "ApplySessionFilter", new object?[] { record.Id });
            PumpUntil(() => window.DataContext is LandingRecord shown && shown.Id == record.Id);
            var displayed = window.DataContext;

            var historyPanel = Get<FrameworkElement>(window, "HistoryLoadingPanel");
            var detailPanel = Get<FrameworkElement>(window, "DetailLoadingPanel");
            var content = Get<FrameworkElement>(window, "LandingContent");
            Assert(content.DataContext is Presentation.LandingPresentation presentation && ReferenceEquals(presentation.Record, displayed),
                "Only the visual root projects captions; selection and replay retain the exact persisted DTO.");
            var history = Get<ListBox>(window, "LandingHistoryList");
            // The template root receives its DTO from the parent content host.
            var templateHost = new ContentPresenter { Content = history.Items[0], ContentTemplate = history.ItemTemplate };
            templateHost.ApplyTemplate(); templateHost.Measure(new Size(500, 100)); templateHost.Arrange(new Rect(0, 0, 500, 100));
            Assert(System.Windows.Media.VisualTreeHelper.GetChildrenCount(templateHost) > 0 &&
                ((FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(templateHost, 0)).DataContext is Presentation.LandingPresentation row &&
                ReferenceEquals(row.Record, history.Items[0]), "History template did not project the original row DTO.");
            var historyRetry = Get<Button>(window, "RetryHistoryIndexLoadButton");
            var detailRetry = Get<Button>(window, "RetryHistoryLoadButton");
            Assert(historyRetry.Visibility == Visibility.Collapsed, "A healthy history has no retry button.");

            Set(window, "_historyLoadError", "Synthetic index read failure");
            Invoke(window, "RefreshHistoryLoadingPresentation");
            Assert(ReferenceEquals(window.DataContext, displayed) && content.Visibility == Visibility.Visible && content.IsEnabled,
                "A failed history refresh retains the currently usable landing.");
            Assert(detailPanel.Visibility == Visibility.Collapsed && historyPanel.Visibility == Visibility.Visible &&
                historyRetry.Visibility == Visibility.Visible && historyRetry.IsEnabled,
                "A populated history exposes retry outside the collapsed detail status panel.");
            Assert(IsVisibleThroughParents(historyRetry, window),
                "The history retry button has no hidden or collapsed presentation ancestor.");

            // When both reads failed, the detail retry still retries details;
            // the left-hand retry must independently recover the history index.
            Set(window, "_detailLoadError", "Synthetic detail read failure");
            Invoke(window, "RefreshHistoryLoadingPresentation");
            detailRetry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Get<string?>(window, "_detailLoadError") == null);
            Assert(Get<string?>(window, "_historyLoadError") != null,
                "The detail retry does not claim to have recovered the history index.");

            Set(window, "_detailLoadError", "Synthetic detail read failure");
            Invoke(window, "RefreshHistoryLoadingPresentation");
            historyRetry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Get<string?>(window, "_historyLoadError") == null &&
                !Get<bool>(window, "_historyLoading") && window.DataContext is LandingRecord);
            DrainHistory(window);
            Assert(historyPanel.Visibility == Visibility.Collapsed && historyRetry.Visibility == Visibility.Collapsed &&
                (window.DataContext as LandingRecord)?.Id == record.Id,
                "The history retry recovers the index and keeps the existing selection even when details also failed.");

            foreach (var language in new[] { "en", "ru" })
            {
                LocalizationManager.Apply(language);
                foreach (var key in new[]
                {
                    "History.Loading", "History.DetailLoading",
                    "History.LoadFailed", "History.DetailLoadFailed", "History.RetryLoad",
                    "Lifecycle.Closing", "Settings.Applying", "Settings.Loading",
                })
                {
                    var text = LocalizationManager.Text(key);
                    Assert(!string.IsNullOrWhiteSpace(text) && text != key && window.TryFindResource(key) is string,
                        "The " + language + " loading presentation resolves " + key + ".");
                }
            }
        }
        finally
        {
            try
            {
                if (window != null)
                {
                    Invoke(window, "StopHistoryOperations");
                    DrainHistory(window);
                    window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window,
                        typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
                    window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
                        typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
                    window.Close();
                }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
                LocalizationManager.Apply("en");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            finally
            {
                Application.Current.ShutdownMode = previousShutdownMode;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    private static bool IsVisibleThroughParents(FrameworkElement element, Window window)
    {
        for (FrameworkElement? current = element; current != null && !ReferenceEquals(current, window);
             current = current.Parent as FrameworkElement)
            if (current.Visibility != Visibility.Visible) return false;
        return true;
    }

    private static void VerifySettingsOverlay(MainWindow window)
    {
        var overlay = Get<Grid>(window, "SettingsOverlay");
        var panel = (Border)overlay.Children[0];
        var scrollViewer = (ScrollViewer)panel.Child;
        var scrollbarStyle = scrollViewer.Resources[typeof(ScrollBar)] as Style;
        Assert(scrollbarStyle != null && ReferenceEquals(scrollbarStyle.BasedOn,
            window.FindResource("HistoryScrollBarStyle")),
            "Settings use the same dark scrollbar style as landing history.");

        overlay.Visibility = Visibility.Visible;
        foreach (var inside in new UIElement[] { panel, scrollViewer,
            Get<CheckBox>(window, "SettingsTelemetryCheckBox") })
        {
            inside.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseDownEvent });
            Assert(overlay.Visibility == Visibility.Visible,
                "Clicks in settings, its padding and its scroll viewer must not dismiss it.");
        }

        var outside = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseDownEvent };
        overlay.RaiseEvent(outside);
        Assert(overlay.Visibility == Visibility.Collapsed && outside.Handled,
            "A backdrop click closes settings and is consumed rather than passed to the underlying view.");
    }

    internal static ResourceDictionary LoadApplicationResources() => new ResourceDictionary
    {
        Source = new Uri("/" + typeof(App).Assembly.GetName().Name + ";component/Controls/ApplicationResources.xaml", UriKind.Relative)
    };

    private static void DrainHistory(MainWindow window)
    {
        var pending = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!;
        PumpUntil(() => pending.IsCompleted);
        var pulse = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        PumpUntil(() => pulse.Task.IsCompleted);
    }

    private static object? Invoke(MainWindow window, string name, object?[]? arguments = null) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);

    private static T Get<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;

    private static void Set(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, __) => { if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert(condition(), "UI review dispatcher regression timed out.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
