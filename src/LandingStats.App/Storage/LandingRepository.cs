using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        var safeTimestamp = record.TimestampUtc.ToString("yyyyMMdd-HHmmss'Z'");
        var path = Path.Combine(RootPath, $"{safeTimestamp}-{record.Id}.landing.json.gz");
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

    public void UpdateSummary(LandingRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        EnsureIndexLoaded();
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

        SaveIndex();
    }

    private void EnsureIndexLoaded()
    {
        if (_summaries != null)
        {
            return;
        }

        _summaries = ReadIndex();
        if (_summaries != null)
        {
            foreach (var summary in _summaries)
            {
                summary.IsSummaryOnly = true;
            }

            return;
        }

        _summaries = new List<LandingRecord>();
        if (Directory.Exists(RootPath))
        {
            foreach (var path in Directory.EnumerateFiles(RootPath, "*.landing.json.gz"))
            {
                var record = ReadRecord(path);
                if (record != null)
                {
                    _summaries.Add(CreateSummary(record));
                }
            }
        }

        if (_summaries.Count > 0)
        {
            SaveIndex();
        }
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
