using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.TelemetryUpload;
using LandingStats.Archives;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ReplaySourcePersistenceRegressionTests
{
    internal static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "source-evidence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var samples = Enumerable.Range(0, 40).Select(i => new TelemetrySample { Sequence = i + 1,
                SimulationTimeSeconds = 20 + i * .05, HostElapsedSeconds = i * .05, MotionSimulation = true,
                SourceEvidence = i == 0 ? null : ReplaySourceCoreRegressionTests.Evidence(1000 + i * 50, 1050 + i * 50,
                    availability: i == 10 ? SourceObservationAvailability.Unavailable : SourceObservationAvailability.Available) }).ToArray();
            Persistence(root, samples); SourceJoinAndRecovery(); Transport(root, samples).GetAwaiter().GetResult();
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Same(IReadOnlyList<TelemetrySample> expected, IReadOnlyList<TelemetrySample> actual)
    {
        Check(TelemetryBinaryCodec.Fingerprint(expected) == TelemetryBinaryCodec.Fingerprint(actual), "Numeric frames changed with optional evidence.");
        Check(ReplaySourceTimelineCodec.Encode(expected)!.SequenceEqual(ReplaySourceTimelineCodec.Encode(actual)!), "Source observation bytes changed.");
    }
    private static void Persistence(string root, TelemetrySample[] samples)
    {
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer); using var reader = new BinaryReader(buffer);
        RuntimeCheckpointCodec.WriteSamples(writer, samples); buffer.Position = 0; Same(samples, RuntimeCheckpointCodec.ReadSamples(reader));
        buffer.SetLength(0); foreach (var sample in samples) RuntimeCheckpointCodec.WriteSample(writer, sample); buffer.Position = 0;
        Same(samples, samples.Select(_ => RuntimeCheckpointCodec.ReadSample(reader)!).ToArray());
        var preRoll = new PreRollBuffer(60, 1000); foreach (var sample in samples) preRoll.Add(sample, sample.HostElapsedSeconds);
        buffer.SetLength(0); preRoll.WriteCheckpoint(writer); buffer.Position = 0;
        var restored = new PreRollBuffer(60, 1000); restored.RestoreCheckpoint(reader); Same(samples, restored.ToArray());
        var episode = new LandingEpisodeEventArgs(1, samples, "MSFS", "Fixture", "type", "model", Array.Empty<AirportFacility>(),
            Array.Empty<string>(), persistentId: Guid.NewGuid());
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
        Same(samples, store.ReadInputs(store.CommitInputs(episode, DateTime.UtcNow, Array.Empty<AirportFacility>(), true, 1)).Episode.Samples);
        var record = new LandingRecord { SourceEvidenceTimeline = ReplaySourceTimelineCodec.EncodeText(samples),
            Series = samples.Select((_, i) => new LandingSeriesPoint { TimeSeconds = i * .05,
                RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(), RawControllerYAxisAgeSeconds = Array.Empty<double>() }).ToList() };
        var serialized = new DataContractJsonSerializer(typeof(LandingRecordFile)); buffer.SetLength(0);
        serialized.WriteObject(buffer, LandingRecordFile.FromRecord(record)); buffer.Position = 0;
        var restoredRecord = ((LandingRecordFile)serialized.ReadObject(buffer)!).ToRecord();
        Check(restoredRecord.SourceEvidenceTimeline == record.SourceEvidenceTimeline && LandingRepository.CreateSummary(record).SourceEvidenceTimeline == null,
            "History lost evidence or copied the detailed timeline into its eager index.");
        var input = new RuntimeInput(Guid.NewGuid(), JournalRecordKind.RuntimeCommand, 1000, 1000, DateTime.UtcNow,
            new RecordingCommand(RecordingCommandKind.Tick).Encode());
        var raw = new RawEffectStore(Path.Combine(root, "effects"));
        raw.Commit(new DurableRuntimeInput(input, new JournalCursor(1, 1)), new object[] { new RawDebugSampleEventArgs(samples[1]) });
        Same(new[] { samples[1] }, new[] { ((RawDebugSampleEventArgs)raw.Read(input.Id).Effects.Single()).Sample });
        var journal = new TelemetryDecisionJournal(); EpisodeProcessor.RecordSourceObservations(samples, journal, false);
        var decision = journal.Snapshot().Entries.Single();
        Check(decision.Code == "source_observations" && decision.Details["frames"] == 40 && decision.Details["unknown"] == 40,
            "Stationary freeze observations became confirmed replay in the production analysis journal.");
        journal = new TelemetryDecisionJournal(); EpisodeProcessor.RecordSourceObservations(new[] { new TelemetrySample() }, journal, true);
        Check(journal.Snapshot().Entries.Count == 0, "Old captures acquired inferred source evidence during analysis.");
    }
    private static void SourceJoinAndRecovery()
    {
        var session = Guid.NewGuid(); var clock = new RuntimeClockEpoch(Guid.NewGuid(), 1000, 1000);
        var identity = new RecoveryJournalIdentity(session, Guid.NewGuid(), clock);
        var state = new RecordingRuntimeState(identity); var prepare = new ControllerInputPreparation(new ControllerJoiner(clock));
        var worker = new WorkerIdentity(session, Guid.NewGuid(), 1, ComponentRole.Simulator); long order = 0, sequence = 0, connection = 1;
        RecordingRuntimeEffects Apply(JournalRecordKind kind, byte[] bytes)
        {
            order++; var input = new RuntimeInput(Guid.NewGuid(), kind, 1000 + order * 100, 1000 + order * 100, DateTime.UtcNow, bytes);
            return state.Apply(new DurableRuntimeInput(prepare.Prepare(input), new JournalCursor(1, order)));
        }
        RecordingRuntimeEffects Feed(RuntimePayload payload) => Apply(JournalRecordKind.SourceMessage,
            RuntimeWire.Encode(new RuntimeEnvelope(worker, payload is WorkerHello ? 0 : connection, ++sequence, 1000 + (order + 1) * 100, payload)));
        // Source joining is independent of capture policy. Frozen observations
        // can now be held before RAW while the next USER reply proves movement.
        TelemetrySample Frame() => Feed(new TelemetryFrameMessage(new TelemetrySample { Sequence = order, SimulationTimeSeconds = order,
            MotionSimulation = true, OnGround = true })).SourceSample ?? throw new InvalidOperationException("Joined source frame missing.");
        Feed(new WorkerHello("fixture", new string('a', 64), 1, clock.Id, 1000)); Feed(new LifecycleMessage(SourceLifecycle.Connected));
        Apply(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.RawCapture, true).Encode());
        Check(Frame().SourceEvidence!.Observation.FreezeMask == -1, "New connection fabricated initial flags.");
        Feed(new SourceObservationMessage(ReplaySourceCoreRegressionTests.Evidence().Observation)); var observed = 1000 + order * 100;
        var checkpoint = state.CaptureCheckpoint(); state = RecordingRuntimeState.RestoreCheckpoint(checkpoint, identity);
        Check(checkpoint.SequenceEqual(state.CaptureCheckpoint()), "Checkpoint changed source evidence state.");
        Feed(new LifecycleMessage(SourceLifecycle.PauseFlags, 0));
        var sample = Frame(); Check(sample.SourceEvidence!.ObservedQpc == observed && sample.SourceEvidence.Observation.FreezeMask == 7,
            "Recovery or an unrelated SDK event refreshed the optional observation.");
        Feed(new SourceObservationMessage(ReplaySourceCoreRegressionTests.Evidence(availability: SourceObservationAvailability.Unavailable).Observation));
        Check(Frame().SourceEvidence!.Observation.Availability == SourceObservationAvailability.Unavailable, "Optional failure erased the recording frame.");
        Feed(new LifecycleMessage(SourceLifecycle.Disconnected)); connection++;
        Feed(new LifecycleMessage(SourceLifecycle.Connected)); sample = Frame();
        Check(sample.SourceEvidence!.Connection == 2 && sample.SourceEvidence.Observation.FreezeMask == -1 && sample.SourceEvidence.SdkReplay == SdkReplayState.Unknown,
            "Reconnect inherited old flags or SDK replay state.");
        Apply(JournalRecordKind.SourceRetirement, new RuntimeSourceRetirement(worker, connection, sequence, SourceGapReason.ProcessExit, false).Encode());
        worker = new WorkerIdentity(session, Guid.NewGuid(), 2, ComponentRole.Simulator); sequence = 0; connection = 1;
        Feed(new WorkerHello("fixture", new string('a', 64), 1, clock.Id, 1000)); Feed(new LifecycleMessage(SourceLifecycle.Connected));
        sample = Frame(); Check(sample.SourceEvidence!.WorkerInstance == worker.InstanceId && sample.SourceEvidence.Generation == 2 &&
            sample.SourceEvidence.Observation.FreezeMask == -1, "Replacement worker inherited retired observations.");
    }
    private static async Task Transport(string root, TelemetrySample[] samples)
    {
        var archives = Path.Combine(root, "archives"); var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives);
        var chunks = new RawChunkStore(Path.Combine(root, "chunks"), archives, outbox, () => true);
        var path = chunks.Publish(Guid.NewGuid(), 1, new RawCaptureMetadata { TimestampUtcTicks = DateTime.UtcNow.Ticks, Simulator = "MSFS", Title = "Fixture" }, samples);
        using (var input = File.OpenRead(path)) using (var archive = new CaptureArchiveReader(input))
        {
            var copies = samples.Select(sample => TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(sample))).ToArray();
            Check(CaptureEvidenceFixture.ReadSource(archive, copies), "RAW archive lost source observations."); Same(samples, copies);
        }
        var original = File.ReadAllBytes(path); var receiver = new Receiver();
        using var client = new TelemetryUploadClient(Path.Combine(root, "queue"), receiver, Path.Combine(root, "identity"), startQueueWorker: false);
        Check((await client.SendClaimAsync(path, CancellationToken.None)).Disposition == TelemetryUploadClient.UploadDisposition.Deferred && receiver.Uploads == 0,
            "Unsupported source metadata reached an old receiver.");
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "Capability wait changed archive bytes.");
        // A receiver without the new capability must still accept every legacy CSV format.
        foreach (var schema in new[] { 5, 6, 7 })
        {
            var old = Path.Combine(root, "legacy-" + schema + ".zip");
            using (var output = File.Create(old)) using (var zip = new CaptureArchiveWriter(output, false))
            {
                using (var text = new StreamWriter(zip.CreateEntry("session.txt"))) text.Write("telemetry_schema=" + schema + "\nsource_period=SIM_FRAME\nsample_count=1\n");
                using (var text = new StreamWriter(zip.CreateEntry("telemetry.csv")))
                {
                    var header = schema == 5 ? TelemetryCsv.V5Header : schema == 6 ? TelemetryCsv.V6Header : TelemetryCsv.Header;
                    var fields = TelemetryCsv.Header.Split(',').Zip(TelemetryCsv.Format(samples[0]).Split(','), (name, value) => new { name, value }).ToDictionary(pair => pair.name, pair => pair.value);
                    text.WriteLine(header); text.WriteLine(string.Join(",", header.Split(',').Select(name => fields[name])));
                }
            }
            var bytes = File.ReadAllBytes(old);
            Check((await client.SendClaimAsync(old, CancellationToken.None)).Disposition == TelemetryUploadClient.UploadDisposition.Uploaded && bytes.SequenceEqual(receiver.Bytes!),
                "Legacy schema " + schema + " was blocked by the new capability or rewritten.");
        }
        receiver.Supported = true;
        Check((await client.SendClaimAsync(path, CancellationToken.None)).Disposition == TelemetryUploadClient.UploadDisposition.Uploaded && original.SequenceEqual(receiver.Bytes!),
            "Newly supported receiver did not receive the same pending bytes.");
        var fixture = Environment.GetEnvironmentVariable("MSFSLS_SOURCE_FIXTURE"); if (!string.IsNullOrEmpty(fixture)) File.WriteAllBytes(fixture, original);
    }
    private sealed class Receiver : HttpMessageHandler
    {
        internal bool Supported; internal int Uploads; internal byte[]? Bytes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/config")) return Reply(HttpStatusCode.OK,
                "{\"protocol\":1,\"registration_mode\":\"open\",\"telemetry_schema\":7,\"telemetry_schemas\":[5,6,7],\"archive_formats\":[1,2],\"source_state_formats\":" + (Supported ? "[1]" : "[]") + "}");
            if (request.RequestUri.AbsolutePath.EndsWith("/enroll")) return Reply(HttpStatusCode.Created, "{}");
            if (request.RequestUri.AbsolutePath.EndsWith("/captures")) { Uploads++; Bytes = await request.Content!.ReadAsByteArrayAsync(); return Reply(HttpStatusCode.Created, "{\"status\":\"accepted\",\"capture_id\":\"0123456789abcdef0123456789abcdef\"}"); }
            throw new InvalidOperationException("Unexpected fixture HTTP request.");
        }
        private static HttpResponseMessage Reply(HttpStatusCode status, string body) => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
