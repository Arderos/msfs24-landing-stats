using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class CloudWorkerRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cw-" + Guid.NewGuid().ToString("N").Substring(0, 8)); var done = false;
        try
        {
            await AllOperations(Path.Combine(root, "rpc"));
            await ConfigurationFailure(Path.Combine(root, "config"));
            await HungOperationRecovers(Path.Combine(root, "hang"));
            await LogoutAndCredentials(Path.Combine(root, "epochs"));
            await Authorization(Path.Combine(root, "auth"));
            using var limited = new BoundedCloudDownload(); limited.Position = CloudApiRequest.MaximumBodyBytes;
            try { limited.WriteByte(1); throw new InvalidOperationException("Oversized cloud download was allocated."); } catch (InvalidDataException) { }
            Check(limited.Length == 0, "Rejected download modified the accepted prefix.");
            Check(!Directory.EnumerateFiles(root, "token.json", SearchOption.AllDirectories).Any(), "Retired workers retained credential snapshots.");
            done = true;
        }
        finally { if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
    private static GoogleOAuthTokenStore Tokens(string root)
    { var store = new GoogleOAuthTokenStore(Path.Combine(root, "parent.json")); store.Save("fixture-original", "original@example.invalid"); return store; }
    private static IsolatedGoogleDriveApi Api(string root, GoogleOAuthTokenStore tokens, Func<long, string?> fixture) => new IsolatedGoogleDriveApi(
        Path.Combine(root, "cloud"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), tokens, Guid.NewGuid(),
        new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency), fixture);
    private static async Task AllOperations(string root)
    {
        var tokens = Tokens(root); using var api = Api(root, tokens, _ => "cloud-refresh");
        var started = 0; api.WorkerStarted += _ => Interlocked.Increment(ref started);
        try
        {
            Check(await api.GetAccountPermissionIdAsync(CancellationToken.None) == "fixture-account", "Cloud account RPC changed its result.");
            Check(tokens.Load()!.RefreshToken == "fixture-refreshed-token", "Current worker credential refresh was not committed by its parent.");
            var files = await api.ListApplicationFilesAsync(CancellationToken.None);
            Check(files.Count == 1 && files[0].Property("kind") == "landing" && files[0].Parents.Single() == "fixture-root", "Cloud listing lost its metadata.");
            var properties = new Dictionary<string, string> { ["kind"] = "landing", ["sha256"] = new string('a', 64) };
            var folder = await api.CreateFolderAsync("fixture", null, properties, CancellationToken.None);
            Check(folder.Parents.Count == 0 && folder.Name == "fixture", "Cloud folder operation changed its parent or name.");
            var uploaded = await api.UploadFileAsync("test.gz", "application/gzip", folder.Id, properties, Encoding.UTF8.GetBytes("immutable"), CancellationToken.None);
            Check(uploaded.Id == "fixture-upload-9" && uploaded.Parents.Single() == folder.Id, "Cloud upload lost its immutable body or metadata.");
            var updated = await api.UpdateFileAsync(uploaded.Id, "new.gz", "application/gzip", properties, new byte[] { 1, 2 }, CancellationToken.None);
            Check(updated.Id == uploaded.Id && updated.Name == "new.gz", "Cloud update replaced its file identity.");
            var tagged = await api.UpdateAppPropertiesAsync(updated.Id, properties, CancellationToken.None);
            Check(tagged.Property("sha256") == properties["sha256"], "Cloud property update changed its request.");
            Check(Encoding.UTF8.GetString(await api.DownloadFileAsync(updated.Id, CancellationToken.None)) == "fixture-content:" + updated.Id, "Cloud download changed its bytes.");
            await api.TrashFileAsync(updated.Id, CancellationToken.None);
            Check(started == 1, "One credential epoch started a new process for each API operation.");
        }
        finally { await api.StopAsync(); }
        using var resumed = Api(root, tokens, _ => "cloud-ok");
        Check(resumed.Enabled, "Normal application shutdown disabled cloud sync for the next launch."); await resumed.StopAsync();
    }
    private static async Task ConfigurationFailure(string root)
    {
        var tokens = Tokens(root); using var api = Api(root, tokens, _ => "cloud-unconfigured");
        try
        {
            try
            {
                await api.GetAccountPermissionIdAsync(CancellationToken.None);
                throw new InvalidOperationException("An unconfigured cloud worker reported success.");
            }
            catch (CloudApiException error)
            {
                Check(error.Message.Contains(new GoogleDriveConfigurationException().Message) && error.Status == 0,
                    "Missing build configuration was hidden behind an exception type or a network status.");
                Check(tokens.HasRefreshToken(), "Missing build configuration erased the user's existing sign-in.");
            }
        }
        finally { await api.StopAsync(); }
    }
    private static async Task HungOperationRecovers(string root)
    {
        var tokens = Tokens(root); using var api = Api(root, tokens, generation => generation == 1 ? "cloud-hang" : "cloud-ok");
        var workers = new List<ProcessWorkerHost>(); var overlap = false; var escalations = 0;
        api.WorkerStarted += host => { lock (workers) { if (workers.Count != 0 && !workers.Last().Exit.IsCompleted) overlap = true; workers.Add(host); } };
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var supervisor = new ComponentSupervisor("cloud.fixture", generation => new ManagedComponentGeneration(generation, async owner =>
        {
            owner.MarkReady(); using var scope = api.BeginSync();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(scope.Cancellation, owner.CancellationToken);
            owner.Progress(false, DependencyWait.Network, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.NewGuid());
            completed.TrySetResult(await api.GetAccountPermissionIdAsync(cancellation.Token));
            while (true) { owner.Progress(false, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromMilliseconds(100)); }
        }), _ => Task.CompletedTask, (_, __) => Interlocked.Increment(ref escalations), new ComponentRecoveryOptions
        { OperationDeadline = TimeSpan.FromSeconds(3), CheckInterval = TimeSpan.FromMilliseconds(20), InitialBackoff = TimeSpan.FromMilliseconds(10), MaximumBackoff = TimeSpan.FromMilliseconds(50) });
        supervisor.Start();
        try
        {
            await Until(() => completed.Task.IsCompleted, "Noncooperative cloud API did not restart.");
            Check(await completed.Task == "fixture-account" && workers.Count >= 2 && !overlap && escalations == 0, "Cloud recovery overlapped workers or escalated the application.");
        }
        finally { await supervisor.StopAsync(); await api.StopAsync(); }
    }
    private static async Task LogoutAndCredentials(string root)
    {
        var tokens = Tokens(root); using (var api = Api(root, tokens, generation => generation == 1 ? "cloud-late" : "cloud-ok"))
        {
            ProcessWorkerHost? worker = null; api.WorkerStarted += host => Volatile.Write(ref worker, host);
            var scope = api.BeginSync(); var attempt = api.GetAccountPermissionIdAsync(scope.Cancellation);
            await Until(() => Volatile.Read(ref worker)?.LastHealth?.OperationId != null && Volatile.Read(ref worker)!.LastHealth!.OperationId != Guid.Empty,
                "Cloud fixture did not start its operation.");
            var disabled = api.SetEnabledAsync(false);
            try
            {
                await Task.Delay(200); Check(!disabled.IsCompleted, "Logout barrier returned while a sync scope still owned local effects.");
                try { await attempt; throw new InvalidOperationException("Revoked cloud result was accepted."); } catch (OperationCanceledException) { }
                Check(tokens.Load()!.RefreshToken == "fixture-original" && worker!.Exit.IsCompleted, "Late credential refresh survived logout or worker exit was not confirmed.");
            }
            finally { scope.Dispose(); await disabled; }
            try { await api.GetAccountPermissionIdAsync(CancellationToken.None); throw new InvalidOperationException("Disabled cloud API accepted work."); } catch (GoogleDriveNotSignedInException) { }
            await api.SetEnabledAsync(true); Check(await api.GetAccountPermissionIdAsync(CancellationToken.None) == "fixture-account", "Cloud re-enable did not create a fresh credential epoch.");
            await api.SetEnabledAsync(false);
            await api.StopAsync();
        }
        using (var restarted = Api(root, tokens, _ => "cloud-ok"))
        {
            Check(!restarted.Enabled, "Cloud disable did not survive process restart."); await restarted.StopAsync();
        }
        var changedRoot = Path.Combine(root, "changed"); tokens = Tokens(changedRoot);
        using (var api = Api(changedRoot, tokens, _ => "cloud-late"))
        {
            ProcessWorkerHost? worker = null; api.WorkerStarted += host => Volatile.Write(ref worker, host);
            var attempt = api.GetAccountPermissionIdAsync(CancellationToken.None);
            await Until(() => Volatile.Read(ref worker)?.LastHealth?.OperationId != null && Volatile.Read(ref worker)!.LastHealth!.OperationId != Guid.Empty,
                "Changed-account fixture did not start.");
            tokens.Save("new-account", "new@example.invalid");
            try { await attempt; throw new InvalidOperationException("Old account result was accepted."); } catch (GoogleDriveNotSignedInException) { }
            Check(tokens.Load()!.RefreshToken == "new-account", "Old worker overwrote credentials saved by a newer account."); await api.StopAsync();
        }
        var invalidRoot = Path.Combine(root, "invalid"); tokens = Tokens(invalidRoot);
        using (var api = Api(invalidRoot, tokens, _ => "cloud-invalid"))
        {
            try { await api.GetAccountPermissionIdAsync(CancellationToken.None); throw new InvalidOperationException("Invalid credential remained usable."); } catch (GoogleDriveNotSignedInException) { }
            Check(!tokens.HasRefreshToken(), "Current revoked credential was not cleared by its parent."); await api.StopAsync();
        }
    }
    private static async Task Authorization(string root)
    {
        var tokens = new GoogleOAuthTokenStore(Path.Combine(root, "parent.json"));
        using (var api = Api(root, tokens, generation => generation == 1 ? "cloud-auth-hang" : "cloud-auth"))
        {
            ProcessWorkerHost? worker = null; api.WorkerStarted += host => Volatile.Write(ref worker, host);
            using var cancellation = new CancellationTokenSource();
            var pending = api.SignInAsync(cancellation.Token);
            await Until(() => Volatile.Read(ref worker)?.LastHealth?.OperationId != null && Volatile.Read(ref worker)!.LastHealth!.OperationId != Guid.Empty,
                "Authorization fixture did not start.");
            cancellation.Cancel();
            try { await pending; throw new InvalidOperationException("Cancelled authorization completed."); } catch (OperationCanceledException) { }
            Check(worker!.Exit.IsCompleted && !tokens.HasRefreshToken(), "Cancelled authorization retained a worker or installed credentials.");
            Check(await api.SignInAsync(CancellationToken.None) == "fixture@example.invalid" && tokens.Load()!.RefreshToken == "fixture-authorized",
                "Authorization did not recover in a fresh worker with a parent-owned credential commit.");
            Check(!api.Enabled, "Authorization enabled background sync before its explicit policy transition.");
            await api.SetEnabledAsync(true); await api.StopAsync();
        }
        var lateRoot = Path.Combine(root, "late"); var lateTokens = new GoogleOAuthTokenStore(Path.Combine(lateRoot, "parent.json"));
        using (var api = Api(lateRoot, lateTokens, _ => "cloud-auth-late"))
        {
            ProcessWorkerHost? worker = null; api.WorkerStarted += host => Volatile.Write(ref worker, host);
            var pending = api.SignInAsync(CancellationToken.None);
            await Until(() => Volatile.Read(ref worker)?.LastHealth?.OperationId != null && Volatile.Read(ref worker)!.LastHealth!.OperationId != Guid.Empty,
                "Late authorization fixture did not start.");
            await api.SetEnabledAsync(false);
            try { await pending; throw new InvalidOperationException("Revoked authorization installed a token."); } catch (OperationCanceledException) { }
            Check(!lateTokens.HasRefreshToken() && worker!.Exit.IsCompleted, "Authorization escaped the logout barrier.");
            await api.StopAsync();
        }
        var stopRoot = Path.Combine(root, "stop"); var stopTokens = new GoogleOAuthTokenStore(Path.Combine(stopRoot, "parent.json"));
        using (var api = Api(stopRoot, stopTokens, _ => "cloud-auth-hang"))
        {
            ProcessWorkerHost? worker = null; api.WorkerStarted += host => Volatile.Write(ref worker, host);
            var pending = api.SignInAsync(CancellationToken.None);
            await Until(() => Volatile.Read(ref worker)?.LastHealth?.OperationId != null && Volatile.Read(ref worker)!.LastHealth!.OperationId != Guid.Empty,
                "Shutdown authorization fixture did not start.");
            var stopped = api.StopAsync();
            await Until(() => stopped.IsCompleted, "Shutdown did not terminate an unresponsive authorization worker."); await stopped;
            try { await pending; throw new InvalidOperationException("Shutdown authorization completed."); } catch (OperationCanceledException) { }
            Check(worker!.Exit.IsCompleted && !stopTokens.HasRefreshToken(), "Shutdown accepted a late authorization result.");
        }
    }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException(message); await Task.Delay(25); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
