using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.Runtime.Contracts;
using LandingStats.WorkerHost;

namespace LandingStats.App.RegressionTests;

internal static class ReviewRuntimeStorageRegressionTests
{
    public static void RunAll() => Task.Run(async () =>
    {
        await OverflowReportsLossAndContinues();
        await OverflowReportsLossAndContinues(true);
        await OverflowReportsLossAndContinues(lostFlow: 11);
        await OverflowReportsLossAndContinues(lostFlow: 12);
        await DependencyWaitCanStop();
        DecodedHistoryBound();
        EngineRepairRetainsIdentityAndPinsBytes();
        TerminalReceiptsLeaveTheActiveQueue();
        TemporaryCleanupRequiresPublishedEvidence();
        await CancelledStartupKeepsCancellation();
    }).GetAwaiter().GetResult();

    private static async Task OverflowReportsLossAndContinues(bool catalog = false, int lostFlow = 0)
    {
        var identity = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, catalog ? ComponentRole.Controllers : ComponentRole.Simulator);
        var ack = RuntimeWire.Encode(new RuntimeEnvelope(identity, 0, 1, 0, new DurableAckMessage(WorkerChannel.MaximumPendingMessages)));
        using var pipe = new DuplexFixture(ack);
        var channel = new WorkerChannel(pipe, identity);
        const int lost = 40;
        for (var i = 0; i < WorkerChannel.MaximumPendingMessages + lost; i++)
        {
            RuntimePayload payload = catalog && (i == 0 || i == WorkerChannel.MaximumPendingMessages + lost - 1)
                ? new ControllerCatalogMessage(i == 0 ? 1 : 2, Array.Empty<ControllerDescriptor>())
                : catalog ? new ControllerSnapshotMessage(1, new long[8], new double[48], new bool[48]) : new LifecycleMessage(SourceLifecycle.SimRunning, 1);
            if (!catalog && i == 0) payload = new LifecycleMessage(SourceLifecycle.Connected);
            if (!catalog && i == 1 && lostFlow == 12) payload = new LifecycleMessage(SourceLifecycle.Flow, 11);
            if (!catalog && i == WorkerChannel.MaximumPendingMessages + lost - 1 && lostFlow != 0)
                payload = new LifecycleMessage(SourceLifecycle.Flow, lostFlow);
            Check(channel.TryPublish(payload, catalog ? 0 : 1, i + 1), "Overflow stopped the source callback.");
        }
        Check(channel.PendingCount == WorkerChannel.MaximumPendingMessages, "Source queue escaped its bound.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writer = channel.WritePumpAsync(stop.Token);
        try { await channel.ReadPumpAsync(stop.Token); } catch (EndOfStreamException) { }
        Check(channel.TryPublish(catalog ? (RuntimePayload)new ControllerSnapshotMessage(2, new long[8], new double[48], new bool[48]) : new LifecycleMessage(SourceLifecycle.SimRunning, 1), catalog ? 0 : 1, 2000), "Source did not resume after ACK.");
        await pipe.WaitForWrites(WorkerChannel.MaximumPendingMessages + 3, stop.Token);
        stop.Cancel(); try { await writer; } catch (OperationCanceledException) { }
        using var frames = new MemoryStream(pipe.Output());
        long previous = 0; var gaps = 0; var messages = 0; var restoredCatalog = false; var restoredLifecycle = false;
        RuntimeEnvelope? message;
        while ((message = await RuntimeWire.ReadAsync(frames, CancellationToken.None)) != null)
        {
            Check(RuntimeSequence.IsNext(previous, message), "Overflow recovery emitted an unexplained sequence hole.");
            previous = message.Sequence; messages++;
            if (message.Payload is ControllerCatalogMessage restored && gaps > 0)
            { Check(restored.Revision == 2, "Overflow restored an obsolete controller catalog."); restoredCatalog = true; }
            if (message.Payload is SimulatorLifecycleSnapshotMessage lifecycle && gaps > 0)
            {
                Check(lifecycle.Connected && lifecycle.Known && lifecycle.Running == (lostFlow == 12 ? -1 : 1), "Overflow lost the latest SDK state.");
                Check(lifecycle.SdkReplay == (lostFlow == 11 ? LandingStats.Core.SdkReplayState.Active :
                    lostFlow == 12 ? LandingStats.Core.SdkReplayState.Inactive : LandingStats.Core.SdkReplayState.Unknown),
                    "The lifecycle transition dropped by the channel was absent from its recovery snapshot.");
                restoredLifecycle = true;
            }
            if (message.Payload is SourceGapMessage gap)
            {
                gaps++;
                Check(gap.Reason == SourceGapReason.BufferOverflow && gap.FirstMissing == 1025 && gap.LastMissing == 1064 && gap.LossCountKnown,
                    "Overflow loss range was not exact.");
            }
        }
        Check(gaps == 1 && messages == 1027 && previous == 1067 && (catalog ? restoredCatalog : restoredLifecycle), "Recovery dropped the first post-gap message or its state.");
    }
    private static async Task DependencyWaitCanStop()
    {
        using var stop = new CancellationTokenSource();
        var context = new ManagedComponentContext(1, stop.Token);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = context.WaitForDependencyAsync(pending.Task, DependencyWait.Storage);
        Check(context.Health?.WaitingFor == DependencyWait.Storage && context.Health.OperationId == Guid.Empty, "Storage wait masquerades as a stuck operation.");
        stop.Cancel();
        try { await wait; throw new InvalidOperationException("Storage wait ignored cancellation."); }
        catch (OperationCanceledException) { }
        Check(!pending.Task.IsCompleted, "Cancellation changed the dispatcher's durability receipt.");
        pending.SetResult(true); context.Dispose();
    }
    private static void DecodedHistoryBound()
    {
        using var source = new MemoryStream(new byte[33]); using var bounded = new BoundedHistoryStream(source, 32);
        try { bounded.CopyTo(Stream.Null); throw new InvalidOperationException("Decoded history bound was not enforced."); }
        catch (InvalidDataException) { }
        Check(source.Position == 33, "History reader allocated/read beyond the boundary witness.");
    }
    private static async Task CancelledStartupKeepsCancellation()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var host = new ProcessWorkerHost(Path.Combine(AppContext.BaseDirectory, "LandingStats.WorkerHost.exe"),
            new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers),
            new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency), (_, __) => Task.CompletedTask);
        try { await host.StartAsync(TimeSpan.FromSeconds(1), cancellationToken: cancelled.Token); throw new InvalidOperationException("Cancelled startup succeeded."); }
        catch (OperationCanceledException) { }
        Check(host.ProcessId == 0, "Already-cancelled startup launched a worker.");
    }
    public static void PermanentStorageFailureStops() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "review-storage-stop-" + Guid.NewGuid().ToString("N"));
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        try
        {
            using (var journal = RollingRuntimeJournal.Open(Path.Combine(root, "journal"), identity)) { }
            var escalations = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var locked = new FileStream(Path.Combine(root, "journal", "session.json"), FileMode.Open, FileAccess.Read, FileShare.None);
            using var runtime = new ApplicationRuntime(root, identity, Path.Combine(AppContext.BaseDirectory, "LandingStats.WorkerHost.exe"),
                (_, __) => Task.CompletedTask, (name, _) => escalations.Enqueue(name), (_, __) => "normal",
                new ComponentRecoveryOptions { CheckInterval = TimeSpan.FromMilliseconds(25), InitialBackoff = TimeSpan.FromMilliseconds(20), MaximumBackoff = TimeSpan.FromMilliseconds(100) });
            using (var deadline = new CancellationTokenSource(300))
            {
                try { await runtime.StartConfiguredAsync(new DeliveryPolicy { Revision = 1 }, cancellationToken: deadline.Token); throw new InvalidOperationException("Unavailable journal acknowledged startup policy."); }
                catch (OperationCanceledException) { }
            }
            var stopping = runtime.StopAsync();
            Check(await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(105))) == stopping, "Permanent storage failure made shutdown unbounded.");
            try { await stopping; throw new InvalidOperationException("Uncommitted startup/shutdown work was reported as durable."); }
            catch (AggregateException error) { Check(error.InnerExceptions.Count != 0, "Shutdown failure lost its cause."); }
            Check(escalations.IsEmpty && runtime.Source(ComponentRole.Simulator).CurrentHost == null && runtime.Source(ComponentRole.Controllers).CurrentHost == null,
                "Storage outage started sources or escalated into an application restart loop.");
            Check(runtime.Health.All(item => item.State == SupervisionState.Stopped), "A later owner survived an earlier shutdown failure.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }).GetAwaiter().GetResult();

    private static void EngineRepairRetainsIdentityAndPinsBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-engine-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var engines = Path.Combine(root, "engines"); Directory.CreateDirectory(source);
        try
        {
            File.WriteAllBytes(Path.Combine(source, "engine.dll"), new byte[] { 1, 2, 3 });
            var engine = ProcessingEngineLease.CaptureFiles(engines, source, new[] { "engine.dll" });
            using (engine.AcquireUse())
            {
                Check(ProcessingEngineLease.CaptureFiles(engines, source, new[] { "engine.dll" }).Identity == engine.Identity, "A pinned healthy engine could not be reused.");
                try { File.WriteAllBytes(Path.Combine(engine.DirectoryPath, "engine.dll"), new byte[] { 9 }); throw new InvalidOperationException("Pinned bytes were mutable."); }
                catch (IOException) { }
            }
            File.WriteAllBytes(Path.Combine(engine.DirectoryPath, "engine.dll"), new byte[] { 9 });
            var repaired = ProcessingEngineLease.CaptureFiles(engines, source, new[] { "engine.dll" });
            Check(repaired.Identity == engine.Identity && File.ReadAllBytes(Path.Combine(repaired.DirectoryPath, "engine.dll"))[0] == 1,
                "Repair substituted a different engine identity.");
            Check(Directory.GetFiles(repaired.DirectoryPath, "*.corrupt-*").Length != 0, "Repair discarded the damaged copy.");
            File.WriteAllBytes(Path.Combine(repaired.DirectoryPath, "unlisted.dll"), new byte[] { 8 });
            try { using var use = repaired.AcquireUse(); throw new InvalidOperationException("An unlisted executable dependency was accepted."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, true); }
    }
    private static void TerminalReceiptsLeaveTheActiveQueue()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-receipts-" + Guid.NewGuid().ToString("N"));
        var archives = Path.Combine(root, "archives"); Directory.CreateDirectory(archives);
        try
        {
            var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives);
            var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload);
            Guid first = Guid.Empty; UploadClaim? delivered = null; string? firstPath = null, firstHash = null;
            for (var i = 0; i < 80; i++)
            {
                var id = Guid.NewGuid(); var path = Path.Combine(archives, id.ToString("N") + ".zip");
                File.WriteAllBytes(path, new byte[] { 1, 2, (byte)i }); var hash = EpisodeTransactionStore.Hash(path);
                var sidecar = Path.Combine(archives, "." + id.ToString("N") + ".sha256");
                File.WriteAllText(sidecar, i == 79 ? new string('0', 64) : hash);
                outbox.Register(id, path, hash, false, DateTime.UtcNow); var claim = outbox.TryClaim(id, worker)!;
                if (i == 0) { first = id; delivered = claim; firstPath = path; firstHash = hash; }
                if (i % 2 == 0) Check(outbox.Acknowledge(claim), "Delivery receipt failed."); else Check(outbox.Reject(claim, 422), "Quarantine receipt failed.");
                Check(File.Exists(sidecar) == (i == 79), "Terminal cleanup removed unknown metadata or retained its verified sidecar.");
            }
            Check(outbox.Enumerate().Count == 0, "Completed archives remained in the owner polling set.");
            var restored = new DurableOutbox(Path.Combine(root, "outbox"), archives);
            Check(restored.Read(first).State == OutboxState.Delivered && restored.Acknowledge(delivered!), "Restart lost delivery idempotency.");
            Check(restored.Register(first, firstPath!, firstHash!, false, DateTime.UtcNow).State == OutboxState.Delivered && !File.Exists(firstPath), "Delivered capture was recreated.");
            Check(Directory.GetFiles(Path.Combine(root, "outbox"), "*.json").Length == 0, "Per-item terminal manifests were never compacted.");
            Check(Directory.GetFiles(archives, "*.rejected-422.zip").Length == 40, "Receipt compaction removed quarantined evidence.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void TemporaryCleanupRequiresPublishedEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-publish-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "state.json"); File.WriteAllText(target, "published");
            string Temporary() => Path.Combine(root, ".w" + Guid.NewGuid().ToString("N") + ".tmp");
            var duplicate = Temporary(); var sole = Temporary(); var active = Temporary();
            File.Copy(target, duplicate); File.Copy(target, active); File.WriteAllText(sole, "sole evidence");
            using (var writer = new FileStream(active, FileMode.Open, FileAccess.Write, FileShare.None))
                DurableFile.Write(target, stream => stream.WriteByte(42));
            Check(!File.Exists(duplicate) && File.Exists(sole) && File.Exists(active), "Temp cleanup discarded sole or active bytes, or leaked a verified duplicate.");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class DuplexFixture : Stream
    {
        private readonly MemoryStream _input;
        private readonly MemoryStream _output = new MemoryStream();
        private readonly SemaphoreSlim _written = new SemaphoreSlim(0);
        private int _writes;
        public DuplexFixture(byte[] input) { _input = new MemoryStream(input); }
        public async Task WaitForWrites(int count, CancellationToken token)
        { while (Volatile.Read(ref _writes) < count) await _written.WaitAsync(token); }
        public byte[] Output() { lock (_output) return _output.ToArray(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count)
        { lock (_output) _output.Write(buffer, offset, count); Interlocked.Increment(ref _writes); _written.Release(); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _input.Dispose(); _output.Dispose(); _written.Dispose(); } base.Dispose(disposing); }
    }
}
