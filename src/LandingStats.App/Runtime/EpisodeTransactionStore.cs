using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.Runtime;

internal sealed class StoredEpisodeInput
{
    public StoredEpisodeInput(EpisodeInputMetadata metadata, LandingEpisodeEventArgs episode)
    { Metadata = metadata; Episode = episode; }
    public EpisodeInputMetadata Metadata { get; }
    public LandingEpisodeEventArgs Episode { get; }
}

[DataContract]
internal sealed class EpisodeAnalysisData
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Id { get; set; }
    [DataMember(Order = 3)] public string Engine { get; set; } = "";
    [DataMember(Order = 4)] public string InputHash { get; set; } = "";
    [DataMember(Order = 5)] public bool ReplayLike { get; set; }
    [DataMember(Order = 6)] public AirportFacility[] Airports { get; set; } = Array.Empty<AirportFacility>();
    [DataMember(Order = 7)] public LandingRecordFile[] Records { get; set; } = Array.Empty<LandingRecordFile>();
    [DataMember(Order = 8)] public DecisionPersistenceData Decisions { get; set; } = new DecisionPersistenceData();
    [DataMember(Order = 9)] public string FacilityHash { get; set; } = "";
    [DataMember(Order = 10, EmitDefaultValue = false)] public string GeometryHash { get; set; } = "";
}

/// <summary>Single-owner per-episode transactions. Every marker follows publication of its immutable data.</summary>
internal sealed partial class EpisodeTransactionStore
{
    private readonly ConcurrentDictionary<Guid, object> _gates = new ConcurrentDictionary<Guid, object>();
    private object Gate(Guid id) => _gates.GetOrAdd(id, _ => new object());
    private const int InputMagic = 0x31495045; // EPI1
    private const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private const int MaximumFrames = 65536;
    private const long MaximumAnalysisBytes = 256L * 1024 * 1024;
    public EpisodeTransactionStore(string root, string engine)
    {
        if (!ValidHash(engine)) throw new ArgumentException("An exact processing engine hash is required.", nameof(engine));
        Root = Path.GetFullPath(root); Engine = engine;
    }
    public string Root { get; }
    public string Engine { get; }
    private string HistoryRoot => Path.Combine(Path.GetDirectoryName(Root)!, "episode-history");
    private string DirectoryFor(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("An episode id is required.");
        var active = Path.Combine(Root, id.ToString("N")); var history = Path.Combine(HistoryRoot, id.ToString("N"));
        return !Directory.Exists(active) && Directory.Exists(history) ? history : active;
    }
    private string PathFor(Guid id, string name) => Path.Combine(DirectoryFor(id), name);
    private void Save(EpisodeWorkManifest manifest)
    { EngineReference.Write(DirectoryFor(manifest.Id), manifest.Engine); DurableFile.WriteJson(PathFor(manifest.Id, "manifest.json"), manifest); }

    public EpisodeWorkManifest Read(Guid id)
    {
        lock (Gate(id)) return ReadOwned(id);
    }
    private EpisodeWorkManifest ReadOwned(Guid id)
    {
        var value = DurableFile.ReadJson<EpisodeWorkManifest>(PathFor(id, "manifest.json"), 32768);
        if (value.Version != 2 || value.Id != id || value.ArchiveId == Guid.Empty || !ValidHash(value.Engine) ||
            !Enum.IsDefined(typeof(EpisodeWorkStage), value.Stage) || value.PolicyRevision < 0 || value.FailureCount < 0 ||
            (value.Stage >= EpisodeWorkStage.InputsCommitted && !ValidHash(value.InputHash)) ||
            (value.Stage >= EpisodeWorkStage.Analysed && !ValidHash(value.AnalysisHash)) ||
            (!string.IsNullOrEmpty(value.FacilityHash) && !ValidHash(value.FacilityHash)) ||
            (!string.IsNullOrEmpty(value.GeometryHash) && !ValidHash(value.GeometryHash)) ||
            (value.Stage >= EpisodeWorkStage.ReportReady && (!ValidHash(value.FacilityHash) || !ValidHash(value.ReportHash))) ||
            (value.Stage >= EpisodeWorkStage.ArchivePublished &&
                (string.IsNullOrEmpty(value.ArchiveSkipReason) ? !ValidHash(value.ArchiveHash) || string.IsNullOrEmpty(value.ArchivePath) :
                    !string.IsNullOrEmpty(value.ArchivePath) || !string.IsNullOrEmpty(value.ArchiveHash))))
            throw new InvalidDataException("Invalid episode manifest; original files retained.");
        return value;
    }

