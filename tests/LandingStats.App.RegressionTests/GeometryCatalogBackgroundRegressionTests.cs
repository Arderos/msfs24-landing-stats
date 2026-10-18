using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class GeometryCatalogBackgroundRegressionTests
{
    public static void RunAll() => Task.Run(async () => { await PreparedSnapshot(); await SessionTrigger(); await IsolatedWorker(); }).GetAwaiter().GetResult();
    private static async Task PreparedSnapshot()
    {
        var root = Temp();
        try
        {
            var packages = Path.Combine(root, "packages"); var cache = Path.Combine(root, "cache"); var ready = Path.Combine(cache, "ready.json");
            void Aircraft(string name, int arm)
            {
                var plane = Path.Combine(packages, name, "SimObjects", "Airplanes", name); Directory.CreateDirectory(plane);
                File.WriteAllText(Path.Combine(plane, "aircraft.cfg"), "[FLTSIM.0]\ntitle=" + name + "\nisUserSelectable=1\n");
                File.WriteAllText(Path.Combine(plane, "flight_model.cfg"), "[CONTACT_POINTS]\npoint.0=1,20,0,-3\npoint.1=1," + arm + ",-5,-3\npoint.2=1," + arm + ",5,-3\n");
            }
            async Task Refresh() => await new FlightModelGeometryResolver(new[] { packages }, false, null, cache).WritePreparedCatalogAsync(ready);
            Aircraft("First", -10); Aircraft("Second", -20); await Refresh();
            var content = File.ReadAllText(ready);
            Check(!content.Contains("flight_model.cfg\",\"Directory\"") && new FileInfo(ready).Length < 4096, "Ready snapshot contains the dependency catalog.");
            Aircraft("First", -30);
            var changed = Path.Combine(packages, "First", "SimObjects", "Airplanes", "First", "flight_model.cfg");
            File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
            // The per-landing reader must not inspect even a now-inaccessible
            // package tree; changing aircraft still selects the prepared entry.
            var offline = packages + "-offline"; Directory.Move(packages, offline);
            var reader = FlightModelGeometryResolver.FromPreparedCatalog(ready);
            Check(reader.TryResolve("First", "", "", out var first) && first.MainGearLongitudinalFeet == -10, "Landing revalidated source files.");
            Check(reader.TryResolve("Second", "", "", out var second) && second.MainGearLongitudinalFeet == -20, "Aircraft switch lost the ready catalog.");
            Check(!reader.TryResolve("Missing", "", "", out _), "Unknown aircraft fabricated geometry.");
            Directory.Move(offline, packages);
            Directory.Delete(Path.Combine(packages, "Second"), true); Aircraft("Third", -40); await Refresh();
            var updated = FlightModelGeometryResolver.FromPreparedCatalog(ready).CatalogForTesting();
            Check(updated.Count == 2 && updated.Single(item => item.AircraftTitle == "First").MainGearLongitudinalFeet == -30 &&
                updated.Single(item => item.AircraftTitle == "Third").MainGearLongitudinalFeet == -40, "Next session missed an update, installation or removal.");
        }
        finally { Delete(root); }
    }

    private static async Task SessionTrigger()
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        var owner = new GeometryCatalogRuntimeOwner(async (_, token) =>
        { Interlocked.Increment(ref calls); entered.TrySetResult(true); using (token.Register(() => release.TrySetCanceled())) await release.Task; }, _ => { });
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Simulator); long sequence = 0;
        void Observe(RuntimePayload payload) => owner.Observe(new RecordingRuntimeEffects
        { SourceObservation = new RuntimeEnvelope(worker, 1, ++sequence, Stopwatch.GetTimestamp(), payload) });
        using var stop = new CancellationTokenSource();
        var context = new ManagedComponentContext(1, stop.Token); var running = owner.RunAsync(context);
        try
        {
            await context.Ready;
            Observe(new LifecycleMessage(SourceLifecycle.Connected)); Observe(new AircraftMessage("fixture", "First", "", ""));
            Check(calls == 0 && !await owner.WaitReadyAsync("fixture", CancellationToken.None), "Startup/connection started package I/O before physics.");
            Observe(new LifecycleMessage(SourceLifecycle.PhysicsReady)); await Deadline(entered.Task);
            var waiting = owner.WaitReadyAsync("fixture", CancellationToken.None);
            Check(!waiting.IsCompleted, "Landing accepted an unfinished refresh.");
            Observe(new AircraftMessage("fixture", "Second", "", "")); Observe(new LifecycleMessage(SourceLifecycle.PhysicsReady));
            Check(calls == 1, "Aircraft switch/duplicate physics started another refresh.");
            using (var cancelled = new CancellationTokenSource())
            {
                var wait = owner.WaitReadyAsync("fixture", cancelled.Token); cancelled.Cancel();
                try { await wait; throw new Exception("Cancelled landing kept waiting."); } catch (OperationCanceledException) { }
            }
            release.TrySetResult(true); Check(await waiting, "Ready catalog was not published.");
            Observe(new AircraftMessage("fixture", "Third", "", ""));
            Check(await owner.WaitReadyAsync("fixture", CancellationToken.None) && calls == 1, "Ready catalog was refreshed for a different aircraft.");
            Observe(new LifecycleMessage(SourceLifecycle.Disconnected)); Observe(new LifecycleMessage(SourceLifecycle.Connected));
            Observe(new AircraftMessage("fixture", "First", "", ""));
            Check(!await owner.WaitReadyAsync("fixture", CancellationToken.None), "New connection reused old readiness before physics.");
            Observe(new LifecycleMessage(SourceLifecycle.PhysicsReady));
            Check(await owner.WaitReadyAsync("fixture", CancellationToken.None) && calls == 2, "New simulator session did not refresh the catalog.");
        }
        finally { stop.Cancel(); try { await running; } catch (OperationCanceledException) { } context.Dispose(); }
    }

    private static async Task IsolatedWorker()
    {
        var root = Temp();
        try
        {
            var engines = Path.Combine(root, "engines"); var engine = ProcessingEngineLease.Capture(engines, AppDomain.CurrentDomain.BaseDirectory);
            var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), engine.Identity);
            var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            var executor = new RetainedAnalysisExecutor(engines, Path.Combine(root, "jobs"), store, Guid.NewGuid(), clock, generation => generation == 1 ? "hang" : "analysis-local");
            var started = Signal(); executor.WorkerStarted += _ => started.TrySetResult(true);
            using (var cancel = new CancellationTokenSource())
            {
                var hanging = executor.RefreshGeometryCatalogAsync("fixture", cancel.Token);
                try { await Deadline(started.Task); }
                finally { cancel.Cancel(); }
                try { await Deadline(hanging); throw new InvalidOperationException("Catalog child ignored cancellation."); }
                catch (OperationCanceledException) { }
            }
            Check(!Directory.Exists(Path.Combine(root, "geometry-index")), "Cancelled worker published a ready catalog.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await executor.RefreshGeometryCatalogAsync("fixture", deadline.Token);
            var path = FlightModelGeometryResolver.PreparedCatalogPath(Path.Combine(root, "geometry-index"), "fixture");
            Check(File.Exists(path) && FlightModelGeometryResolver.FromPreparedCatalog(path).CatalogForTesting().Count == 0,
                "Real worker did not commit its bounded prepared catalog through IPC.");
            Check(!store.Enumerate().Any() && !Directory.EnumerateDirectories(Path.Combine(root, "jobs")).Any(), "Catalog job created an episode or leaked its process scratch.");
        }
        finally { Delete(root); }
    }
    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Deadline(Task task) { if (await Task.WhenAny(task, Task.Delay(10000)) != task) throw new TimeoutException("Catalog fixture timed out."); await task; }
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "ls-catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Delete(string root)
    {
        Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(root).StartsWith("ls-catalog-", StringComparison.Ordinal), "Invalid fixture cleanup root.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
