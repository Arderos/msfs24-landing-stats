using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.TelemetryUpload;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class UploadWorkerRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "uw-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(root);
        var done = false;
        try
        {
            var identity = Path.Combine(root, "identity"); _ = new TelemetryUploadIdentityStore(identity).Identity();
            await HungHttpRecovers(Path.Combine(root, "hang"), identity);
            await PolicyAndManual(Path.Combine(root, "policy"), identity);
            await Outcomes(Path.Combine(root, "results"), identity);
            await AcceptedCallbackRetainsOwnership(Path.Combine(root, "callback"), identity);
            done = true;
        }
        finally { if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
    private static readonly string Executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe");
    public static void RapidOptOutAndInPreservesRequestedRetry() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "upload-policy-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var item = Capture(root, true); var workers = new List<ProcessWorkerHost>(); var claims = new List<Guid>(); var overlap = false;
            var owner = new UploadRuntimeOwner(item.Box, Executable, Path.Combine(root, "identity"), Guid.NewGuid(), Clock(), _ => { },
                generation => generation == 1 ? "upload-late" : "upload-ok");
            ProcessWorkerHost? first = null;
            owner.WorkerStarted += worker =>
            {
                lock (workers)
                {
                    overlap |= workers.Count != 0 && !workers.Last().Exit.IsCompleted;
                    workers.Add(worker); claims.Add(item.Box.Read(item.Id).Attempt);
                    if (workers.Count == 1) Volatile.Write(ref first, worker);
                }
            };
            using var generation = new ManagedComponentGeneration(1, owner.RunAsync);
            await generation.StartAsync(CancellationToken.None);
            try
            {
                await Until(() => Volatile.Read(ref first)?.LastHealth?.OperationId != null && first!.LastHealth!.OperationId != Guid.Empty,
                    "Upload did not enter its held HTTP operation.");
                item.Box.SetAutomaticEnabled(false); var stopped = owner.SetAutomaticAllowed(false);
                // Re-enable while the original helper still owns its cancelled attempt.
                Check(!first!.Exit.IsCompleted, "Fixture missed cancellation while the old helper was still alive.");
                Check(!stopped.IsCompleted, "Opt-out acknowledged before the held worker retired.");
                item.Box.SetAutomaticEnabled(true); await owner.SetAutomaticAllowed(true);
                await stopped;
                await Until(() => item.Box.Read(item.Id).State == OutboxState.Delivered,
                    "Rapid re-enable was lost while the previous upload was unwinding.");
            }
            finally { await generation.StopAsync(TimeSpan.FromSeconds(10)); }
            // A delivered receipt is written by an accepted result callback.
            // Shutdown, rather than that receipt, is the helper-retirement barrier.
            lock (workers)
            {
                Check(!overlap && workers.Count >= 2, "Policy retry overlapped helpers or did not replace its retired attempt.");
                Check(claims.Distinct().Count() == claims.Count, "Policy retry reused a retired claim.");
                Check(workers.All(worker => worker.Exit.IsCompleted), "Upload shutdown left a helper running.");
                Check(item.Box.Read(item.Id).Hash == item.Hash, "Policy retry changed archive bytes.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }).GetAwaiter().GetResult();

    private static RuntimeClockEpoch Clock() => new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private static (DurableOutbox Box, Guid Id, string Path, string Hash) Capture(string root, bool automatic)
    {
        var queue = Path.Combine(root, "queue"); Directory.CreateDirectory(queue);
        var id = Guid.NewGuid(); var path = Path.Combine(queue, id.ToString("N") + (automatic ? "_auto_raw.zip" : "_bug_raw.zip"));
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var info = new StreamWriter(archive.CreateEntry("session.txt").Open())) info.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
            using var data = new StreamWriter(archive.CreateEntry("telemetry.csv").Open()); data.WriteLine(TelemetryCsv.Header);
            data.WriteLine(TelemetryCsv.Format(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true }));
        }
        var box = new DurableOutbox(Path.Combine(root, "outbox"), queue); box.SetAutomaticEnabled(automatic);
        var hash = EpisodeTransactionStore.Hash(path); box.Register(id, path, hash, automatic, DateTime.UtcNow); return (box, id, path, hash);
    }
    private static async Task HungHttpRecovers(string root, string identity)
    {
        var item = Capture(root, true); var workers = new List<ProcessWorkerHost>(); var attempts = new List<Guid>(); var overlapped = false;
        var owner = new UploadRuntimeOwner(item.Box, Executable, identity, Guid.NewGuid(), Clock(), _ => { }, n => n == 1 ? "upload-hang" : "upload-ok");
        owner.WorkerStarted += host =>
        {
            lock (workers)
            {
                if (workers.Count != 0 && !workers.Last().Exit.IsCompleted) overlapped = true;
                workers.Add(host); attempts.Add(item.Box.Read(item.Id).Attempt);
            }
        };
        var escalations = 0;
        using var supervisor = new ComponentSupervisor("upload.fixture", generation => new ManagedComponentGeneration(generation, owner.RunAsync),
            _ => Task.CompletedTask, (_, __) => Interlocked.Increment(ref escalations), new ComponentRecoveryOptions
            { OperationDeadline = TimeSpan.FromSeconds(3), CheckInterval = TimeSpan.FromMilliseconds(25), InitialBackoff = TimeSpan.FromMilliseconds(20), MaximumBackoff = TimeSpan.FromMilliseconds(50) });
        supervisor.Start();
        try { await Until(() => item.Box.Read(item.Id).State == OutboxState.Delivered, "Hung HTTP was not replaced and retried."); }
        finally { await supervisor.StopAsync(); }
        Check(!overlapped && escalations == 0 && workers.Count >= 2 && attempts.Distinct().Count() == attempts.Count && !File.Exists(item.Path) &&
            item.Box.Read(item.Id).Hash == item.Hash, "Upload restart overlapped owners, escalated unnecessarily or changed the archive identity.");
    }
    private static async Task PolicyAndManual(string root, string identity)
    {
        var item = Capture(Path.Combine(root, "automatic"), true);
        var owner = new UploadRuntimeOwner(item.Box, Executable, identity, Guid.NewGuid(), Clock(), _ => { }, n => n == 1 ? "upload-late" : "upload-ok");
        ProcessWorkerHost? worker = null; owner.WorkerStarted += host => Volatile.Write(ref worker, host);
        var attempt = owner.AttemptAsync(item.Id, CancellationToken.None);
        await Until(() => Volatile.Read(ref worker)?.LastHealth?.OperationId != null && Volatile.Read(ref worker)!.LastHealth!.OperationId != Guid.Empty,
            "Upload fixture did not enter its HTTP operation.");
        var originalClaim = item.Box.Read(item.Id).Attempt;
        item.Box.SetAutomaticEnabled(false);
        await owner.SetAutomaticAllowed(false);
        Check(worker!.Exit.IsCompleted, "Opt-out completed before the upload process exited.");
        try { await attempt; throw new InvalidOperationException("Opt-out did not cancel the active automatic attempt."); } catch (OperationCanceledException) { }
        Check(item.Box.Read(item.Id).State == OutboxState.Revoked && File.Exists(item.Path) && EpisodeTransactionStore.Hash(item.Path) == item.Hash && worker!.Exit.IsCompleted,
            "Late automatic result survived policy cancellation or its process still owns the attempt.");
        item.Box.SetAutomaticEnabled(true); await owner.SetAutomaticAllowed(true);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await owner.AttemptAsync(item.Id, deadline.Token);
        Check(item.Box.Read(item.Id).State == OutboxState.Delivered && item.Box.Read(item.Id).Attempt != originalClaim, "Re-enabled upload did not use a fresh claim.");
        // The durable policy can retire a claim before an in-memory observer
        // sees opt-out. That stale reply must not update receiver capabilities.
        var stale = Capture(Path.Combine(root, "stale-capability"), true); var observations = 0;
        var staleOwner = new UploadRuntimeOwner(stale.Box, Executable, identity, Guid.NewGuid(), Clock(), _ => { }, _ => "upload-late",
            _ => Interlocked.Increment(ref observations));
        ProcessWorkerHost? staleWorker = null; staleOwner.WorkerStarted += host => Volatile.Write(ref staleWorker, host);
        var staleAttempt = staleOwner.AttemptAsync(stale.Id, CancellationToken.None);
        await Until(() => Volatile.Read(ref staleWorker)?.LastHealth?.OperationId != null && Volatile.Read(ref staleWorker)!.LastHealth!.OperationId != Guid.Empty,
            "Stale capability fixture did not start its HTTP operation.");
        stale.Box.SetAutomaticEnabled(false);
        await staleAttempt;
        Check(observations == 0 && File.Exists(stale.Path) && EpisodeTransactionStore.Hash(stale.Path) == stale.Hash,
            "A retired durable claim applied its receiver capability or lost pending bytes.");
        var manual = Capture(Path.Combine(root, "manual"), false);
        var explicitOwner = new UploadRuntimeOwner(manual.Box, Executable, identity, Guid.NewGuid(), Clock(), _ => { }, _ => "upload-late");
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        explicitOwner.WorkerStarted += _ => started.TrySetResult(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var explicitAttempt = explicitOwner.AttemptAsync(manual.Id, timeout.Token); await started.Task;
        await explicitOwner.SetAutomaticAllowed(false); await explicitAttempt;
        Check(manual.Box.Read(manual.Id).State == OutboxState.Delivered, "Automatic opt-out cancelled a manual report.");
    }
    private static async Task Outcomes(string root, string identity)
    {
        foreach (var mode in new[] { "upload-defer", "upload-reject" })
        {
            var item = Capture(Path.Combine(root, mode), false);
            var owner = new UploadRuntimeOwner(item.Box, Executable, identity, Guid.NewGuid(), Clock(), _ => { }, _ => mode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await owner.AttemptAsync(item.Id, timeout.Token);
            var entry = item.Box.Read(item.Id);
            if (mode == "upload-defer") Check(entry.State == OutboxState.Pending && EpisodeTransactionStore.Hash(item.Path) == item.Hash, "Temporary HTTP failure discarded or quarantined the input.");
            else Check(entry.State == OutboxState.Quarantined && !File.Exists(item.Path) && EpisodeTransactionStore.Hash(entry.QuarantinePath) == item.Hash,
                "Permanent rejection did not preserve its exact bytes in quarantine.");
        }
    }
    private static async Task AcceptedCallbackRetainsOwnership(string root, string identityRoot)
    {
        var item = Capture(root, false); var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        var work = ProcessJobExecutor.RunAsync(Executable, worker, Clock(),
            new UploadJobMessage(item.Id, Guid.NewGuid(), item.Hash, item.Path, identityRoot, 0, false), message =>
            {
                if (!(message.Payload is UploadResultMessage)) throw new InvalidDataException("Unexpected callback fixture result.");
                entered.TrySetResult(true); release.Wait(); return true;
            }, cancel.Token, "upload-ok");
        try
        {
            await Until(() => entered.Task.IsCompleted, "Fixture never entered its accepted parent result callback."); cancel.Cancel();
            await Task.Delay(250);
            Check(!work.IsCompleted, "Job ownership ended while an accepted parent callback still held storage state.");
        }
        finally { release.Set(); }
        try { await work; throw new InvalidOperationException("Cancelled fixture returned success."); } catch (OperationCanceledException) { }
        Check(File.Exists(item.Path), "A helper deleted an archive without the parent's receipt commit.");
    }
    private static async Task Until(Func<bool> predicate, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException(message); await Task.Delay(25); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
