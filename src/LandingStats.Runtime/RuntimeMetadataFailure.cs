using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace LandingStats.App.Runtime;

/// <summary>Corrupt metadata cannot disable collection of all future engines.
/// Keep its exact reference, or a conservative snapshot of existing engines for
/// legacy data. Source bytes stay untouched; editing metadata permits recovery.</summary>
internal static class RuntimeMetadataFailure
{
    [DataContract] private sealed class Failure
    {
        [DataMember(Order = 1)] public int Version { get; set; } = 1;
        [DataMember(Order = 2)] public string SourceStamp { get; set; } = "";
        [DataMember(Order = 3)] public string[] Engines { get; set; } = Array.Empty<string>();
        [DataMember(Order = 4)] public string Reason { get; set; } = "";
    }
    private static string Stamp(string source)
    {
        var file = new FileInfo(source);
        return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing";
    }
    private static string Marker(string directory) => Path.Combine(directory, "metadata-failure.json");
    private static Failure? Read(string directory, string source)
    {
        if (!File.Exists(Marker(directory))) return null;
        var value = DurableFile.ReadJson<Failure>(Marker(directory), 8L * 1024 * 1024);
        if (value.Version != 1 || value.Engines == null || value.Engines.Length > 65536 || value.Engines.Any(engine => !EpisodeTransactionStore.ValidHash(engine)))
            throw new InvalidDataException("Invalid metadata failure reference.");
        return value.SourceStamp == Stamp(source) ? value : null;
    }
    public static bool Unchanged(string directory, string file)
    {
        try { return Read(directory, Path.Combine(directory, file)) != null; }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is SerializationException || error is UnauthorizedAccessException) { return false; }
    }
    public static string[] Pin(string directory, string file, string engines, Exception error, Action<Guid, string>? report)
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
        if (previous != null) return previous.Engines;
        string? exact = null;
        try { exact = EngineReference.Read(directory); } catch (InvalidDataException) { }
        var hasData = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(path =>
            Path.GetFileName(path) != "runtime.owner" && Path.GetFileName(path) != "metadata-failure.json" && Path.GetFileName(path) != "engine.ref");
        var pinned = exact != null ? new[] { exact } : !hasData ? Array.Empty<string>() :
            Directory.Exists(engines) ? Directory.EnumerateDirectories(engines).Select(Path.GetFileName).Where(EpisodeTransactionStore.ValidHash).ToArray() : Array.Empty<string>();
        var reason = error.Message.Length <= 1024 ? error.Message : error.Message.Substring(0, 1024);
        DurableFile.WriteJson(Marker(directory), new Failure { SourceStamp = Stamp(source), Engines = pinned, Reason = reason });
        if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) report?.Invoke(id, reason);
        return pinned;
    }
}
