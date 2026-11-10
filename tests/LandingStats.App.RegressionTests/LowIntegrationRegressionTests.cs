using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
using Newtonsoft.Json.Linq;

namespace LandingStats.App.RegressionTests;

internal static class LowIntegrationRegressionTests
{
    public static void RunAll() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-low-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await DisableRetiresOnlyIntegrationFailures(Path.Combine(root, "disable"));
            FailedScanRequiresFreshCompletion(Path.Combine(root, "scan"));
            await BusyProjectionPreservesHistoryAndRetiresCallbacks(Path.Combine(root, "busy"));
            await CatalogDisconnectRetiresFailure(Path.Combine(root, "catalog"));
            await CatalogIgnoresRetiredCompletion(false);
            await CatalogIgnoresRetiredCompletion(true);
        }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("ls-low-integration-", StringComparison.Ordinal), "Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }).GetAwaiter().GetResult();

    private static async Task DisableRetiresOnlyIntegrationFailures(string root)
    {
        var f = new Fixture(root); var first = f.Commit(); var second = f.Commit();
        foreach (var id in new[] { first, second }) File.WriteAllText(Path.Combine(f.Store.Root, id.ToString("N"), "analysis.json"), "broken");
        var failures = new ConcurrentQueue<Guid>();
        var api = f.Owner(); api.WorkFailed += (id, _) => failures.Enqueue(id);
        using var generation = new ManagedComponentGeneration(1, api.RunAsync);
        FileStream? held = null;
        try
        {
            await generation.StartAsync(CancellationToken.None); await api.SetEnabledAsync(true);
            await Until(() => failures.Distinct().Count() == 2, "Two corrupt analyses did not report independent integration failures.");
            held = new FileStream(Path.Combine(f.Events, "events-v1.json"), FileMode.Open, FileAccess.Read, FileShare.None);
            lock (f.RepositoryGate) f.Repository.Delete("unrelated-deletion");
            await Until(() => f.Projection.Snapshot.Failure?.Entity == Guid.Empty, "Locked journal did not report its owner-level failure.");
            var independent = Guid.NewGuid(); f.Projection.Failure("storage.retention", independent, "independent failure");
            await api.SetEnabledAsync(false);
            Check(f.Projection.Snapshot.Failure?.Entity == independent, "Disabling integration removed another component's failure.");
            f.Projection.Recovered("storage.retention", independent);
            Check(f.Projection.Snapshot.Failure == null, "Disabling integration retained an owner or episode failure.");
            await api.SetEnabledAsync(true);
            await Until(() => f.Projection.Snapshot.Failure?.Component == "integration.api", "Re-enabling a still-broken API hid its current failure.");
            await api.SetEnabledAsync(false);
            Check(f.Projection.Snapshot.Failure == null, "Second activation did not retire its failure.");
        }
        finally { held?.Dispose(); await api.StopAsync(); await generation.StopAsync(TimeSpan.FromSeconds(10)); }
    }

    private static void FailedScanRequiresFreshCompletion(string root)
    {
        var f = new Fixture(root); f.Commit(); f.Commit();
        using var scan = new FailedEnumerator(false);
        Field(f.Data, "_scan", scan);
        try { f.Data.ReconcileCommitted(); throw new InvalidOperationException("Faulting scan unexpectedly succeeded."); }
        catch (IOException) { }
        Check(scan.Disposed && Field(f.Data, "_scan") == null, "Failed enumerator was kept for a false successful continuation.");
        Check(!f.Data.ReconcileCommitted(), "Publishing one analysis incorrectly proved a full enumeration recovered.");
        Check(!f.Data.ReconcileCommitted(), "Publishing the second analysis incorrectly proved the scan completed.");
        Check(f.Data.ReconcileCommitted(), "A fresh completed enumeration did not prove recovery.");
        Check(!f.Data.ReconcileCommitted(), "An idle reconciliation tick claimed fresh scan evidence.");
        Check(f.EventsCount == 2, "Retrying the failed scan lost or duplicated durable analyses.");
    }

    private static async Task BusyProjectionPreservesHistoryAndRetiresCallbacks(string root)
    {
        var f = new Fixture(root); var id = f.Commit();
        using var scan = new FailedEnumerator(true); Field(f.Data, "_scan", scan);
        var failures = 0; var recovered = 0;
        var api = f.Owner(() => Interlocked.Increment(ref recovered));
        api.WorkFailed += (_, __) => Interlocked.Increment(ref failures);
        using var generation = new ManagedComponentGeneration(1, api.RunAsync);
        Task<ProcessedEpisode>? saving = null;
        try
        {
            await generation.StartAsync(CancellationToken.None); await api.SetEnabledAsync(true);
            await Until(() => scan.Entered.IsSet, "Background reconciliation did not enter the stalled scan.");
            var coordinator = new EpisodeWorkCoordinator(f.Store,
                new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), f.Repository, f.RepositoryGate),
                f.Repository, f.RepositoryGate, resultCommitted: api.Publish);
            saving = Task.Run(() => coordinator.ResumeAsync(id));
            await Deadline(saving, "History waited for the optional API reconciliation lock.");
            Check(f.Repository.LoadAll().Count == 1 && !scan.Released.IsSet, "History did not commit while reconciliation remained blocked.");
            await api.SetEnabledAsync(false);
            scan.Released.Set();
            await Until(() => scan.Disposed && (long)Field(api, "_nextReconcile")! > 0, "Retired scan did not finish.");
            Check(f.Projection.Snapshot.Failure == null && failures == 0 && recovered == 0,
                "A retired activation published stale failure or recovery callbacks.");
            await api.SetEnabledAsync(true);
            await Until(() => f.EventsCount == 1, "Deferred live publication was not recovered from its durable analysis.");
        }
        finally
        {
            scan.Released.Set();
            if (saving != null) await saving;
            await api.StopAsync(); await generation.StopAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task CatalogDisconnectRetiresFailure(string root)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "catalog.json"); File.WriteAllText(path, "{}");
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        var catalog = new GeometryCatalogRuntimeOwner((_, __) => { File.ReadAllText(path); return Task.CompletedTask; },
            error => projection.Failure("analysis.catalog", Guid.Empty, error.GetType().Name), () => projection.Recovered("analysis.catalog", Guid.Empty));
        var observations = new CatalogObservations(catalog);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var generation = new ManagedComponentGeneration(1, catalog.RunAsync);
        try
        {
            await generation.StartAsync(CancellationToken.None); observations.Connect();
            Check(!await catalog.WaitReadyAsync("fixture", CancellationToken.None), "Locked catalog became ready.");
            Check(projection.Snapshot.Failure?.Component == "analysis.catalog", "Catalog read failure was not visible.");
            var independent = Guid.NewGuid(); projection.Failure("analysis.episodes", independent, "independent failure");
            observations.Send(new LifecycleMessage(SourceLifecycle.SimulatorQuit));
            Check(projection.Snapshot.Failure?.Entity == independent, "Catalog retirement erased unrelated episode failure.");
            projection.Recovered("analysis.episodes", independent);
            Check(projection.Snapshot.Failure == null, "Disconnected simulator retained its catalog failure.");
            held.Dispose(); observations.Connect();
            Check(await catalog.WaitReadyAsync("fixture", CancellationToken.None), "Next simulator session did not load the repaired catalog.");
        }
        finally { await generation.StopAsync(TimeSpan.FromSeconds(10)); }
    }

    private static async Task CatalogIgnoresRetiredCompletion(bool oldFails)
    {
        var entered = Signal(); var release = Signal(); var calls = 0; var failures = 0; var recoveries = 0;
        var catalog = new GeometryCatalogRuntimeOwner(async (_, __) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult(true); await release.Task;
                if (oldFails) throw new IOException("retired session");
            }
            else throw new IOException("current session");
        }, _ => Interlocked.Increment(ref failures), () => Interlocked.Increment(ref recoveries));
        var observations = new CatalogObservations(catalog);
        using var generation = new ManagedComponentGeneration(1, catalog.RunAsync);
        try
        {
            await generation.StartAsync(CancellationToken.None); observations.Connect();
            await Deadline(entered.Task, "Old catalog request did not start.");
            var oldReady = catalog.WaitReadyAsync("fixture", CancellationToken.None);
            observations.Send(new LifecycleMessage(SourceLifecycle.Disconnected)); observations.Connect();
            Check(!await oldReady, "Disconnected catalog request retained its readiness obligation.");
            var beforeCompletion = Volatile.Read(ref recoveries); release.TrySetResult(true);
            Check(!await catalog.WaitReadyAsync("fixture", CancellationToken.None), "Current failed catalog became ready.");
            Check(calls == 2 && failures == 1 && recoveries == beforeCompletion,
                "Retired catalog completion emitted a failure or erased the next session's failure.");
        }
        finally { release.TrySetResult(true); await generation.StopAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class Fixture
    {
        public Fixture(string root)
        {
            Events = Path.Combine(root, "events"); Repository = new LandingRepository(Path.Combine(root, "history"));
            Store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
            Data = new IntegrationApiData(Events, Repository, RepositoryGate, Store, () => Projection.Snapshot, () => Array.Empty<SupervisionSnapshot>());
        }
        public readonly string Events;
        public readonly LandingRepository Repository;
        public readonly object RepositoryGate = new object();
        public readonly EpisodeTransactionStore Store;
        public readonly RuntimePresentation Projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        public readonly IntegrationApiData Data;
        public int EventsCount
        {
            get
            {
                var response = JObject.Parse(Data.Handle("{\"id\":\"test\",\"protocolVersion\":1,\"method\":\"events.read\"}"));
                if ((string?)response["error"]?["code"] == "TemporarilyUnavailable") return -1;
                return ((JArray)response["result"]!["events"]!).Count;
            }
        }
        public IntegrationApiRuntimeOwner Owner(Action? recovered = null)
        {
            var api = new IntegrationApiRuntimeOwner(() => Data, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), Guid.NewGuid(),
                new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency), (_, __) => { }, _ => { },
                error => Projection.Failure("integration.api", Guid.Empty, error), new ComponentRecoveryOptions(), _ => "review-api-" + Guid.NewGuid().ToString("N"),
                () => { Projection.Recovered("integration.api", Guid.Empty); recovered?.Invoke(); });
            api.WorkFailed += (id, error) => Projection.Failure("integration.api", id, error);
            api.WorkRecovered += id => Projection.Recovered("integration.api", id);
            api.Retired += () => Projection.Retired("integration.api");
            return api;
        }
        public Guid Commit()
        {
            var id = Guid.NewGuid(); var record = new LandingRecord { Id = id.ToString("N"), RecordingEpisodeId = "physical:" + id.ToString("N"),
                TimestampUtc = DateTime.UtcNow, AircraftTitle = "Fixture", ContactNumber = 1, ContactCount = 1 };
            var episode = new LandingEpisodeEventArgs(1, new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 } }, "MSFS", "Fixture", "TEST", "TEST",
                Array.Empty<AirportFacility>(), Array.Empty<string>(), new TelemetryDecisionJournal(), persistentId: id);
            var manifest = Store.CommitInputs(episode, record.TimestampUtc, Array.Empty<AirportFacility>(), false, 1);
            Store.CommitAnalysis(manifest, ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { record }, new TelemetryDecisionJournal().Snapshot()));
            return id;
        }
    }

    // Fault injection at the filesystem enumerator boundary makes both an
    // exception and a stalled MoveNext deterministic, without changing storage.
    private sealed class FailedEnumerator : IEnumerator<Guid>
    {
        private readonly bool _block;
        private bool _failed;
        public FailedEnumerator(bool block) { _block = block; }
        public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Released = new ManualResetEventSlim();
        public volatile bool Disposed;
        public Guid Current => Guid.Empty;
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            if (_failed) return false;
            Entered.Set(); if (_block) Released.Wait(); _failed = true;
            throw new IOException("Directory enumeration failed.");
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { Disposed = true; }
    }

    private sealed class CatalogObservations
    {
        private readonly GeometryCatalogRuntimeOwner _catalog;
        private readonly WorkerIdentity _worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Simulator);
        private long _sequence;
        public CatalogObservations(GeometryCatalogRuntimeOwner catalog) { _catalog = catalog; }
        public void Send(RuntimePayload payload) => _catalog.Observe(new RecordingRuntimeEffects
        { SourceObservation = new RuntimeEnvelope(_worker, 1, ++_sequence, Stopwatch.GetTimestamp(), payload) });
        public void Connect()
        { Send(new LifecycleMessage(SourceLifecycle.Connected)); Send(new AircraftMessage("fixture", "Aircraft", "", "")); Send(new LifecycleMessage(SourceLifecycle.PhysicsReady)); }
    }
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static void Field(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Deadline(Task task, string message)
    { if (await Task.WhenAny(task, Task.Delay(5000)) != task) throw new TimeoutException(message); await task; }
    private static async Task Until(Func<bool> condition, string message)
    { var timer = Stopwatch.StartNew(); while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(message); await Task.Delay(20); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
