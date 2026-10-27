using System;
using System.IO;
using System.Linq;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal interface IRuntimeInputPreparation
{
    RuntimeInput Prepare(RuntimeInput accepted);
    void Replay(RuntimeInput committed);
    bool Matches(RuntimeInput accepted, RuntimeInput committed);
}

/// <summary>The original source message and the exact chosen axes travel in one
/// WAL record. A crash cannot leave an acknowledged frame with an unrecorded join.</summary>
internal sealed class JoinedRuntimeFrame
{
    public JoinedRuntimeFrame(RuntimeEnvelope source, ControllerJoinDecision controllers)
    {
        if (!(source.Payload is TelemetryFrameMessage) || source.CaptureQpc != controllers.FrameCaptureQpc)
            throw new InvalidDataException("Join belongs to a different simulator frame.");
        Source = source; Controllers = controllers;
    }
    public RuntimeEnvelope Source { get; }
    public ControllerJoinDecision Controllers { get; }
    public byte[] Encode()
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(0x31464a4c);
        foreach (var bytes in new[] { RuntimeWire.Encode(Source), Controllers.Encode() }) { writer.Write(bytes.Length); writer.Write(bytes); }
        return memory.ToArray();
    }
    public static JoinedRuntimeFrame Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length > 3 * (RuntimeWire.MaximumPayloadLength + RuntimeWire.HeaderLength) + 256) throw new InvalidDataException("Joined frame exceeds its bound.");
        using var memory = new MemoryStream(bytes, false); using var reader = new BinaryReader(memory);
        if (reader.ReadInt32() != 0x31464a4c) throw new InvalidDataException("Unsupported joined frame.");
        var source = RuntimeWire.Decode(RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, RuntimeWire.MaximumPayloadLength + RuntimeWire.HeaderLength)));
        var decision = ControllerJoinDecision.Decode(RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, 2 * (RuntimeWire.MaximumPayloadLength + RuntimeWire.HeaderLength) + 64)));
        if (memory.Position != memory.Length) throw new InvalidDataException("Joined frame has an unexpected suffix.");
        return new JoinedRuntimeFrame(source, decision);
    }
}

internal sealed class ControllerInputPreparation : IRuntimeInputPreparation
{
    private readonly ControllerJoiner _joiner;
    public ControllerInputPreparation(ControllerJoiner joiner) { _joiner = joiner; }
    public byte[] CaptureCheckpoint() => _joiner.CaptureCheckpoint();
    public Exception? LastFailure { get; private set; }
    public RuntimeInput Prepare(RuntimeInput accepted)
    {
        Replay(accepted);
        if (LastFailure != null) return accepted;
        if (accepted.Kind == JournalRecordKind.SourceMessage)
        {
            var source = RuntimeWire.Decode(accepted.CopyContent());
            if (source.Payload is TelemetryFrameMessage)
                return new RuntimeInput(accepted.Id, JournalRecordKind.JoinedFrame, accepted.AcceptedQpc, accepted.ArrivedQpc, accepted.TimestampUtc,
                    new JoinedRuntimeFrame(source, _joiner.Join(source.CaptureQpc)).Encode());
        }
        return accepted;
    }
    public void Replay(RuntimeInput committed)
    {
        LastFailure = null;
        try { ReplaySource(committed); }
        catch (Exception error) when ((committed.Kind == JournalRecordKind.SourceMessage || committed.Kind == JournalRecordKind.JoinedFrame) &&
            (error is InvalidDataException || error is ArgumentException || error is EndOfStreamException))
        {
            LastFailure = error;
            if (_joiner.ActiveWorker != null) _joiner.Retire(_joiner.ActiveWorker);
        }
    }
    private void ReplaySource(RuntimeInput committed)
    {
        if (committed.Kind == JournalRecordKind.RuntimeCommand && RecordingCommand.Decode(committed.CopyContent()).Kind == RecordingCommandKind.PreviousRuntimeEnded)
        {
            if (_joiner.ActiveWorker != null) _joiner.Retire(_joiner.ActiveWorker);
            _joiner.ClearAircraftControls();
            return;
        }
        if (committed.Kind == JournalRecordKind.SourceRetirement)
        {
            var retirement = RuntimeSourceRetirement.Decode(committed.CopyContent());
            if (retirement.Worker.Role == ComponentRole.Controllers) _joiner.Retire(retirement.Worker);
            if (retirement.Worker.Role == ComponentRole.Simulator) _joiner.ClearAircraftControls();
        }
        else if (committed.Kind == JournalRecordKind.SourceMessage)
        {
            var message = RuntimeWire.Decode(committed.CopyContent());
            if (message.Worker.Role == ComponentRole.Simulator)
            {
                if (message.Payload is AircraftControlsMessage) _joiner.ObserveAircraftControls(message);
                else if (message.Payload is WorkerHello || message.Payload is SourceGapMessage ||
                    message.Payload is AircraftMessage aircraft && aircraft.Title == "Unknown aircraft" ||
                    message.Payload is LifecycleMessage lifecycle && (lifecycle.State == SourceLifecycle.Connected ||
                        lifecycle.State == SourceLifecycle.Disconnected || lifecycle.State == SourceLifecycle.SimulatorQuit))
                    _joiner.ClearAircraftControls();
                return;
            }
            if (message.Worker.Role != ComponentRole.Controllers) return;
            if (message.Payload is WorkerHello && _joiner.ActiveWorker == null) _joiner.Activate(message.Worker);
            if (_joiner.ActiveWorker == null) return; // A rejected generation stays unavailable until its replacement Hello.
            if (!_joiner.Accept(message)) throw new InvalidDataException("Controller source crossed a retirement boundary.");
        }
        else if (committed.Kind == JournalRecordKind.JoinedFrame) JoinedRuntimeFrame.Decode(committed.CopyContent());
    }
    public bool Matches(RuntimeInput accepted, RuntimeInput committed)
    {
        if (accepted.Id != committed.Id || accepted.AcceptedQpc != committed.AcceptedQpc || accepted.ArrivedQpc != committed.ArrivedQpc || accepted.TimestampUtc != committed.TimestampUtc) return false;
        if (accepted.Kind == committed.Kind) return accepted.CopyContent().SequenceEqual(committed.CopyContent());
        return accepted.Kind == JournalRecordKind.SourceMessage && committed.Kind == JournalRecordKind.JoinedFrame &&
            RuntimeWire.Encode(JoinedRuntimeFrame.Decode(committed.CopyContent()).Source).SequenceEqual(accepted.CopyContent());
    }
}
