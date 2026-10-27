using LandingStats.Archives;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.TelemetryUpload;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ControllerProvenanceRegressionTests
{
    internal static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "catalogs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var samples = Samples();
            Checkpoint(samples);
            History(samples);
            Transactions(root, samples);
            ArchivesAndCapability(root, samples).GetAwaiter().GetResult();
        }
        finally { Directory.Delete(root, true); }
    }

    private static TelemetrySample[] Samples()
    {
        var session = Guid.NewGuid();
        byte[] Catalog(long generation, string name) => RuntimeWire.Encode(new RuntimeEnvelope(
            new WorkerIdentity(session, Guid.NewGuid(), generation, ComponentRole.Controllers), 0, 2, 100,
            new ControllerCatalogMessage(1, new[] { new ControllerDescriptor(3, 7, name, "driver-model", 63,
                new uint[6], Enumerable.Repeat(65535u, 6).ToArray()) })));
        var first = Catalog(1, "Stick A"); var second = Catalog(2, "Yoke B");
        return Enumerable.Range(0, 140).Select(i =>
        {
            var value = Math.Sin(i * .31) * 30 + Math.Cos(i * .17) * 7;
            var sample = new TelemetrySample { Sequence = i + 1, SimulationTimeSeconds = 100 + i * .05, HostElapsedSeconds = i * .05,
                MotionSimulation = true, ControllerCatalog = i < 40 ? first : i < 60 ? Array.Empty<byte>() : second,
                PilotPitchInputPercent = i < 40 ? value : 0, PilotRollInputPercent = i >= 60 ? value : 0 };
            sample.RawControllerAxesPercent[19] = value; sample.RawControllerAxesValid[19] = i < 40 || i >= 60;
            sample.RawControllerAxesAgeSeconds[19] = .01;
            return sample;
        }).ToArray();
    }
    private static byte[] Encode(IReadOnlyList<TelemetrySample> samples)
    { using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer); RuntimeCheckpointCodec.WriteSamples(writer, samples); return buffer.ToArray(); }
    private static TelemetrySample[] Decode(byte[] bytes)
    { using var buffer = new MemoryStream(bytes); using var reader = new BinaryReader(buffer); return RuntimeCheckpointCodec.ReadSamples(reader); }
    private static void Same(IReadOnlyList<TelemetrySample> expected, IReadOnlyList<TelemetrySample> actual)
    {
        Check(expected.Count == actual.Count, "Frame count changed.");
        for (var i = 0; i < expected.Count; i++)
            Check(TelemetryBinaryCodec.Encode(expected[i]).SequenceEqual(TelemetryBinaryCodec.Encode(actual[i])) &&
                ControllerProvenanceCodec.BytesComparer.Instance.Equals(Canonical(expected[i].ControllerCatalog), Canonical(actual[i].ControllerCatalog)), "A frame or catalog changed at " + i);
    }
    private static byte[]? Canonical(byte[]? catalog) => catalog == null || catalog.Length == 0 ? catalog : ControllerProvenanceCodec.Encode(ControllerProvenanceCodec.Decode(catalog)!);
    private static void Checkpoint(TelemetrySample[] samples)
    {
        Same(samples, Decode(Encode(samples)));
        var preRoll = new PreRollBuffer(60, 1000);
        foreach (var sample in samples) preRoll.Add(sample, sample.HostElapsedSeconds);
        using var preRollBytes = new MemoryStream(); using var preRollWriter = new BinaryWriter(preRollBytes);
        preRoll.WriteCheckpoint(preRollWriter); preRollBytes.Position = 0;
        var resumedPreRoll = new PreRollBuffer(60, 1000); using var preRollReader = new BinaryReader(preRollBytes);
        resumedPreRoll.RestoreCheckpoint(preRollReader); Same(samples, resumedPreRoll.ToArray());
        var mixed = new[] { samples[0], new TelemetrySample(), samples[40], samples[60] };
        Same(mixed, Decode(Encode(mixed)));
        using var legacy = new MemoryStream(); using var writer = new BinaryWriter(legacy);
        writer.Write(1); writer.Write(TelemetryBinaryCodec.Encode(samples[0]));
        Check(Decode(legacy.ToArray())[0].ControllerCatalog == null, "Legacy frames acquired fabricated provenance.");
        using var singles = new MemoryStream(); using var singleWriter = new BinaryWriter(singles);
        foreach (var sample in mixed) RuntimeCheckpointCodec.WriteSample(singleWriter, sample);
        singles.Position = 0; using var reader = new BinaryReader(singles);
        Same(mixed, mixed.Select(_ => RuntimeCheckpointCodec.ReadSample(reader)!).ToArray());
        var corrupt = Encode(samples); corrupt[8] = 255; corrupt[9] = 255; corrupt[10] = 255; corrupt[11] = 127;
        Reject(() => Decode(corrupt)); Reject(() => Decode(Encode(samples).Take(100).ToArray()));
        var wrong = RuntimeWire.Encode(new RuntimeEnvelope(new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Simulator),
            1, 1, 1, new GuardFrameMessage(1, 100, false, 0, true)));
        Reject(() => ControllerProvenanceCodec.Decode(wrong));
        Check(Encode(samples).Length < samples.Length * (TelemetryBinaryCodec.EncodedSize + 8) + 2048, "Checkpoint repeated the entire catalog for each frame.");
    }
    private static void History(TelemetrySample[] samples)
    {
        var record = new LandingRecord { ControllerTimeline = ControllerCatalogTimeline.Capture(samples) };
        record.RawControllerAxesSourceIndices.Add(19);
        record.Series = samples.Select((sample, i) => new LandingSeriesPoint { TimeSeconds = i * .05,
            PilotPitchPercent = sample.PilotPitchInputPercent, PilotRollPercent = sample.PilotRollInputPercent,
            RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(), RawControllerYAxisAgeSeconds = Array.Empty<double>(),
            RawControllerAxesPercent = new[] { sample.RawControllerAxesPercent[19] },
            RawControllerAxesValid = new[] { sample.RawControllerAxesValid[19] }, RawControllerAxesAgeSeconds = new[] { .01 } }).ToList();
        var restored = LandingRecordFile.FromRecord(record).ToRecord();
        Check(LandingRepository.CreateSummary(record).ControllerTimeline == null, "History index must not eagerly load detailed catalog metadata.");
        Check(restored.ControllerTimeline!.Transitions.Select(value => value[0]).SequenceEqual(new[] { 0, 40, 60 }), "History lost catalog transitions.");
        var inputs = ReplayControlInputs.Resolve(restored);
        Check(inputs.Sample(.5).PitchPercent.HasValue && inputs.Pitch!.SourceLabel.StartsWith("Stick A") && inputs.Roll == null, "Original stick binding was not scoped to its catalog.");
        Check(!inputs.Sample(2.5).PitchPercent.HasValue && inputs.Pitch == null && inputs.Roll == null, "Unavailable source reused old axes.");
        Check(inputs.Sample(3.5).RollPercent.HasValue && inputs.Roll!.SourceLabel.StartsWith("Yoke B") && inputs.Pitch == null, "Replacement source inherited the previous device's binding.");
        Check(!inputs.Sample(2.975).RollPercent.HasValue && !inputs.Sample(1.975).PitchPercent.HasValue, "Interpolation crossed a controller-generation boundary.");
        Check(inputs.Sample(.5).PitchPercent.HasValue && inputs.Pitch!.SourceLabel.StartsWith("Stick A"), "Backward seek failed to restore the original binding.");
        var invalid = restored.ControllerTimeline.Copy(); invalid.Transitions[1][0] = 0;
        Reject(() => invalid.Validate(samples.Length));
        invalid = restored.ControllerTimeline.Copy(); invalid.Transitions[1][1] = 99;
        Reject(() => invalid.Validate(samples.Length));
    }
    private static void Transactions(string root, TelemetrySample[] samples)
    {
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
        var episode = new LandingEpisodeEventArgs(1, samples, "MSFS", "Fixture", "type", "model", Array.Empty<AirportFacility>(),
            Array.Empty<string>(), persistentId: Guid.NewGuid());
        var manifest = store.CommitInputs(episode, DateTime.UtcNow, Array.Empty<AirportFacility>(), true, 1);
        Same(samples, store.ReadInputs(manifest).Episode.Samples);
        foreach (var format2 in new[] { false, true })
        {
            var directory = Path.Combine(root, format2 ? "archive-format2" : "archive-zip");
            var archives = Path.Combine(directory, "archives"); var outbox = new DurableOutbox(Path.Combine(directory, "outbox"), archives);
            var chunks = new RawChunkStore(Path.Combine(directory, "chunks"), archives, outbox, () => format2);
            var path = chunks.Publish(Guid.NewGuid(), 1, new RawCaptureMetadata { TimestampUtcTicks = DateTime.UtcNow.Ticks, Simulator = "MSFS", Title = "Fixture" }, samples);
            using var file = File.OpenRead(path); using var zip = new CaptureArchiveReader(file);
            var copies = samples.Select(sample => TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(sample))).ToArray();
            Check(CaptureEvidenceFixture.ReadControllers(zip, copies), "RAW publication omitted controller provenance."); Same(samples, copies);
        }
    }
    private static async Task ArchivesAndCapability(string root, TelemetrySample[] samples)
    {
        var path = Path.Combine(root, "fixture_raw.zip");
        using (var file = File.Create(path))
        using (var zip = new CaptureArchiveWriter(file, false))
        {
            ControllerCatalogArchive.Write(zip, samples);
            using (var writer = new StreamWriter(zip.CreateEntry("telemetry.csv")))
            { writer.WriteLine(TelemetryCsv.Header); foreach (var sample in samples) writer.WriteLine(TelemetryCsv.Format(sample)); }
            using (var writer = new StreamWriter(zip.CreateEntry("session.txt")))
                writer.Write("telemetry_schema=" + TelemetryCsv.SchemaVersion + "\nsource_period=SIM_FRAME\nsample_count=" + samples.Length + "\n");
        }
        var original = File.ReadAllBytes(path);
        var handler = new Receiver();
        using var client = new TelemetryUploadClient(Path.Combine(root, "queue"), handler, Path.Combine(root, "identity"), startQueueWorker: false);
        Check((await client.SendClaimAsync(path, CancellationToken.None)).Disposition == TelemetryUploadClient.UploadDisposition.Deferred && handler.Uploads == 0,
            "An old receiver must not receive/reject the new archive metadata.");
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Capability wait rewrote or discarded evidence.");
        handler.Supported = true;
        Check((await client.SendClaimAsync(path, CancellationToken.None)).Disposition == TelemetryUploadClient.UploadDisposition.Uploaded &&
            handler.Uploads == 1 && handler.Bytes!.SequenceEqual(original), "Delivery did not resume with the exact same bytes after receiver support appeared.");
        var fixture = Environment.GetEnvironmentVariable("MSFSLS_PROVENANCE_FIXTURE");
        if (!string.IsNullOrEmpty(fixture)) File.WriteAllBytes(fixture, original);
    }
    private sealed class Receiver : HttpMessageHandler
    {
        internal bool Supported; internal int Uploads; internal byte[]? Bytes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/config")) return Reply(HttpStatusCode.OK,
                "{\"protocol\":1,\"registration_mode\":\"open\",\"telemetry_schema\":" + TelemetryCsv.SchemaVersion + ",\"controller_catalog_formats\":" + (Supported ? "[1]" : "[]") + "}");
            if (request.RequestUri.AbsolutePath.EndsWith("/enroll")) return Reply(HttpStatusCode.Created, "{}");
            if (request.RequestUri.AbsolutePath.EndsWith("/captures")) { Uploads++; Bytes = await request.Content!.ReadAsByteArrayAsync(); return Reply(HttpStatusCode.Created, "{\"status\":\"accepted\",\"capture_id\":\"0123456789abcdef0123456789abcdef\"}"); }
            throw new InvalidOperationException("Unexpected fixture HTTP request.");
        }
        private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } catch (EndOfStreamException) { return; } throw new InvalidOperationException("Invalid provenance was accepted."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
