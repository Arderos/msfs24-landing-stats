using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using LandingStats.App.GoogleDrive;
using LandingStats.App.TelemetryUpload;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Models;
using System.Reflection;
using System.Linq;

namespace LandingStats.App.RegressionTests;

internal static class DeliveryRemediationRegressionTests
{
    public static void RunAll()
    {
        foreach (var value in new[] { "<html>proxy login</html>", "{}", "{\"status\":\"pending\"}", new string('x', 4097) })
            Check(!Acknowledged(value), "Unconfirmed response retired the capture.");
        foreach (var status in new[] { "accepted", "already_received" })
            Check(Acknowledged("{\"status\":\"" + status + "\",\"capture_id\":\"0123456789abcdef0123456789abcdef\"}"), "Receiver acknowledgement was rejected.");
        var root = Path.Combine(Path.GetTempPath(), "delivery-remediation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tokenPath = Path.Combine(root, "token.json"); var tokens = new GoogleOAuthTokenStore(tokenPath);
            tokens.Save("fixture-refresh-token", null);
            tokens.DeleteAsync<object>("refresh-failed").GetAwaiter().GetResult();
            Check(tokens.HasRefreshToken() && File.Exists(tokenPath), "Transient OAuth deletion removed a durable credential.");
            Check(!tokens.ImportSnapshot(Path.Combine(root, "unused"), new string('f', 64), ""), "Changed credential accepted a stale clear result.");
            File.Delete(tokenPath);
            try { tokens.ExportSnapshot(Path.Combine(root, "snapshot")); throw new Exception("Missing token was exported."); }
            catch (GoogleDriveNotSignedInException) { }
            Check(!tokens.HasRefreshToken(), "Missing-token cache stayed signed in.");
            var statePath = Path.Combine(root, "sync.json"); File.WriteAllText(statePath, "{corrupt", Encoding.UTF8);
            var state = new GoogleDriveSyncStateRepository(statePath); var bytes = File.ReadAllBytes(statePath);
            try { state.AddPendingDelete("landing"); throw new Exception("Unreadable state was silently replaced."); }
            catch (System.Runtime.Serialization.SerializationException) { }
            Check(Convert.ToBase64String(bytes) == Convert.ToBase64String(File.ReadAllBytes(statePath)), "Unreadable cloud state was overwritten.");
            File.WriteAllText(tokenPath + ".tmp", "encrypted fixture"); tokens.Clear();
            Check(!File.Exists(tokenPath + ".tmp"), "Disconnect retained the abandoned credential temporary file.");
            var jobs = Path.Combine(root, "jobs"); var job = Path.Combine(jobs, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job); File.WriteAllBytes(Path.Combine(job, "job.owner"), new byte[] { 0, 1, 0 });
            File.WriteAllText(Path.Combine(job, "token.json"), "encrypted fixture");
            Check(RuntimeScratchLease.Collect(jobs, job) && !Directory.Exists(job), "Parent death before worker entry retained its credential snapshot.");
            var parse = typeof(LandingStats.App.Updater.Program).GetMethod("ParseInvocation", BindingFlags.NonPublic | BindingFlags.Static)!;
            var arguments = new[] { "--apply", "--parent-pid", "123", "--target", Assembly.GetExecutingAssembly().Location,
                "--version", "0.9.5", "--ready-event", "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N") };
            Check(parse.Invoke(null, new object[] { arguments }) != null, "Legacy nine-argument updater contract failed.");
            arguments = arguments.Concat(new[] { "--manifest", "update-channel.txt" }).ToArray();
            Check(parse.Invoke(null, new object[] { arguments }) != null, "Published eleven-argument updater contract failed.");
            arguments = arguments.Concat(new[] { "--commit-event", "Local\\MSFSLandingStatsUpdateCommit-" + Guid.NewGuid().ToString("N") }).ToArray();
            Check(parse.Invoke(null, new object[] { arguments }) != null, "Guardian thirteen-argument updater contract failed.");
        }
        finally { Directory.Delete(root, true); }
        GoogleDriveBackupRegressionTests.DamagedLandingAndExpiredTrashRemainIsolated();
        TemporaryCopiesNeedIndependentEvidence();
        DurablePublicationRegressionTests.RunAll();
    }
    private static void TemporaryCopiesNeedIndependentEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "temporary-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var published = Path.Combine(root, "published_raw.zip"); File.WriteAllBytes(published, new byte[] { 1, 2, 3 });
            var duplicate = published + ".tmp"; File.Copy(published, duplicate);
            var interrupted = Path.Combine(root, ".interrupted-" + Guid.NewGuid().ToString("N") + ".part"); File.Copy(published, interrupted);
            var sole = Path.Combine(root, "sole_raw.zip.tmp"); File.WriteAllBytes(sole, new byte[] { 9 });
            var active = Path.Combine(root, "active_raw.zip.tmp"); File.Copy(published, active);
            using (var writer = new FileStream(active, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                TemporaryEvidenceCleanup.Archives(root);
                Check(!File.Exists(duplicate) && !File.Exists(interrupted), "Published duplicates were retained.");
                Check(File.Exists(sole) && File.Exists(active), "Cleanup deleted sole evidence or active writer bytes.");
            }
            TemporaryEvidenceCleanup.Archives(root);
            Check(!File.Exists(active) && File.Exists(sole), "Released duplicate was not collected, or sole copy was removed.");

            var history = Path.Combine(root, "history"); var repository = new LandingRepository(history);
            var record = new LandingRecord { Id = "cleanup-proof", TimestampUtc = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc) };
            var detail = repository.Save(record);
            var copy = Path.Combine(history, ".google-drive-import-" + Guid.NewGuid().ToString("N") + ".landing.json.gz.tmp"); File.Copy(detail, copy);
            var only = Path.Combine(history, ".google-drive-read-" + Guid.NewGuid().ToString("N") + ".landing.json.gz.tmp");
            record.Id = "unpublished-copy"; File.WriteAllBytes(only, repository.SerializeForBackup(record));
            var unknown = Path.Combine(history, ".google-drive-import-" + Guid.NewGuid().ToString("N") + ".landing.json.gz.tmp"); File.WriteAllText(unknown, "partial");
            var reloaded = new LandingRepository(history); _ = reloaded.LoadAll();
            Check(!File.Exists(copy) && File.Exists(only) && File.Exists(unknown), "History cleanup discarded an unproven source or retained an exact duplicate.");
            var survivingBytes = File.ReadAllBytes(detail); reloaded.Delete("cleanup-proof"); File.WriteAllBytes(detail, survivingBytes);
            _ = new LandingRepository(history).LoadAll();
            Check(!File.Exists(detail), "Interrupted deletion rereads a tombstoned detail on every startup.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static bool Acknowledged(string body)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        return TelemetryUploadClient.HasCaptureAcknowledgementAsync(response, CancellationToken.None).GetAwaiter().GetResult();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
