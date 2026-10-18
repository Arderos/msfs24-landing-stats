using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;
using Microsoft.Win32.SafeHandles;

namespace LandingStats.App.RegressionTests;

internal static class AdversarialRuntimeRegressionTests
{
    private static string Executable => Assembly.GetExecutingAssembly().Location;
    public static void RunAll() => Task.Run(async () =>
    {
        foreach (var attack in new[] { "silent", "token", "pid", "sequence", "identity", "oversize" }) await WorkerAttack(attack);
        CommandPersistence(); await LostCommandOutcome(); await PartialControlWriteRetiresGeneration(); CadenceFallback(); await InvalidLaunchArguments(); await MissingActivationExpires();
    }).GetAwaiter().GetResult();

    private static async Task WorkerAttack(string attack)
    {
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
        var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        var received = 0;
        using var host = new ProcessWorkerHost(Executable, worker, clock, (_, __) => { Interlocked.Increment(ref received); return Task.CompletedTask; });
        if (attack == "silent" || attack == "token" || attack == "pid")
        {
            var elapsed = Stopwatch.StartNew();
            try { await host.StartAsync(TimeSpan.FromSeconds(1), "adversarial-" + attack); throw new InvalidOperationException("Invalid handshake accepted: " + attack); }
            catch (IOException error)
            {
                if (attack != "silent") Check(error.InnerException is InvalidDataException, "Wrong handshake failure: " + error);
            }
            Check(received == 0 && host.Exit.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(6), "Handshake deadline or pre-accept boundary failed.");
        }
        else
        {
            await host.StartAsync(TimeSpan.FromSeconds(5), "adversarial-" + attack);
            Check(await Task.WhenAny(host.Completion, Task.Delay(5000)) == host.Completion, "Malformed worker did not fail: " + attack);
            try { await host.Completion; throw new InvalidOperationException("Malformed worker frame accepted: " + attack); }
            catch (InvalidDataException) { }
            Check(received == 1, "Malformed input escaped into recording.");
            await host.StopAsync(TimeSpan.FromMilliseconds(100));
            Check(host.Exit.IsCompleted, "Malformed worker was not retired.");
        }
    }

    // A separate executable path tests the parent's actual pipe boundary. No
    // attack implementation or protocol bypass is shipped in the worker host.
    public static int Fixture(string[] args) => FixtureAsync(args).GetAwaiter().GetResult();
    private static async Task<int> FixtureAsync(string[] args)
    {
        var options = args.Where(a => a.StartsWith("--") && a.Contains("=")).ToDictionary(a => a.Substring(2, a.IndexOf('=') - 2), a => a.Substring(a.IndexOf('=') + 1));
        var mode = options["fixture"].Substring("adversarial-".Length);
        if (mode == "pid" && !args.Contains("--attack-child"))
        {
            using var child = OwnedProcess.Start(Executable, args.Concat(new[] { "--attack-child" }).ToArray());
            await Task.Delay(30000); return 0;
        }
        using var data = new NamedPipeClientStream(".", options["pipe"], PipeDirection.InOut, PipeOptions.Asynchronous);
        using var control = new NamedPipeClientStream(".", options["pipe"] + "-control", PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(data.ConnectAsync(5000), control.ConnectAsync(5000));
        if (mode == "silent" || mode == "pid") { await Task.Delay(30000); return 0; }
        var worker = new WorkerIdentity(Guid.ParseExact(options["session"], "N"), Guid.ParseExact(options["worker"], "N"), long.Parse(options["generation"]), (ComponentRole)byte.Parse(options["role"]));
        var hello = new WorkerHello(RuntimeBuildIdentity.Read(AppDomain.CurrentDomain.BaseDirectory), mode == "token" ? new string('0', 64) : options["token"],
            RuntimeCapabilities.Basic, Guid.ParseExact(options["clock"], "N"), Stopwatch.Frequency);
        var bytes = RuntimeWire.Encode(new RuntimeEnvelope(worker, 0, 1, Stopwatch.GetTimestamp(), hello));
        await data.WriteAsync(bytes, 0, bytes.Length); await control.WriteAsync(bytes, 0, bytes.Length);
        if (mode == "token") { await Task.Delay(30000); return 0; }
        while (true)
        {
            var message = await RuntimeWire.ReadAsync(control, CancellationToken.None);
            if (message?.Payload is WorkerCommandMessage command && command.Command == WorkerCommand.Start) break;
            if (message == null) return 1;
        }
        if (mode == "oversize") { bytes = bytes.Take(12).ToArray(); Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, bytes, 8, 4); }
        else bytes = RuntimeWire.Encode(new RuntimeEnvelope(mode == "identity" ? new WorkerIdentity(worker.AppSessionId, Guid.NewGuid(), 1, worker.Role) : worker,
            0, mode == "sequence" ? 99 : 2, Stopwatch.GetTimestamp(), new ControllerCatalogMessage(1, Array.Empty<ControllerDescriptor>())));
        await data.WriteAsync(bytes, 0, bytes.Length); await Task.Delay(30000); return 0;
    }

