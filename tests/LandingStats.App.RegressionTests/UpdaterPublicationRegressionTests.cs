using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using LandingStats.App;
using UpdaterProgram = LandingStats.App.Updater.Program;

namespace LandingStats.App.RegressionTests;

internal static class UpdaterPublicationRegressionTests
{
    public static void RunAll()
    {
        RejectBeforeTouchingTarget(invalidBundle: true);
        RejectBeforeTouchingTarget(invalidBundle: false);
        LockedTargetPreservesOriginal();
        InstallWithOpenReader();
        AccessPreflightAndStaleArtifacts();
        CompletedUpdateCleanupSurvivesSecondaryLaunch();
        FailedUpdateReleasesInstallBeforeReporting();
        ProbeCleanupAttemptsBothFiles();
    }

    private static void FailedUpdateReleasesInstallBeforeReporting()
    {
        var protocol = typeof(UpdaterProgram).Assembly.GetType("LandingStats.UpdateProtocol.ReleaseUpdateProtocol")!;
        var enter = protocol.GetMethod("EnterInstallTransaction", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var target = current.MainModule!.FileName;
        var reported = false;
        var result = UpdaterProgram.Run(new[] { "--apply", "--parent-pid", current.Id.ToString(), "--target", target,
            "--version", "1.2.3", "--ready-event", "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N") }, error =>
        {
            reported = true;
            // The real updater error path reaches presentation while this test
            // callback stands in for its modal dialog. A launcher must already
            // be able to enter installation admission on another thread.
            var admission = System.Threading.Tasks.Task.Run(() =>
            { using var lease = (IDisposable)enter.Invoke(null, new object[] { target })!; });
            Check(admission.Wait(3000), "Failure presentation retained install ownership and blocked application restart.");
        });
        Check(result == 1 && reported, "Updater failure did not reach its presentation boundary.");
    }

    private static void ProbeCleanupAttemptsBothFiles()
    {
        using var fixture = new Fixture();
        var directory = Path.GetDirectoryName(fixture.Target)!;
        var first = Path.Combine(directory, "probe-first");
        var second = first + ".replace";
        File.WriteAllText(first, "locked probe"); File.WriteAllText(second, "second probe");
        var cleanup = typeof(UpdaterProgram).Assembly.GetType("LandingStats.UpdateProtocol.ReleaseUpdateProtocol")!
            .GetMethod("DeleteInstallProbes", BindingFlags.Static | BindingFlags.NonPublic)!;
        using (File.OpenRead(first))
        {
            try { cleanup.Invoke(null, new object[] { first, second }); throw new Exception("Locked probe deletion succeeded."); }
            catch (TargetInvocationException error) when (error.InnerException is IOException) { }
            Check(!File.Exists(second), "A failed first probe deletion skipped the replacement probe.");
        }
    }

