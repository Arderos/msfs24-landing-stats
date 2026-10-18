using System;
using System.IO;

namespace LandingStats.Runtime.Contracts;

public enum UploadResultKind : byte { Uploaded = 1, Deferred = 2, Rejected = 3, EnrollmentRequired = 4 }
public sealed class UploadJobMessage : RuntimeCommand
{
    public UploadJobMessage(Guid archive, Guid attempt, string hash, string path, string identityRoot, long policyRevision, bool automatic)
    {
        if (archive == Guid.Empty || attempt == Guid.Empty || !AnalysisJobMessage.ValidHash(hash) || policyRevision < 0) throw new ArgumentException("Invalid upload attempt.");
        RuntimeWire.ValidateText(path, 4096); RuntimeWire.ValidateText(identityRoot, 4096);
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(identityRoot)) throw new ArgumentException("Upload paths are required.");
        Archive = archive; Attempt = attempt; Hash = hash; Path = path; IdentityRoot = identityRoot; PolicyRevision = policyRevision; Automatic = automatic;
    }
    public Guid Archive { get; }
    public Guid Attempt { get; }
    public string Hash { get; }
    public string Path { get; }
    public string IdentityRoot { get; }
    public long PolicyRevision { get; }
    public bool Automatic { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.UploadJob;
    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Archive.ToByteArray()); writer.Write(Attempt.ToByteArray()); RuntimeWire.WriteText(writer, Hash, 64);
        RuntimeWire.WriteText(writer, Path, 4096); RuntimeWire.WriteText(writer, IdentityRoot, 4096); writer.Write(PolicyRevision); writer.Write(Automatic);
    }
    internal static UploadJobMessage Read(BinaryReader reader) => new UploadJobMessage(new Guid(reader.ReadBytes(16)), new Guid(reader.ReadBytes(16)),
        RuntimeWire.ReadText(reader, 64), RuntimeWire.ReadText(reader, 4096), RuntimeWire.ReadText(reader, 4096), reader.ReadInt64(), RuntimeWire.ReadBoolean(reader));
}

public sealed class UploadResultMessage : RuntimePayload
{
    public UploadResultMessage(Guid archive, Guid attempt, string hash, long policyRevision, UploadResultKind result, int statusCode, string reason,
        string receiverKey = "", bool supportsArchiveV2 = false)
    {
        RuntimeWire.ValidateText(reason, 256);
        if (receiverKey == null || receiverKey.Length != 0 && !AnalysisJobMessage.ValidHash(receiverKey) || supportsArchiveV2 && receiverKey.Length == 0)
            throw new ArgumentException("Invalid receiver capability observation.");
        if (archive == Guid.Empty || attempt == Guid.Empty || !AnalysisJobMessage.ValidHash(hash) || policyRevision < 0 ||
            !Enum.IsDefined(typeof(UploadResultKind), result) || statusCode < 0 || statusCode > 599 ||
            result == UploadResultKind.Uploaded && (statusCode < 200 || statusCode > 299) ||
            result == UploadResultKind.Rejected && (statusCode < 400 || statusCode > 499 || statusCode == 429))
            throw new ArgumentException("Invalid upload outcome.");
        Archive = archive; Attempt = attempt; Hash = hash; PolicyRevision = policyRevision; Result = result; StatusCode = statusCode; Reason = reason;
        ReceiverKey = receiverKey; SupportsArchiveV2 = supportsArchiveV2;
    }
    public Guid Archive { get; }
    public Guid Attempt { get; }
    public string Hash { get; }
    public long PolicyRevision { get; }
    public UploadResultKind Result { get; }
    public int StatusCode { get; }
    public string Reason { get; }
    public string ReceiverKey { get; }
    public bool SupportsArchiveV2 { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.UploadResult;
    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Archive.ToByteArray()); writer.Write(Attempt.ToByteArray()); RuntimeWire.WriteText(writer, Hash, 64);
        writer.Write(PolicyRevision); writer.Write((byte)Result); writer.Write(StatusCode); RuntimeWire.WriteText(writer, Reason, 256);
        if (ReceiverKey.Length != 0) { writer.Write((byte)1); RuntimeWire.WriteText(writer, ReceiverKey, 64); writer.Write(SupportsArchiveV2); }
    }
    internal static UploadResultMessage Read(BinaryReader reader)
    {
        var archive = new Guid(reader.ReadBytes(16)); var attempt = new Guid(reader.ReadBytes(16)); var hash = RuntimeWire.ReadText(reader, 64);
        var policy = reader.ReadInt64(); var result = (UploadResultKind)reader.ReadByte(); var status = reader.ReadInt32(); var reason = RuntimeWire.ReadText(reader, 256);
        var key = ""; var supports = false;
        if (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported receiver capability observation.");
            key = RuntimeWire.ReadText(reader, 64); if (key.Length == 0) throw new InvalidDataException("Receiver key is missing.");
            supports = RuntimeWire.ReadBoolean(reader);
        }
        return new UploadResultMessage(archive, attempt, hash, policy, result, status, reason, key, supports);
    }
}
