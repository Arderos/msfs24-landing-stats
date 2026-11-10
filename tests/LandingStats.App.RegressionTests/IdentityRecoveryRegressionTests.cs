using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.TelemetryUpload;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class IdentityRecoveryRegressionTests
{
    private static readonly string Executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe");
    public static void RunAll() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "identity-recovery-" + Guid.NewGuid().ToString("N"));
        var complete = false;
        try
        {
            ConsentMetadata(Path.Combine(root, "metadata"));
            await MutexCancellation(Path.Combine(root, "mutex"));
            await LegacyConsent(Path.Combine(root, "denied"), false);
            await LegacyConsent(Path.Combine(root, "accepted"), true);
            await ClosedRegistration(Path.Combine(root, "closed"));
            await PostCommitStorageFailure(Path.Combine(root, "postcommit"));
            await CancelledWaitRetainsOwnership(Path.Combine(root, "cancel"));
            complete = true;
        }
        finally
        {
            if (complete && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }).GetAwaiter().GetResult();

    private static void ConsentMetadata(string root)
    {
        var initial = new TelemetryUploadIdentityStore(Path.Combine(root, "initial"));
        var first = initial.RecoverUnavailableIdentity(true);
        Check(!first.Replaced && first.BackupPath == null && !initial.ConsentAccepted,
            "First-use identity creation pretended to replace a prior key or granted legacy consent.");
        var cases = new string?[] { null, "0", "1", "-1", "\"invalid\"", "true", "1.5", "{\"version\":1}", "[1]" };
        for (var index = 0; index < cases.Length; index++)
        {
            var area = Path.Combine(root, index.ToString()); var store = new TelemetryUploadIdentityStore(area);
            var old = store.Identity().InstallId; var path = Path.Combine(area, "identity.json");
            var json = File.ReadAllText(path);
            json = cases[index] == null ? Regex.Replace(json, ",\"consent_version\":0", "") :
                Regex.Replace(json, "\"consent_version\":0", "\"consent_version\":" + cases[index]);
            File.WriteAllText(path, json);
            var expected = cases[index] == "1";
            Check(new TelemetryUploadIdentityStore(area).Identity().InstallId == old && store.ConsentAccepted == expected,
                "Malformed or missing consent changed a healthy key, or was interpreted as permission.");
            var healthy = File.ReadAllBytes(path);
            Check(!store.RecoverUnavailableIdentity(true).Replaced && healthy.SequenceEqual(File.ReadAllBytes(path)),
                "Healthy-key recovery rewrote identity metadata.");
            CorruptKey(area); var unavailable = File.ReadAllBytes(path);
            try { store.RecoverUnavailableIdentity(false); throw new InvalidOperationException("Unconfirmed replacement was accepted."); }
            catch (InvalidOperationException error) when (error.Message.Contains("explicit confirmation")) { }
            Check(unavailable.SequenceEqual(File.ReadAllBytes(path)), "Declined replacement changed the identity.");
            var result = store.RecoverUnavailableIdentity(true);
            Check(result.Replaced && result.BackupPath != null && File.ReadAllBytes(result.BackupPath).SequenceEqual(unavailable) &&
                store.Identity().InstallId != old && store.ConsentAccepted == expected,
                "DPAPI recovery lost original bytes or changed the independent legacy consent.");
            var recovered = File.ReadAllBytes(path); var id = store.Identity().InstallId;
            Check(!store.RecoverUnavailableIdentity(true).Replaced && recovered.SequenceEqual(File.ReadAllBytes(path)) &&
                store.Identity().InstallId == id && Directory.GetFiles(area, "identity.json.replaced-*").Length == 1,
                "A repeated recovery request rotated a healthy replacement.");
        }
    }

    private static async Task MutexCancellation(string root)
    {
        var store = new TelemetryUploadIdentityStore(root); _ = store.Identity(); CorruptKey(root);
        var path = Path.Combine(root, "identity.json"); var before = File.ReadAllBytes(path);
        using var sha = SHA256.Create();
        var name = "Local\\LandingStats.TelemetryIdentity." + Convert.ToBase64String(
            sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace('/', '_');
        using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = Task.Run(() =>
        {
            using var mutex = new Mutex(false, name); mutex.WaitOne();
            try { entered.TrySetResult(true); release.Wait(); }
            finally { mutex.ReleaseMutex(); }
        });
        try
        {
            await entered.Task;
            var requesting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovery = Task.Run(() =>
            { requesting.TrySetResult(true); return new TelemetryUploadIdentityStore(root).RecoverUnavailableIdentity(true, cancellation.Token); });
            await requesting.Task;
            Check(!recovery.IsCompleted, "A second identity store bypassed the held cross-process mutex.");
            cancellation.Cancel();
            await Until(() => recovery.IsCompleted, "Cancelled recovery remained blocked by another identity owner.");
            try { await recovery; throw new InvalidOperationException("Cancelled mutex acquisition returned success."); }
            catch (OperationCanceledException) { }
            Check(before.SequenceEqual(File.ReadAllBytes(path)) && Directory.GetFiles(root, "identity.json.replaced-*").Length == 0,
                "Cancellation before identity-lock ownership published a replacement.");
        }
        finally { release.Set(); await holding; }
        Check(store.RecoverUnavailableIdentity(true).Replaced, "Cancelled mutex waiter released or damaged another owner's lock.");
        CorruptKey(root); before = File.ReadAllBytes(path);
        using (var abandoned = new Mutex(false, name))
        {
            var thread = new Thread(() => { abandoned.WaitOne(); });
            thread.Start(); Check(thread.Join(TimeSpan.FromSeconds(10)), "Mutex-abandonment fixture did not exit.");
            // The abandoned mutex and cancellation signal are both available.
            // Acquiring the first must still release ownership when cancelled.
            try { store.RecoverUnavailableIdentity(true, cancellation.Token); throw new InvalidOperationException("Cancelled abandoned-mutex recovery returned success."); }
            catch (OperationCanceledException) { }
            Check(before.SequenceEqual(File.ReadAllBytes(path)), "Cancelled abandoned-mutex recovery changed the key.");
            Check(store.RecoverUnavailableIdentity(true).Replaced, "Cancelled recovery leaked ownership of an abandoned mutex.");
        }
    }

    private static async Task LegacyConsent(string root, bool accepted)
    {
        var identityRoot = Path.Combine(root, "identity"); var store = new TelemetryUploadIdentityStore(identityRoot);
        if (accepted) store.AcceptConsent(); else _ = store.Identity();
        CorruptKey(identityRoot);
        var legacy = Path.Combine(root, "legacy");
        var unknown = Capture(legacy, "old_raw.zip", 1); var manual = Capture(legacy, "manual_bug_raw.zip", 2);
        var automatic = Capture(legacy, "known_auto_raw.zip", 3);
        var unknownHash = EpisodeTransactionStore.Hash(unknown); var manualHash = EpisodeTransactionStore.Hash(manual);
        var autoHash = EpisodeTransactionStore.Hash(automatic);
        var unknownId = ArchiveId(unknownHash); var manualId = ArchiveId(manualHash); var autoId = ArchiveId(autoHash);
        var archives = Path.Combine(root, "archives");
        var box = new DurableOutbox(Path.Combine(root, "outbox"), archives); box.SetAutomaticEnabled(true);
        var uploads = new UploadRuntimeOwner(box, Executable, identityRoot, Guid.NewGuid(), Clock(), _ => { }, _ => "upload-ok");
        var intake = new ArchiveIntakeOwner(Path.Combine(root, "intake"), archives, legacy, identityRoot,
            new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64)), box, uploads.Wake);
        using var uploadGeneration = new ManagedComponentGeneration(1, uploads.RunAsync);
        using var intakeGeneration = new ManagedComponentGeneration(1, intake.RunAsync);
        await uploadGeneration.StartAsync(CancellationToken.None); await intakeGeneration.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => box.Enumerate().Contains(manualId) && box.Read(manualId).LastFailure == "CryptographicException" &&
                box.Enumerate().Contains(autoId), "Unavailable key did not retain explicit manual and automatic reports.");
            Check(box.Enumerate().Contains(unknownId) == accepted, "Legacy intake ignored the stored consent before recovery.");
            var result = await uploads.RecoverUnavailableIdentityAsync(true); intake.Wake();
            Check(result.Replaced && new TelemetryUploadIdentityStore(identityRoot).ConsentAccepted == accepted,
                "Confirmed key replacement changed legacy permission.");
            await Until(() => box.Read(manualId).State == OutboxState.Delivered && box.Read(autoId).State == OutboxState.Delivered,
                "Explicit report delivery did not resume after key recovery.");
            if (accepted)
                await Until(() => box.Read(unknownId).State == OutboxState.Delivered, "Previously consented legacy report did not resume.");
            else
                Check(!box.Enumerate().Contains(unknownId) && File.Exists(unknown) && EpisodeTransactionStore.Hash(unknown) == unknownHash,
                    "Recovery adopted an unconsented legacy report while automatic delivery was enabled.");
            Check(box.Read(manualId).Hash == manualHash && box.Read(autoId).Hash == autoHash, "Recovery changed diagnostic report content.");
        }
        finally
        {
            await intakeGeneration.StopAsync(TimeSpan.FromSeconds(10));
            await uploadGeneration.StopAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task ClosedRegistration(string root)
    {
        var priorEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        var endpoint = new Uri("https://receiver.invalid/");
        try
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", endpoint.AbsoluteUri);
            var identityRoot = Path.Combine(root, "identity"); var store = new TelemetryUploadIdentityStore(identityRoot);
            var original = store.Identity().InstallId; store.MarkEnrolled(endpoint, true);
            var path = Path.Combine(identityRoot, "identity.json"); var originalBytes = File.ReadAllBytes(path);
            var box = new DurableOutbox(Path.Combine(root, "outbox"), Path.Combine(root, "archives"));
            var owner = new UploadRuntimeOwner(box, Executable, identityRoot, Guid.NewGuid(), Clock(), _ => { });
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await owner.RecoverUnavailableIdentityAsync(true);
                Check(!result.Replaced && File.ReadAllBytes(path).SequenceEqual(originalBytes) && store.IsEnrolled(endpoint),
                    "Healthy recovery discarded an enrolled identity while registration was closed.");
            }
            using (var client = new TelemetryUploadClient(root, new ClosedReceiver(original), identityRoot, startQueueWorker: false))
                Check((await client.PrepareAsync(CancellationToken.None)).State == TelemetryPreparationState.Ready,
                    "Healthy existing identity could not refresh enrollment with closed registration.");
            CorruptKey(identityRoot);
            var replaced = await owner.RecoverUnavailableIdentityAsync(true);
            Check(replaced.Replaced && !new TelemetryUploadIdentityStore(identityRoot).IsEnrolled(endpoint), "Replacement inherited another key's registration.");
            using (var client = new TelemetryUploadClient(root, new ClosedReceiver(original), identityRoot, startQueueWorker: false))
            {
                var denied = await client.PrepareAsync(CancellationToken.None);
                Check(denied.State == TelemetryPreparationState.Unavailable && denied.MessageKey == "Telemetry.RegistrationClosed",
                    "New identity incorrectly bypassed closed registration.");
            }
            var replacement = new TelemetryUploadIdentityStore(identityRoot).Identity().InstallId;
            Check(!(await owner.RecoverUnavailableIdentityAsync(true)).Replaced && new TelemetryUploadIdentityStore(identityRoot).Identity().InstallId == replacement,
                "Closed registration encouraged another key rotation.");
            foreach (var enrolled in new[] { true, false })
            {
                try { store.MarkEnrolled(endpoint, enrolled); throw new InvalidOperationException("Stale sender changed replacement enrollment."); }
                catch (CryptographicException) { }
            }
            try { store.AcceptConsent(); throw new InvalidOperationException("Stale sender granted replacement consent."); }
            catch (CryptographicException) { }
        }
        finally { Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", priorEndpoint); }
    }

    private static async Task PostCommitStorageFailure(string root)
    {
        var identityRoot = Path.Combine(root, "identity"); var store = new TelemetryUploadIdentityStore(identityRoot);
        _ = store.Identity(); CorruptKey(identityRoot);
        var item = Pending(root, true); var boxPath = Path.Combine(root, "outbox", item.Id.ToString("N") + ".json");
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload);
        var claim = item.Box.TryClaim(item.Id, worker)!; item.Box.Retry(claim, "CryptographicException"); item.Box.SetAutomaticEnabled(false);
        var recovered = false;
        using var postCommitCancellation = new CancellationTokenSource();
        var owner = new UploadRuntimeOwner(item.Box, Executable, identityRoot, Guid.NewGuid(), Clock(), _ => { });
        string replacement;
        using (var locked = new FileStream(boxPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await owner.RecoverUnavailableIdentityAsync(true, () => { recovered = true; postCommitCancellation.Cancel(); }, postCommitCancellation.Token);
            replacement = new TelemetryUploadIdentityStore(identityRoot).Identity().InstallId;
            Check(result.Replaced && result.StatusRefreshFailure != null && recovered && item.Box.HasRecoveryFailures,
                "Postcommit storage failure was reported as failed replacement or lost its pending status.");
            var again = await owner.RecoverUnavailableIdentityAsync(true);
            Check(!again.Replaced && again.StatusRefreshFailure != null && new TelemetryUploadIdentityStore(identityRoot).Identity().InstallId == replacement &&
                Directory.GetFiles(identityRoot, "identity.json.replaced-*").Length == 1,
                "Retry after postcommit cleanup failure replaced the already recovered key.");
        }
        using (var generation = new ManagedComponentGeneration(1, owner.RunAsync))
        {
            await generation.StartAsync(CancellationToken.None);
            try { await Until(() => !item.Box.HasRecoveryFailures && item.Box.Read(item.Id).LastFailure.Length == 0, "Owning sweep did not retry identity-status storage cleanup."); }
            finally { await generation.StopAsync(TimeSpan.FromSeconds(10)); }
        }
        var warning = await owner.RecoverUnavailableIdentityAsync(true, () => throw new IOException("Presentation fixture failure."));
        Check(!warning.Replaced && warning.StatusRefreshFailure == nameof(IOException) && new TelemetryUploadIdentityStore(identityRoot).Identity().InstallId == replacement,
            "Presentation failure changed the committed identity outcome.");
        Check(File.Exists(item.Path) && EpisodeTransactionStore.Hash(item.Path) == item.Hash, "Status cleanup changed retained automatic report bytes.");
    }

    private static async Task CancelledWaitRetainsOwnership(string root)
    {
        var identityRoot = Path.Combine(root, "identity"); _ = new TelemetryUploadIdentityStore(identityRoot).Identity();
        var item = Pending(root, false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        var owner = new UploadRuntimeOwner(item.Box, Executable, identityRoot, Guid.NewGuid(), Clock(), _ =>
        { entered.TrySetResult(true); release.Wait(); }, _ => "upload-defer");
        ProcessWorkerHost? process = null; owner.WorkerStarted += value => process = value;
        var work = owner.AttemptAsync(item.Id, CancellationToken.None);
        try
        {
            await Until(() => entered.Task.IsCompleted, "Held accepted upload callback did not start.");
            CorruptKey(identityRoot); var original = File.ReadAllBytes(Path.Combine(identityRoot, "identity.json"));
            var recovery = owner.RecoverUnavailableIdentityAsync(true, cancellationToken: cancellation.Token);
            Check(!recovery.IsCompleted, "Recovery passed the accepted-callback retirement boundary.");
            cancellation.Cancel();
            try { await recovery; throw new InvalidOperationException("Cancelled recovery returned success."); }
            catch (OperationCanceledException) { }
            Check(!work.IsCompleted && original.SequenceEqual(File.ReadAllBytes(Path.Combine(identityRoot, "identity.json"))),
                "Cancelled recovery changed the key or released accepted upload ownership.");
            try { await owner.AttemptAsync(item.Id, CancellationToken.None); throw new InvalidOperationException("Replacement upload overlapped retained owner."); }
            catch (InvalidOperationException error) when (error.Message.Contains("already owns")) { }
        }
        finally { release.Set(); await work; }
        Check(process != null && process.Exit.IsCompleted, "Cancelled recovery detached a live upload worker.");
        Check((await owner.RecoverUnavailableIdentityAsync(true)).Replaced, "Recovery could not resume after the previous owner retired.");
    }

    private sealed class ClosedReceiver : HttpMessageHandler
    {
        private readonly string _known;
        public ClosedReceiver(string known) { _known = known; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/config")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"protocol\":1,\"registration_mode\":\"closed\",\"telemetry_schema\":" + TelemetryCsv.SchemaVersion + "}") };
            if (request.RequestUri.AbsolutePath != "/v1/enroll") throw new InvalidOperationException("Unexpected fixture request.");
            var body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(body.Contains("\"install_id\":\"" + _known + "\"") ? HttpStatusCode.OK : HttpStatusCode.Forbidden)
                { Content = new StringContent("{}") };
        }
    }
    private static void CorruptKey(string root)
    {
        var path = Path.Combine(root, "identity.json");
        var bad = Convert.ToBase64String(ProtectedData.Protect(new byte[] { 1, 2, 3 }, Encoding.UTF8.GetBytes("unavailable fixture entropy"), DataProtectionScope.CurrentUser));
        File.WriteAllText(path, Regex.Replace(File.ReadAllText(path), "\"protected_private_key\":\"[^\"]*\"", "\"protected_private_key\":\"" + bad + "\""));
        try { new TelemetryUploadIdentityStore(root).Identity(); throw new InvalidOperationException("Fixture did not cause a real DPAPI failure."); }
        catch (CryptographicException) { }
    }
    private static (DurableOutbox Box, Guid Id, string Path, string Hash) Pending(string root, bool automatic)
    {
        var archives = Path.Combine(root, "archives"); var path = Capture(archives, "pending_raw.zip", 1); var hash = EpisodeTransactionStore.Hash(path);
        var box = new DurableOutbox(Path.Combine(root, "outbox"), archives); box.SetAutomaticEnabled(automatic);
        var id = Guid.NewGuid(); box.Register(id, path, hash, automatic, DateTime.UtcNow); return (box, id, path, hash);
    }
    private static string Capture(string folder, string name, long sequence)
    {
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var info = new StreamWriter(zip.CreateEntry("session.txt").Open())) info.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
        using var csv = new StreamWriter(zip.CreateEntry("telemetry.csv").Open()); csv.WriteLine(TelemetryCsv.Header);
        csv.WriteLine(TelemetryCsv.Format(new TelemetrySample { Sequence = sequence, SimulationTimeSeconds = 1 })); return path;
    }
    private static Guid ArchiveId(string hash) => Guid.ParseExact(hash.Substring(0, 32), "N");
    private static RuntimeClockEpoch Clock() => new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException(message); await Task.Delay(25); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
