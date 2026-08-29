using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Globalization;
using System.Runtime.Serialization.Json;
using LandingStats.App.Models;

namespace LandingStats.App.Storage;

public sealed class LandingRepository
{
    private readonly DataContractJsonSerializer _recordSerializer = new DataContractJsonSerializer(typeof(LandingRecord));
    private readonly DataContractJsonSerializer _columnarSerializer = new DataContractJsonSerializer(typeof(LandingRecordFile));
    private readonly DataContractJsonSerializer _indexSerializer = new DataContractJsonSerializer(typeof(List<LandingRecord>));
    private List<LandingRecord>? _summaries;

    public LandingRepository(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Landings");
    }

    public string RootPath { get; }

    public string IndexPath => Path.Combine(RootPath, "landing-index.json.gz");

    public IReadOnlyList<LandingRecord> LoadAll()
    {
        EnsureIndexLoaded();
        return _summaries!
            .OrderByDescending(record => record.TimestampUtc)
            .ThenByDescending(record => record.ContactNumber)
            .ToArray();
    }

    public LandingRecord? LoadDetail(LandingRecord summary)
    {
        if (summary == null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        if (!summary.IsSummaryOnly)
        {
            return summary;
        }

        var path = FindRecordPath(summary.Id);
        if (path == null)
        {
            return null;
        }

        var record = ReadRecord(path);
        if (record == null)
        {
            return null;
        }

        // Airport resolution can happen after the immutable detail file was written.
        record.Airport = summary.Airport;
        record.Runway = summary.Runway;
        record.AirportDistanceNauticalMiles = summary.AirportDistanceNauticalMiles;
        record.IsSummaryOnly = false;
        return record;
    }

    public string Save(LandingRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        Directory.CreateDirectory(RootPath);
        var path = RecordPath(record);
        if (record.FormatVersion >= 7)
        {
            WriteAtomic(path, _columnarSerializer, LandingRecordFile.FromRecord(record));
        }
        else
        {
            WriteAtomic(path, _recordSerializer, record);
        }
        UpdateSummary(record);
        return path;
    }

    public bool Contains(string id) => FindRecordPath(id) != null;

    public byte[] Export(string id)
    {
        var path = FindRecordPath(id);
        if (path == null)
        {
            throw new FileNotFoundException("The landing record was not found.", id);
        }
        return File.ReadAllBytes(path);
    }

    public byte[] ExportForBackup(string id)
    {
        EnsureIndexLoaded();
        var summary = _summaries!.FirstOrDefault(record =>
            string.Equals(record.Id, id, StringComparison.Ordinal));
        if (summary == null)
        {
            throw new FileNotFoundException("The landing record was not found.", id);
        }

        var record = LoadDetail(summary) ??
                     throw new InvalidDataException("The landing record could not be read.");
        return SerializeForBackup(record);
    }

    internal byte[] SerializeForBackup(LandingRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
        {
            if (record.FormatVersion >= 7)
            {
                _columnarSerializer.WriteObject(gzip, LandingRecordFile.FromRecord(record));
            }
            else
            {
                _recordSerializer.WriteObject(gzip, record);
            }
        }
        return output.ToArray();
    }

    internal LandingRecord DeserializeBackup(byte[] compressedRecord)
    {
        if (compressedRecord == null || compressedRecord.Length == 0)
        {
            throw new ArgumentException("A compressed landing record is required.", nameof(compressedRecord));
        }

        Directory.CreateDirectory(RootPath);
        var temporaryPath = Path.Combine(
            RootPath,
            ".google-drive-read-" + Guid.NewGuid().ToString("N") + ".landing.json.gz.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, compressedRecord);
            return ReadRecord(temporaryPath) ??
                   throw new InvalidDataException("The landing backup is invalid or unsupported.");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public string GetBackupFingerprint(LandingRecord summary)
    {
        if (summary == null)
        {
            throw new ArgumentNullException(nameof(summary));
        }
        var path = FindRecordPath(summary.Id);
        if (path == null)
        {
            throw new FileNotFoundException("The landing record was not found.", summary.Id);
        }

        return GetBackupFingerprint(summary, path);
    }

    public IReadOnlyDictionary<string, string> GetBackupFingerprints(
        IReadOnlyCollection<LandingRecord> summaries)
    {
        if (summaries == null)
        {
            throw new ArgumentNullException(nameof(summaries));
        }

        var paths = Directory.Exists(RootPath)
            ? Directory.EnumerateFiles(RootPath, "*.landing.json.gz").ToArray()
            : Array.Empty<string>();
        var pathSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            if (summary == null)
            {
                throw new ArgumentException("A landing summary cannot be null.", nameof(summaries));
            }

            var path = RecordPath(summary);
            if (!pathSet.Contains(path))
            {
                path = paths.FirstOrDefault(candidate => PathMatchesRecord(candidate, summary.Id));
            }
            if (path == null)
            {
                throw new FileNotFoundException("The landing record was not found.", summary.Id);
            }
            result[summary.Id] = GetBackupFingerprint(summary, path);
        }
        return result;
    }

    private static string GetBackupFingerprint(LandingRecord summary, string path)
    {
        var info = new FileInfo(path);
        var airport = summary.Airport ?? string.Empty;
        var runway = summary.Runway ?? string.Empty;
        var distance = summary.AirportDistanceNauticalMiles?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
        return info.Length.ToString(CultureInfo.InvariantCulture) + ":" +
               info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" +
               airport.Length.ToString(CultureInfo.InvariantCulture) + ":" + airport + ":" +
               runway.Length.ToString(CultureInfo.InvariantCulture) + ":" + runway + ":" + distance;
    }

    public LandingRecord Import(
        byte[] compressedRecord,
        string? expectedId = null,
        bool replaceExisting = false)
    {
        if (compressedRecord == null || compressedRecord.Length == 0)
        {
            throw new ArgumentException("A compressed landing record is required.", nameof(compressedRecord));
        }

        Directory.CreateDirectory(RootPath);
        var temporaryPath = Path.Combine(
            RootPath,
            ".google-drive-import-" + Guid.NewGuid().ToString("N") + ".landing.json.gz.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, compressedRecord);
            var record = ReadRecord(temporaryPath) ??
                         throw new InvalidDataException("The downloaded landing record is invalid or unsupported.");
            if (string.IsNullOrWhiteSpace(record.Id) ||
                (!string.IsNullOrWhiteSpace(expectedId) &&
                 !string.Equals(record.Id, expectedId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("The downloaded landing record id does not match its cloud metadata.");
            }

            var existing = FindRecordPath(record.Id);
            if (existing != null)
            {
                var existingBytes = File.ReadAllBytes(existing);
                if (existingBytes.SequenceEqual(compressedRecord))
                {
                    return record;
                }

                if (!replaceExisting)
                {
                    throw new InvalidDataException(
                        "A different local landing already uses id " + record.Id + ".");
                }

                File.Replace(temporaryPath, existing, null);
                UpdateSummary(record);
                return record;
            }

            var destination = RecordPath(record);
            File.Move(temporaryPath, destination);
            UpdateSummary(record);
            return record;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void UpdateSummary(LandingRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        UpdateSummaries(new[] { record });
    }

    public void UpdateSummaries(IEnumerable<LandingRecord> records)
    {
        if (records == null)
        {
            throw new ArgumentNullException(nameof(records));
        }

        EnsureIndexLoaded();
        var changed = false;
        foreach (var record in records)
        {
            if (record == null)
            {
                throw new ArgumentException("A landing summary cannot be null.", nameof(records));
            }

            var summary = CreateSummary(record);
            var index = _summaries!.FindIndex(candidate => string.Equals(candidate.Id, record.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _summaries[index] = summary;
            }
            else
            {
                _summaries.Add(summary);
            }
            changed = true;
        }

        if (changed)
        {
            SaveIndex();
        }
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A landing id is required.", nameof(id));
        }

        EnsureIndexLoaded();
        var paths = Directory.Exists(RootPath)
            ? Directory.EnumerateFiles(RootPath, "*.landing.json.gz")
                .Where(path => PathMatchesRecord(path, id))
                .ToArray()
            : Array.Empty<string>();

        foreach (var path in paths)
        {
            File.Delete(path);
        }

        var summaryRemoved = _summaries!.RemoveAll(summary =>
            string.Equals(summary.Id, id, StringComparison.Ordinal)) > 0;

        if (summaryRemoved || paths.Length > 0)
        {
            SaveIndex();
            return true;
        }

        return false;
    }

    private void EnsureIndexLoaded()
    {
        if (_summaries != null)
        {
            return;
        }

        var indexWasReadable = true;
        _summaries = ReadIndex();
        if (_summaries == null)
        {
            indexWasReadable = false;
            _summaries = new List<LandingRecord>();
        }

        foreach (var summary in _summaries)
        {
            summary.IsSummaryOnly = true;
        }

        var reconciled = ReconcileIndexWithDetailFiles();
        if (reconciled || (!indexWasReadable && _summaries.Count > 0))
        {
            SaveIndex();
        }
    }

    private bool ReconcileIndexWithDetailFiles()
    {
        if (!Directory.Exists(RootPath))
        {
            return false;
        }

        var paths = Directory.EnumerateFiles(RootPath, "*.landing.json.gz").ToArray();
        var changed = _summaries!.RemoveAll(summary =>
            !paths.Any(path => PathMatchesRecord(path, summary.Id))) > 0;

        foreach (var path in paths)
        {
            if (_summaries.Any(summary => PathMatchesRecord(path, summary.Id)))
            {
                continue;
            }

            var record = ReadRecord(path);
            if (record == null || _summaries.Any(summary => string.Equals(summary.Id, record.Id, StringComparison.Ordinal)))
            {
                continue;
            }

            _summaries.Add(CreateSummary(record));
            changed = true;
        }

        return changed;
    }

    private static bool PathMatchesRecord(string path, string id)
    {
        return !string.IsNullOrWhiteSpace(id) &&
               Path.GetFileName(path).EndsWith($"-{id}.landing.json.gz", StringComparison.Ordinal);
    }

    private List<LandingRecord>? ReadIndex()
    {
        if (!File.Exists(IndexPath))
        {
            return null;
        }

        try
        {
            using (var file = File.OpenRead(IndexPath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress, false))
            {
                var values = (_indexSerializer.ReadObject(gzip) as List<LandingRecord>) ?? new List<LandingRecord>();
                foreach (var value in values)
                {
                    LandingRecordFile.RestoreSummary(value);
                }
                return values
                    .Where(record => record.FormatVersion <= LandingRecord.CurrentFormatVersion)
                    .ToList();
            }
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return null;
        }
    }

    private LandingRecord? ReadRecord(string path)
    {
        if (IsColumnarRecord(path))
        {
            return ReadColumnarRecord(path);
        }

        try
        {
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress, false))
            {
                var record = _recordSerializer.ReadObject(gzip) as LandingRecord;
                return record != null && record.FormatVersion <= LandingRecord.CurrentFormatVersion
                    ? record
                    : null;
            }
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return null;
        }
    }

    private static bool IsColumnarRecord(string path)
    {
        try
        {
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress, false))
            using (var reader = new StreamReader(gzip))
            {
                var prefix = new char[128];
                var count = reader.Read(prefix, 0, prefix.Length);
                var text = new string(prefix, 0, count);
                return text.Contains("\"layout\":7") ||
                       text.Contains("\"layout\": 7") ||
                       text.Contains("\"summary\":");
            }
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return false;
        }
    }

    private LandingRecord? ReadColumnarRecord(string path)
    {
        try
        {
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress, false))
            {
                var payload = _columnarSerializer.ReadObject(gzip) as LandingRecordFile;
                var record = payload?.ToRecord();
                return record != null && record.FormatVersion <= LandingRecord.CurrentFormatVersion
                    ? record
                    : null;
            }
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return null;
        }
    }

