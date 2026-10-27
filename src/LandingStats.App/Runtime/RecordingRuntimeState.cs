using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal enum RecordingCommandKind : byte { Tick = 1, RawCapture = 2, Stop = 3, DeliveryPolicy = 4, PreviousRuntimeEnded = 5 }
internal sealed class RecordingCommand
{
    public RecordingCommand(RecordingCommandKind kind, bool enabled = false, long revision = 0)
    { if (!Enum.IsDefined(typeof(RecordingCommandKind), kind) || revision < 0 || (revision != 0 && kind != RecordingCommandKind.DeliveryPolicy)) throw new ArgumentException("Invalid recording command."); Kind = kind; Enabled = enabled; Revision = revision; }
    public RecordingCommandKind Kind { get; }
    public bool Enabled { get; }
    public long Revision { get; }
    public byte[] Encode()
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write((byte)Kind); writer.Write(Enabled); if (Kind == RecordingCommandKind.DeliveryPolicy) writer.Write(Revision);
        return memory.ToArray();
    }
    public static RecordingCommand Decode(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes, false); using var reader = new BinaryReader(memory);
        var kind = (RecordingCommandKind)reader.ReadByte(); var enabled = RuntimeCheckpointCodec.ReadBoolean(reader);
        var revision = kind == RecordingCommandKind.DeliveryPolicy ? reader.ReadInt64() : 0;
        if (memory.Position != memory.Length) throw new InvalidDataException("Invalid recording command.");
        return new RecordingCommand(kind, enabled, revision);
    }
}

internal sealed class RuntimeCommandDispatch
{
    public RuntimeCommandDispatch(WorkerIdentity worker, RuntimeCommand command) { Worker = worker; Command = command; }
    public WorkerIdentity Worker { get; }
    public RuntimeCommand Command { get; }
}
internal sealed class RuntimeCapturedEpisode
{
    public RuntimeCapturedEpisode(LandingEpisodeEventArgs episode, DateTime timestamp, bool localOnly, bool automaticAllowed = false, long policyRevision = 0)
    { Episode = episode; TimestampUtc = timestamp; LocalOnly = localOnly; AutomaticAllowed = automaticAllowed; PolicyRevision = policyRevision; }
    public LandingEpisodeEventArgs Episode { get; }
    public DateTime TimestampUtc { get; }
    public bool LocalOnly { get; }
    public bool AutomaticAllowed { get; }
    public long PolicyRevision { get; }
}
internal sealed class RecordingRuntimeEffects
{
    // Transient latest-value observation, not part of any durable recording schema.
    public RuntimeEnvelope? SourceObservation;
    public TelemetrySample? SourceSample;
    public bool? SimulatorConnected;
    public readonly List<RuntimeCommandDispatch> Commands = new List<RuntimeCommandDispatch>();
    public readonly List<RuntimeCapturedEpisode> Captures = new List<RuntimeCapturedEpisode>();
    public readonly List<RuntimeFinalFacilities> Facilities = new List<RuntimeFinalFacilities>();
    public readonly List<object> Presentation = new List<object>();
    public readonly List<object> RawCapture = new List<object>();
    public readonly List<Guid> Retirements = new List<Guid>();
    public bool SimulatorExited;
    public string? QuarantineReason;
}

