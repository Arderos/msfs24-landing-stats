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
    private readonly DataContractJsonSerializer _serializer = new DataContractJsonSerializer(typeof(LandingRecord));

    public LandingRepository(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Landings");
    }

    public string RootPath { get; }

    public IReadOnlyList<LandingRecord> LoadAll()
    {
        if (!Directory.Exists(RootPath))
        {
            return Array.Empty<LandingRecord>();
        }

        var records = new List<LandingRecord>();
        foreach (var path in Directory.EnumerateFiles(RootPath, "*.landing.json.gz"))
        {
            try
            {
                using (var file = File.OpenRead(path))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress, false))
                {
                    if (_serializer.ReadObject(gzip) is LandingRecord record &&
                        record.FormatVersion <= LandingRecord.CurrentFormatVersion)
                    {
                        records.Add(record);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is InvalidDataException ||
                exception is System.Runtime.Serialization.SerializationException)
            {
                // A damaged record must not prevent the rest of the landing history from loading.
            }
        }

        return records.OrderByDescending(record => record.TimestampUtc).ToArray();
    }

    public string Save(LandingRecord record)
    {
        Directory.CreateDirectory(RootPath);
        var safeTimestamp = record.TimestampUtc.ToString("yyyyMMdd-HHmmss'Z'");
        var path = Path.Combine(RootPath, $"{safeTimestamp}-{record.Id}.landing.json.gz");
        var temporaryPath = path + ".tmp";

        using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal, false))
        {
            _serializer.WriteObject(gzip, record);
        }

        File.Move(temporaryPath, path);
        return path;
    }
}
