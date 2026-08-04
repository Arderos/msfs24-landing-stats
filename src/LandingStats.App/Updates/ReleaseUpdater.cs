using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.App.Updates;

internal enum ReleaseUpdateState
{
    Current,
    Installed,
    AvailableButNotInstalled,
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
    public const string LauncherPathEnvironmentVariable = "MSFS_LANDING_STATS_LAUNCHER_PATH";

    private const string ReleaseRoot = "https://github.com/Arderos/msfs24-landing-stats/releases/latest/download/";
    private const string ManifestName = "update-manifest.txt";
    private const string SignatureName = "update-manifest.sig";
    private const string AssetName = "MSFS-Landing-Stats.exe";
    private const long MaximumAssetBytes = 128L * 1024 * 1024;

    private const string PublicModulus = "3UfZ8cUoPPA/C9ze+Yg2wPErrI/Cry1A12vhPXmebSaNqRPYHEDTiuWadXyHgFCIX/IZGEkMcCamVm6BSv8he+qI+98vU2NtgqKQ+P8YBxmirg7V/8RwbEi1AdcWWwmORZLHo8eOFuZMI9OOwdxhV+0tf89eo8VudLrxHtRjCQWHfB3d2VcoYpjdKse3btCfPxA4bmiVZYnC8M6lo5TqRXBIFjpmCC+oQpmehWodArLmZXT4vd9SaItN3Pfp1EWfLQxQerrmgpmHoySYSKw1yNPO6boelZ9aCWarhglvNlQsqMu5nLQpNCpkcs6jRbD/wY1s5BmmLNnljmNNgn78GxMl98CsVtr7tnmuk91MgQ87eLpfF4/EoEcvRXhlw/B4pjFPttc49M6LUJn5xJRLojq55GuYfgD3D0Bk+Snt2jyWOdIXpPkGt1YDBqdNgQghVje4+1kC8lRh/tgByXOWPjA5T8iJTpupNNjS4pEXo1HXKeVW0uIIoKUT0Dp+ko1N";
    private const string PublicExponent = "AQAB";

    private readonly HttpClient _client;