/// <summary>Pure recording owner: applies committed inputs in journal order and
/// emits explicit effects. The caller commits durable effects before checkpointing
/// this state; it never invokes UI, disk or worker callbacks inside the reducer.</summary>
internal sealed class RecordingRuntimeState
{
    private readonly RecoveryJournalIdentity _identity;
    private readonly Dictionary<ComponentRole, StreamState> _streams = new Dictionary<ComponentRole, StreamState>();
    private ControllerJoiner _controllers;
    private ControllerInputPreparation _preparation;
    private readonly RemoteSceneryQueryPort _query;
    private EpisodeFacilityCoordinator _facilities;
    private LandingEpisodeCoordinator _coordinator;
    private readonly List<PendingFacilities> _pendingFacilities = new List<PendingFacilities>();
    private RecordingRuntimeEffects? _effects;
    private JournalCursor? _cursor;
    private DateTime _utc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private double _hostSeconds;
    private long _nextLookup;
    private long _sampleSequence;
    private string[] _controlSources = Array.Empty<string>();
    private bool _fullRequested;
    private bool _automaticCaptureEnabled;
    private long _policyRevision;
    private string _profileContext = "", _profileEvidence = "";
    private byte[]? _profileCatalogSource, _profileCatalogEnriched;
    public RecordingRuntimeState(RecoveryJournalIdentity identity)
    {
        _identity = identity; _controllers = new ControllerJoiner(identity.Clock); _preparation = new ControllerInputPreparation(_controllers);
        _query = new RemoteSceneryQueryPort(Send, LookupContext, runways => _facilities!.FinishRunwayLookupStage(runways));
        _facilities = new EpisodeFacilityCoordinator(_query);
        _coordinator = new LandingEpisodeCoordinator(_facilities, () => _hostSeconds, () => _controlSources, () => _utc, identity.CaptureSessionId);
        Attach();
    }
    public JournalCursor? Cursor => _cursor;
    public DateTime LastTimestampUtc => _utc;
    public bool IsRecording => _coordinator.IsRecording;
    public bool IsAircraftAirborne => _coordinator.IsAircraftAirborne;
    public bool SafeForPresentationRestart => !IsRecording && !IsAircraftAirborne &&
        (!_streams.TryGetValue(ComponentRole.Simulator, out var simulator) || !simulator.Connected || _coordinator.IsAircraftGrounded || _coordinator.IsFlightEnded);
    public bool RawDebugEnabled => _coordinator.RawDebugEnabled;
    private void Attach()
    {
        _coordinator.StatusChanged += (_, args) => Effects.Presentation.Add(args);
        _coordinator.AircraftGroundStateChanged += (_, args) => Effects.Presentation.Add(args);
        _coordinator.EpisodeStarted += (_, args) => Effects.Presentation.Add(args);
        _coordinator.EpisodeCompleted += (_, args) => Capture(args, false);
        _coordinator.ApproachInterrupted += (_, args) => Capture(args, true);
        _coordinator.RawDebugCaptureStarted += (_, args) => Effects.RawCapture.Add(args);
        _coordinator.RawDebugSampleReceived += (_, args) => Effects.RawCapture.Add(args);
        _coordinator.RawDebugCaptureStopped += (_, _) => Effects.RawCapture.Add(EventArgs.Empty);
        _coordinator.MetadataRequested += (_, _) => Send(new WorkerCommandMessage(WorkerCommand.Metadata));
    }
    private RecordingRuntimeEffects Effects => _effects ?? throw new InvalidOperationException("Recording effects escaped their input transaction.");
    private void Capture(LandingEpisodeEventArgs episode, bool localOnly)
    {
        Effects.Captures.Add(new RuntimeCapturedEpisode(episode, _utc, localOnly, !localOnly && _automaticCaptureEnabled, _policyRevision));
        if (!episode.IsDiagnosticOnly) _pendingFacilities.Add(new PendingFacilities(episode.PersistentId, episode.EpisodeId, episode.FacilitiesTask, episode.DecisionJournal));
        if (_pendingFacilities.Count > 4) throw new InvalidOperationException("Pending facility outcomes exceeded their bound.");
    }
    private void Send(RuntimeCommand command)
    {
        if (_streams.TryGetValue(ComponentRole.Simulator, out var stream) && stream.Connected)
            Effects.Commands.Add(new RuntimeCommandDispatch(stream.Worker, command));
    }
    private SceneryLookupContext LookupContext()
    {
        var id = StableRuntimeIdentity.Episode(_identity.CaptureSessionId, _facilities.EpisodeId);
        return new SceneryLookupContext(id, _facilities.EpisodeId, StableRuntimeIdentity.Lookup(id, ++_nextLookup, (int)(_facilities.IsPreliminary ? LookupStage.Preliminary : LookupStage.Final)),
            _facilities.IsPreliminary ? LookupStage.Preliminary : LookupStage.Final,
            _streams.TryGetValue(ComponentRole.Simulator, out var source) ? source.Capture : _identity.Clock.OriginQpc);
    }
    public RecordingRuntimeEffects Apply(DurableRuntimeInput committed)
    {
        var effects = new RecordingRuntimeEffects();
        if (_cursor != null && committed.Cursor.CompareTo(_cursor) <= 0) return effects;
        if (_effects != null) throw new InvalidOperationException("Recording owner was entered recursively.");
        _effects = effects;
        try
        {
            var input = committed.Input; _utc = input.TimestampUtc;
            try
            {
            _preparation.Replay(input);
            if (_preparation.LastFailure != null) throw new InvalidDataException("Rejected source input.", _preparation.LastFailure);
            switch (input.Kind)
            {
                case JournalRecordKind.SourceMessage: ApplySource(RuntimeWire.Decode(input.CopyContent()), null); break;
                case JournalRecordKind.JoinedFrame:
                    var joined = JoinedRuntimeFrame.Decode(input.CopyContent()); ApplySource(joined.Source, joined.Controllers); break;
                case JournalRecordKind.SourceRetirement:
                    var retirement = RuntimeSourceRetirement.Decode(input.CopyContent());
                    if (_streams.TryGetValue(retirement.Worker.Role, out var stream) && stream.Worker.Equals(retirement.Worker))
                    {
                        if (stream.Sequence != retirement.LastAccepted) throw new InvalidDataException("Retirement skipped an accepted source prefix.");
                        if (retirement.Worker.Role == ComponentRole.Simulator) Disconnect(stream, retirement.Intentional ? "source_stopped" : retirement.Reason.ToString());
                        _streams.Remove(retirement.Worker.Role);
                    }
                    effects.Retirements.Add(input.Id); break;
                case JournalRecordKind.RuntimeCommand:
                    var command = RecordingCommand.Decode(input.CopyContent());
                    switch (command.Kind)
                    {
                        case RecordingCommandKind.Tick:
                            _coordinator.Tick();
                            if (_query.HasExpired(input.AcceptedQpc, _identity.Clock.Frequency))
                                _facilities.FailCurrentLookupStage("Scenery reply deadline elapsed; retain only this episode's accepted snapshots.");
                            break;
                        case RecordingCommandKind.RawCapture:
                            _coordinator.SetRawDebugEnabled(command.Enabled); Send(new WorkerCommandMessage(WorkerCommand.RawCapture, command.Enabled)); break;
                        case RecordingCommandKind.Stop: _coordinator.Stop(); effects.Retirements.Add(input.Id); break;
                        case RecordingCommandKind.PreviousRuntimeEnded:
                            foreach (var source in _streams.Values)
                                if (source.Worker.Role == ComponentRole.Simulator) Disconnect(source, "runtime_process_exit");
                            _streams.Clear(); _coordinator.Stop(); effects.Retirements.Add(input.Id); break;
                        case RecordingCommandKind.DeliveryPolicy:
                            if (command.Revision == _policyRevision && command.Enabled != _automaticCaptureEnabled)
                                throw new InvalidDataException("Delivery policy revision was reused with a different preference.");
                            if (command.Revision > _policyRevision)
                            { _policyRevision = command.Revision; _automaticCaptureEnabled = command.Enabled; }
                            break;
                    }
                    break;
                case JournalRecordKind.CommandResult:
                    var outcome = RuntimeCommandOutcome.Decode(input.CopyContent());
                    if (_query.FailedCurrent(outcome))
                        _facilities.FailCurrentLookupStage("Scenery command could not continue: " + outcome.Result.Reason);
                    break;
                default: throw new InvalidDataException("Unsupported recording journal input.");
            }
            }
            catch (Exception error) when ((input.Kind == JournalRecordKind.SourceMessage || input.Kind == JournalRecordKind.JoinedFrame) &&
                (error is InvalidDataException || error is ArgumentException || error is EndOfStreamException))
            { QuarantineSource(input, error); }
            foreach (var pending in _pendingFacilities.Where(item => item.Completion.IsCompleted).ToArray())
            {
                var snapshot = pending.Completion.GetAwaiter().GetResult();
                if (snapshot.EpisodeId != pending.Ordinal) throw new InvalidDataException("Final facilities belong to another episode.");
                effects.Facilities.Add(new RuntimeFinalFacilities(pending.Id, snapshot, pending.Decisions?.Snapshot()));
                _pendingFacilities.Remove(pending);
            }
            if (_fullRequested != _coordinator.IsRecording)
            { _fullRequested = _coordinator.IsRecording; Send(new WorkerCommandMessage(WorkerCommand.FullTelemetry, _fullRequested)); }
            _cursor = committed.Cursor;
            effects.SimulatorConnected = _streams.TryGetValue(ComponentRole.Simulator, out var simulator) && simulator.Connected;
            return effects;
        }
        finally { _effects = null; }
    }
    private void QuarantineSource(RuntimeInput input, Exception error)
    {
        Effects.QuarantineReason = error.ToString();
        RuntimeEnvelope? rejected = null;
        try { rejected = input.Kind == JournalRecordKind.JoinedFrame ? JoinedRuntimeFrame.Decode(input.CopyContent()).Source : RuntimeWire.Decode(input.CopyContent()); }
        catch (Exception invalid) when (invalid is InvalidDataException || invalid is ArgumentException || invalid is EndOfStreamException) { }
        if (rejected != null && rejected.Worker.AppSessionId == _identity.RuntimeId)
        {
            if (!_streams.TryGetValue(rejected.Worker.Role, out var stream))
                _streams.Add(rejected.Worker.Role, stream = new StreamState(rejected.Worker));
            if (stream.Worker.Equals(rejected.Worker))
            {
                stream.Sequence = Math.Max(stream.Sequence, rejected.Sequence); stream.Capture = Math.Max(stream.Capture, rejected.CaptureQpc);
                stream.Rejected = true;
                if (stream.Worker.Role == ComponentRole.Simulator && stream.Connected) Disconnect(stream, "source_protocol_error");
            }
            Effects.Commands.Add(new RuntimeCommandDispatch(rejected.Worker, new WorkerCommandMessage(WorkerCommand.Stop)));
        }
        _coordinator.DecisionJournal.Record(_hostSeconds, "source", "input_quarantined", error.Message);
    }
    private void ApplySource(RuntimeEnvelope message, ControllerJoinDecision? controllers)
    {
        if (message.Worker.AppSessionId != _identity.RuntimeId) throw new InvalidDataException("Source belongs to another runtime session.");
        if (!_streams.TryGetValue(message.Worker.Role, out var stream))
        {
            if (!(message.Payload is WorkerHello) || message.Sequence != 1) throw new InvalidDataException("Source did not begin with its handshake.");
            stream = new StreamState(message.Worker); _streams.Add(message.Worker.Role, stream);
        }
        if (!stream.Worker.Equals(message.Worker) || !RuntimeSequence.IsNext(stream.Sequence, message) || message.CaptureQpc < stream.Capture || message.ConnectionGeneration < stream.Connection)
            throw new InvalidDataException("Source crossed a recording generation or ordering boundary.");
        stream.Sequence = message.Sequence; stream.Capture = message.CaptureQpc;
        if (stream.Rejected) return;
        if (message.Worker.Role != ComponentRole.Simulator) return;
        if (message.ConnectionGeneration > stream.Connection)
        {
            if (!(message.Payload is LifecycleMessage connected && connected.State == SourceLifecycle.Connected) &&
                !(message.Payload is SourceGapMessage gapBoundary && gapBoundary.Reason == SourceGapReason.BufferOverflow))
                throw new InvalidDataException("Simulator connection has no OPEN boundary.");
            if (stream.Connected) Disconnect(stream, "connection_replaced");
            stream.Connection = message.ConnectionGeneration;
        }
        switch (message.Payload)
        {
            case WorkerHello _: break;
            case LifecycleMessage lifecycle:
                switch (lifecycle.State)
                {
                    case SourceLifecycle.Connected:
                        if (stream.Connection <= 0) throw new InvalidDataException("OPEN has no connection identity.");
                        stream.Connected = true; _query.Connect(stream.Worker, stream.Connection);
                        ClearProfileEvidence();
                        stream.LifecyclePending = false;
                        stream.Observation = new ReplaySourceObservation(SourceObservationAvailability.Unknown, SourceObservationReason.InitialState);
                        stream.ObservationQpc = message.CaptureQpc; stream.SdkReplay = SdkReplayState.Unknown;
                        _coordinator.SetMetadata("Unknown aircraft", "unknown", "unknown");
                        Send(new WorkerCommandMessage(WorkerCommand.RawCapture, _coordinator.RawDebugEnabled));
                        Send(new WorkerCommandMessage(WorkerCommand.FullTelemetry, _coordinator.IsRecording)); break;
                    case SourceLifecycle.Disconnected:
                        var reasons = new[] { "connection_closed", "receive_failed", "setup_failed", "mode_change_failed", "stream_unresponsive", "open_timeout", "definition_rejected", "simulator_quit" };
                        Disconnect(stream, lifecycle.Value >= 0 && lifecycle.Value < reasons.Length ? reasons[lifecycle.Value] : "connection_closed"); break;
                    case SourceLifecycle.SimulatorQuit: Effects.SimulatorExited = true; Disconnect(stream, "simulator_quit"); break;
                    case SourceLifecycle.PauseFlags: _coordinator.SetSdkPauseFlags(lifecycle.Value); break;
                    case SourceLifecycle.SimRunning: _coordinator.SetSdkSimRunning(lifecycle.Value != 0); break;
                    case SourceLifecycle.Flow:
                        var flow = (SimulatorFlowEvent)lifecycle.Value;
                        if (flow == SimulatorFlowEvent.REPLAY_START) stream.SdkReplay = SdkReplayState.Active;
                        else if (flow == SimulatorFlowEvent.REPLAY_END) stream.SdkReplay = SdkReplayState.Inactive;
                        else if (flow == SimulatorFlowEvent.FLIGHT_END || flow == SimulatorFlowEvent.BACK_TO_MAIN_MENU || flow == SimulatorFlowEvent.PLANE_CRASH)
                            stream.SdkReplay = SdkReplayState.Unknown;
                        _coordinator.HandleFlowEvent(flow); break;
                }
                break;
            case AircraftControlsMessage controls:
                if (!stream.Connected) break;
                _profileContext = controls.Context; _profileEvidence = controls.ProfileEvidence;
                _profileCatalogSource = null; _profileCatalogEnriched = null;
                break;
            case AircraftMessage aircraft:
                if (!stream.Connected) break;
                if (aircraft.Title == "Unknown aircraft") ClearProfileEvidence();
                _coordinator.Simulator = aircraft.Simulator; _coordinator.SetMetadata(aircraft.Title, aircraft.Type, aircraft.Model); break;
            case TelemetryFrameMessage frame:
                if (!stream.Connected || controllers == null) throw new InvalidDataException("Telemetry must have a live context and a durable controller join.");
                if (stream.LifecyclePending) break; // A gap cannot invent the end of a replay/pause/loading state.
                var sample = frame.CopySample(); controllers.Apply(sample); EnrichProfileEvidence(sample, controllers.Catalog); sample.Sequence = checked(++_sampleSequence);
                sample.SourceEvidence = new ReplaySourceEvidence(stream.Worker.InstanceId, stream.Worker.Generation, stream.Connection,
                    stream.ObservationQpc, message.CaptureQpc, _identity.Clock.Frequency, stream.SdkReplay, stream.Observation);
                sample.HostElapsedSeconds = _identity.Clock.ElapsedSeconds(_identity.Clock.Id, message.CaptureQpc, _identity.Clock.Frequency);
                _hostSeconds = sample.HostElapsedSeconds;
                _controlSources = ControllerCatalogTimeline.Labels(controllers.Catalog, legacyDescriptions: true).ToArray();
                _coordinator.ProcessSample(sample); Effects.SourceSample = sample; break;
            case GuardFrameMessage _: break; // Guard gating belongs to acquisition; it cannot fabricate a landing frame.
            case SimulatorLifecycleSnapshotMessage lifecycleState:
                if (!stream.LifecyclePending) throw new InvalidDataException("Unsolicited simulator lifecycle snapshot.");
                stream.LifecyclePending = false; stream.SdkReplay = lifecycleState.SdkReplay;
                if (lifecycleState.Connected)
                {
                    _coordinator.RestoreTransportLifecycle(lifecycleState);
                    Send(new WorkerCommandMessage(WorkerCommand.RawCapture, _coordinator.RawDebugEnabled));
                    Send(new WorkerCommandMessage(WorkerCommand.FullTelemetry, _coordinator.IsRecording));
                }
                else Disconnect(stream, "connection_closed");
                if (lifecycleState.Quit) Effects.SimulatorExited = true;
                break;
            case SourceObservationMessage observation:
                if (stream.Connected) { stream.Observation = observation.Observation; stream.ObservationQpc = message.CaptureQpc; }
                break;
            case SceneryChunkMessage _: _query.Accept(message); break;
            case SourceDiagnosticMessage diagnostic:
                if (message.EpisodeId == Guid.Empty)
                    _coordinator.DecisionJournal.Record(diagnostic.Time, diagnostic.Component, diagnostic.Code, diagnostic.Reason, diagnostic.Details);
                else if (_query.MatchesDiagnostic(message))
                    _facilities.DecisionJournal?.Record(diagnostic.Time, diagnostic.Component, diagnostic.Code, diagnostic.Reason, diagnostic.Details);
                break;
            case SourceStatusMessage status:
                if ((RecorderState)status.State == RecorderState.Connected || (RecorderState)status.State == RecorderState.Recording)
                {
                    if (stream.Connected) _coordinator.PublishCaptureStatus();
                }
                else Effects.Presentation.Add(new RecorderStatusEventArgs((RecorderState)status.State, status.Key, status.Arguments.Cast<object>().ToArray()));
                break;
            case SourceGapMessage gap:
                _coordinator.DecisionJournal.Record(_hostSeconds, "source", "source_gap", gap.Reason.ToString(),
                    new Dictionary<string, double> { ["first_missing"] = gap.FirstMissing, ["last_missing"] = gap.LastMissing });
                Disconnect(stream, gap.Reason.ToString());
                if (gap.Reason == SourceGapReason.BufferOverflow && stream.Connection > 0)
                {
                    // The transport reports a lost range, but its native source
                    // still owns this connection. Start a fresh recording segment.
                    stream.Connected = true; _query.Connect(stream.Worker, stream.Connection);
                    stream.LifecyclePending = true;
                    _coordinator.SetMetadata("Unknown aircraft", "unknown", "unknown");
                    Send(new WorkerCommandMessage(WorkerCommand.Metadata));
                }
                break;
            default: throw new InvalidDataException("Unexpected simulator recording payload.");
        }
        Effects.SourceObservation = message;
    }
    private void EnrichProfileEvidence(TelemetrySample sample, RuntimeEnvelope? envelope)
    {
        if (_profileEvidence.Length == 0 || !(envelope?.Payload is ControllerCatalogMessage catalog) ||
            catalog.BindingContext != _profileContext) return;
        var source = sample.ControllerCatalog!;
        if (_profileCatalogSource == null || !_profileCatalogSource.SequenceEqual(source))
        {
            var enriched = new ControllerCatalogMessage(catalog.Revision, catalog.Devices, catalog.Bindings, catalog.BindingContext, _profileEvidence);
            try
            {
                _profileCatalogEnriched = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(envelope.Worker, envelope.ConnectionGeneration,
                    envelope.Sequence, envelope.CaptureQpc, enriched));
            }
            catch (InvalidDataException error)
            {
                _profileCatalogEnriched = source;
                var report = "{\"schema\":1,\"evidenceError\":\"enriched_catalog_exceeds_limit\",\"evidenceBytes\":" +
                    System.Text.Encoding.UTF8.GetByteCount(_profileEvidence).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                try
                {
                    _profileCatalogEnriched = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(envelope.Worker, envelope.ConnectionGeneration,
                        envelope.Sequence, envelope.CaptureQpc, new ControllerCatalogMessage(catalog.Revision, catalog.Devices, catalog.Bindings, catalog.BindingContext, report)));
                }
                catch (InvalidDataException) { /* The catalog itself fits; its optional error report may also exceed the archive bound. */ }
                _coordinator.DecisionJournal.Record(_hostSeconds, "controllers", "profile_evidence_too_large",
                    "Profile diagnostics could not fit the catalog; physical observations and bindings were retained. " + error.Message,
                    new Dictionary<string, double> { ["evidence_bytes"] = System.Text.Encoding.UTF8.GetByteCount(_profileEvidence) });
            }
            _profileCatalogSource = source;
        }
        // The immutable evidence catalog is encoded once per catalog/profile change, then shared by all samples.
        sample.ControllerCatalog = _profileCatalogEnriched;
    }
    private void ClearProfileEvidence()
    {
        _profileContext = ""; _profileEvidence = ""; _profileCatalogSource = null; _profileCatalogEnriched = null;
    }
    private void Disconnect(StreamState stream, string reason)
    {
        stream.Connected = false; _query.Retire(stream.Worker); ClearProfileEvidence();
        stream.SdkReplay = SdkReplayState.Unknown;
        stream.Observation = new ReplaySourceObservation(SourceObservationAvailability.Gap, SourceObservationReason.ConnectionGap);
        stream.ObservationQpc = stream.Capture;
        _coordinator.SourceInterrupted(reason); _facilities.ResetConnection(); _controlSources = Array.Empty<string>();
        Effects.Presentation.Add(new RecorderStatusEventArgs(RecorderState.Waiting, "Recorder.WaitingRetry"));
    }
    public byte[] CaptureCheckpoint()
    {
        if (_effects != null) throw new InvalidOperationException("Cannot checkpoint in the middle of a recording transaction.");
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(0x3753524c); writer.Write(_identity.RuntimeId.ToByteArray()); writer.Write(_identity.CaptureSessionId.ToByteArray());
        writer.Write(_cursor != null); if (_cursor != null) { writer.Write(_cursor.Segment); writer.Write(_cursor.Sequence); }
        writer.Write(_utc.Ticks); writer.Write(_hostSeconds); writer.Write(_nextLookup); writer.Write(_fullRequested);
        writer.Write(_automaticCaptureEnabled); writer.Write(_policyRevision);
        writer.Write(_controlSources.Length); foreach (var label in _controlSources) RuntimeCheckpointCodec.WriteText(writer, label);
        writer.Write(_streams.Count);
        foreach (var stream in _streams.OrderBy(item => item.Key).Select(item => item.Value))
        {
            writer.Write((byte)stream.Worker.Role); writer.Write(stream.Worker.InstanceId.ToByteArray()); writer.Write(stream.Worker.Generation);
            writer.Write(stream.Connection); writer.Write(stream.Sequence); writer.Write(stream.Capture); writer.Write(stream.Connected);
            writer.Write(stream.ObservationQpc); writer.Write((sbyte)stream.SdkReplay); ReplaySourceEvidenceCodec.WriteObservation(writer, stream.Observation);
            writer.Write(stream.Rejected);
            writer.Write(stream.LifecyclePending);
        }
        foreach (var blob in new[] { _controllers.CaptureCheckpoint(), _query.CaptureCheckpoint(), _facilities.CaptureCheckpoint(), _coordinator.CaptureCheckpoint() })
        { writer.Write(blob.Length); writer.Write(blob); }
        writer.Write(_pendingFacilities.Count);
        foreach (var pending in _pendingFacilities)
        { writer.Write(pending.Id.ToByteArray()); writer.Write(pending.Ordinal); }
        writer.Write(_sampleSequence);
        RuntimeCheckpointCodec.WriteText(writer, _profileContext);
        RuntimeCheckpointCodec.WriteText(writer, _profileEvidence, RuntimeWire.MaximumPayloadLength);
        return memory.ToArray();
    }
    public static RecordingRuntimeState RestoreCheckpoint(byte[] bytes, RecoveryJournalIdentity identity)
    {
        if (bytes == null || bytes.Length > 192 * 1024 * 1024) throw new InvalidDataException("Recording checkpoint exceeds its bound.");
        using var memory = new MemoryStream(bytes, false); using var reader = new BinaryReader(memory);
        var version = reader.ReadInt32();
        if ((version < 0x3253524c || version > 0x3753524c || (version & 0xffffff) != 0x53524c) || new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)) != identity.RuntimeId ||
            new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)) != identity.CaptureSessionId) throw new InvalidDataException("Recording checkpoint belongs to another runtime.");
        var state = new RecordingRuntimeState(identity);
        if (RuntimeCheckpointCodec.ReadBoolean(reader)) state._cursor = new JournalCursor(reader.ReadInt64(), reader.ReadInt64());
        state._utc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc); state._hostSeconds = reader.ReadDouble(); state._nextLookup = reader.ReadInt64(); state._fullRequested = RuntimeCheckpointCodec.ReadBoolean(reader);
        state._automaticCaptureEnabled = RuntimeCheckpointCodec.ReadBoolean(reader); state._policyRevision = reader.ReadInt64();
        state._controlSources = new string[RuntimeCheckpointCodec.ReadCount(reader, checked((int)((reader.BaseStream.Length - reader.BaseStream.Position) / sizeof(int))))];
        for (var i = 0; i < state._controlSources.Length; i++) state._controlSources[i] = RuntimeCheckpointCodec.ReadText(reader);
        var count = RuntimeCheckpointCodec.ReadCount(reader, 5);
        for (var i = 0; i < count; i++)
        {
            var role = (ComponentRole)reader.ReadByte();
            var worker = new WorkerIdentity(identity.RuntimeId, new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)), reader.ReadInt64(), role);
            var stream = new StreamState(worker) { Connection = reader.ReadInt64(), Sequence = reader.ReadInt64(), Capture = reader.ReadInt64(), Connected = RuntimeCheckpointCodec.ReadBoolean(reader) };
            stream.ObservationQpc = stream.Capture;
            if (version >= 0x3353524c)
            { stream.ObservationQpc = reader.ReadInt64(); stream.SdkReplay = (SdkReplayState)reader.ReadSByte(); stream.Observation = ReplaySourceEvidenceCodec.ReadObservation(reader); }
            if (version >= 0x3553524c) stream.Rejected = RuntimeCheckpointCodec.ReadBoolean(reader);
            if (version >= 0x3653524c) stream.LifecyclePending = RuntimeCheckpointCodec.ReadBoolean(reader);
            if (stream.Connection < 0 || stream.Sequence <= 0 || stream.Capture < identity.Clock.OriginQpc || (stream.Connected && (role != ComponentRole.Simulator || stream.Connection <= 0)))
                throw new InvalidDataException("Invalid recording source checkpoint.");
            if (!Enum.IsDefined(typeof(SdkReplayState), stream.SdkReplay) || stream.ObservationQpc < 0 || stream.ObservationQpc > stream.Capture)
                throw new InvalidDataException("Invalid recording source evidence checkpoint.");
            state._streams.Add(role, stream);
        }
        byte[] Blob() => RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, 192 * 1024 * 1024));
        state._controllers = ControllerJoiner.RestoreCheckpoint(Blob(), identity.Clock); state._preparation = new ControllerInputPreparation(state._controllers);
        state._query.RestoreCheckpoint(Blob()); state._facilities = EpisodeFacilityCoordinator.RestoreCheckpoint(Blob(), state._query, queryRestored: true);
        state._coordinator = LandingEpisodeCoordinator.RestoreCheckpoint(Blob(), state._facilities, () => state._hostSeconds, () => state._controlSources, () => state._utc);
        state.Attach();
        var pendingCount = RuntimeCheckpointCodec.ReadCount(reader, 4);
        for (var i = 0; i < pendingCount; i++)
        {
            var id = new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)); var ordinal = reader.ReadInt64();
            if (id == Guid.Empty || state._facilities.EpisodeId != ordinal || state._facilities.SnapshotTask == null || state._facilities.SnapshotTask.IsCompleted)
                throw new InvalidDataException("Pending facilities lost their recording context.");
            state._pendingFacilities.Add(new PendingFacilities(id, ordinal, state._facilities.SnapshotTask, state._facilities.DecisionJournal));
        }
        state._sampleSequence = version >= 0x3453524c ? reader.ReadInt64() : Math.Max(state._cursor?.Sequence ?? 0,
            Math.Max(state._coordinator.MaximumRetainedSequence, state._facilities.MaximumRetainedSequence));
        if (version >= 0x3753524c)
        {
            state._profileContext = RuntimeCheckpointCodec.ReadText(reader);
            state._profileEvidence = RuntimeCheckpointCodec.ReadText(reader, RuntimeWire.MaximumPayloadLength);
        }
        if (memory.Position != memory.Length || state._sampleSequence < 0 || state._nextLookup < 0 || state._policyRevision < 0 || double.IsNaN(state._hostSeconds) || double.IsInfinity(state._hostSeconds) || state._hostSeconds < 0)
            throw new InvalidDataException("Invalid recording checkpoint suffix.");
        return state;
    }
    public byte[] CaptureJoinCheckpoint() => _controllers.CaptureCheckpoint();
    private sealed class StreamState
    {
        public StreamState(WorkerIdentity worker) { Worker = worker; }
        public readonly WorkerIdentity Worker;
        public long Connection, Sequence, Capture;
        public bool Connected;
        public bool Rejected;
        public bool LifecyclePending;
        public ReplaySourceObservation Observation = new ReplaySourceObservation(SourceObservationAvailability.Unknown, SourceObservationReason.InitialState);
        public long ObservationQpc;
        public SdkReplayState SdkReplay = SdkReplayState.Unknown;
    }
    private sealed class PendingFacilities
    {
        public PendingFacilities(Guid id, long ordinal, Task<EpisodeFacilitySnapshot> completion, TelemetryDecisionJournal? decisions)
        { Id = id; Ordinal = ordinal; Completion = completion; Decisions = decisions; }
        public readonly Guid Id;
        public readonly long Ordinal;
        public readonly Task<EpisodeFacilitySnapshot> Completion;
        public readonly TelemetryDecisionJournal? Decisions;
    }
}
