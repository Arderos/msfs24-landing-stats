using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using LandingStats.App.Models;
using LandingStats.Core;
using LandingStats.App.Runtime;
using LandingStats.Archives;

namespace LandingStats.App.Storage;

internal sealed class BugReportCandidate
{
    public BugReportCandidate(
        long episodeId,
        IReadOnlyList<TelemetrySample> samples,
        string simulator,
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<string> controlInputSources,
        IReadOnlyList<LandingRecord> records,
        TelemetryDecisionSnapshot? decisions = null)
    {
        EpisodeId = episodeId;
        Samples = (samples ?? Array.Empty<TelemetrySample>()).ToArray();
        Simulator = simulator ?? string.Empty;
        AircraftTitle = aircraftTitle ?? string.Empty;
        AircraftType = aircraftType ?? string.Empty;
        AircraftModel = aircraftModel ?? string.Empty;
        ControlInputSources = (controlInputSources ?? Array.Empty<string>()).ToArray();
        Records = (records ?? Array.Empty<LandingRecord>()).ToArray();
        Decisions = decisions;
    }

    public long EpisodeId { get; }
    public IReadOnlyList<TelemetrySample> Samples { get; }
    public string Simulator { get; }
    public string AircraftTitle { get; }
    public string AircraftType { get; }
    public string AircraftModel { get; }
    public IReadOnlyList<string> ControlInputSources { get; }
    public IReadOnlyList<LandingRecord> Records { get; }
    public TelemetryDecisionSnapshot? Decisions { get; }
}

internal sealed class BugReportRepository
{
    public const int FormatVersion = 1;
    private readonly string _rootPath;
    private readonly Func<bool>? _useZstd;

    public BugReportRepository(string rootPath, Func<bool>? useZstd = null)
    {
        _useZstd = useZstd;
        _rootPath = string.IsNullOrWhiteSpace(rootPath)
            ? throw new ArgumentException("A bug-report queue path is required.", nameof(rootPath))
            : rootPath;
        TemporaryEvidenceCleanup.Archives(_rootPath);
    }

    public string RootPath => _rootPath;

    internal string? TryCreateAutomatic(BugReportCandidate candidate, DateTime timestampUtc, long maximumArchiveBytes, Guid archiveId = default) =>
        CreateCore(candidate, timestampUtc, true, maximumArchiveBytes, archiveId);
    internal string CreateManual(BugReportCandidate candidate, DateTime timestampUtc, Guid archiveId) =>
        CreateCore(candidate, timestampUtc, false, long.MaxValue, archiveId)!;

    internal string AutomaticPath(DateTime timestampUtc, Guid archiveId) =>
        Path.Combine(_rootPath, timestampUtc.ToUniversalTime().ToString("yyyyMMdd_HHmmss'Z'", CultureInfo.InvariantCulture) + "_" + archiveId.ToString("N") + "_auto_raw.zip");

    internal string? ExistingAutomatic(DateTime timestampUtc, Guid archiveId)
        => Existing(timestampUtc, archiveId, true);
    private string? Existing(DateTime timestampUtc, Guid archiveId, bool automatic)
    {
        var path = automatic ? AutomaticPath(timestampUtc, archiveId) : Path.Combine(_rootPath,
            timestampUtc.ToUniversalTime().ToString("yyyyMMdd_HHmmss'Z'", CultureInfo.InvariantCulture) + "_" + archiveId.ToString("N") + "_bug_raw.zip");
        if (!File.Exists(path)) return null;
        var receipt = Path.Combine(_rootPath, "." + archiveId.ToString("N") + ".sha256");
        if (!File.Exists(receipt) || new FileInfo(receipt).Length != 64 ||
            !string.Equals(File.ReadAllText(receipt, Encoding.ASCII), EpisodeTransactionStore.Hash(path), StringComparison.Ordinal))
            throw new InvalidDataException("The published automatic archive has changed; original files retained.");
        return path;
    }

