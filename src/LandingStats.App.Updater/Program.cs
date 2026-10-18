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
    [STAThread]
    private static int Main(string[] args)
    {
        UpdateInvocation? invocation = null;
        var committed = false;
        try
        {
            invocation = ParseInvocation(args);
            ApplyAsync(invocation, () => committed = true).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            if (committed && invocation != null) TryRestartExistingApplication(invocation);
            MessageBox.Show(
                "MSFS Landing Stats was not updated. The existing application was preserved.\r\n\r\n" + exception.Message,
                "MSFS Landing Stats updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static async Task ApplyAsync(UpdateInvocation invocation, Action committed)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var ownPath = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        var updateRoot = Path.GetDirectoryName(ownPath) ?? throw new InvalidDataException("Updater directory is unavailable");
        VerifyUpdateRoot(updateRoot);
        using var target = VerifyTargetProcess(invocation);
        // Own the commit object before advertising readiness; the guardian may
        // signal and close its last handle immediately after ready.
        using var commit = invocation.CommitEventName == null ? null : EventWaitHandle.OpenExisting(invocation.CommitEventName);
        SignalReady(invocation.ReadyEventName);
        if (commit != null) await WaitForCommitAsync(commit, target).ConfigureAwait(false);
        committed();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MSFS-Landing-Stats-Updater/3");
        var releaseRoot = Protocol.VersionReleaseRoot(invocation.Version);
        var signatureName = SignatureNameFor(invocation.ManifestName);
        var manifestBytes = await Protocol.DownloadSmallAsync(
            client,
            releaseRoot + invocation.ManifestName,
            invocation.ManifestName,
            16 * 1024,
            CancellationToken.None).ConfigureAwait(false);
        var signature = Encoding.ASCII.GetString(await Protocol.DownloadSmallAsync(
            client,
            releaseRoot + signatureName,
            signatureName,
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

        WaitForTargetExit(target);
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

        var replacementPath = Path.Combine(updateRoot, Protocol.PackageAssetName);
        ExtractLegacySingleExecutable(packagePath, replacementPath);
        return replacementPath;
    }

    internal static void ExtractLegacySingleExecutable(string packagePath, string destinationPath)
    {
        destinationPath = Path.GetFullPath(destinationPath);
        var createdDestination = false;
        try
        {
            using var input = File.OpenRead(packagePath);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, false);
            if (archive.Entries.Count != 1)
            {
                throw new InvalidDataException("Signed application package must contain exactly one file");
            }

            var entry = archive.Entries[0];
            if (!string.Equals(entry.FullName, Protocol.PackageAssetName, StringComparison.Ordinal) ||
                !string.Equals(entry.Name, Protocol.PackageAssetName, StringComparison.Ordinal) ||
                entry.Length <= 0 ||
                entry.Length > Protocol.MaximumPackageBytes)
            {
                throw new InvalidDataException("Signed application package entry is invalid");
            }

            using var entryStream = entry.Open();
            using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            createdDestination = true;
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
            if (createdDestination && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            throw;
        }
    }

    private static UpdateInvocation ParseInvocation(string[] args)
    {
        if ((args.Length != 9 && args.Length != 11 && args.Length != 13) ||
            args[0] != "--apply" ||
            args[1] != "--parent-pid" ||
            args[3] != "--target" ||
            args[5] != "--version" ||
            args[7] != "--ready-event" ||
            (args.Length >= 11 && args[9] != "--manifest") ||
            (args.Length == 13 && args[11] != "--commit-event"))
        {
            throw new InvalidDataException("Updater invocation is invalid");
        }
        if (!int.TryParse(args[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parentPid) || parentPid <= 0)
        {
            throw new InvalidDataException("Updater parent process is invalid");
        }

        var targetPath = Path.GetFullPath(args[4]);
        var targetFileName = Path.GetFileName(targetPath);
        if (targetFileName.Length <= ".exe".Length ||
            !string.Equals(Path.GetExtension(targetFileName), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(targetPath))
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

        var manifestName = args.Length >= 11 ? args[10] : Protocol.ManifestName;
        SignatureNameFor(manifestName);
        var commit = args.Length == 13 ? args[12] : null;
        if (commit != null && (!commit.StartsWith("Local\\MSFSLandingStatsUpdateCommit-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(commit.Substring("Local\\MSFSLandingStatsUpdateCommit-".Length), "N", out _)))
            throw new InvalidDataException("Updater commit event is invalid");
        return new UpdateInvocation(parentPid, targetPath, version, readyEventName, manifestName, commit);
    }

    private static string SignatureNameFor(string manifestName)
    {
        if (string.Equals(manifestName, Protocol.ManifestName, StringComparison.Ordinal))
        {
            return Protocol.SignatureName;
        }
        if (string.Equals(manifestName, Protocol.ChannelManifestName, StringComparison.Ordinal))
        {
            return Protocol.ChannelSignatureName;
        }
        throw new InvalidDataException("Updater manifest channel is invalid");
    }

    private static Process VerifyTargetProcess(UpdateInvocation invocation)
    {
        var process = Process.GetProcessById(invocation.ParentPid);
        try
        {
        _ = process.Handle; // Retain this exact process identity across downloads.
        var processPath = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
        if (string.Equals(processPath, invocation.TargetPath, StringComparison.OrdinalIgnoreCase))
        {
            return process;
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
        return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static void WaitForTargetExit(Process process)
    {
        if (!process.WaitForExit(150000)) throw new TimeoutException("The application did not close in time for the update");
    }

    private static async Task WaitForCommitAsync(EventWaitHandle commit, Process parent)
    {
        var watch = Stopwatch.StartNew();
        while (!commit.WaitOne(0))
        {
            if (parent.HasExited) throw new OperationCanceledException("Guardian exited before committing the update");
            if (watch.Elapsed >= TimeSpan.FromSeconds(20)) throw new TimeoutException("Guardian did not commit the prepared update");
            await Task.Delay(20).ConfigureAwait(false);
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
        // Replacement is committed. Failure to collect the old executable must
        // never roll a verified new executable back.
        try { File.Delete(backupPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void VerifySingleFileBundle(string path)
    {
        using var input = File.OpenRead(path);
        LandingStats.Packaging.BundlePayload.Locate(input);
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

    private static void TryRestartExistingApplication(UpdateInvocation invocation)
    {
        try
        {
            // The target process is the guardian for a committed handoff. Its
            // mutex must be gone before a new launcher can become resident.
            try { using var parent = Process.GetProcessById(invocation.ParentPid); if (!parent.WaitForExit(150000)) return; }
            catch (ArgumentException) { }
            var targetPath = invocation.TargetPath;
            if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
            {
                var start = new ProcessStartInfo
                {
                    FileName = targetPath,
                    WorkingDirectory = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                };
                start.EnvironmentVariables["MSFS_LANDING_STATS_SKIP_UPDATE_ONCE"] = "1";
                Process.Start(start);
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
        public UpdateInvocation(int parentPid, string targetPath, Version version, string readyEventName, string manifestName, string? commitEventName = null)
        {
            ParentPid = parentPid;
            TargetPath = targetPath;
            Version = version;
            ReadyEventName = readyEventName;
            ManifestName = manifestName;
            CommitEventName = commitEventName;
        }

        public int ParentPid { get; }
        public string TargetPath { get; }
        public Version Version { get; }
        public string ReadyEventName { get; }
        public string ManifestName { get; }
        public string? CommitEventName { get; }
    }
}
