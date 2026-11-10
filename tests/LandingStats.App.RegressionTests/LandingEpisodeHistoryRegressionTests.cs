using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class LandingEpisodeHistoryRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void RunAll()
    {
        IdentityAndChronology();
        SharedCaptureChronology();
        PartialDeletion();
        WindowDeletion();
    }

    private static LandingRecord Record(string id, string? episode, int number, bool imported = false) => new LandingRecord
    {
        Id = id, RecordingEpisodeId = episode, ContactNumber = number, ContactCount = 7, IsImported = imported,
        AircraftTitle = "Fenix A321", AircraftType = "A321", Airport = "KORD", Runway = "10C",
        TimestampUtc = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc), InertialFpm = 150 + number * 20
    };

    private static void IdentityAndChronology()
    {
        var first = Record("first", "episode", 1);
        var second = Record("second", "episode", 2);
        second.TimestampUtc = first.TimestampUtc.AddSeconds(4); second.AircraftTitle = "Different metadata";
        var other = Record("other", "Episode", 1);
        var imported = Record("imported", "episode", 2, true);
        var legacy = Record("legacy", null, 1);
        var legacySecond = Record("legacy-second", null, 2);
        var records = new[] { second, first, other, imported, legacySecond, legacy };
        Check(LandingHistoryEpisodes.ForRecord(second, records).Select(record => record.Id).SequenceEqual(new[] { "first", "second" }),
            "Explicit identity groups contacts across metadata differences without merging case variants or imports.");
        Check(LandingHistoryEpisodes.ForRecord(legacy, records).Single().Id == legacy.Id,
            "Legacy contacts never acquire destructive membership from coincident timestamps.");
        var converter = new LandingHistoryEpisodeConverter();
        LandingHistoryEpisodeLayout Layout(LandingRecord record, LandingRecord[] list) =>
            (LandingHistoryEpisodeLayout)converter.Convert(new object[] { record, list }, typeof(object), null!, CultureInfo.InvariantCulture);
        var top = Layout(second, records); var bottom = Layout(first, records);
        Check(top.ConnectBelow && !top.ConnectAbove && bottom.ConnectAbove && !bottom.ConnectBelow &&
            !Layout(legacySecond, records).ShowConnector,
            "The newest contact stays above the first, with a continuous connector only inside its episode.");
        Check(!Layout(second, new[] { second, other, first }).ShowConnector,
            "A connector must not cross an unrelated chronological row.");
    }

    private static GoogleDriveBackupService Backup(LandingRepository repository, object gate, GoogleDriveSyncStateRepository state) =>
        new GoogleDriveBackupService(new DeferredGoogleDriveApi(), repository, gate, state,
            () => Task.FromResult(new GoogleDriveLocalSettings(new GoogleDriveCloudSettings(), true)),
            (_, __) => Task.FromResult(true));

    private static void SharedCaptureChronology()
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-history-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Record("E1#1", "segment:2", 1); first.EstimatedContactTimeSeconds = 100;
            var firstBounce = Record("E1#2", "segment:2", 2); firstBounce.EstimatedContactTimeSeconds = 101;
            var second = Record("E2#1", "segment:10", 1); second.EstimatedContactTimeSeconds = 200;
            var secondBounce = Record("E2#2", "segment:10", 2); secondBounce.EstimatedContactTimeSeconds = 201;
            var input = new[] { first, secondBounce, firstBounce, second };
            var expected = new[] { "E2#2", "E2#1", "E1#2", "E1#1" };
            Check(LandingHistoryOrder.Sort(input).Select(record => record.Id).SequenceEqual(expected) &&
                LandingHistoryOrder.Sort(input.AsEnumerable().Reverse()).Select(record => record.Id).SequenceEqual(expected),
                "Shared capture timestamps preserve episode chronology and bounce adjacency independently of input order or lexical episode IDs.");
            var repository = new LandingRepository(directory);
            foreach (var record in input) repository.Save(record);
            var records = new LandingRepository(directory).LoadAll();
            Check(records.Select(record => record.Id).SequenceEqual(expected), "Persisted history uses the shared episode order.");
            var converter = new LandingHistoryEpisodeConverter();
            Check(records.All(record => ((LandingHistoryEpisodeLayout)converter.Convert(new object[] { record, records }, typeof(object), null!, CultureInfo.InvariantCulture)).ShowConnector),
                "Each touch-and-go episode keeps both connector ends after a repository reload.");

            var imported = Record("imported", first.RecordingEpisodeId, 2, true); imported.EstimatedContactTimeSeconds = 150;
            var legacy = Record("legacy", null, 2); legacy.EstimatedContactTimeSeconds = double.NaN;
            var legacyOther = Record("legacy-other", null, 1); legacyOther.EstimatedContactTimeSeconds = double.PositiveInfinity;
            var newer = Record("newer", null, 1); newer.TimestampUtc = first.TimestampUtc.AddSeconds(1);
            var varied = LandingHistoryOrder.Sort(input.Concat(new[] { imported, legacy, legacyOther, newer }));
            Check(varied[0].Id == newer.Id && varied.Select(record => record.Id).SequenceEqual(
                LandingHistoryOrder.Sort(varied.AsEnumerable().Reverse()).Select(record => record.Id)),
                "Wall-clock chronology and deterministic ordering survive missing/nonfinite contact times, legacy records, and imports.");
            Check(varied.TakeWhile(record => record.Id != firstBounce.Id).Any(record => record.Id == imported.Id) &&
                LandingHistoryEpisodes.ForRecord(imported, varied).Length == 1 && LandingHistoryEpisodes.ForRecord(legacy, varied).Length == 1,
                "Imported identity collisions and legacy records remain separate episode groups.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void PartialDeletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-episode-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(Path.Combine(directory, "landings"));
            var state = new GoogleDriveSyncStateRepository(Path.Combine(directory, "sync.json"));
            state.Save(new GoogleDriveSyncState { AccountPermissionId = "fixture-account" });
            using var backup = Backup(repository, new object(), state);
            string Bucket(string id) { using var sha = SHA256.Create(); return sha.ComputeHash(Encoding.UTF8.GetBytes(id))[0].ToString("x2").Substring(0, 1); }
            var first = Record("success", "episode", 1);
            var failedId = Enumerable.Range(0, 100).Select(index => "failed-" + index).First(id => Bucket(id) != Bucket(first.Id));
            var failed = Record(failedId, "episode", 2);
            repository.Save(first); repository.Save(failed);
            var index = Path.Combine(repository.RootPath, ".deleted", "index-v1");
            Directory.CreateDirectory(index);
            var obstruction = Path.Combine(index, Bucket(failed.Id));
            File.WriteAllText(obstruction, "A file blocks this ledger shard directory.");
            var result = LandingDeletionCommand.ExecuteAsync(backup, new[] { first.Id, failed.Id }).GetAwaiter().GetResult();
            Check(result.DeletedIds.SequenceEqual(new[] { first.Id }) && result.LocalErrors.Count == 1 && result.QueueErrors.Count == 0,
                "A later storage failure reports partial completion accurately.");
            File.Delete(obstruction);
            Check(!new LandingRepository(repository.RootPath).Contains(first.Id) && repository.Contains(failed.Id) &&
                state.Load().PendingDeletes.Contains(first.Id), "Successful tombstones and cloud intent survive failure and repository restart.");
            var retry = LandingDeletionCommand.ExecuteAsync(backup, new[] { failed.Id }).GetAwaiter().GetResult();
            Check(retry.LocalErrors.Count == 0 && repository.LoadAll().Count == 0 && state.Load().PendingDeletes.Count == 2,
                "Retry deletes the remaining contact and queues every contact for cloud deletion.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void WindowDeletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-episode-ui-" + Guid.NewGuid().ToString("N"));
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        var previousContext = SynchronizationContext.Current;
        var previousShutdown = Application.Current.ShutdownMode;
        Application.Current.Resources.MergedDictionaries.Add(resources);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null;
        GoogleDriveBackupService? backup = null;
        try
        {
            var repository = new LandingRepository(Path.Combine(directory, "landings"));
            var services = new ApplicationServices();
            var state = new GoogleDriveSyncStateRepository(Path.Combine(directory, "sync.json"));
            state.Save(new GoogleDriveSyncState { AccountPermissionId = "fixture-account" });
            backup = Backup(repository, services.LandingGate, state);
            Set(services, "<Landings>k__BackingField", repository);
            Set(services, "<Backup>k__BackingField", backup);
            Set(services, "<Tokens>k__BackingField", new GoogleOAuthTokenStore(Path.Combine(directory, "token.json")));
            window = new MainWindow(services);
            Set(window, "_runtimeStartupFailed", true); Set(window, "_startupReady", true);
            var first = Record("first", "episode", 1);
            var second = Record("second", "episode", 2);
            second.AircraftTitle = "Fenix A321 second-contact-filter";
            var importedFirst = Record("import-first", "shared-episode", 1, true);
            var importedSecond = Record("import-second", "shared-episode", 2, true);
            importedFirst.TimestampUtc = importedSecond.TimestampUtc = first.TimestampUtc.AddHours(-1);
            var legacy = Record("legacy", null, 1); legacy.TimestampUtc = first.TimestampUtc.AddHours(-2);
            foreach (var record in new[] { first, second, importedFirst, importedSecond, legacy }) repository.Save(record);
            Complete((Task)Invoke(window, "LoadHistoryAsync", new object?[] { null })!);
            var history = Get<ListBox>(window, "LandingHistoryList");
            Check(history.Items.Cast<LandingRecord>().Select(record => record.Id).SequenceEqual(repository.LoadAll().Select(record => record.Id)),
                "Episode decoration must preserve the repository's chronological order.");
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(1180, 740)); root.Arrange(new Rect(0, 0, 1180, 740)); root.UpdateLayout();
            var top = (ListBoxItem)history.ItemContainerGenerator.ContainerFromIndex(0);
            var bottom = (ListBoxItem)history.ItemContainerGenerator.ContainerFromIndex(1);
            Check(Enumerable.Range(0, history.Items.Count).Select(index =>
                    Find<Border>((ListBoxItem)history.ItemContainerGenerator.ContainerFromIndex(index), "Row")!.Padding).Distinct().Count() == 1 &&
                Find<Border>(top, "EpisodeConnectorBelow")!.Visibility == Visibility.Visible &&
                Find<Border>(bottom, "EpisodeConnectorAbove")!.Visibility == Visibility.Visible,
                "All WPF rows remain aligned while same-episode chronological neighbours are connected.");
            SaveImage(root, "history");

            void Open(LandingRecord record) => Invoke(window, "OnDeleteLandingClick", new object[] { new Button { Tag = record }, new RoutedEventArgs(Button.ClickEvent) });
            void Confirm() => Get<Button>(window, "ConfirmDeleteLandingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void WaitDeletion() => PumpUntil(() => !Get<bool>(window, "_deleteLandingInProgress"));
            Open(second);
            foreach (var language in new[] { "ru", "en" })
            {
                LocalizationManager.Apply(language); Invoke(window, "RefreshLocalizedPresentation");
                Check(Get<TextBlock>(window, "DeleteLandingDescription").Text.Contains(LocalizationManager.Format("Delete.ContactCountFormat", 2)),
                    "Confirmation reports actual saved contacts, not stale ContactCount metadata, in both languages.");
                root.UpdateLayout(); SaveImage(root, "confirmation-" + language);
            }
            Get<Button>(window, "CancelDeleteLandingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(repository.LoadAll().Count == 5 && Get<Grid>(window, "DeleteLandingOverlay").Visibility == Visibility.Collapsed,
                "Cancellation leaves every contact untouched.");
            Get<TextBox>(window, "SessionFilterBox").Text = "second-contact-filter";
            Check(history.Items.Count == 1, "The fixture filter hides other contacts.");
            Open(second);
            var third = Record("third", "episode", 3); repository.Save(third);
            Complete((Task)Invoke(window, "LoadHistoryAsync", new object?[] { null })!);
            Confirm();
            Check(repository.LoadAll().Count == 6 && Get<TextBlock>(window, "DeleteLandingDescription").Text.Contains(LocalizationManager.Format("Delete.ContactCountFormat", 3)),
                "A changed contact set renews confirmation before any deletion.");

            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var blocker = Task.Run(() => { lock (services.LandingGate) { entered.Set(); release.Wait(); } });
                Check(entered.Wait(TimeSpan.FromSeconds(5)), "Storage gate fixture did not start.");
                Task? refresh = null;
                try
                {
                    refresh = (Task)Invoke(window, "LoadHistoryAsync", new object?[] { null })!;
                    Confirm();
                    Check(Get<bool>(window, "_deleteLandingInProgress") && history.Items.Count == 0 &&
                        !Get<Button>(window, "CancelDeleteLandingButton").IsEnabled,
                        "All contacts are reserved before a blocked deletion yields to a history refresh.");
                }
                finally { release.Set(); Complete(blocker); }
                Complete(refresh!); WaitDeletion();
            }
            Check(repository.LoadAll().Count == 3 && state.Load().PendingDeletes.Count == 3 &&
                Get<Grid>(window, "DeleteLandingOverlay").Visibility == Visibility.Collapsed,
                "Confirming a filtered contact deletes the whole episode and preserves unrelated/imported landings.");
            Get<TextBox>(window, "SessionFilterBox").Text = "";
            history.SelectedItem = history.Items.Cast<LandingRecord>().Single(record => record.Id == legacy.Id);
            Open(importedFirst); Confirm(); WaitDeletion();
            Check(repository.LoadAll().Single().Id == legacy.Id && ((LandingRecord)history.SelectedItem).Id == legacy.Id,
                "Deleting an imported episode preserves selection outside it.");
            Open(legacy);
            Check(Get<TextBlock>(window, "DeleteLandingDescription").Text.Contains(LocalizationManager.Format("Delete.ContactCountFormat", 1)),
                "Single-contact deletion also requires confirmation with its count.");
            Confirm(); WaitDeletion();
            Check(history.Items.Count == 0 && repository.LoadAll().Count == 0, "Deleting the final episode clears history.");
        }
        finally
        {
            if (window != null)
            {
                Invoke(window, "StopHistoryOperations");
                Complete((Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!);
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
                window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
                window.Close();
            }
            backup?.Dispose();
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            LocalizationManager.Apply("en"); Application.Current.ShutdownMode = previousShutdown;
            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void SaveImage(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LANDINGSTATS_EPISODE_UI_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(1180, 740, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T item && item.Name == name) return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var found = Find<T>(VisualTreeHelper.GetChild(root, index), name); if (found != null) return found; }
        return null;
    }
    private static object? Invoke(MainWindow window, string name, object?[]? args = null) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Private)!.SetValue(owner, value);
    private static void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, __) => { if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(condition(), "Episode history dispatcher regression timed out.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
