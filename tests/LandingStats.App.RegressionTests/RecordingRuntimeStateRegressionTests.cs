using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RecordingRuntimeStateRegressionTests
{
    private static readonly Guid Session = Guid.Parse("148b2c44-6c3a-49d9-8a5d-16f899028001");
    private static readonly Guid Capture = Guid.Parse("148b2c44-6c3a-49d9-8a5d-16f899028002");
    private static readonly Guid Clock = Guid.Parse("148b2c44-6c3a-49d9-8a5d-16f899028003");
    public static void RunAll()
    {
        var baseline = Run(-1);
        for (var cut = 0; cut <= baseline.Count; cut++)
            Check(Run(cut).SequenceEqual(baseline), "Recording effects changed at recovery boundary " + cut);
        PendingFinalScenery();
        LateAircraftMetadata();
        RecordingStatusLifecycle();
        UnavailableCoordinatesAndEndedFlight();
        LegacyCheckpointSequenceAfterRotation();
    }
    private static void LegacyCheckpointSequenceAfterRotation()
    {
        foreach (var version in new[] { 0x3253524c, 0x3353524c })
        {
            var identity = new RecoveryJournalIdentity(Session, Capture, new RuntimeClockEpoch(Clock, 1000, 1000));
            var state = new RecordingRuntimeState(identity); var preparation = new ControllerInputPreparation(new ControllerJoiner(identity.Clock));
            var worker = Worker(ComponentRole.Simulator, 1); long sourceSequence = 0, journalSequence = 0, segment = 1;
            RecordingRuntimeEffects Feed(RuntimePayload payload)
            {
                var envelope = new RuntimeEnvelope(worker, payload is WorkerHello ? 0 : 1, ++sourceSequence, 1000 + sourceSequence, payload);
                var input = preparation.Prepare(new RuntimeInput(Guid.NewGuid(), JournalRecordKind.SourceMessage, envelope.CaptureQpc, envelope.CaptureQpc,
                    new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc), RuntimeWire.Encode(envelope)));
                return state.Apply(new DurableRuntimeInput(input, new JournalCursor(segment, ++journalSequence)));
            }
            TelemetryFrameMessage Frame(int ordinal) => new TelemetryFrameMessage(new TelemetrySample
            { Sequence = ordinal, SimulationTimeSeconds = ordinal * .1, SimulationDeltaSeconds = .1, MotionSimulation = true,
                OnGround = ordinal >= 10, AboveGroundLevelFeet = ordinal >= 10 ? 0 : 100, VelocityWorldYFps = ordinal >= 10 ? 0 : -3 });
            Feed(new WorkerHello("fixture", new string('a', 64), 1, Clock, 1000)); Feed(new LifecycleMessage(SourceLifecycle.Connected));
            for (var i = 1; i <= 12; i++) Feed(Frame(i));
            segment = 2; journalSequence = 0; Feed(new SourceStatusMessage((byte)RecorderState.Connected, "Recorder.ConnectedGate", Array.Empty<string>()));
            var checkpoint = LegacyCheckpoint(state.CaptureCheckpoint(), version);
            state = RecordingRuntimeState.RestoreCheckpoint(checkpoint, identity);
            Feed(Frame(13));
            var stopped = Feed(new LifecycleMessage(SourceLifecycle.Disconnected));
            var samples = stopped.Captures.Single().Episode.Samples;
            Check(samples.Count >= 13 && samples.Take(12).Select(sample => sample.Sequence).SequenceEqual(Enumerable.Range(1, 12).Select(value => (long)value)),
                "Legacy checkpoint changed retained frame identities.");
            Check(samples.Last().Sequence > samples.Take(samples.Count - 1).Max(sample => sample.Sequence),
                "Legacy checkpoint reused a parent sample sequence after journal rotation.");
        }
    }
    private static byte[] LegacyCheckpoint(byte[] current, int version)
    {
        using var source = new MemoryStream(current, false); using var reader = new BinaryReader(source);
        using var destination = new MemoryStream(); using var writer = new BinaryWriter(destination);
        Check(reader.ReadInt32() == 0x3753524c, "Update the explicit legacy checkpoint fixture for the new source schema.");
        writer.Write(version); writer.Write(reader.ReadBytes(32));
        var cursor = reader.ReadBoolean(); writer.Write(cursor); if (cursor) writer.Write(reader.ReadBytes(16));
        writer.Write(reader.ReadBytes(24)); writer.Write(reader.ReadBoolean()); writer.Write(reader.ReadBoolean()); writer.Write(reader.ReadInt64());
        var labels = reader.ReadInt32(); writer.Write(labels);
        for (var i = 0; i < labels; i++) RuntimeCheckpointCodec.WriteText(writer, RuntimeCheckpointCodec.ReadText(reader));
        var streams = reader.ReadInt32(); writer.Write(streams);
        for (var i = 0; i < streams; i++)
        {
            writer.Write(reader.ReadBytes(1 + 16 + 8 + 8 + 8 + 8 + 1));
            var observationQpc = reader.ReadInt64(); var replay = reader.ReadSByte(); var observation = ReplaySourceEvidenceCodec.ReadObservation(reader);
            if (version >= 0x3353524c) { writer.Write(observationQpc); writer.Write(replay); ReplaySourceEvidenceCodec.WriteObservation(writer, observation); }
            reader.ReadBoolean(); // v5 rejected-generation marker
            reader.ReadBoolean(); // v6 lifecycle resynchronization marker
        }
        for (var i = 0; i < 4; i++)
        {
            var blob = reader.ReadBytes(reader.ReadInt32());
            if (i == 3)
            {
                Check(BitConverter.ToInt32(blob, 0) == 0x3443454c, "Update the explicit legacy coordinator fixture.");
                Check(blob.Skip(blob.Length - 2).All(b => b == 0), "Legacy fixture has unexpected replay approach state.");
                blob = blob.Take(blob.Length - 2).ToArray();
                // This fixture has one final observation, no pending frozen
                // frames and no replay. Remove v3 state and the v2 axis cutoff.
                const int replayTail = 3 + ReplaySourceEvidenceCodec.EvidenceSize + 2 * sizeof(int);
                Check(blob[blob.Length - replayTail] == 0 && blob[blob.Length - replayTail + 1] == 0 &&
                    blob[blob.Length - replayTail + 2] == 1 && blob.Skip(blob.Length - 8).All(b => b == 0), "Legacy fixture has unexpected replay state.");
                blob = blob.Take(blob.Length - replayTail - sizeof(double)).ToArray();
                Array.Copy(BitConverter.GetBytes(0x3143454c), blob, sizeof(int));
            }
            writer.Write(blob.Length); writer.Write(blob);
        }
        var pending = reader.ReadInt32(); writer.Write(pending); writer.Write(reader.ReadBytes(pending * 24));
        reader.ReadInt64(); // v4 parent-owned sequence
        RuntimeCheckpointCodec.ReadText(reader); // v7 profile context
        RuntimeCheckpointCodec.ReadText(reader, RuntimeWire.MaximumPayloadLength); // v7 profile evidence
        Check(source.Position == source.Length, "Legacy fixture omitted a current checkpoint field.");
        return destination.ToArray();
    }
    private static void UnavailableCoordinatesAndEndedFlight()
    {
        var identity = new RecoveryJournalIdentity(Session, Capture, new RuntimeClockEpoch(Clock, 1000, 1000));
        var state = new RecordingRuntimeState(identity); var prepare = new ControllerInputPreparation(new ControllerJoiner(identity.Clock));
        var worker = Worker(ComponentRole.Simulator, 1); long sequence = 0;
        RecordingRuntimeEffects Feed(RuntimePayload payload)
        {
            sequence++;
            var message = new RuntimeEnvelope(worker, payload is WorkerHello ? 0 : 1, sequence, 1000 + sequence, payload);
            var input = prepare.Prepare(new RuntimeInput(Guid.NewGuid(), JournalRecordKind.SourceMessage, message.CaptureQpc, message.CaptureQpc,
                new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc), RuntimeWire.Encode(message)));
            return state.Apply(new DurableRuntimeInput(input, new JournalCursor(1, sequence)));
        }
        Feed(new WorkerHello("fixture", new string('a', 64), 1, Clock, 1000)); Feed(new LifecycleMessage(SourceLifecycle.Connected));
        Feed(new AircraftMessage("MSFS", "Old aircraft", "Old type", "Old model"));
        foreach (var latitude in new[] { double.NaN, double.PositiveInfinity, 91d, 42d })
        {
            Feed(new LifecycleMessage(SourceLifecycle.Flow, (int)SimulatorFlowEvent.FLT_LOAD));
            Feed(new LifecycleMessage(SourceLifecycle.Flow, (int)SimulatorFlowEvent.FLT_LOADED));
            var effects = Feed(new TelemetryFrameMessage(new TelemetrySample { Sequence = sequence, SimulationTimeSeconds = sequence,
                SimulationDeltaSeconds = .1, MotionSimulation = true, OnGround = false, AboveGroundLevelFeet = 100,
                LatitudeDegrees = latitude, LongitudeDegrees = 23, VelocityWorldYFps = -3, GForce = 1 }));
            Check(state.IsRecording, "Invalid scenery coordinates prevented recording.");
            if (latitude != 42) Check(!effects.Commands.Any(item => item.Command is SceneryCommandMessage command && !command.Cancel), "Invalid coordinates reached the source command.");
            Feed(new AircraftMessage("MSFS", "New aircraft", "New type", "New model"));
            Feed(new LifecycleMessage(SourceLifecycle.Flow, (int)SimulatorFlowEvent.BACK_TO_MAIN_MENU));
            Check(!state.IsAircraftAirborne && state.SafeForPresentationRestart, "Ended flight retained a stale airborne restart veto.");
        }
    }
    private static void RecordingStatusLifecycle()
    {
        for (var restoreAt = 0; restoreAt < 4; restoreAt++)
        {
            var identity = new RecoveryJournalIdentity(Session, Capture, new RuntimeClockEpoch(Clock, 1000, 1000));
            var state = new RecordingRuntimeState(identity); var prepare = new ControllerInputPreparation(new ControllerJoiner(identity.Clock));
            var projection = new RuntimePresentation((_, __) => System.Threading.Tasks.Task.CompletedTask);
            var simulator = Worker(ComponentRole.Simulator, 1); long sequence = 0, frame = 0;
            RecordingRuntimeEffects Feed(RuntimePayload payload)
            {
                sequence++;
                var message = new RuntimeEnvelope(simulator, payload is WorkerHello ? 0 : 1, sequence, 1000 + sequence * 100, payload);
                var input = new RuntimeInput(StableRuntimeIdentity.Episode(Session, sequence), JournalRecordKind.SourceMessage, message.CaptureQpc, message.CaptureQpc,
                    new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(sequence * 100), RuntimeWire.Encode(message));
                var effects = state.Apply(new DurableRuntimeInput(prepare.Prepare(input), new JournalCursor(1, sequence)));
                projection.Recording(identity, effects); return effects;
            }
            RecordingRuntimeEffects Frame(double time, bool ground) => Feed(new TelemetryFrameMessage(new TelemetrySample
            { Sequence = ++frame, SimulationTimeSeconds = time, SimulationDeltaSeconds = .1, MotionSimulation = true, OnGround = ground,
                AboveGroundLevelFeet = ground ? 0 : 100, VelocityWorldYFps = ground ? 0 : -3, GForce = 1 }));
            void Restore(int point)
            { if (restoreAt == point) state = RecordingRuntimeState.RestoreCheckpoint(state.CaptureCheckpoint(), identity); }
            void Status(RecorderState expected, string key, string reason) => Check(projection.Snapshot.Status?.State == expected &&
                projection.Snapshot.Status.MessageKey == key, reason);
            Feed(new WorkerHello("fixture", new string('a', 64), 1, Clock, 1000)); Feed(new LifecycleMessage(SourceLifecycle.Connected));
            Feed(new SourceStatusMessage((byte)RecorderState.Connected, "Recorder.ConnectedGate", Array.Empty<string>()));
            Frame(1, false); Restore(0); Frame(1.1, true); Restore(1);
            Frame(16, true);
            Check(state.IsRecording, "The post-contact tail was stopped before fifteen simulation seconds.");
            Status(RecorderState.Recording, "Recorder.ContactRollout", "The rollout stopped displaying its active capture.");
            var completed = Frame(16.2, true); Restore(2);
            Check(!state.IsRecording && completed.Captures.Count == 1, "The completed landing was lost or still being captured.");
            Status(RecorderState.Connected, "Recorder.ConnectedGate", "Completed capture left the header displaying Recording.");
            Feed(new SourceStatusMessage((byte)RecorderState.Recording, "Recorder.ContactRollout", Array.Empty<string>()));
            Status(RecorderState.Connected, "Recorder.ConnectedGate", "A delayed source status restarted the completed capture's indicator.");
            Feed(new SourceStatusMessage((byte)RecorderState.Error, "Recorder.SimConnectErrorFormat", new[] { "fixture" }));
            Status(RecorderState.Error, "Recorder.SimConnectErrorFormat", "Recording projection hid a source error.");
            Feed(new SourceStatusMessage((byte)RecorderState.Connected, "Recorder.ConnectedGate", Array.Empty<string>()));
            Status(RecorderState.Connected, "Recorder.ConnectedGate", "A healthy source did not clear its recovered error.");
            Frame(20, false);
            Check(state.IsRecording, "A second approach could not start after capture completion.");
            Feed(new SourceStatusMessage((byte)RecorderState.Connected, "Recorder.ConnectedGate", Array.Empty<string>()));
            Status(RecorderState.Recording, "Recorder.BelowGate", "A delayed healthy source status hid the new approach.");
            Feed(new LifecycleMessage(SourceLifecycle.Flow, (int)SimulatorFlowEvent.TELEPORT_START));
            Check(!state.IsRecording, "Flight replacement retained an obsolete capture.");
            Status(RecorderState.Connected, "Recorder.ConnectedGate", "Discarding an approach left Recording displayed.");
            Feed(new LifecycleMessage(SourceLifecycle.Flow, (int)SimulatorFlowEvent.REPLAY_START));
            Feed(new SourceStatusMessage((byte)RecorderState.Connected, "Recorder.ConnectedGate", Array.Empty<string>()));
            Status(RecorderState.Connected, "Recorder.ReplayActive", "Source recovery erased the replay state.");
            Feed(new LifecycleMessage(SourceLifecycle.Disconnected));
            Feed(new SourceStatusMessage((byte)RecorderState.Recording, "Recorder.ContactRollout", Array.Empty<string>()));
            Status(RecorderState.Waiting, "Recorder.WaitingRetry", "Disconnected source advertised a live capture.");
        }
    }
    private static void LateAircraftMetadata()
    {
        for (var restore = 0; restore < 3; restore++)
        {
            var identity = new RecoveryJournalIdentity(Session, Capture, new RuntimeClockEpoch(Clock, 1000, 1000));
            var state = new RecordingRuntimeState(identity); var prepare = new ControllerInputPreparation(new ControllerJoiner(identity.Clock));
            var simulator = Worker(ComponentRole.Simulator, 1); long sequence = 0;
            RecordingRuntimeEffects Feed(RuntimePayload payload)
            {
                sequence++;
                var message = new RuntimeEnvelope(simulator, payload is WorkerHello ? 0 : 1, sequence, 1000 + sequence * 100, payload);
                var input = new RuntimeInput(StableRuntimeIdentity.Episode(Session, sequence), JournalRecordKind.SourceMessage, message.CaptureQpc, message.CaptureQpc,
                    new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(sequence * 100), RuntimeWire.Encode(message));
                return state.Apply(new DurableRuntimeInput(prepare.Prepare(input), new JournalCursor(1, sequence)));
            }
            void Restore() => state = RecordingRuntimeState.RestoreCheckpoint(state.CaptureCheckpoint(), identity);
            Feed(new WorkerHello("fixture", new string('a', 64), 1, Clock, 1000)); Feed(new LifecycleMessage(SourceLifecycle.Connected));
            Feed(new AircraftMessage("MSFS", "Unknown aircraft", "unknown", "unknown"));
            Feed(new TelemetryFrameMessage(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, SimulationDeltaSeconds = .1,
                MotionSimulation = true, OnGround = false, AboveGroundLevelFeet = 100, VelocityWorldYFps = -3, GForce = 1 }));
            Check(state.IsRecording, "Late-metadata fixture did not start before the aircraft reply.");
            if (restore == 1) Restore();
            Feed(new AircraftMessage("MSFS", "Delayed aircraft", "unknown", "unknown"));
            if (restore == 2) Restore();
            Feed(new AircraftMessage("MSFS", "Delayed aircraft", "Actual type", "Actual model"));
            Feed(new TelemetryFrameMessage(new TelemetrySample { Sequence = 2, SimulationTimeSeconds = 1.1, SimulationDeltaSeconds = .1,
                MotionSimulation = true, OnGround = true, AboveGroundLevelFeet = 0, GForce = 1 }));
            Feed(new AircraftMessage("MSFS", "Another aircraft", "Other type", "Other model"));
            var completed = Feed(new LifecycleMessage(SourceLifecycle.Disconnected)).Captures.Single().Episode;
            Check(completed.AircraftTitle == "Delayed aircraft" && completed.AircraftType == "Actual type" && completed.AircraftModel == "Actual model",
                "An episode started before metadata retained Unknown aircraft or borrowed a later aircraft.");
            Check(completed.Samples.Count == 2 && completed.PersistentId == StableRuntimeIdentity.Episode(Capture, 1),
                "Metadata completion changed frames or episode identity.");
        }
    }
    private static void PendingFinalScenery()
    {
        var identity = new RecoveryJournalIdentity(Session, Capture, new RuntimeClockEpoch(Clock, 1000, 1000));
        var state = new RecordingRuntimeState(identity); var prepare = new ControllerInputPreparation(new ControllerJoiner(identity.Clock));
        var simulator = Worker(ComponentRole.Simulator, 1); long sequence = 0;
        RuntimeCapturedEpisode? captured = null; SceneryCommandMessage? final = null;
        RecordingRuntimeEffects Feed(RuntimePayload payload, Guid episode = default, Guid lookup = default, LookupStage stage = LookupStage.None)
        {
            sequence++;
            var message = new RuntimeEnvelope(simulator, payload is WorkerHello ? 0 : 1, sequence, 1000 + sequence * 100, payload, episodeId: episode, lookupId: lookup, lookupStage: stage);
            var input = new RuntimeInput(StableRuntimeIdentity.Episode(Session, sequence), JournalRecordKind.SourceMessage, message.CaptureQpc, message.CaptureQpc,
                new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(sequence * 100), RuntimeWire.Encode(message));
            var effects = state.Apply(new DurableRuntimeInput(prepare.Prepare(input), new JournalCursor(1, sequence)));
            captured = effects.Captures.LastOrDefault() ?? captured;
            final = effects.Commands.Select(item => item.Command).OfType<SceneryCommandMessage>().LastOrDefault(item => item.Stage == LookupStage.Final && !item.Cancel) ?? final;
            return effects;
        }
        Feed(new WorkerHello("fixture", new string('a', 64), 1, Clock, 1000)); Feed(new LifecycleMessage(SourceLifecycle.Connected));
        Check(!state.SafeForPresentationRestart, "An open SDK connection without ground evidence was treated as a safe UI restart boundary.");
        for (var frame = 0; frame < 180 && captured == null; frame++)
            Feed(new TelemetryFrameMessage(new TelemetrySample { Sequence = frame + 1, SimulationTimeSeconds = 1 + frame * .1, SimulationDeltaSeconds = .1,
                MotionSimulation = true, OnGround = frame != 0, AboveGroundLevelFeet = frame == 0 ? 100 : 0, VelocityWorldYFps = frame == 0 ? -3 : 0,
                LatitudeDegrees = 42, LongitudeDegrees = 23, GForce = 1 }));
        Check(captured != null && final != null && !captured.Episode.FacilitiesTask.IsCompleted, "Fixture must complete a contact while final scenery is still pending.");
        var checkpoint = state.CaptureCheckpoint(); state = RecordingRuntimeState.RestoreCheckpoint(checkpoint, identity);
        Check(state.CaptureCheckpoint().SequenceEqual(checkpoint), "Pending final lookup changed on recovery.");
        var snapshot = new SceneryQuerySnapshot(new[] { new RunwayFacility { AirportIdent = "FINAL", WidthMeters = 45 } },
            new[] { new AirportFacility { Ident = "FINAL" } }, true, true, 1, 1);
        byte[] data;
        using (var stream = new MemoryStream()) { using var writer = new BinaryWriter(stream); RuntimeCheckpointCodec.WriteJson(writer, SceneryCheckpointData.Capture(snapshot)); data = stream.ToArray(); }
        using var sha = SHA256.Create();
        var result = Feed(new SceneryChunkMessage(StableRuntimeIdentity.Lookup(Capture, 999, 2), 0, data.Length, true, sha.ComputeHash(data), data),
            final!.Episode, final.Lookup, final.Stage);
        Check(result.Captures.Count == 0 && result.Facilities.Count == 1 && result.Facilities[0].Episode == captured!.Episode.PersistentId &&
            result.Facilities[0].Snapshot.FinalRunways.Single().WidthMeters == 45 && result.Facilities[0].Snapshot.FinalLookupCompleted,
            "Recovered final reply repeated the landing or lost its original episode identity.");
        var settled = state.CaptureCheckpoint();
        Check(RecordingRuntimeState.RestoreCheckpoint(settled, identity).CaptureCheckpoint().SequenceEqual(settled), "Settled facility state changed on another recovery.");
    }
    private static List<string> Run(int restoreAt)
    {
        var identity = new RecoveryJournalIdentity(Session, Capture, new RuntimeClockEpoch(Clock, 1000, 1000));
        var state = new RecordingRuntimeState(identity); var prepare = new ControllerInputPreparation(new ControllerJoiner(identity.Clock));
        var simulator = Worker(ComponentRole.Simulator, 1); var controllers = Worker(ComponentRole.Controllers, 1);
        var signatures = new List<string>(); var captures = new List<RuntimeCapturedEpisode>(); var final = new List<RuntimeFinalFacilities>();
        long order = 0, simSequence = 0, controllerSequence = 0, connection = 1, frameSequence = 0;
        SceneryCommandMessage? lookup = null;
        void Restore()
        {
            var checkpoint = state.CaptureCheckpoint(); state = RecordingRuntimeState.RestoreCheckpoint(checkpoint, identity);
            Check(state.CaptureCheckpoint().SequenceEqual(checkpoint), "Recording state changed during checkpoint restoration.");
            prepare = new ControllerInputPreparation(ControllerJoiner.RestoreCheckpoint(state.CaptureJoinCheckpoint(), identity.Clock));
        }
        if (restoreAt == 0) Restore();
        void Feed(JournalRecordKind kind, byte[] bytes)
        {
            order++;
            var input = new RuntimeInput(StableRuntimeIdentity.Episode(Session, order), kind, 1000 + order * 100, 1000 + order * 100,
                new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc).AddSeconds(order), bytes);
            var prepared = prepare.Prepare(input);
            var durable = new DurableRuntimeInput(prepared, new JournalCursor(1, order));
            var effects = state.Apply(durable);
            Check(state.Apply(durable).Captures.Count == 0 && state.Apply(durable).Commands.Count == 0, "Duplicate journal cursor repeated effects.");
            captures.AddRange(effects.Captures); final.AddRange(effects.Facilities);
            lookup = effects.Commands.Select(item => item.Command).OfType<SceneryCommandMessage>().LastOrDefault(item => !item.Cancel) ?? lookup;
            signatures.Add(Signature(effects, prepared));
            if (order == restoreAt) Restore();
        }
        void Sim(RuntimePayload payload, Guid episode = default, Guid request = default, LookupStage stage = LookupStage.None) =>
            Feed(JournalRecordKind.SourceMessage, RuntimeWire.Encode(new RuntimeEnvelope(simulator, payload is WorkerHello ? 0 : connection,
                ++simSequence, 1000 + (order + 1) * 100, payload, episodeId: episode, lookupId: request, lookupStage: stage)));
        void Control(RuntimePayload payload) => Feed(JournalRecordKind.SourceMessage, RuntimeWire.Encode(new RuntimeEnvelope(controllers, 0,
            ++controllerSequence, 1000 + (order + 1) * 100, payload)));
        void Hello(bool sim)
        {
            var message = new WorkerHello("fixture", new string('a', 64), 1, Clock, 1000);
            if (sim) Sim(message); else Control(message);
        }
        void Catalog(string name) => Control(new ControllerCatalogMessage(1, new[] { new ControllerDescriptor(0, 0, name, name, 2, new uint[6], Enumerable.Repeat(65535u, 6).ToArray()) }));
        void Axes(double value)
        {
            var clocks = new long[8]; clocks[0] = 1000 + (order + 1) * 100 - 1;
            var values = new double[48]; values[1] = value; var valid = new bool[48]; valid[1] = true;
            Control(new ControllerSnapshotMessage(1, clocks, values, valid));
        }
        void Frame(double time, bool ground) => Sim(new TelemetryFrameMessage(new TelemetrySample
        { Sequence = ++frameSequence, SimulationTimeSeconds = time, SimulationDeltaSeconds = .1, MotionSimulation = true, OnGround = ground,
            AboveGroundLevelFeet = ground ? 0 : 100, VelocityWorldYFps = ground ? 0 : -3, GForce = 1, LatitudeDegrees = 42, LongitudeDegrees = 23 }));
        void Retire(WorkerIdentity worker, long sequence, bool intentional = false) => Feed(JournalRecordKind.SourceRetirement,
            new RuntimeSourceRetirement(worker, worker.Role == ComponentRole.Simulator ? connection : 0, sequence, SourceGapReason.ProcessExit, intentional).Encode());
        Feed(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.DeliveryPolicy, true, 1).Encode());
        Hello(true); Sim(new LifecycleMessage(SourceLifecycle.Connected)); Sim(new AircraftMessage("MSFS", "Original aircraft", "type", "model"));
        Hello(false); Catalog("Stick A"); Axes(-25); Frame(1, false);
        Check(state.IsRecording && lookup != null && lookup.Stage == LookupStage.Preliminary, "Approach did not create a context-bound scenery request.");
        var scenery = new SceneryQuerySnapshot(new[] { new RunwayFacility { AirportIdent = "PRE", WidthMeters = 30 } },
            new[] { new AirportFacility { Ident = "PRE", LatitudeDegrees = 42, LongitudeDegrees = 23 } }, true, true, 1, 1);
        byte[] site;
        using (var stream = new MemoryStream())
        { using var writer = new BinaryWriter(stream); RuntimeCheckpointCodec.WriteJson(writer, SceneryCheckpointData.Capture(scenery)); site = stream.ToArray(); }
        using (var sha = SHA256.Create())
            Sim(new SceneryChunkMessage(StableRuntimeIdentity.Lookup(Capture, 99, 1), 0, site.Length, true, sha.ComputeHash(site), site), lookup!.Episode, lookup.Lookup, lookup.Stage);
        Retire(controllers, controllerSequence);
        Check(state.IsRecording, "Controller retirement stopped the active approach.");
        Frame(1.1, true);
        controllers = Worker(ComponentRole.Controllers, 2); controllerSequence = 0; Hello(false); Catalog("Stick B"); Axes(75); Frame(1.2, true);
        Sim(new AircraftMessage("MSFS", "Changed aircraft", "other", "other"));
        Sim(new LifecycleMessage(SourceLifecycle.Disconnected));
        Check(captures.Count == 1 && captures[0].Episode.AircraftTitle == "Original aircraft" && !captures[0].LocalOnly && final.Count == 1,
            "Gap changed aircraft metadata, lost the contact or failed to settle its facilities.");
        Check(captures[0].AutomaticAllowed && captures[0].PolicyRevision == 1, "Recording recovery lost the policy at capture.");
        Feed(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.DeliveryPolicy, false, 2).Encode());
        var samples = captures[0].Episode.Samples;
        Check(samples[0].RawControllerAxesPercent[1] == -25 && samples[0].RawControllerAxesValid[1] &&
            samples.Any(sample => sample.OnGround && (sample.RawControllerAxesValid.Length <= 1 || !sample.RawControllerAxesValid[1])) && samples.Last().RawControllerAxesPercent[1] == 75,
            "Source restart changed already joined axes or prevented their recovery.");
        Check(final[0].Snapshot.PreliminaryRunways.Single().WidthMeters == 30 && !final[0].Snapshot.FinalLookupCompleted,
            "Source retirement lost accepted scenery or fabricated final lookup success.");
        connection = 2; Sim(new LifecycleMessage(SourceLifecycle.Connected)); Frame(1.3, true);
        Check(!state.IsRecording, "First ground after reconnection invented a landing.");
        Frame(1.4, false); Retire(simulator, simSequence);
        Check(captures.Count == 2 && captures[1].LocalOnly && captures[1].Episode.IsDiagnosticOnly && captures[1].Episode.AircraftTitle == "Unknown aircraft",
            "Interrupted approach was lost, automatically uploaded, or borrowed aircraft metadata.");
        simulator = Worker(ComponentRole.Simulator, 2); simSequence = 0; connection = 1;
        Hello(true); Sim(new LifecycleMessage(SourceLifecycle.Connected)); Frame(2, true); Retire(simulator, simSequence, true);
        Retire(controllers, controllerSequence, true);
        Feed(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.Stop).Encode());
        Check(captures.Count == 2 && !state.IsRecording, "Shutdown duplicated a completed episode.");
        Check(!captures[1].AutomaticAllowed && captures[1].PolicyRevision == 2, "Capture crossed the opted-out policy revision.");
        Check(captures[0].TimestampUtc == new DateTime(2026, 10, 17, 10, 0, 17, DateTimeKind.Utc), "Recovery used its current wall clock for the capture timestamp.");
        return signatures;
    }
    private static WorkerIdentity Worker(ComponentRole role, long generation) => new WorkerIdentity(Session,
        StableRuntimeIdentity.Episode(Capture, (int)role * 100 + generation), generation, role);
    private static string Signature(RecordingRuntimeEffects effects, RuntimeInput prepared)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.UTF8, true);
        writer.Write(prepared.CopyContent()); writer.Write(effects.SimulatorExited);
        foreach (var command in effects.Commands) writer.Write(RuntimeWire.Encode(new RuntimeEnvelope(command.Worker, 0, 1, 1, command.Command, Capture, 1)));
        foreach (var capture in effects.Captures)
        {
            writer.Write(capture.Episode.PersistentId.ToByteArray()); writer.Write(capture.TimestampUtc.Ticks); writer.Write(capture.LocalOnly);
            writer.Write(capture.AutomaticAllowed); writer.Write(capture.PolicyRevision);
            writer.Write(capture.Episode.AircraftTitle); RuntimeCheckpointCodec.WriteSamples(writer, capture.Episode.Samples);
            RuntimeCheckpointCodec.WriteJournal(writer, capture.Episode.DecisionJournal);
        }
        foreach (var site in effects.Facilities) { writer.Write(site.Episode.ToByteArray()); RuntimeCheckpointCodec.WriteJson(writer, FacilityPersistenceData.Capture(site.Snapshot)); }
        foreach (var item in effects.Presentation)
        {
            writer.Write(item.GetType().Name);
            if (item is RecorderStatusEventArgs status) { writer.Write((byte)status.State); writer.Write(status.MessageKey); }
        }
        foreach (var id in effects.Retirements) writer.Write(id.ToByteArray());
        writer.Flush(); using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(memory.ToArray()));
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
