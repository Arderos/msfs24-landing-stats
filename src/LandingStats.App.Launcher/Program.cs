using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LandingStats.App.Runtime;

namespace LandingStats.App.Launcher;

internal static class Program
{
    private const string VerifyArgument = "--verify-bundle";
    private const string ChildExecutable = "MSFS-Landing-Stats.exe";
    private const string LauncherPathEnvironmentVariable = "MSFS_LANDING_STATS_LAUNCHER_PATH";

    private static readonly string[] RequiredFiles = LandingStats.Packaging.RuntimePayload.AllowedFiles;

    private static readonly string[][] LegacyRuntimeFileSets =
    {
        new[]
        {
            ".complete",
            "LandingStats.App.exe",
            "LandingStats.App.exe.config",
            "LandingStats.Core.dll",
            "Microsoft.FlightSimulator.SimConnect.dll",
            "SimConnect.dll",
        },
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var payload = ReadPayload();
            var bundledFiles = ValidateArchive(payload);
            if (args.Any(argument => string.Equals(argument, VerifyArgument, StringComparison.OrdinalIgnoreCase)))
            {
                return 0;
            }

            if (!GuardianInstance.TryAcquire(out var instance)) return 0;
            using (instance!)
            {
                // Preserve simulator-owned launch policy through the external
                // updater and its replacement launcher as well as App retries.
                if (Array.IndexOf(args, "--simulator-autostart") >= 0)
                    Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_SIMULATOR_AUTOSTART", "1");
                // An older/direct runtime has its own mutex but no guardian.
                // Do not replace its extracted files or repeatedly spawn duplicates.
                if (GuardianInstance.ApplicationAlreadyRunning()) return 0;
                var runtimeDirectory = RuntimeDirectoryPath();
                var session = Guid.NewGuid();
                var launcher = Assembly.GetExecutingAssembly().Location;
                var updates = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSFS Landing Stats", "Updates");
                var supervisor = new GuardianSupervisor(generation =>
                {
                    // The previous process tree has exited before this factory
                    // runs. Repair deleted/corrupted cached binaries from the
                    // same embedded payload before admitting the next owner.
                    PrepareRuntime(payload, bundledFiles);
                    return new GuardianGeneration(session, generation,
                        (request, token) => GuardianUpdater.PrepareAsync(request, launcher, updates, token));
                });
                instance!.StartActivation(() => supervisor.Current!.ActivateAsync(), () => supervisor.Current?.Health?.Ready == true);
                supervisor.Changed += LogGuardian;
                Microsoft.Win32.SessionEndingEventHandler ending = (_, _) => supervisor.Stop();
                Microsoft.Win32.SystemEvents.SessionEnding += ending;
                try
                {
                    supervisor.RunAsync(Path.Combine(runtimeDirectory, ChildExecutable), args,
                        Path.GetDirectoryName(launcher) ?? Environment.CurrentDirectory,
                        new Dictionary<string, string> { [LauncherPathEnvironmentVariable] = launcher }).GetAwaiter().GetResult();
                }
                finally { Microsoft.Win32.SystemEvents.SessionEnding -= ending; }
            }

            return 0;
        }
        catch (Exception exception)
        {
            if (args.Any(argument => string.Equals(argument, VerifyArgument, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
            MessageBox.Show(
                "MSFS Landing Stats could not start.\r\n\r\n" + exception.Message,
                "MSFS Landing Stats",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void LogGuardian(long generation, string state)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSFS Landing Stats", "Runtime");
            Directory.CreateDirectory(directory); var path = Path.Combine(directory, "guardian.log");
            if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
            { var previous = path + ".previous"; if (File.Exists(previous)) File.Delete(previous); File.Move(path, previous); }
            File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " generation=" + generation + " " + state + Environment.NewLine);
        }
        catch { /* Diagnostics cannot prevent ownership and recovery. */ }
    }

    private static string PrepareRuntime(byte[] payload, IReadOnlyCollection<string> bundledFiles)
    {
        var runtimeDirectory = RuntimeDirectoryPath();
        var runtimeRoot = Path.GetDirectoryName(runtimeDirectory)!;
        var versionText = Path.GetFileName(runtimeDirectory);
        var markerPath = Path.Combine(runtimeDirectory, ".bootstrap");
        var payloadIdentity = PayloadIdentity(payload);
        var mutexName = "Local\\MSFSLandingStatsBootstrap-" + versionText.Replace('.', '-');

        using (var mutex = new Mutex(false, mutexName))
        {
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(TimeSpan.FromMinutes(2));
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    throw new TimeoutException("Timed out while preparing the application runtime.");
                }

                // Reuse only the exact embedded payload; a new launcher repairs
                // a missing or altered cache after the previous job has retired.
                if (File.Exists(markerPath) &&
                    string.Equals(File.ReadAllText(markerPath).Trim(), payloadIdentity, StringComparison.Ordinal) &&
                    RuntimeMatchesPayload(runtimeDirectory, payload, bundledFiles))
                {
                    CleanupOldRuntimeDirectories(runtimeRoot, runtimeDirectory);
                    CleanupLegacyRuntimeRoot(runtimeRoot);
                    return runtimeDirectory;
                }

                Directory.CreateDirectory(runtimeRoot);
                var stagingDirectory = Path.Combine(runtimeRoot, ".staging-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ExtractArchive(payload, stagingDirectory);
                    if (!RuntimeMatchesPayload(stagingDirectory, payload, bundledFiles))
                    {
                        throw new InvalidDataException("The application bundle is incomplete.");
                    }

                    File.WriteAllText(
                        Path.Combine(stagingDirectory, ".bootstrap"),
                        payloadIdentity,
                        new UTF8Encoding(false));
                    if (Directory.Exists(runtimeDirectory))
                    {
                        Directory.Delete(runtimeDirectory, true);
                    }

                    Directory.Move(stagingDirectory, runtimeDirectory);
                }
                finally
                {
                    if (Directory.Exists(stagingDirectory))
                    {
                        Directory.Delete(stagingDirectory, true);
                    }
                }

                CleanupOldRuntimeDirectories(runtimeRoot, runtimeDirectory);
                CleanupLegacyRuntimeRoot(runtimeRoot);
                return runtimeDirectory;
            }
            finally
            {
                if (ownsMutex)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    private static string RuntimeDirectoryPath()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? throw new InvalidDataException("Bootstrap version is unavailable.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSFS Landing Stats", "Runtime", "App",
            $"{version.Major}.{version.Minor}.{version.Build}-guardian");
    }

    private static byte[] ReadPayload()
    {
        using (var executable = File.OpenRead(Assembly.GetExecutingAssembly().Location))
        using (var reader = new BinaryReader(executable, Encoding.UTF8, true))
        {
            var bounds = LandingStats.Packaging.BundlePayload.Locate(executable);
            executable.Position = bounds.Offset;
            var payload = reader.ReadBytes(bounds.Length);
            if (payload.Length != bounds.Length)
            {
                throw new EndOfStreamException("The application bundle is truncated.");
            }

            return payload;
        }
    }

    private static IReadOnlyCollection<string> ValidateArchive(byte[] payload)
    {
        using var memory = new MemoryStream(payload, false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, false);
        if (archive.Entries.Count < RequiredFiles.Length)
        {
            throw new InvalidDataException("The application bundle is incomplete.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) ||
                !string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal) ||
                entry.Length <= 0 ||
                !IsAllowedBundleFile(entry.Name) ||
                !seen.Add(entry.Name))
            {
                throw new InvalidDataException("The application bundle contains an unexpected or unsafe entry.");
            }
        }

        if (RequiredFiles.Any(file => !seen.Contains(file)))
        {
            throw new InvalidDataException("The application bundle is incomplete.");
        }

        return seen.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsAllowedBundleFile(string name) => LandingStats.Packaging.RuntimePayload.IsAllowed(name);

    private static void ExtractArchive(byte[] payload, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        using var memory = new MemoryStream(payload, false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, false);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.Combine(destinationDirectory, entry.Name);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            if (output.Length != entry.Length)
            {
                throw new InvalidDataException("The application bundle entry is truncated: " + entry.Name);
            }
        }
    }

    private static bool RuntimeIsComplete(
        string runtimeDirectory,
        IReadOnlyCollection<string> bundledFiles)
    {
        if (!bundledFiles.All(file =>
        {
            var path = Path.Combine(runtimeDirectory, file);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }))
        {
            return false;
        }

        // Runtime directories are user-visible and may legitimately contain
        // desktop.ini, antivirus leftovers, or diagnostic files. Completeness
        // depends only on every bundled file being present and non-empty.
        return true;
    }

    private static string PayloadIdentity(byte[] payload)
    {
        using var sha256 = SHA256.Create();
        return string.Concat(sha256.ComputeHash(payload).Select(value => value.ToString("x2")));
    }

    private static bool RuntimeMatchesPayload(string runtimeDirectory, byte[] payload, IReadOnlyCollection<string> bundledFiles)
    {
        if (!RuntimeIsComplete(runtimeDirectory, bundledFiles)) return false;
        try
        {
            using var memory = new MemoryStream(payload, false);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read, false);
            using var sha = SHA256.Create();
            foreach (var entry in archive.Entries)
            {
                using var actual = File.OpenRead(Path.Combine(runtimeDirectory, entry.Name));
                if (actual.Length != entry.Length) return false;
                using var expected = entry.Open();
                if (!sha.ComputeHash(actual).SequenceEqual(sha.ComputeHash(expected))) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void CleanupOldRuntimeDirectories(string runtimeRoot, string currentRuntimeDirectory)
    {
        if (!Directory.Exists(runtimeRoot))
        {
            return;
        }

        var resolvedRoot = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolvedCurrent = Path.GetFullPath(currentRuntimeDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(resolvedRoot).ToArray();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var resolvedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Directory.GetParent(resolvedCandidate)?.FullName
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(resolvedCandidate);
                if (!string.Equals(parent, resolvedRoot, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(resolvedCandidate, resolvedCurrent, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(resolvedCandidate) & FileAttributes.ReparsePoint) != 0 ||
                    (!Version.TryParse(name, out _) && !Version.TryParse(name.Replace("-guardian", ""), out _) &&
                        !IsStalePreparation(resolvedCandidate, name) && !IsLegacyHashRuntimeDirectory(resolvedCandidate, name)))
                {
                    continue;
                }

                CollectRuntimeDirectory(resolvedCandidate);
            }
            catch (IOException)
            {
                // A running older process can still have files loaded. Keep it and
                // retry after that process exits on a later launch.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best-effort and must not block application startup.
            }
        }
    }

    private static void CleanupLegacyRuntimeRoot(string currentRuntimeRoot)
    {
        var legacyRoot = Directory.GetParent(currentRuntimeRoot)?.FullName;
        if (string.IsNullOrEmpty(legacyRoot) || !Directory.Exists(legacyRoot))
        {
            return;
        }

        var resolvedRoot = Path.GetFullPath(legacyRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(resolvedRoot).ToArray();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var resolvedCandidate = Path.GetFullPath(candidate)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Directory.GetParent(resolvedCandidate)?.FullName
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(resolvedCandidate);
                if (!string.Equals(parent, resolvedRoot, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(resolvedCandidate) & FileAttributes.ReparsePoint) != 0 ||
                    !IsLegacyHashRuntimeDirectory(resolvedCandidate, name))
                {
                    continue;
                }

                CollectRuntimeDirectory(resolvedCandidate);
            }
            catch (IOException)
            {
                // A running legacy process can still hold the runtime open.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best-effort and must not block application startup.
            }
        }
    }

    private static bool IsLegacyHashRuntimeDirectory(string directory, string name)
    {
        if (name.Length != 64 || name.Any(value => !Uri.IsHexDigit(value)))
        {
            return false;
        }

        var markerPath = Path.Combine(directory, ".complete");
        try
        {
            if (!File.Exists(markerPath) ||
                !string.Equals(File.ReadAllText(markerPath).Trim(), name, StringComparison.OrdinalIgnoreCase) ||
                Directory.EnumerateDirectories(directory).Any())
            {
                return false;
            }

            var actualFiles = Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return LegacyRuntimeFileSets.Any(expected =>
                actualFiles.SequenceEqual(
                    expected.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsStalePreparation(string directory, string name) =>
        (name.StartsWith(".staging-", StringComparison.Ordinal) || name.StartsWith(".retired-", StringComparison.Ordinal)) &&
        Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1);

    private static void CollectRuntimeDirectory(string directory)
    {
        // Probe every file before deleting any. Keeping handles without read
        // sharing also excludes a loader racing the collection. Loaded images
        // refuse this write/exclusive access, so their whole cache survives.
        if (Directory.EnumerateDirectories(directory).Any()) return;
        var held = new List<FileStream>();
        var files = Directory.EnumerateFiles(directory).ToArray();
        try
        {
            foreach (var path in files)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                held.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete));
            }
            // Windows cannot rename a directory while these child handles are
            // open, even with delete sharing. Unlink only after every file was
            // proven unused; holding all handles prevents a loader entering
            // between the preflight and removal of the complete file set.
            foreach (var path in files) File.Delete(path);
        }
        finally { foreach (var file in held) file.Dispose(); }
        Directory.Delete(directory, false);
    }

}
