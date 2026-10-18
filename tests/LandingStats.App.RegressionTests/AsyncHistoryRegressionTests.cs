using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class AsyncHistoryRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-history-async-" + Guid.NewGuid().ToString("N"));
        var resources = LoadApplicationResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        var previousContext = SynchronizationContext.Current;
        var previousShutdownMode = Application.Current.ShutdownMode;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow? window = null;
        try
        {
            // Never show this window: SourceInitialized/Loaded (simulator,
            // update and cloud startup) must not run in an isolated disk test.
            window = new MainWindow();
            var repository = new LandingRepository(directory);
            Set(window, "_repository", repository);
            Set(window, "_airportFacilityRepository", new AirportFacilityRepository(Path.Combine(directory, "airports.gz")));
            var first = new LandingRecord
            {
                AircraftTitle = "Synthetic A", TimestampUtc = DateTime.UtcNow,
                ContactNumber = 1, ContactCount = 2, RawPitchInputSourceIndex = 3,
            };
            var sibling = new LandingRecord
            {
                AircraftTitle = first.AircraftTitle, TimestampUtc = first.TimestampUtc,
                ContactNumber = 2, ContactCount = 2, RawPitchInputSourceIndex = 4,
            };
            var second = new LandingRecord { AircraftTitle = "Synthetic B", TimestampUtc = first.TimestampUtc.AddMinutes(-1) };
            repository.Save(first);
            repository.Save(sibling);
            repository.Save(second);
            var gate = Get<object>(window, "_landingRepositoryGate");
            var list = Get<ListBox>(window, "LandingHistoryList");
            var cache = Get<Dictionary<string, LandingRecord>>(window, "_loadedDetails");

            using (var pause = new RepositoryPause(gate))
            {
                var elapsed = Stopwatch.StartNew();
                var loading = (Task)Invoke(window, "LoadHistoryAsync", new object?[] { first.Id })!;
                Assert(elapsed.Elapsed < TimeSpan.FromSeconds(1), "history load yields instead of waiting for the storage gate");
                Assert(!loading.IsCompleted, "history read is genuinely pending");
                Assert(Get<FrameworkElement>(window, "DetailLoadingPanel").Visibility == Visibility.Collapsed,
                    "startup does not flash a loading placeholder immediately");
                AssertDispatcherPulse();
                PumpUntil(() => Get<FrameworkElement>(window, "DetailLoadingPanel").Visibility == Visibility.Visible);
                Assert(!loading.IsCompleted, "a genuinely slow startup eventually shows loading feedback");
                pause.Release();
                PumpUntil(() => loading.IsCompleted && window.DataContext is LandingRecord);
                loading.GetAwaiter().GetResult();
            }
            Assert(cache.ContainsKey(first.Id) && cache.ContainsKey(sibling.Id), "episode sibling details loaded off the dispatcher");
            var persistedFirst = repository.LoadDetail(repository.LoadAll().Single(record => record.Id == first.Id))!;
            var persistedSibling = repository.LoadDetail(repository.LoadAll().Single(record => record.Id == sibling.Id))!;
            Assert(persistedFirst.RawPitchInputSourceIndex == -1 && persistedSibling.RawPitchInputSourceIndex == -1,
                "legacy input-selection migration is durable for both the selected contact and its siblings");

            list.SelectedItem = list.Items.Cast<LandingRecord>().Single(record => record.Id == second.Id);
            PumpUntil(() => (window.DataContext as LandingRecord)?.Id == second.Id);
            var cachedSecond = window.DataContext;
            Invoke(window, "ApplyZoom", new object?[] { -1.25, 2.5 });
            Invoke(window, "SelectRecord", new[] { list.SelectedItem });
            Assert(ReferenceEquals(window.DataContext, cachedSecond) && Get<double?>(window, "_zoomStartSeconds") == -1.25,
                "cached metadata refresh preserves the detail instance and the user's zoom");

            var refresh = (Task)Invoke(window, "LoadHistoryAsync", new object?[] { first.Id })!;
            var diskRead = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!;
            Assert(diskRead.Wait(TimeSpan.FromSeconds(10)), "history snapshot finishes without a UI continuation");
            var newerSummary = Get<IReadOnlyList<LandingRecord>>(window, "_landings").Single(record => record.Id == second.Id);
            newerSummary.Airport = "NEWER";
            newerSummary.Runway = "09";
            newerSummary.RunwayGeometry = new RunwayFacility { AirportIdent = "NEWER", PrimaryName = "09" };
            PumpUntil(() => refresh.IsCompleted);
            Assert((window.DataContext as LandingRecord)?.Id == second.Id &&
                Get<IReadOnlyList<LandingRecord>>(window, "_landings").Single(record => record.Id == second.Id).Airport == "NEWER",
                "background history refresh preserves current selection and newer runway metadata");

            cache.Remove(first.Id);
            cache.Remove(sibling.Id);
            using (var pause = new RepositoryPause(gate))
            {
                list.SelectedItem = list.Items.Cast<LandingRecord>().Single(record => record.Id == first.Id);
                Assert(ReferenceEquals(window.DataContext, cachedSecond),
                    "switching contacts retains the previous detail during the loading grace period");
                Assert(!Get<FrameworkElement>(window, "LandingContent").IsEnabled &&
                    !Get<FrameworkElement>(window, "LandingContent").IsHitTestVisible &&
                    Get<FrameworkElement>(window, "DetailLoadingPanel").Visibility == Visibility.Collapsed,
                    "retained detail is not interactive and does not flash a loading placeholder");
                AssertDispatcherPulse();
                PumpUntil(() => Get<FrameworkElement>(window, "DetailLoadingPanel").Visibility == Visibility.Visible);
                Assert(Get<FrameworkElement>(window, "LandingContent").Visibility == Visibility.Collapsed,
                    "a slow selection replaces retained content with loading feedback");
                list.SelectedItem = list.Items.Cast<LandingRecord>().Single(record => record.Id == second.Id);
                Assert(ReferenceEquals(window.DataContext, cachedSecond), "cached latest selection appears while the older read is blocked");
                Assert(Get<FrameworkElement>(window, "LandingContent").IsEnabled &&
                    Get<FrameworkElement>(window, "LandingContent").IsHitTestVisible &&
                    Get<FrameworkElement>(window, "DetailLoadingPanel").Visibility == Visibility.Collapsed,
                    "cached selection restores interaction and cancels stale loading feedback");
                pause.Release();
                DrainHistory(window);
            }
            Assert((window.DataContext as LandingRecord)?.Id == second.Id && !cache.ContainsKey(first.Id),
                "late A result cannot overwrite B or repopulate a stale cache");

            using (var pause = new RepositoryPause(gate))
            {
                list.SelectedItem = list.Items.Cast<LandingRecord>().Single(record => record.Id == first.Id);
                list.SelectedItem = list.Items.Cast<LandingRecord>().Single(record => record.Id == second.Id);
                var gracePeriod = Stopwatch.StartNew();
                PumpUntil(() => gracePeriod.ElapsedMilliseconds >= 600);
                Assert(ReferenceEquals(window.DataContext, cachedSecond) &&
                    Get<FrameworkElement>(window, "LandingContent").IsEnabled &&
                    Get<FrameworkElement>(window, "DetailLoadingPanel").Visibility == Visibility.Collapsed,
                    "quick A-to-cached-B selection cannot show A's delayed placeholder after half a second");
                pause.Release();
                DrainHistory(window);
            }

            using (var pause = new RepositoryPause(gate))
            {
                list.SelectedItem = list.Items.Cast<LandingRecord>().Single(record => record.Id == first.Id);
                Get<TextBox>(window, "SessionFilterBox").Text = "no synthetic landing matches";
                Assert(list.SelectedItem == null, "filter clears the selection");
                pause.Release();
                DrainHistory(window);
            }
            Assert(window.DataContext == null, "filtered-out detail cannot return after its read completes");
            Get<TextBox>(window, "SessionFilterBox").Text = string.Empty;
            PumpUntil(() => window.DataContext is LandingRecord);
            list.SelectedItem = list.Items.Cast<LandingRecord>().Single(record => record.Id == second.Id);
            cache.Remove(first.Id);
            cache.Remove(sibling.Id);

            using (var pause = new RepositoryPause(gate, () => repository.Delete(first.Id)))
            {
                list.SelectedItem = list.Items.Cast<LandingRecord>().Single(record => record.Id == first.Id);
                Get<HashSet<string>>(window, "_locallyRemovedHistoryIds").Add(first.Id);
                Set(window, "_landings", Get<IReadOnlyList<LandingRecord>>(window, "_landings").Where(record => record.Id != first.Id).ToArray());
                Invoke(window, "ApplySessionFilter", new object?[] { second.Id });
                pause.Release();
                DrainHistory(window);
            }
            Assert((window.DataContext as LandingRecord)?.Id == second.Id && !cache.ContainsKey(first.Id) && !repository.Contains(first.Id),
                "deletion wins over in-flight details and their migration writes");

            FreshStorageMetadataSurvivesQueuedMigration(window, repository, gate, list, cache);

            var missing = new LandingRecord { IsSummaryOnly = true, AircraftTitle = "Missing synthetic record" };
            Set(window, "_landings", Get<IReadOnlyList<LandingRecord>>(window, "_landings").Concat(new[] { missing }).ToArray());
            Invoke(window, "ApplySessionFilter", new object?[] { missing.Id });
            PumpUntil(() => Get<string?>(window, "_detailLoadError") != null);
            Assert(window.DataContext == null && Get<FrameworkElement>(window, "RetryHistoryLoadButton").Visibility == Visibility.Visible,
                "missing details show a retryable error, not fabricated summary charts");
            cache.Remove(second.Id);
            using (var pause = new RepositoryPause(gate))
            {
                list.SelectedItem = list.Items.Cast<LandingRecord>().Single(record => record.Id == second.Id);
                Invoke(window, "StopHistoryOperations");
                Set(window, "_isClosed", true);
                pause.Release();
                DrainHistory(window);
            }
            Assert(window.DataContext == null, "closed windows reject pending results");
        }
        finally
        {
            try
            {
                if (window != null)
                {
                    Invoke(window, "StopHistoryOperations");
                    DrainHistory(window);
                    // Avoid invoking unrelated application shutdown in this fixture.
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

    private static void FreshStorageMetadataSurvivesQueuedMigration(MainWindow window,
        LandingRepository repository, object gate, ListBox list, Dictionary<string, LandingRecord> cache)
    {
        var record = new LandingRecord
        {
            AircraftTitle = "Queued synthetic record", TimestampUtc = DateTime.UtcNow.AddMinutes(-5),
            RawPitchInputSourceIndex = 3,
        };
        repository.Save(record);
        var capturedSummary = LandingRepository.CreateSummary(record);
        Set(window, "_landings", Get<IReadOnlyList<LandingRecord>>(window, "_landings")
            .Concat(new[] { capturedSummary }).ToArray());
        using (var pause = new RepositoryPause(gate, () =>
        {
            // Model a cloud replacement after selection has captured the old
            // summary, but before its worker can enter the repository gate.
            record.Airport = "FRESH";
            record.Runway = "09";
            record.AirportDistanceNauticalMiles = 2;
            record.RunwayGeometry = new RunwayFacility
            {
                AirportIdent = "FRESH", PrimaryName = "09", SecondaryName = "27",
                HeadingTrueDegrees = 90, LengthMeters = 2500, WidthMeters = 45,
            };
            record.AircraftTitle = "Downloaded synthetic record";
            record.InertialFpm = -321;
            repository.Save(record);
        }))
        {
            Invoke(window, "ApplySessionFilter", new object?[] { record.Id });
            Assert(!((Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!
                .GetValue(window)!).IsCompleted, "selection is queued before the newer storage write");
            pause.Release();
            DrainHistory(window);
        }
        var freshRepository = new LandingRepository(repository.RootPath);
        var saved = freshRepository.LoadDetail(freshRepository.LoadAll().Single(value => value.Id == record.Id))!;
        Assert(saved.RawPitchInputSourceIndex == -1 && saved.Airport == "FRESH" && saved.Runway == "09" &&
            saved.AirportDistanceNauticalMiles == 2 && saved.RunwayGeometry?.AirportIdent == "FRESH",
            "migration keeps the latest committed airport and runway metadata, not the captured summary");
        Assert(saved.AircraftTitle == "Downloaded synthetic record" && saved.InertialFpm == -321,
            "migration loads the newest full detail, preserving its non-metadata values");
        var presented = (LandingRecord)window.DataContext;
        Assert(presented.Id == record.Id && presented.Airport == "FRESH" && presented.Runway == "09" &&
            presented.AircraftTitle == "Downloaded synthetic record" && presented.InertialFpm == -321 &&
            capturedSummary.Airport == "FRESH" && capturedSummary.RunwayGeometry?.AirportIdent == "FRESH",
            "an unchanged stale UI summary cannot overwrite fresh detail metadata during publication");

        cache.Remove(record.Id);
        var loading = (Task)Invoke(window, "SelectRecordAsync", new object?[] { list.SelectedItem })!;
        var diskRead = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!;
        Assert(diskRead.Wait(TimeSpan.FromSeconds(10)), "detail storage work finishes before its UI continuation");
        capturedSummary.Airport = "LATER";
        capturedSummary.Runway = "27";
        capturedSummary.RunwayGeometry = new RunwayFacility { AirportIdent = "LATER", PrimaryName = "27" };
        PumpUntil(() => loading.IsCompleted);
        loading.GetAwaiter().GetResult();
        presented = (LandingRecord)window.DataContext;
        Assert(presented.Airport == "LATER" && presented.Runway == "27" &&
            ReferenceEquals(presented.RunwayGeometry, capturedSummary.RunwayGeometry),
            "scenery metadata received after the disk read still outranks the worker snapshot");
    }

    private static ResourceDictionary LoadApplicationResources() => Review089UiRegressionTests.LoadApplicationResources();

    private static void DrainHistory(MainWindow window)
    {
        var pending = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!;
        PumpUntil(() => pending.IsCompleted);
        // A completed disk task can have a dispatcher continuation still queued.
        var pulse = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        PumpUntil(() => pulse.Task.IsCompleted);
    }

    private static void AssertDispatcherPulse()
    {
        var pulse = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
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
        Assert(condition(), "dispatcher operation timed out");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RepositoryPause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
        private readonly Task _task;

        public RepositoryPause(object gate, Action? beforeRelease = null)
        {
            var entered = new ManualResetEventSlim();
            _task = Task.Run(() =>
            {
                lock (gate)
                {
                    entered.Set();
                    _release.Wait();
                    beforeRelease?.Invoke();
                }
            });
            Assert(entered.Wait(TimeSpan.FromSeconds(10)), "synthetic repository gate entered");
            entered.Dispose();
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            Release();
            Assert(_task.Wait(TimeSpan.FromSeconds(10)), "synthetic repository gate released");
            _release.Dispose();
        }
    }
}
