using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

internal static class LowDeliveryRegressionTests
{
    public static void RunAll() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-low-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var endpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://fixture.invalid/");
            await QuarantineRepairAndRemoval(Path.Combine(root, "quarantine"));
            await IdentityAggregateAndCleanup(Path.Combine(root, "identity"));
            await HealthyRecoveryPreservesSender(Path.Combine(root, "healthy"));
            await HealthyProofSerializesFreshFailure(Path.Combine(root, "proof"));
            await RecoveryRetiresFormerClaim(Path.Combine(root, "former"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", endpoint);
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }).GetAwaiter().GetResult();

    private static readonly string Executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe");
    private static RuntimeClockEpoch Clock() => new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private static WorkerIdentity Worker() => new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload);
    private static string Entry(string root, Guid id) => Path.Combine(root, "outbox", id.ToString("N") + ".json");
    private static string Receipt(string root, Guid id) => Path.Combine(root, "outbox", "quarantine", id.ToString("N") + ".json");
    private static DurableOutbox Box(string root) => new DurableOutbox(Path.Combine(root, "outbox"), Path.Combine(root, "archives"));
    private static OutboxEntry Add(DurableOutbox box, string root)
    {
        var id = Guid.NewGuid(); var directory = Path.Combine(root, "archives"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id.ToString("N") + "_bug_raw.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var info = new StreamWriter(zip.CreateEntry("session.txt").Open())) info.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
            using var csv = new StreamWriter(zip.CreateEntry("telemetry.csv").Open());
            csv.WriteLine(TelemetryCsv.Header);
            csv.WriteLine(TelemetryCsv.Format(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 }));
        }
        return box.Register(id, path, EpisodeTransactionStore.Hash(path), false, DateTime.UtcNow);
    }
    private static void BlockIdentity(DurableOutbox box, Guid id)
    { var claim = box.TryClaim(id, Worker())!; Check(box.Retry(claim, "CryptographicException"), "Fixture did not retain its failed attempt."); }

    private static async Task QuarantineRepairAndRemoval(string root)
    {
        var box = Box(root); var damaged = Add(box, root); var removed = Add(box, root); var healthy = Add(box, root);
        var original = File.ReadAllBytes(Entry(root, damaged.Id)); var malformed = Encoding.UTF8.GetBytes("{broken metadata");
        File.WriteAllBytes(Entry(root, damaged.Id), malformed); File.WriteAllBytes(Entry(root, removed.Id), malformed);
        var accessFailures = new List<Guid>(); var recovered = new List<Guid>(); var quarantines = new List<bool>();
        box.RecoveryFailed += (id, _) => accessFailures.Add(id); box.Recovered += recovered.Add; box.QuarantineChanged += quarantines.Add;
        box.RecoverTerminalEntries();
        Check(box.HasQuarantinedEntries && !box.HasRecoveryFailures && accessFailures.Count == 0 && quarantines.SequenceEqual(new[] { true }),
            "Permanent corruption reported an endless file-access wait or lost aggregate quarantine.");
        Check(File.ReadAllBytes(Entry(root, damaged.Id)).SequenceEqual(malformed) && EpisodeTransactionStore.Hash(damaged.Path) == damaged.Hash &&
            File.Exists(Receipt(root, damaged.Id)) && File.Exists(Receipt(root, removed.Id)), "Quarantine lost source bytes or its durable receipt.");
        var claim = box.TryClaim(healthy.Id, Worker())!; Check(box.Acknowledge(claim), "Healthy neighboring report could not be delivered.");
        Check(box.HasQuarantinedEntries && !box.HasRecoveryFailures, "Neighbor delivery changed quarantine into an access error.");
        File.Delete(Entry(root, removed.Id));
        await Until(() => { box.RetryRecoveryFailures(); return !File.Exists(Receipt(root, removed.Id)); }, "Deleted metadata retained an impossible recovery obligation.");
        Check(EpisodeTransactionStore.Hash(removed.Path) == removed.Hash && box.HasQuarantinedEntries, "Removing metadata deleted archive bytes or another quarantine.");

        // Repair under a real Windows exclusive lock: the same operation must
        // change from quarantine to access-pending, and clear only after unlock.
        File.WriteAllBytes(Entry(root, damaged.Id), original);
        using (var held = new FileStream(Entry(root, damaged.Id), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Until(() => { box.RetryRecoveryFailures(); return box.HasRecoveryFailures; }, "A repaired but locked record was mistaken for accessible storage.");
            Check(accessFailures.Contains(damaged.Id), "Real file-lock failure was hidden.");
        }
        await Until(() => { box.RetryRecoveryFailures(); return !box.HasRecoveryFailures && !box.HasQuarantinedEntries; }, "Repaired record did not return to service.");
        Check(recovered.Contains(damaged.Id) && !File.Exists(Receipt(root, damaged.Id)) && EpisodeTransactionStore.Hash(damaged.Path) == damaged.Hash,
            "Confirmed repair lost bytes or retained stale quarantine metadata.");
        Check(box.TryClaim(damaged.Id, Worker()) != null, "Repaired report could not resume delivery.");

        // A restart must also retire a sidecar whose source was removed while
        // the previous runtime was down.
        var abandoned = Add(box, root); File.WriteAllBytes(Entry(root, abandoned.Id), malformed); box.RecoverTerminalEntries();
        File.Delete(Entry(root, abandoned.Id));
        var restarted = Box(root); restarted.RecoverTerminalEntries();
        Check(!File.Exists(Receipt(root, abandoned.Id)) && !restarted.HasQuarantinedEntries && !restarted.HasRecoveryFailures,
            "Restart retained quarantine for a source that no longer exists.");
    }

    private static async Task IdentityAggregateAndCleanup(string root)
    {
        var box = Box(root); var first = Add(box, root); var second = Add(box, root); var unrelated = Add(box, root);
        var changes = new List<bool>(); box.IdentityFailuresChanged += changes.Add;
        BlockIdentity(box, first.Id); BlockIdentity(box, second.Id);
        Check(box.HasIdentityFailures && changes.SequenceEqual(new[] { true }), "Identity availability followed the latest delivery slot.");
        var success = Add(box, root); Check(box.Acknowledge(box.TryClaim(success.Id, Worker())!), "Fixture delivery failed.");
        Check(box.HasIdentityFailures, "Successful unrelated delivery hid blocked reports.");
        File.Delete(Entry(root, second.Id)); box.RetryRecoveryFailures();
        Check(box.HasIdentityFailures, "Removing one blocked report cleared another identity failure.");
        File.WriteAllText(Entry(root, unrelated.Id), "damaged unrelated metadata"); box.RecoverTerminalEntries();
        var identityRoot = Path.Combine(root, "identity"); _ = new TelemetryUploadIdentityStore(identityRoot).Identity();
        var owner = new UploadRuntimeOwner(box, Executable, identityRoot, Guid.NewGuid(), Clock(), _ => { });
        using (var held = new FileStream(Entry(root, first.Id), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await owner.RecoverUnavailableIdentityAsync(true);
            Check(!result.Replaced && result.StatusRefreshFailure != null && box.HasIdentityRecoveryFailures && !box.HasIdentityFailures,
                "Verified usable key remained unavailable, or its exact locked cleanup was lost.");
        }
        // A new attempt may acquire a readable record before the cleanup retry
        // deadline. Its new failure must survive the old attempt's cleanup.
        var replacement = box.TryClaim(first.Id, Worker()) ?? throw new InvalidOperationException("New claim was not admitted after identity recovery.");
        Check(box.Read(first.Id).LastFailure.Length == 0, "New claim inherited a resolved identity failure.");
        box.Retry(replacement, "CryptographicException");
        Check(box.HasIdentityFailures, "A new identity failure was suppressed by the earlier recovery.");
        await Until(() => { box.RetryRecoveryFailures(); return !box.HasRecoveryFailures; }, "Resolved file lock retained stale cleanup work.");
        Check(box.Read(first.Id).LastFailure == "CryptographicException" && box.HasIdentityFailures,
            "Old identity cleanup erased a newer attempt's failure.");
        var clean = await owner.RecoverUnavailableIdentityAsync(true);
        Check(clean.StatusRefreshFailure == null && !box.HasIdentityFailures && box.HasQuarantinedEntries && !box.HasRecoveryFailures,
            "Unrelated malformed metadata qualified an otherwise complete identity recovery.");
        Check(EpisodeTransactionStore.Hash(first.Path) == first.Hash && EpisodeTransactionStore.Hash(second.Path) == second.Hash,
            "Identity status cleanup changed retained report bytes.");

        var last = Add(box, root); BlockIdentity(box, last.Id); File.Delete(Entry(root, last.Id)); box.RetryRecoveryFailures();
        Check(!box.HasIdentityFailures, "Deleted last blocked report kept aggregate recovery enabled.");
        var active = Add(box, root); BlockIdentity(box, active.Id);
        var activeClaim = box.TryClaim(active.Id, Worker())!;
        await owner.RecoverUnavailableIdentityAsync(true);
        Check(!box.HasIdentityFailures, "Usable-key check retained the running claim's prior failure label.");
        box.Retry(activeClaim, "CryptographicException");
        Check(box.HasIdentityFailures, "Fresh failure from an already-running attempt was suppressed by the usable-key check.");
    }

    private static async Task HealthyRecoveryPreservesSender(string root)
    {
        var box = Box(root); var item = Add(box, root); var identityRoot = Path.Combine(root, "identity");
        var identity = new TelemetryUploadIdentityStore(identityRoot); _ = identity.Identity();
        var owner = new UploadRuntimeOwner(box, Executable, identityRoot, Guid.NewGuid(), Clock(), _ => { }, _ => "upload-hang");
        ProcessWorkerHost? worker = null; owner.WorkerStarted += value => worker = value;
        using var cancellation = new CancellationTokenSource(); var attempt = owner.AttemptAsync(item.Id, cancellation.Token);
        try
        {
            await Until(() => worker?.LastHealth?.OperationId == box.Read(item.Id).Attempt, "Healthy sender did not enter its held HTTP operation.");
            await Until(() => identity.IsEnrolled(new Uri("https://fixture.invalid/")), "Healthy sender did not finish its enrollment metadata write.");
            var before = File.ReadAllBytes(Path.Combine(identityRoot, "identity.json"));
            var claim = box.Read(item.Id).Attempt;
            var recovery = owner.RecoverUnavailableIdentityAsync(true);
            Check(await Task.WhenAny(recovery, Task.Delay(TimeSpan.FromSeconds(5))) == recovery, "Healthy key recovery waited for or cancelled its active sender.");
            var result = await recovery;
            Check(!result.Replaced && !result.Created && result.StatusRefreshFailure == null, "Healthy recovery reported replacement or incomplete cleanup.");
            Check(!attempt.IsCompleted && worker != null && !worker.Exit.IsCompleted, "Healthy recovery cancelled its active sender.");
            Check(box.Read(item.Id).Attempt == claim, "Healthy recovery replaced its active claim.");
            Check(File.ReadAllBytes(Path.Combine(identityRoot, "identity.json")).SequenceEqual(before), "Healthy recovery changed the enrolled identity bytes.");
        }
        finally
        {
            cancellation.Cancel();
            try { await attempt; } catch (OperationCanceledException) { }
        }
        Check(worker != null && worker.Exit.IsCompleted && File.Exists(item.Path), "Fixture shutdown detached its active sender or removed its archive.");
    }

    private static async Task HealthyProofSerializesFreshFailure(string root)
    {
        var box = Box(root); var item = Add(box, root); BlockIdentity(box, item.Id);
        var identityRoot = Path.Combine(root, "identity"); var identity = new TelemetryUploadIdentityStore(identityRoot); _ = identity.Identity();
        var started = new TaskCompletionSource<ProcessWorkerHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource<UploadResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sendCommand = new ManualResetEventSlim(); using var finishProof = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var owner = new UploadRuntimeOwner(box, Executable, identityRoot, Guid.NewGuid(), Clock(), _ => { }, _ => "upload-ok",
            receiverObserved: value => accepted.TrySetResult(value));
        owner.WorkerStarted += worker => { started.TrySetResult(worker); sendCommand.Wait(); };
        var attempt = owner.AttemptAsync(item.Id, cancellation.Token);
        Task<TelemetryIdentityRecoveryResult?>? recovery = null;
        try
        {
            await Until(() => started.Task.IsCompleted, "Worker did not reach its command-admission boundary.");
            var worker = await started.Task; var beforeResult = worker.LastReceivedSequence;
            recovery = Task.Run(() => owner.TryRecoverUsableIdentity(() =>
            {
                var usable = identity.IsCurrentIdentityUsable(); proved.TrySetResult(usable);
                finishProof.Wait(); return usable;
            }));
            await Until(() => proved.Task.IsCompleted, "Healthy identity probe did not reach its cleanup boundary.");
            Check(await proved.Task, "Probe fixture did not establish a usable key.");
            // The already-admitted worker now produces a real new failure after
            // the proof. The source sequence advances before result acceptance.
            CorruptKey(identityRoot); sendCommand.Set();
            await Until(() => worker.LastReceivedSequence > beforeResult, "Fresh identity failure did not reach the parent's result intake.");
            Check(!accepted.Task.IsCompleted && box.Read(item.Id).State == OutboxState.Claimed,
                "Fresh result committed while the usable-key proof and cleanup still owned admission.");
            finishProof.Set();
            var result = await recovery;
            Check(result != null && !result.Replaced && result.StatusRefreshFailure == null, "Read-only proof reported a replacement or failed cleanup.");
            await attempt;
            Check(accepted.Task.IsCompleted && (await accepted.Task).Reason == "CryptographicException" &&
                box.HasIdentityFailures && box.Read(item.Id).State == OutboxState.Pending && box.Read(item.Id).LastFailure == "CryptographicException",
                "Cleanup of earlier failures suppressed a fresh result committed after the usable-key proof.");
        }
        finally
        {
            finishProof.Set(); sendCommand.Set(); cancellation.Cancel();
            if (recovery != null) await recovery;
            try { await attempt; } catch (OperationCanceledException) { }
        }
    }

    private static async Task RecoveryRetiresFormerClaim(string root)
    {
        var box = Box(root); var item = Add(box, root); var identityRoot = Path.Combine(root, "identity");
        var owner = new UploadRuntimeOwner(box, Executable, identityRoot, Guid.NewGuid(), Clock(), _ => { });
        Check((await owner.RecoverUnavailableIdentityAsync(true)).Created, "Initial identity recovery did not create its missing key.");
        var old = Worker(); box.TryClaim(item.Id, old);
        using var worker = new ProcessWorkerHost(Executable, old, Clock(), (_, __) => Task.CompletedTask);
        await worker.StartAsync(TimeSpan.FromSeconds(10), "normal");
        var claimedBytes = File.ReadAllBytes(Entry(root, item.Id));
        File.WriteAllText(Entry(root, item.Id), "damaged claimed metadata");
        box.RecoverTerminalEntries(); Check(box.TryRead(item.Id) == null, "Damaged claim was not quarantined.");
        File.WriteAllBytes(Entry(root, item.Id), claimedBytes);
        CorruptKey(identityRoot);
        try
        {
            var result = await owner.RecoverUnavailableIdentityAsync(true);
            await Until(() => worker.Exit.IsCompleted, "Identity replacement bypassed former native worker retirement.");
            Check(result.Replaced && box.Read(item.Id).State == OutboxState.Pending && EpisodeTransactionStore.Hash(item.Path) == item.Hash,
                "Standalone identity recovery bypassed durable claim release or changed archive bytes.");
        }
        finally { await worker.StopAsync(TimeSpan.FromSeconds(5)); }
    }
    private static void CorruptKey(string root)
    {
        var path = Path.Combine(root, "identity.json");
        var bad = Convert.ToBase64String(ProtectedData.Protect(new byte[] { 1, 2, 3 }, Encoding.UTF8.GetBytes("different entropy"), DataProtectionScope.CurrentUser));
        File.WriteAllText(path, Regex.Replace(File.ReadAllText(path), "\"protected_private_key\":\"[^\"]*\"", "\"protected_private_key\":\"" + bad + "\""));
    }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException(message); await Task.Delay(25); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
