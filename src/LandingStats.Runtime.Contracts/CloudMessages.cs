using System;
using System.IO;

namespace LandingStats.Runtime.Contracts;

public sealed class CloudJobMessage : RuntimeCommand
{
    public CloudJobMessage(Guid job, long credentialRevision, string directory, string requestHash, string credentialHash)
    {
        RuntimeWire.ValidateText(directory, 4096);
        if (job == Guid.Empty || credentialRevision < 1 || string.IsNullOrWhiteSpace(directory) ||
            !AnalysisJobMessage.ValidHash(requestHash) || credentialHash != "" && !AnalysisJobMessage.ValidHash(credentialHash)) throw new ArgumentException("Invalid cloud operation.");
        Job = job; CredentialRevision = credentialRevision; Directory = directory; RequestHash = requestHash; CredentialHash = credentialHash;
    }
    public Guid Job { get; }
    public long CredentialRevision { get; }
    public string Directory { get; }
    public string RequestHash { get; }
    public string CredentialHash { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.CloudJob;
    internal override void Write(BinaryWriter writer)
    { writer.Write(Job.ToByteArray()); writer.Write(CredentialRevision); RuntimeWire.WriteText(writer, Directory, 4096); RuntimeWire.WriteText(writer, RequestHash, 64); RuntimeWire.WriteText(writer, CredentialHash, 64); }
    internal static CloudJobMessage Read(BinaryReader reader) => new CloudJobMessage(new Guid(reader.ReadBytes(16)), reader.ReadInt64(), RuntimeWire.ReadText(reader, 4096), RuntimeWire.ReadText(reader, 64), RuntimeWire.ReadText(reader, 64));
}
public sealed class CloudResultMessage : RuntimePayload
{
    public const long MaximumMetadataBytes = 64L * 1024 * 1024;
    public CloudResultMessage(Guid job, long credentialRevision, string hash, long length, string failure = "")
    {
        RuntimeWire.ValidateText(failure, 256);
        if (job == Guid.Empty || credentialRevision < 1 || (failure.Length == 0 ? !AnalysisJobMessage.ValidHash(hash) || length <= 0 || length > MaximumMetadataBytes : hash != "" || length != 0))
            throw new ArgumentException("Invalid cloud result.");
        Job = job; CredentialRevision = credentialRevision; Hash = hash; Length = length; Failure = failure;
    }
    public Guid Job { get; }
    public long CredentialRevision { get; }
    public string Hash { get; }
    public long Length { get; }
    public string Failure { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.CloudResult;
    internal override void Write(BinaryWriter writer)
    { writer.Write(Job.ToByteArray()); writer.Write(CredentialRevision); RuntimeWire.WriteText(writer, Hash, 64); writer.Write(Length); RuntimeWire.WriteText(writer, Failure, 256); }
    internal static CloudResultMessage Read(BinaryReader reader) => new CloudResultMessage(new Guid(reader.ReadBytes(16)), reader.ReadInt64(), RuntimeWire.ReadText(reader, 64), reader.ReadInt64(), RuntimeWire.ReadText(reader, 256));
}
