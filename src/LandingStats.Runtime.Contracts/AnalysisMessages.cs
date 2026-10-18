using System;
using System.IO;
using System.Linq;

namespace LandingStats.Runtime.Contracts;

public enum AnalysisOperation : byte { Analyze = 1, FinalizeReport = 2, RecoverRecording = 3, ResolveGeometry = 4, RefreshGeometryCatalog = 5, ResolvePreparedGeometry = 6 }

public sealed class AnalysisJobMessage : RuntimeCommand
{
    public AnalysisJobMessage(Guid job, Guid episode, string engine, string inputRoot, string outputDirectory, bool finalizeReport = false)
        : this(job, episode, engine, inputRoot, outputDirectory, finalizeReport ? AnalysisOperation.FinalizeReport : AnalysisOperation.Analyze) { }
    public AnalysisJobMessage(Guid job, Guid episode, string engine, string inputRoot, string outputDirectory, AnalysisOperation operation, string simulator = "")
    {
        if (job == Guid.Empty || episode == Guid.Empty || !ValidHash(engine) || !Enum.IsDefined(typeof(AnalysisOperation), operation)) throw new ArgumentException("Invalid analysis job identity.");
        RuntimeWire.ValidateText(inputRoot, 4096); RuntimeWire.ValidateText(outputDirectory, 4096);
        if (string.IsNullOrWhiteSpace(inputRoot) || string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Analysis job paths are required.");
        RuntimeWire.ValidateText(simulator, 128);
        if ((operation == AnalysisOperation.RefreshGeometryCatalog) != !string.IsNullOrWhiteSpace(simulator)) throw new ArgumentException("Catalog refresh requires a simulator identity.");
        Job = job; Episode = episode; Engine = engine; InputRoot = inputRoot; OutputDirectory = outputDirectory; Operation = operation; Simulator = simulator;
    }
    public Guid Job { get; }
    public Guid Episode { get; }
    public string Engine { get; }
    public string InputRoot { get; }
    public string OutputDirectory { get; }
    public AnalysisOperation Operation { get; }
    public string Simulator { get; }
    public bool FinalizeReport => Operation == AnalysisOperation.FinalizeReport;
    public override RuntimeMessageKind Kind => RuntimeMessageKind.AnalysisJob;
    internal static bool ValidHash(string hash) => hash != null && hash.Length == 64 && hash.All(c => c >= 'a' && c <= 'f' || c >= '0' && c <= '9');
    internal override void Write(BinaryWriter writer)
    { writer.Write(Job.ToByteArray()); writer.Write(Episode.ToByteArray()); RuntimeWire.WriteText(writer, Engine, 64); RuntimeWire.WriteText(writer, InputRoot, 4096); RuntimeWire.WriteText(writer, OutputDirectory, 4096); writer.Write((byte)Operation);
        if (Operation == AnalysisOperation.RefreshGeometryCatalog) RuntimeWire.WriteText(writer, Simulator, 128); }
    internal static AnalysisJobMessage Read(BinaryReader reader)
    {
        var job = new Guid(reader.ReadBytes(16)); var episode = new Guid(reader.ReadBytes(16));
        var engine = RuntimeWire.ReadText(reader, 64); var input = RuntimeWire.ReadText(reader, 4096); var output = RuntimeWire.ReadText(reader, 4096);
        var operation = (AnalysisOperation)reader.ReadByte();
        return new AnalysisJobMessage(job, episode, engine, input, output, operation,
            operation == AnalysisOperation.RefreshGeometryCatalog ? RuntimeWire.ReadText(reader, 128) : "");
    }
}

public sealed class AnalysisResultMessage : RuntimePayload
{
    public const long MaximumBytes = 256L * 1024 * 1024;
    public AnalysisResultMessage(Guid job, string hash, long length, string failure = "")
    {
        RuntimeWire.ValidateText(failure, 256);
        if (job == Guid.Empty || (failure.Length == 0 ? !AnalysisJobMessage.ValidHash(hash) || length <= 0 || length > MaximumBytes : hash != "" || length != 0))
            throw new ArgumentException("Invalid analysis result.");
        Job = job; Hash = hash; Length = length; Failure = failure;
    }
    public Guid Job { get; }
    public string Hash { get; }
    public long Length { get; }
    public string Failure { get; }
    public override RuntimeMessageKind Kind => RuntimeMessageKind.AnalysisResult;
    internal override void Write(BinaryWriter writer)
    { writer.Write(Job.ToByteArray()); RuntimeWire.WriteText(writer, Hash, 64); writer.Write(Length); RuntimeWire.WriteText(writer, Failure, 256); }
    internal static AnalysisResultMessage Read(BinaryReader reader) => new AnalysisResultMessage(new Guid(reader.ReadBytes(16)), RuntimeWire.ReadText(reader, 64), reader.ReadInt64(), RuntimeWire.ReadText(reader, 256));
}
