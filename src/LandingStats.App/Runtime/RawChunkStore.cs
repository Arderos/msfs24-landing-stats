using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
using LandingStats.Archives;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class RawCaptureMetadata
{
    [DataMember(Order = 1)] public long TimestampUtcTicks { get; set; }
    [DataMember(Order = 2)] public string Simulator { get; set; } = "";
    [DataMember(Order = 3)] public string Title { get; set; } = "";
    [DataMember(Order = 4)] public string Type { get; set; } = "";
    [DataMember(Order = 5)] public string Model { get; set; } = "";
    [DataMember(Order = 6)] public string[] Sources { get; set; } = Array.Empty<string>();
    public DateTime TimestampUtc => new DateTime(TimestampUtcTicks, DateTimeKind.Utc);
    public static RawCaptureMetadata From(RawDebugCaptureStartedEventArgs start) => new RawCaptureMetadata
    {
        TimestampUtcTicks = start.StartedUtc.ToUniversalTime().Ticks, Simulator = start.Simulator, Title = start.AircraftTitle,
        Type = start.AircraftType, Model = start.AircraftModel, Sources = start.ControlInputSources.ToArray()
    };
    public void Validate()
    {
        _ = TimestampUtc;
        if (Sources == null || Sources.Length > 64) throw new InvalidDataException("Invalid RAW metadata sources.");
        foreach (var value in new[] { Simulator, Title, Type, Model }.Concat(Sources))
            if (value == null || Encoding.UTF8.GetByteCount(value) > 4096) throw new InvalidDataException("RAW metadata text exceeds its bound.");
    }
}

[DataContract]
internal sealed class RawChunkManifest
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Id { get; set; }
    [DataMember(Order = 3)] public Guid Capture { get; set; }
    [DataMember(Order = 4)] public int Ordinal { get; set; }
    [DataMember(Order = 5)] public string InputHash { get; set; } = "";
    [DataMember(Order = 6)] public int Stage { get; set; }
    [DataMember(Order = 7)] public string ArchiveHash { get; set; } = "";
    [DataMember(Order = 8)] public int SampleCount { get; set; }
    [DataMember(Order = 9)] public bool Zstd { get; set; }
    [DataMember(Order = 10)] public bool Automatic { get; set; }
}

