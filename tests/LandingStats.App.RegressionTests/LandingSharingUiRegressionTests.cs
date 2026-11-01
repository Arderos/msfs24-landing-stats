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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class LandingSharingUiRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        VerifyStatisticsAndDialogs();
        var directory = Path.Combine(Path.GetTempPath(), "landing-sharing-ui-" + Guid.NewGuid().ToString("N"));
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        var previousContext = SynchronizationContext.Current;
        var previousShutdownMode = Application.Current.ShutdownMode;
        Application.Current.Resources.MergedDictionaries.Add(resources);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null;
        try
        {
            // The real MainWindow is never shown. Its startup, simulator, updater,
            // association registration and cloud work therefore never run.
            window = new MainWindow();
            var repository = new LandingRepository(Path.Combine(directory, "recipient"));
            Set(window, "_repository", repository);
            Set(window, "_airportFacilityRepository", new AirportFacilityRepository(Path.Combine(directory, "airports.gz")));
            Set(window, "_runtimeStartupFailed", true);
            Set(window, "_startupReady", true);
            var own = new LandingRecord { AircraftTitle = "My Airbus A320", InertialFpm = 150 };
            repository.Save(own);
            var source = new LandingRepository(Path.Combine(directory, "sender"));
            var first = new LandingRecord { AircraftTitle = "Shared Boeing 737", InertialFpm = 270 };
            var second = new LandingRecord { AircraftTitle = "Shared Cessna 172", InertialFpm = 95 };
            source.Save(first); source.Save(second);
            var firstPath = Path.Combine(directory, "Boeing" + LandingShareArchive.Extension);
            var secondPath = Path.Combine(directory, "Cessna" + LandingShareArchive.Extension);
            LandingShareArchive.Export(source, first.Id, firstPath);
            LandingShareArchive.Export(source, second.Id, secondPath);

            // A shell activation can arrive before initial history has loaded.
            var initialHistory = (Task)Invoke(window, "LoadHistoryAsync", new object?[] { null })!;
            Complete(window.QueueLandingImports(new[] { firstPath, secondPath }));
            Complete(initialHistory);
            var history = Get<ListBox>(window, "LandingHistoryList");
            Assert(history.Items.Count == 3 && repository.LoadAll().Count(item => item.IsImported) == 2,
                "Multi-file import must merge with concurrent initial history and retain the user's own landing.");
            Assert(Get<TextBlock>(window, "SharingStatusText").Text == LocalizationManager.Format("Sharing.ImportedFormat", 2, 0, 0),
                "The result reports aggregate imported contacts, duplicates and failed files.");
            var importedId = ((LandingRecord)history.SelectedItem).Id;
            Assert(((LandingRecord)history.SelectedItem).ImportedSourceId == first.Id, "Import selects its first available landing.");
            Get<TextBox>(window, "SessionFilterBox").Text = "no aircraft can match this filter";
            Complete(window.QueueLandingImports(new[] { firstPath, firstPath }));
            Assert(history.Items.Count == 3 && ((LandingRecord)history.SelectedItem).Id == importedId &&
                Get<TextBox>(window, "SessionFilterBox").Text.Length == 0,
                "Opening an already imported file reveals its existing landing through an earlier search.");
            Assert(Get<TextBlock>(window, "SharingStatusText").Text == LocalizationManager.Format("Sharing.ImportedFormat", 0, 2, 0),
                "Repeated files are reported as duplicates without new rows.");

            var brokenPath = Path.Combine(directory, "broken" + LandingShareArchive.Extension);
            File.WriteAllText(brokenPath, "invalid shared file");
            Complete(window.QueueLandingImports(new[] { brokenPath, secondPath, "invalid\0" + LandingShareArchive.Extension }));
            Assert(Get<TextBlock>(window, "SharingStatusText").Text == LocalizationManager.Format("Sharing.ImportedFormat", 0, 1, 2) &&
                Get<Expander>(window, "SharingErrorDetails").Visibility == Visibility.Visible &&
                Get<TextBlock>(window, "SharingErrorText").Text.Contains(brokenPath),
                "One bad file cannot prevent the rest of a batch; all failures remain inspectable.");

            var exported = Path.Combine(directory, "exported" + LandingShareArchive.Extension);
            Complete(window.ExportLandingAsync(own.Id, exported));
            Assert(File.Exists(exported), "The window export command writes the selected landing to one package.");
            var failed = window.ExportLandingAsync(own.Id, Path.Combine(exported, "export" + LandingShareArchive.Extension));
            PumpUntil(() => failed.IsCompleted);
            Assert(failed.IsFaulted && Get<TextBlock>(window, "SharingStatusText").Text == LocalizationManager.Text("Sharing.ExportFailed"),
                "An export destination failure must be observable and replace the busy message.");
            _ = failed.Exception;
            Complete((Task)Invoke(window, "WaitForUpdateRestartAsync", new object[] { CancellationToken.None })!);
            Assert((bool)Invoke(window, "CanInstallUpdate")!, "A failed export releases update readiness.");

            Complete(window.QueueLandingImports(new[] { brokenPath, firstPath }));
            VerifyRenderedHistory(window, own.Id);
            VerifyStorageDrain(window, repository, directory);
        }
        finally
        {
            try
            {
                if (window != null)
                {
                    Invoke(window, "StopAcceptingLandingSharing");
                    Invoke(window, "StopHistoryOperations");
                    var pending = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!;
                    PumpUntil(() => pending.IsCompleted);
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

    private static void VerifyStatisticsAndDialogs()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Local);
        var own = new LandingRecord { TimestampUtc = now.ToUniversalTime(), InertialFpm = 120 };
        var imported = new LandingRecord { TimestampUtc = now.ToUniversalTime(), InertialFpm = 900, IsImported = true };
        var bounce = new LandingRecord { TimestampUtc = now.ToUniversalTime(), InertialFpm = 800, ContactNumber = 2 };
        var previousMonth = new LandingRecord { TimestampUtc = now.AddMonths(-1).ToUniversalTime(), InertialFpm = 600 };
        Assert(MainWindow.MonthlyAverageDisplayedFpm(new[] { own, imported, bounce, previousMonth }, now) == -120 &&
            MainWindow.MonthlyAverageDisplayedFpm(new[] { imported }, now) == null,
            "Monthly averages exclude imported contacts, bounces and other months, including imported-only histories.");
        var open = MainWindow.CreateLandingImportDialog();
        var save = MainWindow.CreateLandingExportDialog(own);
        Assert(open.Multiselect && open.CheckFileExists && open.Filter.Contains("*" + LandingShareArchive.Extension),
            "The native open dialog accepts multiple landing files.");
        Assert(save.AddExtension && save.OverwritePrompt && save.FileName.EndsWith(LandingShareArchive.Extension),
            "The native save dialog supplies the application extension and confirms replacing a file.");
        var dropped = new DataObject(DataFormats.FileDrop, new[] { "one.LSLANDING", "two.lslanding", "unsupported.txt" });
        Assert(MainWindow.LandingFilesFromDrop(dropped).Length == 3,
            "Mixed drops deliver every file to validation and match the extension without case sensitivity.");
        Assert(MainWindow.LandingFilesFromDrop(new DataObject(DataFormats.FileDrop, new[] { "other.txt" })).Length == 0 &&
            MainWindow.LandingFilesFromDrop(new DataObject(DataFormats.Text, "one.lslanding")).Length == 0,
            "Unrelated files and pasted strings do not claim a file import drop.");
    }

    private static void VerifyStorageDrain(MainWindow window, LandingRepository repository, string directory)
    {
        var pendingSource = new LandingRepository(Path.Combine(directory, "pending-sender"));
        var pendingRecord = new LandingRecord { AircraftTitle = "Imported during shutdown" };
        pendingSource.Save(pendingRecord);
        var package = Path.Combine(directory, "pending" + LandingShareArchive.Extension);
        LandingShareArchive.Export(pendingSource, pendingRecord.Id, package);
        var repositoryGate = Get<object>(window, "_landingRepositoryGate");
        var exportId = repository.LoadAll().First().Id;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = Task.Run(() => { lock (repositoryGate) { entered.Set(); release.Wait(); } });
        PumpUntil(() => entered.IsSet);
        Task? queued = null;
        try
        {
            queued = window.QueueLandingImports(new[] { package });
            var exportPath = Path.Combine(directory, "drained" + LandingShareArchive.Extension);
            var export = window.ExportLandingAsync(exportId, exportPath);
            var pulse = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            PumpUntil(() => pulse.Task.IsCompleted);
            Assert(!queued.IsCompleted && !export.IsCompleted && !Get<Button>(window, "ImportLandingsButton").IsEnabled,
                "Queued import/export storage never blocks the dispatcher and exposes its busy state.");
            Assert(!window.ReadGuardianHealth(Stopwatch.GetTimestamp()).SafeToRestart && !(bool)Invoke(window, "CanInstallUpdate")! &&
                !(bool)Invoke(window, "TrySealSharingForRestart")!,
                "Guardian and updater cannot retire a process that owns an accepted sharing write.");
            var update = (Task)Invoke(window, "WaitForUpdateRestartAsync", new object[] { CancellationToken.None })!;
            Assert(!update.IsCompleted, "Update readiness asynchronously waits for pending sharing work.");
            using var cancellation = new CancellationTokenSource();
            var cancelledWait = (Task)Invoke(window, "WaitForSharingStorageAsync", new object[] { cancellation.Token })!;
            cancellation.Cancel();
            PumpUntil(() => cancelledWait.IsCompleted);
            Assert(cancelledWait.IsCanceled && !queued.IsCompleted, "Cancelling an update waiter cannot cancel accepted imports.");
            Invoke(window, "StopAcceptingLandingSharing");
            var rejected = window.QueueLandingImports(new[] { package });
            PumpUntil(() => rejected.IsCompleted);
            Assert(rejected.IsCanceled, "A closing window rejects new activations so their durable request remains pending.");
            var draining = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!;
            Assert(!draining.IsCompleted, "Shutdown includes all accepted sharing tasks in its history drain.");
            release.Set();
            Complete(blocker); Complete(draining); Complete(update);
            PumpUntil(() => queued.IsCompleted && export.IsCompleted);
            Assert(queued.IsCanceled && export.IsCanceled && File.Exists(exportPath) &&
                repository.LoadAll().Any(record => record.IsImported && record.ImportedSourceId == pendingRecord.Id),
                "Accepted storage drains after admission closes, while UI activation acknowledgement is cancelled.");
        }
        finally
        {
            release.Set();
            Complete(blocker);
            if (queued != null) PumpUntil(() => queued.IsCompleted);
        }
    }

    private static void VerifyRenderedHistory(MainWindow window, string ownId)
    {
        Invoke(window, "ApplySessionFilter", new object?[] { ownId });
        PumpUntil(() => (window.DataContext as LandingRecord)?.Id == ownId);
        var history = Get<ListBox>(window, "LandingHistoryList");
        var root = (FrameworkElement)window.Content;
        window.Width = 1180; window.Height = 740;
        foreach (var language in new[] { "en", "ru" })
        {
            LocalizationManager.Apply(language);
            Invoke(window, "RefreshLocalizedPresentation");
            root.Measure(new Size(1180, 740)); root.Arrange(new Rect(0, 0, 1180, 740)); root.UpdateLayout();
            var imported = history.Items.Cast<LandingRecord>().First(record => record.IsImported);
            var row = (ListBoxItem)history.ItemContainerGenerator.ContainerFromItem(imported);
            Assert(row != null, "The imported landing must generate a rendered history row.");
            var label = Find<TextBlock>(row!, "ImportedLandingLabel");
            var share = Find<Button>(row!, "ShareLandingButton");
            var delete = Find<Button>(row!, "DeleteLandingButton");
            var rowBorder = Find<Border>(row!, "Row");
            Assert(label.Visibility == Visibility.Visible && label.Text == LocalizationManager.Text("Sharing.Imported") &&
                ((SolidColorBrush)rowBorder.Background).Color == Color.FromRgb(0x17, 0x25, 0x2D),
                "Imported rows retain their translated marker and distinct shade when another landing is selected.");
            Assert(share.Visibility == Visibility.Collapsed && delete.Visibility == Visibility.Collapsed,
                "History actions stay hidden before hover.");
            // Supply WPF's read-only input state at the fixture boundary; the
            // production templates and their ancestor bindings react normally.
            var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            row!.SetValue(hoverKey, true);
            root.UpdateLayout();
            Assert(share.Visibility == Visibility.Visible && delete.Visibility == Visibility.Visible,
                "Hover exposes both actual row action buttons.");
            var sharePoint = share.TransformToAncestor(row).Transform(new Point(0, 0));
            var deletePoint = delete.TransformToAncestor(row).Transform(new Point(0, 0));
            var labelPoint = label.TransformToAncestor(row).Transform(new Point(0, 0));
            Assert(share.ActualWidth >= 28 && delete.ActualWidth >= 28 && sharePoint.X + share.ActualWidth < deletePoint.X &&
                deletePoint.X + delete.ActualWidth <= row.ActualWidth && labelPoint.Y + label.ActualHeight <= row.ActualHeight,
                "At the minimum window width share sits left of delete with a gap, and neither actions nor marker are clipped.");
            Assert(ReferenceEquals(share.Tag, imported) && ReferenceEquals(delete.Tag, imported),
                "Both row actions operate on the original stored landing, not its presentation wrapper.");
            var importButton = Get<Button>(window, "ImportLandingsButton");
            Assert(importButton.ActualWidth > 0 && importButton.IsEnabled && window.AllowDrop,
                "The main screen exposes the import action and receives file drops.");
            var evidence = Environment.GetEnvironmentVariable("LANDINGSTATS_SHARING_UI_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(window.Background, null, new Rect(0, 0, 1180, 740));
                }
                var bitmap = new RenderTargetBitmap(1180, 740, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                bitmap.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(evidence, "landing-sharing-" + language + ".png"));
                png.Save(stream);
            }
            row.SetValue(hoverKey, false);
        }
    }

    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindOrNull<T>(VisualTreeHelper.GetChild(root, index), name);
            if (found != null) return found;
        }
        throw new InvalidOperationException("Missing visual: " + name);
    }

    private static T? FindOrNull<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindOrNull<T>(VisualTreeHelper.GetChild(root, index), name);
            if (found != null) return found;
        }
        return null;
    }

    private static object? Invoke(MainWindow window, string name, object?[]? arguments = null) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, __) => { if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert(condition(), "Sharing UI dispatcher regression timed out.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
