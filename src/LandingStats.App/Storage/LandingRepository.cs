using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Runtime;

namespace LandingStats.App.Storage;

public sealed class LandingRepository
{
    private readonly DataContractJsonSerializer _recordSerializer = new DataContractJsonSerializer(typeof(LandingRecord));
    private readonly DataContractJsonSerializer _columnarSerializer = new DataContractJsonSerializer(typeof(LandingRecordFile));
    private readonly DataContractJsonSerializer _indexSerializer = new DataContractJsonSerializer(typeof(List<LandingRecord>));
    private List<LandingRecord>? _summaries;
    private readonly LandingDeletionLedger _deletions;
    private Dictionary<string, string>? _paths;
    private int _batchDepth;
    private bool _indexDirty;
    private bool _temporaryCleanupComplete;
    private string _indexGeneration = LandingIndexGeneration.Missing;
    public IDisposable BeginBatch() { EnsureIndexLoaded(); _batchDepth++; return new Batch(this); }
    private sealed class Batch : IDisposable
    {
        private LandingRepository? _owner;
        public Batch(LandingRepository owner) { _owner = owner; }
        public void Dispose()
        {
            var owner = _owner; if (owner == null) return; _owner = null;
            if (--owner._batchDepth == 0 && owner._indexDirty) owner.SaveIndex();
        }
    }

    public LandingRepository(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Landings");
        _deletions = new LandingDeletionLedger(RootPath);
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

        if (_deletions.Contains(record.Id)) throw new InvalidOperationException("This landing was deleted by the user.");
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
        Paths()[record.Id] = path;
        UpdateSummary(record);
        return path;
    }

    public bool Contains(string id) => FindRecordPath(id) != null;

    internal bool IsDeleted(string id) => _deletions.Contains(id);

