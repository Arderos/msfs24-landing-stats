using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Protocol = LandingStats.UpdateProtocol.ReleaseUpdateProtocol;

namespace LandingStats.App.Updater;

internal static class Program
{
    private const string TargetExecutableName = "MSFS-Landing-Stats.exe";
    private static readonly byte[] BundleMagic = Encoding.ASCII.GetBytes("MSFSLSABUNDLE1");

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
                "MSFS Landing Stats was not updated. The existing application was preserved.\r\n\r\n" + exception.Message,
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MSFS-Landing-Stats-Updater/3");
        var releaseRoot = Protocol.VersionReleaseRoot(invocation.Version);
        var manifestBytes = await Protocol.DownloadSmallAsync(
            client,
            releaseRoot + Protocol.ManifestName,
            Protocol.ManifestName,
            16 * 1024,
            CancellationToken.None).ConfigureAwait(false);
        var signature = Encoding.ASCII.GetString(await Protocol.DownloadSmallAsync(
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
        VerifyAssemblyVersion(ownPath, manifest.Version, "MSFS-Landing-Stats.Updater");

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
        var replacementPath = PrepareReplacement(packagePath, manifest.PackageAsset, updateRoot);
        VerifySingleFileBundle(replacementPath);
        VerifyAssemblyVersion(replacementPath, manifest.Version, "MSFS-Landing-Stats");

        WaitForTargetExit(invocation.ParentPid);
        InstallExecutableTransactionally(replacementPath, invocation.TargetPath, invocation.Version);

        var cleanupArguments = string.Join(" ", new[]
        {
            "--finish-update",
            Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            QuoteArgument(updateRoot),
        });
        var started = Process.Start(new ProcessStartInfo
        {
            FileName = invocation.TargetPath,
            WorkingDirectory = Path.GetDirectoryName(invocation.TargetPath) ?? Environment.CurrentDirectory,
            Arguments = cleanupArguments,
            UseShellExecute = false,
        });
        if (started == null)
        {
            throw new InvalidOperationException("The updated application could not be restarted");
        }
    }

    internal static string PrepareReplacement(string packagePath, string packageAsset, string updateRoot)
    {
        packagePath = Path.GetFullPath(packagePath);
        updateRoot = Path.GetFullPath(updateRoot);
        if (string.Equals(packageAsset, Protocol.PackageAssetName, StringComparison.Ordinal))
        {
            return packagePath;
        }
        if (!string.Equals(packageAsset, Protocol.LegacyPackageAssetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signed application package name is unsupported");
        }

        var replacementPath = Path.Combine(updateRoot, TargetExecutableName);
        ExtractLegacySingleExecutable(packagePath, replacementPath);
        return replacementPath;
    }

    internal static void ExtractLegacySingleExecutable(string packagePath, string destinationPath)
    {
        destinationPath = Path.GetFullPath(destinationPath);
        try
        {
            using var input = File.OpenRead(packagePath);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, false);
            if (archive.Entries.Count != 1)
            {
                throw new InvalidDataException("Signed application package must contain exactly one file");
            }

            var entry = archive.Entries[0];
            if (!string.Equals(entry.FullName, TargetExecutableName, StringComparison.Ordinal) ||
                !string.Equals(entry.Name, TargetExecutableName, StringComparison.Ordinal) ||
                entry.Length <= 0 ||
                entry.Length > Protocol.MaximumPackageBytes)
            {
                throw new InvalidDataException("Signed application package entry is invalid");
            }

            using var entryStream = entry.Open();
            using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > entry.Length || total > Protocol.MaximumPackageBytes)
                {
                    throw new InvalidDataException("Signed application package expands beyond its declared size");
                }
                output.Write(buffer, 0, read);
            }
            if (total != entry.Length)
            {
                throw new InvalidDataException("Signed application package entry length is inconsistent");
            }
        }
        catch
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            throw;
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
        if (string.Equals(processPath, invocation.TargetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var runtimeRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Runtime",
            "App")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!processPath.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Updater requester is outside the private application runtime");
        }

        VerifySingleFileBundle(invocation.TargetPath);
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

    internal static void InstallExecutableTransactionally(string replacementPath, string targetPath, Version expectedVersion)
    {
        replacementPath = Path.GetFullPath(replacementPath);
        targetPath = Path.GetFullPath(targetPath);
        if (string.Equals(replacementPath, targetPath, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(replacementPath) ||
            !File.Exists(targetPath))
        {
            throw new InvalidDataException("Executable replacement paths are invalid");
        }

        var targetDirectory = Path.GetDirectoryName(targetPath) ?? throw new InvalidDataException("Application directory is unavailable");
        var backupPath = Path.Combine(targetDirectory, ".msfs-landing-stats-backup-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Move(targetPath, backupPath);
        try
        {
            File.Move(replacementPath, targetPath);
            VerifySingleFileBundle(targetPath);
            VerifyAssemblyVersion(targetPath, expectedVersion, "MSFS-Landing-Stats");
            File.Delete(backupPath);
        }
        catch
        {
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            if (File.Exists(backupPath))
            {
                File.Move(backupPath, targetPath);
            }
            throw;
        }
    }

    private static void VerifySingleFileBundle(string path)
    {
        using var input = File.OpenRead(path);
        var trailerLength = sizeof(long) + BundleMagic.Length;
        if (input.ReadByte() != 'M' || input.ReadByte() != 'Z' || input.Length <= trailerLength)
        {
            throw new InvalidDataException("Signed application is not a Windows executable");
        }

        input.Seek(-BundleMagic.Length, SeekOrigin.End);
        var actual = new byte[BundleMagic.Length];
        if (input.Read(actual, 0, actual.Length) != actual.Length || !actual.SequenceEqual(BundleMagic))
        {
            throw new InvalidDataException("Signed application bundle is incomplete");
        }

        input.Seek(-trailerLength, SeekOrigin.End);
        using var reader = new BinaryReader(input, Encoding.UTF8, true);
        var payloadLength = reader.ReadInt64();
        if (payloadLength <= 0 || payloadLength > input.Length - trailerLength)
        {
            throw new InvalidDataException("Signed application bundle length is invalid");
        }
    }

    private static void VerifyAssemblyVersion(string executablePath, Version expected, string expectedName)
    {
        var assembly = AssemblyName.GetAssemblyName(executablePath);
        var actual = assembly.Version;
        if (!string.Equals(assembly.Name, expectedName, StringComparison.Ordinal) ||
            actual == null || actual.Major != expected.Major || actual.Minor != expected.Minor || actual.Build != expected.Build)
        {
            throw new InvalidDataException("Application version does not match the signed manifest");
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

    private static string QuoteArgument(string value)
    {
        if (value.Length != 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return value;
        }

        var result = new StringBuilder("\"");
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
