using System;
using System.IO;
using System.Linq;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.Archives;
using LandingStats.App.Runtime;
using LandingStats.App.TelemetryUpload;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class DynamicControllerDataRegressionTests
{
    internal static void RunAll()
    {
        foreach (var count in new[] { 0, 1, 9, 17 })
        foreach (var stride in new[] { 6, 8 }) RoundTrip(count, stride);
        var legacy = new TelemetrySample { Sequence = 42 };
        var encoded = TelemetryBinaryCodec.Encode(legacy);
        Check(encoded.Length == TelemetryBinaryCodec.EncodedSize && encoded[0] == 1, "Historical binary contract changed.");
        Check(TelemetryCsv.TryParse(GeometryFingerprintCsvV7.Format(legacy), out var restored) && restored.Sequence == 42, "Historical CSV v7 no longer reads.");
        var malformed = ControllerSampleCodec.Encode(legacy); malformed[2] = 255; malformed[3] = 255; malformed[4] = 255; malformed[5] = 127;
        Reject(() => ControllerSampleCodec.Apply(new TelemetrySample(), malformed));
        var invalid = TelemetryCsv.Format(legacy).Split(','); invalid[invalid.Length - 1] = Convert.ToBase64String(malformed);
        Check(!TelemetryCsv.TryParse(string.Join(",", invalid), out _), "CSV accepted dimensions exceeding its payload.");
        LargeControllerMetadataSurvivesArchives();
        RawChunksRespectDecodedBytesAndRecover();
    }
    private static void RoundTrip(int count, int stride)
    {
        var sample = new TelemetrySample { Sequence = 9, SimulationTimeSeconds = 42.25 };
        sample.ResizeControllers(count, stride);
        for (var slot = 0; slot < count; slot++)
        {
            sample.RawControllerDeviceId[slot] = 100 + slot;
            sample.RawControllerYAxisPercent[slot] = slot + .25; sample.RawControllerYAxisValid[slot] = true; sample.RawControllerYAxisAgeSeconds[slot] = .02;
            for (var axis = 0; axis < stride; axis++)
            {
                var offset = slot * stride + axis;
                sample.RawControllerAxesPercent[offset] = slot * 3 + axis * .25;
                sample.RawControllerAxesValid[offset] = axis % 2 == 0; sample.RawControllerAxesAgeSeconds[offset] = .01;
            }
        }
        var binary = TelemetryBinaryCodec.Encode(sample);
        Same(sample, TelemetryBinaryCodec.Decode(binary));
        Check(TelemetryCsv.TryParse(TelemetryCsv.Format(sample), out var csv), "Dynamic CSV could not be parsed."); Same(sample, csv);
        Check(TelemetryBinaryCodec.Fingerprint(new[] { sample }) == TelemetryBinaryCodec.Fingerprint(new[] { sample.LegacyControllerProjection() }), "Frozen geometry projection changed.");
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
        var descriptors = Enumerable.Range(0, count).Select(slot => new ControllerDescriptor(slot, slot, "Controller " + slot, "product", (byte)((1 << stride) - 1),
            new uint[stride], Enumerable.Repeat(65535u, stride).ToArray(), Guid.NewGuid(), Guid.NewGuid())).ToArray();
        var bindings = count == 0 ? Array.Empty<ControllerBinding>() : new[] {
            new ControllerBinding(ControllerControlKind.Throttle, 0, descriptors[count - 1].InstanceId, stride - 1, true, "profile"),
            new ControllerBinding(ControllerControlKind.Throttle, 1, descriptors[count - 1].InstanceId, stride - 1, true, "profile") };
        var catalog = new RuntimeEnvelope(worker, 0, 1, 100, new ControllerCatalogMessage(1, descriptors, bindings, "aircraft/config"));
        sample.ControllerCatalog = ControllerProvenanceCodec.Encode(catalog);
        var archived = (ControllerCatalogMessage)ControllerProvenanceCodec.Decode(sample.ControllerCatalog)!.Payload;
        Check(archived.Devices.Count == count && archived.Bindings.Count == bindings.Length, "Catalog or shared throttle binding lost.");
        if (count > 0) Check(archived.Devices[count - 1].InstanceId == descriptors[count - 1].InstanceId && archived.Devices[count - 1].Supports(stride - 1), "Last device/axis identity lost.");
        var clocks = Enumerable.Repeat(100L, count).ToArray();
        var snapshot = new ControllerSnapshotMessage(1, clocks, sample.RawControllerAxesPercent, sample.RawControllerAxesValid, stride);
        var wire = RuntimeWire.Decode(RuntimeWire.Encode(new RuntimeEnvelope(worker, 0, 2, 100, snapshot)));
        var reread = (ControllerSnapshotMessage)wire.Payload;
        Check(reread.DeviceCount == count && reread.AxisCount == stride, "Wire dimensions lost.");
        if (count > 0) Check(reread.Value(count - 1, stride - 1) == sample.RawControllerAxesPercent[count * stride - 1], "Wire truncated the last axis.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        RuntimeCheckpointCodec.WriteSamples(writer, new[] { sample, new TelemetrySample(), sample });
        stream.Position = 0; using var reader = new BinaryReader(stream);
        var frames = RuntimeCheckpointCodec.ReadSamples(reader);
        Same(sample, frames[0]); Same(sample, frames[2]);
        Check(frames[0].ControllerCatalog!.SequenceEqual(sample.ControllerCatalog), "Checkpoint dropped catalog bindings.");
        var timeline = ControllerCatalogTimeline.Capture(new[] { sample, sample })!;
        timeline.Validate(2);
        var record = new LandingRecord { ControllerAxisCount = stride };
        using var json = new MemoryStream();
        var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(LandingRecord)); serializer.WriteObject(json, record); json.Position = 0;
        Check(((LandingRecord)serializer.ReadObject(json)!).ControllerAxisCount == stride, "History lost its axis layout.");
        if (count == 17 && stride == 8) ExportFixture(sample, worker, descriptors, bindings);
    }
    private static void ExportFixture(TelemetrySample sample, WorkerIdentity worker, ControllerDescriptor[] devices, ControllerBinding[] bindings)
    {
        var path = Environment.GetEnvironmentVariable("MSFSLS_DYNAMIC_CONTROLLER_FIXTURE");
        if (string.IsNullOrEmpty(path)) return;
        var metadata = new ControllerCatalogMessage(1, devices, bindings.Concat(new[] {
            new ControllerBinding(ControllerControlKind.Pitch, -1, Guid.NewGuid(), 1, false, "Disconnected stick profile") }), "SimObjects/Airplanes/Fixture/config", "{\"schema\":1,\"availableProfiles\":[\"Fixture\"],\"selectedProfiles\":[\"Fixture\"],\"readErrors\":[],\"devices\":[{\"instanceId\":\"" + devices[0].InstanceId + "\"}],\"diagnostics\":\"" + new string('x', 200000) + "\"}");
        var catalog = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(worker, 0, 1, 100, metadata));
        var samples = Enumerable.Range(0, 3).Select(row =>
        {
            var value = TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(sample));
            value.Sequence = row + 1; value.SimulationTimeSeconds = 42.25 + row * .05; value.HostElapsedSeconds = row * .05; value.MotionSimulation = true;
            value.ControllerCatalog = catalog;
            for (var slot = 0; slot < value.ControllerCount; slot++)
            {
                value.RawControllerDeviceId[slot] = slot;
                for (var axis = 0; axis < value.ControllerAxisCount; axis++) value.RawControllerAxesValid[slot * value.ControllerAxisCount + axis] = true;
                value.RawControllerYAxisPercent[slot] = value.RawControllerAxesPercent[slot * value.ControllerAxisCount + 1];
                value.RawControllerYAxisAgeSeconds[slot] = value.RawControllerAxesAgeSeconds[slot * value.ControllerAxisCount + 1];
            }
            return value;
        }).ToArray();
        using var file = File.Create(path); using var archive = new CaptureArchiveWriter(file, false);
        using (var writer = new StreamWriter(archive.CreateEntry("session.txt"))) writer.Write("telemetry_schema=" + TelemetryCsv.SchemaVersion + "\nsource_period=SIM_FRAME\nsample_count=3\n");
        using (var writer = new StreamWriter(archive.CreateEntry("telemetry.csv")))
        { writer.WriteLine(TelemetryCsv.Header); foreach (var value in samples) writer.WriteLine(TelemetryCsv.Format(value)); }
        ControllerCatalogArchive.Write(archive, samples);
    }
    private static void Same(TelemetrySample expected, TelemetrySample actual)
    {
        Check(expected.ControllerCount == actual.ControllerCount && expected.ControllerAxisCount == actual.ControllerAxisCount, "Controller dimensions changed.");
        Check(expected.RawControllerDeviceId.SequenceEqual(actual.RawControllerDeviceId) &&
            expected.RawControllerAxesPercent.SequenceEqual(actual.RawControllerAxesPercent) && expected.RawControllerAxesValid.SequenceEqual(actual.RawControllerAxesValid) &&
            expected.RawControllerAxesAgeSeconds.SequenceEqual(actual.RawControllerAxesAgeSeconds) &&
            expected.RawControllerYAxisPercent.SequenceEqual(actual.RawControllerYAxisPercent), "Physical observations changed.");
    }
    private static void LargeControllerMetadataSurvivesArchives()
    {
        var root = Path.Combine(Path.GetTempPath(), "controller-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
            var devices = Enumerable.Range(0, 128).Select(slot => new ControllerDescriptor(slot, slot,
                string.Concat(Enumerable.Range(0, 16).Select(_ => Guid.NewGuid().ToString("N"))), "product", 255,
                new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), Guid.NewGuid())).ToArray();
            var catalog = new RuntimeEnvelope(worker, 0, 1, 100, new ControllerCatalogMessage(1, devices));
            var sources = ControllerCatalogTimeline.Labels(catalog, legacyDescriptions: true).ToArray();
            var sample = new TelemetrySample { Sequence = 1, ControllerCatalog = ControllerProvenanceCodec.Encode(catalog) };
            sample.ResizeControllers(devices.Length, 8);
            var samples = new[] { sample }; var utc = DateTime.UtcNow;
            foreach (var zstd in new[] { false, true })
            {
                var directory = Path.Combine(root, zstd ? "zstd" : "legacy");
                var archives = Path.Combine(directory, "archives");
                var chunks = new RawChunkStore(Path.Combine(directory, "chunks"), archives,
                    new DurableOutbox(Path.Combine(directory, "outbox"), archives), () => zstd);
                var raw = chunks.Publish(Guid.NewGuid(), 1, new RawCaptureMetadata
                    { TimestampUtcTicks = utc.Ticks, Simulator = "MSFS", Title = "Fixture", Sources = sources }, samples);
                var reports = new BugReportRepository(Path.Combine(directory, "reports"), () => zstd);
                var report = reports.TryCreateAutomatic(new BugReportCandidate(1, samples, "MSFS", "Fixture", "Type", "Model",
                    sources, Array.Empty<LandingRecord>()), utc, long.MaxValue)!;
                foreach (var path in new[] { raw, report })
                {
                    Check(TelemetryUploadClient.ReadCaptureSchema(path) == TelemetryCsv.SchemaVersion,
                        "A legal dynamic controller catalog cannot pass upload schema detection.");
                    using var input = File.OpenRead(path); using var archive = new CaptureArchiveReader(input);
                    Check(archive.GetEntry("session.txt")!.Length > 16384, "Fixture did not exceed the historical session bound.");
                    using var reader = new StreamReader(archive.Open("session.txt"));
                    var text = reader.ReadToEnd();
                    Check(sources.All(source => text.Contains(source)), "Controller source labels were truncated in the archive.");
                }
                var effects = new RawEffectStore(Path.Combine(directory, "effects")); var session = Guid.NewGuid();
                DurableRuntimeInput Input(int sequence) => new DurableRuntimeInput(new RuntimeInput(StableRuntimeIdentity.Command(session, sequence),
                    JournalRecordKind.RuntimeCommand, sequence, sequence, utc, new RecordingCommand(RecordingCommandKind.Tick).Encode()), new JournalCursor(1, sequence));
                effects.Commit(Input(1), new object[] { new RawDebugCaptureStartedEventArgs(samples, utc, "MSFS", "Fixture", "Type", "Model", sources) });
                var published = 0;
                new RawExportOwner(effects, chunks, (_, _, _) => published++, 1).Advance();
                Check(new FileInfo(Path.Combine(effects.Root, "export.json")).Length > 65536, "Fixture did not exceed the historical export checkpoint bound.");
                var recovered = new RawExportOwner(new RawEffectStore(effects.Root), chunks, (_, _, _) => published++, 1);
                recovered.Advance();
                effects.Commit(Input(2), new object[] { EventArgs.Empty });
                // The restarted store reloads the newly committed stop from disk.
                recovered = new RawExportOwner(new RawEffectStore(effects.Root), chunks, (_, _, _) => published++, 1);
                recovered.Advance();
                Check(published == 1 && recovered.CaughtUp, "Large controller metadata prevented RAW recovery or republished a completed chunk.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static void RawChunksRespectDecodedBytesAndRecover()
    {
        var witness = new TelemetrySample(); witness.ResizeControllers(40, 8);
        var rowBytes = Encoding.UTF8.GetByteCount(TelemetryCsv.Format(witness)) + Environment.NewLine.Length;
        Check((long)rowBytes * RawCaptureRepository.ChunkMaximumSamples > CaptureArchiveFormat.MaximumDecodedBytes,
            "The real forty-controller CSV rows no longer reproduce the oversized fixed-count RAW chunk.");
        Console.WriteLine("RAW size witness: 40 controllers, " + rowBytes + " CSV bytes/row, " +
            (long)rowBytes * RawCaptureRepository.ChunkMaximumSamples + " bytes for the historical 30000-row chunk.");
        var root = Path.Combine(Path.GetTempPath(), "controller-chunks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new RawEffectStore(Path.Combine(root, "raw")); var archives = Path.Combine(root, "archives");
            var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives);
            var chunks = new RawChunkStore(Path.Combine(root, "chunks"), archives, outbox);
            var utc = DateTime.UtcNow; var session = Guid.NewGuid();
            DurableRuntimeInput Input(int sequence) => new DurableRuntimeInput(new RuntimeInput(StableRuntimeIdentity.Command(session, sequence),
                JournalRecordKind.RuntimeCommand, sequence, sequence, utc, new RecordingCommand(RecordingCommandKind.Tick).Encode()), new JournalCursor(1, sequence));
            var samples = Enumerable.Range(1, 12).Select(index =>
            { var value = new TelemetrySample { Sequence = index, SimulationTimeSeconds = index }; value.ResizeControllers(128, 8); return value; }).ToArray();
            store.Commit(Input(1), new object[] { new RawDebugCaptureStartedEventArgs(samples, utc, "MSFS", "Fixture", "Type", "Model", Array.Empty<string>()) });
            store.Commit(Input(2), new object[] { EventArgs.Empty });
            var maximum = CaptureArchiveFormat.MaximumSessionMetadataBytes + ControllerCatalogArchive.MaximumBytes + 128 * 1024L;
            // Published under an earlier boundary before its export checkpoint:
            // retry must honor these five rows even if today's byte planner would split sooner.
            chunks.Publish(StableRuntimeIdentity.Command(Input(1).Input.Id, 0), 1,
                new RawCaptureMetadata { TimestampUtcTicks = utc.Ticks, Simulator = "MSFS", Title = "Fixture", Type = "Type", Model = "Model" }, samples.Take(5).ToArray());
            var failed = false;
            try
            {
                new RawExportOwner(store, chunks, (_, _, _) => { failed = true; throw new IOException("Simulated interruption after first chunk checkpoint."); },
                    maximumChunkBytes: maximum).Advance();
            }
            catch (IOException) when (failed) { }
            Check(failed, "Byte-bound RAW fixture did not publish a partial pre-roll chunk.");
            var first = outbox.Enumerate().Single(); var firstHash = outbox.Read(first).Hash;
            new RawExportOwner(new RawEffectStore(store.Root), chunks, (_, _, _) => { }, maximumChunkBytes: maximum).Advance();
            var entries = outbox.Enumerate().Select(outbox.Read).ToArray();
            Check(entries.Length > 1 && outbox.Read(first).Hash == firstHash, "Byte-bound recovery changed the first immutable chunk or failed to split the stream.");
            var restored = new System.Collections.Generic.List<TelemetrySample>();
            foreach (var entry in entries)
            {
                using var file = File.OpenRead(entry.Path); using var archive = new CaptureArchiveReader(file);
                Check(archive.Entries.Sum(value => value.Length) <= maximum, "Published RAW chunk exceeds its decoded byte budget.");
                using var reader = new StreamReader(archive.Open("telemetry.csv")); Check(reader.ReadLine() == TelemetryCsv.Header, "Unexpected RAW header.");
                string? line; while ((line = reader.ReadLine()) != null)
                { Check(TelemetryCsv.TryParse(line, out var sample), "Byte-bound RAW row cannot be parsed."); restored.Add(sample); }
            }
            Check(restored.OrderBy(value => value.Sequence).Select(TelemetryCsv.Format).SequenceEqual(samples.Select(TelemetryCsv.Format)),
                "Byte-bound RAW recovery lost, duplicated or changed a dynamic-controller sample.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Malformed controller data accepted."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
