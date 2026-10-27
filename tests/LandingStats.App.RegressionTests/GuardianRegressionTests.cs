using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;

namespace LandingStats.App.RegressionTests;

internal static class GuardianRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "gg-" + Guid.NewGuid().ToString("N").Substring(0, 8)); var done = false;
        try
        {
            await Protocol();
            InstanceActivation();
            await StopDuringPreparation();
            await PersistentStartupFailure(Path.Combine(root, "startup-failure"));
            await ParentDeath(Path.Combine(root, "parent-death"));
            await GuardianUpdateRegressionTests.Run(Path.Combine(root, "handoff"));
            await Scenario(Path.Combine(root, "crash"), "crash", 3);
            await Scenario(Path.Combine(root, "control"), "control", 2);
            await Scenario(Path.Combine(root, "ui"), "ui", 2);
            await Scenario(Path.Combine(root, "stop"), "stop-hang", 1);
            await Scenario(Path.Combine(root, "stop-progress"), "stop-progress", 1);
            await Scenario(Path.Combine(root, "stop-pipe"), "stop-pipe", 1);
            await Scenario(Path.Combine(root, "hello"), "wrong-hello", 2);
            await Scenario(Path.Combine(root, "invalid-packet"), "invalid-packet", 2);
            await Scenario(Path.Combine(root, "update-rejected"), "update-rejected", 1);
            await Scenario(Path.Combine(root, "prepare"), "prepare-fail", 1);
            await Scenario(Path.Combine(root, "sim-exit"), "sim-exit", 1);
            await Scenario(Path.Combine(root, "session-end"), "session-end", 1);
            await Scenario(Path.Combine(root, "update"), "update", 1);
            await Scenario(Path.Combine(root, "update-cancel"), "update-cancel", 1);
            GC.Collect(); GC.WaitForPendingFinalizers();
            using var process = Process.GetCurrentProcess(); process.Refresh(); var handles = process.HandleCount; var memory = process.PrivateMemorySize64;
            await Scenario(Path.Combine(root, "cycles"), "cycles", 101);
            GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh();
            Check(process.HandleCount <= handles + 24 && process.PrivateMemorySize64 <= memory + 64 * 1024 * 1024,
                "One hundred application replacements retained unbounded process/control resources.");
            done = true;
        }
        finally { if (done && Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
    private static void InstanceActivation()
    {
        var name = "Local\\LS-guardian-fixture-" + Guid.NewGuid().ToString("N");
        Check(GuardianInstance.TryAcquire(out var instance, name), "First guardian did not own its instance.");
        using (instance!)
        using (var activated = new ManualResetEventSlim())
        {
            var ready = false;
            instance!.StartActivation(() => { activated.Set(); return Task.CompletedTask; }, () => Volatile.Read(ref ready));
            var duplicate = Task.Run(() => GuardianInstance.TryAcquire(out _, name)).GetAwaiter().GetResult();
            Check(!activated.Wait(200), "Activation ran before the application handshake was ready.");
            Volatile.Write(ref ready, true);
            Check(!duplicate && activated.Wait(3000), "Secondary launch did not activate the one resident owner.");
        }
        Check(GuardianInstance.TryAcquire(out var replacement, name), "A stopped guardian retained single-instance ownership.");
        replacement!.Dispose();
    }
    private static async Task PersistentStartupFailure(string root)
    {
        Directory.CreateDirectory(root); var attempts = 0;
        var supervisor = new GuardianSupervisor(generation => { attempts++; return new GuardianGeneration(Guid.NewGuid(), generation); },
            new GuardianPolicy { Startup = TimeSpan.FromSeconds(20), InitialBackoff = TimeSpan.Zero, MaximumBackoff = TimeSpan.Zero });
        var elapsed = Stopwatch.StartNew();
        try
        {
            await supervisor.RunAsync(Assembly.GetExecutingAssembly().Location, new[] { "--guardian-fixture", "startup-fail", root }, AppDomain.CurrentDomain.BaseDirectory, visible: false);
            throw new InvalidOperationException("Persistent startup failure was silently retried forever.");
        }
        catch (IOException) { Check(attempts == 3 && elapsed.Elapsed < TimeSpan.FromSeconds(10), "Early process exits waited for handshake deadlines or bypassed the startup attempt cap."); }
        finally { supervisor.Stop(); }
    }
    private static async Task StopDuringPreparation()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        GuardianGeneration? child = null;
        var supervisor = new GuardianSupervisor(generation =>
        { entered.Set(); release.Wait(); return child = new GuardianGeneration(Guid.NewGuid(), generation); });
        var running = Task.Run(() => supervisor.RunAsync(Assembly.GetExecutingAssembly().Location, Array.Empty<string>(), AppDomain.CurrentDomain.BaseDirectory, visible: false));
        try { Check(entered.Wait(3000), "Guardian did not enter preparation."); supervisor.Stop(); }
        finally { release.Set(); }
        await running;
        Check(child != null && child.ProcessId == 0, "Shutdown during preparation admitted a new application process.");
    }
    private static async Task ParentDeath(string root)
    {
        Directory.CreateDirectory(root);
        using var parent = Process.Start(new ProcessStartInfo
        {
            FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--guardian-parent " + OwnedProcess.Quote(root),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        })!;
        try
        {
            await GuardianUpdateRegressionTests.Until(() => File.Exists(Path.Combine(root, "orphan-grandchild")), "Guardian's descendant fixture did not start.");
            var child = File.ReadAllText(Path.Combine(root, "orphan-child"));
            var grandchild = File.ReadAllText(Path.Combine(root, "orphan-grandchild"));
            parent.Kill(); parent.WaitForExit();
            await GuardianUpdateRegressionTests.Until(() => GuardianUpdateRegressionTests.Exited(child) && GuardianUpdateRegressionTests.Exited(grandchild),
                "Losing the guardian left an orphan application/helper tree.");
        }
        finally { if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(); } }
    }
    internal static int Parent(string root)
    {
        var session = Guid.NewGuid(); var guardian = new GuardianSupervisor(generation => new GuardianGeneration(session, generation));
        guardian.RunAsync(Assembly.GetExecutingAssembly().Location, new[] { "--guardian-fixture", "orphan", root }, AppDomain.CurrentDomain.BaseDirectory, visible: false).GetAwaiter().GetResult();
        return 0;
    }
    private static async Task Protocol()
    {
        var descriptor = GuardianDescriptor.Create(Guid.NewGuid(), 3);
        Check(GuardianDescriptor.Parse(descriptor.Encode()).Token == descriptor.Token, "Guardian bootstrap identity changed.");
        var packets = new[] { new GuardianPacket(GuardianMessageKind.Hello, 1, descriptor.Token),
            new GuardianPacket(GuardianMessageKind.Status, status: new GuardianStatus(true, 3, 4, false, GuardianRecovery.Presentation, "presentation")),
            new GuardianPacket(GuardianMessageKind.Stop, 2, stop: GuardianStopReason.UserExit),
            new GuardianPacket(GuardianMessageKind.Ack, 2), new GuardianPacket(GuardianMessageKind.Activate),
            new GuardianPacket(GuardianMessageKind.PrepareRecovery), new GuardianPacket(GuardianMessageKind.RecoveryReady),
            new GuardianPacket(GuardianMessageKind.Update, 3, update: new GuardianUpdateRequest(@"C:\fixture\updater.exe", @"C:\fixture\app.exe", "1.2.3",
                "Local\\fixture", "update-current.json", 32, new string('a', 64))) };
        using var bytes = new MemoryStream(); using var writer = new GuardianWire(bytes, descriptor);
        foreach (var packet in packets) await writer.SendAsync(packet, CancellationToken.None);
        var serialized = bytes.ToArray(); bytes.Position = 0;
        using var reader = new GuardianWire(bytes, descriptor);
        foreach (var expected in packets) { var actual = await reader.ReadAsync(CancellationToken.None); Check(actual.Kind == expected.Kind && actual.Request == expected.Request, "Guardian control contract changed."); }
        using var staleBytes = new MemoryStream(serialized); using var stale = new GuardianWire(staleBytes, GuardianDescriptor.Create(descriptor.Session, 4));
        try { await stale.ReadAsync(CancellationToken.None); throw new InvalidOperationException("An earlier application generation was accepted."); } catch (InvalidDataException) { }
        using var hugeBytes = new MemoryStream(BitConverter.GetBytes(int.MaxValue)); using var huge = new GuardianWire(hugeBytes, descriptor);
        try { await huge.ReadAsync(CancellationToken.None); throw new InvalidOperationException("An unbounded guardian packet was allocated."); } catch (InvalidDataException) { }
        var policy = new GuardianPolicy(); var now = Stopwatch.GetTimestamp();
        var live = new GuardianStatus(true, now, 1, false);
        Check(policy.Decide(now, now, live, GuardianStopReason.None, false, false) == GuardianDecision.WaitForSafePoint, "UI-only stall interrupted a healthy flight.");
        Check(policy.Decide(now, now, new GuardianStatus(true, 1, now, false), GuardianStopReason.None, false, false) == GuardianDecision.Restart, "Live UI concealed a dead runtime.");
        Check(policy.Decide(now, now, live, GuardianStopReason.SessionEnd, true, true) == GuardianDecision.Stop, "Shutdown intent lost precedence over failure.");
        Check(policy.Backoff(1000000) == policy.MaximumBackoff, "Repeated application failures disabled recovery or exceeded the backoff bound.");
    }
    private static async Task Scenario(string root, string mode, int expectedGenerations)
    {
        Directory.CreateDirectory(root); var session = Guid.NewGuid(); var generations = new List<GuardianGeneration>(); var overlap = false;
        var states = new List<string>();
        var policy = new GuardianPolicy { Startup = TimeSpan.FromSeconds(8), ControlStall = TimeSpan.FromMilliseconds(600), UiStall = TimeSpan.FromMilliseconds(500),
            GracefulRecovery = TimeSpan.FromMilliseconds(900), ShutdownIdle = TimeSpan.FromMilliseconds(900), CheckInterval = TimeSpan.FromMilliseconds(25), InitialBackoff = TimeSpan.FromMilliseconds(10), MaximumBackoff = TimeSpan.FromMilliseconds(25) };
        var supervisor = new GuardianSupervisor(generation =>
        {
            if (mode == "prepare-fail" && generation <= 2) throw new IOException("Temporary runtime-cache repair failure.");
            if (generations.Count > 0 && !generations.Last().Exit.IsCompleted) overlap = true;
            var next = new GuardianGeneration(session, generation, (_, token) => mode == "update-rejected"
                ? Task.FromException<GuardianUpdateLease>(new IOException("Updater rejected fixture.", new InvalidOperationException("Original handoff cause.")))
                : GuardianUpdateRegressionTests.Prepare(root,
                mode == "update-cancel" ? "no-ready" : "normal", token)); generations.Add(next); return next;
        }, policy);
        supervisor.Changed += (generation, state) => { lock (states) states.Add(generation + ":" + state); };
        var run = supervisor.RunAsync(Assembly.GetExecutingAssembly().Location, new[] { "--guardian-fixture", mode, root }, AppDomain.CurrentDomain.BaseDirectory, visible: false);
        try
        {
            if (await Task.WhenAny(run, Task.Delay(mode == "cycles" ? 120000 : 30000)) != run) throw new TimeoutException("Guardian fixture timed out: " + mode + " " + string.Join(";", states));
            await run;
            Check(generations.Count == expectedGenerations && !overlap, "Guardian replaced a live owner or missed recovery: " + mode + " " + string.Join(";", states));
            Check(File.Exists(Path.Combine(root, "stop-acked")), "Application shutdown was not acknowledged before exit.");
            Check(generations.All(generation => generation.Exit.IsCompleted), "Guardian returned with an owned application still alive.");
            if (mode == "prepare-fail") Check(states.Any(state => state.Contains("System.IO.IOException: Temporary runtime-cache repair failure.")), "Preparation error message was lost.");
            if (mode == "wrong-hello") Check(states.Any(state => state.Contains("System.IO.InvalidDataException: Application guardian handshake differs.")), "Startup exception message was lost.");
            if (mode == "invalid-packet") Check(states.Count(state => state.Contains("channel_failed error=")) == 1 &&
                states.Any(state => state.Contains("System.IO.InvalidDataException: Unexpected application control packet.")), "Guardian channel error was lost or duplicated: " + string.Join(";", states));
            if (mode == "update-rejected") Check(states.Any(state => state.Contains("update_handoff_rejected error=") &&
                state.Contains("Updater rejected fixture.") && state.Contains("Original handoff cause.")), "Rejected update lost its original exception.");
            if (mode == "ui") Check(File.Exists(Path.Combine(root, "prepared")) && File.Exists(Path.Combine(root, "recording-continued")), "UI-only recovery skipped the flight-safe boundary or its local drain.");
            if (mode == "stop-progress") Check(File.Exists(Path.Combine(root, "drain-complete")), "Guardian killed shutdown while durable drain was progressing.");
            if (mode == "update")
            {
                await GuardianUpdateRegressionTests.Until(() => File.Exists(Path.Combine(root, "committed")), "Guardian did not commit update.");
                Check(!GuardianUpdateRegressionTests.Exited(File.ReadAllText(Path.Combine(root, "started"))), "Updater inherited the retired App's job.");
            }
            if (mode == "update-cancel") Check(!File.Exists(Path.Combine(root, "committed")) && GuardianUpdateRegressionTests.Exited(File.ReadAllText(Path.Combine(root, "started"))), "Exit raced a late updater commit.");
            if (mode == "crash")
            {
                foreach (var file in Directory.GetFiles(root, "descendant-*"))
                {
                    Check(GuardianUpdateRegressionTests.Exited(File.ReadAllText(file)), "An application descendant survived confirmed retirement.");
                }
            }
        }
        finally
        {
            supervisor.Stop(); await run;
            if (mode == "update")
            {
                File.WriteAllText(Path.Combine(root, "finish"), "yes");
                await GuardianUpdateRegressionTests.Until(() => GuardianUpdateRegressionTests.Exited(File.ReadAllText(Path.Combine(root, "started"))), "Independent updater did not finish.");
            }
        }
    }
    internal static int Fixture(string mode, string root) => Task.Run(() => FixtureAsync(mode, root)).GetAwaiter().GetResult();
    private static async Task<int> FixtureAsync(string mode, string root)
    {
        var descriptor = GuardianDescriptor.Parse(Environment.GetEnvironmentVariable(GuardianDescriptor.EnvironmentName)!);
        Environment.SetEnvironmentVariable(GuardianDescriptor.EnvironmentName, null);
        // An exclusive local owner is independent evidence that the parent's
        // exit/retirement handshake never overlaps two application generations.
        using var ownership = new FileStream(Path.Combine(root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var started = Stopwatch.GetTimestamp(); var watch = Stopwatch.StartNew();
        long shutdownProgress = 0;
        if (mode == "startup-fail") return 77;
        if (mode == "wrong-hello" && descriptor.Generation == 1)
            descriptor = new GuardianDescriptor(descriptor.Pipe, descriptor.Session, descriptor.Generation, new string('f', 64), descriptor.Parent);
        GuardianStatus Health()
        {
            var now = Stopwatch.GetTimestamp(); var first = descriptor.Generation == 1;
            if (mode == "ui" && first && watch.ElapsedMilliseconds >= 1200) File.WriteAllText(Path.Combine(root, "recording-continued"), "durable fixture input");
            return new GuardianStatus(true, mode == "control" && first ? started : now, mode == "ui" && first ? started : now,
                mode != "ui" || !first || watch.ElapsedMilliseconds >= 1500, shutdownProgressQpc: Interlocked.Read(ref shutdownProgress));
        }
        using var client = new GuardianClient(descriptor, Health, () => File.WriteAllText(Path.Combine(root, "activated"), "yes"), () =>
        { File.WriteAllText(Path.Combine(root, "prepared"), "drained"); return Task.FromResult(true); });
        await client.ConnectAsync();
        if (mode == "invalid-packet" && descriptor.Generation == 1)
        {
            var wire = (GuardianWire)typeof(GuardianClient).GetField("_wire", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            await wire.SendAsync(new GuardianPacket(GuardianMessageKind.Activate, 2), CancellationToken.None);
            await Task.Delay(Timeout.Infinite);
        }
        if (mode == "update-rejected")
        {
            Check(!await client.UpdateAsync(new GuardianUpdateRequest(Assembly.GetExecutingAssembly().Location, Assembly.GetExecutingAssembly().Location,
                "1.2.3", "Local\\fixture", "update-channel.txt", 1, new string('a', 64)), CancellationToken.None), "Rejected update was accepted.");
        }
        if (mode == "orphan")
        {
            using var child = Process.Start(new ProcessStartInfo { FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"),
                Arguments = "--fixture-child", UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
            using var self = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(root, "orphan-child"), GuardianUpdateRegressionTests.Identity(self));
            File.WriteAllText(Path.Combine(root, "orphan-grandchild.tmp"), GuardianUpdateRegressionTests.Identity(child));
            File.Move(Path.Combine(root, "orphan-grandchild.tmp"), Path.Combine(root, "orphan-grandchild"));
            await Task.Delay(Timeout.Infinite);
        }
        if (mode == "cycles" && descriptor.Generation <= 100) Environment.Exit(77);
        if (mode == "crash" && descriptor.Generation < 3)
        {
            using var descendant = Process.Start(new ProcessStartInfo { FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"),
                Arguments = "--fixture-child", UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
            File.WriteAllText(Path.Combine(root, "descendant-" + descriptor.Generation), GuardianUpdateRegressionTests.Identity(descendant));
            Environment.Exit(77);
        }
        if (descriptor.Generation == 1 && (mode == "control" || mode == "ui")) await Task.Delay(Timeout.Infinite);
        await Task.Delay(300);
        if (mode == "update" || mode == "update-cancel")
        {
            var updating = client.UpdateAsync(new GuardianUpdateRequest(Assembly.GetExecutingAssembly().Location, Assembly.GetExecutingAssembly().Location,
                "1.2.3", "Local\\fixture", "update-channel.txt", 1, new string('a', 64)), CancellationToken.None);
            if (mode == "update-cancel")
            {
                await GuardianUpdateRegressionTests.Until(() => File.Exists(Path.Combine(root, "started")), "Cancelable updater was not prepared.");
                Check(await client.StopAsync(GuardianStopReason.UserExit, CancellationToken.None), "Exit during update was not acknowledged.");
                File.WriteAllText(Path.Combine(root, "stop-acked"), "yes");
                try { Check(!await updating, "Exit did not cancel update."); } catch (IOException) { } catch (OperationCanceledException) { }
                return 0;
            }
            Check(await updating, "Prepared update was not accepted.");
        }
        var reason = mode == "sim-exit" ? GuardianStopReason.SimulatorQuit : mode == "session-end" ? GuardianStopReason.SessionEnd :
            mode == "update" ? GuardianStopReason.Update : GuardianStopReason.UserExit;
        var stopping = client.StopAsync(reason, CancellationToken.None);
        if (mode == "stop-pipe") ((GuardianWire)typeof(GuardianClient).GetField("_wire", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!).Dispose();
        Check(await stopping, "Fixture stop intent was rejected.");
        File.WriteAllText(Path.Combine(root, "stop-acked"), "yes");
        if (mode == "stop-hang") await Task.Delay(Timeout.Infinite);
        if (mode == "stop-progress")
        {
            for (var i = 0; i < 8; i++) { Interlocked.Exchange(ref shutdownProgress, Stopwatch.GetTimestamp()); await Task.Delay(300); }
            File.WriteAllText(Path.Combine(root, "drain-complete"), "yes");
        }
        return 0;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
