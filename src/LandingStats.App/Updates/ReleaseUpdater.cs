using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Settings;
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
    public ReleaseUpdateResult(
        ReleaseUpdateState state,
        string messageKey,
        Version? version = null,
        string? path = null,
        object[]? messageArguments = null)
    {
        State = state;
        MessageKey = messageKey;
        MessageArguments = messageArguments ?? Array.Empty<object>();
        Version = version;
        Path = path;
    }

    public ReleaseUpdateState State { get; }
    public string MessageKey { get; }
    public object[] MessageArguments { get; }
    public string Message => LocalizationManager.Format(MessageKey, MessageArguments);
    public Version? Version { get; }
    public string? Path { get; }
}

internal sealed class ReleaseUpdater : IDisposable
{
    private const string LauncherPathEnvironmentVariable = "MSFS_LANDING_STATS_LAUNCHER_PATH";

    private readonly HttpClient _client;
    private readonly string _updatesRoot;

    public ReleaseUpdater(HttpMessageHandler? handler = null, string? updatesRoot = null)
    {
        _updatesRoot = Path.GetFullPath(updatesRoot ?? UpdatesRoot());
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        _client = handler == null ? new HttpClient() : new HttpClient(handler, true);
        _client.Timeout = TimeSpan.FromSeconds(45);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("MSFS-Landing-Stats-Updater/3");
    }

    public async Task<ReleaseUpdateResult> CheckAndInstallAsync(Version currentVersion, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? waitForInstallAllowed = null, Func<bool>? canInstall = null, Action<string>? phaseChanged = null)
    {
        if (Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_SKIP_UPDATE_ONCE") == "1")
        {
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_SKIP_UPDATE_ONCE", null);
            return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "Update.MetadataUnavailable");
        }
        string? updateRoot = null;
        var updaterStarted = false;
        try
        {
            phaseChanged?.Invoke("update_metadata");
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
                return new ReleaseUpdateResult(
                    ReleaseUpdateState.Current,
                    "Update.Current",
                    version: manifest.Version);
            }

            var targetPath = ResolveUpdateTarget();

            updateRoot = Path.Combine(
                _updatesRoot,
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

            phaseChanged?.Invoke("update_prepared");
            var waitingNoticeSent = false;
            while (true)
            {
                if (waitForInstallAllowed != null)
                {
                    if (!waitingNoticeSent && (canInstall == null || !canInstall()))
                    { waitingNoticeSent = true; phaseChanged?.Invoke("update_wait_safe"); }
                    do
                    {
                        await waitForInstallAllowed(cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    while (canInstall != null && !canInstall());
                }
                else if (canInstall != null && !canInstall())
                    return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "Update.DeferredUnsafe");

                var readyEventName = "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N");
                // Keep this operation's prepared file until readiness or cancellation.
                // Revalidate and lock its bytes through the handoff, including the
                // direct launch path without a guardian.
                using var preparedUpdater = VerifyPreparedUpdater(updaterPath, manifest.UpdaterSize, manifest.UpdaterSha256);
                phaseChanged?.Invoke("update_handoff");
                cancellationToken.ThrowIfCancellationRequested();
                if (canInstall != null && !canInstall())
                {
                    if (waitForInstallAllowed == null)
                        return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "Update.DeferredUnsafe");
                    waitingNoticeSent = false;
                    continue;
                }
                // Capture the foreground owner at handoff, after downloads/readiness.
                // A background update must not activate the replacement application.
                var restartInBackground = !IsCurrentProcessForeground();
                var guardian = App.Guardian;
                if (guardian != null)
                {
                    var request = new Runtime.GuardianUpdateRequest(updaterPath, targetPath,
                        $"{manifest.Version.Major}.{manifest.Version.Minor}.{manifest.Version.Build}", readyEventName,
                        Protocol.ChannelManifestName, manifest.UpdaterSize, manifest.UpdaterSha256, restartInBackground: restartInBackground);
                    if (!await guardian.UpdateAsync(request, cancellationToken).ConfigureAwait(false))
                        throw new IOException("The application guardian rejected the verified updater handoff");
                }
                else
                {
                var processId = Process.GetCurrentProcess().Id;
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
                arguments += restartInBackground ? " --background" : " --foreground";
                using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
                using var started = Process.Start(new ProcessStartInfo
                {
                    FileName = updaterPath,
                    WorkingDirectory = updateRoot,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Normal,
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
                }

                updaterStarted = true;
                return new ReleaseUpdateResult(
                    ReleaseUpdateState.UpdateStarted,
                    "Update.StartedFormat",
                    version: manifest.Version,
                    path: updaterPath,
                    messageArguments: new object[] { manifest.Version });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "Update.Cancelled");
        }
        catch (HttpRequestException)
        {
            return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "Update.MetadataUnavailable");
        }
        catch (OperationCanceledException)
        {
            return new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "Update.Timeout");
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is InvalidOperationException ||
            exception is TimeoutException ||
            exception is Win32Exception)
        {
            return new ReleaseUpdateResult(
                ReleaseUpdateState.Unavailable,
                "Update.StartFailedFormat",
                messageArguments: new object[] { exception.Message });
        }
        catch (Exception exception) when (exception is InvalidDataException || exception is CryptographicException || exception is FormatException)
        {
            return new ReleaseUpdateResult(
                ReleaseUpdateState.Rejected,
                "Update.RejectedFormat",
                messageArguments: new object[] { exception.Message });
        }
        finally
        {
            if (!updaterStarted && updateRoot != null)
            {
                TryDeleteDirectory(updateRoot, _updatesRoot);
            }
        }
    }

    internal static bool IsCurrentProcessForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        using var current = Process.GetCurrentProcess();
        return processId == current.Id;
    }
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

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

    private static void VerifyCleanupRoot(string path, string? updatesRoot = null)
    {
        var allowed = Path.GetFullPath(updatesRoot ?? UpdatesRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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

    private static FileStream VerifyPreparedUpdater(string path, long expectedSize, string expectedHash)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Prepared updater cannot use a redirected file.");
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var sha = SHA256.Create();
            var hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            if (input.Length != expectedSize || !Protocol.FixedTimeEquals(hash, expectedHash))
                throw new InvalidDataException("Prepared updater bytes changed.");
            input.Position = 0;
            if (input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                throw new InvalidDataException("Signed updater is not a Windows executable.");
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    private static string ResolveUpdateTarget()
    {
        var launcherPath = Environment.GetEnvironmentVariable(LauncherPathEnvironmentVariable);
        var targetPath = string.IsNullOrWhiteSpace(launcherPath)
            ? Assembly.GetExecutingAssembly().Location
            : launcherPath;
        targetPath = Path.GetFullPath(targetPath);
        var fileName = Path.GetFileName(targetPath);
        if (fileName.Length <= ".exe".Length ||
            !string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(targetPath))
        {
            throw new InvalidDataException("The update target is invalid");
        }

        return targetPath;
    }

    private static void TryDeleteDirectory(string path, string? updatesRoot = null)
    {
        try
        {
            VerifyCleanupRoot(path, updatesRoot);
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
