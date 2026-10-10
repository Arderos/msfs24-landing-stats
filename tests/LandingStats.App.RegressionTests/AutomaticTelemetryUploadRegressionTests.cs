using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.TelemetryUpload;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class AutomaticTelemetryUploadRegressionTests
{
    public static void AutomaticReportsAreSilentAndDoNotConsentToLegacy()
    {
        using var fixture = new Fixture();
        var automatic = fixture.Report("automatic", true);
        var legacy = fixture.Report("legacy", false, legacy: true);
        using var client = fixture.Client();
        var notifications = 0;
        client.StatusChanged += (_, _) => Interlocked.Increment(ref notifications);
        Require(!client.AutomaticUploadEnabled && !client.EnqueueAutomatic(automatic), "Uploader requires explicit application setting initialization.");
        Require(!client.HasEligiblePendingReports(), "Disabled automatic and unconsented legacy archives are not eligible.");
        client.SetAutomaticUploadEnabled(true);
        Require(client.HasEligiblePendingReports(), "Enabled automatic backlog is eligible.");
        client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Wait(() => !File.Exists(automatic), "Automatic archive uploads.");
        Require(File.Exists(legacy), "New default does not upload old full-flight captures without their original consent.");
        Require(!client.ConsentAccepted, "Automatic setting must not manufacture legacy consent.");
        Require(Volatile.Read(ref notifications) == 0, "Automatic scheduling and success are silent.");
    }

    public static void DisabledAutomaticReportsRemainSeparateFromConsentedLegacyAndManual()
    {
        using var fixture = new Fixture();
        var automatic = fixture.Report("automatic", true);
        var legacy = fixture.Report("legacy", false, legacy: true);
        using var client = fixture.Client();
        client.AcceptConsent();
        Require(!client.HasEligiblePendingReports() && !client.Enqueue(legacy), "Global opt-out overrides historical RAW consent, including direct enqueue.");
        client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(fixture.Handler.Enrollments == 0 && fixture.Handler.Uploaded.IsEmpty, "Opt-out does not enroll or upload historically consented RAW captures.");
        Require(File.Exists(legacy) && File.Exists(automatic), "Both automatic formats remain local while disabled.");
        var manual = fixture.Report("manual", false);
        Require(client.Enqueue(manual), "Manual reporting still works with automatic upload disabled.");
        Wait(() => !File.Exists(manual), "Explicit manual archive uploads.");
        Require(File.Exists(legacy) && File.Exists(automatic), "Manual completion does not start either opted-out automatic backlog.");
        client.SetAutomaticUploadEnabled(true);
        client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Wait(() => !File.Exists(legacy) && !File.Exists(automatic), "Re-enabling resumes new automatic and previously consented legacy reports.");
    }

    public static void OptOutCancelsAutomaticUploadButNotManualReports()
    {
        using var fixture = new Fixture();
        fixture.Handler.BlockFirstCapture = true;
        var first = fixture.Report("automatic-a", true);
        var second = fixture.Report("automatic-b", true);
        using var client = fixture.Client();
        client.SetAutomaticUploadEnabled(true);
        client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Wait(() => fixture.Handler.Blocked.IsSet, "Automatic HTTP request reaches the cancellation fixture.");
        // A cancellation callback may resume the upload loop synchronously.
        // Bound this check so a blocking dequeue regression fails, rather than
        // hanging the entire test runner inside CancellationTokenSource.Cancel.
        var optOut = Task.Run(() => client.SetAutomaticUploadEnabled(false));
        Require(optOut.Wait(TimeSpan.FromSeconds(5)), "Opt-out returns without blocking on the next queue item.");
        Wait(() => fixture.Handler.Cancelled.IsSet, "Opt-out cancels active automatic HTTP upload.");
        var manual = fixture.Report("manual", false);
        Require(client.Enqueue(manual), "Manual work can still queue after opt-out.");
        Wait(() => !File.Exists(manual), "Manual work proceeds after cancelled automatic upload.");
        Require(File.Exists(first) && File.Exists(second), "Cancelled and queued automatic archives remain durable.");
        Require(fixture.Handler.Uploaded.Count == 1, "No second automatic archive was sent while disabled.");
        client.SetAutomaticUploadEnabled(true);
        client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Wait(() => !File.Exists(first) && !File.Exists(second), "Re-enabling resumes both durable automatic archives.");
    }

    public static void OptOutCancelsAutomaticEnrollment()
    {
        foreach (var legacy in new[] { false, true })
        {
            using var fixture = new Fixture();
            fixture.Handler.BlockFirstConfig = true;
            fixture.Report("automatic", !legacy, legacy: legacy);
            using var client = fixture.Client();
            if (legacy) client.AcceptConsent();
            client.SetAutomaticUploadEnabled(true);
            var prepare = client.PreparePendingReportsUntilReadyAsync(CancellationToken.None);
            Wait(() => fixture.Handler.Blocked.IsSet, "Automatic enrollment reaches the receiver-config request.");
            client.SetAutomaticUploadEnabled(false);
            Require(prepare.Wait(TimeSpan.FromSeconds(10)), "Opt-out completes automatic-only enrollment without a long retry wait.");
            Require(fixture.Handler.Cancelled.IsSet && fixture.Handler.Enrollments == 0 && fixture.Handler.Uploaded.IsEmpty,
                "Opt-out prevents enrollment for new and legacy automatic reports, preserving their data.");
        }
    }

    public static void OptOutCancelsConsentedLegacyUploadButNotManualReports()
    {
        using var fixture = new Fixture();
        fixture.Handler.BlockFirstCapture = true;
        var first = fixture.Report("legacy-a", false, legacy: true);
        var second = fixture.Report("legacy-b", false, legacy: true);
        using var client = fixture.Client();
        var notifications = 0;
        client.StatusChanged += (_, _) => Interlocked.Increment(ref notifications);
        client.AcceptConsent();
        client.SetAutomaticUploadEnabled(true);
        client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Wait(() => fixture.Handler.Blocked.IsSet, "Consented legacy RAW reaches the cancellation fixture.");
        var optOut = Task.Run(() => client.SetAutomaticUploadEnabled(false));
        Require(optOut.Wait(TimeSpan.FromSeconds(5)), "Opt-out of legacy upload does not block its caller.");
        Wait(() => fixture.Handler.Cancelled.IsSet, "Global opt-out cancels in-flight legacy RAW upload.");
        Require(Volatile.Read(ref notifications) == 0, "Legacy background uploads also remain silent.");
        var manual = fixture.Report("manual", false);
        Require(client.Enqueue(manual), "Explicit manual reporting remains independent of global automatic opt-out.");
        Wait(() => !File.Exists(manual), "Manual report proceeds after legacy cancellation.");
        Require(File.Exists(first) && File.Exists(second) && fixture.Handler.Uploaded.Count == 1,
            "Neither cancelled nor queued legacy RAW bypasses opt-out.");
        client.SetAutomaticUploadEnabled(true);
        client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Wait(() => !File.Exists(first) && !File.Exists(second), "Re-enabling resumes preserved consented legacy backlog.");
    }

    public static void RapidOptOutAndInPreservesRequestedRetry()
    {
        using var fixture = new Fixture();
        fixture.Handler.BlockFirstConfig = true;
        fixture.Handler.DelayCancellationCompletion = true;
        var automatic = fixture.Report("automatic", true);
        using var client = fixture.Client();
        using var lifetime = new CancellationTokenSource();
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetField("_bugReportRetryGate", flags)!.SetValue(window, new object());
        typeof(MainWindow).GetField("_lifetimeCancellation", flags)!.SetValue(window, lifetime);
        typeof(MainWindow).GetField("_bugReportRetryTask", flags)!.SetValue(window, Task.CompletedTask);
        var start = typeof(MainWindow).GetMethod("StartPendingBugReportRetry", flags)!;
        Task Pending() => (Task)typeof(MainWindow).GetField("_bugReportRetryTask", flags)!.GetValue(window)!;
        try
        {
            client.SetAutomaticUploadEnabled(true);
            start.Invoke(window, new object[] { client });
            Wait(() => fixture.Handler.Blocked.IsSet, "Automatic retry reaches its first enrollment.");
            client.SetAutomaticUploadEnabled(false);
            Wait(() => fixture.Handler.Cancelled.IsSet, "Opt-out cancels enrollment before delayed completion.");
            Require(!Pending().IsCompleted, "Fixture keeps canceled enrollment unwinding during re-enable.");
            client.SetAutomaticUploadEnabled(true);
            start.Invoke(window, new object[] { client });
            fixture.Handler.ReleaseCancellation.TrySetResult(true);
            Wait(() => !File.Exists(automatic), "Re-enable request must survive canceled task completion without another landing or restart.");
        }
        finally
        {
            fixture.Handler.ReleaseCancellation.TrySetResult(true);
            lifetime.Cancel();
            Require(Pending().Wait(TimeSpan.FromSeconds(15)), "Retry fixture drains cleanly.");
        }
    }

    public static void AutomaticTimeoutRetriesSilently()
    {
        using var fixture = new Fixture();
        fixture.Handler.TimeoutFirstCapture = true;
        var automatic = fixture.Report("automatic", true);
        using var client = fixture.Client();
        var notifications = 0;
        client.StatusChanged += (_, _) => Interlocked.Increment(ref notifications);
        client.SetAutomaticUploadEnabled(true);
        client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Wait(() => !File.Exists(automatic), "An HTTP timeout must retry the durable automatic capture, not stop the worker.");
        Require(fixture.Handler.CaptureRequests == 2 && notifications == 0, "Timed-out automatic upload retries silently once.");
    }

    public static void OldReceiverDefersNewArchivesUntilCapabilityIsAvailable()
    {
        using var fixture = new Fixture();
        fixture.Handler.SupportsNewReports = false;
        var automatic = fixture.Report("automatic", true);
        var manual = fixture.Report("manual", false, journal: true);
        using var client = fixture.Client();
        client.SetAutomaticUploadEnabled(true);
        client.PreparePendingReportsUntilReadyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Wait(() => fixture.Handler.ConfigRequests >= 4, "Both new formats check receiver capabilities before sending.");
        Require(fixture.Handler.Uploaded.IsEmpty, "Unsupported receiver does not receive incompatible capture POSTs.");
        Require(File.Exists(automatic) && File.Exists(manual), "Both archives stay queued rather than being quarantined by an old receiver's 422.");
        Require(Directory.GetFiles(fixture.Queue, "*.rejected-*.zip").Length == 0, "Capability rollout never rejects queued data.");
        fixture.Handler.SupportsNewReports = true;
        Wait(() => !File.Exists(automatic) && !File.Exists(manual), "A later capability refresh uploads both preserved archives.");
    }

    private static void Wait(Func<bool> predicate, string message) =>
        Require(SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(15)), message);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "landing-stats-auto-upload-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousEndpoint;
        public Fixture()
        {
            Queue = Path.Combine(_root, "queue");
            Directory.CreateDirectory(Queue);
            _previousEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://telemetry.example.test/");
        }
        public string Queue { get; }
        public Handler Handler { get; } = new Handler();
        public TelemetryUploadClient Client() => new TelemetryUploadClient(
            Queue, Handler, Path.Combine(_root, "identity"), retryDelay: TimeSpan.FromMilliseconds(20));

        public string Report(string name, bool automatic, bool legacy = false, bool journal = false)
        {
            var path = Path.Combine(Queue, name + (automatic ? "_auto_raw.zip" : legacy ? "_raw.zip" : "_bug_raw.zip"));
            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(archive.CreateEntry("session.txt").Open()))
                writer.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion + "\nfixture=" + name);
            using (var writer = new StreamWriter(archive.CreateEntry("telemetry.csv").Open()))
                writer.WriteLine(TelemetryCsv.Header);
            if (automatic || journal)
            {
                using var writer = new StreamWriter(archive.CreateEntry("decisions.json").Open());
                writer.Write("{\"format\":1,\"entries\":[]}");
            }
            return path;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", _previousEndpoint);
            Directory.Delete(_root, true);
            Handler.Blocked.Dispose();
            Handler.Cancelled.Dispose();
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool BlockFirstCapture;
        public bool BlockFirstConfig;
        public bool TimeoutFirstCapture;
        public bool DelayCancellationCompletion;
        public TaskCompletionSource<bool> ReleaseCancellation { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool SupportsNewReports = true;
        private int _captureRequests;
        private int _configRequests;
        private int _enrollments;
        public int ConfigRequests => Volatile.Read(ref _configRequests);
        public int CaptureRequests => Volatile.Read(ref _captureRequests);
        public int Enrollments => Volatile.Read(ref _enrollments);
        public ManualResetEventSlim Blocked { get; } = new ManualResetEventSlim();
        public ManualResetEventSlim Cancelled { get; } = new ManualResetEventSlim();
        public ConcurrentQueue<string> Uploaded { get; } = new ConcurrentQueue<string>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/v1/config", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _configRequests) == 1 && BlockFirstConfig)
                    await Block(cancellationToken).ConfigureAwait(false);
                return Json("{\"protocol\":1,\"registration_mode\":\"open\",\"telemetry_schema\":6" +
                    (SupportsNewReports ? ",\"capture_kinds\":[\"raw_debug\",\"bug_report\",\"automatic_landing\"],\"decision_journal_formats\":[1]" : "") + "}");
            }
            if (route.EndsWith("/v1/enroll", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _enrollments);
                return Json("{}", HttpStatusCode.Created);
            }
            if (route.EndsWith("/v1/captures", StringComparison.Ordinal))
            {
                var first = Interlocked.Increment(ref _captureRequests) == 1;
                if (first && TimeoutFirstCapture) throw new TaskCanceledException("Fixture HTTP timeout.");
                if (first && BlockFirstCapture)
                    await Block(cancellationToken).ConfigureAwait(false);
                var bytes = await request.Content!.ReadAsByteArrayAsync().ConfigureAwait(false);
                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                using var reader = new StreamReader(archive.GetEntry("session.txt")!.Open());
                Uploaded.Enqueue(reader.ReadToEnd());
                return Json("{}", HttpStatusCode.Created);
            }
            throw new InvalidOperationException("Unexpected fixture request: " + route);
        }

        private async Task Block(CancellationToken token)
        {
            Blocked.Set();
            try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                Cancelled.Set();
                if (DelayCancellationCompletion) await ReleaseCancellation.Task.ConfigureAwait(false);
                throw;
            }
        }

        private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
            new HttpResponseMessage(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
