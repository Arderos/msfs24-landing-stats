using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>Executes one Drive API operation against a private credential copy.
/// Local history, settings and sync checkpoints are never writable by this role.</summary>
internal sealed class CloudWorkerRole : IWorkerRole
{
    private Google.Apis.Auth.OAuth2.Responses.TokenResponse? _cachedToken;
    private readonly Func<GoogleOAuthTokenStore, IGoogleDriveApi>? _fixture;
    private readonly Func<GoogleOAuthTokenStore, CancellationToken, Task<string?>>? _authorize;
    public CloudWorkerRole(Func<GoogleOAuthTokenStore, IGoogleDriveApi>? fixture = null,
        Func<GoogleOAuthTokenStore, CancellationToken, Task<string?>>? authorize = null) { _fixture = fixture; _authorize = authorize; }
    public async Task RunAsync(WorkerExecutionContext context)
    {
        var owner = new WorkerOwner(context); CloudJobMessage? job = null; Task<CloudResultMessage>? pending = null; long started = 0;
        CommandResultMessage Command(RuntimeEnvelope message)
        {
            if (!(message.Payload is CloudJobMessage request)) return new CommandResultMessage(CommandOutcome.Rejected, "unsupported_cloud_command");
            if (job != null && (pending != null || job.Job == request.Job))
                return new CommandResultMessage(job.Job == request.Job && job.CredentialRevision == request.CredentialRevision && job.Directory == request.Directory &&
                    job.RequestHash == request.RequestHash && job.CredentialHash == request.CredentialHash ? CommandOutcome.Completed : CommandOutcome.Rejected, "cloud_generation_already_assigned");
            job = request; started = Stopwatch.GetTimestamp(); pending = Task.Run(() => ExecuteAsync(request, context.CancellationToken));
            return new CommandResultMessage(CommandOutcome.Completed, "cloud_operation_accepted");
        }
        while (true)
        {
            var completed = pending?.IsCompleted == true;
            if (completed) { owner.Publish(await pending!.ConfigureAwait(false), Stopwatch.GetTimestamp()); pending = null; }
            owner.Progress(completed, pending == null ? DependencyWait.Idle : DependencyWait.Network, Command,
                pending == null ? 0 : started, pending == null ? Guid.Empty : job!.Job);
            if (owner.Stopping) return;
            await Task.Delay(50, context.CancellationToken).ConfigureAwait(false);
        }
    }
    private async Task<CloudResultMessage> ExecuteAsync(CloudJobMessage job, CancellationToken cancellationToken)
    {
        try
        {
            if (!Path.IsPathRooted(job.Directory)) throw new InvalidDataException("Cloud job directory is not absolute.");
            using var scratch = RuntimeScratchLease.EnterWorker(job.Directory);
            var input = Path.Combine(job.Directory, "request.json"); var credentials = Path.Combine(job.Directory, "token.json");
            EpisodeTransactionStore.Verify(input, job.RequestHash);
            var request = DurableFile.ReadJson<CloudApiRequest>(input, 512 * 1024); request.Validate();
            if (job.CredentialHash == "")
            {
                if (request.Operation != CloudApiOperation.SignIn && request.Operation != CloudApiOperation.SignOut || File.Exists(credentials)) throw new InvalidDataException("Cloud credentials are missing or changed.");
            }
            else EpisodeTransactionStore.Verify(credentials, job.CredentialHash);
            var store = new GoogleOAuthTokenStore(credentials);
            using var oauth = new GoogleOAuthClient(store, interactive: request.Operation == CloudApiOperation.SignIn, cachedToken: _cachedToken);
            var drive = request.Operation == CloudApiOperation.SignIn || request.Operation == CloudApiOperation.SignOut ? null : _fixture?.Invoke(store) ?? new GoogleDriveApiClient(oauth);
            CloudApiResponse response;
            try
            {
                if (request.Operation == CloudApiOperation.SignIn && _fixture != null && _authorize == null)
                    throw new InvalidOperationException("This fixture has no authorization endpoint.");
                if (request.Operation == CloudApiOperation.SignOut)
                {
                    if (_fixture == null) await oauth.RevokeAsync(cancellationToken).ConfigureAwait(false);
                    else store.Clear();
                    response = new CloudApiResponse();
                }
                else response = request.Operation == CloudApiOperation.SignIn
                    ? new CloudApiResponse { Account = (await (_authorize == null ? oauth.SignInAsync(cancellationToken) : _authorize(store, cancellationToken)).ConfigureAwait(false)) ?? "" }
                    : await InvokeWithRetryAsync(drive!, request, job.Directory, cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleDriveNotSignedInException) { response = new CloudApiResponse { Outcome = CloudApiOutcome.NotSignedIn }; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                var status = error is Google.GoogleApiException apiError ? (int)apiError.HttpStatusCode : error is OperationCanceledException ? 408 : 0;
                response = new CloudApiResponse { Outcome = CloudApiOutcome.Failed, HttpStatus = status,
                    Failure = error is GoogleDriveConfigurationException ? error.Message : error.GetType().Name + (status == 0 ? "" : " (HTTP " + status + ")") };
            }
            finally { _cachedToken = oauth.CurrentToken; (drive as IDisposable)?.Dispose(); }
            cancellationToken.ThrowIfCancellationRequested();
            response.RequestHash = job.RequestHash; response.Operation = request.Operation;
            response.CredentialHash = File.Exists(credentials) ? EpisodeTransactionStore.Hash(credentials) : "";
            response.Validate(request.Operation, job.RequestHash);
            var output = Path.Combine(job.Directory, "result.json"); DurableFile.WriteJson(output, response);
            return new CloudResultMessage(job.Job, job.CredentialRevision, EpisodeTransactionStore.Hash(output), new FileInfo(output).Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return new CloudResultMessage(job.Job, job.CredentialRevision, "", 0, error.GetType().Name); }
    }
    private static async Task<CloudApiResponse> InvokeWithRetryAsync(IGoogleDriveApi drive, CloudApiRequest request, string directory, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await InvokeAsync(drive, request, directory, token).ConfigureAwait(false); }
            catch (Google.GoogleApiException error) when (attempt < 3 && ((int)error.HttpStatusCode == 429 || (int)error.HttpStatusCode >= 500) &&
                request.Operation != CloudApiOperation.Upload && request.Operation != CloudApiOperation.CreateFolder)
            { await Task.Delay(250 * (1 << (attempt * 2)), token).ConfigureAwait(false); }
        }
    }
    private static async Task<CloudApiResponse> InvokeAsync(IGoogleDriveApi drive, CloudApiRequest request, string directory, CancellationToken token)
    {
        var result = new CloudApiResponse(); byte[] Body()
        {
            var path = Path.Combine(directory, "input.bin");
            if (new FileInfo(path).Length != request.BodyLength) throw new InvalidDataException("Cloud request content length differs.");
            EpisodeTransactionStore.Verify(path, request.BodyHash); return File.ReadAllBytes(path);
        }
        GoogleDriveFile? file = null;
        switch (request.Operation)
        {
            case CloudApiOperation.Account: result.Account = await drive.GetAccountPermissionIdAsync(token).ConfigureAwait(false); break;
            case CloudApiOperation.List:
                var files = await drive.ListApplicationFilesAsync(token).ConfigureAwait(false);
                if (files.Count > 65536) throw new InvalidDataException("Cloud listing exceeds its bound.");
                result.Files = files.GroupBy(file => file.Id, StringComparer.Ordinal).Select(group => CloudFileData.From(group.Last())).ToArray(); break;
            case CloudApiOperation.CreateFolder:
                file = await drive.CreateFolderAsync(request.Name, request.Parent.Length == 0 ? null : request.Parent, request.Properties, token).ConfigureAwait(false); break;
            case CloudApiOperation.Upload:
                file = await drive.UploadFileAsync(request.Name, request.Mime, request.Parent, request.Properties, Body(), token).ConfigureAwait(false); break;
            case CloudApiOperation.Update:
                file = await drive.UpdateFileAsync(request.File, request.Name, request.Mime, request.Properties, Body(), token).ConfigureAwait(false); break;
            case CloudApiOperation.Properties:
                file = await drive.UpdateAppPropertiesAsync(request.File, request.Properties, token).ConfigureAwait(false); break;
            case CloudApiOperation.Download:
                var body = await drive.DownloadFileAsync(request.File, token).ConfigureAwait(false);
                if (body.LongLength > CloudApiRequest.MaximumBodyBytes) throw new InvalidDataException("Cloud content exceeds its bound.");
                var output = Path.Combine(directory, "output.bin"); DurableFile.Write(output, stream => stream.Write(body, 0, body.Length));
                result.BodyHash = EpisodeTransactionStore.Hash(output); result.BodyLength = body.Length; break;
            case CloudApiOperation.Trash: await drive.TrashFileAsync(request.File, token).ConfigureAwait(false); break;
            default: throw new InvalidDataException("Unknown cloud operation.");
        }
        if (file != null) result.Files = new[] { CloudFileData.From(file) };
        return result;
    }
}