    // Replaying a completed analysis never replaces an already committed result.
    // Rebuild its index entry if a crash separated detail publication from index publication.
    internal bool SaveRecovered(LandingRecord record)
    {
        if (_deletions.Contains(record.Id)) return false;
        var existing = FindRecordPath(record.Id);
        if (existing == null) Save(record);
        else UpdateSummary(ReadRecord(existing) ?? throw new InvalidDataException("The existing landing detail is unreadable; retained for recovery."));
        return true;
    }

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
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(compressedRecord, 0, compressedRecord.Length); output.Flush(true); }
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

        var paths = Paths();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            if (summary == null)
            {
                throw new ArgumentException("A landing summary cannot be null.", nameof(summaries));
            }

            paths.TryGetValue(summary.Id, out var path);
            if (path != null && !File.Exists(path)) { paths.Remove(summary.Id); path = null; }
            result[summary.Id] = path == null ? "missing" : GetBackupFingerprint(summary, path);
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

        EnsureIndexLoaded();
        // Retry a failed index commit before deciding that identical detail bytes
        // make this import a no-op. This never rebuilds intentional index-only edits.
        if (_indexDirty && _batchDepth == 0) SaveIndex();
        Directory.CreateDirectory(RootPath);
        var temporaryPath = Path.Combine(
            RootPath,
            ".google-drive-import-" + Guid.NewGuid().ToString("N") + ".landing.json.gz.tmp");
        string? publicationPath = null;
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(compressedRecord, 0, compressedRecord.Length); output.Flush(true); }
            var record = ReadRecord(temporaryPath) ??
                         throw new InvalidDataException("The downloaded landing record is invalid or unsupported.");
            if (string.IsNullOrWhiteSpace(record.Id) ||
                (!string.IsNullOrWhiteSpace(expectedId) &&
                 !string.Equals(record.Id, expectedId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("The downloaded landing record id does not match its cloud metadata.");
            }
            if (_deletions.Contains(record.Id)) throw new InvalidOperationException("This landing was deleted by the user.");

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

                // A valid remote copy may repair unreadable local bytes; retain
                // those bytes separately for diagnosis before publishing it.
                if (ReadRecord(existing) == null)
                    File.Copy(existing, existing + ".corrupt-" + Guid.NewGuid().ToString("N"));
                BeginImportCommit(record, existing, compressedRecord);
                publicationPath = existing; DurableFile.PublishTemporary(temporaryPath, existing);
                UpdateSummary(record);
                return record;
            }

            var destination = RecordPath(record);
            BeginImportCommit(record, destination, compressedRecord);
            publicationPath = destination; DurableFile.PublishTemporary(temporaryPath, destination);
            Paths()[record.Id] = destination;
            UpdateSummary(record);
            return record;
        }
        finally
        {
            if (File.Exists(temporaryPath) && (publicationPath == null || File.Exists(publicationPath)))
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
            if (_deletions.Contains(record.Id)) continue;
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
        // Commit intent before changing detail/index. A failure after this point
        // cannot resurrect the record through index repair or delayed recovery.
        var newIntent = !_deletions.Contains(id);
        _deletions.Add(id);
        var paths = Directory.Exists(RootPath)
            ? Directory.EnumerateFiles(RootPath, "*.landing.json.gz")
                .Where(path => PathMatchesRecord(path, id))
                .ToArray()
            : Array.Empty<string>();

        // The tombstone is the commit point. Cleanup failures must never
        // report a rollback to callers or cancel their cloud deletion intent.
        foreach (var path in paths)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Paths().Remove(id);
        var summaryRemoved = _summaries!.RemoveAll(summary => string.Equals(summary.Id, id, StringComparison.Ordinal)) > 0;
        if (summaryRemoved || paths.Length > 0)
        {
            try { SaveIndex(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return newIntent;
    }

    private void EnsureIndexLoaded()
    {
        if (_summaries != null)
        {
            return;
        }

        CleanupRedundantCloudTemporaryFiles();

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

        var recovered = RecoverPendingImports();
        var reconciled = ReconcileIndexWithDetailFiles();
        if (recovered || reconciled || (!indexWasReadable && _summaries.Count > 0))
        {
            SaveIndex();
        }
    }

    private void CleanupRedundantCloudTemporaryFiles()
    {
        if (_temporaryCleanupComplete) return;
        _temporaryCleanupComplete = true;
        if (!Directory.Exists(RootPath)) return;
        try
        {
            var files = Directory.EnumerateFiles(RootPath, ".google-drive-*.landing.json.gz.tmp").Take(65536);
            foreach (var temporary in files)
            {
                var name = Path.GetFileName(temporary);
                var prefix = name.StartsWith(".google-drive-read-", StringComparison.Ordinal) ? ".google-drive-read-" :
                    name.StartsWith(".google-drive-import-", StringComparison.Ordinal) ? ".google-drive-import-" : "";
                const string suffix = ".landing.json.gz.tmp";
                if (prefix.Length == 0 || !Guid.TryParseExact(name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length), "N", out _)) continue;
                var record = ReadRecord(temporary);
                if (record == null) continue; // Preserve incomplete/unknown and sole surviving copies.
                var published = FindRecordPath(record.Id);
                if (published != null) TemporaryEvidenceCleanup.DeleteDuplicate(temporary, published);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool ReconcileIndexWithDetailFiles()
    {
        if (!Directory.Exists(RootPath))
        {
            return false;
        }

        var paths = Paths();
        var changed = _summaries!.RemoveAll(summary => _deletions.Contains(summary.Id) || !paths.ContainsKey(summary.Id)) > 0;
        var known = new HashSet<string>(_summaries.Select(summary => summary.Id), StringComparer.Ordinal);
        foreach (var pair in paths.ToArray())
        {
            if (_deletions.Contains(pair.Key))
            {
                try { File.Delete(pair.Value); paths.Remove(pair.Key); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                continue;
            }
            if (known.Contains(pair.Key)) continue;
            var record = ReadRecord(pair.Value);
            if (record == null || !known.Add(record.Id)) continue;
            _summaries.Add(CreateSummary(record)); changed = true;
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
            {
                _indexGeneration = LandingIndexGeneration.Read(file);
                using var gzip = new GZipStream(file, CompressionMode.Decompress, false);
                var values = (_indexSerializer.ReadObject(new BoundedHistoryStream(gzip, BoundedHistoryStream.MaximumIndexBytes)) as List<LandingRecord>) ?? new List<LandingRecord>();
                var valid = new List<LandingRecord>();
                foreach (var value in values)
                {
                    try { LandingRecordFile.RestoreSummary(value); valid.Add(value); }
                    catch (Exception error) when (IsStorageException(error) || error is ArgumentException) { }
                }
                return valid
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
                var record = _recordSerializer.ReadObject(new BoundedHistoryStream(gzip, BoundedHistoryStream.MaximumDetailBytes)) as LandingRecord;
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
                var payload = _columnarSerializer.ReadObject(new BoundedHistoryStream(gzip, BoundedHistoryStream.MaximumDetailBytes)) as LandingRecordFile;
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

    private Dictionary<string, string> Paths()
    {
        if (_paths != null) return _paths;
        _paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(RootPath))
            foreach (var path in Directory.EnumerateFiles(RootPath, "*.landing.json.gz"))
            {
                var name = Path.GetFileName(path);
                const int prefix = 17; const string suffix = ".landing.json.gz";
                if (name.Length <= prefix + suffix.Length || name[8] != '-' || name[15] != 'Z' || name[16] != '-') continue;
                var id = name.Substring(prefix, name.Length - prefix - suffix.Length);
                if (!_paths.ContainsKey(id)) _paths.Add(id, path);
            }
        return _paths;
    }
    private string? FindRecordPath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || _deletions.Contains(id)) return null;
        if (!Paths().TryGetValue(id, out var path)) return null;
        if (File.Exists(path)) return path;
        Paths().Remove(id); return null;
    }

    private string RecordPath(LandingRecord record)
    {
        var safeTimestamp = record.TimestampUtc.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(RootPath, $"{safeTimestamp}-{record.Id}.landing.json.gz");
    }

    private void SaveIndex()
    {
        _indexDirty = true;
        if (_batchDepth != 0) return;
        Directory.CreateDirectory(RootPath);
        var ordered = _summaries!
            .OrderByDescending(record => record.TimestampUtc)
            .ThenByDescending(record => record.ContactNumber)
            .Select(LandingRecordFile.SanitizedSummaryCopy)
            .ToList();
        var generation = Guid.NewGuid();
        DurableFile.Write(IndexPath, file =>
        {
            using var generated = LandingIndexGeneration.Wrap(file, generation);
            using var gzip = new GZipStream(generated, CompressionLevel.Optimal, true);
            _indexSerializer.WriteObject(gzip, ordered);
        });
        _indexGeneration = LandingIndexGeneration.Name(generation);
        _indexDirty = false;
        CleanupImportCommits();
    }

    // The intent precedes detail publication. The generation changes with every
    // durable index publication, even if later metadata returns to identical values.
    [DataContract]
    private sealed class ImportCommit
    {
        [DataMember(Order = 1)] public string Id { get; set; } = "";
        [DataMember(Order = 2)] public string DetailName { get; set; } = "";
        [DataMember(Order = 3)] public string DetailHash { get; set; } = "";
        [DataMember(Order = 4, IsRequired = true)] public string PreviousIndexGeneration { get; set; } = "";
    }
    private string ImportRoot => Path.Combine(RootPath, ".pending-imports");
    private static string HashBytes(byte[] bytes)
    { using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(bytes)); }
    private static string HashFile(string path)
    { using var file = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(file)); }
    private void BeginImportCommit(LandingRecord record, string destination, byte[] bytes)
    {
        using var sha = SHA256.Create();
        var name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(record.Id))).Replace("-", "");
        Directory.CreateDirectory(ImportRoot);
        var path = Path.Combine(ImportRoot, name.Substring(0, 32) + ".json");
        var previous = File.Exists(path) ? DurableFile.ReadJson<ImportCommit>(path, 256 * 1024) : null;
        if (previous != null && previous.Id != record.Id) throw new InvalidDataException("Pending import identity hash collision.");
        var commit = new ImportCommit { Id = record.Id, DetailName = Path.GetFileName(destination), DetailHash = HashBytes(bytes),
            PreviousIndexGeneration = _indexGeneration };
        DurableFile.WriteJson(path, commit);
    }
    private bool RecoverPendingImports()
    {
        if (!Directory.Exists(ImportRoot)) return false;
        var changed = false;
        foreach (var path in Directory.EnumerateFiles(ImportRoot, "*.json"))
        {
            var commit = DurableFile.ReadJson<ImportCommit>(path, 256 * 1024);
            if (string.IsNullOrEmpty(commit.Id) || string.IsNullOrEmpty(commit.DetailName) || Path.GetFileName(commit.DetailName) != commit.DetailName ||
                !PathMatchesRecord(commit.DetailName, commit.Id)) throw new InvalidDataException("Invalid pending landing import.");
            if (_deletions.Contains(commit.Id) || commit.PreviousIndexGeneration != _indexGeneration) continue;
            var detail = Path.Combine(RootPath, commit.DetailName);
            if (!File.Exists(detail) || HashFile(detail) != commit.DetailHash) continue; // Detail never published.
            var existing = _summaries!.FindIndex(item => item.Id == commit.Id);
            var record = ReadRecord(detail) ?? throw new InvalidDataException("Pending import detail is unreadable; retained for recovery.");
            if (record.Id != commit.Id) throw new InvalidDataException("Pending import identity changed.");
            if (existing < 0) _summaries.Add(CreateSummary(record)); else _summaries[existing] = CreateSummary(record);
            changed = true;
        }
        return changed;
    }
    private void CleanupImportCommits()
    {
        if (!Directory.Exists(ImportRoot)) return;
        foreach (var path in Directory.EnumerateFiles(ImportRoot, "*.json"))
        {
            try { File.Delete(path); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
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
            EstimatedContactTimeSeconds = source.EstimatedContactTimeSeconds,
            RecordingEpisodeId = source.RecordingEpisodeId,
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
            RawControllerAxesSourceIndices = new List<int>(source.RawControllerAxesSourceIndices ?? new List<int>()),
            ControllerAxisCount = source.ControllerAxisCount,
            RunwayGeometry = source.RunwayGeometry,
            WindSpeedKnotsAtContact = source.WindSpeedKnotsAtContact,
            WindDirectionDegreesAtContact = source.WindDirectionDegreesAtContact,
            ClosureReconstructionModel = source.ClosureReconstructionModel,
            ClosureReconstructionAvailable = source.ClosureReconstructionAvailable,
            ReconstructedClosureFpm = source.ReconstructedClosureFpm,
            ReconstructedInertialFpm = source.ReconstructedInertialFpm,
            ReconstructedTerrainFpm = source.ReconstructedTerrainFpm,
            ReconstructedPitchFpm = source.ReconstructedPitchFpm,
            ReconstructedRotationFpm = source.ReconstructedRotationFpm,
            ClosureReconstructionContactPointIndices = source.ClosureReconstructionContactPointIndices?.ToArray(),
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

        summary.ImpactAnalysisVersion = source.ImpactAnalysisVersion;
        summary.ImpactPeakG = source.ImpactPeakG;
        summary.ImpactPeakOffsetSeconds = source.ImpactPeakOffsetSeconds;
        summary.ImpactEndOffsetSeconds = source.ImpactEndOffsetSeconds;
        summary.ImpactWindowSeconds = source.ImpactWindowSeconds;
        summary.ImpactStatus = source.ImpactStatus;
        return summary;
    }

    private static void WriteAtomic(string path, DataContractJsonSerializer serializer, object value)
    {
        DurableFile.Write(path, file =>
        {
            using var gzip = new GZipStream(file, CompressionLevel.Optimal, true);
            serializer.WriteObject(gzip, value);
        });
    }

    private static bool IsStorageException(Exception exception) =>
        exception is IOException ||
        exception is InvalidDataException ||
        exception is System.Runtime.Serialization.SerializationException;
}
