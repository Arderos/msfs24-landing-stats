using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LandingStats.Runtime.Contracts;

public enum ComponentRole : byte { Simulator = 1, Controllers = 2, Upload = 3, CloudSync = 4, Analysis = 5, IntegrationApi = 6 }
public enum LookupStage : byte { None = 0, Preliminary = 1, Final = 2 }

public sealed class WorkerIdentity : IEquatable<WorkerIdentity>
{
    public WorkerIdentity(Guid appSessionId, Guid instanceId, long generation, ComponentRole role)
    {
        if (appSessionId == Guid.Empty || instanceId == Guid.Empty || generation <= 0 || !Enum.IsDefined(typeof(ComponentRole), role))
            throw new ArgumentException("Invalid worker identity.");
        AppSessionId = appSessionId; InstanceId = instanceId; Generation = generation; Role = role;
    }
    public Guid AppSessionId { get; }
    public Guid InstanceId { get; }
    public long Generation { get; }
    public ComponentRole Role { get; }
    public bool Equals(WorkerIdentity? other) => other != null && AppSessionId == other.AppSessionId &&
        InstanceId == other.InstanceId && Generation == other.Generation && Role == other.Role;
    public override bool Equals(object? obj) => Equals(obj as WorkerIdentity);
    public override int GetHashCode() => AppSessionId.GetHashCode() ^ InstanceId.GetHashCode() ^ Generation.GetHashCode() ^ (int)Role;
}

/// <summary>Immutable source identity and capture time. Receipt time belongs to the receiving ingress.</summary>
public sealed class RuntimeEnvelope
{
    public RuntimeEnvelope(WorkerIdentity worker, long connectionGeneration, long sequence, long captureQpc, RuntimePayload payload,
        Guid correlationId = default, long commandRevision = 0, Guid episodeId = default, Guid lookupId = default, LookupStage lookupStage = LookupStage.None)
    {
        if (connectionGeneration < 0 || sequence <= 0 || captureQpc < 0 || commandRevision < 0 || !Enum.IsDefined(typeof(LookupStage), lookupStage))
            throw new ArgumentException("Invalid runtime envelope.");
        if ((lookupStage == LookupStage.None) != (lookupId == Guid.Empty) || (lookupId != Guid.Empty && episodeId == Guid.Empty))
            throw new ArgumentException("Incomplete facility lookup context.");
        Worker = worker ?? throw new ArgumentNullException(nameof(worker));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        if (worker.Role != ComponentRole.Simulator && (connectionGeneration != 0 || payload is TelemetryFrameMessage ||
            payload is GuardFrameMessage || payload is LifecycleMessage || payload is SimulatorLifecycleSnapshotMessage || payload is AircraftMessage || payload is AircraftControlsMessage || payload is SceneryCommandMessage || payload is SceneryChunkMessage))
            throw new ArgumentException("Payload does not belong to this source role.");
        if ((payload is TelemetryFrameMessage || payload is GuardFrameMessage || payload is SimulatorLifecycleSnapshotMessage || payload is AircraftControlsMessage) && connectionGeneration == 0)
            throw new ArgumentException("A simulator frame requires a connection generation.");
        if ((payload is ControllerCatalogMessage || payload is ControllerSnapshotMessage) && worker.Role != ComponentRole.Controllers)
            throw new ArgumentException("Controller data requires the controller source role.");
        if ((payload is AnalysisJobMessage || payload is AnalysisResultMessage) && worker.Role != ComponentRole.Analysis)
            throw new ArgumentException("Analysis work requires the analysis role.");
        if ((payload is UploadJobMessage || payload is UploadResultMessage) && worker.Role != ComponentRole.Upload)
            throw new ArgumentException("Upload work requires the upload role.");
        if ((payload is CloudJobMessage || payload is CloudResultMessage) && worker.Role != ComponentRole.CloudSync)
            throw new ArgumentException("Cloud work requires the cloud role.");
        if ((payload is IntegrationRequestMessage || payload is IntegrationResponseCommand) && worker.Role != ComponentRole.IntegrationApi)
            throw new ArgumentException("Integration work requires the integration API role.");
        if (payload is SceneryChunkMessage && (connectionGeneration == 0 || lookupId == Guid.Empty))
            throw new ArgumentException("Scenery replies require their complete source and lookup context.");
        if ((payload is RuntimeCommand || payload is CommandResultMessage) && (correlationId == Guid.Empty || commandRevision == 0))
            throw new ArgumentException("Command correlation and revision are required.");
        if (payload is WorkerCommandMessage command && worker.Role != ComponentRole.Simulator &&
            command.Command != WorkerCommand.Stop && command.Command != WorkerCommand.Probe && command.Command != WorkerCommand.Start)
            throw new ArgumentException("Command does not belong to this worker role.");
        ConnectionGeneration = connectionGeneration; Sequence = sequence; CaptureQpc = captureQpc;
        CorrelationId = correlationId; CommandRevision = commandRevision; EpisodeId = episodeId; LookupId = lookupId; LookupStage = lookupStage;
    }
    public WorkerIdentity Worker { get; }
    public long ConnectionGeneration { get; }
    public long Sequence { get; }
    public long CaptureQpc { get; }
    public RuntimePayload Payload { get; }
    public Guid CorrelationId { get; }
    public long CommandRevision { get; }
    public Guid EpisodeId { get; }
    public Guid LookupId { get; }
    public LookupStage LookupStage { get; }
}

public sealed class RuntimeClockEpoch
{
    public RuntimeClockEpoch(Guid id, long originQpc, long frequency)
    {
        if (id == Guid.Empty || originQpc < 0 || frequency <= 0) throw new ArgumentException("Invalid clock epoch.");
        Id = id; OriginQpc = originQpc; Frequency = frequency;
    }
    public Guid Id { get; }
    public long OriginQpc { get; }
    public long Frequency { get; }
    public double ElapsedSeconds(Guid epochId, long qpc, long frequency)
    {
        if (epochId != Id || frequency != Frequency || qpc < OriginQpc) throw new InvalidDataException("Unrelated clock epoch.");
        return (qpc - OriginQpc) / (double)Frequency;
    }
}

/// <summary>Persist the capture session once; retries and recovery derive the same identities from it.</summary>
public static class StableRuntimeIdentity
{
    public static Guid Episode(Guid captureSession, long firstSequence) => Derive("episode", captureSession, firstSequence, 0);
    public static Guid Segment(Guid episode, long firstSequence, int ordinal) => Derive("segment", episode, firstSequence, ordinal);
    public static Guid Contact(Guid segment, long contactSequence, int ordinal) => Derive("contact", segment, contactSequence, ordinal);
    public static Guid Lookup(Guid episode, long sequence, int stage) => Derive("lookup", episode, sequence, stage);
    public static Guid Command(Guid input, int ordinal) => Derive("command", input, 0, ordinal);

    private static Guid Derive(string kind, Guid parent, long sequence, int ordinal)
    {
        if (parent == Guid.Empty || sequence < 0 || ordinal < 0) throw new ArgumentException("Invalid stable identity input.");
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write("LS-RUNTIME-ID/1/" + kind); writer.Write(parent.ToByteArray()); writer.Write(sequence); writer.Write(ordinal);
        }
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(buffer.ToArray());
        var id = new byte[16]; Array.Copy(hash, id, id.Length);
        return new Guid(id);
    }
}
