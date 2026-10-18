using System;
using System.IO;

namespace LandingStats.App.Runtime;

/// <summary>A scratch directory is disposable only after its process and
/// callbacks have exited. Unknown/legacy orphan directories remain evidence.
/// Current workers take the same lease before accessing any job files.</summary>
internal sealed class RuntimeScratchLease : IDisposable
{
    private readonly string _root, _directory;
    private readonly FileStream _owner;
    private RuntimeScratchLease(string root, string directory, FileStream owner) { _root = root; _directory = directory; _owner = owner; }
    public static RuntimeScratchLease Create(string root, Guid id)
    {
        root = Path.GetFullPath(root); var directory = Path.Combine(root, id.ToString("N")); Directory.CreateDirectory(directory);
        var owner = new FileStream(Path.Combine(directory, "job.owner"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        owner.Write(new byte[] { 0, 1, 0 }, 0, 3); owner.Flush(true);
        return new RuntimeScratchLease(root, directory, owner);
    }
    public static IDisposable? EnterWorker(string directory)
    {
        var path = Path.Combine(directory, "job.owner");
        // Old callers do not have a lease. Their directories are never eligible
        // for automatic orphan collection.
        if (!File.Exists(path))
        {
            if (!Directory.Exists(directory)) throw new IOException("Scratch job directory no longer exists.");
            return null;
        }
        var owner = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (owner.Length != 3 || owner.ReadByte() != 0 || owner.ReadByte() != 1) throw new IOException("Scratch job has retired.");
            owner.WriteByte(1); owner.Flush(true); return owner;
        }
        catch { owner.Dispose(); throw; }
    }
    public void Dispose()
    {
        // The caller reaches Dispose only after ProcessJobExecutor has drained
        // callbacks and confirmed exit, including cancellation and failed jobs.
        try { _owner.Position = 0; _owner.WriteByte(1); _owner.Flush(true); }
        catch (IOException) { }
        finally { _owner.Dispose(); }
        try { Collect(_root, _directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static bool Collect(string root, string directory)
    {
        root = Path.GetFullPath(root); directory = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new ArgumentException("Scratch cleanup must stay inside its job root.");
        if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, "job.owner"))) return false;
        RequireUnlinked(directory);
        using (var owner = new FileStream(Path.Combine(directory, "job.owner"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (owner.Length != 3) return false;
            var retired = owner.ReadByte(); var version = owner.ReadByte(); var worker = owner.ReadByte();
            if (version != 1 || (retired != 0 && retired != 1) || (worker != 0 && worker != 1)) return false;
            // Exclusive acquisition also proves the parent died before a worker
            // entered. The retired marker fences a worker that arrives later.
            owner.Position = 0; owner.WriteByte(1); owner.Flush(true);
        }
        // Rename with the retired marker still present. A late worker finds
        // either that marker or a missing directory, never a legacy directory
        // whose marker was already removed. The destination stays under root.
        var retiredPath = Path.Combine(root, ".retired-" + Path.GetFileName(directory));
        Directory.Move(directory, retiredPath); Directory.Delete(retiredPath, true); return true;
    }
    internal static void RequireUnlinked(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup refuses linked runtime paths.");
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup refuses linked runtime paths.");
            if ((attributes & FileAttributes.Directory) != 0) RequireUnlinked(child);
        }
    }
}