    private static DurableRuntimeInput Input(Guid id, int sequence) => new DurableRuntimeInput(new RuntimeInput(id, JournalRecordKind.RuntimeCommand,
        sequence, sequence, DateTime.UtcNow, new RecordingCommand(RecordingCommandKind.Tick).Encode()), new JournalCursor(1, sequence));
    private static RuntimeCommandDispatch[] Commands() => new[] { new RuntimeCommandDispatch(new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Simulator), new WorkerCommandMessage(WorkerCommand.Probe)) };
    private static void CommandPersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-command-boundaries-" + Guid.NewGuid().ToString("N"));
        try
        {
            var queue = new RuntimeCommandQueue(root, _ => null, _ => Task.CompletedTask); var commands = Commands();
            var first = Input(Guid.NewGuid(), 1); queue.Commit(first, commands);
            for (var i = 2; i <= 1024; i++) queue.Commit(Input(Guid.NewGuid(), i), commands);
            Expect<IOException>(() => queue.Commit(Input(Guid.NewGuid(), 1025), commands));
            queue.Commit(first, commands); // Capacity must not break accepted-input replay.
            var batchPath = Path.Combine(root, first.Input.Id.ToString("N") + ".json"); var original = File.ReadAllBytes(batchPath);
            var batch = DurableFile.ReadJson<RuntimeCommandBatch>(batchPath, 2097152); batch.Input = Guid.NewGuid(); DurableFile.WriteJson(batchPath, batch);
            Expect<InvalidDataException>(() => queue.Collect(new JournalCursor(2, 1024)));
            File.WriteAllBytes(batchPath, original);
            DurableFile.WriteJson(Path.Combine(root, first.Input.Id.ToString("N") + ".progress"), new RuntimeCommandProgress { Input = first.Input.Id, Completed = 2 });
            Expect<InvalidDataException>(() => queue.Collect(new JournalCursor(2, 1024)));
            Check(File.ReadAllBytes(batchPath).SequenceEqual(original), "Corruption checks modified accepted commands.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task LostCommandOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-command-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = Input(Guid.NewGuid(), 1); byte[]? first = null; var attempts = 0;
            var queue = new RuntimeCommandQueue(root, _ => null, outcome =>
            {
                attempts++; var encoded = outcome.Encode();
                if (attempts == 1) { first = encoded; throw new IOException("lost durable outcome reply"); }
                Check(encoded.SequenceEqual(first!), "Retry changed the logical command outcome identity."); return Task.CompletedTask;
            });
            queue.Commit(input, Commands());
            using (var stop = new CancellationTokenSource(5000))
            {
                var owner = new ManagedComponentContext(1, stop.Token);
                try { await queue.RunAsync(owner); throw new InvalidOperationException("Failure was swallowed."); }
                catch (IOException) { }
                finally { owner.Dispose(); }
            }
            using (var stop = new CancellationTokenSource(5000))
            {
                var owner = new ManagedComponentContext(2, stop.Token);
                try
                {
                    var run = queue.RunAsync(owner); await queue.DrainAsync(); stop.Cancel();
                    try { await run; } catch (OperationCanceledException) { }
                }
                finally { owner.Dispose(); }
            }
            queue.Collect(input.Cursor);
            Check(attempts == 2 && Directory.GetFiles(root).Length == 0, "Retry lost or duplicated a command effect.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void CadenceFallback()
    {
        using var stop = new CancellationTokenSource(); var calls = 0;
        using var cadence = new OwnerCadence(stop.Token, flags =>
        {
            calls++;
            return flags == 2 ? new SafeWaitHandle(IntPtr.Zero, true) : CreateWaitableTimerExW(IntPtr.Zero, null, flags, 0x00100002);
        }, () => 87);
        Check(calls == 2 && !cadence.HighResolution, "Older-Windows timer fallback was not taken.");
        cadence.Wait(TimeSpan.FromMilliseconds(1)); stop.Cancel(); var watch = Stopwatch.StartNew(); cadence.Wait(TimeSpan.FromMinutes(1));
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Fallback wait ignored cancellation.");
        Expect<Win32Exception>(() => new OwnerCadence(CancellationToken.None, _ => new SafeWaitHandle(IntPtr.Zero, true), () => 5));
    }
    private static async Task PartialControlWriteRetiresGeneration()
    {
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
        using var host = new ProcessWorkerHost(Executable, worker, new RuntimeClockEpoch(Guid.NewGuid(), 0, Stopwatch.Frequency), (_, __) => Task.CompletedTask);
        using var pipe = new PartialWrite();
        var write = (Task)typeof(ProcessWorkerHost).GetMethod("WriteFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host,
            new object[] { pipe, new RuntimeEnvelope(worker, 0, 1, Stopwatch.GetTimestamp(), new WorkerCommandMessage(WorkerCommand.Probe), Guid.NewGuid(), 1) })!;
        try { await write; throw new InvalidOperationException("Partial write succeeded."); } catch (IOException) { }
        Check(pipe.ToArray().Length == 7 && !pipe.CanWrite, "The fault did not split and close the transport.");
        try { await host.CommandAsync(new WorkerCommandMessage(WorkerCommand.Probe), CancellationToken.None); throw new InvalidOperationException("A torn transport generation accepted a new command."); }
        catch (OperationCanceledException) { }
    }
    private sealed class PartialWrite : MemoryStream
    {
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { base.Write(buffer, offset, Math.Min(7, count)); return Task.FromException(new IOException("Broken transport after a partial prefix.")); }
    }
    private static async Task InvalidLaunchArguments()
    {
        var worker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe");
        foreach (var arguments in new[] { Array.Empty<string>(), new[] { "--unknown=1" }, new[] { "--role=1", "--role=2" }, new[] { "--role=" }, new[] { "bad" } })
        {
            using var process = OwnedProcess.Start(worker, arguments);
            Check(await Task.WhenAny(process.Exit, Task.Delay(5000)) == process.Exit && await process.Exit == 70, "Invalid bootstrap options were accepted or hung.");
        }
    }
    private static async Task MissingActivationExpires()
    {
        var name = "ls-runtime-" + Guid.NewGuid().ToString("N");
        using var data = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var control = new NamedPipeServerStream(name + "-control", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connect = Task.WhenAll(data.WaitForConnectionAsync(), control.WaitForConnectionAsync());
        using var process = OwnedProcess.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), new[] {
            "--pipe=" + name, "--session=" + Guid.NewGuid().ToString("N"), "--worker=" + Guid.NewGuid().ToString("N"),
            "--generation=1", "--role=" + (byte)ComponentRole.Analysis, "--clock=" + Guid.NewGuid().ToString("N"),
            "--origin=0", "--frequency=" + Stopwatch.Frequency, "--token=" + new string('a', 64), "--parent=" + Process.GetCurrentProcess().Id });
        Check(await Task.WhenAny(connect, Task.Delay(5000)) == connect, "Activation fixture did not connect."); await connect;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Check((await RuntimeWire.ReadAsync(data, deadline.Token))?.Payload is WorkerHello &&
            (await RuntimeWire.ReadAsync(control, deadline.Token))?.Payload is WorkerHello, "Activation fixture had no valid handshake.");
        var watch = Stopwatch.StartNew();
        Check(await Task.WhenAny(process.Exit, Task.Delay(18000)) == process.Exit && await process.Exit == 70 && watch.Elapsed >= TimeSpan.FromSeconds(10),
            "Worker did not enforce the production activation deadline.");
    }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
}