    private string? FindRecordPath(string id)
    {
        if (!Directory.Exists(RootPath) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Directory.EnumerateFiles(RootPath, $"*-{id}.landing.json.gz").FirstOrDefault();
    }

    private string RecordPath(LandingRecord record)
    {
        var safeTimestamp = record.TimestampUtc.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(RootPath, $"{safeTimestamp}-{record.Id}.landing.json.gz");
    }

    private void SaveIndex()
    {
        Directory.CreateDirectory(RootPath);
        var ordered = _summaries!
            .OrderByDescending(record => record.TimestampUtc)
            .ThenByDescending(record => record.ContactNumber)
            .Select(LandingRecordFile.SanitizedSummaryCopy)
            .ToList();
        WriteAtomic(IndexPath, _indexSerializer, ordered);
    }

    internal static LandingRecord CreateSummary(LandingRecord source)
    {
        var summary = new LandingRecord
        {
            FormatVersion = source.FormatVersion,
            Id = source.Id,
            TimestampUtc = source.TimestampUtc,
            Simulator = source.Simulator,
            AircraftTitle = source.AircraftTitle,
            AircraftType = source.AircraftType,
            Airport = source.Airport,
            Runway = source.Runway,
            ContactNumber = source.ContactNumber,
            ContactCount = source.ContactCount,
            InertialFpm = source.InertialFpm,
            SurfaceFpm = source.SurfaceFpm,
            SurfaceDeltaFpm = source.SurfaceDeltaFpm,
            TerrainFpm = source.TerrainFpm,
            UnresolvedFpm = source.UnresolvedFpm,
            PeakG150Milliseconds = source.PeakG150Milliseconds,
            PeakG2Seconds = source.PeakG2Seconds,
            PitchDegrees = source.PitchDegrees,
            BankDegrees = source.BankDegrees,
            AirspeedKnots = source.AirspeedKnots,
            FlareStartSeconds = source.FlareStartSeconds,
            WeightPounds = source.WeightPounds,
            CgPercent = source.CgPercent,
            ApproachGateSeconds = source.ApproachGateSeconds,
            TouchdownLatitudeDegrees = source.TouchdownLatitudeDegrees,
            TouchdownLongitudeDegrees = source.TouchdownLongitudeDegrees,
            AirportDistanceNauticalMiles = source.AirportDistanceNauticalMiles,
            InertialExtrapolated = source.InertialExtrapolated,
            InertialFitDurationSeconds = source.InertialFitDurationSeconds,
            LatchUpdateDetected = source.LatchUpdateDetected,
            LatchUpdateOffsetSeconds = source.LatchUpdateOffsetSeconds,
            ContactTimeEstimatedFromCompression = source.ContactTimeEstimatedFromCompression,
            GroundSpeedKnots = source.GroundSpeedKnots,
            AngleOfAttackDegrees = source.AngleOfAttackDegrees,
            ControlInputSources = new List<string>(source.ControlInputSources ?? new List<string>()),
            RawPitchInputSourceIndex = source.RawPitchInputSourceIndex,
            RawPitchInputCorrelation = source.RawPitchInputCorrelation,
            RawPitchInputLagSeconds = source.RawPitchInputLagSeconds,
            RawControllerSourceIndices = new List<int>(source.RawControllerSourceIndices ?? new List<int>()),
            WindSpeedKnotsAtContact = source.WindSpeedKnotsAtContact,
            WindDirectionDegreesAtContact = source.WindDirectionDegreesAtContact,
            ClosureReconstructionModel = source.ClosureReconstructionModel,
            ClosureReconstructionAvailable = source.ClosureReconstructionAvailable,
            ReconstructedClosureFpm = source.ReconstructedClosureFpm,
            ReconstructedInertialFpm = source.ReconstructedInertialFpm,
            ReconstructedTerrainFpm = source.ReconstructedTerrainFpm,
            ReconstructedPitchFpm = source.ReconstructedPitchFpm,
            ClosureReconstructionResidualFpm = source.ClosureReconstructionResidualFpm,
            ClosureReconstructionUncertaintyFpm = source.ClosureReconstructionUncertaintyFpm,
            ClosureReconstructionFitPointCount = source.ClosureReconstructionFitPointCount,
            ClosureReconstructionLongitudinalArmFeet = source.ClosureReconstructionLongitudinalArmFeet,
            ClosureReconstructionGeometryQuality = source.ClosureReconstructionGeometryQuality,
            ClosureReconstructionArmRecoveredFromTelemetry = source.ClosureReconstructionArmRecoveredFromTelemetry,
            ClosureReconstructionGeometrySource = source.ClosureReconstructionGeometrySource,
            IsSummaryOnly = true,
        };

        if (source.FormatVersion < 7 && source.Series != null && source.Series.Count > 0)
        {
            var closest = source.Series.OrderBy(point => Math.Abs(point.TimeSeconds)).First();
            summary.WindSpeedKnotsAtContact = closest.WindSpeedKnots;
            summary.WindDirectionDegreesAtContact = closest.WindDirectionDegrees;
        }

        return summary;
    }

    private static void WriteAtomic(string path, DataContractJsonSerializer serializer, object value)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal, false))
            {
                serializer.WriteObject(gzip, value);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsStorageException(Exception exception) =>
        exception is IOException ||
        exception is InvalidDataException ||
        exception is System.Runtime.Serialization.SerializationException;
}