    private static void CompletedUpdateCleanupSurvivesSecondaryLaunch()
    {
        var protocol = typeof(UpdaterProgram).Assembly.GetType("LandingStats.UpdateProtocol.ReleaseUpdateProtocol")!;
        var complete = protocol.GetMethod("CompleteUpdateCleanup", BindingFlags.Static | BindingFlags.NonPublic)!;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats", "Updates", "cleanup-regression-" + Guid.NewGuid().ToString("N"));
        using var fixture = new Fixture();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "completed.txt"), "completed update");
            using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/c exit 0", UseShellExecute = false, CreateNoWindow = true,
            })!;
            exited.WaitForExit();
            var pid = exited.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            complete.Invoke(null, new object[] { new[] { "--finish-update", pid, Path.GetDirectoryName(fixture.Target)! } });
            fixture.AssertOriginal();
            // The launcher performs this synchronously before single-instance
            // activation, so completion needs neither a new App nor a UI pump.
            complete.Invoke(null, new object[] { new[] { "--finish-update", pid, root } });
            Check(!Directory.Exists(root), "Secondary-launch cleanup left the completed update directory.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void RejectBeforeTouchingTarget(bool invalidBundle)
    {
        using var fixture = new Fixture();
        if (invalidBundle) File.WriteAllText(fixture.Replacement, "invalid package");
        // A target opened without delete sharing makes any premature move fail.
        // Validation must reject the candidate without attempting publication.
        using var original = File.OpenRead(fixture.Target);
        try
        {
            UpdaterProgram.InstallExecutableTransactionally(fixture.Replacement, fixture.Target,
                invalidBundle ? fixture.Version : new Version(fixture.Version.Major + 1, 0, 0));
            throw new InvalidOperationException("Invalid replacement was accepted.");
        }
        catch (InvalidDataException) { }
        fixture.AssertOriginal();
        Check(File.Exists(fixture.Replacement), "Rejected download was removed.");
        fixture.AssertNoTransactionFiles();
    }

    private static void LockedTargetPreservesOriginal()
    {
        using var fixture = new Fixture();
        File.SetAttributes(fixture.Target, FileAttributes.ReadOnly);
        using (var original = File.OpenRead(fixture.Target))
        {
            try
            {
                UpdaterProgram.InstallExecutableTransactionally(fixture.Replacement, fixture.Target, fixture.Version);
                throw new InvalidOperationException("A target without delete sharing was replaced.");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            fixture.AssertOriginal();
            Check((File.GetAttributes(fixture.Target) & FileAttributes.ReadOnly) != 0,
                "Failed publication did not restore the original read-only attribute.");
            Check(File.Exists(fixture.Replacement), "Failed publication lost the validated download.");
            fixture.AssertNoTransactionFiles();
        }
    }

    private static void InstallWithOpenReader()
    {
        using var fixture = new Fixture();
        var expected = File.ReadAllBytes(fixture.Replacement);
        using var original = new FileStream(fixture.Target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        UpdaterProgram.InstallExecutableTransactionally(fixture.Replacement, fixture.Target, fixture.Version);
        Check(File.ReadAllBytes(fixture.Target).SequenceEqual(expected), "Installed file differs from the verified package.");
        using var reader = new StreamReader(original, Encoding.UTF8, false, 1024, leaveOpen: true);
        Check(reader.ReadToEnd() == Fixture.Original, "Existing reader did not retain the old file.");
        Check(!File.Exists(fixture.Replacement), "Committed download was not collected.");
        fixture.AssertNoTransactionFiles();
    }

    private static void AccessPreflightAndStaleArtifacts()
    {
        using var fixture = new Fixture();
        var protocol = typeof(UpdaterProgram).Assembly.GetType("LandingStats.UpdateProtocol.ReleaseUpdateProtocol")!;
        var enter = protocol.GetMethod("EnterInstallTransaction", BindingFlags.Static | BindingFlags.NonPublic)!;
        System.Threading.Tasks.Task waiting;
        using ((IDisposable)enter.Invoke(null, new object[] { fixture.Target })!)
        {
            waiting = System.Threading.Tasks.Task.Run(() =>
            {
                using var lease = (IDisposable)enter.Invoke(null, new object[] { Path.Combine(Path.GetDirectoryName(fixture.Target)!, "another portable copy.exe") })!;
            });
            Check(!waiting.Wait(100), "Two installations sharing staging filenames admitted concurrent publication/cleanup.");
        }
        Check(waiting.Wait(3000), "Install admission was not released.");
        var access = protocol.GetMethod("VerifyInstallAccess", BindingFlags.Static | BindingFlags.NonPublic)!;
        access.Invoke(null, new object[] { fixture.Target });
        fixture.AssertOriginal(); fixture.AssertNoTransactionFiles();
        using (File.OpenRead(fixture.Target))
        {
            try { access.Invoke(null, new object[] { fixture.Target }); throw new Exception("Preflight ignored a non-replaceable installed file."); }
            catch (TargetInvocationException error) when (error.InnerException is System.ComponentModel.Win32Exception) { }
        }
        fixture.AssertOriginal();
        var directory = Path.GetDirectoryName(fixture.Target)!;
        var pending = Path.Combine(directory, ".msfs-landing-stats-pending-" + Guid.NewGuid().ToString("N") + ".exe");
        var backup = pending.Replace("-pending-", "-backup-");
        var unrelated = Path.Combine(directory, ".msfs-landing-stats-backup-user-copy.exe");
        File.WriteAllText(pending, "interrupted copy"); File.WriteAllText(backup, "old image"); File.WriteAllText(unrelated, "user file");
        var collect = protocol.GetMethod("CollectInstallArtifacts", BindingFlags.Static | BindingFlags.NonPublic)!;
        using (File.OpenRead(backup))
        {
            collect.Invoke(null, new object[] { fixture.Target });
            Check(!File.Exists(pending) && File.Exists(backup), "Cleanup lost a locked backup or ignored a completed staging orphan.");
        }
        collect.Invoke(null, new object[] { fixture.Target });
        Check(!File.Exists(backup) && File.Exists(unrelated), "Later cleanup did not collect the released backup or removed an unrelated file.");
        fixture.AssertOriginal();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Original = "known-good installed executable";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "landing-stats-publication-" + Guid.NewGuid().ToString("N"));
        // Test output and Temp may be on different volumes, as in normal updates.
        private readonly string _downloads = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-fixture-" + Guid.NewGuid().ToString("N"));
        public string Target { get; }
        public string Replacement { get; }
        public Version Version { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_downloads);
            Target = Path.Combine(_root, "downloaded application (3).exe");
            Replacement = Path.Combine(_downloads, "replacement.exe");
            File.WriteAllText(Target, Original);
            File.Copy(typeof(App).Assembly.Location, Replacement);
            Version = AssemblyName.GetAssemblyName(Replacement).Version!;
            using var payload = new MemoryStream();
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Create, true))
            using (var entry = archive.CreateEntry("fixture.txt").Open()) entry.WriteByte(1);
            using var output = new FileStream(Replacement, FileMode.Append, FileAccess.Write);
            payload.Position = 0;
            payload.CopyTo(output);
            var length = BitConverter.GetBytes(payload.Length);
            output.Write(length, 0, length.Length);
            var marker = Encoding.ASCII.GetBytes("MSFSLSABUNDLE1");
            output.Write(marker, 0, marker.Length);
        }

        public void AssertOriginal() => Check(File.ReadAllText(Target) == Original, "Failed update changed the installed executable.");
        public void AssertNoTransactionFiles() => Check(!Directory.EnumerateFiles(_root, ".msfs-landing-stats-*.exe").Any(), "Transaction staging or backup was left behind.");
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_root)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
            Directory.Delete(_downloads, true);
        }
    }
}
