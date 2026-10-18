using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;

namespace LandingStats.App.Telemetry;

internal sealed partial class FlightModelGeometryResolver
{
    // A session's ready snapshot contains geometry only. Package validation is
    // performed by the background worker, never by a landing's reader.
    internal static string PreparedCatalogPath(string directory, string simulator)
    {
        using var hash = SHA256.Create();
        var key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(simulator))).Replace("-", "").ToLowerInvariant();
        return Path.Combine(directory, "prepared-" + key + ".json");
    }
    internal async Task WritePreparedCatalogAsync(string path)
    {
        var catalog = await CurrentCatalogTask().ConfigureAwait(false);
        DurableFile.WriteJson(path, new GeometryCatalogData
        { Matches = catalog.Matches.Select(CachedGeometryMatch.Capture).ToArray(), Failures = catalog.Failures });
    }
    internal static FlightModelGeometryResolver FromPreparedCatalog(string path) => new FlightModelGeometryResolver(Task.Run(() =>
    {
        var data = DurableFile.ReadJson<GeometryCatalogData>(path, 16L * 1024 * 1024);
        if (data.Version != 3 || data.Matches == null || data.Matches.Length > 100000 || data.Failures == null)
            throw new InvalidDataException("Invalid prepared geometry catalog.");
        var catalog = new Catalog();
        foreach (var item in data.Matches) catalog.Matches.Add((item ?? throw new InvalidDataException("Missing prepared geometry.")).Restore());
        foreach (var failure in data.Failures) catalog.Failures[failure.Key] = failure.Value;
        return catalog;
    }));

    private readonly string? _persistentCacheDirectory;
    [ThreadStatic] private static Dictionary<string, CatalogPathStamp>? _catalogPaths;
    [ThreadStatic] private static bool _catalogUncacheable;
    internal static void TrackCatalogPath(string path, bool directory)
    {
        if (_catalogPaths == null) return;
        var key = (directory ? "d:" : "f:") + Path.GetFullPath(path);
        if (!_catalogPaths.ContainsKey(key))
            try { _catalogPaths.Add(key, CatalogPathStamp.Capture(path, directory)); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { _catalogUncacheable = true; }
    }

    private Catalog BuildPersistentCatalog(string[] roots)
    {
        if (_persistentCacheDirectory == null) return BuildCatalog(roots);
        string key;
        using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
            "LS-CFG-CATALOG/1\n" + string.Join("\n", roots.Select(Path.GetFullPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))))).Replace("-", "").ToLowerInvariant();
        var path = Path.Combine(_persistentCacheDirectory, key + ".json");
        try
        {
            var cached = DurableFile.ReadJson<GeometryCatalogData>(path, 16L * 1024 * 1024);
            if (cached.Version == 3 && cached.Stamps != null && cached.Stamps.Length > 0 && cached.Stamps.Length <= 100000 &&
                AllStampsUnchanged(cached.Stamps) && cached.Matches != null && cached.Matches.Length <= 100000 && cached.Matches.All(item => item != null) && cached.Failures != null)
            {
                var restored = new Catalog();
                foreach (var item in cached.Matches) restored.Matches.Add(item.Restore());
                foreach (var failure in cached.Failures) restored.Failures[failure.Key] = failure.Value;
                return restored;
            }
        }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is SerializationException || error is ArgumentException)
        { /* A disposable cache can always be rebuilt from installed sources. */ }
        var previous = _catalogPaths; var previousUncacheable = _catalogUncacheable;
        var stamps = new Dictionary<string, CatalogPathStamp>(StringComparer.OrdinalIgnoreCase); var uncacheable = false;
        Catalog catalog;
        _catalogPaths = stamps; _catalogUncacheable = false;
        try { catalog = BuildCatalog(roots); }
        finally { uncacheable = _catalogUncacheable; _catalogPaths = previous; _catalogUncacheable = previousUncacheable; }
        // Never publish a view whose dependencies changed during enumeration.
        // The current resolution remains best effort; the next process rebuilds.
        if (!uncacheable && stamps.Count > 0 && stamps.Count <= 100000 && AllStampsUnchanged(stamps.Values))
            try
            {
                DurableFile.WriteJson(path, new GeometryCatalogData
                { Stamps = stamps.Values.ToArray(), Matches = catalog.Matches.Select(CachedGeometryMatch.Capture).ToArray(), Failures = catalog.Failures });
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { /* Cache persistence is optional and cannot make geometry unavailable. */ }
        return catalog;
    }

    private static bool AllStampsUnchanged(ICollection<CatalogPathStamp> stamps)
    {
        if (stamps.Count < 128) return stamps.All(stamp => stamp != null && stamp.Unchanged());
        // Each probe reads an independent immutable stamp. Bound filesystem
        // concurrency while retaining every dependency check in large installs.
        var unchanged = 1;
        Exception? failure = null;
        Parallel.ForEach(stamps, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (stamp, loop) =>
        {
            try
            {
                if (stamp == null || !stamp.Unchanged())
                {
                    Interlocked.Exchange(ref unchanged, 0);
                    loop.Stop();
                }
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, error, null);
                loop.Stop();
            }
        });
        // Preserve the sequential path's optional-cache exception handling;
        // Parallel.ForEach must not turn a known exception into AggregateException.
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return unchanged != 0;
    }

    [DataContract] private sealed class GeometryCatalogData
    {
        [DataMember(Order = 1)] public int Version { get; set; } = 3;
        [DataMember(Order = 2)] public CatalogPathStamp[] Stamps { get; set; } = Array.Empty<CatalogPathStamp>();
        [DataMember(Order = 3)] public CachedGeometryMatch[] Matches { get; set; } = Array.Empty<CachedGeometryMatch>();
        [DataMember(Order = 4)] public Dictionary<string, string> Failures { get; set; } = new Dictionary<string, string>();
    }
    [DataContract] private sealed class CatalogPathStamp
    {
        [DataMember(Order = 1)] public string Path { get; set; } = "";
        [DataMember(Order = 2)] public bool Directory { get; set; }
        [DataMember(Order = 3)] public bool Exists { get; set; }
        [DataMember(Order = 4)] public long Modified { get; set; }
        [DataMember(Order = 5)] public long Length { get; set; }
        [DataMember(Order = 6)] public string Resolved { get; set; } = "";
        [DataMember(Order = 7)] public string Entries { get; set; } = "";
        public static CatalogPathStamp Capture(string path, bool directory)
        {
            FileSystemInfo info = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path); info.Refresh();
            return new CatalogPathStamp { Path = System.IO.Path.GetFullPath(path), Directory = directory, Exists = info.Exists,
                Modified = info.Exists ? info.LastWriteTimeUtc.Ticks : 0, Length = info.Exists && !directory ? ((FileInfo)info).Length : 0,
                Resolved = directory && info.Exists ? DirectoryTraversalIdentity.Resolve(path) : "",
                Entries = directory && info.Exists ? EntryHash(path) : "" };
        }
        private static string EntryHash(string path)
        {
            using var hash = SHA256.Create();
            try
            {
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",
                    System.IO.Directory.EnumerateFileSystemEntries(path).Select(System.IO.Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)))));
            }
            catch (DirectoryNotFoundException)
            {
                // A dangling package junction can still have Exists == true.
                // Remember its unavailable contents without disabling caching
                // for every other aircraft. Unchanged probes again, so a target
                // appearing later replaces this marker with its entries hash.
                return "directory-unavailable";
            }
        }
        public bool Unchanged()
        {
            try
            {
                var now = Capture(Path, Directory);
                return now.Exists == Exists && now.Modified == Modified && now.Length == Length && now.Resolved == Resolved && now.Entries == Entries;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException) { return false; }
        }
    }
    [DataContract] private sealed class CachedGeometryMatch
    {
        [DataMember(Order = 1)] public string Title { get; set; } = "";
        [DataMember(Order = 2)] public string Model { get; set; } = "";
        [DataMember(Order = 3)] public string Config { get; set; } = "";
        [DataMember(Order = 4)] public double Arm { get; set; }
        [DataMember(Order = 5)] public double Datum { get; set; }
        [DataMember(Order = 6)] public int[] MainIndices { get; set; } = Array.Empty<int>();
        [DataMember(Order = 7)] public double[] MainArms { get; set; } = Array.Empty<double>();
        [DataMember(Order = 8)] public int[] Nose { get; set; } = Array.Empty<int>();
        public static CachedGeometryMatch Capture(FlightModelGeometryMatch value) => new CachedGeometryMatch
        { Title = value.AircraftTitle, Model = value.AircraftModel, Config = value.ConfigPath, Arm = value.MainGearLongitudinalFeet,
            Datum = value.ReferenceDatumLongitudinalFeet, MainIndices = value.MainGearPoints.Select(point => point.ContactPointIndex).ToArray(),
            MainArms = value.MainGearPoints.Select(point => point.LongitudinalFeet).ToArray(), Nose = value.NoseGearContactPointIndices.ToArray() };
        public FlightModelGeometryMatch Restore()
        {
            if (string.IsNullOrEmpty(Title) || MainIndices == null || MainArms == null || Nose == null || MainIndices.Length != MainArms.Length ||
                MainIndices.Length > 20 || Nose.Length > 20 || MainIndices.Concat(Nose).Any(index => index < 0 || index >= 20) ||
                MainArms.Any(value => double.IsNaN(value) || double.IsInfinity(value)) || double.IsNaN(Arm) || double.IsInfinity(Arm) || double.IsNaN(Datum) || double.IsInfinity(Datum))
                throw new InvalidDataException("Invalid cached geometry.");
            return new FlightModelGeometryMatch(Title, Model, new FlightModelGearGeometry(Arm,
                MainIndices.Select((index, i) => new FlightModelGearPoint(index, MainArms[i])).ToArray(), Nose, Config, Datum));
        }
    }
}
