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
        IReadOnlyList<LandingRecord> records)
    {
        EpisodeId = episodeId;
        Samples = (samples ?? Array.Empty<TelemetrySample>()).ToArray();
        Simulator = simulator ?? string.Empty;
        AircraftTitle = aircraftTitle ?? string.Empty;
        AircraftType = aircraftType ?? string.Empty;
        AircraftModel = aircraftModel ?? string.Empty;
        ControlInputSources = (controlInputSources ?? Array.Empty<string>()).ToArray();
        Records = (records ?? Array.Empty<LandingRecord>()).ToArray();
    }

    public long EpisodeId { get; }
    public IReadOnlyList<TelemetrySample> Samples { get; }
    public string Simulator { get; }
    public string AircraftTitle { get; }
    public string AircraftType { get; }
    public string AircraftModel { get; }
    public IReadOnlyList<string> ControlInputSources { get; }
    public IReadOnlyList<LandingRecord> Records { get; }
}

internal sealed class LastLandingBugReportBuffer
{
    private readonly object _gate = new object();
    private long _activeEpisodeId;
    private long _submittedEpisodeId;
    private BugReportCandidate? _candidate;

    public void BeginEpisode(long episodeId)
    {
        lock (_gate)
        {
            _activeEpisodeId = episodeId;
            _submittedEpisodeId = 0;
            _candidate = null;
        }
    }

    public bool TryRetain(BugReportCandidate candidate)
    {
        if (candidate == null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }

        lock (_gate)
        {
            if (candidate.EpisodeId != _activeEpisodeId || candidate.Samples.Count == 0 || candidate.Records.Count == 0)
            {
                return false;
            }

            _candidate = candidate;
            _submittedEpisodeId = 0;
            return true;
        }
    }

    public BugReportCandidate? Available()
    {
        lock (_gate)
        {
            return _candidate != null && _candidate.EpisodeId != _submittedEpisodeId
                ? _candidate
                : null;
        }
    }

    public bool IsActiveEpisode(long episodeId)
    {
        lock (_gate)
        {
            return _activeEpisodeId == episodeId;
        }
    }

    public void MarkSubmitted(long episodeId)
    {
        lock (_gate)
        {
            if (_candidate?.EpisodeId == episodeId)
            {
                _submittedEpisodeId = episodeId;
            }
        }
    }
}

internal sealed class BugReportRepository
{
    public const int FormatVersion = 1;
    private readonly string _rootPath;
    private readonly DataContractJsonSerializer _serializer =
        new DataContractJsonSerializer(typeof(BugReportPayload));

    public BugReportRepository(string rootPath)
    {
        _rootPath = string.IsNullOrWhiteSpace(rootPath)
            ? throw new ArgumentException("A bug-report queue path is required.", nameof(rootPath))
            : rootPath;
    }

    public string Create(BugReportCandidate candidate, DateTime timestampUtc)
    {
        if (candidate == null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }
        if (candidate.Samples.Count == 0 || candidate.Records.Count == 0)
        {
            throw new InvalidOperationException("A bug report requires telemetry and at least one calculated landing.");
        }

        Directory.CreateDirectory(_rootPath);
        var safeTimestamp = timestampUtc.ToUniversalTime().ToString("yyyyMMdd_HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(_rootPath, $"{safeTimestamp}_{Guid.NewGuid():N}_bug_raw.zip");
        var temporaryPath = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, false))
            {
                WriteTelemetry(archive, candidate.Samples);
                WriteSession(archive, candidate, timestampUtc);
                WriteResults(archive, candidate, timestampUtc);
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

    private static void WriteTelemetry(ZipArchive archive, IReadOnlyList<TelemetrySample> samples)
    {
        var entry = archive.CreateEntry("telemetry.csv", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false), 65536, false);
        writer.WriteLine(TelemetryCsv.Header);
        foreach (var sample in samples)
        {
            writer.WriteLine(TelemetryCsv.Format(sample));
        }
    }

    private static void WriteSession(ZipArchive archive, BugReportCandidate candidate, DateTime timestampUtc)
    {
        var entry = archive.CreateEntry("session.txt", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false), 4096, false);
        writer.WriteLine($"Generated by MSFS Landing Stats {RawCaptureRepository.ApplicationVersion()} by {RawCaptureRepository.ApplicationAuthor()}");
        writer.WriteLine($"capture_utc={timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}");
        writer.WriteLine($"telemetry_schema={TelemetryCsv.SchemaVersion.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine("source_period=SIM_FRAME");
        writer.WriteLine("sample_policy=retained_landing_episode");
        writer.WriteLine($"sample_count={candidate.Samples.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine("capture_kind=bug_report");
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

    private void WriteResults(ZipArchive archive, BugReportCandidate candidate, DateTime timestampUtc)
    {
        var payload = new BugReportPayload
        {
            CreatedUtc = timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            EpisodeId = candidate.EpisodeId,
            Records = candidate.Records.Select(LandingRecordFile.FromRecord).ToList(),
        };
        var entry = archive.CreateEntry("landing-results.json", CompressionLevel.Optimal);
        using var output = entry.Open();
        _serializer.WriteObject(output, payload);
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