    public ReleaseUpdater(HttpMessageHandler? handler = null)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        _client = handler == null ? new HttpClient() : new HttpClient(handler, true);
        _client.Timeout = TimeSpan.FromSeconds(45);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("MSFS-Landing-Stats-Updater/1");
    }

    public async Task<ReleaseUpdateResult> CheckAndInstallAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            var manifestBytes = await DownloadSmallAsync(ManifestName, 16 * 1024, cancellationToken).ConfigureAwait(false);
            var signatureText = Encoding.ASCII.GetString(
                await DownloadSmallAsync(SignatureName, 8 * 1024, cancellationToken).ConfigureAwait(false)).Trim();
            if (!VerifyManifest(manifestBytes, signatureText))
            {
                return new ReleaseUpdateResult(ReleaseUpdateState.Rejected, "GitHub update manifest signature is invalid");
            }

            var manifest = ParseManifest(manifestBytes);
            if (manifest.Version <= currentVersion)
            {
                return new ReleaseUpdateResult(ReleaseUpdateState.Current, "The installed version is current", manifest.Version);
            }

            var stagedPath = await DownloadAndVerifyAssetAsync(manifest, cancellationToken).ConfigureAwait(false);
            if (!VerifyBundle(stagedPath))
            {
                File.Delete(stagedPath);
                return new ReleaseUpdateResult(ReleaseUpdateState.Rejected, "The signed update contains an invalid application bundle", manifest.Version);
            }

            var launcherPath = Environment.GetEnvironmentVariable(LauncherPathEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
            {
                return new ReleaseUpdateResult(
                    ReleaseUpdateState.AvailableButNotInstalled,
                    "A signed update was downloaded, but this process was not started by the single-file launcher",
                    manifest.Version,
                    stagedPath);
            }

            try
            {
                InstallAtomically(stagedPath, launcherPath!, manifest.Sha256);
                return new ReleaseUpdateResult(
                    ReleaseUpdateState.Installed,
                    $"v{manifest.Version} is installed and will be used next time the application starts",
                    manifest.Version,
                    launcherPath);
            }
            catch (UnauthorizedAccessException)
            {
                return new ReleaseUpdateResult(
                    ReleaseUpdateState.AvailableButNotInstalled,
                    "A signed update was downloaded, but the launcher directory is not writable",
                    manifest.Version,
                    stagedPath);
            }
            catch (IOException)
            {
                return new ReleaseUpdateResult(
                    ReleaseUpdateState.AvailableButNotInstalled,
                    "A signed update was downloaded, but the launcher could not be replaced",
                    manifest.Version,
                    stagedPath);
            }
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
        catch (Exception exception) when (exception is InvalidDataException || exception is CryptographicException || exception is FormatException)
        {
            return new ReleaseUpdateResult(ReleaseUpdateState.Rejected, "Update rejected: " + exception.Message);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private async Task<byte[]> DownloadSmallAsync(string name, int maximumBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseRoot + name);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException($"{name} exceeds the size limit");
        }

        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"{name} exceeds the size limit");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<string> DownloadAndVerifyAssetAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Updates");
        Directory.CreateDirectory(updateRoot);
        var path = Path.Combine(updateRoot, $"MSFS-Landing-Stats-{manifest.Version}-{Guid.NewGuid():N}.download.exe");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseRoot + AssetName);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != manifest.Size)
            {
                throw new InvalidDataException("Update Content-Length does not match the signed manifest");
            }

            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            using var sha256 = SHA256.Create();
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > manifest.Size || total > MaximumAssetBytes)
                {
                    throw new InvalidDataException("Update download exceeds the signed size");
                }

                sha256.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total != manifest.Size || !FixedTimeEquals(ToHex(sha256.Hash!), manifest.Sha256))
            {
                throw new InvalidDataException("Update hash or size does not match the signed manifest");
            }

            return path;
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            throw;
        }
    }

    private static bool VerifyManifest(byte[] manifest, string signatureText)
    {
        var signature = Convert.FromBase64String(signatureText);
        using var rsa = new RSACryptoServiceProvider();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromBase64String(PublicModulus),
            Exponent = Convert.FromBase64String(PublicExponent),
        });
        return rsa.VerifyData(manifest, CryptoConfig.MapNameToOID("SHA256"), signature);
    }

    private static UpdateManifest ParseManifest(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        if (lines.Length != 5 || lines[0] != "format=1")
        {
            throw new InvalidDataException("Update manifest format is invalid");
        }

        var versionText = Value(lines[1], "version");
        var asset = Value(lines[2], "asset");
        var sizeText = Value(lines[3], "size");
        var sha256 = Value(lines[4], "sha256").ToLowerInvariant();
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || version.Revision >= 0)
        {
            throw new InvalidDataException("Update version is invalid");
        }
        if (!string.Equals(asset, AssetName, StringComparison.Ordinal) ||
            !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size <= 0 || size > MaximumAssetBytes ||
            sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Update manifest values are invalid");
        }

        return new UpdateManifest(version, size, sha256);
    }

    private static string Value(string line, string key)
    {
        var prefix = key + "=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length == prefix.Length)
        {
            throw new InvalidDataException($"Update manifest is missing {key}");
        }
        return line.Substring(prefix.Length);
    }

    private static bool VerifyBundle(string path)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "--verify-bundle",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        if (process == null || !process.WaitForExit(30000))
        {
            try
            {
                process?.Kill();
            }
            catch
            {
                // The timeout is already a failed verification.
            }
            return false;
        }
        return process.ExitCode == 0;
    }

    private static void InstallAtomically(string stagedPath, string launcherPath, string expectedSha256)
    {
        var target = Path.GetFullPath(launcherPath);
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Launcher directory is unavailable");
        var incoming = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.update");
        var backup = target + ".previous";
        try
        {
            File.Copy(stagedPath, incoming, false);
            if (!FixedTimeEquals(HashFile(incoming), expectedSha256))
            {
                throw new InvalidDataException("Copied update failed its hash check");
            }
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
            File.Replace(incoming, target, backup, true);
            try
            {
                File.Delete(stagedPath);
            }
            catch (IOException)
            {
                // The launcher was already replaced atomically. A stale download is harmless.
            }
        }
        finally
        {
            if (File.Exists(incoming))
            {
                File.Delete(incoming);
            }
        }
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return ToHex(sha256.ComputeHash(input));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        var difference = 0;
        for (var index = 0; index < left.Length; index++)
        {
            difference |= left[index] ^ right[index];
        }
        return difference == 0;
    }

    private static string ToHex(byte[] bytes)
    {
        var result = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private sealed class UpdateManifest
    {
        public UpdateManifest(Version version, long size, string sha256)
        {
            Version = version;
            Size = size;
            Sha256 = sha256;
        }

        public Version Version { get; }
        public long Size { get; }
        public string Sha256 { get; }
    }
}
