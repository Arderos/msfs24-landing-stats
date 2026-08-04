using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Protocol = LandingStats.UpdateProtocol.ReleaseUpdateProtocol;

namespace LandingStats.App.Updater;

internal static class Program
{
    private const string TargetExecutableName = "MSFS-Landing-Stats.exe";
    private const long MaximumExpandedPackageBytes = 64L * 1024 * 1024;

    private static readonly string[] RequiredFiles =
    {
        TargetExecutableName,
        "MSFS-Landing-Stats.exe.config",
        "LandingStats.Core.dll",
        "Microsoft.FlightSimulator.SimConnect.dll",
        "SimConnect.dll",
    };

    [STAThread]
    private static int Main(string[] args)
    {
        string? targetPath = null;
        try
        {
            var invocation = ParseInvocation(args);
            targetPath = invocation.TargetPath;
            ApplyAsync(invocation).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            TryRestartExistingApplication(targetPath);
            MessageBox.Show(
                "MSFS Landing Stats was not updated. The existing installation was preserved.\r\n\r\n" + exception.Message,
                "MSFS Landing Stats updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static async Task ApplyAsync(UpdateInvocation invocation)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var ownPath = Path.GetFullPath(Application.ExecutablePath);
        var updateRoot = Path.GetDirectoryName(ownPath) ?? throw new InvalidDataException("Updater directory is unavailable");
        VerifyUpdateRoot(updateRoot);
        VerifyTargetProcess(invocation);
        SignalReady(invocation.ReadyEventName);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MSFS-Landing-Stats-Updater/2");
        var releaseRoot = Protocol.VersionReleaseRoot(invocation.Version);
        var manifestBytes = await Protocol.DownloadSmallAsync(
            client,
            releaseRoot + Protocol.ManifestName,
            Protocol.ManifestName,
            16 * 1024,
            CancellationToken.None).ConfigureAwait(false);
        var signature = System.Text.Encoding.ASCII.GetString(await Protocol.DownloadSmallAsync(
            client,
            releaseRoot + Protocol.SignatureName,
            Protocol.SignatureName,
            8 * 1024,
            CancellationToken.None).ConfigureAwait(false));
        var manifest = Protocol.VerifyAndParse(manifestBytes, signature);
        if (manifest.Version != invocation.Version)
        {
            throw new InvalidDataException("Signed release version does not match the requested update");
        }
        if (new FileInfo(ownPath).Length != manifest.UpdaterSize ||
            !Protocol.FixedTimeEquals(Protocol.HashFile(ownPath), manifest.UpdaterSha256))
        {
            throw new InvalidDataException("Updater does not match the signed release manifest");
        }
        VerifyApplicationVersion(ownPath, manifest.Version);

        WaitForTargetExit(invocation.ParentPid);

        var packagePath = Path.Combine(updateRoot, manifest.PackageAsset);
        await Protocol.DownloadVerifiedFileAsync(
            client,
            releaseRoot + manifest.PackageAsset,
            manifest.PackageAsset,
            packagePath,
            manifest.PackageSize,
            manifest.PackageSha256,
            Protocol.MaximumPackageBytes,
            CancellationToken.None).ConfigureAwait(false);

        var targetDirectory = Path.GetDirectoryName(invocation.TargetPath)
                              ?? throw new InvalidDataException("Application directory is unavailable");
        var transactionId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(targetDirectory, ".msfs-landing-stats-update-" + transactionId);
        var backupDirectory = Path.Combine(targetDirectory, ".msfs-landing-stats-backup-" + transactionId);
        var installed = false;
        try
        {
            ExtractValidatedPackage(packagePath, stagingDirectory);
            VerifyApplicationVersion(Path.Combine(stagingDirectory, TargetExecutableName), manifest.Version);
            InstallTransactionally(stagingDirectory, backupDirectory, targetDirectory);
            VerifyApplicationVersion(invocation.TargetPath, manifest.Version);
            installed = true;
        }
        finally
        {
            DeleteDirectoryIfPresent(stagingDirectory);
            if (installed)
            {
                DeleteDirectoryIfPresent(backupDirectory);
            }
        }

        var cleanupArguments = string.Join(" ", new[]
        {
            "--finish-update",
            Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            QuoteArgument(updateRoot),
        });
        var started = Process.Start(new ProcessStartInfo
        {
            FileName = invocation.TargetPath,
            WorkingDirectory = targetDirectory,
            Arguments = cleanupArguments,
            UseShellExecute = false,
        });
        if (started == null)
        {
            throw new InvalidOperationException("The updated application could not be restarted");
        }
    }

    private static UpdateInvocation ParseInvocation(string[] args)
    {
        if (args.Length != 9 ||
            args[0] != "--apply" ||
            args[1] != "--parent-pid" ||
            args[3] != "--target" ||
            args[5] != "--version" ||
            args[7] != "--ready-event")
        {
            throw new InvalidDataException("Updater invocation is invalid");
        }
        if (!int.TryParse(args[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parentPid) || parentPid <= 0)
        {
            throw new InvalidDataException("Updater parent process is invalid");
        }

        var targetPath = Path.GetFullPath(args[4]);
        if (!string.Equals(Path.GetFileName(targetPath), TargetExecutableName, StringComparison.OrdinalIgnoreCase) || !File.Exists(targetPath))
        {
            throw new InvalidDataException("Updater target is invalid");
        }
        if (!Version.TryParse(args[6], out var version) || version.Build < 0 || version.Revision >= 0)
        {
            throw new InvalidDataException("Updater version is invalid");
        }
        var readyEventName = args[8];
        if (!readyEventName.StartsWith("Local\\MSFSLandingStatsUpdate-", StringComparison.Ordinal) ||
            readyEventName.Length != "Local\\MSFSLandingStatsUpdate-".Length + 32 ||
            !Guid.TryParseExact(readyEventName.Substring("Local\\MSFSLandingStatsUpdate-".Length), "N", out _))
        {
            throw new InvalidDataException("Updater readiness event is invalid");
        }

        return new UpdateInvocation(parentPid, targetPath, version, readyEventName);
    }

    private static void VerifyTargetProcess(UpdateInvocation invocation)
    {
        using var process = Process.GetProcessById(invocation.ParentPid);
        var processPath = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
        if (!string.Equals(processPath, invocation.TargetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Updater target does not match the requesting process");
        }
    }

    private static void WaitForTargetExit(int parentPid)
    {
        try
        {
            using var process = Process.GetProcessById(parentPid);
            if (!process.WaitForExit(60000))
            {
                throw new TimeoutException("The application did not close in time for the update");
            }
        }
        catch (ArgumentException)
        {
            // The application exited between identity verification and this wait.
        }
    }

    private static void SignalReady(string readyEventName)
    {
        using var ready = EventWaitHandle.OpenExisting(readyEventName);
        ready.Set();
    }

    internal static void ExtractValidatedPackage(string packagePath, string stagingDirectory)
    {
        Directory.CreateDirectory(stagingDirectory);
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count != RequiredFiles.Length)
        {
            throw new InvalidDataException("Update package contains an unexpected number of files");
        }

        var expected = new HashSet<string>(RequiredFiles, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) ||
                !string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal) ||
                !expected.Contains(entry.Name) ||
                !seen.Add(entry.Name))
            {
                throw new InvalidDataException("Update package contains an unexpected or unsafe entry");
            }

            if (entry.Length <= 0 || entry.Length > MaximumExpandedPackageBytes - expandedBytes)
            {
                throw new InvalidDataException("Update package expanded size is invalid");
            }

            var destination = Path.Combine(stagingDirectory, entry.Name);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[65536];
            long entryBytes = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                entryBytes += read;
                if (entryBytes > entry.Length || entryBytes > MaximumExpandedPackageBytes - expandedBytes)
                {
                    throw new InvalidDataException("Update package entry exceeds its declared size");
                }
                output.Write(buffer, 0, read);
            }
            if (entryBytes != entry.Length)
            {
                throw new InvalidDataException("Update package entry length is inconsistent");
            }
            expandedBytes += entryBytes;
        }

