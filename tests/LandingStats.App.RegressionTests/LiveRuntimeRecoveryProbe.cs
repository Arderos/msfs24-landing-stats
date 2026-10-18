using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

// Explicitly opt-in: actual native sources and production dispatch/checkpoints,
// private data, no network delivery or user history. Only this probe's helpers
// are terminated. No simulator variables, aircraft controls or pause are changed.
internal static class LiveRuntimeRecoveryProbe
{
    public static int Run(string directory) => Task.Run(() => RunAsync(directory)).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "runtime-recovery.tsv"), FileMode.CreateNew));
        log.AutoFlush = true;
        log.WriteLine("elapsed_s\tevent\tdetail");
        var clock = Stopwatch.StartNew(); var gate = new object();
        var counts = new Dictionary<string, long>(); var failures = new List<string>();
        var pids = new HashSet<int>();
        void Log(string kind, string detail)
        { lock (gate) log.WriteLine(clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) + "\t" + kind + "\t" + detail.Replace('\t', ' ').Replace('\n', ' ')); }
        void Count(string key) { lock (gate) counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1; }
        long Read(string key) { lock (gate) return counts.TryGetValue(key, out var count) ? count : 0; }
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task Until(Func<bool> condition, string message)
        {
            var wait = Stopwatch.StartNew();
            while (!condition())
            {
                lock (gate) Check(failures.Count == 0, string.Join("; ", failures));
                if (wait.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException(message);
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(),
            new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        var privateRoot = Path.Combine(directory, identity.RuntimeId.ToString("N"));
        using var runtime = new ApplicationRuntime(privateRoot, identity,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), (input, effects) =>
            {
                RuntimeEnvelope? envelope = null;
                if (input.Input.Kind == JournalRecordKind.SourceMessage) envelope = RuntimeWire.Decode(input.Input.CopyContent());
                if (input.Input.Kind == JournalRecordKind.JoinedFrame) envelope = JoinedRuntimeFrame.Decode(input.Input.CopyContent()).Source;
                if (envelope != null)
                {
                    var prefix = envelope.Worker.Role + "." + envelope.Worker.Generation + ".";
                    Count(prefix + envelope.Payload.Kind);
                    if (envelope.Payload is AircraftMessage aircraft) Log("aircraft", prefix + aircraft.Title);
                    if (envelope.Payload is SourceObservationMessage observed && observed.Observation.Availability == SourceObservationAvailability.Available)
                    {
                        Count(prefix + "available");
                        Log("observation", prefix + "freeze=" + observed.Observation.FreezeMask + " slew=" + observed.Observation.Slew);
                    }
                }
                if (input.Input.Kind == JournalRecordKind.SourceRetirement)
                {
                    var retired = RuntimeSourceRetirement.Decode(input.Input.CopyContent());
                    Log("retirement", retired.Worker.Role + "." + retired.Worker.Generation + " intentional=" + retired.Intentional);
                    Count("retired." + retired.Worker.Role);
                }
                foreach (var raw in effects.RawCapture.OfType<RawDebugSampleEventArgs>())
                {
                    var evidence = raw.Sample.SourceEvidence;
                    if (evidence == null)
                    { lock (gate) failures.Add("Production RAW sample lost source evidence."); continue; }
                    Count("raw." + evidence!.Generation);
                    if (evidence.Observation.Availability == SourceObservationAvailability.Available)
                        Count("annotated." + evidence.Generation);
                }
                if (effects.Captures.Count != 0) lock (gate) failures.Add("Ground-only recovery invented a landing.");
                return Task.CompletedTask;
            }, (name, generation) => { lock (gate) failures.Add(name + "." + generation); });
        runtime.HealthChanged += health => Log("health", health.Component + "." + health.Generation + " " + health.State + " " + health.Reason);
        try
        {
            runtime.Start();
            await runtime.SetRawCaptureAsync(true).ConfigureAwait(false);
            var simulator = runtime.Source(ComponentRole.Simulator);
            var controllers = runtime.Source(ComponentRole.Controllers);
            for (var generation = 1; generation <= 3; generation++)
            {
                await Until(() => Read("annotated." + generation) >= 100 && Read("Simulator." + generation + ".Aircraft") >= 1 &&
                    Read("Controllers." + generation + ".ControllerSnapshot") >= 100, "Native sources did not provide fresh frames/identity/controllers.");
                Check(simulator.CurrentHost!.Identity.Generation == generation && controllers.CurrentHost!.Identity.Generation == generation,
                    "Unexpected unplanned source restart.");
                pids.Add(simulator.CurrentHost.ProcessId); pids.Add(controllers.CurrentHost!.ProcessId);
                Log("generation_ready", generation + " sim_pid=" + simulator.CurrentHost.ProcessId + " controls_pid=" + controllers.CurrentHost!.ProcessId);
                if (generation == 3) break;
                var originalSimulator = simulator.CurrentHost;
                var simulatorCount = Read("annotated." + generation);
                Log("terminate", "Controllers." + generation);
                await controllers.CurrentHost.TerminateAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                await Until(() => Read("Controllers." + (generation + 1) + ".ControllerSnapshot") >= 100 &&
                    Read("annotated." + generation) >= simulatorCount + 100, "Controller replacement did not resume while simulator continued.");
                Check(simulator.CurrentHost == originalSimulator && originalSimulator.Identity.Generation == generation,
                    "Controller failure replaced the simulator.");
                var originalControllers = controllers.CurrentHost!;
                pids.Add(originalControllers.ProcessId);
                var controllerCount = Read("Controllers." + (generation + 1) + ".ControllerSnapshot");
                Log("terminate", "Simulator." + generation);
                await simulator.CurrentHost.TerminateAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                await Until(() => Read("annotated." + (generation + 1)) >= 100 &&
                    Read("Controllers." + (generation + 1) + ".ControllerSnapshot") >= controllerCount + 100,
                    "Simulator replacement did not resume while controllers continued.");
                Check(controllers.CurrentHost == originalControllers, "Simulator failure replaced the controllers.");
            }
            await runtime.StopAsync().ConfigureAwait(false);
            Check(runtime.Health.All(item => item.State == SupervisionState.Stopped), "An owner remained active after shutdown.");
            Check(Read("retired.Simulator") == 3 && Read("retired.Controllers") == 3, "Retirement boundaries were lost.");
            foreach (var pid in pids)
            {
                try { using var process = Process.GetProcessById(pid); Check(process.HasExited, "Owned helper still running: " + pid); }
                catch (ArgumentException) { }
            }
            lock (gate)
            {
                Check(failures.Count == 0, string.Join("; ", failures));
                foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Log("count", pair.Key + "=" + pair.Value);
            }
            Log("result", "PASS: two replacements per native role; independent peer progress; six confirmed exits; no synthetic landing.");
            Console.WriteLine("Live runtime recovery PASS: " + directory);
            return 0;
        }
        catch (Exception error) { Log("failure", error.ToString()); Console.WriteLine(error); return 2; }
        finally { await runtime.StopAsync().ConfigureAwait(false); }
    }
}
