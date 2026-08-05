using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protocol = LandingStats.UpdateProtocol.ReleaseUpdateProtocol;

namespace LandingStats.App.Updates;

internal enum ReleaseUpdateState
{
    Current,
    UpdateStarted,
    Unavailable,
    Rejected,
}

internal sealed class ReleaseUpdateResult
{
    public ReleaseUpdateResult(ReleaseUpdateState state, string message, Version? version = null, string? path = null)
    {
        State = state;
        Message = message;
        Version = version;
        Path = path;
    }

    public ReleaseUpdateState State { get; }
    public string Message { get; }
    public Version? Version { get; }
    public string? Path { get; }
}

internal sealed class ReleaseUpdater : IDisposable
{
    private const string TargetExecutableName = "MSFS-Landing-Stats.exe";
    private const string LauncherPathEnvironmentVariable = "MSFS_LANDING_STATS_LAUNCHER_PATH";

    private readonly HttpClient _client;

    public ReleaseUpdater(HttpMessageHandler? handler = null)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        _client = handler == null ? new HttpClient() : new HttpClient(handler, true);
        _client.Timeout = TimeSpan.FromSeconds(45);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("MSFS-Landing-Stats-Updater/3");
    }

    public async Task<ReleaseUpdateResult> CheckAndInstallAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        string? updateRoot = null;
        var updaterStarted = false;
        try
        {
            var manifestBytes = await Protocol.DownloadSmallAsync(
                _client,
                Protocol.LatestReleaseRoot + Protocol.ChannelManifestName,
                Protocol.ChannelManifestName,
                16 * 1024,
                cancellationToken).ConfigureAwait(false);
            var signature = Encoding.ASCII.GetString(await Protocol.DownloadSmallAsync(
                _client,
                Protocol.LatestReleaseRoot + Protocol.ChannelSignatureName,
                Protocol.ChannelSignatureName,
                8 * 1024,
                cancellationToken).ConfigureAwait(false));
            var manifest = Protocol.VerifyAndParse(manifestBytes, signature);
            if (manifest.Version <= currentVersion)
            {
                return new ReleaseUpdateResult(ReleaseUpdateState.Current, "The installed version is current", manifest.Version);
            }

            var targetPath = ResolveUpdateTarget();

            updateRoot = Path.Combine(
                UpdatesRoot(),
                $"v{manifest.Version.Major}.{manifest.Version.Minor}.{manifest.Version.Build}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(updateRoot);
            var updaterPath = Path.Combine(updateRoot, manifest.UpdaterAsset);
            var releaseRoot = Protocol.VersionReleaseRoot(manifest.Version);
            await Protocol.DownloadVerifiedFileAsync(
                _client,
                releaseRoot + manifest.UpdaterAsset,
                manifest.UpdaterAsset,
                updaterPath,
                manifest.UpdaterSize,
                manifest.UpdaterSha256,
                Protocol.MaximumUpdaterBytes,
                cancellationToken).ConfigureAwait(false);
            VerifyPortableExecutable(updaterPath);

            var processId = Process.GetCurrentProcess().Id;
            var readyEventName = "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N");
            var arguments = string.Join(" ", new[]
            {
                "--apply",
                "--parent-pid",
                processId.ToString(CultureInfo.InvariantCulture),
                "--target",
                QuoteArgument(targetPath),
                "--version",
                $"{manifest.Version.Major}.{manifest.Version.Minor}.{manifest.Version.Build}",
                "--ready-event",
                readyEventName,
                "--manifest",
                Protocol.ChannelManifestName,
            });
            using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = updateRoot,
                Arguments = arguments,
                UseShellExecute = false,
            });
            if (started == null)
            {
                throw new InvalidOperationException("The verified updater could not be started");
            }
            if (!readyEvent.WaitOne(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    started.Kill();
                }
                catch
                {
                    // The helper may have exited while the timeout was being handled.
                }
                throw new TimeoutException("The verified updater did not confirm the application identity");
            }

            updaterStarted = true;
            return new ReleaseUpdateResult(
                ReleaseUpdateState.UpdateStarted,
                $"Verified updater started for v{manifest.Version}",
                manifest.Version,
                updaterPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "Update check was cancelled");
        }
        catch (HttpRequestException)
        {
            return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "GitHub update metadata is unavailable");
        }
        catch (TaskCanceledException)
        {
            return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "GitHub update check timed out");
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is InvalidOperationException ||
            exception is TimeoutException ||
            exception is Win32Exception)
        {
            return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "Update could not be started: " + exception.Message);
        }
        catch (Exception exception) when (exception is InvalidDataException || exception is CryptographicException || exception is FormatException)
        {
            return new ReleaseUpdateResult(ReleaseUpdateState.Rejected, "Update rejected: " + exception.Message);
        }
        finally
        {
            if (!updaterStarted && updateRoot != null)
            {
                TryDeleteDirectory(updateRoot);
            }
        }
    }

    public static void BeginCompletedUpdateCleanup(string[] commandLineArgs)
    {
        if (commandLineArgs.Length != 4 ||
            !string.Equals(commandLineArgs[1], "--finish-update", StringComparison.Ordinal) ||
            !int.TryParse(commandLineArgs[2], NumberStyles.None, CultureInfo.InvariantCulture, out var updaterPid) ||
            updaterPid <= 0)
        {
            return;
        }

        string updateRoot;
        try
        {
            updateRoot = Path.GetFullPath(commandLineArgs[3]);
            VerifyCleanupRoot(updateRoot);
        }
        catch
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                using var updater = Process.GetProcessById(updaterPid);
                updater.WaitForExit(30000);
            }
            catch (ArgumentException)
            {
                // The updater already exited.
            }
            catch (InvalidOperationException)
            {
                // The updater already exited.
            }

            TryDeleteDirectory(updateRoot);
        });
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static string UpdatesRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Updates");
    }

    private static void VerifyCleanupRoot(string path)
    {
        var allowed = Path.GetFullPath(UpdatesRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate, allowed, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Updater cleanup path is unsafe");
        }
    }

    private static void VerifyPortableExecutable(string path)
    {
        using var input = File.OpenRead(path);
        if (input.ReadByte() != 'M' || input.ReadByte() != 'Z')
        {
            throw new InvalidDataException("Signed updater is not a Windows executable");
        }
    }

    private static string ResolveUpdateTarget()
    {
        var launcherPath = Environment.GetEnvironmentVariable(LauncherPathEnvironmentVariable);
        var targetPath = string.IsNullOrWhiteSpace(launcherPath)
            ? Assembly.GetExecutingAssembly().Location
            : launcherPath;
        targetPath = Path.GetFullPath(targetPath);
        if (!string.Equals(Path.GetFileName(targetPath), TargetExecutableName, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(targetPath))
        {
            throw new InvalidDataException("The update target is invalid");
        }

        return targetPath;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            VerifyCleanupRoot(path);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
            // A later application start can remove a stale bounded update directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure must not affect the installed application.
        }
        catch (InvalidDataException)
        {
            // Never delete a path outside the dedicated update root.
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return argument;
        }

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
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
}
