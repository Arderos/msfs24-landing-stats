using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class EpisodeReportLifecycleRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type Processed = typeof(MainWindow).GetNestedType("ProcessedEpisode", BindingFlags.NonPublic)!;

    public static void RunAll()
    {
        ShutdownStillPersistsRawAndDecisionsWithoutWaitingForFacilities();
        OptOutWhileAwaitingFacilitiesPreventsAutomaticArchive();
        FailedAnalysisStillProducesDiagnosticArchive();
        ReportChainKeepsEveryAcceptedCaptureInTheShutdownBoundary();
        LateAirportDecisionsAreArchivedExactlyOnce();
    }

    private static void ShutdownStillPersistsRawAndDecisionsWithoutWaitingForFacilities()
    {
        WithWindow((window, directory, lifetime) =>
        {
            lifetime.Cancel();
            var unavailable = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
            var task = Queue(window, SuccessfulProcessing(), Episode(1, unavailable.Task));
            Complete(task);
            Assert(ReferenceEquals(Get(window, "_bugReportPersistenceTask"), task), "report is absent from durable shutdown tracking");
            var path = Directory.GetFiles(directory, "*_auto_raw.zip").Single();
            using var archive = ZipFile.OpenRead(path);
            Assert(archive.GetEntry("telemetry.csv") != null && archive.GetEntry("landing-results.json") != null,
                "shutdown lost raw or calculated data");
            using var reader = new StreamReader(archive.GetEntry("decisions.json")!.Open());
            var json = reader.ReadToEnd();
            Assert(json.Contains("replay_rejected") && json.Contains("facilities_unavailable"),
                "shutdown diagnostic archive lost analysis or facility outcome");
            Assert(!unavailable.Task.IsCompleted, "test did not actually bypass an unresolved facility lookup");
        });
    }

    private static void OptOutWhileAwaitingFacilitiesPreventsAutomaticArchive()
    {
        WithWindow((window, directory, lifetime) =>
        {
            var facilities = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
            var task = Queue(window, SuccessfulProcessing(), Episode(2, facilities.Task));
            Assert(!task.IsCompleted, "report did not await optional facilities");
            Set(window, "_automaticTelemetryEnabled", 0);
            facilities.SetResult(Array.Empty<RunwayFacility>());
            Complete(task);
            Assert(!Directory.Exists(directory) || Directory.GetFiles(directory, "*_auto_raw.zip").Length == 0,
                "automatic report was archived after opt-out while waiting");
        });
    }

    private static void FailedAnalysisStillProducesDiagnosticArchive()
    {
        WithWindow((window, directory, lifetime) =>
        {
            var failed = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "FromException" && method.IsGenericMethodDefinition)
                .MakeGenericMethod(Processed).Invoke(null, new object[] { new InvalidDataException("Sensitive diagnostic text must not be included.") })!;
            Complete(Queue(window, failed, Episode(3)));
            using var archive = ZipFile.OpenRead(Directory.GetFiles(directory, "*_auto_raw.zip").Single());
            using var reader = new StreamReader(archive.GetEntry("decisions.json")!.Open());
            var json = reader.ReadToEnd();
            Assert(json.Contains("analysis_failed") && json.Contains("InvalidDataException") && !json.Contains("Sensitive"),
                "analysis failure was lost or copied arbitrary exception text into the journal");
        });
    }

    private static void ReportChainKeepsEveryAcceptedCaptureInTheShutdownBoundary()
    {
        WithWindow((window, directory, lifetime) =>
        {
            var facilities = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
            var first = Queue(window, SuccessfulProcessing(), Episode(4, facilities.Task));
            var second = Queue(window, SuccessfulProcessing(), Episode(5));
            Assert(ReferenceEquals(Get(window, "_bugReportPersistenceTask"), second), "newest durable task was not published");
            Assert(!first.IsCompleted && !second.IsCompleted, "later capture escaped its predecessor's durability boundary");
            facilities.SetResult(Array.Empty<RunwayFacility>());
            Complete(second);
            Assert(first.IsCompleted && Directory.GetFiles(directory, "*_auto_raw.zip").Length == 2,
                "draining the latest task did not retain both accepted captures");
        });
    }

    private static void LateAirportDecisionsAreArchivedExactlyOnce()
    {
        WithWindow((window, directory, lifetime) =>
        {
            var journal = new TelemetryDecisionJournal();
            journal.Record(1, "runway.airports", "requested", "Fresh airport list requested.");
            var processing = SuccessfulProcessing(journal);
            var facilities = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
            var task = Queue(window, processing, Episode(6, facilities.Task, journal));
            journal.Record(2, "runway.airports", "ready", "Fresh airport list received after analysis.");
            facilities.SetResult(Array.Empty<RunwayFacility>());
            Complete(task);
            using var archive = ZipFile.OpenRead(Directory.GetFiles(directory, "*_auto_raw.zip").Single());
            using var reader = new StreamReader(archive.GetEntry("decisions.json")!.Open());
            var json = reader.ReadToEnd();
            foreach (var code in new[] { "requested", "ready" })
                Assert(System.Text.RegularExpressions.Regex.Matches(json, "\"code\":\"" + code + "\"").Count == 1,
                    "early and late airport decisions must each be archived exactly once");
        });
    }

    private static void WithWindow(Action<MainWindow, string, CancellationTokenSource> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-stats-report-lifecycle-" + Guid.NewGuid().ToString("N"));
        using var lifetime = new CancellationTokenSource();
        // Avoid all constructor/startup services and real user repositories.
        // Logical closing also makes starting any network upload impossible.
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        Set(window, "_isClosed", true);
        Set(window, "_automaticTelemetryEnabled", 1);
        Set(window, "_lifetimeCancellation", lifetime);
        Set(window, "_bugReportPersistenceGate", new object());
        Set(window, "_bugReportPersistenceTask", Task.CompletedTask);
        Set(window, "_bugReportRepository", new BugReportRepository(directory));
        try { test(window, directory, lifetime); }
        finally
        {
            lifetime.Cancel();
            Complete((Task)Get(window, "_bugReportPersistenceTask")!);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static object SuccessfulProcessing(TelemetryDecisionJournal? journal = null)
    {
        journal ??= new TelemetryDecisionJournal();
        journal.Record(1, "analysis", "replay_rejected", "Synthetic rejected capture for lifecycle testing.");
        var result = Processed.GetMethod("Replay", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { journal.Snapshot() });
        return typeof(Task).GetMethod("FromResult", BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(Processed).Invoke(null, new[] { result })!;
    }

    private static LandingEpisodeEventArgs Episode(long id, Task<IReadOnlyList<RunwayFacility>>? facilities = null,
        TelemetryDecisionJournal? journal = null) => new LandingEpisodeEventArgs(
        id, new[] { new TelemetrySample { SimulationTimeSeconds = id, MotionSimulation = true } },
        "MSFS", "Test aircraft", "TEST", "TEST", Array.Empty<AirportFacility>(), Array.Empty<string>(),
        decisionJournal: journal, runwayFacilitiesTask: facilities);

    private static Task Queue(MainWindow window, object processing, LandingEpisodeEventArgs episode) =>
        (Task)typeof(MainWindow).GetMethod("QueueEpisodeReport", PrivateInstance)!
            .Invoke(window, new[] { processing, episode, (object)DateTime.UtcNow })!;

    private static object? Get(MainWindow window, string name) => typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window);
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(window, value);
    private static void Complete(Task task)
    {
        Assert(task.Wait(TimeSpan.FromSeconds(5)), "report lifecycle test timed out");
        task.GetAwaiter().GetResult();
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
