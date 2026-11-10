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
            InstanceTakeover();
            SessionNotifications();
            await StopDuringSessionConfirmation(Path.Combine(root, "session-owner-stop"));
            await GuardianOnlySessionEnd(Path.Combine(root, "guardian-only-session"));
            await CancelledSessionThenAppOnlyStop(Path.Combine(root, "cancelled-then-app-only"));
            await CancelledSessionRestartsDrainedApp(Path.Combine(root, "cancelled-drained-app"));
            await CancelledSessionDuringDrain(Path.Combine(root, "cancelled-active-drain"));
            await SessionStopBeforeHello(Path.Combine(root, "stop-before-hello"));
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
            await Scenario(Path.Combine(root, "session-cancel"), "session-end", 2, cancelledSession: true);
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
            instance!.StartActivation(() => { activated.Set(); return Task.FromResult(true); }, () => Volatile.Read(ref ready));
            var duplicate = Task.Run(() => GuardianInstance.TryAcquire(out _, name));
            Check(!activated.Wait(200), "Activation ran before the application handshake was ready.");
            Volatile.Write(ref ready, true);
            Check(activated.Wait(3000) && !duplicate.GetAwaiter().GetResult(), "Secondary launch did not activate the one resident owner.");
        }
        Check(GuardianInstance.TryAcquire(out var replacement, name), "A stopped guardian retained single-instance ownership.");
        replacement!.Dispose();
    }
    private static void InstanceTakeover()
    {
        var name = "Local\\LS-guardian-fixture-" + Guid.NewGuid().ToString("N");
        Check(GuardianInstance.TryAcquire(out var instance, name), "Initial owner failed.");
        var secondary = Task.Run(() =>
        {
            var acquired = GuardianInstance.TryAcquire(out var replacement, name);
            replacement?.Dispose(); return acquired;
        });
        instance!.StartActivation(() => Task.FromResult(false), () => false);
        Check(!secondary.Wait(150), "A secondary launch disappeared while the application was retiring.");
        instance.Dispose();
        Check(secondary.Wait(3000) && secondary.Result, "Secondary launch failed to acquire the retired owner.");
    }
    private static void SessionNotifications()
    {
        using var observer = new GuardianSessionEnd(() => Task.CompletedTask);
        Check(observer.ConfirmAsync().IsCompleted && observer.ConfirmAsync().Result,
            "An App-only session stop waited for a guardian query that never happened.");
        Check(SendMessage(observer.Handle, 0x0011, IntPtr.Zero, IntPtr.Zero) == new IntPtr(1), "Guardian vetoed session end.");
        Check(!observer.ConfirmAsync().IsCompleted, "A shutdown query committed session end.");
        SendMessage(observer.Handle, 0x0016, IntPtr.Zero, IntPtr.Zero);
        Check(!observer.ConfirmAsync().GetAwaiter().GetResult(), "Cancelled Windows shutdown stopped the guardian.");
        observer.BeginApplication();
        Check(observer.ConfirmAsync().IsCompleted && observer.ConfirmAsync().Result,
            "An old cancelled shutdown was reused by a later App generation.");
        SendMessage(observer.Handle, 0x0011, IntPtr.Zero, IntPtr.Zero);
        SendMessage(observer.Handle, 0x0016, new IntPtr(1), IntPtr.Zero);
        Check(observer.ConfirmAsync().GetAwaiter().GetResult(), "Confirmed Windows shutdown was not observed.");
        observer.BeginApplication();
        // Restart Manager's CLOSEAPP flag requests the same terminal stop. It
        // is not a cancellation, nor permission for guardian-driven recovery.
        SendMessage(observer.Handle, 0x0011, IntPtr.Zero, new IntPtr(1));
        var restartManager = observer.ConfirmAsync();
        Check(!restartManager.IsCompleted, "Restart Manager's query was treated as final confirmation.");
        SendMessage(observer.Handle, 0x0016, new IntPtr(1), new IntPtr(1));
        Check(restartManager.GetAwaiter().GetResult(), "Restart Manager closure triggered App recovery.");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static async Task StopDuringSessionConfirmation(string root)
    {
        Directory.CreateDirectory(root);
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new GuardianSupervisor(generation => new GuardianGeneration(Guid.NewGuid(), generation));
        supervisor.ConfirmSessionEnd = () => { waiting.TrySetResult(true); return decision.Task; };
        var run = supervisor.RunAsync(Assembly.GetExecutingAssembly().Location, new[] { "--guardian-fixture", "session-end", root },
            AppDomain.CurrentDomain.BaseDirectory, visible: false);
        try
        {
            Check(await Task.WhenAny(waiting.Task, Task.Delay(10000)) == waiting.Task, "Guardian never reached session confirmation.");
            supervisor.Stop();
            Check(await Task.WhenAny(run, Task.Delay(3000)) == run, "Explicit guardian stop remained blocked on Windows confirmation.");
            await run;
        }
        finally { supervisor.Stop(); decision.TrySetResult(true); await run; }
    }

    private static async Task GuardianOnlySessionEnd(string root)
    {
        Directory.CreateDirectory(root); var generations = 0; Task? running = null;
        var supervisor = new GuardianSupervisor(generation =>
        { generations++; return new GuardianGeneration(Guid.NewGuid(), generation); });
        using var observer = new GuardianSessionEnd(() => running, supervisor.RequestSessionEndAsync, () => supervisor.ApplicationStoppingForSession);
        supervisor.ApplicationStarting = observer.BeginApplication; supervisor.ConfirmSessionEnd = observer.ConfirmAsync;
        running = supervisor.RunAsync(Assembly.GetExecutingAssembly().Location, new[] { "--guardian-fixture", "guardian-session", root },
            AppDomain.CurrentDomain.BaseDirectory, visible: false);
        try
        {
            await GuardianUpdateRegressionTests.Until(() => supervisor.Current?.Health?.Ready == true, "Guardian-only fixture did not become ready.");
            SendMessage(observer.Handle, 0x0011, IntPtr.Zero, new IntPtr(1));
            Check(supervisor.Current!.StopIntent == GuardianStopReason.None, "A guardian query prematurely stopped App.");
            SendMessage(observer.Handle, 0x0016, IntPtr.Zero, new IntPtr(1));
            Check(supervisor.Current.StopIntent == GuardianStopReason.None && !running.IsCompleted,
                "Cancelled guardian-only shutdown drained or replaced the live application.");
            SendMessage(observer.Handle, 0x0011, IntPtr.Zero, new IntPtr(1));
            SendMessage(observer.Handle, 0x0016, new IntPtr(1), new IntPtr(1));
            Check(running.IsCompleted && generations == 1 && File.Exists(Path.Combine(root, "drain-complete")),
                "Confirmed guardian-only shutdown did not drain its App through authenticated control before returning.");
            await running;
        }
        finally { supervisor.Stop(); await running; }
    }

    private static async Task CancelledSessionThenAppOnlyStop(string root)
    {
        Directory.CreateDirectory(root); var generations = 0; Task? running = null;
        var supervisor = new GuardianSupervisor(generation =>
        { generations++; return new GuardianGeneration(Guid.NewGuid(), generation); });
        using var observer = new GuardianSessionEnd(() => running, supervisor.RequestSessionEndAsync, () => supervisor.ApplicationStoppingForSession);
        supervisor.ApplicationStarting = observer.BeginApplication; supervisor.ConfirmSessionEnd = observer.ConfirmAsync;
        running = supervisor.RunAsync(Assembly.GetExecutingAssembly().Location, new[] { "--guardian-fixture", "guardian-session", root },
            AppDomain.CurrentDomain.BaseDirectory, visible: false);
        try
        {
            await GuardianUpdateRegressionTests.Until(() => supervisor.Current?.Health?.Ready == true, "App-only stop fixture did not become ready.");
            SendMessage(observer.Handle, 0x0011, IntPtr.Zero, IntPtr.Zero);
            SendMessage(observer.Handle, 0x0016, IntPtr.Zero, IntPtr.Zero);
            // A prior system query was cancelled before querying App. A later
            // App-only CLOSEAPP must not consume that old cancellation.
            await supervisor.RequestSessionEndAsync();
            Check(await Task.WhenAny(running, Task.Delay(5000)) == running && generations == 1,
                "An old cancelled query restarted App during its later terminal stop.");
            await running;
        }
        finally { supervisor.Stop(); await running; }
    }

    private static async Task CancelledSessionRestartsDrainedApp(string root)
    {
        Directory.CreateDirectory(root); var generations = 0; Task? running = null;
        var supervisor = new GuardianSupervisor(generation =>
        { Interlocked.Increment(ref generations); return new GuardianGeneration(Guid.NewGuid(), generation); });
        using var observer = new GuardianSessionEnd(() => running, supervisor.RequestSessionEndAsync, () => supervisor.ApplicationStoppingForSession);
        supervisor.ApplicationStarting = observer.BeginApplication; supervisor.ConfirmSessionEnd = observer.ConfirmAsync;
        running = supervisor.RunAsync(Assembly.GetExecutingAssembly().Location, new[] { "--guardian-fixture", "guardian-session", root },
            AppDomain.CurrentDomain.BaseDirectory, visible: false);
        try
        {
            await GuardianUpdateRegressionTests.Until(() => supervisor.Current?.Health?.Ready == true, "Cancelled drain fixture did not become ready.");
            SendMessage(observer.Handle, 0x0011, IntPtr.Zero, IntPtr.Zero);
            await supervisor.RequestSessionEndAsync();
            await GuardianUpdateRegressionTests.Until(() => supervisor.Current == null, "Drained App did not retire before session decision.");
            Check(!running.IsCompleted, "Unconfirmed Windows session end released the guardian.");
            SendMessage(observer.Handle, 0x0016, IntPtr.Zero, IntPtr.Zero);
            await GuardianUpdateRegressionTests.Until(() => Volatile.Read(ref generations) == 2 && supervisor.Current?.Health?.Ready == true,
                "A cancelled shutdown did not restore the App that already drained.");
            await supervisor.RequestSessionEndAsync();
            Check(await Task.WhenAny(running, Task.Delay(5000)) == running && generations == 2,
                "A replacement App reused the previous generation's cancellation.");
            await running;
        }
        finally { supervisor.Stop(); await running; }
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

    private static async Task CancelledSessionDuringDrain(string root)
    {
        Directory.CreateDirectory(root); var generations = 0; Task? running = null;
        var supervisor = new GuardianSupervisor(generation =>
        { Interlocked.Increment(ref generations); return new GuardianGeneration(Guid.NewGuid(), generation); });
        using var observer = new GuardianSessionEnd(() => running, supervisor.RequestSessionEndAsync, () => supervisor.ApplicationStoppingForSession);
        supervisor.ApplicationStarting = observer.BeginApplication; supervisor.ConfirmSessionEnd = observer.ConfirmAsync;
        running = supervisor.RunAsync(Assembly.GetExecutingAssembly().Location, new[] { "--guardian-fixture", "guardian-session-drain", root },
            AppDomain.CurrentDomain.BaseDirectory, visible: false);
        try
        {
            await GuardianUpdateRegressionTests.Until(() => supervisor.Current?.Health?.Ready == true, "Active-drain fixture did not become ready.");
            SendMessage(observer.Handle, 0x0011, IntPtr.Zero, IntPtr.Zero);
            await supervisor.RequestSessionEndAsync();
            await GuardianUpdateRegressionTests.Until(() => File.Exists(Path.Combine(root, "draining-1")), "App did not reach the explicit drain barrier.");
            Check(supervisor.Current != null && !supervisor.Current.Exit.IsCompleted && supervisor.ApplicationStoppingForSession,
                "Active-drain cancellation was not delivered while the App was alive and stopping.");
            SendMessage(observer.Handle, 0x0016, IntPtr.Zero, IntPtr.Zero);
            Check(observer.ConfirmAsync().IsCompleted && !await observer.ConfirmAsync(), "Cancellation during App drain was discarded.");
            File.WriteAllText(Path.Combine(root, "release-1"), "yes");
            await GuardianUpdateRegressionTests.Until(() => Volatile.Read(ref generations) == 2 && supervisor.Current?.Health?.Ready == true,
                "Cancellation during active drain did not restore exactly one App.");
            File.WriteAllText(Path.Combine(root, "release-2"), "yes");
            await supervisor.RequestSessionEndAsync();
            Check(await Task.WhenAny(running, Task.Delay(10000)) == running, "Replacement App did not stop after its App-only session request.");
            await running;
            Check(generations == 2, "Active-drain cancellation was reused after App replacement.");
            Console.WriteLine("Native ENDSESSION(FALSE) during active drain: exactly one replacement; subsequent App-only stop terminal.");
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, "release-1"), "cleanup"); File.WriteAllText(Path.Combine(root, "release-2"), "cleanup");
            supervisor.Stop(); await running;
        }
    }

    private static async Task SessionStopBeforeHello(string root)
    {
        Directory.CreateDirectory(root);
        using var child = new GuardianGeneration(Guid.NewGuid(), 1);
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>(); child.Diagnostic += diagnostics.Enqueue;
        var starting = child.StartAsync(Assembly.GetExecutingAssembly().Location, new[] { "--guardian-fixture", "session-before-hello", root },
            AppDomain.CurrentDomain.BaseDirectory, null, TimeSpan.FromSeconds(15), visible: false);
        Exception? failure = null;
        try
        {
            await GuardianUpdateRegressionTests.Until(() => File.Exists(Path.Combine(root, "before-hello")), "App did not connect before its Hello barrier.");
            await child.RequestSessionEndAsync();
            Check(child.StopIntent == GuardianStopReason.SessionEnd, "Pre-Hello session intent was not retained immediately.");
            File.WriteAllText(Path.Combine(root, "release-hello"), "yes");
            Check(await Task.WhenAny(starting, Task.Delay(10000)) == starting, "Queued session stop blocked authenticated startup."); await starting;
            await child.RequestSessionEndAsync(); // Repeated notifications reuse the accepted delivery.
            Check(await Task.WhenAny(child.Exit, Task.Delay(10000)) == child.Exit, "Authenticated session-stop child did not exit.");
            Check(await child.Exit == 0 && File.Exists(Path.Combine(root, "handshake-first")),
                "Session stop displaced or corrupted the first authenticated ACK: " +
                (File.Exists(Path.Combine(root, "failure")) ? File.ReadAllText(Path.Combine(root, "failure")) : "exit=" + await child.Exit));
            Check(diagnostics.IsEmpty, "Queued session stop reported delivery errors: " + string.Join(";", diagnostics));
            Console.WriteLine("Connected pre-Hello session stop: ACK sequence 1 followed by Stop sequence 2.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            File.WriteAllText(Path.Combine(root, "release-hello"), "cleanup");
            await child.RetireAsync(TimeSpan.FromSeconds(3));
            try { await starting; } catch when (failure != null) { }
        }
    }

    private static async Task<int> SessionBeforeHelloFixture(GuardianDescriptor descriptor, string root)
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", descriptor.Pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            using var wire = new GuardianWire(pipe, descriptor);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(10000, timeout.Token); GuardianPipes.VerifyServer(pipe, descriptor.Parent);
            File.WriteAllText(Path.Combine(root, "before-hello"), "connected");
            await GuardianUpdateRegressionTests.Until(() => File.Exists(Path.Combine(root, "release-hello")), "Hello barrier was not released.");
            await wire.SendAsync(new GuardianPacket(GuardianMessageKind.Hello, 1, descriptor.Token), timeout.Token);
            var hello = await wire.ReadAsync(timeout.Token);
            Check(hello.Kind == GuardianMessageKind.Ack && hello.Request == 1 && hello.Accepted, "Session stop arrived before Hello acknowledgement.");
            var stop = await wire.ReadAsync(timeout.Token);
            Check(stop.Kind == GuardianMessageKind.Stop && stop.Stop == GuardianStopReason.SessionEnd, "Queued session stop was not delivered after ACK.");
            File.WriteAllText(Path.Combine(root, "handshake-first"), "ACK sequence 1; Stop sequence 2");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(root, "failure"), error.ToString()); return 1; }
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
    private static async Task Scenario(string root, string mode, int expectedGenerations, bool cancelledSession = false)
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
        using var sessionObserver = mode == "session-end" && !cancelledSession ? new GuardianSessionEnd(() => Task.CompletedTask) : null;
        if (sessionObserver != null)
        { supervisor.ConfirmSessionEnd = sessionObserver.ConfirmAsync; supervisor.ApplicationStarting = sessionObserver.BeginApplication; }
        if (cancelledSession) { var decisions = 0; supervisor.ConfirmSessionEnd = () => Task.FromResult(++decisions > 1); }
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
        if (mode == "session-before-hello") return await SessionBeforeHelloFixture(descriptor, root);
        // An exclusive local owner is independent evidence that the parent's
        // exit/retirement handshake never overlaps two application generations.
        using var ownership = new FileStream(Path.Combine(root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var started = Stopwatch.GetTimestamp(); var watch = Stopwatch.StartNew();
        long shutdownProgress = 0;
        var requestedSessionEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        using var client = new GuardianClient(descriptor, Health, () => { File.WriteAllText(Path.Combine(root, "activated"), "yes"); return Task.FromResult(true); }, () =>
        { File.WriteAllText(Path.Combine(root, "prepared"), "drained"); return Task.FromResult(true); }, () =>
        { requestedSessionEnd.TrySetResult(true); return Task.CompletedTask; });
        await client.ConnectAsync();
        if (mode == "guardian-session" || mode == "guardian-session-drain") await requestedSessionEnd.Task;
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
        var reason = mode == "sim-exit" ? GuardianStopReason.SimulatorQuit : mode == "session-end" || mode == "guardian-session" || mode == "guardian-session-drain" ? GuardianStopReason.SessionEnd :
            mode == "update" ? GuardianStopReason.Update : GuardianStopReason.UserExit;
        var stopping = client.StopAsync(reason, CancellationToken.None);
        if (mode == "stop-pipe") ((GuardianWire)typeof(GuardianClient).GetField("_wire", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!).Dispose();
        Check(await stopping, "Fixture stop intent was rejected.");
        File.WriteAllText(Path.Combine(root, "stop-acked"), "yes");
        if (mode == "guardian-session") File.WriteAllText(Path.Combine(root, "drain-complete"), "accepted work persisted");
        if (mode == "guardian-session-drain")
        {
            File.WriteAllText(Path.Combine(root, "draining-" + descriptor.Generation), "acknowledged");
            await GuardianUpdateRegressionTests.Until(() => File.Exists(Path.Combine(root, "release-" + descriptor.Generation)), "App drain barrier was not released.");
        }
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