    private string? CreateCore(BugReportCandidate candidate, DateTime timestampUtc, bool automatic, long maximumArchiveBytes, Guid archiveId = default)
    {
        if (candidate == null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }
        if (candidate.Samples.Count == 0 || (!automatic && candidate.Records.Count == 0))
        {
            throw new InvalidOperationException("A bug report requires telemetry and at least one calculated landing.");
        }

        Directory.CreateDirectory(_rootPath);
        var safeTimestamp = timestampUtc.ToUniversalTime().ToString("yyyyMMdd_HHmmss'Z'", CultureInfo.InvariantCulture);
        var kind = automatic ? "auto" : "bug";
        var path = Path.Combine(_rootPath, $"{safeTimestamp}_{(archiveId == Guid.Empty ? Guid.NewGuid() : archiveId):N}_{kind}_raw.zip");
        if (archiveId != Guid.Empty && File.Exists(path)) return Existing(timestampUtc, archiveId, automatic);
        var temporaryPath = path + ".tmp";
        if (archiveId != Guid.Empty && File.Exists(temporaryPath))
            File.Move(temporaryPath, Path.Combine(_rootPath, ".interrupted-" + archiveId.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".part"));
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
            {
                using (var bounded = RuntimeStorageQuota.Protect(file, path, true))
                using (var archive = new CaptureArchiveWriter(bounded, _useZstd?.Invoke() == true, true))
                {
                    WriteTelemetry(archive, candidate.Samples);
                    ControllerCatalogArchive.Write(archive, candidate.Samples);
                    ReplaySourceArchive.Write(archive, candidate.Samples);
                    WriteSession(archive, candidate, timestampUtc, automatic);
                    WriteResults(archive, candidate, timestampUtc);
                    if (candidate.Decisions != null)
                        TelemetryDecisionArchive.Write(archive, candidate.EpisodeId, candidate.Decisions);
                }
                file.Flush(true);
            }

            if (new FileInfo(temporaryPath).Length > maximumArchiveBytes)
            {
                // This diagnostic copy has not been published to the durable
                // queue. Never remove existing reports to make space for it.
                File.Delete(temporaryPath);
                return null;
            }
            if (archiveId != Guid.Empty)
            {
                var checksum = Encoding.ASCII.GetBytes(EpisodeTransactionStore.Hash(temporaryPath));
                DurableFile.Write(Path.Combine(_rootPath, "." + archiveId.ToString("N") + ".sha256"), output => output.Write(checksum, 0, checksum.Length));
            }
            File.Move(temporaryPath, path);
            return path;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            throw;
        }
    }

    private static void WriteTelemetry(CaptureArchiveWriter archive, IReadOnlyList<TelemetrySample> samples)
    {
        using var writer = new StreamWriter(archive.CreateEntry("telemetry.csv"), new UTF8Encoding(false), 65536, false);
        writer.WriteLine(TelemetryCsv.Header);
        foreach (var sample in samples)
        {
            writer.WriteLine(TelemetryCsv.Format(sample));
        }
    }

    private static void WriteSession(CaptureArchiveWriter archive, BugReportCandidate candidate, DateTime timestampUtc, bool automatic)
    {
        using var writer = new StreamWriter(archive.CreateEntry("session.txt"), new UTF8Encoding(false), 4096, false);
        writer.WriteLine($"Generated by MSFS Landing Stats {RawCaptureRepository.ApplicationVersion()} by {RawCaptureRepository.ApplicationAuthor()}");
        writer.WriteLine($"capture_utc={timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}");
        writer.WriteLine($"telemetry_schema={TelemetryCsv.SchemaVersion.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine("source_period=SIM_FRAME");
        writer.WriteLine("sample_policy=retained_landing_episode");
        writer.WriteLine($"sample_count={candidate.Samples.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine(automatic ? "capture_kind=automatic_landing" : "capture_kind=bug_report");
        writer.WriteLine($"bug_report_format={FormatVersion.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"episode_id={candidate.EpisodeId.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"landing_count={candidate.Records.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"simulator={RawCaptureRepository.SingleLine(candidate.Simulator)}");
        writer.WriteLine($"aircraft_title={RawCaptureRepository.SingleLine(candidate.AircraftTitle)}");
        writer.WriteLine($"aircraft_type={RawCaptureRepository.SingleLine(candidate.AircraftType)}");
        writer.WriteLine($"aircraft_model={RawCaptureRepository.SingleLine(candidate.AircraftModel)}");
        writer.WriteLine($"control_input_source_count={candidate.ControlInputSources.Count.ToString(CultureInfo.InvariantCulture)}");
        for (var index = 0; index < candidate.ControlInputSources.Count; index++)
        {
            writer.WriteLine($"control_input_source_{index.ToString(CultureInfo.InvariantCulture)}={RawCaptureRepository.SingleLine(candidate.ControlInputSources[index])}");
        }
    }

    private void WriteResults(CaptureArchiveWriter archive, BugReportCandidate candidate, DateTime timestampUtc)
    {
        var payload = new BugReportPayload
        {
            CreatedUtc = timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            EpisodeId = candidate.EpisodeId,
            Records = candidate.Records.Select(LandingRecordFile.FromRecord).ToList(),
        };
        using var output = archive.CreateEntry("landing-results.json");
        new DataContractJsonSerializer(typeof(BugReportPayload)).WriteObject(output, payload);
    }

    [DataContract(Name = "bug-report")]
    private sealed class BugReportPayload
    {
        [DataMember(Name = "format_version", Order = 1)]
        public int Version { get; set; } = FormatVersion;

        [DataMember(Name = "created_utc", Order = 2)]
        public string CreatedUtc { get; set; } = string.Empty;

        [DataMember(Name = "episode_id", Order = 3)]
        public long EpisodeId { get; set; }

        [DataMember(Name = "records", Order = 4)]
        public List<LandingRecordFile> Records { get; set; } = new List<LandingRecordFile>();
    }
}