/// <summary>Replayable RAW publication. A stable chunk identity follows the
/// original capture; a helper never has deletion authority over these archives.</summary>
internal sealed class RawChunkStore
{
    private readonly string _root, _archives;
    private readonly DurableOutbox _outbox;
    private readonly Func<bool>? _useZstd;
    private readonly bool _automatic;
    public RawChunkStore(string root, string archives, DurableOutbox outbox, Func<bool>? useZstd = null, bool automatic = false)
    { _root = Path.GetFullPath(root); _archives = Path.GetFullPath(archives); _outbox = outbox; _useZstd = useZstd; _automatic = automatic; }
    public string Publish(Guid capture, int ordinal, RawCaptureMetadata metadata, IReadOnlyList<TelemetrySample> samples)
    {
        metadata.Validate();
        if (capture == Guid.Empty || ordinal < 1 || samples.Count == 0 || samples.Count > RawCaptureRepository.ChunkMaximumSamples)
            throw new ArgumentException("Invalid RAW chunk.");
        var id = StableRuntimeIdentity.Command(capture, ordinal); var directory = Path.Combine(_root, id.ToString("N"));
        var marker = Path.Combine(directory, "manifest.json"); var temporary = Path.Combine(directory, "archive.zip");
        var path = Path.Combine(_archives, metadata.TimestampUtc.ToString("yyyyMMdd_HHmmss'Z'", CultureInfo.InvariantCulture) + "_" + id.ToString("N") + "_raw.zip");
        var inputHash = InputHash(metadata, samples);
        var manifest = File.Exists(marker) ? DurableFile.ReadJson<RawChunkManifest>(marker, 8192) : new RawChunkManifest
        { Version = 2, Automatic = _automatic, Id = id, Capture = capture, Ordinal = ordinal, InputHash = inputHash, SampleCount = samples.Count, Zstd = _useZstd?.Invoke() == true };
        if ((manifest.Version != 1 && manifest.Version != 2) || manifest.Id != id || manifest.Capture != capture || manifest.Ordinal != ordinal || manifest.InputHash != inputHash ||
            manifest.SampleCount != samples.Count || manifest.Stage < 0 || manifest.Stage > 3 || manifest.Stage > 0 && !EpisodeTransactionStore.ValidHash(manifest.ArchiveHash))
            throw new InvalidDataException("RAW replay differs from its original immutable chunk.");
        if (!File.Exists(marker)) DurableFile.WriteJson(marker, manifest);
        if (manifest.Stage == 0)
        {
            // The private ZIP is replaceable until its hash is committed. It is
            // never visible to uploaders before that durable marker exists.
            DurableFile.Write(temporary, stream => WriteArchive(stream, capture, ordinal, metadata, samples, manifest.Zstd));
            manifest.ArchiveHash = EpisodeTransactionStore.Hash(temporary); manifest.Stage = 1; DurableFile.WriteJson(marker, manifest);
        }
        if (manifest.Stage == 1)
        {
            EpisodeTransactionStore.CopyImmutable(temporary, path, manifest.ArchiveHash);
            manifest.Stage = 2; DurableFile.WriteJson(marker, manifest);
        }
        if (manifest.Stage == 2)
        {
            _outbox.Register(id, path, manifest.ArchiveHash, manifest.Version >= 2 && manifest.Automatic, metadata.TimestampUtc);
            manifest.Stage = 3; DurableFile.WriteJson(marker, manifest);
        }
        // A delivered outbox entry can legitimately have removed the published
        // archive already. Stage 3 is never rebuilt from the recording replay.
        if (File.Exists(temporary)) File.Delete(temporary);
        return path;
    }
    private static string InputHash(RawCaptureMetadata metadata, IReadOnlyList<TelemetrySample> samples)
    {
        using var sha = SHA256.Create();
        using (var sink = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        {
            using var writer = new BinaryWriter(sink, Encoding.UTF8, true);
            RuntimeCheckpointCodec.WriteJson(writer, metadata); RuntimeCheckpointCodec.WriteSamples(writer, samples, RawCaptureRepository.ChunkMaximumSamples);
            writer.Flush(); sink.FlushFinalBlock();
        }
        return BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
    }
    private static void WriteArchive(Stream output, Guid capture, int ordinal, RawCaptureMetadata metadata, IReadOnlyList<TelemetrySample> samples, bool zstd)
    {
        var entryTime = new DateTimeOffset(metadata.TimestampUtc.Year < 1980 ? new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc) : metadata.TimestampUtc);
        using var zip = new CaptureArchiveWriter(output, zstd, true, entryTime);
        using (var writer = new StreamWriter(zip.CreateEntry("telemetry.csv"), new UTF8Encoding(false), 65536))
        { writer.WriteLine(TelemetryCsv.Header); foreach (var sample in samples) writer.WriteLine(TelemetryCsv.Format(sample)); }
        ControllerCatalogArchive.Write(zip, samples);
        ReplaySourceArchive.Write(zip, samples);
        using var info = new StreamWriter(zip.CreateEntry("session.txt"), new UTF8Encoding(false), 4096);
        info.WriteLine($"Generated by MSFS Landing Stats {RawCaptureRepository.ApplicationVersion()} by {RawCaptureRepository.ApplicationAuthor()}");
        info.WriteLine("capture_utc=" + metadata.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        info.WriteLine("capture_id=" + capture.ToString("N")); info.WriteLine("chunk_index=" + ordinal.ToString(CultureInfo.InvariantCulture));
        info.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        info.WriteLine("source_period=SIM_FRAME"); info.WriteLine("sample_policy=all_received_frames_no_downsampling");
        info.WriteLine("sample_count=" + samples.Count.ToString(CultureInfo.InvariantCulture));
        info.WriteLine("simulator=" + RawCaptureRepository.SingleLine(metadata.Simulator)); info.WriteLine("aircraft_title=" + RawCaptureRepository.SingleLine(metadata.Title));
        info.WriteLine("aircraft_type=" + RawCaptureRepository.SingleLine(metadata.Type)); info.WriteLine("aircraft_model=" + RawCaptureRepository.SingleLine(metadata.Model));
        info.WriteLine("control_input_source_count=" + metadata.Sources.Length.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < metadata.Sources.Length; i++) info.WriteLine("control_input_source_" + i.ToString(CultureInfo.InvariantCulture) + "=" + RawCaptureRepository.SingleLine(metadata.Sources[i]));
    }
}
