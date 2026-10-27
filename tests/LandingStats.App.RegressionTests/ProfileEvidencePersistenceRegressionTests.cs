using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.Archives;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ProfileEvidencePersistenceRegressionTests
{
    internal static void RunAll()
    {
        OptionalFooterCompatibility();
        RecordingEnrichmentAndRecovery();
        OversizedEvidenceKeepsPhysicalRecording();
    }
    private static void OptionalFooterCompatibility()
    {
        var simulator = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Simulator);
        var source = new AircraftControlsMessage("aircraft", "context", Array.Empty<ControllerBinding>());
        var legacy = RuntimeWire.Encode(new RuntimeEnvelope(simulator, 1, 1, 1000, source));
        Check(((AircraftControlsMessage)RuntimeWire.Decode(legacy).Payload).ProfileEvidence == "", "Old profile message acquired evidence.");
        var evidence = "{\"readErrors\":[\"profile unavailable\"],\"availableProfiles\":[\"Профиль\"]}";
        var current = RuntimeWire.Encode(new RuntimeEnvelope(simulator, 1, 1, 1000,
            new AircraftControlsMessage(source.AircraftPath, source.Context, source.Bindings, evidence)));
        Check(((AircraftControlsMessage)RuntimeWire.Decode(current).Payload).ProfileEvidence == evidence, "Profile evidence changed on the wire.");
        var marker = legacy.Length;
        Check(BitConverter.ToUInt32(current, marker) == 0x31464550, "Evidence extension changed its PEF1 contract.");
        var corrupt = (byte[])current.Clone(); corrupt[marker] ^= 1; Reject(() => RuntimeWire.Decode(corrupt));
        corrupt = (byte[])current.Clone(); Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, corrupt, marker + 4, 4); Reject(() => RuntimeWire.Decode(corrupt));
        corrupt = (byte[])current.Clone(); corrupt[marker + 8] = 0xff; Reject(() => RuntimeWire.Decode(corrupt));

        var worker = new WorkerIdentity(simulator.AppSessionId, Guid.NewGuid(), 1, ComponentRole.Controllers);
        var catalog = new ControllerCatalogMessage(1, Array.Empty<ControllerDescriptor>(), bindingContext: "context");
        var bytes = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(worker, 0, 1, 1000, catalog));
        Check(((ControllerCatalogMessage)ControllerProvenanceCodec.Decode(bytes)!.Payload).ProfileEvidence == "", "Pre-evidence LCC2 no longer reads.");
        var extended = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(worker, 0, 1, 1000,
            new ControllerCatalogMessage(1, catalog.Devices, bindingContext: "context", profileEvidence: evidence)));
        Check(extended.Take(bytes.Length).SequenceEqual(bytes), "Evidence modified the existing LCC2 prefix.");
        Check(((ControllerCatalogMessage)ControllerProvenanceCodec.Decode(extended)!.Payload).ProfileEvidence == evidence, "Archived evidence changed.");
        corrupt = extended.Concat(new byte[] { 0 }).ToArray(); Reject(() => ControllerProvenanceCodec.Decode(corrupt));
    }
    private static void RecordingEnrichmentAndRecovery()
    {
        var session = Guid.NewGuid(); var clock = new RuntimeClockEpoch(Guid.NewGuid(), 1000, 1000);
        var identity = new RecoveryJournalIdentity(session, Guid.NewGuid(), clock);
        var recorder = new RecordingRuntimeState(identity);
        var preparation = new ControllerInputPreparation(new ControllerJoiner(clock));
        var simulator = new WorkerIdentity(session, Guid.NewGuid(), 1, ComponentRole.Simulator);
        var controllers = new WorkerIdentity(session, Guid.NewGuid(), 1, ComponentRole.Controllers);
        var sequences = new Dictionary<ComponentRole, long>(); long journal = 0;
        RuntimeInput? lastInput = null;
        RecordingRuntimeEffects Feed(WorkerIdentity worker, RuntimePayload payload)
        {
            sequences.TryGetValue(worker.Role, out var sequence); sequences[worker.Role] = ++sequence;
            var capture = 1000 + ++journal;
            var envelope = new RuntimeEnvelope(worker, worker.Role == ComponentRole.Simulator && !(payload is WorkerHello) ? 1 : 0, sequence, capture, payload);
            lastInput = preparation.Prepare(new RuntimeInput(Guid.NewGuid(), JournalRecordKind.SourceMessage, capture, capture,
                new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(journal), RuntimeWire.Encode(envelope)));
            var result = recorder.Apply(new DurableRuntimeInput(lastInput, new JournalCursor(1, journal)));
            Check(result.QuarantineReason == null, "Profile evidence caused source quarantine: " + result.QuarantineReason);
            return result;
        }
        TelemetrySample Frame() => Feed(simulator, new TelemetryFrameMessage(new TelemetrySample
        { Sequence = journal + 1, SimulationTimeSeconds = journal * .05, SimulationDeltaSeconds = .05,
            MotionSimulation = true, OnGround = true, AboveGroundLevelFeet = 0, GForce = 1 })).SourceSample!;
        AircraftControlsMessage Profiles(string context, string evidence, Guid instance) => new AircraftControlsMessage("SimObjects/Airplanes/fixture/config", context,
            new[] { new ControllerBinding(ControllerControlKind.Throttle, -1, instance, 7, false, "Throttle profile") }, evidence);
        ControllerCatalogMessage Catalog(TelemetrySample sample) => (ControllerCatalogMessage)ControllerProvenanceCodec.Decode(sample.ControllerCatalog!)!.Payload;
        Feed(simulator, new WorkerHello("fixture", new string('a', 64), 1, clock.Id, clock.Frequency));
        Feed(simulator, new LifecycleMessage(SourceLifecycle.Connected));
        Feed(controllers, new WorkerHello("fixture", new string('a', 64), 1, clock.Id, clock.Frequency));
        var instance = Guid.NewGuid();
        Feed(controllers, new ControllerCatalogMessage(1, new[] { new ControllerDescriptor(0, 0, "Throttle", "device", 255,
            new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), instance, Guid.NewGuid()) }));
        Feed(controllers, new ControllerSnapshotMessage(1, new[] { 1000 + journal + 1 }, new double[8], Enumerable.Repeat(true, 8).ToArray(), 8));
        var evidence = "{\"schema\":1,\"readErrors\":[],\"availableProfiles\":[\"Fixture\"],\"diagnostics\":\"" + new string('x', 200000) + "\"}";
        Feed(simulator, Profiles("context-sha256-a", evidence, instance));
        Check(((AircraftControlsMessage)RuntimeWire.Decode(lastInput!.CopyContent()).Payload).ProfileEvidence == evidence, "Full evidence was not committed with its source observation.");
        var first = Frame();
        var joined = JoinedRuntimeFrame.Decode(lastInput!.CopyContent());
        Check(((ControllerCatalogMessage)joined.Controllers.Catalog!.Payload).ProfileEvidence.Length == 0 && lastInput.CopyContent().Length < evidence.Length / 4,
            "Joined WAL frame repeats the diagnostic snapshot.");
        Check(Catalog(first).ProfileEvidence == evidence && Catalog(first).Bindings.Single().AxisIndex == 7, "Recording lost evidence or its axis assignment.");
        var second = Frame(); Check(ReferenceEquals(first.ControllerCatalog, second.ControllerCatalog), "Recorder re-encoded evidence per frame.");
        recorder = RecordingRuntimeState.RestoreCheckpoint(recorder.CaptureCheckpoint(), identity);
        var recovered = Frame(); Check(Catalog(recovered).ProfileEvidence == evidence, "Recovery lost the active diagnostic snapshot.");
        var repeated = Frame(); Check(ReferenceEquals(recovered.ControllerCatalog, repeated.ControllerCatalog), "Recovered evidence cache does not share catalog bytes.");
        var newEvidence = "{\"schema\":1,\"readErrors\":[\"device disconnected\"]}";
        Feed(simulator, Profiles("context-sha256-b", newEvidence, instance));
        var changed = Frame(); Check(Catalog(changed).ProfileEvidence == newEvidence && Catalog(first).ProfileEvidence == evidence,
            "Profile change overwrote historical evidence.");
        Feed(simulator, Profiles("legacy-without-evidence", "", instance));
        Check(Catalog(Frame()).ProfileEvidence.Length == 0, "A profile observation without evidence inherited stale JSON.");
        Feed(simulator, new AircraftMessage("MSFS", "Unknown aircraft", "unknown", "unknown"));
        Check(Catalog(Frame()).ProfileEvidence.Length == 0, "Aircraft invalidation retained stale evidence.");
        Archive(new[] { first, second, recovered, repeated, changed }, evidence, newEvidence);
    }
    private static void OversizedEvidenceKeepsPhysicalRecording()
    {
        try
        {
            _ = new AircraftControlsMessage("aircraft", "context", Array.Empty<ControllerBinding>(), new string('x', RuntimeWire.MaximumPayloadLength));
            throw new InvalidOperationException("Aircraft controls constructor allowed an oversized complete payload.");
        }
        catch (ArgumentException) { }
        var clock = new RuntimeClockEpoch(Guid.NewGuid(), 1000, 1000); var session = Guid.NewGuid();
        var identity = new RecoveryJournalIdentity(session, Guid.NewGuid(), clock); var recorder = new RecordingRuntimeState(identity);
        var preparation = new ControllerInputPreparation(new ControllerJoiner(clock));
        var simulator = new WorkerIdentity(session, Guid.NewGuid(), 1, ComponentRole.Simulator);
        var controllers = new WorkerIdentity(session, Guid.NewGuid(), 1, ComponentRole.Controllers);
        var sequences = new Dictionary<ComponentRole, long>(); long journal = 0;
        RecordingRuntimeEffects Feed(WorkerIdentity worker, RuntimePayload payload)
        {
            sequences.TryGetValue(worker.Role, out var sequence); sequences[worker.Role] = ++sequence;
            var capture = 1000 + ++journal;
            var message = new RuntimeEnvelope(worker, worker.Role == ComponentRole.Simulator && !(payload is WorkerHello) ? 1 : 0, sequence, capture, payload);
            var input = preparation.Prepare(new RuntimeInput(Guid.NewGuid(), JournalRecordKind.SourceMessage, capture, capture,
                new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc), RuntimeWire.Encode(message)));
            var effects = recorder.Apply(new DurableRuntimeInput(input, new JournalCursor(1, journal)));
            Check(effects.QuarantineReason == null, "Oversized optional evidence quarantined physical recording: " + effects.QuarantineReason);
            return effects;
        }
        Feed(simulator, new WorkerHello("fixture", new string('a', 64), 1, clock.Id, clock.Frequency));
        Feed(simulator, new LifecycleMessage(SourceLifecycle.Connected));
        Feed(controllers, new WorkerHello("fixture", new string('a', 64), 1, clock.Id, clock.Frequency));
        var devices = Enumerable.Range(0, 200).Select(slot => new ControllerDescriptor(slot, slot, new string('n', 512), new string('p', 512), 255,
            new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), Guid.NewGuid(), Guid.NewGuid())).ToArray();
        Feed(controllers, new ControllerCatalogMessage(1, devices));
        Feed(simulator, new AircraftControlsMessage("aircraft", "context", Array.Empty<ControllerBinding>(), "{\"profiles\":\"" + new string('x', 900000) + "\"}"));
        var frame = new TelemetryFrameMessage(new TelemetrySample { Sequence = 1, OnGround = true, MotionSimulation = true, GForce = 1, SimulationTimeSeconds = 1, SimulationDeltaSeconds = .05 });
        var first = Feed(simulator, frame).SourceSample!; var second = Feed(simulator, frame).SourceSample!;
        var catalog = (ControllerCatalogMessage)ControllerProvenanceCodec.Decode(first.ControllerCatalog!)!.Payload;
        Check(first.GForce == 1 && first.ControllerCount == 200 && catalog.ProfileEvidence.Contains("enriched_catalog_exceeds_limit"),
            "Oversized evidence dropped physical data or concealed the diagnostic loss.");
        Check(ReferenceEquals(first.ControllerCatalog, second.ControllerCatalog), "Oversized diagnostics were retried and reallocated on every frame.");
    }
    private static void Archive(TelemetrySample[] samples, string firstEvidence, string secondEvidence)
    {
        using var frames = new MemoryStream(); using var writer = new BinaryWriter(frames);
        RuntimeCheckpointCodec.WriteSamples(writer, samples); frames.Position = 0; using var reader = new BinaryReader(frames);
        var restored = RuntimeCheckpointCodec.ReadSamples(reader);
        Check(ReferenceEquals(restored[0].ControllerCatalog, restored[3].ControllerCatalog), "Frame checkpoint failed to deduplicate equivalent recovered catalogs.");
        using var bytes = new MemoryStream();
        using (var archive = new CaptureArchiveWriter(bytes, false, leaveOpen: true)) ControllerCatalogArchive.Write(archive, samples);
        bytes.Position = 0;
        using var input = new CaptureArchiveReader(bytes, true); using var metadata = input.Open(ControllerCatalogArchive.EntryName);
        var timeline = (ControllerCatalogTimeline)new DataContractJsonSerializer(typeof(ControllerCatalogTimeline)).ReadObject(metadata)!;
        Check(timeline.Catalogs.Length == 2 && timeline.Transitions.Length == 2, "Archive repeated evidence for unchanged rows or dropped a profile transition.");
        var copies = samples.Select(value => TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(value))).ToArray(); timeline.Apply(copies);
        Check(((ControllerCatalogMessage)ControllerProvenanceCodec.Decode(copies[0].ControllerCatalog!)!.Payload).ProfileEvidence == firstEvidence &&
            ((ControllerCatalogMessage)ControllerProvenanceCodec.Decode(copies[4].ControllerCatalog!)!.Payload).ProfileEvidence == secondEvidence,
            "Archive round trip changed profile diagnostics.");
    }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Malformed evidence was accepted."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
