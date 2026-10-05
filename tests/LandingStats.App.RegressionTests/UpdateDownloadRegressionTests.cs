using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Updates;
using UpdaterProgram = LandingStats.App.Updater.Program;

namespace LandingStats.App.RegressionTests;

internal static class UpdateDownloadRegressionTests
{
    private const string DownloadUrl = "https://github.com/Arderos/msfs24-landing-stats/releases/download/v0.8.7/test";
    private static readonly Assembly[] ProtocolAssemblies = { typeof(ReleaseUpdater).Assembly, typeof(UpdaterProgram).Assembly };

    public static void BodyTimeouts() => CheckBodyCancellation(false).GetAwaiter().GetResult();
    public static void CallerCancellation() => CheckBodyCancellation(true).GetAwaiter().GetResult();
    public static void DestinationOwnership() => CheckDestinationOwnership().GetAwaiter().GetResult();
    public static void SuccessfulDownloads() => CheckSuccessfulDownloads().GetAwaiter().GetResult();
    public static void ApplicationClassifiesCancellation() => CheckApplicationCancellation().GetAwaiter().GetResult();

    public static void ExtractionPreservesExistingFile()
    {
        foreach (var validEntry in new[] { true, false })
        {
            var root = NewRoot();
            var destination = Path.Combine(root, "MSFS-Landing-Stats.exe");
            var package = Path.Combine(root, "package.zip");
            var original = new byte[] { 7, 8, 9, 10 };
            try
            {
                File.WriteAllBytes(destination, original);
                using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
                using (var entry = archive.CreateEntry(validEntry ? "MSFS-Landing-Stats.exe" : "unexpected.exe").Open())
                    entry.WriteByte(1);
                Exception? failure = null;
                try { UpdaterProgram.ExtractLegacySingleExecutable(package, destination); }
                catch (Exception error) { failure = error; }
                Require(failure is IOException || failure is InvalidDataException, "Existing extraction target or malformed archive must fail.");
                Require(File.Exists(destination) && Convert.ToBase64String(File.ReadAllBytes(destination)) == Convert.ToBase64String(original),
                    "Failed extraction must not delete a destination it never created.");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static async Task CheckBodyCancellation(bool cancelCaller)
    {
        foreach (var assembly in ProtocolAssemblies)
        foreach (var file in new[] { false, true })
        {
            var root = NewRoot();
            var destination = Path.Combine(root, "partial.exe");
            using var stream = new StalledBodyStream();
            using var client = new HttpClient(new BodyHandler(stream)) { Timeout = cancelCaller ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(500) };
            using var caller = new CancellationTokenSource();
            try
            {
                var download = file ? DownloadFile(assembly, client, destination, 2, new string('0', 64), caller.Token) : DownloadSmall(assembly, client, caller.Token);
                Require(await Task.WhenAny(stream.Waiting.Task, Task.Delay(3000)).ConfigureAwait(false) == stream.Waiting.Task,
                    "Fixture must reach the body read before testing its cancellation.");
                if (cancelCaller) caller.Cancel();
                var completed = await Task.WhenAny(download, Task.Delay(3000)).ConfigureAwait(false) == download;
                // Always release the old implementation's hung read so a failed
                // regression does not leak a task or block the test process.
                if (!completed) stream.Dispose();
                var failure = await Failure(download).ConfigureAwait(false);
                Require(completed, assembly.GetName().Name + ": " + (file ? "file" : "metadata") + " body outlived " + (cancelCaller ? "caller cancellation" : "HttpClient.Timeout"));
                Require(failure is OperationCanceledException, "Aborted body must be reported as cancellation, not its disposal IOException.");
                Require(stream.WasDisposed, "Cancellation must close a body stream that ignores ReadAsync's token on net48.");
                Require(!File.Exists(destination), "An owned partial download must be removed on cancellation.");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static async Task CheckDestinationOwnership()
    {
        foreach (var assembly in ProtocolAssemblies)
        foreach (var rejectHeaders in new[] { false, true })
        {
            var root = NewRoot();
            var destination = Path.Combine(root, "already-present.exe");
            var original = new byte[] { 7, 8, 9, 10 };
            File.WriteAllBytes(destination, original);
            using var client = new HttpClient(new ByteHandler(new byte[] { 1, 2 }));
            try
            {
                var failure = await Failure(DownloadFile(assembly, client, destination, rejectHeaders ? 3 : 2, new string('0', 64), CancellationToken.None)).ConfigureAwait(false);
                Require(failure is IOException || failure is InvalidDataException, "Existing destination or rejected signed length must fail.");
                Require(File.Exists(destination) && Convert.ToBase64String(File.ReadAllBytes(destination)) == Convert.ToBase64String(original),
                    assembly.GetName().Name + ": failed download must not delete a destination it never created.");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static async Task CheckSuccessfulDownloads()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        foreach (var assembly in ProtocolAssemblies)
        foreach (var timeout in new[] { TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan })
        {
            var root = NewRoot();
            var destination = Path.Combine(root, "verified.exe");
            using var client = new HttpClient(new ByteHandler(bytes)) { Timeout = timeout };
            try
            {
                var small = await DownloadSmall(assembly, client, CancellationToken.None).ConfigureAwait(false);
                Require(Convert.ToBase64String(small) == Convert.ToBase64String(bytes), "Metadata bytes must survive the whole-transfer deadline.");
                await DownloadFile(assembly, client, destination, bytes.Length, hash, CancellationToken.None).ConfigureAwait(false);
                Require(Convert.ToBase64String(File.ReadAllBytes(destination)) == Convert.ToBase64String(bytes), "Successful verified downloads must retain exact bytes.");
                var invalid = Path.Combine(root, "invalid.exe");
                Require(await Failure(DownloadFile(assembly, client, invalid, bytes.Length, new string('0', 64), CancellationToken.None)).ConfigureAwait(false) is InvalidDataException,
                    "Deadline changes must retain hash verification.");
                Require(!File.Exists(invalid), "Hash failure must remove the newly owned file.");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static async Task CheckApplicationCancellation()
    {
        foreach (var cancelCaller in new[] { false, true })
        {
            using var stream = new StalledBodyStream();
            using var updater = new ReleaseUpdater(new BodyHandler(stream));
            var client = (HttpClient)typeof(ReleaseUpdater).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(updater)!;
            client.Timeout = cancelCaller ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(500);
            using var caller = new CancellationTokenSource();
            var check = updater.CheckAndInstallAsync(new Version(0, 8, 7), caller.Token);
            Require(await Task.WhenAny(stream.Waiting.Task, Task.Delay(3000)).ConfigureAwait(false) == stream.Waiting.Task, "Application fixture must enter its metadata body.");
            if (cancelCaller) caller.Cancel();
            var completed = await Task.WhenAny(check, Task.Delay(3000)).ConfigureAwait(false) == check;
            if (!completed) stream.Dispose();
            var result = await check.ConfigureAwait(false);
            Require(completed, "Application update check must not hang on its metadata body.");
            Require(result.State == ReleaseUpdateState.Unavailable && result.MessageKey == (cancelCaller ? "Update.Cancelled" : "Update.Timeout"),
                "Application must distinguish explicit cancellation from the transfer deadline.");
        }
    }

    private static Task<byte[]> DownloadSmall(Assembly assembly, HttpClient client, CancellationToken token) =>
        (Task<byte[]>)Protocol(assembly).GetMethod("DownloadSmallAsync")!.Invoke(null, new object[] { client, DownloadUrl, "metadata", 32, token })!;

    private static Task DownloadFile(Assembly assembly, HttpClient client, string destination, long size, string hash, CancellationToken token) =>
        (Task)Protocol(assembly).GetMethod("DownloadVerifiedFileAsync")!.Invoke(null, new object[] { client, DownloadUrl, "package", destination, size, hash, 64L, token })!;

    private static Type Protocol(Assembly assembly) => assembly.GetType("LandingStats.UpdateProtocol.ReleaseUpdateProtocol", true)!;
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-download-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static async Task<Exception?> Failure(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (Exception error) { return error; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class BodyHandler : HttpMessageHandler
    {
        private readonly Stream _stream;
        internal BodyHandler(Stream stream) { _stream = stream; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(_stream); content.Headers.ContentLength = 2;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }
    private sealed class ByteHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        internal ByteHandler(byte[] bytes) { _bytes = bytes; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(_bytes) });
    }
    private sealed class StalledBodyStream : Stream
    {
        private readonly TaskCompletionSource<int> _pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        internal readonly TaskCompletionSource<bool> Waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed { get; private set; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _reads) == 1) { buffer[offset] = 1; return Task.FromResult(1); }
            Waiting.TrySetResult(true);
            // Models a net48 response stream: cancellation alone cannot interrupt
            // an in-flight native read, but closing the response aborts it.
            return _pending.Task;
        }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true; _pending.TrySetException(new IOException("The response body was closed.")); base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
