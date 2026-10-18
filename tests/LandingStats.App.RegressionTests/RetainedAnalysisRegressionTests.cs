using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.App.Storage;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RetainedAnalysisRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ra-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var engines = Path.Combine(root, "e"); var scratch = Path.Combine(root, "j"); var inputRoot = Path.Combine(root, "p");
        Directory.CreateDirectory(root);
        var completed = false;
        try
        {
            var lease = ProcessingEngineLease.Capture(engines, AppDomain.CurrentDomain.BaseDirectory);
            var original = new EpisodeTransactionStore(inputRoot, lease.Identity);
            // The running parent's engine differs from the pending episode's.
            // Only the verified retained helper is allowed to produce its result.
            var parent = new EpisodeTransactionStore(inputRoot, new string('b', 64));
            var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            var executor = new RetainedAnalysisExecutor(engines, scratch, parent, Guid.NewGuid(), clock, generation => generation == 1 ? "hang" : null);
            var id = Guid.NewGuid(); var episode = new LandingEpisodeEventArgs(1,
                new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true } },
                "fixture", "Retained aircraft", "T", "M", Array.Empty<AirportFacility>(), Array.Empty<string>(), isDiagnosticOnly: true, persistentId: id);
            var timestamp = new DateTime(2026, 10, 17, 10, 11, 12, DateTimeKind.Utc);
            var manifest = original.CommitInputs(episode, timestamp, Array.Empty<AirportFacility>(), false, 0);
            var originalHash = manifest.InputHash;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.WorkerStarted += _ => ready.TrySetResult(true);
            using (var cancel = new CancellationTokenSource())
            {
                var blocked = executor.AnalyzeAsync(manifest, cancel.Token);
                try { await Deadline(ready.Task, "Analysis fixture failed to start."); }
                finally { cancel.Cancel(); }
                try { await Deadline(blocked, "Cancelled analysis did not retire its process."); throw new InvalidOperationException("Hung analysis ignored cancellation."); }
                catch (OperationCanceledException) { }
            }
            Check(parent.Read(id).Stage == EpisodeWorkStage.InputsCommitted && parent.Read(id).InputHash == originalHash,
                "Failed analysis changed its source inputs or committed an incomplete result.");
            ProcessedEpisode result;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20))) result = await executor.AnalyzeAsync(manifest, timeout.Token);
            var saved = parent.Read(id);
            Check(saved.Stage == EpisodeWorkStage.Analysed && saved.Engine == lease.Identity && saved.TimestampUtc == timestamp && saved.InputHash == originalHash &&
                result.Records.Count == 0 && result.Decisions!.Entries.Any(entry => entry.Code == "diagnostic_only"),
                "Retained worker used the parent's engine/context or failed to preserve the diagnostic capture.");
            Check(Directory.GetFiles(root, "*.landing.json.gz", SearchOption.AllDirectories).Length == 0 && !Directory.Exists(Path.Combine(inputRoot, id.ToString("N"), "unused-history")),
                "Analysis helper wrote landing history instead of its private result.");
            var jobs = Directory.GetDirectories(scratch).Length;
            await executor.AnalyzeAsync(manifest, CancellationToken.None);
            Check(Directory.GetDirectories(scratch).Length == jobs && parent.Read(id).AnalysisHash == saved.AnalysisHash,
                "Retry recomputed an already accepted result or launched another worker.");
            var repository = new LandingRepository(Path.Combine(root, "h")); var repositoryGate = new object();
            parent.SaveHistory(saved, repository, repositoryGate);
            parent.CommitFacilities(new RuntimeFinalFacilities(id, episode.InitialFacilities, null));
            ProcessedEpisode report;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                report = await executor.FinalizeReportAsync(saved, repository, repositoryGate, timeout.Token);
            var finalized = parent.Read(id);
            Check(finalized.Stage == EpisodeWorkStage.ReportReady && finalized.Engine == lease.Identity && finalized.ReportHash.Length == 64 &&
                report.Decisions!.Entries.Any(entry => entry.Code == "lookup_not_required"),
                "Final report did not run through the retained engine or lost its terminal facility context.");
            jobs = Directory.GetDirectories(scratch).Length;
            await executor.FinalizeReportAsync(saved, repository, repositoryGate, CancellationToken.None);
            Check(Directory.GetDirectories(scratch).Length == jobs && parent.Read(id).ReportHash == finalized.ReportHash,
                "A frozen final report was recomputed on retry.");
            completed = true;
        }
        finally
        {
            // Executor returns only after its job tree exits; these are exclusively
            // owned fixture copies, never the original deployment or user history.
            if (completed && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    private static async Task Deadline(Task operation, string message)
    { if (await Task.WhenAny(operation, Task.Delay(20000)) != operation) throw new TimeoutException(message); await operation; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
