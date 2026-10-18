using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Core;

namespace LandingStats.App.Storage;

public sealed class RawCaptureRepository
{
    public const int ChunkMaximumSamples = 30000;

    public RawCaptureRepository(string? rootPath = null) : this(rootPath, true)
    {
    }

    internal RawCaptureRepository(string? rootPath, bool cleanup)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Telemetry Queue");
        if (cleanup) CleanupAbandonedTemporaryFiles();
    }

    public string RootPath { get; }

    internal static string ApplicationVersion()
    {
        var assembly = typeof(RawCaptureRepository).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return informational?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }

    internal static string ApplicationAuthor()
    {
        var assembly = typeof(RawCaptureRepository).Assembly;
        return assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "unknown";
    }

    internal static string SingleLine(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value!.Trim().Replace("\r", " ").Replace("\n", " ");
    }

    internal void CleanupAbandonedTemporaryFiles()
    {
        TemporaryEvidenceCleanup.Archives(RootPath);
    }
}

// Cleanup requires an independently published identical copy. Age alone cannot
// prove that an interrupted recording is redundant or that its writer is dead.
internal static class TemporaryEvidenceCleanup
{
    internal static void Archives(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            var temporary = Directory.EnumerateFiles(root, "*_raw.zip.tmp")
                .Concat(Directory.EnumerateFiles(root, ".interrupted-*.part")).Take(65536).ToArray();
            if (temporary.Length == 0) return;
            var published = Directory.EnumerateFiles(root, "*.zip").Take(65536).ToArray();
            foreach (var path in temporary)
            {
                if (path.EndsWith(".tmp", StringComparison.Ordinal) && DeleteDuplicate(path, path.Substring(0, path.Length - 4))) continue;
                foreach (var witness in published) if (DeleteDuplicate(path, witness)) break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool DeleteDuplicate(string temporary, string published)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(temporary), Path.GetFullPath(published), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(temporary) || !File.Exists(published) ||
                (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(published) & FileAttributes.ReparsePoint) != 0) return false;
            // Exclusive write access rejects active writers/readers. The witness
            // cannot be replaced or changed while its bytes authorize deletion.
            using var candidate = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            using var witness = new FileStream(published, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (candidate.Length != witness.Length) return false;
            var left = new byte[65536]; var right = new byte[left.Length];
            int count;
            while ((count = candidate.Read(left, 0, left.Length)) != 0)
            {
                var read = 0;
                while (read < count) { var next = witness.Read(right, read, count - read); if (next == 0) return false; read += next; }
                for (var i = 0; i < count; i++) if (left[i] != right[i]) return false;
            }
            if (witness.ReadByte() != -1) return false;
            File.Delete(temporary); return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
