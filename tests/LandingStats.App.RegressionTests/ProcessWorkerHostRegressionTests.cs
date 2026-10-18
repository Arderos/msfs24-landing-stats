using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ProcessWorkerHostRegressionTests
{
    private static readonly string Executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe");
    public static void RunAll() => Task.Run(RunAsync).GetAwaiter().GetResult();
    private static RuntimeClockEpoch Clock() => new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private static WorkerIdentity Worker(ComponentRole role) => new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, role);
    private static async Task RunAsync()
    {
        foreach (ComponentRole role in Enum.GetValues(typeof(ComponentRole)))
        {
            ProcessWorkerHost? host = null;
            using (host = new ProcessWorkerHost(Executable, Worker(role), Clock(), (message, _) => host!.AcknowledgeAsync(message.Sequence)))
            {
                await host.StartAsync(TimeSpan.FromSeconds(5), "normal");
                await Until(() => host.LastHealth?.UsefulProgress > 0, "Real worker produced no useful progress.");
                using var probeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var result = await host.CommandAsync(new WorkerCommandMessage(WorkerCommand.Probe), probeDeadline.Token);
                Check(result.Outcome == CommandOutcome.Completed, "Owner did not observe its command.");
                using (var process = Process.GetProcessById(host.ProcessId))
                    Check(!process.Modules.Cast<ProcessModule>().Any(module => string.Equals(module.ModuleName, "SimConnect.dll", StringComparison.OrdinalIgnoreCase)),
                        "Bootstrap fixture loaded another role's native SDK.");
                await host.StopAsync(TimeSpan.FromSeconds(3));
                Check(await host.Exit == 0 && host.LastAcknowledgedSequence == host.LastReceivedSequence, "Graceful stop lost its source prefix or did not exit.");
            }
        }
        await CrashAndHang();
        await IndependentControlDuringDataBackpressure();
        await DescendantsAndParentCrash();
        Check(OwnedProcess.Quote("a b\\") == "\"a b\\\\\"" && OwnedProcess.Quote("a\"b") == "\"a\\\"b\"", "Windows argument quoting changed its boundaries.");
    }
    private static async Task CrashAndHang()
    {
        ProcessWorkerHost? crash = null;
        using (crash = new ProcessWorkerHost(Executable, Worker(ComponentRole.Controllers), Clock(), (message, _) => crash!.AcknowledgeAsync(message.Sequence)))
        {
            await crash.StartAsync(TimeSpan.FromSeconds(5), "crash");
            await Until(() => crash.Exit.IsCompleted, "Faulted worker did not exit.");
            Check(await crash.Exit == 73, "Worker fault exit was hidden.");
        }
        foreach (var mode in new[] { "hang", "owner-stall" })
        {
            ProcessWorkerHost? host = null;
            using (host = new ProcessWorkerHost(Executable, Worker(ComponentRole.Upload), Clock(), (message, _) => host!.AcknowledgeAsync(message.Sequence)))
            {
                await host.StartAsync(TimeSpan.FromSeconds(5), mode);
                if (mode == "owner-stall")
                {
                    await Until(() => host.LastHealth != null, "Fixture health did not arrive.");
                    var first = host.LastMessageQpc; var owner = host.LastHealth!.OwnerProgressQpc;
                    await Until(() => host.LastMessageQpc > first, "Independent heartbeat did not advance.");
                    Check(host.LastHealth!.OwnerProgress == 0 && host.LastHealth.OwnerProgressQpc == owner,
                        "A transport heartbeat falsely advanced owner health.");
                }
                using var probe = new CancellationTokenSource(100);
                try { await host.CommandAsync(new WorkerCommandMessage(WorkerCommand.Probe), probe.Token); throw new InvalidOperationException("A stalled owner acknowledged a probe."); }
                catch (OperationCanceledException) { }
                await host.StopAsync(TimeSpan.FromMilliseconds(100));
                Check(host.Exit.IsCompleted && await host.Exit != 0, "Noncooperative owner was replaced before confirmed termination.");
            }
        }
    }
    private static async Task IndependentControlDuringDataBackpressure()
    {
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessWorkerHost? host = null;
        using (host = new ProcessWorkerHost(Executable, Worker(ComponentRole.Simulator), Clock(), async (message, _) =>
        {
            if (message.Payload is TelemetryFrameMessage) { blocked.TrySetResult(true); await release.Task.ConfigureAwait(false); }
            await host!.AcknowledgeAsync(message.Sequence).ConfigureAwait(false);
        }))
        {
            try
            {
                await host.StartAsync(TimeSpan.FromSeconds(5), "source-flood");
                await Until(() => blocked.Task.IsCompleted, "Source did not enter the blocked journal fixture.");
                await Task.Delay(1000); // Fill the data pipe while the independent owner/control loop advances.
                var before = host.LastHealth?.UsefulProgress ?? 0;
                using var probe = new CancellationTokenSource(500);
                var response = await host.CommandAsync(new WorkerCommandMessage(WorkerCommand.Probe), probe.Token);
                Check(response.Outcome == CommandOutcome.Completed && host.LastHealth?.UsefulProgress >= before,
                    "Data backpressure blocked the independent owner probe.");
                release.TrySetResult(true);
                await host.StopAsync(TimeSpan.FromSeconds(3));
                Check(await host.Exit == 0 && host.LastAcknowledgedSequence == host.LastReceivedSequence,
                    "Healthy source did not drain the retained prefix after storage recovered.");
            }
            finally { release.TrySetResult(true); }
        }
    }
    private static async Task DescendantsAndParentCrash()
    {
        ProcessWorkerHost? host = null;
        using (host = new ProcessWorkerHost(Executable, Worker(ComponentRole.Controllers), Clock(), (message, _) => host!.AcknowledgeAsync(message.Sequence)))
        {
            await host.StartAsync(TimeSpan.FromSeconds(5), "descendant");
            using var deadline = new CancellationTokenSource(2000);
            var response = await host.CommandAsync(new WorkerCommandMessage(WorkerCommand.Probe), deadline.Token);
            Check(response.Reason.StartsWith("child:", StringComparison.Ordinal), "Descendant fixture did not start.");
            using var child = Process.GetProcessById(int.Parse(response.Reason.Substring(6), CultureInfo.InvariantCulture));
            await host.StopAsync(TimeSpan.FromSeconds(3));
            Check(child.HasExited, "Root exit left a worker descendant running.");
        }
        var ready = Path.Combine(Path.GetTempPath(), "job-ready-Юникод " + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            // Keep this outer job alive while the nested owner dies. Its own job
            // must kill the child, including the create-before-resume boundary.
            using var parent = OwnedProcess.Start(Assembly.GetExecutingAssembly().Location, new[] { "--job-parent", ready });
            await Until(() => parent.Exit.IsCompleted, "Crash fixture parent did not exit.");
            Check(await parent.Exit == 55 && File.Exists(ready), "Crash fixture did not reach its suspended-child boundary.");
            var id = int.Parse(File.ReadAllText(ready), CultureInfo.InvariantCulture);
            try
            {
                using var child = Process.GetProcessById(id);
                await Until(() => child.HasExited, "Parent death left a suspended worker orphan.");
            }
            catch (ArgumentException) { /* The kernel has already removed the terminated child. */ }
        }
        finally { if (File.Exists(ready)) File.Delete(ready); }
    }
    public static int RunJobParent(string ready)
    {
        ready = Path.GetFullPath(ready);
        if (!ready.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(ready).StartsWith("job-ready-", StringComparison.Ordinal)) return 2;
        _ = OwnedProcess.Start(Executable, new[] { "--fixture-child" }, beforeResume: id =>
        {
            using (var file = new FileStream(ready, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file)) { writer.Write(id.ToString(CultureInfo.InvariantCulture)); writer.Flush(); file.Flush(true); }
            Environment.Exit(55); // Deliberately skips Dispose; the kernel owns orphan cleanup.
        });
        return 3;
    }
    private static async Task Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(message); await Task.Delay(10); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
