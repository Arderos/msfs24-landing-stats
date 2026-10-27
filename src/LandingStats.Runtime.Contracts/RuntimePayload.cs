using System;
using System.IO;
using LandingStats.Core;

namespace LandingStats.Runtime.Contracts;

public enum RuntimeMessageKind : ushort
{
    Hello = 1, Frame = 2, GuardFrame = 3, Lifecycle = 4, Aircraft = 5,
    Health = 6, Gap = 7, Command = 8, CommandResult = 9, DurableAck = 10, ControlAck = 11,
    ControllerCatalog = 12, ControllerSnapshot = 13, SceneryCommand = 14, SceneryChunk = 15, SourceDiagnostic = 16, SourceStatus = 17,
    AnalysisJob = 18, AnalysisResult = 19,
    UploadJob = 20, UploadResult = 21,
    CloudJob = 22, CloudResult = 23,
    SourceObservation = 24,
    IntegrationRequest = 25, IntegrationResponse = 26,
    SimulatorLifecycleSnapshot = 27,
    AircraftControls = 28,
}

public abstract class RuntimePayload
{
    public abstract RuntimeMessageKind Kind { get; }
    internal abstract void Write(BinaryWriter writer);
}

public sealed class SourceObservationMessage : RuntimePayload
{
    public SourceObservationMessage(ReplaySourceObservation observation) { Observation = observation ?? throw new ArgumentNullException(nameof(observation)); }
    public ReplaySourceObservation Observation { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.SourceObservation;
    internal override void Write(BinaryWriter writer) => ReplaySourceEvidenceCodec.WriteObservation(writer, Observation);
}

public sealed class WorkerHello : RuntimePayload
{
    public WorkerHello(string build, string launchToken, ulong capabilities, Guid clockEpochId, long qpcFrequency)
    {
        if (clockEpochId == Guid.Empty || qpcFrequency <= 0) throw new ArgumentException("Invalid handshake clock.");
        RuntimeWire.ValidateText(build, 128); RuntimeWire.ValidateText(launchToken, 64);
        if (launchToken.Length != 64) throw new ArgumentException("Invalid launch token.");
        foreach (var c in launchToken) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) throw new ArgumentException("Invalid launch token.");
        Build = build; LaunchToken = launchToken; Capabilities = capabilities; ClockEpochId = clockEpochId; QpcFrequency = qpcFrequency;
    }
    public string Build { get; }
    public string LaunchToken { get; }
    public ulong Capabilities { get; }
    public Guid ClockEpochId { get; }
    public long QpcFrequency { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.Hello;
    internal override void Write(BinaryWriter writer)
    {
        RuntimeWire.WriteText(writer, Build, 128); RuntimeWire.WriteText(writer, LaunchToken, 64);
        writer.Write(Capabilities); writer.Write(ClockEpochId.ToByteArray()); writer.Write(QpcFrequency);
    }
}

public sealed class TelemetryFrameMessage : RuntimePayload
{
    private readonly byte[] _bytes;
    public TelemetryFrameMessage(TelemetrySample sample) { _bytes = TelemetryBinaryCodec.Encode(sample); }
    internal TelemetryFrameMessage(byte[] bytes)
    {
        TelemetryBinaryCodec.Decode(bytes); // Reject malformed values before they can enter the owner queue.
        _bytes = (byte[])bytes.Clone();
    }
    public TelemetrySample CopySample() => TelemetryBinaryCodec.Decode(_bytes);
    public override RuntimeMessageKind Kind => RuntimeMessageKind.Frame;
    internal override void Write(BinaryWriter writer) => writer.Write(_bytes);
}

public sealed class GuardFrameMessage : RuntimePayload
{
    public GuardFrameMessage(double simulationTime, double aglFeet, bool onGround, double verticalVelocity, bool motionSimulation)
    { SimulationTime = simulationTime; AglFeet = aglFeet; OnGround = onGround; VerticalVelocity = verticalVelocity; MotionSimulation = motionSimulation; }
    public double SimulationTime { get; }
    public double AglFeet { get; }
    public bool OnGround { get; }
    public double VerticalVelocity { get; }
    public bool MotionSimulation { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.GuardFrame;
    internal override void Write(BinaryWriter writer)
    { writer.Write(SimulationTime); writer.Write(AglFeet); writer.Write(OnGround); writer.Write(VerticalVelocity); writer.Write(MotionSimulation); }
}

// Flow values are copied as observed integers and interpreted by the versioned source adapter.
public enum SourceLifecycle : byte { Connected = 1, Disconnected = 2, SimulatorQuit = 3, PauseFlags = 4, SimRunning = 5, Flow = 6, ElevatorAxis = 7, PhysicsReady = 8 }
public enum SourceDisconnectionReason { ConnectionClosed, ReceiveFailed, SetupFailed, ModeChangeFailed, SilentStream, OpenTimeout, DefinitionRejected, SimulatorQuit }
public sealed class LifecycleMessage : RuntimePayload
{
    public LifecycleMessage(SourceLifecycle state, int value = 0)
    { if (!Enum.IsDefined(typeof(SourceLifecycle), state)) throw new ArgumentException("Unknown lifecycle event."); State = state; Value = value; }
    public SourceLifecycle State { get; }
    public int Value { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.Lifecycle;
    internal override void Write(BinaryWriter writer) { writer.Write((byte)State); writer.Write(Value); }
}

public sealed class AircraftMessage : RuntimePayload
{
    public AircraftMessage(string simulator, string title, string type, string model)
    {
        RuntimeWire.ValidateText(simulator, 128); RuntimeWire.ValidateText(title, 512);
        RuntimeWire.ValidateText(type, 256); RuntimeWire.ValidateText(model, 256);
        Simulator = simulator; Title = title; Type = type; Model = model;
    }
    public string Simulator { get; }
    public string Title { get; }
    public string Type { get; }
    public string Model { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.Aircraft;
    internal override void Write(BinaryWriter writer)
    { RuntimeWire.WriteText(writer, Simulator, 128); RuntimeWire.WriteText(writer, Title, 512); RuntimeWire.WriteText(writer, Type, 256); RuntimeWire.WriteText(writer, Model, 256); }
}

public enum ComponentPhase : byte { Starting = 1, Running = 2, Waiting = 3, Recovering = 4, Stopping = 5, Failed = 6 }
public enum DependencyWait : byte { None = 0, Idle = 1, Simulator = 2, Pause = 3, Loading = 4, Device = 5, Storage = 6, Network = 7, Authorization = 8 }
public sealed class ComponentHealthMessage : RuntimePayload
{
    public ComponentHealthMessage(ComponentPhase phase, DependencyWait waitingFor, long ownerProgress, long usefulProgress,
        long ownerProgressQpc, long oldestPendingQpc, int queueDepth, Guid operationId)
    {
        if (!Enum.IsDefined(typeof(ComponentPhase), phase) || !Enum.IsDefined(typeof(DependencyWait), waitingFor) ||
            ownerProgress < 0 || usefulProgress < 0 || ownerProgressQpc < 0 || oldestPendingQpc < 0 || queueDepth < 0)
            throw new ArgumentException("Invalid component health.");
        Phase = phase; WaitingFor = waitingFor; OwnerProgress = ownerProgress; UsefulProgress = usefulProgress;
        OwnerProgressQpc = ownerProgressQpc; OldestPendingQpc = oldestPendingQpc; QueueDepth = queueDepth; OperationId = operationId;
    }
    public ComponentPhase Phase { get; }
    public DependencyWait WaitingFor { get; }
    public long OwnerProgress { get; }
    public long UsefulProgress { get; }
    public long OwnerProgressQpc { get; }
    public long OldestPendingQpc { get; }
    public int QueueDepth { get; }
    public Guid OperationId { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.Health;
    internal override void Write(BinaryWriter writer)
    {
        writer.Write((byte)Phase); writer.Write((byte)WaitingFor); writer.Write(OwnerProgress); writer.Write(UsefulProgress);
        writer.Write(OwnerProgressQpc); writer.Write(OldestPendingQpc); writer.Write(QueueDepth); writer.Write(OperationId.ToByteArray());
    }
}

public enum SourceGapReason : byte { ProcessExit = 1, OwnerStalled = 2, BufferOverflow = 3, ProtocolFailure = 4, ConnectionLost = 5, ClockEpochChanged = 6 }
public sealed class SourceGapMessage : RuntimePayload
{
    public SourceGapMessage(SourceGapReason reason, long firstMissing, long lastMissing, bool lossCountKnown)
    {
        if (!Enum.IsDefined(typeof(SourceGapReason), reason) || firstMissing <= 0 || lastMissing < firstMissing)
            throw new ArgumentException("Invalid source gap.");
        Reason = reason; FirstMissing = firstMissing; LastMissing = lastMissing; LossCountKnown = lossCountKnown;
    }
    public SourceGapReason Reason { get; }
    public long FirstMissing { get; }
    public long LastMissing { get; }
    public bool LossCountKnown { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.Gap;
    internal override void Write(BinaryWriter writer) { writer.Write((byte)Reason); writer.Write(FirstMissing); writer.Write(LastMissing); writer.Write(LossCountKnown); }
}

public enum WorkerCommand : byte { Stop = 1, Probe = 2, RawCapture = 3, FullTelemetry = 4, Metadata = 5, Airports = 6, Start = 7 }
public abstract class RuntimeCommand : RuntimePayload { }
public sealed class WorkerCommandMessage : RuntimeCommand
{
    public WorkerCommandMessage(WorkerCommand command, bool enabled = false)
    { if (!Enum.IsDefined(typeof(WorkerCommand), command)) throw new ArgumentException("Unknown command."); Command = command; Enabled = enabled; }
    public WorkerCommand Command { get; }
    public bool Enabled { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.Command;
    internal override void Write(BinaryWriter writer) { writer.Write((byte)Command); writer.Write(Enabled); }
}

public enum CommandOutcome : byte { Completed = 1, Rejected = 2, Cancelled = 3, Failed = 4 }
public sealed class CommandResultMessage : RuntimePayload
{
    public CommandResultMessage(CommandOutcome outcome, string reason)
    {
        if (!Enum.IsDefined(typeof(CommandOutcome), outcome)) throw new ArgumentException("Unknown command outcome.");
        RuntimeWire.ValidateText(reason, 1024); Outcome = outcome; Reason = reason;
    }
    public CommandOutcome Outcome { get; }
    public string Reason { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.CommandResult;
    internal override void Write(BinaryWriter writer) { writer.Write((byte)Outcome); RuntimeWire.WriteText(writer, Reason, 1024); }
}

public sealed class DurableAckMessage : RuntimePayload
{
    public DurableAckMessage(long sequence)
    { if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence)); Sequence = sequence; }
    public long Sequence { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.DurableAck;
    internal override void Write(BinaryWriter writer) => writer.Write(Sequence);
}

// Control traffic is disposable; this acknowledgement never advances a journal checkpoint.
public sealed class ControlAckMessage : RuntimePayload
{
    public ControlAckMessage(long sequence)
    { if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence)); Sequence = sequence; }
    public long Sequence { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.ControlAck;
    internal override void Write(BinaryWriter writer) => writer.Write(Sequence);
}
