using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;

namespace LandingStats.App.Runtime;

/// <summary>Durably isolates corrupt metadata without blocking future recovery work.
/// Keep its exact reference, or a conservative snapshot of existing engines for
/// legacy data. Source bytes stay untouched; editing metadata permits recovery.</summary>
internal static class RuntimeMetadataFailure
{
    [DataContract] private sealed class Failure
    {
        [DataMember(Order = 1)] public int Version { get; set; } = 2;
        [DataMember(Order = 2)] public string SourceStamp { get; set; } = "";
        [DataMember(Order = 3)] public string[] Engines { get; set; } = Array.Empty<string>();
        [DataMember(Order = 4)] public string Reason { get; set; } = "";
    }
    private static string Stamp(string source, bool legacy = false)
    {
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (legacy) return input.Length + ":" + File.GetLastWriteTimeUtc(source).Ticks;
            // Valid session and episode metadata fit within the manifest bound.
            // An oversized corrupt source must change length before it can be
            // repaired, so inspecting it never requires hashing unbounded data.
            var identity = Path.GetFileName(Path.GetDirectoryName(source)) + ":" + input.Length + ":";
            if (input.Length > EpisodeTransactionStore.MaximumManifestBytes)
                return identity + "oversized:" + File.GetLastWriteTimeUtc(source).Ticks;
            using var hash = SHA256.Create();
            return identity + Convert.ToBase64String(hash.ComputeHash(input));
        }
        catch (FileNotFoundException) { return "missing"; }
        catch (DirectoryNotFoundException) { return "missing"; }
    }
    private static string Marker(string directory) => Path.Combine(directory, "metadata-failure.json");
    private static Failure? Read(string directory, string source)
    {
        Failure value;
        try { value = DurableFile.ReadJson<Failure>(Marker(directory), 8L * 1024 * 1024); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((value.Version != 1 && value.Version != 2) || value.Engines == null || value.Engines.Length > 65536 || value.Engines.Any(engine => !EpisodeTransactionStore.ValidHash(engine)))
            throw new InvalidDataException("Invalid metadata failure reference.");
        return value.SourceStamp == Stamp(source, value.Version == 1) ? value : null;
    }
    public static bool Unchanged(string directory, string file)
    {
        // Earlier receipts cannot prove unchanged bytes after a timestamp-
        // preserving restore. Reinspect them before upgrading their identity.
        try { return Read(directory, Path.Combine(directory, file))?.Version == 2; }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is SerializationException || error is UnauthorizedAccessException) { return false; }
    }
    public static string[] Pin(string directory, string file, string engines, Exception error)
    {
        var source = Path.Combine(directory, file); Failure? previous;
        try { previous = Read(directory, source); }
        catch (Exception malformed) when (malformed is InvalidDataException || malformed is SerializationException)
        {
            // Preserve the damaged receipt before replacing it with another
            // conservative snapshot; it cannot pin every future engine forever.
            File.Move(Marker(directory), Marker(directory) + ".corrupt-" + Guid.NewGuid().ToString("N"));
            previous = null;
        }
        if (previous != null)
        {
            if (previous.Version == 1)
            {
                previous.Version = 2; previous.SourceStamp = Stamp(source);
                DurableFile.WriteJson(Marker(directory), previous);
            }
            return previous.Engines;
        }
        string? exact = null;
        try { exact = EngineReference.Read(directory); } catch (InvalidDataException) { }
        var hasData = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(path =>
            Path.GetFileName(path) != "runtime.owner" && Path.GetFileName(path) != "metadata-failure.json" && Path.GetFileName(path) != "engine.ref");
        var pinned = exact != null ? new[] { exact } : !hasData ? Array.Empty<string>() :
            Directory.Exists(engines) ? Directory.EnumerateDirectories(engines).Select(Path.GetFileName).Where(EpisodeTransactionStore.ValidHash).ToArray() : Array.Empty<string>();
        var reason = error.Message.Length <= 1024 ? error.Message : error.Message.Substring(0, 1024);
        DurableFile.WriteJson(Marker(directory), new Failure { SourceStamp = Stamp(source), Engines = pinned, Reason = reason });
        // The durable receipt isolates historical damage; it is not a failure of
        // the currently running service. Repeated scans retain the same evidence.
        Trace.TraceWarning("Runtime metadata quarantined at {0}: {1}", directory, reason);
        return pinned;
    }
}
