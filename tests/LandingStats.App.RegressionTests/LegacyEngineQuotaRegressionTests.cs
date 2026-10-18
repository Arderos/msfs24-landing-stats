using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class LegacyEngineQuotaRegressionTests
{
    internal static void RunAll() => Task.Run(() => RejectBeforeActivation(AppDomain.CurrentDomain.BaseDirectory, "legacy-quota")).GetAwaiter().GetResult();
    internal static void RunRetained() => Task.Run(async () =>
    {
        var legacy = Environment.GetEnvironmentVariable("LANDINGSTATS_LEGACY_ENGINE");
        if (string.IsNullOrWhiteSpace(legacy)) throw new RegressionTestSkipped("LANDINGSTATS_LEGACY_ENGINE is not configured.");
        await RejectBeforeActivation(legacy!, null);
    }).GetAwaiter().GetResult();

    private static async Task RejectBeforeActivation(string source, string? fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "lq-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(root);
        try
        {
            var engines = Path.Combine(root, "e"); var scratch = Path.Combine(root, "j");
            var engine = fixture == null ? CaptureLegacy(engines, source) : ProcessingEngineLease.Capture(engines, source);
            var store = new EpisodeTransactionStore(Path.Combine(root, "p"), engine.Identity);
            var id = Guid.NewGuid(); var episode = new LandingEpisodeEventArgs(1,
                new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true } },
                "fixture", "Retained", "T", "M", Array.Empty<AirportFacility>(), Array.Empty<string>(), isDiagnosticOnly: true, persistentId: id);
            var original = store.CommitInputs(episode, DateTime.UtcNow, Array.Empty<AirportFacility>(), false, 0);
            var limit = Bytes(root) + 2 * 1024 * 1024;
            using var quota = RuntimeStorageQuota.Attach(root, new RuntimeStorageLimits { Bytes = limit, MetadataReserve = 128 * 1024, Quantum = 4096 });
            Check(RuntimeRetentionRegressionTests.Writer(root, "fill") == 0, "Storage fixture did not fill its data budget.");
            Check(RuntimeStorageQuota.RequiresParticipation(scratch), "Retained output did not inherit the runtime budget.");
            var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            var executor = new RetainedAnalysisExecutor(engines, scratch, store, Guid.NewGuid(), clock, _ => fixture);
            var activated = false; executor.WorkerStarted += _ => activated = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            IOException? failure = null;
            try { await executor.AnalyzeAsync(original, timeout.Token); }
            catch (IOException error) { failure = error; }
            Check(failure != null && failure.Message.Contains("shared storage quota"), "Incompatible engine lacked an explicit pending reason.");
            store.RecordFailure(store.Read(id), failure!);
            var pending = store.Read(id);
            Check(!activated && pending.Stage == EpisodeWorkStage.InputsCommitted && pending.Engine == original.Engine && pending.InputHash == original.InputHash,
                "Old engine activated or changed pending inputs.");
            Check(store.ReadInputs(pending).Episode.Samples.Count == 1 && ProcessingEngineLease.Open(engines, engine.Identity).Identity == engine.Identity,
                "Rejected old engine lost the retained recording or binary lease.");
            Check(!Directory.Exists(scratch) || Directory.GetDirectories(scratch).Length == 0, "Rejected worker left a writable output job.");
            Check(Bytes(root) <= limit, "Legacy engine bypassed the shared budget.");
            Console.WriteLine("Legacy quota gate: rejected before activation; original engine and inputs retained: " + engine.Identity);
        }
        finally
        {
            var target = Path.GetFullPath(root); var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup.");
            Directory.Delete(target, true);
        }
    }

    private static ProcessingEngineLease CaptureLegacy(string root, string source)
    {
        source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        Check(File.Exists(Path.Combine(source, "MSFS-Landing-Stats.exe")) && !File.Exists(Path.Combine(source, "LandingStats.Runtime.dll")),
            "Retained compatibility source must use the real legacy App-contained engine layout.");
        var manifestPath = Path.Combine(source, "engine.json");
        if (File.Exists(manifestPath))
        {
            // Verify the original lease read-only; only CaptureFiles writes to
            // this test's private root. Never repair a user's retained engine.
            var retained = ProcessingEngineLease.Open(Path.GetDirectoryName(source)!, Path.GetFileName(source));
            var manifest = DurableFile.ReadJson<EngineLeaseManifest>(manifestPath, 128 * 1024);
            var copy = ProcessingEngineLease.CaptureFiles(root, source, manifest.Files.Select(file => file.Name).ToArray());
            Check(copy.Identity == retained.Identity, "Copying the legacy lease changed its exact engine identity.");
            return copy;
        }

        // Pre-lease test snapshots contain diagnostic executables as well.
        // Retain the historical deployment list, not every file in that folder.
        var required = new[]
        {
            "Google.Apis.Auth.dll", "Google.Apis.Core.dll", "Google.Apis.Drive.v3.dll", "Google.Apis.dll",
            "LandingStats.Core.dll", "LandingStats.Runtime.Contracts.dll", "LandingStats.WorkerHost.exe",
            "LandingStats.WorkerHost.exe.config", "MSFS-Landing-Stats.exe", "MSFS-Landing-Stats.exe.config",
            "Microsoft.FlightSimulator.SimConnect.dll", "Newtonsoft.Json.dll", "SimConnect.dll"
        };
        foreach (var name in required)
            Check(File.Exists(Path.Combine(source, name)), "Retained legacy deployment is incomplete: " + name);
        var optional = new[]
        {
            "LandingStats.Archives.dll", "System.Buffers.dll", "System.Memory.dll", "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll", "System.Threading.Tasks.Extensions.dll", "ZstdSharp.dll"
        };
        return ProcessingEngineLease.CaptureFiles(root, source,
            required.Concat(optional.Where(name => File.Exists(Path.Combine(source, name)))).ToArray());
    }
    private static long Bytes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