        if (!expected.SetEquals(seen))
        {
            throw new InvalidDataException("Update package is incomplete");
        }
    }

    internal static void InstallTransactionally(string stagingDirectory, string backupDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var movedToBackup = new List<string>();
        var movedIntoPlace = new List<string>();
        try
        {
            foreach (var file in RequiredFiles)
            {
                var target = Path.Combine(targetDirectory, file);
                var backup = Path.Combine(backupDirectory, file);
                if (!File.Exists(target))
                {
                    throw new InvalidDataException("Installed application is incomplete: " + file);
                }
                File.Move(target, backup);
                movedToBackup.Add(file);
            }

            foreach (var file in RequiredFiles)
            {
                File.Move(Path.Combine(stagingDirectory, file), Path.Combine(targetDirectory, file));
                movedIntoPlace.Add(file);
            }
        }
        catch
        {
            foreach (var file in movedIntoPlace.AsEnumerable().Reverse())
            {
                var target = Path.Combine(targetDirectory, file);
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            foreach (var file in movedToBackup.AsEnumerable().Reverse())
            {
                var backup = Path.Combine(backupDirectory, file);
                var target = Path.Combine(targetDirectory, file);
                if (File.Exists(backup))
                {
                    File.Move(backup, target);
                }
            }
            throw;
        }
    }

    private static void VerifyApplicationVersion(string executablePath, Version expected)
    {
        var actual = AssemblyName.GetAssemblyName(executablePath).Version;
        if (actual == null || actual.Major != expected.Major || actual.Minor != expected.Minor || actual.Build != expected.Build)
        {
            throw new InvalidDataException("Application package version does not match the signed manifest");
        }
    }

    private static void VerifyUpdateRoot(string updateRoot)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Updates")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(updateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate, allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Updater is running from an unsafe directory");
        }
    }

    private static void TryRestartExistingApplication(string? targetPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    WorkingDirectory = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                });
            }
        }
        catch
        {
            // The visible error still tells the user the update did not complete.
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
            // A failed update may leave only a bounded staging directory beside the app.
        }
        catch (UnauthorizedAccessException)
        {
            // The update result is already determined; cleanup can be retried manually.
        }
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length != 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return value;
        }

        var result = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private sealed class UpdateInvocation
    {
        public UpdateInvocation(int parentPid, string targetPath, Version version, string readyEventName)
        {
            ParentPid = parentPid;
            TargetPath = targetPath;
            Version = version;
            ReadyEventName = readyEventName;
        }

        public int ParentPid { get; }
        public string TargetPath { get; }
        public Version Version { get; }
        public string ReadyEventName { get; }
    }
}
