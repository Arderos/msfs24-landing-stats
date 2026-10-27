using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.TelemetryUpload;
using LandingStats.Archives;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class CaptureArchiveRegressionTests
{
    internal static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "formats-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Policy(root); WritersAndTransport(root).GetAwaiter().GetResult(); }
        finally { Directory.Delete(root, true); }
    }
    private static void Policy(string root)
    {
        var now = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc); var endpoint = new string('a', 64);
        var policy = new CaptureArchivePolicy(Path.Combine(root, "capabilities.json"), () => endpoint, () => now);
        UploadResultMessage Result(string key, bool supported) => new UploadResultMessage(Guid.NewGuid(), Guid.NewGuid(), new string('b', 64), 1, UploadResultKind.Deferred, 0, "", key, supported);
        Check(!policy.UseZstd(), "Unknown receiver enabled the new format.");
        policy.Observe(Result(endpoint, true)); Check(policy.UseZstd(), "Observed receiver was not enabled.");
        endpoint = new string('c', 64); Check(!policy.UseZstd(), "Different endpoint inherited a capability.");
        policy.Observe(Result(new string('a', 64), true)); Check(!policy.UseZstd(), "A stale endpoint reply was accepted.");
        endpoint = new string('a', 64); now = now.AddHours(1); Check(!policy.UseZstd(), "Expired capability stayed enabled.");
        policy.Observe(Result(endpoint, true)); now = now.AddSeconds(-1); Check(!policy.UseZstd(), "Clock reversal enabled a future observation."); now = now.AddSeconds(1);
        policy.Observe(Result(endpoint, false)); Check(!policy.UseZstd(), "Receiver downgrade was ignored.");
        File.WriteAllText(Path.Combine(root, "capabilities.json"), "broken"); Check(!policy.UseZstd(), "Damaged capability state did not fail closed.");
    }
    private static async Task WritersAndTransport(string root)
    {
        var samples = Enumerable.Range(0, 40).Select(i => new TelemetrySample { Sequence = i + 1, SimulationTimeSeconds = 20 + i * .05,
            HostElapsedSeconds = i * .05, MotionSimulation = true, ControllerCatalog = Array.Empty<byte>() }).ToArray();
        var candidate = new BugReportCandidate(4, samples, "MSFS", "Format fixture", "A320", "Fenix", Array.Empty<string>(), Array.Empty<LandingRecord>());
        var enabled = true; var repository = new BugReportRepository(Path.Combine(root, "reports"), () => enabled);
        var id = Guid.NewGuid(); var utc = DateTime.UtcNow;
        var report = repository.TryCreateAutomatic(candidate, utc, long.MaxValue, id)!;
        using (var input = File.OpenRead(report)) using (var archive = new CaptureArchiveReader(input))
        { Check(archive.Format == 2 && archive.GetEntry(ControllerCatalogArchive.EntryName) != null, "Report writer omitted negotiated format/provenance."); }
        var hash = EpisodeTransactionStore.Hash(report); enabled = false;
        Check(repository.TryCreateAutomatic(candidate, utc, long.MaxValue, id) == report && EpisodeTransactionStore.Hash(report) == hash, "Published report was rebuilt after downgrade.");
        var legacy = repository.Create(candidate, utc, true);
        using (var input = File.OpenRead(legacy)) using (var archive = new CaptureArchiveReader(input)) Check(archive.Format == 1, "Unknown receiver did not use legacy ZIP.");
        Check(TelemetryUploadClient.ReadCaptureSchema(report) == TelemetryCsv.SchemaVersion, "Upload schema reader cannot read new report.");
        var archives = Path.Combine(root, "raw"); var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives);
        var chunks = new RawChunkStore(Path.Combine(root, "chunks"), archives, outbox, () => enabled);
        var capture = Guid.NewGuid(); var metadata = new RawCaptureMetadata { TimestampUtcTicks = utc.Ticks, Simulator = "MSFS", Title = "Fixture" };
        enabled = true; var raw = chunks.Publish(capture, 1, metadata, samples); var rawHash = EpisodeTransactionStore.Hash(raw);
        enabled = false; chunks.Publish(capture, 1, metadata, samples); Check(EpisodeTransactionStore.Hash(raw) == rawHash, "Published RAW chunk was rebuilt after downgrade.");
        using (var input = File.OpenRead(raw)) using (var archive = new CaptureArchiveReader(input)) Check(archive.Format == 2, "RAW writer omitted negotiated format.");
        var fixture = Environment.GetEnvironmentVariable("MSFSLS_ARCHIVE_FIXTURE"); if (!string.IsNullOrWhiteSpace(fixture)) File.Copy(report, fixture, true);
        var handler = new Receiver(); var identity = Path.Combine(root, "identity");
        async Task<UploadResultMessage> Send(string path)
        {
            var job = new UploadJobMessage(id, Guid.NewGuid(), EpisodeTransactionStore.Hash(path), path, identity, 1, true);
            return await new UploadWorkerRole(() => handler).SendAsync(job, CancellationToken.None);
        }
        var oldReceiver = await Send(report);
        Check(oldReceiver.Result == UploadResultKind.Deferred && handler.Uploads == 0 && oldReceiver.ReceiverKey.Length == 64 && !oldReceiver.SupportsArchiveV2,
            "Unsupported receiver got new-format bytes or fabricated capability.");
        Check(EpisodeTransactionStore.Hash(report) == hash, "Deferred report bytes changed.");
        handler.Supported = true; handler.Status = HttpStatusCode.ServiceUnavailable;
        Check((await Send(report)).Result == UploadResultKind.Deferred && handler.Uploads == 1, "Temporary failure did not defer the same report.");
        handler.Status = HttpStatusCode.Created; var accepted = await Send(report);
        Check(accepted.Result == UploadResultKind.Uploaded && accepted.SupportsArchiveV2 && handler.Uploads == 2 && handler.Bytes!.SequenceEqual(File.ReadAllBytes(report)),
            "Capable receiver/retry did not receive exact original bytes.");
        handler.Supported = false;
        Check((await Send(legacy)).Result == UploadResultKind.Uploaded && handler.Uploads == 3, "Legacy report no longer works with old receiver.");
        Check((await Send(report)).Result == UploadResultKind.Deferred && handler.Uploads == 3 && File.Exists(report), "Receiver rollback lost a pending new-format report.");
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload);
        var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var isolated = await ProcessJobExecutor.RunAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), worker, clock,
            new UploadJobMessage(id, Guid.NewGuid(), hash, report, identity, 1, true), message => (UploadResultMessage)message.Payload,
            timeout.Token, "upload-codec");
        Check(isolated.Result == UploadResultKind.Uploaded && isolated.SupportsArchiveV2, "Isolated codec dependency check failed: " + isolated.Reason);
        var policy = new CaptureArchivePolicy(Path.Combine(root, "observed.json"));
        var ownedId = Guid.NewGuid(); var ownedPath = Path.Combine(archives, ownedId.ToString("N") + "_auto_raw.zip"); File.Copy(report, ownedPath);
        outbox.Register(ownedId, ownedPath, hash, false, utc);
        var observed = 0;
        var owner = new UploadRuntimeOwner(outbox, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), identity,
            Guid.NewGuid(), clock, _ => { }, _ => "upload-codec", result => { observed++; policy.Observe(result); });
        await owner.AttemptAsync(ownedId, timeout.Token);
        Check(observed == 1 && outbox.Read(ownedId).State == OutboxState.Delivered && policy.UseZstd() && EpisodeTransactionStore.Hash(report) == hash,
            "Parent did not persist capability from the accepted current claim.");
    }
    private sealed class Receiver : HttpMessageHandler
    {
        public bool Supported; public int Uploads; public byte[]? Bytes; public HttpStatusCode Status = HttpStatusCode.Created;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/config")) return Reply(HttpStatusCode.OK,
                "{\"protocol\":1,\"registration_mode\":\"open\",\"telemetry_schema\":" + TelemetryCsv.SchemaVersion + ",\"capture_kinds\":[\"automatic_landing\",\"bug_report\"],\"decision_journal_formats\":[1],\"controller_catalog_formats\":[1],\"archive_formats\":" + (Supported ? "[1,2]" : "[1]") + "}");
            if (request.RequestUri.AbsolutePath.EndsWith("/enroll")) return Reply(HttpStatusCode.Created, "{}");
            if (request.RequestUri.AbsolutePath.EndsWith("/captures")) { Uploads++; Bytes = await request.Content!.ReadAsByteArrayAsync(); return Reply(Status, "{\"status\":\"accepted\",\"capture_id\":\"0123456789abcdef0123456789abcdef\"}"); }
            throw new InvalidOperationException("Unexpected HTTP fixture request.");
        }
        protected override void Dispose(bool disposing) { } // Shared in sequential independent transport attempts.
        private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
