using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class CloudCredentialPolicy
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public long Revision { get; set; } = 1;
    [DataMember(Order = 3)] public bool Enabled { get; set; }
}
internal sealed class CloudSyncScope : IDisposable
{
    private readonly IDisposable _lease;
    public CloudSyncScope(IDisposable lease, CancellationToken cancellation, long revision) { _lease = lease; Cancellation = cancellation; Revision = revision; }
    public CancellationToken Cancellation { get; }
    public long Revision { get; }
    public void Dispose() => _lease.Dispose();
}

/// <summary>Only network/credential execution crosses the process boundary.
/// Sync logic and local writers remain in the parent, inside an epoch scope
/// whose drain is part of the logout barrier.</summary>
internal sealed class IsolatedGoogleDriveApi : IGoogleDriveApi, IDisposable
{
    private readonly object _gate = new object();
    private readonly SemaphoreSlim _configuration = new SemaphoreSlim(1, 1), _oneAttempt = new SemaphoreSlim(1, 1);
    private readonly string _root, _executable, _policyPath;
    private readonly GoogleOAuthTokenStore _tokens;
    private readonly Guid _session;
    private readonly RuntimeClockEpoch _clock;
    private readonly Func<long, string?>? _fixture;
    private readonly TimeSpan _deadline;
    private Epoch _epoch;
    private long _request, _generation, _revision;
    private bool _enabled, _disposed;
    private Task? _stop;
    public IsolatedGoogleDriveApi(string root, string executable, GoogleOAuthTokenStore tokens, Guid session, RuntimeClockEpoch clock,
        Func<long, string?>? fixture = null, TimeSpan? deadline = null)
    {
        _root = Path.GetFullPath(root); _executable = Path.GetFullPath(executable); _tokens = tokens; _session = session; _clock = clock; _fixture = fixture;
        _deadline = deadline ?? TimeSpan.FromSeconds(120);
        if (_deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        _policyPath = Path.Combine(_root, "credentials.json");
        var policy = File.Exists(_policyPath) ? DurableFile.ReadJson<CloudCredentialPolicy>(_policyPath, 4096) : new CloudCredentialPolicy { Enabled = tokens.HasRefreshToken() };
        if (policy.Version != 1 || policy.Revision < 1) throw new InvalidDataException("Invalid cloud credential policy.");
        _revision = policy.Revision; _enabled = policy.Enabled; _epoch = new Epoch(_revision);
    }
    internal event Action<ProcessWorkerHost>? WorkerStarted;
    internal event Action<Guid, bool>? Progress;
    internal bool Enabled { get { lock (_gate) return _enabled && !_disposed && _tokens.HasRefreshToken(); } }
    internal bool AuthorizationRequired { get { lock (_gate) return _enabled && !_disposed && !_tokens.HasRefreshToken(); } }
    internal CloudSyncScope BeginSync()
    {
        lock (_gate)
        {
            if (!_enabled || _disposed || !_tokens.HasRefreshToken()) throw new GoogleDriveNotSignedInException();
            return new CloudSyncScope(_epoch.Operations.TryEnter() ?? throw new GoogleDriveNotSignedInException(), _epoch.Cancellation.Token, _epoch.Revision);
        }
    }
    public Task SetEnabledAsync(bool enabled)
    {
        Epoch previous; long request;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IsolatedGoogleDriveApi));
            _enabled = false; previous = _epoch; request = ++_request;
            previous.Cancel();
        }
        return ConfigureAsync(enabled, previous, request);
    }
    private async Task ConfigureAsync(bool enabled, Epoch previous, long request)
    {
        var drained = previous.Operations.Stop();
        await _configuration.WaitAsync().ConfigureAwait(false);
        try
        {
            long revision; bool latest;
            lock (_gate) { latest = request == _request; revision = checked(_revision + 1); }
            var available = !enabled || _tokens.HasRefreshToken();
            try { if (latest) await Task.Run(() => DurableFile.WriteJson(_policyPath, new CloudCredentialPolicy { Revision = revision, Enabled = enabled && available })).ConfigureAwait(false); }
            finally { await drained.ConfigureAwait(false); if (previous.Worker != null) await previous.Worker.StopAsync().ConfigureAwait(false); }
            lock (_gate)
            {
                _revision = revision;
                if (request != _request) return;
                previous.Cancellation.Dispose(); _epoch = new Epoch(revision); _enabled = enabled && available && !_disposed;
            }
            if (!available) throw new GoogleDriveNotSignedInException();
        }
        finally { _configuration.Release(); }
    }
    internal async Task<string?> SignInAsync(CancellationToken cancellationToken)
    {
        Task transition; long intent;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(IsolatedGoogleDriveApi));
            transition = SetEnabledAsync(false); intent = _request;
        }
        await transition.ConfigureAwait(false);
        Task<Result> pending;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || intent != _request) throw new OperationCanceledException("Authorization was superseded before it started.");
            pending = Call(new CloudApiRequest { Operation = CloudApiOperation.SignIn }, cancellationToken, authorization: true);
        }
        var result = await pending.ConfigureAwait(false);
        return result.Response.Account.Length == 0 ? null : result.Response.Account;
    }
    internal async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await SetEnabledAsync(false).ConfigureAwait(false);
        try { await Call(new CloudApiRequest { Operation = CloudApiOperation.SignOut }, cancellationToken, authorization: true).ConfigureAwait(false); }
        finally { _tokens.Clear(); }
    }
    private Task<Result> Call(CloudApiRequest request, CancellationToken cancellationToken, byte[]? body = null, bool authorization = false)
    {
        if (body != null && body.LongLength > CloudApiRequest.MaximumBodyBytes) throw new InvalidDataException("Cloud upload exceeds its operation bound; local source retained.");
        var frozen = body == null ? null : (byte[])body.Clone();
        Epoch epoch; IDisposable accepted;
        lock (_gate)
        {
            if ((!_enabled && !authorization) || _disposed) throw new GoogleDriveNotSignedInException();
            epoch = _epoch; accepted = epoch.Operations.TryEnter() ?? throw new GoogleDriveNotSignedInException();
        }
        // Accept ownership synchronously; neither a queued scheduler nor a caller
        // closing its window can discard this operation's exit/cleanup barrier.
        return Task.Run(async () =>
        {
            using (accepted)
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, epoch.Cancellation.Token))
            {
                await _oneAttempt.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    cancellation.CancelAfter(authorization ? TimeSpan.FromMinutes(3) : _deadline);
                    return await RunAsync(epoch, request, frozen, cancellation.Token, authorization).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !epoch.Cancellation.IsCancellationRequested)
                { throw new TimeoutException("Cloud API operation exceeded its deadline and its worker has exited."); }
                finally { _oneAttempt.Release(); }
            }
        });
    }
    private async Task<Result> RunAsync(Epoch epoch, CloudApiRequest request, byte[]? body, CancellationToken cancellationToken, bool authorization)
    {
        var id = Guid.NewGuid(); var directory = Path.Combine(_root, "jobs", id.ToString("N"));
        using var scratch = RuntimeScratchLease.Create(Path.Combine(_root, "jobs"), id);
        var tokenPath = Path.Combine(directory, "token.json");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenHash = authorization ? _tokens.ExportAuthorizationSnapshot(tokenPath) : _tokens.ExportSnapshot(tokenPath);
            if (body != null)
            {
                if (body.LongLength > CloudApiRequest.MaximumBodyBytes) throw new InvalidDataException("Cloud upload exceeds its operation bound; local source retained.");
                var input = Path.Combine(directory, "input.bin"); DurableFile.Write(input, stream => stream.Write(body, 0, body.Length));
                request.BodyHash = EpisodeTransactionStore.Hash(input); request.BodyLength = body.Length;
            }
            request.Validate();
            var path = Path.Combine(directory, "request.json"); DurableFile.WriteJson(path, request); var hash = EpisodeTransactionStore.Hash(path);
            if (epoch.Worker == null)
            {
                var worker = new WorkerIdentity(_session, Guid.NewGuid(), Interlocked.Increment(ref _generation), ComponentRole.CloudSync);
                epoch.Worker = new CloudWorkerSession(_executable, worker, _clock, _fixture?.Invoke(worker.Generation), cancellationToken,
                    host => { try { WorkerStarted?.Invoke(host); } catch { } });
            }
            Changed(id, false);
            var result = await epoch.Worker.RunAsync(new CloudJobMessage(id, epoch.Revision, directory, hash, tokenHash), message =>
            {
                if (!(message.Payload is CloudResultMessage received) || received.Job != id || received.CredentialRevision != epoch.Revision)
                    throw new InvalidDataException("Cloud result belongs to another operation or credential epoch.");
                cancellationToken.ThrowIfCancellationRequested();
                if (received.Failure.Length != 0) throw new IOException("Cloud worker failed: " + received.Failure);
                var responsePath = Path.Combine(directory, "result.json");
                if (new FileInfo(responsePath).Length != received.Length) throw new InvalidDataException("Cloud result length changed.");
                EpisodeTransactionStore.Verify(responsePath, received.Hash);
                var response = DurableFile.ReadJson<CloudApiResponse>(responsePath, CloudResultMessage.MaximumMetadataBytes); response.Validate(request.Operation, hash);
                byte[]? output = null;
                if (response.BodyHash.Length != 0)
                {
                    var file = Path.Combine(directory, "output.bin");
                    if (new FileInfo(file).Length != response.BodyLength) throw new InvalidDataException("Cloud download length changed.");
                    EpisodeTransactionStore.Verify(file, response.BodyHash); output = File.ReadAllBytes(file);
                }
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((!_enabled && !authorization) || _disposed || !ReferenceEquals(epoch, _epoch))
                        throw new OperationCanceledException("Cloud credentials changed before its result committed.");
                    if ((response.CredentialHash.Length != 0 || response.Outcome == CloudApiOutcome.NotSignedIn || request.Operation == CloudApiOperation.SignOut) &&
                        !_tokens.ImportSnapshot(tokenPath, tokenHash, response.CredentialHash)) throw new GoogleDriveNotSignedInException();
                }
                if (response.Outcome == CloudApiOutcome.NotSignedIn) throw new GoogleDriveNotSignedInException();
                if (response.Outcome != CloudApiOutcome.Success) throw new CloudApiException("Cloud API operation failed: " + response.Failure, response.HttpStatus);
                return new Result(response, output);
            }, cancellationToken).ConfigureAwait(false);
            Changed(id, true); return result;
        }
        catch
        {
            if (epoch.Worker != null) { await epoch.Worker.StopAsync().ConfigureAwait(false); epoch.Worker = null; }
            throw;
        }
        finally
        {
            // Success is published after the worker releases this job lease;
            // failures stop the worker and drain callbacks before this cleanup.
            try { if (File.Exists(tokenPath)) File.Delete(tokenPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private void Changed(Guid id, bool completed) { try { Progress?.Invoke(id, completed); } catch { } }
    private static Dictionary<string, string> Properties(IReadOnlyDictionary<string, string> values) => values.ToDictionary(pair => pair.Key, pair => pair.Value);
    public async Task<string> GetAccountPermissionIdAsync(CancellationToken token) => (await Call(new CloudApiRequest { Operation = CloudApiOperation.Account }, token).ConfigureAwait(false)).Response.Account;
    public async Task<IReadOnlyList<GoogleDriveFile>> ListApplicationFilesAsync(CancellationToken token) => (await Call(new CloudApiRequest { Operation = CloudApiOperation.List }, token).ConfigureAwait(false)).Response.Files.Select(file => file.Restore()).ToArray();
    public async Task<GoogleDriveFile> CreateFolderAsync(string name, string? parentId, IReadOnlyDictionary<string, string> appProperties, CancellationToken token) =>
        (await Call(new CloudApiRequest { Operation = CloudApiOperation.CreateFolder, Name = name, Parent = parentId ?? "", Properties = Properties(appProperties) }, token).ConfigureAwait(false)).Response.Files.Single().Restore();
    public async Task<GoogleDriveFile> UploadFileAsync(string name, string mimeType, string parentId, IReadOnlyDictionary<string, string> appProperties, byte[] content, CancellationToken token) =>
        (await Call(new CloudApiRequest { Operation = CloudApiOperation.Upload, Name = name, Mime = mimeType, Parent = parentId, Properties = Properties(appProperties) }, token, content).ConfigureAwait(false)).Response.Files.Single().Restore();
    public async Task<GoogleDriveFile> UpdateFileAsync(string fileId, string name, string mimeType, IReadOnlyDictionary<string, string> appProperties, byte[] content, CancellationToken token) =>
        (await Call(new CloudApiRequest { Operation = CloudApiOperation.Update, File = fileId, Name = name, Mime = mimeType, Properties = Properties(appProperties) }, token, content).ConfigureAwait(false)).Response.Files.Single().Restore();
    public async Task<GoogleDriveFile> UpdateAppPropertiesAsync(string fileId, IReadOnlyDictionary<string, string> appProperties, CancellationToken token) =>
        (await Call(new CloudApiRequest { Operation = CloudApiOperation.Properties, File = fileId, Properties = Properties(appProperties) }, token).ConfigureAwait(false)).Response.Files.Single().Restore();
    public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken token) => (await Call(new CloudApiRequest { Operation = CloudApiOperation.Download, File = fileId }, token).ConfigureAwait(false)).Body!;
    public async Task TrashFileAsync(string fileId, CancellationToken token) => await Call(new CloudApiRequest { Operation = CloudApiOperation.Trash, File = fileId }, token).ConfigureAwait(false);
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stop != null) return _stop;
            _disposed = true; _enabled = false; _epoch.Cancel();
            return _stop = StopOwnedAsync(_epoch.Operations.Stop());
        }
    }
    private async Task StopOwnedAsync(Task drained)
    {
        // Application shutdown retires owners without changing the user's
        // persisted cloud preference. Explicit logout uses SetEnabledAsync.
        await drained.ConfigureAwait(false); await _configuration.WaitAsync().ConfigureAwait(false);
        try
        {
            Epoch final;
            lock (_gate) { final = _epoch; final.Cancel(); }
            await final.Operations.Stop().ConfigureAwait(false);
            if (final.Worker != null) await final.Worker.StopAsync().ConfigureAwait(false);
            final.Cancellation.Dispose();
        }
        finally { _configuration.Release(); }
    }
    public void Dispose() { _ = StopAsync(); }
    private sealed class Epoch
    {
        public Epoch(long revision) { Revision = revision; }
        public readonly long Revision;
        public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        public readonly OperationLifetime Operations = new OperationLifetime();
        public CloudWorkerSession? Worker;
        public void Cancel() { try { Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    }
    private sealed class Result
    { public Result(CloudApiResponse response, byte[]? body) { Response = response; Body = body; } public readonly CloudApiResponse Response; public readonly byte[]? Body; }
}
