using System;
using System.Collections.Concurrent;
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
using LandingStats.App.GoogleDrive;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class CloudPresentationRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void RunOwner() => Task.Run(OwnerTransitions).GetAwaiter().GetResult();

    private static async Task OwnerTransitions()
    {
        var root = Path.Combine(Path.GetTempPath(), "cp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var done = false;
        var tokens = new GoogleOAuthTokenStore(Path.Combine(root, "parent.json")); tokens.Save("fixture", "fixture@example.invalid");
        using var api = new IsolatedGoogleDriveApi(Path.Combine(root, "api"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"),
            tokens, Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency), _ => "cloud-sync");
        using var backup = Backup(api, Path.Combine(root, "h"));
        var notices = new ConcurrentQueue<RuntimeCloudNotice>();
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        var entered = Completion(); var release = Completion(); var fail = true;
        var path = Path.Combine(root, "sync.json");
        var owner = new CloudSyncRuntimeOwner(path, api, backup, notice => { notices.Enqueue(notice); projection.Cloud(notice); }, async result =>
        {
            entered.TrySetResult(true); await release.Task.ConfigureAwait(false);
            if (fail) throw new IOException("fixture background failure");
            result.LocalHistoryChanged = true;
        });
        using var stop = new CancellationTokenSource(); Task? running = null;
        try
        {
            running = Task.Run(() => owner.RunAsync(new ManagedComponentContext(1, stop.Token)));
            await Deadline(entered.Task);
            var start = projection.Snapshot.Cloud!;
            Check(start.Phase == CloudSyncPhase.Syncing && start.CompletedUtc == DateTime.MinValue && start.HistoryRevision == 0,
                "A background first sync was invisible or falsely advertised a completion/history change.");
            release.TrySetResult(true);
            try { await Deadline(running); throw new InvalidOperationException("Fixture failure did not leave its owner."); }
            catch (IOException) { }
            var failed = projection.Snapshot.Cloud!;
            Check(failed.Phase == CloudSyncPhase.Retrying && failed.Error == "fixture background failure" && failed.Revision > start.Revision && failed.CompletedUtc == DateTime.MinValue,
                "An unobserved background failure remained connected/waiting or advanced completion.");
            Check(DurableFile.ReadJson<CloudSyncIntent>(path, 4096).Completed == 0, "Presentation failure was persisted as successful batch completion.");

            fail = false; entered = Completion(); release = Completion();
            running = Task.Run(() => owner.RunAsync(new ManagedComponentContext(2, stop.Token)));
            await Deadline(entered.Task);
            Check(projection.Snapshot.Cloud!.Phase == CloudSyncPhase.Syncing && projection.Snapshot.Cloud.Error == null &&
                projection.Snapshot.Cloud.Revision > failed.Revision, "Retry startup retained a stale failure or reused its presentation revision.");
            release.TrySetResult(true);
            await Until(() => projection.Snapshot.Cloud!.Phase == CloudSyncPhase.Idle);
            var success = projection.Snapshot.Cloud!;
            var durable = DurableFile.ReadJson<CloudSyncIntent>(path, 4096);
            Check(success.CompletedUtc > DateTime.MinValue && success.Error == null && success.HistoryRevision == durable.Completed && success.Revision > durable.Completed,
                "Recovery confused presentation revisions with the durable history/batch counter.");

            entered = Completion(); release = Completion(); fail = true;
            var request = owner.Request();
            await Deadline(entered.Task);
            Check(projection.Snapshot.Cloud!.CompletedUtc == success.CompletedUtc && projection.Snapshot.Cloud.HistoryRevision == success.HistoryRevision,
                "Progress erased the previous successful sync or changed history.");
            release.TrySetResult(true);
            try { await Deadline(request); throw new InvalidOperationException("Requested fixture failure succeeded."); } catch (IOException) { }
            try { await Deadline(running); } catch (IOException) { }
            Check(projection.Snapshot.Cloud!.Phase == CloudSyncPhase.Retrying && projection.Snapshot.Cloud.CompletedUtc == success.CompletedUtc,
                "A later failure erased the last successful sync time.");

            await api.SetEnabledAsync(false);
            // A fresh runtime has a fresh presentation projection, just as a relaunch does.
            projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            var restored = new CloudSyncRuntimeOwner(path, api, backup, notice => projection.Cloud(notice));
            running = Task.Run(() => restored.RunAsync(new ManagedComponentContext(3, stop.Token)));
            await Until(() => projection.Snapshot.Cloud?.Phase == CloudSyncPhase.Paused);
            Check(projection.Snapshot.Cloud!.CompletedUtc == success.CompletedUtc, "Relaunch lost its durable last success.");
            stop.Cancel(); await Cancelled(running); running = null;

            // Missing tokens with persisted enabled policy mean expired authorization;
            // deliberate disable remains Paused even after credentials are removed.
            await api.SetEnabledAsync(true); tokens.Clear();
            using var reopened = new IsolatedGoogleDriveApi(Path.Combine(root, "api"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"),
                tokens, Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency), _ => "cloud-sync");
            Check(reopened.AuthorizationRequired && !reopened.Enabled, "Restart classified expired authorization as a deliberate pause.");
            using var authStop = new CancellationTokenSource();
            var authOwner = new CloudSyncRuntimeOwner(path, reopened, backup, notice => projection.Cloud(notice));
            projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            var authorization = Task.Run(() => authOwner.RunAsync(new ManagedComponentContext(4, authStop.Token)));
            try { await Until(() => projection.Snapshot.Cloud?.Phase == CloudSyncPhase.AuthorizationRequired); }
            finally { authStop.Cancel(); await Cancelled(authorization); }
            await reopened.SetEnabledAsync(false);
            Check(!reopened.AuthorizationRequired, "Explicit disconnect still claimed expired authorization.");
            await reopened.StopAsync();
            var ordered = notices.ToArray();
            Check(ordered.Zip(ordered.Skip(1), (a, b) => b.Revision > a.Revision).All(value => value), "Owner reused or reversed presentation revisions.");
            done = true;
        }
        finally
        {
            release.TrySetResult(true); owner.StopIntake(); stop.Cancel();
            if (running != null) { try { await Deadline(running); } catch (IOException) { } catch (OperationCanceledException) { } }
            await api.StopAsync();
            if (done) Directory.Delete(root, true);
        }
    }

    public static void RunWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "cpw-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var resources = Review089UiRegressionTests.LoadApplicationResources(); Application.Current.Resources.MergedDictionaries.Add(resources);
        var oldMode = Application.Current.ShutdownMode; Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var oldContext = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null; LandingRuntime? runtime = null; GoogleDriveBackupService? backup = null; var done = false;
        try
        {
            var tokens = new GoogleOAuthTokenStore(Path.Combine(root, "tokens.json")); tokens.Save("fixture", "fixture@example.invalid");
            var binding = new DeferredGoogleDriveApi(); var repository = new LandingRepository(Path.Combine(root, "h")); var gate = new object();
            backup = new GoogleDriveBackupService(binding, repository, gate, new GoogleDriveSyncStateRepository(Path.Combine(root, "state.json")),
                () => Task.FromResult(new GoogleDriveLocalSettings(new GoogleDriveCloudSettings { Language = "en" }, true)), (_, __) => Task.FromResult(false));
            runtime = new LandingRuntime(Path.Combine(root, "r"), AppDomain.CurrentDomain.BaseDirectory, Path.Combine(root, "i"), repository, gate,
                (_, __) => Task.CompletedTask, (_, __) => { }, cloud: new RuntimeCloudServices(tokens, backup, binding));
            window = new MainWindow(); // Offscreen, no Loaded/startup/network handlers.
            Set(window, "_repository", repository); Set(window, "_landingRepositoryGate", gate); Set(window, "_googleDriveTokenStore", tokens);
            Set(window, "_runtime", runtime); Set(window, "_startupReady", true); Set(window, "_historyLoading", false);
            var apply = (Func<RuntimePresentationSnapshot, CancellationToken, Task>)Delegate.CreateDelegate(typeof(Func<RuntimePresentationSnapshot, CancellationToken, Task>),
                window, typeof(MainWindow).GetMethod("ApplyRuntimeSnapshotAsync", Private)!);
            void Present(long revision, CloudSyncPhase phase, DateTime completed, string? error = null)
            {
                var snapshot = new RuntimePresentationSnapshot(revision, null, null, Guid.Empty, 0, false, Array.Empty<RuntimeEpisodeNotice>(),
                    Array.Empty<SupervisionSnapshot>(), Guid.Empty, null, new RuntimeCloudNotice(revision, completed, 0, null, phase, error));
                var pending = Task.Run(() => apply(snapshot, CancellationToken.None)); Pump(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            }
            TextBlock State() => Get<TextBlock>(window, "GoogleDriveStateText");
            Present(1, CloudSyncPhase.Syncing, DateTime.MinValue);
            Check(State().Text == LocalizationManager.Text("Settings.DriveSyncing") && Get<Button>(window, "GoogleDrivePrimaryButton").IsEnabled,
                "Background sync was invisible or unnecessarily blocked Sync now.");
            Present(2, CloudSyncPhase.Retrying, DateTime.MinValue, "fixture failure");
            Check(State().Text == LocalizationManager.Format("Settings.DriveRetryingFormat", "fixture failure") && Get<DateTime?>(window, "_lastGoogleDriveSyncUtc") == null,
                "Background failure still displayed waiting for first sync or fabricated a success time.");
            var success = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
            Present(3, CloudSyncPhase.Idle, success);
            Check(State().Text == LocalizationManager.Format("Settings.DriveLastSyncFormat", success.ToLocalTime()), "Successful background retry did not replace the failure.");
            tokens.Clear(); Present(4, CloudSyncPhase.AuthorizationRequired, success);
            Check(State().Text.Contains(LocalizationManager.Text("Settings.DriveSignInAgain")) &&
                (string)Get<Button>(window, "GoogleDrivePrimaryButton").Content == LocalizationManager.Text("Settings.DriveConnect"), "Expired access was shown as connected or lacked a reconnect action.");
            tokens.Save("fixture", "fixture@example.invalid"); Present(5, CloudSyncPhase.Syncing, success);
            typeof(MainWindow).GetMethod("RefreshLocalizedPresentation", Private)!.Invoke(window, null);
            Check(Get<string?>(window, "_googleDriveErrorKey") == null && State().Text == LocalizationManager.Text("Settings.DriveSyncing") &&
                Get<DateTime?>(window, "_lastGoogleDriveSyncUtc") == success, "Retry or locale refresh resurrected an auth error or erased last success.");
            Present(6, CloudSyncPhase.Retrying, success, new string('x', 2048));
            Check(Get<string>(window, "_googleDriveErrorDetail").Length == 1024 && (string)State().ToolTip == LocalizationManager.Format("Settings.DriveLastSyncFormat", success.ToLocalTime()),
                "Retry errors were unbounded or hid the last successful sync time.");
            tokens.Clear(); Present(7, CloudSyncPhase.Paused, success);
            Check(State().Text == LocalizationManager.Text("Settings.DriveDisconnected") && State().ToolTip == null, "Disconnected cloud retained a failure/connected presentation.");
            done = true;
        }
        finally
        {
            if (window != null)
            {
                Set(window, "_isClosed", true); typeof(MainWindow).GetMethod("StopHistoryOperations", Private)!.Invoke(window, null);
                var history = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!; Pump(() => history.IsCompleted);
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
                window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!); window.Close();
            }
            if (runtime != null) { var stop = runtime.StopAsync(); Pump(() => stop.IsCompleted); stop.GetAwaiter().GetResult(); runtime.Dispose(); }
            backup?.Dispose(); Application.Current.Resources.MergedDictionaries.Remove(resources); Application.Current.ShutdownMode = oldMode; SynchronizationContext.SetSynchronizationContext(oldContext);
            if (done) Directory.Delete(root, true);
        }
    }
    private static GoogleDriveBackupService Backup(IGoogleDriveApi api, string root) => new GoogleDriveBackupService(api, new LandingRepository(root), new object(),
        new GoogleDriveSyncStateRepository(Path.Combine(root, "state.json")), () => Task.FromResult(new GoogleDriveLocalSettings(new GoogleDriveCloudSettings { Language = "en" }, true)), (_, __) => Task.FromResult(false));
    private static TaskCompletionSource<bool> Completion() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static async Task Until(Func<bool> condition)
    { var watch = Stopwatch.StartNew(); while (!condition()) { Check(watch.Elapsed < TimeSpan.FromSeconds(20), "Cloud presentation condition timed out."); await Task.Delay(20); } }
    private static async Task Deadline(Task task)
    { Check(await Task.WhenAny(task, Task.Delay(20000)) == task, "Cloud presentation operation timed out."); await task; }
    private static async Task Cancelled(Task task) { try { await Deadline(task); } catch (OperationCanceledException) { } }
    private static void Pump(Func<bool> complete)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, __) => { if (complete() || watch.Elapsed > TimeSpan.FromSeconds(20)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); } Check(complete(), "Cloud window fixture timed out.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
