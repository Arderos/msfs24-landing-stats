using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using LandingStats.App.Storage;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class EpisodeFacilityData
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Id { get; set; }
    [DataMember(Order = 3)] public long Ordinal { get; set; }
    [DataMember(Order = 4)] public string InputHash { get; set; } = "";
    [DataMember(Order = 5)] public FacilityPersistenceData Facilities { get; set; } = new FacilityPersistenceData();
    [DataMember(Order = 6)] public DecisionPersistenceData Decisions { get; set; } = new DecisionPersistenceData();
}

internal sealed partial class EpisodeTransactionStore
{
    // Recording may commit final scenery while analysis awaits an external
    // geometry resolver. Refresh under one owner lock before every mutation so
    // that a stale analysis manifest cannot erase that independently saved result.
    private IDisposable Acquire(EpisodeWorkManifest manifest)
    {
        var gate = Gate(manifest.Id);
        Monitor.Enter(gate);
        try { manifest.CopyFrom(Read(manifest.Id)); return new Mutation(gate); }
        catch { Monitor.Exit(gate); throw; }
    }
    private sealed class Mutation : IDisposable
    {
        private readonly object _gate;
        public Mutation(object gate) { _gate = gate; }
        public void Dispose() => Monitor.Exit(_gate);
    }
    public void CommitFacilities(RuntimeFinalFacilities result)
    {
        lock (Gate(result.Episode))
        {
            var manifest = Read(result.Episode);
            if (manifest.Stage < EpisodeWorkStage.InputsCommitted) throw new InvalidOperationException("Facilities require committed episode inputs.");
            if (!string.IsNullOrEmpty(manifest.FacilityHash)) { ReadFacilities(manifest); return; }
            var input = ReadInputs(manifest);
            if (result.Snapshot.EpisodeId != input.Episode.EpisodeId) throw new InvalidDataException("Facilities belong to another episode.");
            if (result.Snapshot.ApproachSamples.Count > MaximumFrames) throw new InvalidDataException("Facility approach exceeds its bound.");
            var path = PathFor(manifest.Id, "facilities.bin.gz");
            if (!File.Exists(path)) DurableFile.Write(path, output =>
            {
                using var gzip = new GZipStream(output, CompressionLevel.Optimal, true);
                using var writer = new BinaryWriter(gzip, Encoding.UTF8, true);
                writer.Write(0x31465045);
                WriteJson(writer, new EpisodeFacilityData { Id = result.Episode, Ordinal = result.Snapshot.EpisodeId, InputHash = manifest.InputHash,
                    Facilities = FacilityPersistenceData.Capture(result.Snapshot), Decisions = DecisionPersistenceData.Capture(result.Decisions) });
                WriteFrames(writer, result.Snapshot.ApproachSamples);
            });
            ReadFacilityFile(manifest, path, input.Episode.EpisodeId);
            manifest.FacilityHash = Hash(path); Save(manifest);
        }
    }
    public RuntimeFinalFacilities ReadFacilities(EpisodeWorkManifest manifest)
    {
        if (!ValidHash(manifest.FacilityHash)) throw new InvalidOperationException("Final facilities are not committed.");
        var path = PathFor(manifest.Id, "facilities.bin.gz"); Verify(path, manifest.FacilityHash);
        return ReadFacilityFile(manifest, path);
    }
    private RuntimeFinalFacilities ReadFacilityFile(EpisodeWorkManifest manifest, string path, long? expectedOrdinal = null)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumMetadataBytes + ControllerProvenanceCodec.MaximumTableBytes + (long)MaximumFrames * (LandingStats.Core.TelemetryBinaryCodec.EncodedSize + 8 + LandingStats.Core.ReplaySourceEvidenceCodec.EvidenceSize + 1))
            throw new InvalidDataException("Facility input exceeds its bound.");
        using var gzip = new GZipStream(file, CompressionMode.Decompress); using var reader = new BinaryReader(gzip, Encoding.UTF8);
        if (reader.ReadInt32() != 0x31465045) throw new InvalidDataException("Unsupported final facility format.");
        var data = ReadJson<EpisodeFacilityData>(reader);
        if (data.Version != 1 || data.Id != manifest.Id || data.Ordinal != (expectedOrdinal ?? ReadInputs(manifest).Episode.EpisodeId) ||
            data.InputHash != manifest.InputHash || data.Facilities == null || data.Decisions == null)
            throw new InvalidDataException("Final facilities differ from their committed episode context.");
        var approach = ReadFrames(reader);
        if (gzip.ReadByte() != -1) throw new InvalidDataException("Trailing facility data.");
        return new RuntimeFinalFacilities(data.Id, data.Facilities.Restore(data.Ordinal, approach), data.Decisions.Restore().Snapshot());
    }
    public ProcessedEpisode CommitReport(EpisodeWorkManifest manifest, ProcessedEpisode result, LandingRepository repository, object repositoryGate)
    {
        using var change = Acquire(manifest);
        if (manifest.Stage >= EpisodeWorkStage.ReportReady) return ReadReport(manifest);
        RequireStage(manifest, EpisodeWorkStage.HistorySaved); ReadFacilities(manifest);
        var path = PathFor(manifest.Id, "report.json");
        if (!File.Exists(path)) DurableFile.WriteJson(path, new EpisodeAnalysisData
        {
            Id = manifest.Id, Engine = manifest.Engine, InputHash = manifest.InputHash, FacilityHash = manifest.FacilityHash, GeometryHash = manifest.GeometryHash,
            ReplayLike = result.ReplayLike, Airports = result.AirportFacilities.ToArray(), Records = result.Records.Select(LandingRecordFile.FromRecord).ToArray(),
            Decisions = DecisionPersistenceData.Capture(result.Decisions)
        });
        var committed = ReadAnalysisFile(manifest, path, report: true);
        LandingSiteMetadataPersistence.Save(repository, repositoryGate, committed.Records);
        manifest.ReportHash = Hash(path); manifest.Stage = EpisodeWorkStage.ReportReady; Save(manifest);
        return committed;
    }
    public ProcessedEpisode ReadReport(EpisodeWorkManifest manifest)
    {
        if (manifest.Stage < EpisodeWorkStage.ReportReady) throw new InvalidOperationException("Final report is not committed.");
        var path = PathFor(manifest.Id, "report.json"); Verify(path, manifest.ReportHash);
        return ReadAnalysisFile(manifest, path, report: true);
    }
}
