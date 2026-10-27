using System;
using System.IO;
using System.Linq;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ProfileEvidenceRawRegressionTests
{
    internal static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "profile-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sample = Sample();
            BatchesAndCollection(Path.Combine(root, "batches"), sample);
            ImportAndIntegrity(Path.Combine(root, "import"), sample);
            ImportOverExistingLiveBatch(Path.Combine(root, "batch-import"), sample);
            Quota(Path.Combine(root, "quota"), sample);
            OrphanPublicationRecovery(Path.Combine(root, "orphans"), sample);
            Legacy(Path.Combine(root, "legacy"), sample);
        }
        finally { Directory.Delete(root, true); }
    }
    private static TelemetrySample Sample()
    {
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
        var catalog = new ControllerCatalogMessage(1, Array.Empty<ControllerDescriptor>(), bindingContext: "profile-sha256",
            profileEvidence: "{\"schema\":1,\"profiles\":\"" + new string('x', 200000) + "\"}");
        return new TelemetrySample { Sequence = 1, MotionSimulation = true, ControllerCatalog = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(worker, 0, 1, 1000, catalog)) };
    }
    private static void BatchesAndCollection(string root, TelemetrySample sample)
    {
        var store = new RawEffectStore(root); var batches = new RawBatchPersistence(store); var session = Guid.NewGuid();
        var initial = new RawDebugCaptureStartedEventArgs(new[] { sample, sample }, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "MSFS", "Aircraft", "Type", "Model", Array.Empty<string>());
        batches.Commit(Input(session, 1), new object[] { initial });
        for (var row = 2; row <= 20; row++) batches.Commit(Input(session, row), new object[] { new RawDebugSampleEventArgs(sample) });
        Check(!Directory.Exists(root), "Recording producer wrote evidence sidecars before the storage flush.");
        batches.Flush(); Check(batches.LastGap == null, "Evidence persistence produced an unexpected RAW gap.");
        Check(Directory.GetFiles(root, "*.catalog").Length == 1, "Repeated RAW frames duplicated their diagnostic catalog.");
        var diskBytes = Directory.GetFiles(root).Sum(path => new FileInfo(path).Length);
        Check(diskBytes < sample.ControllerCatalog!.Length + 22L * TelemetryBinaryCodec.EncodedSize + 10000, "RAW storage repeated large evidence per frame.");
        var restored = new RawEffectStore(root);
        var starts = (RawDebugCaptureStartedEventArgs)restored.Read(Input(session, 1).Input.Id).Effects.Single();
        var first = ((RawDebugSampleEventArgs)restored.Read(Input(session, 2).Input.Id).Effects.Single()).Sample;
        var last = ((RawDebugSampleEventArgs)restored.Read(Input(session, 20).Input.Id).Effects.Single()).Sample;
        Check(first.ControllerCatalog!.SequenceEqual(sample.ControllerCatalog) && ReferenceEquals(first.ControllerCatalog, last.ControllerCatalog) &&
            ReferenceEquals(first.ControllerCatalog, starts.InitialSamples[0].ControllerCatalog), "RAW sidecar recovery changed or repeatedly allocated immutable metadata.");
        restored.Commit(Input(session, 21), new object[] { EventArgs.Empty });
        restored.CollectBefore(new JournalCursor(1, 21));
        Check(Directory.GetFiles(root, "*.catalog").Length == 0 && restored.Enumerate().Single() == Input(session, 21).Input.Id,
            "RAW collection leaked unreferenced evidence or deleted the retained boundary.");
    }
    private static void ImportAndIntegrity(string root, TelemetrySample sample)
    {
        var source = new RawEffectStore(Path.Combine(root, "source")); var destination = new RawEffectStore(Path.Combine(root, "destination"));
        var input = Input(Guid.NewGuid(), 1); source.Commit(input, new object[] { new RawDebugSampleEventArgs(sample) });
        var hash = EpisodeTransactionStore.Hash(source.PathFor(input.Input.Id));
        destination.Import(source, input.Input.Id, hash); destination.Import(source, input.Input.Id, hash);
        var recovered = ((RawDebugSampleEventArgs)new RawEffectStore(destination.Root).Read(input.Input.Id).Effects.Single()).Sample;
        Check(recovered.ControllerCatalog!.SequenceEqual(sample.ControllerCatalog) && Directory.GetFiles(destination.Root, "*.catalog").Length == 1,
            "Cold recovery import lost or duplicated its referenced profile evidence.");
        var sidecar = Directory.GetFiles(destination.Root, "*.catalog").Single(); var corrupt = File.ReadAllBytes(sidecar); corrupt[corrupt.Length - 1] ^= 1; File.WriteAllBytes(sidecar, corrupt);
        Reject(() => new RawEffectStore(destination.Root).Read(input.Input.Id));
        File.Delete(sidecar); Reject(() => new RawEffectStore(destination.Root).Read(input.Input.Id));
    }
    private static void Quota(string root, TelemetrySample sample)
    {
        var store = new RawEffectStore(root, sample.ControllerCatalog!.Length - 1); var pending = new RawBatchPersistence(store);
        pending.Commit(Input(Guid.NewGuid(), 1), new object[] { new RawDebugSampleEventArgs(sample) }); pending.Flush();
        Check(pending.LastGap != null && Directory.GetFiles(root, "*.catalog").Length == 0,
            "Evidence sidecars bypassed the RAW quota or its explicit gap diagnostic.");
    }
    private static void ImportOverExistingLiveBatch(string root, TelemetrySample sample)
    {
        var input = Input(Guid.NewGuid(), 1); var effects = new object[] { new RawDebugSampleEventArgs(sample) };
        var live = new RawEffectStore(Path.Combine(root, "live")); var batch = new RawBatchPersistence(live);
        batch.Commit(input, effects); batch.Flush();
        var recovered = new RawEffectStore(Path.Combine(root, "recovered")); recovered.Commit(input, effects);
        var hash = EpisodeTransactionStore.Hash(recovered.PathFor(input.Input.Id));
        // Simulate a crash after the live RAW batch flush and before the recording
        // checkpoint: cold journal replay reconstructs the same effect as a .bin.
        live = new RawEffectStore(live.Root);
        live.Import(recovered, input.Input.Id, hash); live.Import(recovered, input.Input.Id, hash);
        Check(!File.Exists(live.PathFor(input.Input.Id)) && new RawEffectStore(live.Root).Enumerate().Single() == input.Input.Id,
            "Cold replay duplicated an effect already durable in a live batch.");
        var changed = new RawEffectStore(Path.Combine(root, "changed"));
        changed.Commit(input, new object[] { EventArgs.Empty });
        Reject(() => live.Import(changed, input.Input.Id, EpisodeTransactionStore.Hash(changed.PathFor(input.Input.Id))));
        Check(!File.Exists(live.PathFor(input.Input.Id)) && new RawEffectStore(live.Root).Read(input.Input.Id).Effects.Single() is RawDebugSampleEventArgs,
            "Conflicting cold replay damaged the committed live batch.");
    }
    private static void OrphanPublicationRecovery(string root, TelemetrySample sample)
    {
        foreach (var reopen in new[] { false, true })
        {
            var path = Path.Combine(root, reopen ? "restart" : "retry");
            var store = new RawEffectStore(path, sample.ControllerCatalog!.Length + 10000); var pending = new RawBatchPersistence(store); var session = Guid.NewGuid(); var first = Input(session, 1);
            var blocked = Path.Combine(path, first.Input.Id.ToString("N") + ".batch"); Directory.CreateDirectory(blocked);
            pending.Commit(first, new object[] { new RawDebugSampleEventArgs(sample) }); pending.Flush();
            Check(pending.LastGap != null && Directory.GetFiles(path, "*.catalog").Length == 1, "Fixture did not fail between sidecar and effect publication.");
            Directory.Delete(blocked);
            // A stranded 200KB catalog cannot consume the quota of the next 100KB store.
            if (reopen) { store = new RawEffectStore(path, sample.ControllerCatalog!.Length / 2); pending = new RawBatchPersistence(store); }
            // This production batch exceeds the 10KB remaining behind the orphan,
            // while fitting the quota once the abandoned diagnostic snapshot is reclaimed.
            for (var row = 2; row <= 21; row++) pending.Commit(Input(session, row), new object[] { new RawDebugSampleEventArgs(new TelemetrySample { Sequence = row }) });
            pending.Flush();
            Check(Directory.GetFiles(path, "*.catalog").Length == 0 && store.Enumerate().SequenceEqual(Enumerable.Range(2, 20).Select(row => Input(session, row).Input.Id)),
                "An orphan diagnostic catalog blocked RAW batch recovery before the first export cursor.");
        }
    }
    private static void Legacy(string root, TelemetrySample sample)
    {
        var store = new RawEffectStore(root); var input = Input(Guid.NewGuid(), 1); Directory.CreateDirectory(root);
        using (var file = File.Create(store.PathFor(input.Input.Id)))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write(0x31455752); writer.Write(input.Input.Id.ToByteArray()); writer.Write(1L); writer.Write(1L); writer.Write(1);
            writer.Write((byte)2); RuntimeCheckpointCodec.WriteSample(writer, sample);
        }
        var restored = ((RawDebugSampleEventArgs)store.Read(input.Input.Id).Effects.Single()).Sample;
        Check(restored.ControllerCatalog!.SequenceEqual(sample.ControllerCatalog!), "Legacy embedded-catalog RAW data is no longer readable.");
    }
    private static DurableRuntimeInput Input(Guid session, int sequence) => new DurableRuntimeInput(new RuntimeInput(StableRuntimeIdentity.Command(session, sequence),
        JournalRecordKind.RuntimeCommand, sequence, sequence, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new RecordingCommand(RecordingCommandKind.Tick).Encode()), new JournalCursor(1, sequence));
    private static void Reject(Action action) { try { action(); } catch (Exception error) when (error is IOException || error is InvalidDataException) { return; } throw new InvalidOperationException("Corrupt RAW evidence was accepted."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