    public IEnumerable<Guid> Enumerate()
    {
        foreach (var root in new[] { Root, HistoryRoot })
        foreach (var directory in Directory.Exists(root) ? Directory.EnumerateDirectories(root) : Array.Empty<string>())
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && File.Exists(Path.Combine(directory, "manifest.json")))
                yield return id;
    }
    public IEnumerable<Guid> EnumeratePending() => Directory.Exists(Root) ? Directory.EnumerateDirectories(Root)
        .Select(Path.GetFileName).Where(name => Guid.TryParseExact(name, "N", out _)).Select(Guid.Parse) : Array.Empty<Guid>();
    public bool ArchiveCompleted(Guid id)
    {
        lock (Gate(id))
        {
            var active = Path.Combine(Root, id.ToString("N"));
            if (!Directory.Exists(active)) return false;
            var manifest = Read(id); if (manifest.Stage != EpisodeWorkStage.Completed) return false;
            ReadReport(manifest); // Manual reports retain their immutable inputs and final report.
            Directory.CreateDirectory(HistoryRoot);
            var destination = Path.Combine(HistoryRoot, id.ToString("N"));
            if (Directory.Exists(destination)) throw new IOException("Completed episode has two storage locations; both retained.");
            Directory.Move(active, destination);
            return true;
        }
    }

    public EpisodeWorkManifest CommitInputs(LandingEpisodeEventArgs episode, DateTime timestamp,
        IReadOnlyList<AirportFacility> known, bool automaticAllowed, long policyRevision)
    {
        lock (Gate(episode.PersistentId)) return CommitInputsOwned(episode, timestamp, known, automaticAllowed, policyRevision);
    }
    private EpisodeWorkManifest CommitInputsOwned(LandingEpisodeEventArgs episode, DateTime timestamp,
        IReadOnlyList<AirportFacility> known, bool automaticAllowed, long policyRevision)
    {
        if (policyRevision < 0 || episode.Samples.Count > MaximumFrames || episode.InitialFacilities.ApproachSamples.Count > MaximumFrames)
            throw new ArgumentException("Episode inputs exceed their bounds.");
        var exists = File.Exists(PathFor(episode.PersistentId, "manifest.json"));
        var manifest = exists ? Read(episode.PersistentId) : new EpisodeWorkManifest
        {
            Id = episode.PersistentId, Engine = Engine, TimestampUtc = timestamp.ToUniversalTime(), ArchiveId = Guid.NewGuid(),
            AutomaticAllowedAtCapture = automaticAllowed, PolicyRevision = policyRevision
        };
        if (manifest.Stage >= EpisodeWorkStage.InputsCommitted) return manifest;
        RequireEngine(manifest);
        if (!exists) Save(manifest); // Identity/timestamp/policy precede the first data file.
        var path = PathFor(manifest.Id, "inputs.bin.gz");
        if (!File.Exists(path))
        {
            var location = episode.Samples.LastOrDefault(sample => !double.IsNaN(sample.LatitudeDegrees) && !double.IsInfinity(sample.LatitudeDegrees) &&
                !double.IsNaN(sample.LongitudeDegrees) && !double.IsInfinity(sample.LongitudeDegrees));
            var nearby = location == null ? Array.Empty<AirportFacility>() : known
                .Select(airport => new { Airport = airport, Distance = GeoDistance.GreatCircleNauticalMiles(location.LatitudeDegrees, location.LongitudeDegrees, airport.LatitudeDegrees, airport.LongitudeDegrees) })
                .Where(item => item.Distance <= 200).OrderBy(item => item.Distance).Take(1024).Select(item => item.Airport).ToArray();
            var metadata = EpisodeInputMetadata.Capture(episode, manifest.TimestampUtc, nearby);
            DurableFile.Write(path, output =>
            {
                using var gzip = new GZipStream(output, CompressionLevel.Optimal, true);
                using var writer = new BinaryWriter(gzip, Encoding.UTF8, true);
                writer.Write(InputMagic); WriteJson(writer, metadata);
                WriteFrames(writer, episode.Samples); WriteFrames(writer, episode.InitialFacilities.ApproachSamples);
            });
        }
        // A crash can leave a complete input file ahead of its marker. Adopt it
        // only after reading its bounded format and checking the persisted identity.
        ReadInputFile(manifest, path);
        manifest.InputHash = Hash(path); manifest.Stage = EpisodeWorkStage.InputsCommitted; Save(manifest);
        return manifest;
    }

    public StoredEpisodeInput ReadInputs(EpisodeWorkManifest manifest)
    {
        if (manifest.Stage < EpisodeWorkStage.InputsCommitted) throw new InvalidOperationException("Inputs are not committed.");
        var path = PathFor(manifest.Id, "inputs.bin.gz"); Verify(path, manifest.InputHash);
        return ReadInputFile(manifest, path);
    }
    private StoredEpisodeInput ReadInputFile(EpisodeWorkManifest manifest, string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumMetadataBytes + 2L * (ControllerProvenanceCodec.MaximumTableBytes + MaximumFrames * (TelemetryBinaryCodec.EncodedSize + 8L + ReplaySourceEvidenceCodec.EvidenceSize + 1)))
            throw new InvalidDataException("Episode input file exceeds its bound.");
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8);
        if (reader.ReadInt32() != InputMagic) throw new InvalidDataException("Unsupported episode input version.");
        var metadata = ReadJson<EpisodeInputMetadata>(reader);
        if (metadata.Id != manifest.Id || metadata.TimestampUtc != manifest.TimestampUtc)
            throw new InvalidDataException("Episode input identity differs from its manifest.");
        var samples = ReadFrames(reader); var approach = ReadFrames(reader);
        if (gzip.ReadByte() != -1) throw new InvalidDataException("Trailing episode input data.");
        return new StoredEpisodeInput(metadata, metadata.Restore(samples, approach));
    }

    public ProcessedEpisode CommitAnalysis(EpisodeWorkManifest manifest, ProcessedEpisode result, string? executedEngine = null)
    {
        using var change = Acquire(manifest);
        if (manifest.Stage >= EpisodeWorkStage.Analysed) return ReadAnalysis(manifest);
        RequireStage(manifest, EpisodeWorkStage.InputsCommitted);
        if (executedEngine == null) RequireEngine(manifest);
        else if (executedEngine != manifest.Engine) throw new InvalidDataException("Analysis came from another retained engine.");
        var path = PathFor(manifest.Id, "analysis.json");
        if (!File.Exists(path))
            DurableFile.WriteJson(path, new EpisodeAnalysisData
            {
                Id = manifest.Id, Engine = manifest.Engine, InputHash = manifest.InputHash, GeometryHash = manifest.GeometryHash, ReplayLike = result.ReplayLike,
                Airports = result.AirportFacilities.ToArray(), Records = result.Records.Select(LandingRecordFile.FromRecord).ToArray(),
                Decisions = DecisionPersistenceData.Capture(result.Decisions)
            });
        var committed = ReadAnalysisFile(manifest, path);
        manifest.AnalysisHash = Hash(path); manifest.Stage = EpisodeWorkStage.Analysed; Save(manifest);
        return committed;
    }
    public ProcessedEpisode ReadAnalysis(EpisodeWorkManifest manifest)
    {
        if (manifest.Stage < EpisodeWorkStage.Analysed) throw new InvalidOperationException("Analysis is not committed.");
        var path = PathFor(manifest.Id, "analysis.json"); Verify(path, manifest.AnalysisHash);
        return ReadAnalysisFile(manifest, path);
    }
    private static ProcessedEpisode ReadAnalysisFile(EpisodeWorkManifest manifest, string path, bool report = false)
    {
        var data = DurableFile.ReadJson<EpisodeAnalysisData>(path, MaximumAnalysisBytes);
        if (data.Version != 1 || data.Id != manifest.Id || data.Engine != manifest.Engine || data.InputHash != manifest.InputHash ||
            (data.GeometryHash ?? "") != (manifest.GeometryHash ?? "") ||
            data.Records == null || data.Records.Length > MaximumFrames || data.Airports == null || data.Airports.Length > 65536 || data.Decisions == null ||
            (report && data.FacilityHash != manifest.FacilityHash))
            throw new InvalidDataException("Invalid committed analysis.");
        var decisions = data.Decisions.Restore().Snapshot();
        return data.ReplayLike ? ProcessedEpisode.Replay(decisions) :
            ProcessedEpisode.Completed(data.Airports, data.Records.Select(value => value.ToRecord()).ToArray(), decisions);
    }
    internal static ProcessedEpisode ReadWorkerAnalysis(EpisodeWorkManifest manifest, string path, bool report = false) => ReadAnalysisFile(manifest, path, report);

    public EpisodeGeometryData ReadGeometry(EpisodeWorkManifest manifest)
    {
        var path = PathFor(manifest.Id, "geometry.json"); Verify(path, manifest.GeometryHash);
        var data = DurableFile.ReadJson<EpisodeGeometryData>(path, MaximumMetadataBytes); data.Validate(manifest); return data;
    }
    public void CommitGeometry(EpisodeWorkManifest manifest, EpisodeGeometryData data)
    {
        using var change = Acquire(manifest);
        if (!string.IsNullOrEmpty(manifest.GeometryHash)) { ReadGeometry(manifest); return; }
        RequireStage(manifest, EpisodeWorkStage.InputsCommitted); data.Validate(manifest);
        var path = PathFor(manifest.Id, "geometry.json");
        if (!File.Exists(path)) DurableFile.WriteJson(path, data);
        DurableFile.ReadJson<EpisodeGeometryData>(path, MaximumMetadataBytes).Validate(manifest);
        manifest.GeometryHash = Hash(path); Save(manifest);
    }

    public ProcessedEpisode SaveHistory(EpisodeWorkManifest manifest, LandingRepository repository, object gate)
    {
        using var change = Acquire(manifest);
        var result = manifest.Stage >= EpisodeWorkStage.ReportReady ? ReadReport(manifest) : ReadAnalysis(manifest);
        var visible = new List<LandingRecord>();
        lock (gate)
        using (repository.BeginBatch())
            foreach (var record in result.Records)
                if (repository.SaveRecovered(record)) visible.Add(record);
        if (manifest.Stage == EpisodeWorkStage.Analysed) { manifest.Stage = EpisodeWorkStage.HistorySaved; Save(manifest); }
        return result.ReplayLike ? result : ProcessedEpisode.Completed(result.AirportFacilities, visible, result.Decisions!);
    }

    public void CommitArchive(EpisodeWorkManifest manifest, string? path, string? skipReason, long policyRevision)
    {
        using var change = Acquire(manifest);
        RequireStage(manifest, EpisodeWorkStage.ReportReady);
        if (policyRevision < 0 || ((path == null) == string.IsNullOrWhiteSpace(skipReason)))
            throw new ArgumentException("An archive or a policy skip reason is required.");
        if (path == null && skipReason != "capture_opted_out" && skipReason != "current_policy_opted_out" &&
            skipReason != "no_telemetry" && skipReason != "not_required" && skipReason != "automatic_queue_full")
            throw new ArgumentException("Storage or analysis failure cannot be recorded as a successful skip.");
        if (path != null)
        {
            if (!manifest.AutomaticAllowedAtCapture) throw new InvalidOperationException("Capture policy forbids automatic delivery.");
            manifest.ArchivePath = Path.GetFullPath(path); manifest.ArchiveHash = Hash(path);
        }
        else manifest.ArchiveSkipReason = skipReason!;
        manifest.PolicyRevision = policyRevision; manifest.Stage = EpisodeWorkStage.ArchivePublished; Save(manifest);
    }
    public void MarkOutboxEnqueued(EpisodeWorkManifest manifest)
    { using var change = Acquire(manifest); RequireStage(manifest, EpisodeWorkStage.ArchivePublished); manifest.Stage = EpisodeWorkStage.OutboxEnqueued; Save(manifest); }
    public void Complete(EpisodeWorkManifest manifest)
    {
        using var change = Acquire(manifest); RequireStage(manifest, EpisodeWorkStage.OutboxEnqueued);
        manifest.Stage = EpisodeWorkStage.Completed; manifest.LastFailure = ""; Save(manifest);
        try { ArchiveCompleted(manifest.Id); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void RecordFailure(EpisodeWorkManifest manifest, Exception error)
    {
        using var change = Acquire(manifest);
        manifest.LastFailure = error.GetType().Name + ": " + error.Message;
        if (manifest.LastFailure.Length > 2048) manifest.LastFailure = manifest.LastFailure.Substring(0, 2048);
        if (manifest.FailureCount < int.MaxValue) manifest.FailureCount++;
        Save(manifest);
    }
    public void Fail(Guid id, Exception error, bool terminal)
    {
        lock (Gate(id))
        {
            try
            {
                var manifest = Read(id); RecordFailure(manifest, error);
                if (terminal) { manifest.IsFailed = true; Save(manifest); }
            }
            catch (Exception failure) when (failure is InvalidDataException || failure is SerializationException || failure is ArgumentException)
            {
                // A corrupt manifest cannot be rewritten. Preserve it and place
                // an independent terminal marker next to the original bytes.
                terminal = true;
            }
            if (terminal) DurableFile.WriteJson(PathFor(id, "failure.json"), error.ToString());
        }
    }
    public bool HasFailed(Guid id) => File.Exists(PathFor(id, "failure.json"));
    public void RequireEngine(EpisodeWorkManifest manifest)
    {
        if (!string.Equals(Engine, manifest.Engine, StringComparison.Ordinal))
            throw new InvalidOperationException("This pending episode requires its retained processing engine " + manifest.Engine + ".");
    }
    private static void RequireStage(EpisodeWorkManifest manifest, EpisodeWorkStage expected)
    { if (manifest.Stage != expected) throw new InvalidOperationException("Invalid episode stage transition."); }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path); using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    internal static bool ValidHash(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    internal static void Verify(string path, string hash)
    { if (!string.Equals(Hash(path), hash, StringComparison.Ordinal)) throw new InvalidDataException("Persisted episode checksum mismatch; original bytes retained."); }
    private static void WriteFrames(BinaryWriter writer, IReadOnlyList<TelemetrySample> samples)
    { RuntimeCheckpointCodec.WriteSamples(writer, samples, MaximumFrames); }
    private static TelemetrySample[] ReadFrames(BinaryReader reader)
        => RuntimeCheckpointCodec.ReadSamples(reader, MaximumFrames);
    private static void WriteJson<T>(BinaryWriter writer, T value)
    {
        using var buffer = new MemoryStream(); new DataContractJsonSerializer(typeof(T)).WriteObject(buffer, value);
        if (buffer.Length > MaximumMetadataBytes) throw new InvalidDataException("Episode metadata exceeds its bound.");
        writer.Write((int)buffer.Length); writer.Write(buffer.ToArray());
    }
    private static T ReadJson<T>(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumMetadataBytes) throw new InvalidDataException("Invalid episode metadata length.");
        using var stream = new MemoryStream(ReadBytes(reader, count), false);
        return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(stream) ?? throw new InvalidDataException("Empty episode metadata."));
    }
    private static byte[] ReadBytes(BinaryReader reader, int count)
    { var bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException(); return bytes; }
}
