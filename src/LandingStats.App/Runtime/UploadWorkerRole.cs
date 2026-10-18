using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.TelemetryUpload;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class UploadWorkerRole : IWorkerRole
{
    private readonly Func<HttpMessageHandler?>? _handler;
    public UploadWorkerRole(Func<HttpMessageHandler?>? handler = null) { _handler = handler; }
    public async Task RunAsync(WorkerExecutionContext context)
    {
        var owner = new WorkerOwner(context); UploadJobMessage? job = null;
        Task<UploadResultMessage>? pending = null; long started = 0;
        CommandResultMessage Command(RuntimeEnvelope envelope)
        {
            if (!(envelope.Payload is UploadJobMessage request)) return new CommandResultMessage(CommandOutcome.Rejected, "unsupported_upload_command");
            if (job != null)
            {
                if (job.Attempt != request.Attempt) return new CommandResultMessage(CommandOutcome.Rejected, "upload_generation_already_assigned");
                if (job.Archive != request.Archive || job.Hash != request.Hash || job.Path != request.Path || job.IdentityRoot != request.IdentityRoot ||
                    job.PolicyRevision != request.PolicyRevision || job.Automatic != request.Automatic)
                    return new CommandResultMessage(CommandOutcome.Rejected, "upload_attempt_id_reused");
                return new CommandResultMessage(CommandOutcome.Completed, "upload_attempt_exists");
            }
            job = request; started = Stopwatch.GetTimestamp(); pending = Task.Run(() => SendAsync(request, context.CancellationToken));
            return new CommandResultMessage(CommandOutcome.Completed, "upload_attempt_accepted");
        }
        while (true)
        {
            var completed = pending?.IsCompleted == true;
            if (completed) { owner.Publish(await pending!.ConfigureAwait(false), Stopwatch.GetTimestamp()); pending = null; }
            owner.Progress(completed, pending == null ? DependencyWait.Idle : DependencyWait.Network, Command,
                pending == null ? 0 : started, pending == null ? Guid.Empty : job!.Attempt);
            if (owner.Stopping) return;
            await Task.Delay(50, context.CancellationToken).ConfigureAwait(false);
        }
    }
    internal async Task<UploadResultMessage> SendAsync(UploadJobMessage request, CancellationToken cancellationToken)
    {
        var receiverKey = ""; var supportsArchiveV2 = false;
        UploadResultMessage Result(UploadResultKind result, int status = 0, string reason = "") =>
            new UploadResultMessage(request.Archive, request.Attempt, request.Hash, request.PolicyRevision, result, status, reason, receiverKey, supportsArchiveV2);
        try
        {
            if (!Path.IsPathRooted(request.Path) || !Path.IsPathRooted(request.IdentityRoot)) throw new InvalidDataException("Upload paths must be absolute.");
            // Keep a read lease through verification and HTTP completion so a
            // file replacement cannot change the content signed for this claim.
            using var lease = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            EpisodeTransactionStore.Verify(request.Path, request.Hash);
            using var client = new TelemetryUploadClient(Path.GetDirectoryName(request.Path)!, _handler?.Invoke(), request.IdentityRoot, startQueueWorker: false);
            var outcome = await client.SendClaimAsync(request.Path, cancellationToken).ConfigureAwait(false);
            receiverKey = client.ObservedReceiverKey; supportsArchiveV2 = client.SupportsArchiveV2;
            cancellationToken.ThrowIfCancellationRequested();
            switch (outcome.Disposition)
            {
                case TelemetryUploadClient.UploadDisposition.Uploaded: return Result(UploadResultKind.Uploaded, outcome.StatusCode);
                case TelemetryUploadClient.UploadDisposition.Rejected: return Result(UploadResultKind.Rejected, outcome.StatusCode);
                case TelemetryUploadClient.UploadDisposition.EnrollmentRequired: return Result(UploadResultKind.EnrollmentRequired, outcome.StatusCode);
                default: return Result(UploadResultKind.Deferred, outcome.StatusCode);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException || error is HttpRequestException || error is System.Security.Cryptography.CryptographicException ||
            error is System.Runtime.Serialization.SerializationException || error is InvalidDataException || error is UnauthorizedAccessException)
        { return Result(UploadResultKind.Deferred, reason: error.GetType().Name); }
    }
}
