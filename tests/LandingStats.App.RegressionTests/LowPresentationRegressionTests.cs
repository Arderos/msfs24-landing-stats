using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.TelemetryUpload;

namespace LandingStats.App.RegressionTests;

internal static class LowPresentationRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "low-presentation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        var oldMode = Application.Current.ShutdownMode; Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var oldLanguage = LocalizationManager.EffectiveLanguage;
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null; var done = false;
        try
        {
            // The window is never loaded, so no real settings, history or runtime starts.
            window = new MainWindow();
            Set(window, "_repository", new LandingRepository(Path.Combine(root, "history")));
            Set(window, "_historyLoading", false); Set(window, "_startupReady", true);
            var apply = (Func<RuntimePresentationSnapshot, CancellationToken, Task>)Delegate.CreateDelegate(
                typeof(Func<RuntimePresentationSnapshot, CancellationToken, Task>), window,
                typeof(MainWindow).GetMethod("ApplyRuntimeSnapshotAsync", Private)!);
            foreach (var language in new[] { "en", "ru" })
            {
                LocalizationManager.Apply(language);
                RetainedDeliveryConditions(window, apply);
                DeliveryReasons(window, apply);
                RetiredComponents(window, apply);
            }
            Await(Task.Run(() => IdentityCreation(Path.Combine(root, "created"))));
            Await(Task.Run(() => CanonicalConsentWrites(Path.Combine(root, "consent"))));
            Await(ReceiverFormatFailures(window, apply, Path.Combine(root, "receiver")));
            done = true;
        }
        finally
        {
            if (window != null)
            {
                Set(window, "_isClosed", true);
                typeof(MainWindow).GetMethod("StopHistoryOperations", Private)!.Invoke(window, Array.Empty<object>());
                Await((Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!);
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window,
                    typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
                window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
                    typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
                window.Close();
            }
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            Application.Current.ShutdownMode = oldMode;
            LocalizationManager.Apply(oldLanguage);
            SynchronizationContext.SetSynchronizationContext(oldContext);
            if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }

    private static void RetainedDeliveryConditions(MainWindow window, Func<RuntimePresentationSnapshot, CancellationToken, Task> apply)
    {
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        projection.IdentityAvailability(true);
        projection.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = OutboxState.Pending, LastFailure = "CryptographicException" });
        Apply(apply, projection);
        CheckIdentityControl(window, true);
        var delivered = Guid.NewGuid();
        projection.Upload(new OutboxEntry { Id = delivered, State = OutboxState.Delivered });
        projection.Cloud(new RuntimeCloudNotice(1, DateTime.MinValue, false));
        Apply(apply, projection);
        Check(projection.Snapshot.UploadIdentityUnavailable && Get<string>(window, "_telemetryDeliveryKey") == "Delivery.IdentityUnavailable",
            "An unrelated delivered report cleared the retained identity failure.");
        CheckIdentityControl(window, true);
        Check(!Get<bool>(window, "_uploadStatusIsError") && projection.Snapshot.Failure == null,
            "Optional identity availability became a recording failure.");

        projection.DeliveryQuarantine(true);
        projection.IdentityAvailability(false);
        Apply(apply, projection);
        CheckIdentityControl(window, false);
        Check(projection.Snapshot.DeliveryQuarantined && Get<string>(window, "_telemetryDeliveryKey") == "Delivery.Damaged",
            "Clearing identity failure also hid the independent damaged queue entry.");
        delivered = Guid.NewGuid();
        projection.Upload(new OutboxEntry { Id = delivered, State = OutboxState.Delivered });
        Apply(apply, projection);
        Check(Get<TextBlock>(window, "SettingsTelemetryStatusText").Text == LocalizationManager.Text("Delivery.Damaged") &&
            !Get<bool>(window, "_uploadStatusIsError"), "A delivered neighbor erased or misclassified the damaged queue status.");

        projection.DeliveryQuarantine(false);
        Apply(apply, projection);
        Check(!projection.Snapshot.DeliveryQuarantined && Get<string>(window, "_telemetryDeliveryKey") == "Delivery.Delivered" &&
            Get<TextBlock>(window, "SettingsTelemetryStatusText").Text == LocalizationManager.Text("Delivery.Delivered"),
            "Clearing the aggregate left a stale damaged-delivery warning.");
        Check(Get<string>(window, "_uploadStatusKey") == "Telemetry.UploadedFormat" &&
            Get<TextBlock>(window, "UploadStatusText").Text == LocalizationManager.Format("Telemetry.UploadedFormat", delivered.ToString("N")),
            "The successful-delivery footer lost its archive ID.");
        projection.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = OutboxState.Pending, LastFailure = "CryptographicException" });
        projection.IdentityAvailability(true); Apply(apply, projection);
        projection.IdentityAvailability(false); Apply(apply, projection);
        CheckIdentityControl(window, false);
        Check(Get<string>(window, "_telemetryDeliveryKey") != "Delivery.IdentityUnavailable",
            "Removing the last key-failed queue entry left a stale unavailable-identity message.");
    }

    private static async Task ReceiverFormatFailures(MainWindow window, Func<RuntimePresentationSnapshot, CancellationToken, Task> apply, string root)
    {
        var oldEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        try
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://receiver.invalid/");
            foreach (var response in new[] { Encoding.UTF8.GetBytes("{\"protocol\":999,\"telemetry_schema\":8}"),
                Encoding.UTF8.GetBytes("<html>Proxy error</html>"), new byte[] { 0, 255, 123 } })
            {
                using var client = new TelemetryUploadClient(root, new MalformedReceiver(response), Path.Combine(root, "identity"), startQueueWorker: false);
                var prepared = await client.PrepareAsync(CancellationToken.None);
                Check(prepared.State == TelemetryPreparationState.Unavailable && prepared.FailureReason == "SerializationException",
                    "Malformed receiver response did not become a retryable protocol failure.");
                foreach (var language in new[] { "en", "ru" })
                {
                    LocalizationManager.Apply(language);
                    var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
                    projection.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = OutboxState.Pending, LastFailure = prepared.FailureReason });
                    Apply(apply, projection);
                    Check(Get<string>(window, "_telemetryDeliveryKey") == "Delivery.DataPending" && !Get<bool>(window, "_uploadStatusIsError"),
                        "Actual receiver format failure was rendered as file access or a processing failure.");
                }
            }
            Check(!File.Exists(Path.Combine(root, "identity", "identity.json")), "Malformed config unexpectedly created or replaced an identity.");
        }
        finally { Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", oldEndpoint); }
    }
    private sealed class MalformedReceiver : HttpMessageHandler
    {
        private readonly byte[] _body;
        public MalformedReceiver(byte[] body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
    }

    private static void DeliveryReasons(MainWindow window, Func<RuntimePresentationSnapshot, CancellationToken, Task> apply)
    {
        foreach (var reason in new[] { "SerializationException", "InvalidDataException", "UnauthorizedAccessException" })
        {
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            var key = reason == "UnauthorizedAccessException" ? "Delivery.StoragePending" : "Delivery.DataPending";
            projection.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = OutboxState.Pending, LastFailure = reason });
            Apply(apply, projection);
            Check(Get<string>(window, "_telemetryDeliveryKey") == key &&
                Get<TextBlock>(window, "SettingsTelemetryStatusText").Text == LocalizationManager.Text(key) &&
                Get<TextBlock>(window, "UploadStatusText").Text == LocalizationManager.Text(key),
                "Delivery reason " + reason + " gave misleading local-file or free-space advice.");
            Check(!Get<bool>(window, "_uploadStatusIsError") && !Get<TextBlock>(window, "UploadStatusText").Text.Contains(reason),
                "A recoverable delivery reason rendered a raw exception or recording failure.");
        }
        foreach (var state in new[] { OutboxState.QuarantinePending, OutboxState.Quarantined })
        {
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            projection.Upload(new OutboxEntry { Id = Guid.NewGuid(), State = state });
            Apply(apply, projection);
            Check(Get<string>(window, "_telemetryDeliveryKey") == "Delivery.Rejected",
                "A terminal server rejection was presented as recoverable local data damage.");
            projection.DeliveryQuarantine(true);
            Apply(apply, projection);
            Check(Get<string>(window, "_telemetryDeliveryKey") == "Delivery.Damaged" &&
                !Get<bool>(window, "_uploadStatusIsError"), "Local quarantine was presented as an explicit server rejection.");
        }
    }

    private static void RetiredComponents(MainWindow window, Func<RuntimePresentationSnapshot, CancellationToken, Task> apply)
    {
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        var report = Guid.NewGuid(); var simulator = Guid.NewGuid();
        projection.Failure("storage.reports", report, "UnauthorizedAccessException");
        var retainedDelivery = projection.Snapshot.DeliveryFailure;
        projection.Failure("simulator.source", simulator, "retained source failure");
        var retainedSource = projection.Snapshot.Failure;
        projection.Failure("integration.api", Guid.NewGuid(), "first retired failure");
        projection.Failure("integration.api", Guid.NewGuid(), "second retired failure");
        Apply(apply, projection);
        Check(Get<TextBlock>(window, "UploadStatusText").Text.Contains("integration.api"), "Component fixture did not display its current failure.");
        projection.Retired("integration.api");
        Apply(apply, projection);
        Check(ReferenceEquals(projection.Snapshot.Failure, retainedSource) && ReferenceEquals(projection.Snapshot.DeliveryFailure, retainedDelivery) &&
            Get<TextBlock>(window, "UploadStatusText").Text.Contains("simulator.source"),
            "Retiring one component erased another component's failure.");
        projection.Recovered("simulator.source", simulator);
        Apply(apply, projection);
        Check(projection.Snapshot.Failure == null && ReferenceEquals(projection.Snapshot.DeliveryFailure, retainedDelivery) &&
            !Get<bool>(window, "_uploadStatusIsError") && Get<string>(window, "_telemetryDeliveryKey") == "Delivery.StoragePending",
            "Retirement left an older failure for the same component, or erased the independent report failure.");
        projection.Retired("storage.reports");
        Apply(apply, projection);
        Check(projection.Snapshot.DeliveryFailure == null && Get<string>(window, "_telemetryDeliveryKey") == "Delivery.Idle",
            "Retired report ownership left a stale delivery failure.");
    }

    private static void IdentityCreation(string root)
    {
        var store = new TelemetryUploadIdentityStore(root);
        var result = store.RecoverUnavailableIdentity(true);
        Check(result.Created && !result.Replaced && result.BackupPath == null && !store.ConsentAccepted,
            "First-use recovery did not distinguish a newly created identity from an already available key.");
        var identity = store.Identity();
        VerifySignature(identity);
        var path = Path.Combine(root, "identity.json"); var original = File.ReadAllBytes(path);
        var retained = store.RecoverUnavailableIdentity(true);
        Check(!retained.Created && !retained.Replaced && retained.BackupPath == null &&
            original.SequenceEqual(File.ReadAllBytes(path)) && store.Identity().InstallId == identity.InstallId,
            "A healthy identity was reported as created or rewritten by repeated recovery.");
        Check(ReadLegacyIdentity(path).ConsentVersion == 0, "First-use identity granted legacy upload consent.");
    }

    private static void CanonicalConsentWrites(string root)
    {
        var seedRoot = Path.Combine(root, "seed"); var seed = new TelemetryUploadIdentityStore(seedRoot);
        var identity = seed.Identity(); var endpoint = new Uri("https://fixture.invalid"); seed.MarkEnrolled(endpoint, true);
        var seedPath = Path.Combine(seedRoot, "identity.json"); var original = ReadLegacyIdentity(seedPath);
        var seedJson = File.ReadAllText(seedPath);
        var variants = new (string? Json, int Consent)[]
        {
            (null, 0), ("null", 0), ("\"invalid\"", 0), ("true", 0), ("1.5", 0),
            ("{\"version\":1}", 0), ("[1]", 0), ("-1", 0), ("0", 0), ("1", 1), ("3", 3),
        };
        for (var index = 0; index < variants.Length; index++)
        {
            var area = Path.Combine(root, index.ToString(System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(area);
            var path = Path.Combine(area, "identity.json"); var variant = variants[index];
            var json = variant.Json == null ? Regex.Replace(seedJson, ",\"consent_version\":0", "") :
                Regex.Replace(seedJson, "\"consent_version\":0", "\"consent_version\":" + variant.Json);
            File.WriteAllText(path, json);
            var store = new TelemetryUploadIdentityStore(area);
            Check(store.ConsentAccepted == (variant.Consent >= 1) && store.Identity().InstallId == identity.InstallId && store.IsEnrolled(endpoint),
                "Reading malformed consent changed signing identity, enrollment or denied-consent semantics.");
            // Exercise an ordinary persisted update. This must canonicalize the
            // tolerated input for older clients whose consent field is an int.
            store.MarkEnrolled(endpoint, true);
            var compatible = ReadLegacyIdentity(path);
            Check(compatible.ConsentVersion == variant.Consent && compatible.InstallId == original.InstallId &&
                compatible.ProtectedPrivateKey == original.ProtectedPrivateKey && compatible.EnrolledEndpoint == original.EnrolledEndpoint,
                "Enrollment write did not canonicalize consent without changing the existing key and enrollment.");
            var reloaded = new TelemetryUploadIdentityStore(area);
            Check(reloaded.Identity().InstallId == identity.InstallId && reloaded.ConsentAccepted == (variant.Consent >= 1) && reloaded.IsEnrolled(endpoint),
                "Canonical consent did not survive a new identity-store instance.");
            VerifySignature(reloaded.Identity());
            Check(!Directory.EnumerateFiles(area).Any(file => Path.GetFileName(file).StartsWith("identity.json.", StringComparison.Ordinal)),
                "Consent normalization unnecessarily replaced or quarantined the healthy identity.");
        }
    }

    private static void VerifySignature(TelemetryUploadIdentity identity)
    {
        var message = Encoding.UTF8.GetBytes("identity compatibility fixture");
        using var verifier = new RSACryptoServiceProvider { PersistKeyInCsp = false };
        verifier.ImportParameters(new RSAParameters
        { Modulus = Convert.FromBase64String(identity.PublicModulus), Exponent = Convert.FromBase64String(identity.PublicExponent) });
        Check(verifier.VerifyData(message, CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(identity.Sign(message))),
            "DPAPI identity could not produce a signature verified by its advertised public key.");
    }

    private static LegacyIdentityFile ReadLegacyIdentity(string path)
    {
        using var input = File.OpenRead(path);
        return (LegacyIdentityFile)new DataContractJsonSerializer(typeof(LegacyIdentityFile)).ReadObject(input)!;
    }

    [DataContract]
    private sealed class LegacyIdentityFile
    {
        [DataMember(Name = "install_id")] public string InstallId { get; set; } = "";
        [DataMember(Name = "protected_private_key")] public string ProtectedPrivateKey { get; set; } = "";
        [DataMember(Name = "consent_version")] public int ConsentVersion { get; set; }
        [DataMember(Name = "enrolled_endpoint")] public string? EnrolledEndpoint { get; set; }
    }

    private static void CheckIdentityControl(MainWindow window, bool unavailable)
    {
        var button = Get<Button>(window, "RecoverTelemetryIdentityButton");
        Check(button.Visibility == (unavailable ? Visibility.Visible : Visibility.Collapsed) && (unavailable || !button.IsEnabled),
            "The identity recovery control did not reflect retained queue-wide identity availability.");
    }

    private static void Apply(Func<RuntimePresentationSnapshot, CancellationToken, Task> apply, RuntimePresentation projection)
    { var snapshot = projection.Snapshot; Await(Task.Run(() => apply(snapshot, CancellationToken.None))); }

    private static void Await(Task task)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, __) => { if (task.IsCompleted || watch.Elapsed > TimeSpan.FromSeconds(60)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(task.IsCompleted, "LOW presentation fixture timed out."); task.GetAwaiter().GetResult();
    }

    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
