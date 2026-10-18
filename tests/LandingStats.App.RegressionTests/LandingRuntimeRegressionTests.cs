using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.TelemetryUpload;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class LandingRuntimeRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "lr-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var done = false; var repository = new LandingRepository(Path.Combine(root, "h")); var gate = new object();
        var identityRoot = Path.Combine(root, "i"); _ = new TelemetryUploadIdentityStore(identityRoot).Identity();
        var runtimeRoot = Path.Combine(root, "r"); var legacy = Path.Combine(root, "legacy"); var escalations = new ConcurrentQueue<string>(); var failures = 0;
        var options = new ComponentRecoveryOptions { CheckInterval = TimeSpan.FromMilliseconds(25), InitialBackoff = TimeSpan.FromMilliseconds(10),
            MaximumBackoff = TimeSpan.FromMilliseconds(50), OperationDeadline = TimeSpan.FromSeconds(20) };
        var uploadFixture = "upload-ok";
        LandingRuntime Create(bool flight, bool presentationFailure) => new LandingRuntime(runtimeRoot, AppDomain.CurrentDomain.BaseDirectory, identityRoot, repository, gate,
            (snapshot, _) =>
            {
                if (presentationFailure && snapshot.Episodes.Any(item => item.ReportReady) && Interlocked.CompareExchange(ref failures, 1, 0) == 0)
                    throw new IOException("Presentation failed after history/report commit.");
                return Task.CompletedTask;
            }, (name, _) => escalations.Enqueue(name), (role, _) => role == ComponentRole.Analysis ? "analysis-local" :
                role == ComponentRole.Upload ? uploadFixture : role == ComponentRole.Simulator && flight ? "landing" : "normal", options, legacyQueue: legacy);
        try
        {
            string oldSession; Guid episode; Guid manualReport; Guid pendingId; int historyCount;
            using (var runtime = Create(true, true))
            {
                try { using var duplicate = Create(false, false); throw new InvalidOperationException("A second runtime acquired live storage."); }
                catch (IOException) { }
                // An abandoned claim is deliberately held by a live isolated process.
                // Startup must identify its whole job and wait for exit before retry.
                Directory.CreateDirectory(runtime.Archives); var orphanId = Guid.NewGuid();
                var path = Path.Combine(runtime.Archives, orphanId.ToString("N") + "_bug_raw.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    using (var text = new StreamWriter(zip.CreateEntry("session.txt").Open())) text.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
                    using var text2 = new StreamWriter(zip.CreateEntry("telemetry.csv").Open()); text2.WriteLine(TelemetryCsv.Header);
                    text2.WriteLine(TelemetryCsv.Format(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 }));
                }
                runtime.Outbox.Register(orphanId, path, EpisodeTransactionStore.Hash(path), false, DateTime.UtcNow);
                var former = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload);
                var claim = runtime.Outbox.TryClaim(orphanId, former)!;
                using var orphan = new ProcessWorkerHost(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), former,
                    runtime.Identity.Clock, (_, __) => Task.CompletedTask);
                await orphan.StartAsync(TimeSpan.FromSeconds(10), "normal");
                try
                {
                    try
                    {
                        using var duplicate = OwnedProcess.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), Array.Empty<string>(), jobName: OwnedProcess.JobName(former));
                        throw new InvalidOperationException("An existing worker job was reused.");
                    }
                    catch (IOException) { }
                    Check(!orphan.Exit.IsCompleted, "Rejecting a duplicate identity killed its existing owner.");
                    await runtime.StartAsync(true, rawCapture: true);
                    await Until(() => runtime.Outbox.Read(orphanId).State == OutboxState.Delivered, "Abandoned upload was not recovered.");
                    await Until(() => orphan.Exit.IsCompleted, "Replacement upload preceded former job exit.");
                    Check(runtime.Outbox.Read(orphanId).Attempt != claim.AttemptId, "Abandoned upload reused its former attempt identity.");
                    await Until(() => runtime.Store.Enumerate().Any(id => runtime.Store.Read(id).Stage == EpisodeWorkStage.Completed), "Composed pipeline did not complete the landing.",
                        () => string.Join("; ", runtime.Health.Select(state => state.Component + ":" + state.Generation + ":" + state.State + ":" + state.Reason)) + " | " +
                            string.Join("; ", runtime.Store.Enumerate().Select(id => { var state = runtime.Store.Read(id); return state.Stage + ":" + state.LastFailure; })));
                    episode = runtime.Store.Enumerate().Single(); var manifest = runtime.Store.Read(episode);
                    Check(manifest.AutomaticAllowedAtCapture && manifest.PolicyRevision == runtime.Outbox.Policy.Revision, "First capture preceded durable startup policy.");
                    await Until(() => runtime.Outbox.Read(manifest.ArchiveId).State == OutboxState.Delivered, "Committed landing was not delivered.");
                    await runtime.SetRawCaptureAsync(false);
                    await Until(() =>
                    {
                        var chunks = Path.Combine(runtime.Root, "chunks");
                        var ids = Directory.Exists(chunks) ? Directory.GetDirectories(chunks).Select(Path.GetFileName)
                            .Where(name => Guid.TryParseExact(name, "N", out _)).Select(Guid.Parse).ToArray() : Array.Empty<Guid>();
                        return ids.Length != 0 && ids.All(id => Delivered(runtime.Outbox, id));
                    }, "RAW effects were not independently exported and delivered.");
                    await Until(() => runtime.Health.Single(item => item.Component == "presentation").Generation >= 2 && failures == 1,
                        "Presentation did not recover independently after committed history.");
                    lock (gate) historyCount = repository.LoadAll().Count;
                    Check(historyCount > 0 && runtime.Store.Enumerate().Count() == 1, "Presentation restart lost or duplicated history.");
                    // Exercise the public policy task while its child really
                    // owns an HTTP operation, not only after the queue drains.
                    uploadFixture = "upload-hang";
                    var uploadOwner = (UploadRuntimeOwner)typeof(LandingRuntime).GetField("_uploads",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runtime)!;
                    ProcessWorkerHost? activeUpload = null;
                    uploadOwner.WorkerStarted += host => Volatile.Write(ref activeUpload, host);
                    pendingId = Guid.NewGuid();
                    var pendingPath = Path.Combine(runtime.Archives, pendingId.ToString("N") + "_auto_raw.zip");
                    using (var zip = ZipFile.Open(pendingPath, ZipArchiveMode.Create))
                    {
                        using (var metadata = new StreamWriter(zip.CreateEntry("session.txt").Open()))
                            metadata.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
                        using var data = new StreamWriter(zip.CreateEntry("telemetry.csv").Open());
                        data.WriteLine(TelemetryCsv.Header);
                        data.WriteLine(TelemetryCsv.Format(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 }));
                    }
                    runtime.Outbox.Register(pendingId, pendingPath, EpisodeTransactionStore.Hash(pendingPath), true, DateTime.UtcNow);
                    runtime.WakeDelivery();
                    await Until(() => Volatile.Read(ref activeUpload)?.LastHealth?.OperationId != null &&
                        Volatile.Read(ref activeUpload)!.LastHealth!.OperationId != Guid.Empty, "Public opt-out fixture did not enter HTTP.");
                    await runtime.SetAutomaticDeliveryAsync(false);
                    Check(!runtime.Outbox.Policy.AutomaticEnabled && activeUpload!.Exit.IsCompleted,
                        "Public opt-out completed before its upload process exited and policy committed.");
                    uploadFixture = "upload-ok";
                    manualReport = await runtime.RequestManualReportAsync(episode);
                    await Until(() => Delivered(runtime.Outbox, manualReport),
                        "Explicit report did not survive automatic opt-out.");
                    Check(await runtime.RequestManualReportAsync(episode) == manualReport && !File.Exists(runtime.Outbox.Read(manualReport).Path),
                        "Repeated report intent recreated a delivered archive.");
                    Directory.CreateDirectory(legacy);
                    var original = Path.Combine(legacy, "original_bug_raw.zip");
                    using (var zip = ZipFile.Open(original, ZipArchiveMode.Create))
                    {
                        using (var text = new StreamWriter(zip.CreateEntry("session.txt").Open())) text.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
                        using var text2 = new StreamWriter(zip.CreateEntry("telemetry.csv").Open()); text2.WriteLine(TelemetryCsv.Header);
                        text2.WriteLine(TelemetryCsv.Format(new TelemetrySample { Sequence = 17, SimulationTimeSeconds = 4 }));
                    }
                    var legacyHash = EpisodeTransactionStore.Hash(original); var legacyId = Guid.ParseExact(legacyHash.Substring(0, 32), "N");
                    var duplicateLegacyBytes = File.ReadAllBytes(original);
                    await Until(() => Delivered(runtime.Outbox, legacyId) && !File.Exists(original),
                        "Previous-version manual queue was not adopted and delivered.");
                    Check(runtime.Outbox.Read(legacyId).Hash == legacyHash, "Legacy queue adoption changed archive bytes.");
                    File.WriteAllBytes(original, duplicateLegacyBytes);
                    await Until(() => !File.Exists(original), "Repeated legacy input was not reconciled with its delivery receipt.");
                    Check(!File.Exists(runtime.Outbox.Read(legacyId).Path), "Repeated legacy input recreated its delivered ZIP.");
                    oldSession = runtime.SessionRoot;
                }
                finally { await Deadline(runtime.StopAsync()); await orphan.TerminateAsync(TimeSpan.FromSeconds(3)); }
                Check(runtime.Health.All(item => item.State == SupervisionState.Stopped), "Composed shutdown left an owner alive.");
            }
            using (var next = Create(false, false))
            {
                // A malformed completion marker cannot authorize exporting or
                // collecting a different old session's effects.
                var badIdentity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), next.Identity.Clock);
                var bad = Path.Combine(runtimeRoot, "sessions", badIdentity.RuntimeId.ToString("N"));
                RuntimeSession.Create(bad, badIdentity, next.Engine.Identity);
                DurableFile.WriteJson(Path.Combine(bad, "recovered.json"), new RecordingRecoveryBundle { Runtime = Guid.NewGuid(), Engine = next.Engine.Identity, LastSegment = 1 });
                try
                {
                    await next.StartAsync(false);
                    await Until(() => RuntimeSessionDirectories.Collected(oldSession) ||
                        File.Exists(Path.Combine(oldSession, "recovered.json")) && File.Exists(Path.Combine(oldSession, "raw-completed.json")),
                        "Startup did not recover and finalize the previous runtime session.");
                    await Task.Delay(1200);
                    Check(!File.Exists(Path.Combine(bad, "raw-completed.json")), "Corrupt recovery marker bypassed ownership validation.");
                    lock (gate) Check(repository.LoadAll().Count == historyCount, "Cold restart duplicated or lost committed history.");
                    Check(next.Store.Read(episode).Stage == EpisodeWorkStage.Completed && next.Outbox.Enumerate().SequenceEqual(new[] { pendingId }) &&
                        next.Outbox.Read(pendingId).State == OutboxState.Revoked && next.Outbox.Read(manualReport).State == OutboxState.Delivered,
                        "Cold restart recreated a previously delivered archive.");
                }
                finally { await Deadline(next.StopAsync()); }
            }
            Check(escalations.IsEmpty, "A recoverable component unnecessarily escalated the application: " + string.Join(",", escalations));
            done = true;
        }
        finally { if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
    private static async Task Until(Func<bool> condition, string message, Func<string>? diagnostic = null)
    { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException(message + (diagnostic == null ? "" : " " + diagnostic())); await Task.Delay(30); } }
    private static async Task Deadline(Task task)
    { if (await Task.WhenAny(task, Task.Delay(35000)) != task) throw new TimeoutException("Composed shutdown timed out."); await task; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Delivered(DurableOutbox outbox, Guid id)
    { try { return outbox.Read(id).State == OutboxState.Delivered; } catch (FileNotFoundException) { return false; } }
}
