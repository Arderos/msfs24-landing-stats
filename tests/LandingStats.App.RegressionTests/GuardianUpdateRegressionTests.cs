using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;

namespace LandingStats.App.RegressionTests;

internal static class GuardianUpdateRegressionTests
{
    public static async Task Run(string root)
    {
        Directory.CreateDirectory(root); ValidateBytes(root);
        var abandoned = Path.Combine(root, "abandoned"); Directory.CreateDirectory(abandoned);
        var lease = await Prepare(abandoned, "normal", CancellationToken.None);
        var id = lease.ProcessId;
        using var abandonedProcess = Process.GetProcessById(id); _ = abandonedProcess.Handle;
        await lease.RetireAsync();
        Check(!File.Exists(Path.Combine(abandoned, "committed")) && abandonedProcess.HasExited, "Prepared update ran without commit or survived cancellation.");

        var committed = Path.Combine(root, "committed"); Directory.CreateDirectory(committed);
        lease = await Prepare(committed, "normal", CancellationToken.None); id = lease.ProcessId;
        using var accepted = Process.GetProcessById(id); _ = accepted.Handle;
        try
        {
            lease.Commit(); await lease.RetireAsync();
            await Until(() => File.Exists(Path.Combine(committed, "committed")), "Accepted updater did not observe commit.");
            Check(!accepted.HasExited, "Handoff killed the independent updater.");
        }
        finally { File.WriteAllText(Path.Combine(committed, "finish"), "yes"); if (!accepted.WaitForExit(5000)) { accepted.Kill(); accepted.WaitForExit(); } }

        var canceled = Path.Combine(root, "canceled"); Directory.CreateDirectory(canceled);
        using var cancellation = new CancellationTokenSource(); var preparing = Prepare(canceled, "no-ready", cancellation.Token);
        await Until(() => File.Exists(Path.Combine(canceled, "started")), "Unready updater fixture did not start.");
        cancellation.Cancel();
        try { await preparing; throw new InvalidOperationException("Canceled update was accepted."); } catch (OperationCanceledException) { }
        Check(Exited(File.ReadAllText(Path.Combine(canceled, "started"))) && !File.Exists(Path.Combine(canceled, "committed")), "Canceled preparation returned with a live helper.");
        using var self = Process.GetCurrentProcess();
        Check(!Exited(Identity(self)), "A live exact process identity was treated as exited.");
        Check(Exited(self.Id + "|" + (self.StartTime.ToUniversalTime().Ticks - 1)), "PID reuse was treated as the original process.");
    }
    private static void ValidateBytes(string root)
    {
        var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MSFS-Landing-Stats.Updater.exe");
        var v = AssemblyName.GetAssemblyName(file).Version!; var version = $"{v.Major}.{v.Minor}.{v.Build}";
        var updates = Path.Combine(root, "Updates"); var directory = Path.Combine(updates, "v" + version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var copied = Path.Combine(directory, Path.GetFileName(file)); File.Copy(file, copied);
        using var sha = SHA256.Create(); string hash; using (var stream = File.OpenRead(copied)) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        var target = Assembly.GetExecutingAssembly().Location;
        GuardianUpdateRequest Request(string path, string destination, string fingerprint) => new GuardianUpdateRequest(path, destination, version,
            "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N"), "update-channel.txt", new FileInfo(copied).Length, fingerprint);
        using (GuardianUpdater.Verify(Request(copied, target, hash), target, updates)) { }
        Reject(() => { using var _ = GuardianUpdater.Verify(Request(copied, target + ".other.exe", hash), target, updates); });
        Reject(() => { using var _ = GuardianUpdater.Verify(Request(copied, target, new string('a', 64)), target, updates); });
        Reject(() => { using var _ = GuardianUpdater.Verify(Request(file, target, hash), target, updates); });
        File.WriteAllBytes(copied, new byte[] { 1, 2, 3 });
        Reject(() => { using var _ = GuardianUpdater.Verify(new GuardianUpdateRequest(copied, target, version,
            "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N"), "update-channel.txt", 20, hash), target, updates); });
    }
    internal static Task<GuardianUpdateLease> Prepare(string root, string mode, CancellationToken token)
    {
        var ready = "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N");
        var commit = "Local\\MSFSLandingStatsUpdateCommit-" + Guid.NewGuid().ToString("N");
        return GuardianUpdateLease.PrepareAsync(Assembly.GetExecutingAssembly().Location,
            new[] { "--guardian-update-helper", ready, commit, root, mode }, AppDomain.CurrentDomain.BaseDirectory, ready, commit, token);
    }
    internal static int Helper(string readyName, string commitName, string root, string mode)
    {
        using var ready = EventWaitHandle.OpenExisting(readyName); using var commit = EventWaitHandle.OpenExisting(commitName);
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(root, "started"), Identity(self));
        if (mode != "no-ready") ready.Set();
        if (!commit.WaitOne(20000)) return 42;
        File.WriteAllText(Path.Combine(root, "committed"), "yes");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "finish"))) { if (clock.Elapsed > TimeSpan.FromSeconds(30)) return 43; Thread.Sleep(20); }
        return 0;
    }
    internal static string Identity(Process process) => process.Id + "|" + process.StartTime.ToUniversalTime().Ticks;
    internal static bool Exited(string identity)
    {
        var fields = identity.Split('|');
        if (fields.Length != 2 || !int.TryParse(fields[0], out var id) || !long.TryParse(fields[1], out var started))
            throw new InvalidDataException("Invalid process fixture identity.");
        try { using var process = Process.GetProcessById(id); return process.HasExited || process.StartTime.ToUniversalTime().Ticks != started; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }
    internal static async Task Until(Func<bool> ready, string message)
    { var clock = Stopwatch.StartNew(); while (!ready()) { if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(message); await Task.Delay(10); } }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid update handoff accepted."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
