using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.TelemetryUpload;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class DeliveryRecoveryRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-recovery-" + Guid.NewGuid().ToString("N"));
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        var oldMode = Application.Current.ShutdownMode; Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var oldLanguage = LocalizationManager.EffectiveLanguage;
        var oldContext = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null; var done = false;
        try
        {
            window = new MainWindow(); // Not shown or loaded. Every identity/report path is fixture-owned.
            Set(window, "_repository", new LandingRepository(Path.Combine(root, "unused-history")));
            Set(window, "_historyLoading", false); Set(window, "_startupReady", true);
            Presentation(window);
            Await(RecoveryCommand(window, Path.Combine(root, "command")));
            Await(Task.Run(() => CleanupRetry(Path.Combine(root, "cleanup"))));
            Await(Task.Run(() => ActiveWorkerReplacement(Path.Combine(root, "active"))));
            StructuralRecoveryFencesOldEnrollment(Path.Combine(root, "structural"));
            done = true;
        }
        finally
        {
            if (window != null)
            {
                Set(window, "_isClosed", true);
                typeof(MainWindow).GetMethod("StopHistoryOperations", Private)!.Invoke(window, Array.Empty<object>());
                Await((Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!);
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
                window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
                window.Close();
            }
            Application.Current.Resources.MergedDictionaries.Remove(resources); Application.Current.ShutdownMode = oldMode;
            LocalizationManager.Apply(oldLanguage);
            SynchronizationContext.SetSynchronizationContext(oldContext);
            if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    private static void Presentation(MainWindow window)
    {
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        var apply = (Func<RuntimePresentationSnapshot, CancellationToken, Task>)Delegate.CreateDelegate(typeof(Func<RuntimePresentationSnapshot, CancellationToken, Task>), window,
            typeof(MainWindow).GetMethod("ApplyRuntimeSnapshotAsync", Private)!);
        foreach (var language in new[] { "en", "ru" })
        {
            LocalizationManager.Apply(language);
            foreach (var reason in new[] { "HttpRequestException", "Deferred", "Telemetry.RegistrationClosed", "CryptographicException" })
            {
                projection.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = OutboxState.Pending, LastFailure = reason });
                Check(projection.Snapshot.Failure == null && !projection.Snapshot.AnalysisFailed, "Optional delivery became an analysis failure.");
                Set(window, "_automaticTelemetryEnabled", 0);
                Await(Task.Run(() => apply(projection.Snapshot, CancellationToken.None)));
                Check(Get<TextBlock>(window, "UploadStatusText").Visibility == Visibility.Visible &&
                    !Get<bool>(window, "_uploadStatusIsError") && !Get<TextBlock>(window, "UploadStatusText").Text.Contains(reason),
                    "Manual delivery rendered a raw reason or processing error.");
                Set(window, "_automaticTelemetryEnabled", 1);
                Await(Task.Run(() => apply(projection.Snapshot, CancellationToken.None)));
                Check(Get<TextBlock>(window, "SettingsTelemetryStatusText").Visibility == Visibility.Visible &&
                    Get<TextBlock>(window, "SettingsTelemetryStatusText").Text == Get<TextBlock>(window, "UploadStatusText").Text,
                    "Automatic delivery lost its localized settings status.");
            }
        }
        projection.Failure("analysis.episodes", Guid.NewGuid(), "analysis fixture");
        projection.Failure("storage.reports", Guid.NewGuid(), "UnauthorizedAccessException");
        projection.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = OutboxState.Pending, LastFailure = "Deferred" });
        var failure = projection.Snapshot.Failure;
        Check(failure?.Component == "analysis.episodes" && projection.Snapshot.DeliveryFailure?.Component == "storage.reports", "Delivery overwrote analysis failure.");
        Await(Task.Run(() => apply(projection.Snapshot, CancellationToken.None)));
        Check(Get<string>(window, "_uploadStatusKey") == "Runtime.ProcessingFailedFormat", "Analysis failure was hidden by delivery status.");
        projection.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = OutboxState.Pending, LastFailure = "HttpRequestException" });
        Await(Task.Run(() => apply(projection.Snapshot, CancellationToken.None)));
        Check(ReferenceEquals(failure, projection.Snapshot.Failure) && Get<string>(window, "_uploadStatusKey") == "Runtime.ProcessingFailedFormat",
            "A later delivery retry replaced the retained analysis notice.");
        var recovered = new RuntimePresentation((_, __) => Task.CompletedTask);
        recovered.Upload(new OutboxEntry { Id = Guid.NewGuid(), Automatic = true, State = OutboxState.Revoked, LastFailure = "CryptographicException" });
        recovered.Failure("analysis.episodes", Guid.NewGuid(), "retained analysis");
        recovered.Failure("storage.reports", Guid.NewGuid(), "UnauthorizedAccessException");
        recovered.IdentityRecovered();
        recovered.Cloud(new RuntimeCloudNotice(1, DateTime.UtcNow, false)); // Unrelated publication must not resurrect the old key error.
        Set(window, "_automaticTelemetryEnabled", 0);
        Await(Task.Run(() => apply(recovered.Snapshot, CancellationToken.None)));
        Check(recovered.Snapshot.UploadReason.Length == 0 && recovered.Snapshot.Failure?.Message == "retained analysis" &&
            recovered.Snapshot.DeliveryFailure?.Message == "UnauthorizedAccessException" && Get<string>(window, "_telemetryDeliveryKey") == "Delivery.StoragePending",
            "Identity recovery resurrected its error or erased unrelated failures.");
        var component = new RuntimePresentation((_, __) => Task.CompletedTask);
        component.Failure("integration.api", Guid.Empty, "IOException");
        Await(Task.Run(() => apply(component.Snapshot, CancellationToken.None)));
        Check(Get<TextBlock>(window, "UploadStatusText").Text.Contains("integration.api"), "Component failure displayed a zero GUID instead of its owner.");
        component.Recovered("integration.api", Guid.Empty);
        Await(Task.Run(() => apply(component.Snapshot, CancellationToken.None)));
        Check(!Get<bool>(window, "_uploadStatusIsError") && !Get<bool>(window, "_runtimeHasFailure"), "Resolved service failure left the footer red.");
        foreach (var state in new[] { OutboxState.QuarantinePending, OutboxState.Quarantined })
        {
            component.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = state });
            Await(Task.Run(() => apply(component.Snapshot, CancellationToken.None)));
            Check(Get<string>(window, "_telemetryDeliveryKey") == "Delivery.Rejected", "Terminal rejection promised an automatic retry.");
        }
        component.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = OutboxState.Pending, LastFailure = "IOException" });
        Await(Task.Run(() => apply(component.Snapshot, CancellationToken.None)));
        Check(Get<string>(window, "_telemetryDeliveryKey") == "Delivery.Pending", "Ambiguous HTTP/file I/O failure claimed a file-access cause.");
        Check(Get<Button>(window, "RecoverTelemetryIdentityButton").Visibility == Visibility.Collapsed && !Get<Button>(window, "RecoverTelemetryIdentityButton").IsEnabled,
            "Working/unknown identity exposed destructive recovery.");
        var readyEpisode = Guid.NewGuid(); var readyRecord = new LandingRecord { Id = Guid.NewGuid().ToString("N"), TimestampUtc = DateTime.UtcNow };
        var notice = new RuntimeEpisodeNotice(new RuntimeEpisodeUpdate(readyEpisode,
            ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { readyRecord }, new TelemetryDecisionJournal().Snapshot()),
            new BugReportCandidate(1, Array.Empty<TelemetrySample>(), "fixture", "fixture", "fixture", "fixture", Array.Empty<string>(), new[] { readyRecord }), DateTime.UtcNow, 1, false));
        var ready = new RuntimePresentationSnapshot(999, null, false, readyEpisode, 1, false, new[] { notice }, Array.Empty<SupervisionSnapshot>(), Guid.NewGuid(), OutboxState.Pending)
            { UploadReason = "HttpRequestException" };
        Await(Task.Run(() => apply(ready, CancellationToken.None)));
        Check(Get<string>(window, "_uploadStatusKey") == "Footer.ReportReady" && Get<string>(window, "_telemetryDeliveryKey") == "Delivery.NetworkPending",
            "Previous queued delivery replaced the latest report's ready status.");
        // A valid manual report remains selectable after its bounded notice
        // leaves the projection. Recovering another service restores its status.
        var withoutNotice = new RuntimePresentationSnapshot(1000, null, false, readyEpisode, 1, false, Array.Empty<RuntimeEpisodeNotice>(),
            Array.Empty<SupervisionSnapshot>(), Guid.Empty, null)
            { Failure = new RuntimeFailureNotice(1000, "integration.api", Guid.Empty, "IOException") };
        Await(Task.Run(() => apply(withoutNotice, CancellationToken.None)));
        Check(Get<bool>(window, "_uploadStatusIsError"), "Missing-notice fixture did not show its actual service failure.");
        withoutNotice.Failure = null;
        Await(Task.Run(() => apply(withoutNotice, CancellationToken.None)));
        Check(!Get<bool>(window, "_uploadStatusIsError") && Get<string>(window, "_uploadStatusKey") == "Footer.ReportReady" &&
            Get<Guid>(window, "_runtimeReportEpisode") == readyEpisode, "Recovered service left stale red text on a retained report without a current episode notice.");
        Set(window, "_bugReportSubmitting", true);
        typeof(MainWindow).GetMethod("RefreshBugReportButton", Private)!.Invoke(window, Array.Empty<object>());
        var reportButton = Get<Button>(window, "ReportBugButton"); reportButton.ApplyTemplate();
        Check(!reportButton.IsEnabled && reportButton.Opacity == 1 && ((Border)reportButton.Template.FindName("Chrome", reportButton)).Opacity == .45,
            "Disabled report button opacity was multiplied twice.");
        Set(window, "_bugReportSubmitting", false);
        using (var cancellation = new CancellationTokenSource())
        {
            Set(window, "_telemetryRecoveryCancellation", cancellation); Set(window, "_settingsPendingOperations", 1);
            typeof(MainWindow).GetMethod("RefreshDeliverySettingsPresentation", Private)!.Invoke(window, Array.Empty<object>());
            var cancel = Get<Button>(window, "CancelTelemetryRecoveryButton");
            Check(cancel.Visibility == Visibility.Visible && cancel.IsEnabled, "Pending recovery has no usable cancellation control while Settings is busy.");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(cancellation.IsCancellationRequested && !cancel.IsEnabled, "Recovery cancellation control did not cancel the pending wait.");
            Set(window, "_telemetryRecoveryCancellation", null); Set(window, "_settingsPendingOperations", 0);
            typeof(MainWindow).GetMethod("RefreshDeliverySettingsPresentation", Private)!.Invoke(window, Array.Empty<object>());
        }
        var record = new LandingRecord(); record.Series.Add(new LandingSeriesPoint { TimeSeconds = 0 }); record.Series.Add(new LandingSeriesPoint { TimeSeconds = 1 });
        window.DataContext = record;
        typeof(MainWindow).GetMethod("ApplyZoom", Private)!.Invoke(window, new object?[] { null, null });
        Check(Get<Button>(window, "ChartWindowButton").IsEnabled, "Chart fixture was not enabled.");
        typeof(MainWindow).GetMethod("ClearRecordPresentation", Private)!.Invoke(window, Array.Empty<object>());
        var button = Get<Button>(window, "ChartWindowButton"); button.ApplyTemplate();
        Check(!button.IsEnabled && Equals(button.Content, LocalizationManager.Text("Chart.NoData")) &&
            ((Border)button.Template.FindName("Chrome", button)).Opacity < 1, "Cleared chart retained enabled text or appearance.");
    }
    private static async Task RecoveryCommand(MainWindow window, string root)
    {
        var identityRoot = Path.Combine(root, "identity"); var old = new TelemetryUploadIdentityStore(identityRoot);
        old.AcceptConsent(); var oldId = old.Identity().InstallId; var endpoint = new Uri("https://fixture.invalid"); old.MarkEnrolled(endpoint, true);
        var identityPath = Path.Combine(identityRoot, "identity.json"); var bytes = File.ReadAllBytes(identityPath);
        var bad = Convert.ToBase64String(ProtectedData.Protect(new byte[] { 1, 2, 3 }, Encoding.UTF8.GetBytes("different entropy"), DataProtectionScope.CurrentUser));
        File.WriteAllText(identityPath, Regex.Replace(Encoding.UTF8.GetString(bytes), "\"protected_private_key\":\"[^\"]*\"", "\"protected_private_key\":\"" + bad + "\""));
        var original = File.ReadAllBytes(identityPath);
        try { new TelemetryUploadIdentityStore(identityRoot).Identity(); throw new InvalidOperationException("Fixture key unexpectedly decrypted."); }
        catch (CryptographicException) { }
        Check(new TelemetryUploadIdentityStore(identityRoot).ConsentAccepted, "Plain consent required DPAPI.");
        var legacy = Path.Combine(root, "legacy"); var unknown = Archive(legacy, "old_raw.zip", 1); var manual = Archive(legacy, "manual_bug_raw.zip", 2);
        var unknownHash = EpisodeTransactionStore.Hash(unknown); var manualHash = EpisodeTransactionStore.Hash(manual);
        var unknownId = Guid.ParseExact(unknownHash.Substring(0, 32), "N"); var manualId = Guid.ParseExact(manualHash.Substring(0, 32), "N");
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
        using var runtime = new LandingRuntime(Path.Combine(root, "runtime"), AppDomain.CurrentDomain.BaseDirectory, identityRoot, repository, gate,
            (_, __) => Task.CompletedTask, (_, __) => { }, (role, _) => role == ComponentRole.Simulator ? "landing" :
                role == ComponentRole.Analysis ? "analysis-local" : role == ComponentRole.Upload ? "upload-ok" : "normal", legacyQueue: legacy);
        Set(window, "_runtime", runtime); Set(window, "_automaticTelemetryEnabled", 0);
        try
        {
            await runtime.StartAsync(true);
            await Until(() => runtime.Store.Enumerate().Any(id => runtime.Store.Read(id).Stage == EpisodeWorkStage.Completed), "Recording failed with an unavailable identity.");
            var episode = runtime.Store.Enumerate().Single();
            await Until(() => runtime.Outbox.Enumerate().Contains(unknownId) && runtime.Outbox.Read(unknownId).LastFailure == "CryptographicException",
                "Automatic legacy report did not preserve its identity failure.");
            await runtime.SetAutomaticDeliveryAsync(false);
            var requestId = await runtime.RequestManualReportAsync(episode);
            await Until(() => runtime.Outbox.Enumerate().Contains(requestId) && runtime.Outbox.Read(requestId).LastFailure == "CryptographicException" &&
                runtime.Outbox.Enumerate().Contains(unknownId), "Manual request or prior-consent legacy intake failed.");
            var manualRequestHash = runtime.Outbox.Read(requestId).Hash;
            var policy = runtime.Outbox.Policy.Revision;
            Check(!await window.RecoverTelemetryIdentityAsync(() => false) && File.ReadAllBytes(identityPath).SequenceEqual(original), "Declined recovery changed the key.");
            Check(await window.RecoverTelemetryIdentityAsync(() => true), "Explicit settings recovery failed.");
            var backups = Directory.GetFiles(identityRoot, "identity.json.replaced-*");
            var fresh = new TelemetryUploadIdentityStore(identityRoot);
            Check(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(original) && fresh.Identity().InstallId != oldId && fresh.ConsentAccepted,
                "Recovery lost original identity bytes or renewed consent.");
            Check(runtime.Outbox.Read(unknownId).LastFailure.Length == 0 && runtime.Snapshot.UploadReason != "CryptographicException",
                "Explicit recovery retained an obsolete identity failure while automatic uploads were disabled.");
            Check(!runtime.Outbox.Policy.AutomaticEnabled && runtime.Outbox.Policy.Revision == policy, "Recovery changed automatic policy.");
            Check(runtime.Outbox.Read(unknownId).Hash == unknownHash && File.Exists(runtime.Outbox.Read(unknownId).Path), "Disabled automatic legacy report lost its bytes.");
            await Until(() => runtime.Outbox.Read(requestId).State == OutboxState.Delivered && runtime.Outbox.Read(manualId).State == OutboxState.Delivered,
                "Recovery did not resume retained manual requests in the same runtime.");
            Check(runtime.Outbox.Read(requestId).Hash == manualRequestHash && runtime.Outbox.Read(manualId).Hash == manualHash, "Recovery changed queued report content.");
            await runtime.SetAutomaticDeliveryAsync(true);
            await Until(() => runtime.Outbox.Read(unknownId).State == OutboxState.Delivered, "Previously consented legacy report did not resume after policy opt-in.");
        }
        finally { await runtime.StopAsync(); }
        // A cached old sender cannot reapply its enrollment after a replacement.
        try { old.MarkEnrolled(endpoint, true); throw new InvalidOperationException("Stale identity wrote enrollment."); }
        catch (CryptographicException) { }
    }
    private static async Task CleanupRetry(string root)
    {
        var legacy = Path.Combine(root, "legacy"); var path = Archive(legacy, "readonly_bug_raw.zip", 1);
        var hash = EpisodeTransactionStore.Hash(path); File.SetAttributes(path, FileAttributes.ReadOnly);
        var box = new DurableOutbox(Path.Combine(root, "outbox"), Path.Combine(root, "archives"));
        var intake = new ArchiveIntakeOwner(Path.Combine(root, "intake"), Path.Combine(root, "archives"), legacy, Path.Combine(root, "identity"),
            new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64)), box, () => { });
        var failures = new ConcurrentQueue<string>(); intake.Failed += (_, reason) => failures.Enqueue(reason);
        using var owner = new ManagedComponentGeneration(1, intake.RunAsync); await owner.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => failures.Count > 0, "Read-only cleanup did not reach its real filesystem failure.");
            await Task.Delay(4200);
            Check(failures.Count == 1 && failures.Single() == "UnauthorizedAccessException" && box.Enumerate().Count() == 1 && File.Exists(path),
                "Cleanup emitted repeated/raw failures or duplicated registration.");
            File.SetAttributes(path, FileAttributes.Normal);
            await Until(() => !File.Exists(path), "Cleanup did not recover through its owning retry.");
            Check(box.Read(Guid.ParseExact(hash.Substring(0, 32), "N")).Hash == hash, "Cleanup changed immutable capture identity.");
        }
        finally { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); await owner.StopAsync(TimeSpan.FromSeconds(10)); }
    }
    private static async Task ActiveWorkerReplacement(string root)
    {
        foreach (var automatic in new[] { false, true })
        {
            var area = Path.Combine(root, automatic ? "automatic" : "manual"); var identityRoot = Path.Combine(area, "identity");
            var identity = new TelemetryUploadIdentityStore(identityRoot); var oldId = identity.Identity().InstallId;
            var endpoint = new Uri("https://fixture.invalid"); identity.MarkEnrolled(endpoint, true);
            var path = Archive(Path.Combine(area, "archives"), "pending_raw.zip", 3); var hash = EpisodeTransactionStore.Hash(path); var id = Guid.NewGuid();
            var box = new DurableOutbox(Path.Combine(area, "outbox"), Path.GetDirectoryName(path)!); box.SetAutomaticEnabled(automatic); box.Register(id, path, hash, automatic, DateTime.UtcNow);
            var owner = new UploadRuntimeOwner(box, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), identityRoot,
                Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency), _ => { }, _ => "upload-late");
            ProcessWorkerHost? worker = null; owner.WorkerStarted += value => Volatile.Write(ref worker, value);
            var attempt = owner.AttemptAsync(id, CancellationToken.None);
            await Until(() => worker?.LastHealth?.OperationId != null && worker!.LastHealth!.OperationId != Guid.Empty, "Held upload did not start.");
            // The old worker already holds the signing identity. Recovery must
            // retire it before replacing the now-unavailable on-disk key.
            var identityPath = Path.Combine(identityRoot, "identity.json");
            var bad = Convert.ToBase64String(ProtectedData.Protect(new byte[] { 1, 2, 3 }, Encoding.UTF8.GetBytes("different entropy"), DataProtectionScope.CurrentUser));
            File.WriteAllText(identityPath, Regex.Replace(File.ReadAllText(identityPath), "\"protected_private_key\":\"[^\"]*\"", "\"protected_private_key\":\"" + bad + "\""));
            await owner.RecoverUnavailableIdentityAsync(true);
            Check(worker!.Exit.IsCompleted && !await attempt, "Recovery returned before the old manual/automatic worker retired.");
            var fresh = new TelemetryUploadIdentityStore(identityRoot);
            Check(fresh.Identity().InstallId != oldId && !fresh.IsEnrolled(endpoint) && box.Policy.AutomaticEnabled == automatic && File.Exists(path) && EpisodeTransactionStore.Hash(path) == hash,
                "Active-worker recovery restored old enrollment, changed policy, or lost pending bytes.");
            foreach (var enrolled in new[] { true, false, true })
            {
                try { identity.MarkEnrolled(endpoint, enrolled); throw new InvalidOperationException("Stale store changed enrollment."); }
                catch (CryptographicException) { }
            }
        }
    }
    private static void StructuralRecoveryFencesOldEnrollment(string root)
    {
        var store = new TelemetryUploadIdentityStore(root); store.AcceptConsent(); var oldId = store.Identity().InstallId;
        var endpoint = new Uri("https://fixture.invalid"); store.MarkEnrolled(endpoint, true);
        var path = Path.Combine(root, "identity.json");
        File.WriteAllText(path, Regex.Replace(File.ReadAllText(path), "\"protected_private_key\":\"[^\"]*\"", "\"protected_private_key\":\"not-base64\""));
        var damaged = File.ReadAllBytes(path);
        foreach (var enrolled in new[] { true, false, true })
        {
            try { store.MarkEnrolled(endpoint, enrolled); throw new InvalidOperationException("Old sender wrote structurally recovered enrollment."); }
            catch (CryptographicException) { }
        }
        var fresh = new TelemetryUploadIdentityStore(root);
        Check(fresh.Identity().InstallId != oldId && fresh.ConsentAccepted && !fresh.IsEnrolled(endpoint) &&
            File.ReadAllBytes(Directory.GetFiles(root, "identity.json.unreadable-*").Single()).SequenceEqual(damaged),
            "Structural recovery lost consent/original bytes or accepted stale enrollment.");
    }
    private static string Archive(string folder, string name, long sequence)
    {
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var info = new StreamWriter(zip.CreateEntry("session.txt").Open())) info.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
        using var csv = new StreamWriter(zip.CreateEntry("telemetry.csv").Open()); csv.WriteLine(TelemetryCsv.Header);
        csv.WriteLine(TelemetryCsv.Format(new TelemetrySample { Sequence = sequence, SimulationTimeSeconds = 1 })); return path;
    }
    private static async Task Until(Func<bool> condition, string message)
    { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException(message); await Task.Delay(25); } }
    private static void Await(Task task)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, __) => { if (task.IsCompleted || watch.Elapsed > TimeSpan.FromSeconds(75)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(task.IsCompleted, "Delivery recovery fixture timed out."); task.GetAwaiter().GetResult();
    }
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
