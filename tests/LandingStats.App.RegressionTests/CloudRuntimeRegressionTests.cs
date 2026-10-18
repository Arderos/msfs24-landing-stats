using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class CloudRuntimeRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cr-" + Guid.NewGuid().ToString("N").Substring(0, 8)); var done = false;
        var tokens = new GoogleOAuthTokenStore(Path.Combine(root, "token-parent.json")); tokens.Save("fixture", "fixture@example.invalid");
        var runtimeRoot = Path.Combine(root, "r"); var gate = new object(); var repository = new LandingRepository(Path.Combine(root, "h1"));
        var landing = new LandingRecord { Id = Guid.NewGuid().ToString("N"), AircraftTitle = "Cloud recovery fixture", TimestampUtc = DateTime.UtcNow };
        repository.Save(landing); var escalations = new ConcurrentQueue<string>();
        var options = new ComponentRecoveryOptions { OperationDeadline = TimeSpan.FromSeconds(3), CheckInterval = TimeSpan.FromMilliseconds(25),
            InitialBackoff = TimeSpan.FromMilliseconds(10), MaximumBackoff = TimeSpan.FromMilliseconds(50) };
        TaskCompletionSource<bool>? blockedSettings = null;
        var enteredSettings = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<GoogleDriveLocalSettings> ReadSettings()
        {
            var block = blockedSettings;
            if (block != null)
            {
                using var dependency = CloudOperationCancellation.WaitForPresentation();
                enteredSettings.TrySetResult(true);
                await block.Task.ConfigureAwait(false); // An accepted UI action does not abandon ownership on cancellation.
            }
            return new GoogleDriveLocalSettings(new GoogleDriveCloudSettings { Language = "en" }, true);
        }
        LandingRuntime Runtime(LandingRepository local, bool loseReply, out GoogleDriveBackupService backup, out DeferredGoogleDriveApi binding)
        {
            binding = new DeferredGoogleDriveApi();
            backup = new GoogleDriveBackupService(binding, local, gate, new GoogleDriveSyncStateRepository(Path.Combine(local.RootPath, "cloud-state.json")),
                ReadSettings, (_, __) => Task.FromResult(false));
            return new LandingRuntime(runtimeRoot, AppDomain.CurrentDomain.BaseDirectory, Path.Combine(root, "identity"), local, gate,
                (_, __) => Task.CompletedTask, (name, _) => escalations.Enqueue(name), (role, _) => role == ComponentRole.CloudSync ? loseReply ? "cloud-sync-lost" : "cloud-sync" :
                    role == ComponentRole.Analysis ? null : role == ComponentRole.Upload ? "upload-ok" : "normal", options,
                cloud: new RuntimeCloudServices(tokens, backup, binding));
        }
        try
        {
            using (var first = Runtime(repository, true, out var backup, out var api))
            using (backup)
            {
                try
                {
                    await first.StartAsync(false);
                    try { await Deadline(first.RequestCloudSyncAsync()); } catch (OperationCanceledException) { } catch (IOException) { }
                    await Until(() => first.Snapshot.Cloud?.CompletedUtc > DateTime.MinValue && first.Snapshot.Cloud.Phase == CloudSyncPhase.Idle &&
                        first.Health.Single(state => state.Component == "cloud.sync").Generation >= 2,
                        "Cloud supervisor did not recover its committed remote upload after a lost reply.");
                    var files = await api.ListApplicationFilesAsync(CancellationToken.None);
                    Check(files.Count(file => !file.Trashed && file.Property("kind") == "landing" && file.Property("landingId") == landing.Id) == 1,
                        "Lost upload reply created a duplicate logical landing backup.");
                    Check(first.Health.Where(state => state.Component.StartsWith("source.", StringComparison.Ordinal)).All(state => state.Generation == 1),
                        "Cloud network recovery restarted an independent acquisition component.");
                    await Deadline(first.RequestCloudSyncAsync());
                    var generation = first.Health.Single(state => state.Component == "cloud.sync").Generation;
                    blockedSettings = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var syncing = first.RequestCloudSyncAsync();
                    try
                    {
                        await Deadline(enteredSettings.Task);
                        await Until(() => escalations.Contains("cloud.sync"), "An accepted blocked settings action did not escalate its unconfirmed owner.");
                        Check(first.IsPresentationDependency("cloud.sync", true) && !first.IsPresentationDependency("cloud.sync", false),
                            "UI dependency concealed a runtime fault with a live dispatcher or interrupted a healthy flight with a stalled dispatcher.");
                        Check(first.NeedsRecovery("cloud.sync", generation), "A live old cloud owner was treated as retired.");
                        Check(first.Health.Where(state => state.Component.StartsWith("source.", StringComparison.Ordinal)).All(state => state.Generation == 1),
                            "Blocked presentation restarted independent source owners.");
                    }
                    finally { var block = blockedSettings; blockedSettings = null; block!.TrySetResult(true); }
                    try { await Deadline(syncing); } catch (OperationCanceledException) { }
                    await Until(() => first.Health.Single(state => state.Component == "cloud.sync").Generation > generation,
                        "Released UI dependency did not allow the old cloud owner to retire.");
                    Check(!first.NeedsRecovery("cloud.sync", generation), "A safely replaced owner kept an obsolete application-restart request.");
                    Check(escalations.TryDequeue(out var escalated) && escalated == "cloud.sync", "Unexpected escalation source.");
                }
                finally { await Deadline(first.StopAsync()); }
            }
            var restored = new LandingRepository(Path.Combine(root, "h2"));
            using (var second = Runtime(restored, false, out var backup, out var api))
            using (backup)
            {
                try
                {
                    await second.StartAsync(false); Check(second.CloudEnabled, "Ordinary runtime shutdown forgot cloud authorization.");
                    await Deadline(second.RequestCloudSyncAsync());
                    lock (gate) Check(restored.LoadAll().Single().Id == landing.Id, "Parent did not restore the exact committed cloud landing.");
                    Check(second.Snapshot.Cloud?.HistoryRevision > 0, "Cloud completion did not advertise its committed history change.");
                    backup.DeleteLandingLocally(landing.Id);
                    await Deadline(second.RequestCloudSyncAsync());
                    var files = await api.ListApplicationFilesAsync(CancellationToken.None);
                    Check(files.Where(file => file.Property("kind") == "landing" && file.Property("landingId") == landing.Id).All(file => file.Trashed), "Pending local delete was lost across the process transport.");
                    Check(files.Any(file => !file.Trashed && file.Property("kind") == "landing-tombstone" && file.Property("landingId") == landing.Id), "Durable deletion marker was not retained outside Drive trash.");
                    lock (gate) Check(restored.LoadAll().Count == 0, "Cloud sync resurrected a locally deleted landing.");
                    await second.SetCloudEnabledAsync(false); Check(!second.CloudEnabled, "Runtime cloud disable did not complete.");
                }
                finally { await Deadline(second.StopAsync()); }
            }
            Check(escalations.IsEmpty, "A cloud API hang escalated the recording application: " + string.Join(",", escalations)); done = true;
        }
        finally { if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException(message); await Task.Delay(30); } }
    private static async Task Deadline(Task task)
    { if (await Task.WhenAny(task, Task.Delay(45000)) != task) throw new TimeoutException("Cloud runtime operation timed out."); await task; }
    private static async Task<T> Deadline<T>(Task<T> task)
    { await Deadline((Task)task); return await task; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
