using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace LandingStats.App.Runtime;

/// <summary>Publish only complete, flushed files. Callers serialize writes to a destination.</summary>
internal static class DurableFile
{
    public static void Write(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        CleanupRedundantTemporaries(path);
        // Keep temporary names short: net48 deployments can still have MAX_PATH
        // semantics even when the final destination itself fits.
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".w" + Guid.NewGuid().ToString("N") + ".tmp");
        var publishing = false;
        Exception? failure = null;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            { using var bounded = RuntimeStorageQuota.Protect(file, path, true); write(bounded); file.Flush(true); }
            publishing = true; PublishTemporary(temporary, path);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { if ((!publishing || File.Exists(path)) && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanup) when (failure != null && (cleanup is IOException || cleanup is UnauthorizedAccessException)) { }
        }
    }

    private static void CleanupRedundantTemporaries(string published)
    {
        if (!File.Exists(published)) return;
        try
        {
            var examined = 0;
            foreach (var temporary in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(published))!, ".w*.tmp"))
            {
                if (++examined > 128) break;
                var name = Path.GetFileName(temporary);
                if (name.Length != 38 || !Guid.TryParseExact(name.Substring(2, 32), "N", out _) ||
                    (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    using var candidate = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                    using var witness = new FileStream(published, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (candidate.Length != witness.Length) continue;
                    var left = new byte[65536]; var right = new byte[left.Length]; var identical = true; int count;
                    while (identical && (count = candidate.Read(left, 0, left.Length)) != 0)
                    {
                        var read = 0;
                        while (read < count) { var next = witness.Read(right, read, count - read); if (next == 0) break; read += next; }
                        if (read != count) { identical = false; break; }
                        for (var i = 0; i < count; i++) if (left[i] != right[i]) { identical = false; break; }
                    }
                    if (identical && witness.ReadByte() == -1) File.Delete(temporary);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void PublishTemporary(string temporary, string path)
    {
        try
        {
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        catch (IOException)
        {
            // ReplaceFile may remove the destination before failing (1176).
            // The flushed replacement is then the sole surviving copy.
            if (File.Exists(path) || !File.Exists(temporary)) throw;
            File.Move(temporary, path);
        }
    }

    public static void WriteJson<T>(string path, T value) => Write(path, stream =>
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value));

    public static T ReadJson<T>(string path, long maximumBytes)
    {
        // Atomic replacement leaves existing readers on the previous complete
        // file. Do not make a read-only observer block publication on Windows.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > maximumBytes) throw new InvalidDataException("Persisted metadata exceeds its limit.");
        return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(file) ??
            throw new InvalidDataException("Persisted metadata is empty."));
    }
}
