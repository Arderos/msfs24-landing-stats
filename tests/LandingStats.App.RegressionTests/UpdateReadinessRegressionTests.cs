using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Updates;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class UpdateReadinessRegressionTests
{
    public static void PreparedUpdateWaitsForReadiness() => ResumeWithoutDownloadingAgain().GetAwaiter().GetResult();
    public static void WaitingUpdateCancelsAndCleansUp() => CancelWhileWaiting().GetAwaiter().GetResult();
    public static void WaitingUpdateRejectsChangedFile() => RejectChangedPreparedFile().GetAwaiter().GetResult();
    public static void UnsafeHandoffResumesPreparedUpdate() => RecheckSafetyAtHandoff().GetAwaiter().GetResult();
    public static void NaturalReadinessResumesWithoutPresentation() => Task.Run(ResumeFromRecordingOwner).GetAwaiter().GetResult();

    private static async Task ResumeFromRecordingOwner()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(),
            new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        var callbacks = 0; var initialPresentation = Signal(); var waiting = Signal();
        var presentation = new RuntimePresentation((_, _) =>
        { Interlocked.Increment(ref callbacks); initialPresentation.TrySetResult(true); return Task.CompletedTask; });
        var recording = new RecordingRuntimeOwner(identity, (_, effects) =>
        { presentation.Recording(identity, effects); return Task.CompletedTask; }, (_, _) => Task.CompletedTask,
            error => throw new InvalidOperationException("Unexpected integrated recording failure.", error));
        using var owner = new ManagedComponentGeneration(1, recording.RunAsync);
        using var ui = new ManagedComponentGeneration(1, presentation.RunAsync);
        Task<ReleaseUpdateResult>? update = null;
        var ownerStarted = false; var uiStarted = false;
        var clock = Stopwatch.StartNew(); double preparedMs = 0, handoffMs = 0;
        var handoffs = 0; var waitingNotices = 0;
        try
        {
            await owner.StartAsync(CancellationToken.None).ConfigureAwait(false); ownerStarted = true;
            await ui.StartAsync(CancellationToken.None).ConfigureAwait(false); uiStarted = true;
            await Deadline(initialPresentation.Task).ConfigureAwait(false);
            update = fixture.Updater.CheckAndInstallAsync(new Version(0, 9, 5), cancellation.Token,
                token => { waiting.TrySetResult(true); return recording.WaitForSafeRestartAsync(token); }, () => recording.SafeToRestart,
                phase =>
                {
                    if (phase == "update_prepared") preparedMs = clock.Elapsed.TotalMilliseconds;
                    if (phase == "update_wait_safe") Interlocked.Increment(ref waitingNotices);
                    if (phase != "update_handoff") return;
                    handoffMs = clock.Elapsed.TotalMilliseconds; Interlocked.Increment(ref handoffs);
                    Require(recording.SafeToRestart, "Real recording owner did not authorize the handoff.");
                    cancellation.Cancel(); // Stop at the verified launch boundary; never execute a real updater.
                });
            await Deadline(waiting.Task).ConfigureAwait(false); fixture.CapturePreparedDirectory();
            Require(!recording.SafeToRestart && !update.IsCompleted && fixture.Handler.Requests == 3 && File.Exists(fixture.PreparedFile),
                "Integrated updater did not retain its downloaded file while recording startup was pending.");
            // Start the real five-second stability policy only after preparation.
            // Restoring idle state emits no new presentation revision or callback.
            await Deadline(recording.RestoreAsync(null, null)).ConfigureAwait(false);
            Require(await Task.WhenAny(update, Task.Delay(TimeSpan.FromSeconds(12))).ConfigureAwait(false) == update,
                "Natural recording readiness did not resume its prepared update without presentation changes.");
            var result = await update.ConfigureAwait(false);
            Require(result.MessageKey == "Update.Cancelled" && handoffs == 1 && waitingNotices == 1 && fixture.Handler.Requests == 3,
                "Natural readiness duplicated downloads/handoffs or did not reach the launch boundary.");
            Require(presentation.Snapshot.Revision == 0 && Volatile.Read(ref callbacks) == 1,
                "The owner-to-updater path depended on an incidental presentation refresh.");
            Require(!Directory.Exists(fixture.PreparedDirectory), "Integrated canceled handoff retained its prepared directory.");
            Console.WriteLine("    Natural updater readiness: prepared={0:F0} ms, handoff={1:F0} ms, HTTP requests={2}, presentation callbacks={3}",
                preparedMs, handoffMs, fixture.Handler.Requests, Volatile.Read(ref callbacks));
        }
        finally
        {
            cancellation.Cancel();
            try { if (update != null) await Deadline(update).ConfigureAwait(false); }
            finally
            {
                try { if (ownerStarted) await Deadline(recording.CloseIntakeAsync()).ConfigureAwait(false); }
                finally
                {
                    await Task.WhenAll(ownerStarted ? owner.StopAsync(TimeSpan.FromSeconds(2)) : Task.CompletedTask,
                        uiStarted ? ui.StopAsync(TimeSpan.FromSeconds(2)) : Task.CompletedTask).ConfigureAwait(false);
                }
            }
        }
    }
    private static async Task ResumeWithoutDownloadingAgain()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var firstEntered = Signal(); var secondEntered = Signal(); var firstReady = Signal(); var secondReady = Signal();
        var allowed = 0; var calls = 0; var waitingNotices = 0; var handoffs = 0;
        var update = fixture.Updater.CheckAndInstallAsync(new Version(0, 9, 5), cancellation.Token,
            token =>
            {
                if (Interlocked.Increment(ref calls) == 1) { firstEntered.TrySetResult(true); return Wait(firstReady.Task, token); }
                secondEntered.TrySetResult(true); return Wait(secondReady.Task, token);
            }, () => Volatile.Read(ref allowed) != 0,
            phase =>
            {
                if (phase == "update_wait_safe") Interlocked.Increment(ref waitingNotices);
                if (phase != "update_handoff") return;
                Interlocked.Increment(ref handoffs);
                var locked = false;
                try { using var writable = new FileStream(fixture.PreparedFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                catch (IOException) { locked = true; }
                Require(locked, "Prepared updater was writable during handoff.");
                cancellation.Cancel(); // Exercise the real path up to its final launch boundary without starting a helper.
            });
        try
        {
            await Deadline(firstEntered.Task).ConfigureAwait(false);
            fixture.CapturePreparedDirectory();
            Require(!update.IsCompleted && File.Exists(fixture.PreparedFile), "Readiness wait discarded its prepared updater.");
            Require(fixture.Handler.Requests == 3, "Preparation must download only metadata, signature and updater.");
            firstReady.TrySetResult(true); // A notification alone does not authorize installation.
            await Deadline(secondEntered.Task).ConfigureAwait(false);
            Require(!update.IsCompleted && fixture.Handler.Requests == 3 && File.Exists(fixture.PreparedFile),
                "An unsafe wakeup restarted downloads or discarded the prepared updater.");
            Volatile.Write(ref allowed, 1); secondReady.TrySetResult(true);
            var result = await Deadline(update).ConfigureAwait(false);
            Require(result.MessageKey == "Update.Cancelled" && handoffs == 1 && waitingNotices == 1,
                "Prepared update did not resume exactly once or ignored cancellation at handoff.");
            Require(fixture.Handler.Requests == 3 && !Directory.Exists(fixture.PreparedDirectory),
                "Resuming re-downloaded files or failed to clean up its canceled handoff.");
        }
        finally { cancellation.Cancel(); await Deadline(update).ConfigureAwait(false); }
    }

    private static async Task RecheckSafetyAtHandoff()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        var firstEntered = Signal(); var secondEntered = Signal(); var firstReady = Signal(); var secondReady = Signal();
        var allowed = 0; var waits = 0; var handoffs = 0; var waitingNotices = 0;
        var update = fixture.Updater.CheckAndInstallAsync(new Version(0, 9, 5), cancellation.Token,
            token =>
            {
                if (Interlocked.Increment(ref waits) == 1) { firstEntered.TrySetResult(true); return Wait(firstReady.Task, token); }
                secondEntered.TrySetResult(true); return Wait(secondReady.Task, token);
            }, () => Volatile.Read(ref allowed) != 0,
            phase =>
            {
                if (phase == "update_wait_safe") Interlocked.Increment(ref waitingNotices);
                if (phase != "update_handoff") return;
                if (Interlocked.Increment(ref handoffs) == 1) Volatile.Write(ref allowed, 0);
                else cancellation.Cancel(); // The second boundary is verified without launching a helper.
            });
        try
        {
            await Deadline(firstEntered.Task).ConfigureAwait(false); fixture.CapturePreparedDirectory();
            Volatile.Write(ref allowed, 1); firstReady.TrySetResult(true);
            await Deadline(secondEntered.Task).ConfigureAwait(false);
            Require(!update.IsCompleted && handoffs == 1 && fixture.Handler.Requests == 3 && File.Exists(fixture.PreparedFile),
                "Safety lost at handoff did not return to readiness with the same prepared updater.");
            using (var writable = new FileStream(fixture.PreparedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Require(writable.Length != 0, "The verified file lease was retained while readiness was pending again.");
            Volatile.Write(ref allowed, 1); secondReady.TrySetResult(true);
            var result = await Deadline(update).ConfigureAwait(false);
            Require(result.MessageKey == "Update.Cancelled" && handoffs == 2 && waitingNotices == 2 && fixture.Handler.Requests == 3,
                "A second safe transition repeated downloads or duplicated readiness notices.");
            Require(!Directory.Exists(fixture.PreparedDirectory), "Canceled second handoff retained its prepared updater directory.");
        }
        finally { cancellation.Cancel(); await Deadline(update).ConfigureAwait(false); }
    }
    private static async Task CancelWhileWaiting()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var handoffs = 0;
        var update = fixture.Updater.CheckAndInstallAsync(new Version(0, 9, 5), cancellation.Token,
            token => { entered.TrySetResult(true); return Task.Delay(Timeout.Infinite, token); }, () => false,
            phase => { if (phase == "update_handoff") Interlocked.Increment(ref handoffs); });
        try
        {
            await Deadline(entered.Task).ConfigureAwait(false); fixture.CapturePreparedDirectory();
            Require(File.Exists(fixture.PreparedFile), "Prepared updater was not retained while waiting.");
            cancellation.Cancel();
            var result = await Deadline(update).ConfigureAwait(false);
            Require(result.MessageKey == "Update.Cancelled" && handoffs == 0 && fixture.Handler.Requests == 3,
                "Canceling readiness started a handoff or repeated the network check.");
            Require(!Directory.Exists(fixture.PreparedDirectory), "Canceled readiness retained its prepared updater directory.");
        }
        finally { cancellation.Cancel(); await Deadline(update).ConfigureAwait(false); }
    }

    private static async Task RejectChangedPreparedFile()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var ready = Signal(); var allowed = 0; var handoffs = 0;
        var update = fixture.Updater.CheckAndInstallAsync(new Version(0, 9, 5), cancellation.Token,
            token => { entered.TrySetResult(true); return Wait(ready.Task, token); }, () => Volatile.Read(ref allowed) != 0,
            phase => { if (phase == "update_handoff") { Interlocked.Increment(ref handoffs); cancellation.Cancel(); } });
        try
        {
            await Deadline(entered.Task).ConfigureAwait(false); fixture.CapturePreparedDirectory();
            using (var changed = new FileStream(fixture.PreparedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { changed.Position = changed.Length - 1; var original = changed.ReadByte(); changed.Position--; changed.WriteByte((byte)(original ^ 1)); }
            Volatile.Write(ref allowed, 1); ready.TrySetResult(true);
            var result = await Deadline(update).ConfigureAwait(false);
            Require(result.State == ReleaseUpdateState.Rejected && handoffs == 0,
                "Changed updater bytes reached the launch boundary after readiness resumed.");
            Require(fixture.Handler.Requests == 3 && !Directory.Exists(fixture.PreparedDirectory),
                "Rejected prepared updater was downloaded again or retained on disk.");
        }
        finally { cancellation.Cancel(); await Deadline(update).ConfigureAwait(false); }
    }

    private static async Task Wait(Task ready, CancellationToken token)
    {
        var canceled = Signal();
        using (token.Register(() => canceled.TrySetCanceled()))
        {
            var completed = await Task.WhenAny(ready, canceled.Task).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); await completed.ConfigureAwait(false);
        }
    }
    private static async Task Deadline(Task operation)
    {
        Require(await Task.WhenAny(operation, Task.Delay(5000)).ConfigureAwait(false) == operation, "Updater readiness operation did not complete.");
        await operation.ConfigureAwait(false);
    }
    private static async Task<T> Deadline<T>(Task<T> operation)
    { await Deadline((Task)operation).ConfigureAwait(false); return await operation.ConfigureAwait(false); }
    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        private const string LauncherVariable = "MSFS_LANDING_STATS_LAUNCHER_PATH";
        private const string SkipVariable = "MSFS_LANDING_STATS_SKIP_UPDATE_ONCE";
        private readonly string? _previousLauncher = Environment.GetEnvironmentVariable(LauncherVariable);
        private readonly string? _previousSkip = Environment.GetEnvironmentVariable(SkipVariable);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "update-readiness-" + Guid.NewGuid().ToString("N"));
        private readonly string _updates;
        public readonly ReleaseHandler Handler = new ReleaseHandler();
        public readonly ReleaseUpdater Updater;
        public string PreparedDirectory { get; private set; } = "";
        public string PreparedFile => Path.Combine(PreparedDirectory, "MSFS-Landing-Stats.Updater.exe");
        public Fixture()
        {
            _updates = Path.Combine(_root, "Updates");
            Directory.CreateDirectory(_root); var target = Path.Combine(_root, "test-launcher.exe"); File.WriteAllBytes(target, new byte[] { 77, 90 });
            Environment.SetEnvironmentVariable(LauncherVariable, target); Environment.SetEnvironmentVariable(SkipVariable, null);
            Updater = new ReleaseUpdater(Handler, _updates);
        }
        public void CapturePreparedDirectory()
        {
            PreparedDirectory = Directory.GetDirectories(_updates, "v0.9.6-*").Single();
        }
        public void Dispose()
        {
            Updater.Dispose();
            Environment.SetEnvironmentVariable(LauncherVariable, _previousLauncher); Environment.SetEnvironmentVariable(SkipVariable, _previousSkip);
            if (PreparedDirectory.Length != 0 && Directory.Exists(PreparedDirectory)) Directory.Delete(PreparedDirectory, true);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        // Immutable public v0.9.6 channel/signature/updater from GitHub releases.
        // The real protocol checks its RSA signature and updater SHA-256; the helper is never executed.
        private readonly byte[] _manifest = Resource("manifest.txt");
        private readonly byte[] _signature = Resource("signature.txt");
        private readonly byte[] _updater = Resource("updater.gz", true);
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref _requests);
            var path = request.RequestUri!.AbsolutePath;
            var bytes = path.EndsWith("/update-channel.txt", StringComparison.Ordinal) ? _manifest :
                path.EndsWith("/update-channel.sig", StringComparison.Ordinal) ? _signature :
                path.EndsWith("/v0.9.6/MSFS-Landing-Stats.Updater.exe", StringComparison.Ordinal) ? _updater :
                throw new InvalidOperationException("Unexpected update fixture request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
        private static byte[] Resource(string suffix, bool compressed = false)
        {
            var assembly = typeof(UpdateReadinessRegressionTests).Assembly;
            using var source = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith("update-readiness-v0.9.6-" + suffix, StringComparison.Ordinal)))!;
            using var output = new MemoryStream();
            if (compressed) { using var gzip = new GZipStream(source, CompressionMode.Decompress); gzip.CopyTo(output); }
            else source.CopyTo(output);
            return output.ToArray();
        }
    }
}
