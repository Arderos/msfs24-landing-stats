using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Storage;
using LandingStats.App.TelemetryUpload;
using LandingStats.Archives;

namespace LandingStats.App.RegressionTests;

internal static partial class GoogleDriveBackupRegressionTests
{
    public static void ConcurrentLocalEditDuringDownloadIsPreserved()
    {
        foreach (var branch in new[] { "replace", "merge", "repair" })
        {
            var root = TemporaryDirectory("review-download-" + branch);
            try
            {
                var drive = new FakeGoogleDriveApi();
                var first = new LandingRepository(Path.Combine(root, "first"));
                first.Save(Record("same-landing", 200));
                var settings1 = new GoogleDriveCloudSettings();
                using var service1 = Service(drive, first, Path.Combine(root, "first.json"), () => settings1, value => settings1 = value);
                service1.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                var second = new LandingRepository(Path.Combine(root, "second"));
                var settings2 = new GoogleDriveCloudSettings();
                var gate = new object();
                using var service2 = new GoogleDriveBackupService(drive, second, gate,
                    new GoogleDriveSyncStateRepository(Path.Combine(root, "second.json")),
                    () => Task.FromResult(new GoogleDriveLocalSettings(settings2, true)),
                    (value, expected) => { settings2 = value; return Task.FromResult(true); });
                var seeded = service2.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(seeded.FailedLandings.Count == 0, "Initial download failed: " + string.Join("; ", seeded.FailedLandings.Select(pair => pair.Key + ": " + pair.Value)));
                var changed = first.LoadAll().Single();
                changed.Runway = "09L";
                first.UpdateSummary(changed);
                service1.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (branch == "merge")
                {
                    var local = second.LoadAll().Single();
                    local.AirportDistanceNauticalMiles = 1.2;
                    second.UpdateSummary(local);
                }
                if (branch == "repair")
                    File.WriteAllText(Directory.GetFiles(second.RootPath, "*.landing.json.gz", SearchOption.AllDirectories).Single(), "damaged");
                drive.BlockNextDownload();
                var pending = service2.SyncAsync(CancellationToken.None);
                Assert(drive.WaitUntilDownloadIsBlocked().Wait(TimeSpan.FromSeconds(10)), "Download did not reach " + branch);
                try
                {
                    lock (gate)
                    {
                        if (branch == "repair")
                        {
                            var repaired = Record("same-landing", 200);
                            repaired.Airport = "LBSF";
                            second.Save(repaired);
                        }
                        else
                        {
                            var concurrent = second.LoadAll().Single();
                            concurrent.Airport = "LBSF";
                            second.UpdateSummary(concurrent);
                        }
                    }
                }
                finally { drive.ReleaseBlockedDownload(); }
                var result = pending.GetAwaiter().GetResult();
                Equal("LBSF", second.LoadAll().Single().Airport, branch + " overwrote the concurrent airport edit");
                Assert(result.ConflictedLandingIds.Contains("same-landing"), branch + " did not defer the changed revision");
                Equal(0, result.DownloadedLandings, branch + " counted an uncommitted download");
                var retried = service2.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Equal(0, retried.ConflictedLandingIds.Count, branch + " did not reconcile on the next sync");
                Equal("LBSF", second.LoadAll().Single().Airport, branch + " retry lost local metadata");
                Equal("09L", second.LoadAll().Single().Runway, branch + " retry lost remote metadata");
            }
            finally { DeleteDirectory(root); }
        }
    }
}

internal static class ReviewEnrollmentRegressionTests
{
    public static void ClosedRegistrationRefreshesExistingIdentityAndRejectsNewOrPruned()
    {
        var oldEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        var endpoint = new Uri("https://receiver.invalid/");
        var root = Path.Combine(Path.GetTempPath(), "review-enrollment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", endpoint.AbsoluteUri);
            foreach (var enrolled in new[] { false, true })
            foreach (var rejected in new[] { false, true })
            {
                var path = Path.Combine(root, enrolled + "-" + rejected);
                var identity = new TelemetryUploadIdentityStore(path);
                if (enrolled) identity.MarkEnrolled(endpoint, true);
                var handler = new ClosedReceiver(rejected);
                using var client = new TelemetryUploadClient(path, handler, path, startQueueWorker: false);
                var result = client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (handler.Enrollments != (enrolled ? 1 : 0))
                    throw new Exception("Closed mode must refresh only existing identities.");
                var expected = enrolled && !rejected ? TelemetryPreparationState.Ready : TelemetryPreparationState.Unavailable;
                if (result.State != expected) throw new Exception("Closed registration returned " + result.State + " instead of " + expected);
                var report = Path.Combine(path, "pending_raw.zip");
                using (var file = File.Create(report))
                using (var archive = new CaptureArchiveWriter(file, false))
                {
                    using (var text = new StreamWriter(archive.CreateEntry("session.txt"))) text.Write("telemetry_schema=7\n");
                    using (var text = new StreamWriter(archive.CreateEntry("telemetry.csv"))) text.Write("fixture\n");
                }
                var outcome = client.SendClaimAsync(report, CancellationToken.None).GetAwaiter().GetResult();
                var shouldUpload = enrolled && !rejected;
                if ((outcome.Disposition == TelemetryUploadClient.UploadDisposition.Uploaded) != shouldUpload ||
                    handler.Uploads != (shouldUpload ? 1 : 0))
                    throw new Exception("Closed registration did not preserve the existing-client delivery contract.");
                if (!File.Exists(report)) throw new Exception("Preparation removed pending report bytes.");
                if (enrolled && rejected)
                {
                    if (new TelemetryUploadIdentityStore(path).IsEnrolled(endpoint)) throw new Exception("Server-pruned identity remained marked enrolled.");
                    client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (handler.Enrollments != 1) throw new Exception("Closed registration repeated a rejected new registration.");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", oldEndpoint);
            Directory.Delete(root, true);
        }
    }

    private sealed class ClosedReceiver : HttpMessageHandler
    {
        private readonly bool _reject;
        public int Enrollments;
        public int Uploads;
        public ClosedReceiver(bool reject) { _reject = reject; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/config")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"protocol\":1,\"registration_mode\":\"closed\",\"telemetry_schema\":8,\"telemetry_schemas\":[5,6,7,8]}") });
            if (request.RequestUri.AbsolutePath == "/v1/captures")
            {
                Uploads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"status\":\"accepted\",\"capture_id\":\"0123456789abcdef0123456789abcdef\"}") });
            }
            Enrollments++;
            return Task.FromResult(new HttpResponseMessage(_reject ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
