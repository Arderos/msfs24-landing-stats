using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.UpdateProtocol;

internal sealed class UpdateManifest
{
    public UpdateManifest(
        Version version,
        string packageAsset,
        long packageSize,
        string packageSha256,
        string updaterAsset,
        long updaterSize,
        string updaterSha256)
    {
        Version = version;
        PackageAsset = packageAsset;
        PackageSize = packageSize;
        PackageSha256 = packageSha256;
        UpdaterAsset = updaterAsset;
        UpdaterSize = updaterSize;
        UpdaterSha256 = updaterSha256;
    }

    public Version Version { get; }
    public string PackageAsset { get; }
    public long PackageSize { get; }
    public string PackageSha256 { get; }
    public string UpdaterAsset { get; }
    public long UpdaterSize { get; }
    public string UpdaterSha256 { get; }
}

internal static class ReleaseUpdateProtocol
{
    public const string LatestReleaseRoot = "https://github.com/Arderos/msfs24-landing-stats/releases/latest/download/";
    public const string ManifestName = "update-manifest.txt";
    public const string SignatureName = "update-manifest.sig";
    public const string ChannelManifestName = "update-channel.txt";
    public const string ChannelSignatureName = "update-channel.sig";
    public const string PackageAssetName = "MSFS-Landing-Stats.exe";
    public const string LegacyPackageAssetName = "MSFS-Landing-Stats.zip";
    public const string UpdaterAssetName = "MSFS-Landing-Stats.Updater.exe";
    public const long MaximumPackageBytes = 128L * 1024 * 1024;
    public const long MaximumUpdaterBytes = 16L * 1024 * 1024;

    private const string PublicModulus = "3UfZ8cUoPPA/C9ze+Yg2wPErrI/Cry1A12vhPXmebSaNqRPYHEDTiuWadXyHgFCIX/IZGEkMcCamVm6BSv8he+qI+98vU2NtgqKQ+P8YBxmirg7V/8RwbEi1AdcWWwmORZLHo8eOFuZMI9OOwdxhV+0tf89eo8VudLrxHtRjCQWHfB3d2VcoYpjdKse3btCfPxA4bmiVZYnC8M6lo5TqRXBIFjpmCC+oQpmehWodArLmZXT4vd9SaItN3Pfp1EWfLQxQerrmgpmHoySYSKw1yNPO6boelZ9aCWarhglvNlQsqMu5nLQpNCpkcs6jRbD/wY1s5BmmLNnljmNNgn78GxMl98CsVtr7tnmuk91MgQ87eLpfF4/EoEcvRXhlw/B4pjFPttc49M6LUJn5xJRLojq55GuYfgD3D0Bk+Snt2jyWOdIXpPkGt1YDBqdNgQghVje4+1kC8lRh/tgByXOWPjA5T8iJTpupNNjS4pEXo1HXKeVW0uIIoKUT0Dp+ko1N";
    private const string PublicExponent = "AQAB";

    public static string VersionReleaseRoot(Version version)
    {
        return $"https://github.com/Arderos/msfs24-landing-stats/releases/download/v{version.Major}.{version.Minor}.{version.Build}/";
    }

    public static UpdateManifest VerifyAndParse(byte[] manifestBytes, string signatureText)
    {
        if (!VerifyManifest(manifestBytes, signatureText))
        {
            throw new CryptographicException("GitHub update manifest signature is invalid");
        }

        return ParseManifest(manifestBytes);
    }

    public static async Task<byte[]> DownloadSmallAsync(
        HttpClient client,
        string url,
        string name,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        VerifyGitHubDownloadUri(response.RequestMessage?.RequestUri);
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

    public static async Task DownloadVerifiedFileAsync(
        HttpClient client,
        string url,
        string name,
        string destination,
        long expectedSize,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (expectedSize <= 0 || expectedSize > maximumBytes)
        {
            throw new InvalidDataException($"Signed size for {name} is invalid");
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidDataException($"Destination for {name} is invalid");
        }
        Directory.CreateDirectory(parent);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            VerifyGitHubDownloadUri(response.RequestMessage?.RequestUri);
            if (response.Content.Headers.ContentLength.HasValue &&
                response.Content.Headers.ContentLength.Value != expectedSize)
            {
                throw new InvalidDataException($"{name} Content-Length does not match the signed manifest");
            }

            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            using var sha256 = SHA256.Create();
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > expectedSize || total > maximumBytes)
                {
                    throw new InvalidDataException($"{name} exceeds the signed size");
                }

                sha256.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total != expectedSize || !FixedTimeEquals(ToHex(sha256.Hash!), expectedSha256))
            {
                throw new InvalidDataException($"{name} hash or size does not match the signed manifest");
            }
        }
        catch
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            throw;
        }
    }

    public static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return ToHex(sha256.ComputeHash(input));
    }

    public static bool FixedTimeEquals(string left, string right)
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

    private static bool VerifyManifest(byte[] manifest, string signatureText)
    {
        var signature = Convert.FromBase64String(signatureText.Trim());
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
        if (lines.Length != 8 || (lines[0] != "format=3" && lines[0] != "format=2"))
        {
            throw new InvalidDataException("Update manifest format is invalid");
        }

        var versionText = Value(lines[1], "version");
        var packageAsset = Value(lines[2], "package");
        var packageSizeText = Value(lines[3], "package-size");
        var packageSha256 = Value(lines[4], "package-sha256").ToLowerInvariant();
        var updaterAsset = Value(lines[5], "updater");
        var updaterSizeText = Value(lines[6], "updater-size");
        var updaterSha256 = Value(lines[7], "updater-sha256").ToLowerInvariant();

        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || version.Revision >= 0)
        {
            throw new InvalidDataException("Update version is invalid");
        }
        var expectedPackageAsset = lines[0] == "format=3" ? PackageAssetName : LegacyPackageAssetName;
        if (!string.Equals(packageAsset, expectedPackageAsset, StringComparison.Ordinal) ||
            !long.TryParse(packageSizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var packageSize) ||
            packageSize <= 0 || packageSize > MaximumPackageBytes ||
            !ValidSha256(packageSha256) ||
            !string.Equals(updaterAsset, UpdaterAssetName, StringComparison.Ordinal) ||
            !long.TryParse(updaterSizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var updaterSize) ||
            updaterSize <= 0 || updaterSize > MaximumUpdaterBytes ||
            !ValidSha256(updaterSha256))
        {
            throw new InvalidDataException("Update manifest values are invalid");
        }

        return new UpdateManifest(
            version,
            packageAsset,
            packageSize,
            packageSha256,
            updaterAsset,
            updaterSize,
            updaterSha256);
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

    private static bool ValidSha256(string value)
    {
        return value.Length == 64 && value.All(character => Uri.IsHexDigit(character));
    }

    private static void VerifyGitHubDownloadUri(Uri? uri)
    {
        if (uri == null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update download did not use HTTPS");
        }

        var host = uri.DnsSafeHost;
        if (!string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update download was redirected outside GitHub");
        }
    }

    private static string ToHex(IEnumerable<byte> bytes)
    {
        var result = new StringBuilder();
        foreach (var value in bytes)
        {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }
}
