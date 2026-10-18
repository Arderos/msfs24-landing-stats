using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;
using LandingStats.Core;

namespace LandingStats.WorkerHost;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
    [STAThread]
    private static int Main(string[] args)
    {
        SetErrorMode(3); // Background faults must not display a system error dialog.
        if (args.Length == 1 && args[0] == "--fixture-child") { FixtureFactory().GetMethod("RunChild")!.Invoke(null, null); return 0; }
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            try
            {
                var path = args.FirstOrDefault(value => value.StartsWith("--failure-log=", StringComparison.Ordinal))?.Substring(14);
                if (path != null)
                {
                    // No exception message or arguments: they may contain credentials.
                    var detail = error.GetType().FullName + " 0x" + error.HResult.ToString("x8", CultureInfo.InvariantCulture) + "\n" + error.StackTrace;
                    File.WriteAllText(path, detail.Length <= 8192 ? detail : detail.Substring(0, 8192));
                }
            }
            catch { }
            return 70;
        }
    }
    private static async Task<int> RunAsync(string[] args)
    {
        var options = Parse(args);
        var identity = new WorkerIdentity(Guid.ParseExact(Required(options, "session"), "N"), Guid.ParseExact(Required(options, "worker"), "N"),
            long.Parse(Required(options, "generation"), CultureInfo.InvariantCulture), (ComponentRole)byte.Parse(Required(options, "role"), CultureInfo.InvariantCulture));
        var clock = new RuntimeClockEpoch(Guid.ParseExact(Required(options, "clock"), "N"), long.Parse(Required(options, "origin"), CultureInfo.InvariantCulture),
            long.Parse(Required(options, "frequency"), CultureInfo.InvariantCulture));
        if (clock.Frequency != Stopwatch.Frequency) throw new InvalidDataException("Worker clock frequency differs from the runtime.");
        var pipeName = Required(options, "pipe");
        if (!pipeName.StartsWith("ls-runtime-", StringComparison.Ordinal) || pipeName.Length > 128 ||
            pipeName.Any(c => !char.IsLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Invalid worker pipe name.");
        using var lifetime = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var controlPipe = new NamedPipeClientStream(".", pipeName + "-control", PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(pipe.ConnectAsync(10000, lifetime.Token), controlPipe.ConnectAsync(10000, lifetime.Token)).ConfigureAwait(false);
        var parent = uint.Parse(Required(options, "parent"), CultureInfo.InvariantCulture);
        if (parent == 0 || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parent ||
            !GetNamedPipeServerProcessId(controlPipe.SafePipeHandle, out var controlServer) || controlServer != parent)
            throw new InvalidDataException("Worker channels do not belong to the launching parent.");
        var channel = new WorkerChannel(pipe, identity);
        var control = new WorkerChannel(controlPipe, identity, true);
        var transport = new WorkerTransport(channel, control);
        var capabilities = RuntimeCapabilities.Basic | RuntimeCapabilities.SharedStorageQuota | RuntimeCapabilities.FrozenGeometry;
        if (options.TryGetValue("fixture", out var capabilityFixture) && capabilityFixture == "legacy-quota") capabilities = RuntimeCapabilities.Basic;
        var hello = new WorkerHello(RuntimeBuildIdentity.Read(AppDomain.CurrentDomain.BaseDirectory), Required(options, "token"), capabilities, clock.Id, Stopwatch.Frequency);
        if (!channel.TryPublish(hello, 0, Stopwatch.GetTimestamp())) throw new IOException("Cannot publish worker handshake.");
        if (!control.TryPublish(hello, 0, Stopwatch.GetTimestamp())) throw new IOException("Cannot publish worker control handshake.");
        var pumps = new[] { channel.WritePumpAsync(lifetime.Token), channel.ReadPumpAsync(lifetime.Token),
            control.WritePumpAsync(lifetime.Token), control.ReadPumpAsync(lifetime.Token) };
        var context = new WorkerExecutionContext(identity, clock, transport, lifetime.Token);
        var startDeadline = Stopwatch.StartNew();
        while (true)
        {
            foreach (var pump in pumps) if (pump.IsCompleted) await pump.ConfigureAwait(false);
            if (transport.TryTakeCommand(out var start))
            {
                if (!(start!.Payload is WorkerCommandMessage command) || command.Command != WorkerCommand.Start)
                    throw new InvalidDataException("The runtime must authorize this generation before role initialization.");
                break;
            }
            if (startDeadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Worker activation was not acknowledged.");
            await Task.Delay(10).ConfigureAwait(false);
        }
        IWorkerRole role;
        if (options.TryGetValue("fixture", out var mode))
            role = (IWorkerRole)FixtureFactory().GetMethod("CreateFixture")!.Invoke(null, new object[] { identity.Role, mode })!;
        else
        {
            var module = identity.Role == ComponentRole.CloudSync ? "Cloud" : identity.Role.ToString();
            if (!new[] { "Simulator", "Controllers", "Analysis", "Upload", "Cloud", "IntegrationApi" }.Contains(module))
                throw new InvalidDataException("Unsupported worker role.");
            var assembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.Workers." + module + ".dll"));
            role = (IWorkerRole)assembly.GetType("LandingStats.Workers.EntryPoint", true)!.GetMethod("Create")!.Invoke(null, null)!;
        }
        var run = role.RunAsync(context);
        var ioEnded = Task.WhenAny(pumps);
        var first = await Task.WhenAny(run, ioEnded).ConfigureAwait(false);
        if (first == run && run.Status == TaskStatus.RanToCompletion)
        {
            var drain = Stopwatch.StartNew();
            while (transport.PendingCount != 0 && !ioEnded.IsCompleted && drain.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10).ConfigureAwait(false);
            lifetime.Cancel(); pipe.Dispose(); controlPipe.Dispose();
            return transport.PendingCount == 0 ? 0 : 71;
        }
        lifetime.Cancel(); pipe.Dispose(); controlPipe.Dispose();
        // An owner that ignores cancellation is isolated by this process. Do not
        // wait for a native loop while the parent channel has already failed.
        if (await Task.WhenAny(run, Task.Delay(500)).ConfigureAwait(false) != run) Environment.Exit(70);
        return 70;
    }

    private static Type FixtureFactory() => Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
        "LandingStats.Workers.Testing.dll")).GetType("LandingStats.App.Runtime.FixtureRoleFactory", true)!;

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in args)
        {
            var split = argument.IndexOf('=');
            if (!argument.StartsWith("--", StringComparison.Ordinal) || split < 3 || argument.Length > 1024)
                throw new ArgumentException("Invalid worker launch options.");
            var name = argument.Substring(2, split - 2);
            if (!new[] { "session", "worker", "generation", "role", "clock", "origin", "frequency", "pipe", "token", "parent", "fixture", "failure-log" }.Contains(name) || options.ContainsKey(name))
                throw new ArgumentException("Unknown or duplicate worker launch option.");
            options.Add(name, argument.Substring(split + 1));
        }
        return options;
    }
    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException("Missing worker launch option.");
}
