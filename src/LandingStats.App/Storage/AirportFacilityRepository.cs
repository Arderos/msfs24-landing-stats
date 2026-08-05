using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Json;
using LandingStats.App.Models;

namespace LandingStats.App.Storage;

public sealed class AirportFacilityRepository
{
    private readonly DataContractJsonSerializer _serializer =
        new DataContractJsonSerializer(typeof(List<AirportFacility>));

    public AirportFacilityRepository(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "airport-facilities.json.gz");
    }

    public string Path { get; }

    public IReadOnlyList<AirportFacility> Load()
    {
        if (!File.Exists(Path))
        {
            return Array.Empty<AirportFacility>();
        }

        try
        {
            return ReadExisting();
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is InvalidDataException ||
            exception is System.Runtime.Serialization.SerializationException)
        {
            return Array.Empty<AirportFacility>();
        }
    }

    public IReadOnlyList<AirportFacility> MergeAndSave(IEnumerable<AirportFacility> facilities)
    {
        // A read failure is not an empty database. Propagate it so a transient lock,
        // antivirus scan, or I/O error can never replace the accumulated cache with
        // the facilities from one event.
        IReadOnlyList<AirportFacility> existing;
        try
        {
            existing = ReadExisting();
        }
        catch (FileNotFoundException)
        {
            existing = Array.Empty<AirportFacility>();
        }
        catch (DirectoryNotFoundException)
        {
            existing = Array.Empty<AirportFacility>();
        }

        var merged = existing
            .Concat(facilities ?? Enumerable.Empty<AirportFacility>())
            .Where(facility => !string.IsNullOrWhiteSpace(facility.Ident))
            .GroupBy(facility => facility.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(facility => facility.Ident, StringComparer.OrdinalIgnoreCase)
            .ThenBy(facility => facility.Region, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = Path + ".tmp";
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal, false))
            {
                _serializer.WriteObject(gzip, merged);
            }

            if (File.Exists(Path))
            {
                File.Replace(temporaryPath, Path, null);
            }
            else
            {
                File.Move(temporaryPath, Path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return merged;
    }

    private IReadOnlyList<AirportFacility> ReadExisting()
    {
        using (var file = File.OpenRead(Path))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress, false))
        {
            return (_serializer.ReadObject(gzip) as List<AirportFacility>) ?? new List<AirportFacility>();
        }
    }
}
