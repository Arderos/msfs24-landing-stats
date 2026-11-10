using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class FreshDeliveryAuditRegressionTests
{
    public static void ResolvedRunningFailureSurvivesRetirement() => WithOutbox((box, root, id) =>
    {
        var first = box.TryClaim(id, Worker())!;
        Check(box.Retry(first, "CryptographicException"), "Initial identity failure was not recorded.");
        var worker = Worker(); var running = box.TryClaim(id, worker)!;
        Check(box.HasIdentityFailures, "Running retry did not retain its earlier identity failure.");

        // A verified healthy key can be recovered while this sender is running.
        // Opt-out or shutdown can then retire it without producing a new result.
        box.ClearIdentityFailures();
        Check(!box.HasIdentityFailures, "Healthy-key proof did not clear the old aggregate failure.");
        box.RetireConfirmedWorker(worker, id);
        var restarted = new DurableOutbox(Path.Combine(root, "outbox"), Path.Combine(root, "archives"));
        var retained = restarted.Read(id);
        Check(retained.State == OutboxState.Pending && retained.LastFailure.Length == 0 && !restarted.HasIdentityFailures,
            "Retiring a recovered running retry resurrected its old identity failure after restart.");
        Check(retained.Attempt == running.AttemptId && EpisodeTransactionStore.Hash(retained.Path) == retained.Hash,
            "Identity recovery changed claim identity or retained archive bytes.");

        // A later failure from the same running attempt is fresh evidence.
        var next = restarted.TryClaim(id, Worker())!;
        restarted.Retry(next, "CryptographicException");
        var stillRunning = restarted.TryClaim(id, Worker())!;
        restarted.ClearIdentityFailures();
        Check(restarted.Retry(stillRunning, "CryptographicException") && restarted.HasIdentityFailures,
            "Durable cleanup suppressed a fresh failure from an already-running attempt.");
    });

    public static void RepeatedRecoveryReplacesObsoleteCleanup() => WithOutbox((box, root, id) =>
    {
        var first = box.TryClaim(id, Worker())!;
        box.Retry(first, "CryptographicException");
        var metadata = Path.Combine(root, "outbox", id.ToString("N") + ".json");
        using (var held = new FileStream(metadata, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            box.ClearIdentityFailures();
            Check(box.HasIdentityRecoveryFailures && !box.HasIdentityFailures,
                "Locked identity cleanup did not retain its deferred obligation.");
        }

        // A new failure and a second healthy-key proof can occur before the
        // earlier cleanup is due. Its old attempt must not own the new cleanup.
        var second = box.TryClaim(id, Worker())!;
        Check(second.AttemptId != first.AttemptId, "Fixture did not create a new upload attempt.");
        box.Retry(second, "CryptographicException");
        var pending = (IDictionary)typeof(DurableOutbox).GetField("_pendingRecovery", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(box)!;
        var operations = (IDictionary)pending[id]!; var cleanup = operations["identity"]!;
        var retry = (RuntimeWorkRetry)cleanup.GetType().GetProperty("Retry")!.GetValue(cleanup)!;
        Check(Stopwatch.GetTimestamp() < retry.After, "Fixture missed the deferred cleanup's unexpired backoff.");
        box.ClearIdentityFailures();
        var elapsed = Stopwatch.StartNew();
        while (box.HasRecoveryFailures)
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Resolved identity cleanup did not finish.");
            box.RetryRecoveryFailures();
            Task.Delay(20).GetAwaiter().GetResult();
        }
        var restarted = new DurableOutbox(Path.Combine(root, "outbox"), Path.Combine(root, "archives"));
        var retained = restarted.Read(id);
        Check(retained.LastFailure.Length == 0 && !restarted.HasIdentityFailures,
            "An obsolete cleanup retired the new recovery without clearing its persisted identity failure.");
        Check(retained.Attempt == second.AttemptId && EpisodeTransactionStore.Hash(retained.Path) == retained.Hash,
            "Repeated identity recovery changed claim identity or retained archive bytes.");
    });

    public static void RetiredDamagedArchiveDoesNotBlockIndependentDelivery() => Task.Run(async () =>
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temporary, "ls-fresh-retirement-" + Guid.NewGuid().ToString("N"));
        var endpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        using var cancellation = new CancellationTokenSource();
        Task<bool>? firstAttempt = null;
        try
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://fixture.invalid/");
            var archives = Path.Combine(root, "archives"); Directory.CreateDirectory(archives);
            var box = new DurableOutbox(Path.Combine(root, "outbox"), archives);
            var damaged = AddCapture(box, archives); var independent = AddCapture(box, archives);
            var owner = new UploadRuntimeOwner(box, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"),
                Path.Combine(root, "identity"), Guid.NewGuid(),
                new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency), _ => { },
                generation => generation == 1 ? "upload-hang" : "upload-ok");
            ProcessWorkerHost? firstWorker = null; var overlapped = false;
            owner.WorkerStarted += worker =>
            {
                if (firstWorker == null) firstWorker = worker;
                else overlapped |= !firstWorker.Exit.IsCompleted;
            };
            firstAttempt = owner.AttemptAsync(damaged.Id, cancellation.Token);
            await Until(() => firstWorker?.LastHealth?.OperationId == box.Read(damaged.Id).Attempt,
                "First upload did not enter its owned operation.");
            var metadata = Path.Combine(root, "outbox", damaged.Id.ToString("N") + ".json");
            var original = File.ReadAllBytes(metadata); var broken = new byte[] { (byte)'{', (byte)'!' };
            File.WriteAllBytes(metadata, broken);
            cancellation.Cancel();
            try { await firstAttempt; }
            catch (Exception error) when (error is OperationCanceledException || error is IOException || error is SerializationException || error is InvalidDataException) { }
            Check(firstWorker != null && firstWorker.Exit.IsCompleted && box.HasQuarantinedEntries,
                "Damaged claim retirement did not prove native exit and preserve its recovery obligation.");
            Check(File.ReadAllBytes(metadata).SequenceEqual(broken) && EpisodeTransactionStore.Hash(damaged.Path) == damaged.Hash,
                "Retirement changed corrupt metadata or its retained archive evidence.");

            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                await owner.AttemptAsync(independent.Id, deadline.Token);
            Check(!overlapped && box.Read(independent.Id).State == OutboxState.Delivered && box.HasQuarantinedEntries,
                "One retired damaged claim blocked independent delivery or admitted overlapping native workers.");

            File.WriteAllBytes(metadata, original);
            await Until(() => { box.RetryRecoveryFailures(); return box.Read(damaged.Id).State == OutboxState.Pending; },
                "Repair did not finish the exact retirement retained for the dead worker.");
            var successor = Worker(); var fresh = box.TryClaim(damaged.Id, successor)!;
            box.RetryRecoveryFailures();
            Check(box.Read(damaged.Id).Attempt == fresh.AttemptId && box.Read(damaged.Id).State == OutboxState.Claimed &&
                !box.HasQuarantinedEntries && EpisodeTransactionStore.Hash(damaged.Path) == damaged.Hash,
                "Old retirement recovery revoked a successor claim or lost original archive bytes.");
            box.RetireConfirmedWorker(successor, damaged.Id);
        }
        finally
        {
            cancellation.Cancel();
            if (firstAttempt != null)
                try { await firstAttempt; }
                catch (Exception error) when (error is OperationCanceledException || error is IOException || error is SerializationException || error is InvalidDataException) { }
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", endpoint);
            Check(Path.GetFullPath(root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase), "Unsafe fixture cleanup.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }).GetAwaiter().GetResult();

    private static OutboxEntry AddCapture(DurableOutbox box, string archives)
    {
        var id = Guid.NewGuid(); var path = Path.Combine(archives, id.ToString("N") + "_bug_raw.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var info = new StreamWriter(archive.CreateEntry("session.txt").Open())) info.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
            using var data = new StreamWriter(archive.CreateEntry("telemetry.csv").Open());
            data.WriteLine(TelemetryCsv.Header);
            data.WriteLine(TelemetryCsv.Format(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true }));
        }
        return box.Register(id, path, EpisodeTransactionStore.Hash(path), false, DateTime.UtcNow);
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(message);
            await Task.Delay(25);
        }
    }

    private static WorkerIdentity Worker() => new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload);

    private static void WithOutbox(Action<DurableOutbox, string, Guid> test)
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temporary, "ls-fresh-delivery-" + Guid.NewGuid().ToString("N"));
        var archives = Path.Combine(root, "archives"); Directory.CreateDirectory(archives);
        try
        {
            var box = new DurableOutbox(Path.Combine(root, "outbox"), archives); var id = Guid.NewGuid();
            var path = Path.Combine(archives, id.ToString("N") + "_bug_raw.zip");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            box.Register(id, path, EpisodeTransactionStore.Hash(path), false, DateTime.UtcNow);
            test(box, root, id);
        }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase), "Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
