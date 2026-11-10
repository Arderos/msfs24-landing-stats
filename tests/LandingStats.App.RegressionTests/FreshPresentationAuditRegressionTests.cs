using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.TelemetryUpload;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class FreshPresentationAuditRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll() => Run(false);
    public static void IdentityRecoveryPreservesIndependentDeliveryState() => Run(true);

    private static void Run(bool identityRecovery)
    {
        var root = Path.Combine(Path.GetTempPath(), "fresh-presentation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        var oldMode = Application.Current.ShutdownMode;
        var oldLanguage = LocalizationManager.EffectiveLanguage;
        var oldContext = SynchronizationContext.Current;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null;
        try
        {
            // An unloaded window uses only this fixture repository; window startup never runs.
            window = new MainWindow();
            Set(window, "_repository", new LandingRepository(Path.Combine(root, "history")));
            Set(window, "_historyLoading", false);
            Set(window, "_startupReady", true);
            var apply = (Func<RuntimePresentationSnapshot, CancellationToken, Task>)Delegate.CreateDelegate(
                typeof(Func<RuntimePresentationSnapshot, CancellationToken, Task>), window,
                typeof(MainWindow).GetMethod("ApplyRuntimeSnapshotAsync", Private)!);
            if (identityRecovery) Await(RecoveryCommand(window, apply, Path.Combine(root, "identity-recovery")));
            else foreach (var language in new[] { "en", "ru" })
            {
                LocalizationManager.Apply(language);
                DeliveryRetirement(window, apply);
            }
        }
        finally
        {
            if (window != null)
            {
                Set(window, "_isClosed", true);
                typeof(MainWindow).GetMethod("StopHistoryOperations", Private)!.Invoke(window, Array.Empty<object>());
                Await((Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!);
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window,
                    typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
                window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
                    typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
                window.Close();
            }
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            Application.Current.ShutdownMode = oldMode;
            LocalizationManager.Apply(oldLanguage);
            SynchronizationContext.SetSynchronizationContext(oldContext);
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }

    private static async Task RecoveryCommand(MainWindow window, Func<RuntimePresentationSnapshot, CancellationToken, Task> apply, string root)
    {
        var identityRoot = Path.Combine(root, "identity");
        var identity = new TelemetryUploadIdentityStore(identityRoot);
        var formerId = identity.Identity().InstallId;
        var identityPath = Path.Combine(identityRoot, "identity.json");
        var unreadable = Convert.ToBase64String(ProtectedData.Protect(new byte[] { 1, 2, 3 },
            Encoding.UTF8.GetBytes("fresh UI audit different entropy"), DataProtectionScope.CurrentUser));
        File.WriteAllText(identityPath, Regex.Replace(File.ReadAllText(identityPath),
            "\"protected_private_key\":\"[^\"]*\"", "\"protected_private_key\":\"" + unreadable + "\""));
        var runtimeRoot = Path.Combine(root, "runtime");
        var archives = Path.Combine(runtimeRoot, "archives"); Directory.CreateDirectory(archives);
        var prepared = new DurableOutbox(Path.Combine(runtimeRoot, "outbox"), archives);
        prepared.SetAutomaticEnabled(true);
        Guid Add(byte value)
        {
            var id = Guid.NewGuid(); var path = Path.Combine(archives, id.ToString("N") + ".zip");
            File.WriteAllBytes(path, new[] { value });
            prepared.Register(id, path, EpisodeTransactionStore.Hash(path), true, DateTime.UtcNow);
            return id;
        }
        var waiting = Add(1); var damaged = Add(2);
        var claim = prepared.TryClaim(waiting, new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload))!;
        Check(prepared.Retry(claim, "CryptographicException"), "Fixture did not persist its identity failure.");
        var damagedPath = Path.Combine(runtimeRoot, "outbox", damaged.ToString("N") + ".json");
        File.WriteAllText(damagedPath, "{malformed metadata");
        prepared.RecoverTerminalEntries();
        var repository = new LandingRepository(Path.Combine(root, "history"));
        using var runtime = new LandingRuntime(runtimeRoot, AppDomain.CurrentDomain.BaseDirectory, identityRoot, repository, new object(),
            (_, __) => Task.CompletedTask, (_, __) => { },
            (role, _) => role == ComponentRole.Analysis ? "analysis-local" : role == ComponentRole.Upload ? "upload-ok" : "normal");
        Set(window, "_runtime", runtime);
        try
        {
            // Automatic work remains disabled, making the recovery result independent
            // of upload timing. The real command replaces the key and refreshes outbox state.
            await runtime.StartAsync(false);
            await Until(() => runtime.Snapshot.UploadIdentityUnavailable && runtime.Snapshot.DeliveryQuarantined,
                "Fixture lost its independent identity and metadata failures.");
            var projection = (RuntimePresentation)typeof(LandingRuntime).GetField("_presentation", Private)!.GetValue(runtime)!;
            projection.Failure("integration.api", Guid.Empty, "independent runtime failure");
            await Task.Run(() => apply(runtime.Snapshot, CancellationToken.None));
            Check(await window.RecoverTelemetryIdentityAsync(() => true), "The real settings identity-recovery command failed.");
            Check(new TelemetryUploadIdentityStore(identityRoot).Identity().InstallId != formerId &&
                !runtime.Snapshot.UploadIdentityUnavailable && runtime.Snapshot.DeliveryQuarantined,
                "Identity recovery failed to preserve the independent damaged queue entry.");
            foreach (var language in new[] { "en", "ru" })
            {
                LocalizationManager.Apply(language);
                typeof(MainWindow).GetMethod("RefreshDeliverySettingsPresentation", Private)!.Invoke(window, Array.Empty<object>());
                Check(Get<string>(window, "_telemetryRecoveryKey") == "Settings.TelemetryRecovered" &&
                    Get<TextBlock>(window, "SettingsTelemetryStatusText").Text == LocalizationManager.Text("Delivery.Damaged"),
                    "Successful identity recovery replaced independent queue damage with a generic pending status.");
                Check(Get<Button>(window, "RecoverTelemetryIdentityButton").Visibility == Visibility.Collapsed,
                    "A recovered identity kept its destructive recovery action visible.");
                Check(Get<string>(window, "_uploadStatusKey") == "Runtime.ComponentFailedFormat" && Get<bool>(window, "_uploadStatusIsError"),
                    "Identity recovery displaced the independently retained runtime failure in the footer.");
            }
        }
        finally { await runtime.StopAsync(); Set(window, "_runtime", null); }
    }

    private static void DeliveryRetirement(MainWindow window, Func<RuntimePresentationSnapshot, CancellationToken, Task> apply)
    {
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        var report = Guid.NewGuid();
        foreach (var retired in new[] { false, true })
        {
            projection.Failure("storage.reports", report, "UnauthorizedAccessException");
            Apply(apply, projection);
            Check(Get<TextBlock>(window, "UploadStatusText").Text == LocalizationManager.Text("Delivery.StoragePending"),
                "The fixture did not publish its delivery failure in the footer.");
            if (retired) projection.Retired("storage.reports");
            else projection.DeliveryRecovered(report);
            Apply(apply, projection);
            Check(Get<TextBlock>(window, "SettingsTelemetryStatusText").Text == LocalizationManager.Text("Delivery.Idle"),
                "Recovered delivery did not become idle in Settings.");
            Check(Get<string>(window, "_uploadStatusKey") == "Footer.ReportWaiting" &&
                Get<TextBlock>(window, "UploadStatusText").Text == LocalizationManager.Text("Footer.ReportWaiting"),
                "Retiring the last delivery failure left a stale storage warning in the footer.");
        }

        projection.DeliveryQuarantine(true);
        Apply(apply, projection);
        projection.DeliveryQuarantine(false);
        Apply(apply, projection);
        Check(Get<string>(window, "_uploadStatusKey") == "Footer.ReportWaiting",
            "Repairing the last quarantined entry left a stale damaged-delivery footer.");

        projection.IdentityAvailability(true);
        Apply(apply, projection);
        Check(Get<string>(window, "_uploadStatusKey") == "Delivery.IdentityUnavailable",
            "Retained identity unavailability was omitted before the first upload notice.");
        projection.IdentityAvailability(false);
        Apply(apply, projection);
        Check(Get<string>(window, "_uploadStatusKey") == "Footer.ReportWaiting",
            "Recovering identity availability left a stale unavailable-identity footer.");

        projection.Failure("storage.reports", report, "UnauthorizedAccessException");
        Apply(apply, projection);
        typeof(MainWindow).GetMethod("SetUploadStatus", Private)!.Invoke(window, new object[] { "BugReport.Queued", Array.Empty<object>() });
        projection.DeliveryRecovered(report);
        Apply(apply, projection);
        Check(Get<string>(window, "_uploadStatusKey") == "BugReport.Queued",
            "Retiring an older delivery failure replaced the manual report's newer queued status.");
    }

    private static void Apply(Func<RuntimePresentationSnapshot, CancellationToken, Task> apply, RuntimePresentation projection)
    { var snapshot = projection.Snapshot; Await(Task.Run(() => apply(snapshot, CancellationToken.None))); }

    private static async Task Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException(message);
            await Task.Delay(25);
        }
    }

    private static void Await(Task task)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, __) => { if (task.IsCompleted || watch.Elapsed > TimeSpan.FromSeconds(60)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(task.IsCompleted, "Fresh presentation fixture timed out."); task.GetAwaiter().GetResult();
    }

    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
