using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Core;

namespace LandingStats.App.TelemetryUpload;

internal enum TelemetryPreparationState
{
    Ready,
    Unavailable,
}

internal sealed class TelemetryPreparationResult
{
    public TelemetryPreparationResult(TelemetryPreparationState state, string message)
    {
        State = state;
        Message = message;
    }

    public TelemetryPreparationState State { get; }
    public string Message { get; }
}

internal sealed class TelemetryUploadStatusEventArgs : EventArgs
{
    public TelemetryUploadStatusEventArgs(string message, bool isError = false)
    {
        Message = message;
        IsError = isError;
    }

    public string Message { get; }
    public bool IsError { get; }
}

internal sealed class TelemetryUploadClient : IDisposable
{
    private const long MaximumQueueBytes = 256L * 1024 * 1024;
    private const string EndpointEnvironmentVariable = "MSFS_LANDING_STATS_TELEMETRY_URL";
    private const string DefaultEndpoint = "https://msfsls.fallensky.us/";

    private readonly string _queuePath;
    private readonly Uri? _endpoint;
    private readonly TelemetryUploadIdentityStore _identityStore;
    private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), 128);
    private readonly HashSet<string> _queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueGate = new object();
    private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
    private readonly HttpClient _client;
    private readonly Task _worker;
    private int _disposed;

    public TelemetryUploadClient(string queuePath, HttpMessageHandler? handler = null, string? identityRoot = null)
    {
        _queuePath = queuePath;
        var configured = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = DefaultEndpoint;
        }
        if (Uri.TryCreate(configured, UriKind.Absolute, out var endpoint) &&
            string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _endpoint = endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
        }

        identityRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Telemetry Upload");
        _identityStore = new TelemetryUploadIdentityStore(identityRoot);
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        _client = handler == null ? new HttpClient() : new HttpClient(handler, true);
        _client.Timeout = TimeSpan.FromMinutes(3);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("(Windows NT 10.0; Win64; x64)");
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("MSFS-Landing-Stats-Telemetry/1");
        EnqueueExisting();
        _worker = Task.Run(UploadLoop);
    }

    public event EventHandler<TelemetryUploadStatusEventArgs>? StatusChanged;

    public bool ConsentAccepted => _identityStore.ConsentAccepted;

    public void AcceptConsent() => _identityStore.AcceptConsent();

    public async Task<TelemetryPreparationResult> PrepareAsync(CancellationToken cancellationToken)
    {
        if (_endpoint == null)
        {
            return new TelemetryPreparationResult(TelemetryPreparationState.Unavailable, "Telemetry receiver is not configured in this build");
        }
        try
        {
            var config = await GetConfigAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(config.RegistrationMode, "open", StringComparison.OrdinalIgnoreCase))
            {
                return new TelemetryPreparationResult(TelemetryPreparationState.Unavailable, "Telemetry receiver is not accepting automatic registration");
            }

            var identity = _identityStore.Identity();
            var sentAt = Timestamp();
            var nonce = Nonce();
            var canonical = CanonicalEnrollment(identity, sentAt, nonce);
            var payload = new EnrollmentPayload
            {
                InstallId = identity.InstallId,
                SentAtUtc = sentAt,
                Nonce = nonce,
                PublicModulus = identity.PublicModulus,
                PublicExponent = identity.PublicExponent,
                Signature = identity.Sign(canonical),
            };

            using var memory = new MemoryStream();
            new DataContractJsonSerializer(typeof(EnrollmentPayload)).WriteObject(memory, payload);
            using var content = new ByteArrayContent(memory.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await _client.PostAsync(new Uri(_endpoint, "v1/enroll"), content, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new TelemetryPreparationResult(TelemetryPreparationState.Unavailable, "Telemetry registration was rejected or this installation was revoked");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new TelemetryPreparationResult(TelemetryPreparationState.Unavailable, $"Telemetry enrollment failed ({(int)response.StatusCode})");
            }

            _identityStore.MarkEnrolled(_endpoint, true);
            EnqueueExisting();
            return new TelemetryPreparationResult(TelemetryPreparationState.Ready, "Secure telemetry upload is ready");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TelemetryPreparationResult(TelemetryPreparationState.Unavailable, "Telemetry setup was cancelled");
        }
        catch (Exception exception) when (exception is HttpRequestException || exception is IOException || exception is CryptographicException || exception is SerializationException)
        {
            return new TelemetryPreparationResult(TelemetryPreparationState.Unavailable, "Telemetry receiver is unavailable: " + exception.Message);
        }
    }

    public bool Enqueue(string path)
    {
        if (Volatile.Read(ref _disposed) != 0 || !File.Exists(path))
        {
            return false;
        }

        var capacityExceeded = QueueBytes() > MaximumQueueBytes;
        var acceptedForUpload = EnqueueCore(path);
        if (capacityExceeded)
        {
            RaiseStatus("Telemetry upload queue reached 256 MB; local DEBUG RAW continues and queued captures stay on disk", true);
            return false;
        }

        return acceptedForUpload;
    }

    private bool EnqueueCore(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_queueGate)
        {
            if (!_queued.Add(fullPath))
            {
                return true;
            }
            if (!_queue.TryAdd(fullPath))
            {
                _queued.Remove(fullPath);
                RaiseStatus("Telemetry queue is busy; capture remains on disk for the next start", true);
                return false;
            }
        }
        RaiseStatus($"Queued {Path.GetFileName(path)} for secure upload");
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cancellation.Cancel();
        _queue.CompleteAdding();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Any file still on disk will be retried by the next process.
        }
        _client.Dispose();
        _cancellation.Dispose();
        _queue.Dispose();
    }

    private async Task UploadLoop()
    {
        try
        {
            foreach (var path in _queue.GetConsumingEnumerable(_cancellation.Token))
            {
                var retry = false;
                try
                {
                    if (_endpoint == null || !_identityStore.IsEnrolled(_endpoint))
                    {
                        RaiseStatus("Telemetry capture is waiting for enrollment");
                        continue;
                    }
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var uploaded = await UploadAsync(path, _cancellation.Token).ConfigureAwait(false);
                    if (uploaded)
                    {
                        File.Delete(path);
                        RaiseStatus($"Uploaded {Path.GetFileName(path)} · local queue copy removed");
                    }
                    else if (_endpoint != null && _identityStore.IsEnrolled(_endpoint))
                    {
                        retry = true;
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException || exception is IOException || exception is CryptographicException)
                {
                    RaiseStatus("Telemetry upload deferred: " + exception.Message, true);
                    retry = true;
                }
                finally
                {
                    lock (_queueGate)
                    {
                        _queued.Remove(path);
                    }
                }

                if (retry && File.Exists(path) && !_cancellation.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cancellation.Token).ConfigureAwait(false);
                    Enqueue(path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task<bool> UploadAsync(string path, CancellationToken cancellationToken)
    {
        var identity = _identityStore.Identity();
        var size = new FileInfo(path).Length;
        var sha256 = HashFile(path);
        var sentAt = Timestamp();
        var nonce = Nonce();
        var appVersion = ApplicationVersion();
        var canonical = CanonicalCapture(identity.InstallId, sentAt, nonce, sha256, size, TelemetryCsv.SchemaVersion, appVersion);

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var content = new StreamContent(input, 65536);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Headers.ContentLength = size;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint!, "v1/captures")) { Content = content };
        request.Headers.Add("X-Install-Id", identity.InstallId);
        request.Headers.Add("X-Sent-At-Utc", sentAt);
        request.Headers.Add("X-Capture-Nonce", nonce);
        request.Headers.Add("X-Capture-SHA256", sha256);
        request.Headers.Add("X-Capture-Size", size.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Telemetry-Schema", TelemetryCsv.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-App-Version", appVersion);
        request.Headers.Add("X-Signature", identity.Sign(canonical));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            _identityStore.MarkEnrolled(_endpoint!, false);
            RaiseStatus("Telemetry installation must be enrolled again; queued capture was kept", true);
            return false;
        }
        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
        {
            RaiseStatus($"Telemetry receiver deferred the upload ({(int)response.StatusCode}); capture was kept", true);
            return false;
        }
        response.EnsureSuccessStatusCode();
        return true;
    }

    private async Task<ReceiverConfig> GetConfigAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(new Uri(_endpoint!, "v1/config"), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var config = (ReceiverConfig?)new DataContractJsonSerializer(typeof(ReceiverConfig)).ReadObject(input);
        if (config == null || config.Protocol != 1 || config.TelemetrySchema != TelemetryCsv.SchemaVersion)
        {
            throw new SerializationException("Telemetry receiver protocol is incompatible");
        }
        return config;
    }

    private void EnqueueExisting()
    {
        if (!Directory.Exists(_queuePath))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(_queuePath, "*_raw.zip", SearchOption.TopDirectoryOnly).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            EnqueueCore(path);
        }
    }

    private long QueueBytes()
    {
        if (!Directory.Exists(_queuePath))
        {
            return 0;
        }
        return Directory.EnumerateFiles(_queuePath, "*_raw.zip", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                try { return new FileInfo(path).Length; }
                catch (IOException) { return 0L; }
            })
            .Sum();
    }

    private void RaiseStatus(string message, bool isError = false)
    {
        try
        {
            StatusChanged?.Invoke(this, new TelemetryUploadStatusEventArgs(message, isError));
        }
        catch
        {
            // A presentation callback cannot change queue ownership.
        }
    }

    private static byte[] CanonicalEnrollment(TelemetryUploadIdentity identity, string sentAt, string nonce) =>
        Encoding.ASCII.GetBytes(
            "MSFS-LANDING-STATS-ENROLLMENT-V1\n" +
            $"install_id={identity.InstallId}\n" +
            $"sent_at_utc={sentAt}\n" +
            $"nonce={nonce}\n" +
            $"public_modulus={identity.PublicModulus}\n" +
            $"public_exponent={identity.PublicExponent}\n");

    private static byte[] CanonicalCapture(string installId, string sentAt, string nonce, string sha256, long size, int schema, string appVersion) =>
        Encoding.ASCII.GetBytes(
            "MSFS-LANDING-STATS-TELEMETRY-V1\n" +
            $"install_id={installId}\n" +
            $"sent_at_utc={sentAt}\n" +
            $"nonce={nonce}\n" +
            $"sha256={sha256}\n" +
            $"size={size.ToString(CultureInfo.InvariantCulture)}\n" +
            $"schema={schema.ToString(CultureInfo.InvariantCulture)}\n" +
            $"app_version={appVersion}\n");

    private static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Nonce()
    {
        var bytes = new byte[16];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(bytes);
        }
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return string.Concat(sha256.ComputeHash(input).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static string ApplicationVersion()
    {
        var version = typeof(TelemetryUploadClient).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    [DataContract]
    private sealed class ReceiverConfig
    {
        [DataMember(Name = "protocol")]
        public int Protocol { get; set; }
        [DataMember(Name = "registration_mode")]
        public string RegistrationMode { get; set; } = string.Empty;
        [DataMember(Name = "telemetry_schema")]
        public int TelemetrySchema { get; set; }
    }

    [DataContract]
    private sealed class EnrollmentPayload
    {
        [DataMember(Name = "install_id", Order = 1)] public string InstallId { get; set; } = string.Empty;
        [DataMember(Name = "sent_at_utc", Order = 2)] public string SentAtUtc { get; set; } = string.Empty;
        [DataMember(Name = "nonce", Order = 3)] public string Nonce { get; set; } = string.Empty;
        [DataMember(Name = "public_modulus", Order = 4)] public string PublicModulus { get; set; } = string.Empty;
        [DataMember(Name = "public_exponent", Order = 5)] public string PublicExponent { get; set; } = string.Empty;
        [DataMember(Name = "signature", Order = 6)] public string Signature { get; set; } = string.Empty;
    }
}
