using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ReviewRawBatchRegressionTests
{
    internal static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "raw-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var complete = false;
        try { Batched(Path.Combine(root, "batch")); IsolatedFailure(Path.Combine(root, "failure")); complete = true; }
        finally { if (complete && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
    private static void Batched(string root)
    {
        var store = new RawEffectStore(Path.Combine(root, "raw")); var batches = new RawBatchPersistence(store);
        var session = Guid.NewGuid();
        batches.Commit(Input(session, 1), new object[] { Start() });
        for (var n = 2; n <= 101; n++) batches.Commit(Input(session, n), new object[] { new RawDebugSampleEventArgs(Sample(n)) });
        batches.Commit(Input(session, 102), new object[] { EventArgs.Empty });
        Check(!Directory.Exists(store.Root), "The recording producer performed RAW disk I/O.");
        batches.FlushAtCheckpointAsync().GetAwaiter().GetResult();
        Check(Directory.GetFiles(store.Root, "*.batch").Length == 1 && Directory.GetFiles(store.Root, "*.bin").Length == 0,
            "RAW created/flushed one file per frame instead of a batch.");
        var recovered = new RawEffectStore(store.Root);
        Check(recovered.Enumerate().Count() == 102 && recovered.Read(Input(session, 101).Input.Id).Effects.Count == 1, "Batched RAW cannot be recovered.");
        var archives = Path.Combine(root, "archives"); var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives);
        outbox.SetAutomaticEnabled(true);
        var chunks = new RawChunkStore(Path.Combine(root, "chunks"), archives, outbox, automatic: true);
        new RawExportOwner(recovered, chunks, (_, __, ___) => { }, 25).Advance();
        Check(outbox.Enumerate().Count == 4 && outbox.Enumerate().All(id => outbox.Read(id).Automatic), "Consented RAW bypassed automatic-delivery policy.");
        Check(outbox.Enumerate().Sum(id => Rows(outbox.Read(id).Path)) == 100, "Batched recovery lost or duplicated samples.");
        outbox.SetAutomaticEnabled(false);
        var worker = new LandingStats.Runtime.Contracts.WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, LandingStats.Runtime.Contracts.ComponentRole.Upload);
        Check(outbox.Enumerate().All(id => outbox.TryClaim(id, worker) == null), "Opt-out still permits automatic RAW delivery.");
        new RawExportOwner(new RawEffectStore(store.Root), chunks, (_, __, ___) => throw new InvalidOperationException("Duplicate RAW publication"), 25).Advance();
    }
    private static void IsolatedFailure(string root)
    {
        var store = new RawEffectStore(Path.Combine(root, "raw")); var batches = new RawBatchPersistence(store); var session = Guid.NewGuid();
        var first = Input(session, 1);
        batches.Commit(first, new object[] { Start(), new RawDebugSampleEventArgs(Sample(1)) });
        var blocked = Path.Combine(store.Root, first.Input.Id.ToString("N") + ".batch"); Directory.CreateDirectory(blocked);
        batches.FlushAtCheckpointAsync().GetAwaiter().GetResult();
        Check(batches.PendingCount == 0 && batches.LastGap?.DroppedInputs == 1 && File.Exists(Path.Combine(store.Root, "gap.json")),
            "RAW I/O failure blocked the checkpoint or lacked a bounded gap diagnostic.");
        Directory.Delete(blocked);
        batches.Commit(Input(session, 2), new object[] { new RawDebugSampleEventArgs(Sample(2)) });
        batches.Commit(Input(session, 3), new object[] { EventArgs.Empty }); batches.Flush();
        var archives = Path.Combine(root, "archives"); var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives);
        var chunks = new RawChunkStore(Path.Combine(root, "chunks"), archives, outbox);
        new RawExportOwner(new RawEffectStore(store.Root), chunks, (_, __, ___) => { }, 25).Advance();
        Check(outbox.Enumerate().Count == 1 && Rows(outbox.Read(outbox.Enumerate().Single()).Path) == 1,
            "RAW failed to start a separate fragment after optional storage recovered.");
        var limited = new RawBatchPersistence(new RawEffectStore(Path.Combine(root, "quota"), 1));
        limited.Commit(Input(session, 4), new object[] { Start() }); limited.FlushAtCheckpointAsync().GetAwaiter().GetResult();
        Check(limited.LastGap != null, "RAW quota exhaustion escaped into recording or was hidden.");
    }
    private static DurableRuntimeInput Input(Guid session, int n) => new DurableRuntimeInput(new RuntimeInput(StableRuntimeIdentity.Command(session, n),
        JournalRecordKind.RuntimeCommand, n, n, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new RecordingCommand(RecordingCommandKind.Tick).Encode()), new JournalCursor(1, n));
    private static RawDebugCaptureStartedEventArgs Start() => new RawDebugCaptureStartedEventArgs(Array.Empty<TelemetrySample>(),
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "MSFS", "Test", "T", "M", Array.Empty<string>());
    private static TelemetrySample Sample(int n) => new TelemetrySample { Sequence = n, SimulationTimeSeconds = n * .1, MotionSimulation = true };
    private static int Rows(string path)
    { using var zip = ZipFile.OpenRead(path); using var reader = new StreamReader(zip.GetEntry("telemetry.csv")!.Open()); return reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length - 1; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
