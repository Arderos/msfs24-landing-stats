#if GUARDIAN_TEST_HOST
extern alias ApplicationHost;
#endif
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
#if GUARDIAN_TEST_HOST
using Protocol = ApplicationHost::LandingStats.UpdateProtocol.ReleaseUpdateProtocol;
#else
using Protocol = LandingStats.UpdateProtocol.ReleaseUpdateProtocol;
#endif

namespace LandingStats.App.Runtime;

internal static class GuardianUpdater
{
    public static async Task<GuardianUpdateLease> PrepareAsync(GuardianUpdateRequest request, string launcher, string updatesRoot, CancellationToken token)
    {
        using var verified = Verify(request, launcher, updatesRoot);
        var commit = "Local\\MSFSLandingStatsUpdateCommit-" + Guid.NewGuid().ToString("N");
        return await GuardianUpdateLease.PrepareAsync(request.Executable, new[]
        {
            "--apply", "--parent-pid", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), "--target", launcher,
            "--version", request.Version, "--ready-event", request.ReadyEvent, "--manifest", request.Manifest, "--commit-event", commit,
        }, Path.GetDirectoryName(request.Executable)!, request.ReadyEvent, commit, token).ConfigureAwait(false);
    }
    internal static FileStream Verify(GuardianUpdateRequest request, string launcher, string updatesRoot)
    {
        if (!string.Equals(Path.GetFullPath(request.Target), Path.GetFullPath(launcher), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(request.Executable), Protocol.UpdaterAssetName, StringComparison.Ordinal) ||
            request.Size > Protocol.MaximumUpdaterBytes || request.Manifest != Protocol.ChannelManifestName ||
            !IsEvent(request.ReadyEvent, "Local\\MSFSLandingStatsUpdate-")) throw new InvalidDataException("Updater handoff target differs.");
        var version = Version.Parse(request.Version);
        if (version.Build < 0 || version.Revision >= 0) throw new InvalidDataException("Invalid updater version.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(request.Executable))!;
        var allowedRoot = Path.GetFullPath(updatesRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expectedPrefix = "v" + request.Version + "-"; var name = Path.GetFileName(directory);
        if (!string.Equals(Path.GetDirectoryName(directory), allowedRoot, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(expectedPrefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(expectedPrefix.Length), "N", out _))
            throw new InvalidDataException("Updater handoff is outside its prepared directory.");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(request.Executable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Updater handoff cannot use a redirected file.");
        var file = new FileStream(request.Executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var sha = SHA256.Create(); var hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
            if (file.Length != request.Size || !Protocol.FixedTimeEquals(hash, request.Hash)) throw new InvalidDataException("Prepared updater bytes changed.");
            var assembly = AssemblyName.GetAssemblyName(request.Executable); var actual = assembly.Version;
            if (assembly.Name != "MSFS-Landing-Stats.Updater" || actual == null || actual.Major != version.Major || actual.Minor != version.Minor || actual.Build != version.Build)
                throw new InvalidDataException("Prepared updater assembly differs.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }
    internal static bool IsEvent(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(value.Substring(prefix.Length), "N", out _);
}
