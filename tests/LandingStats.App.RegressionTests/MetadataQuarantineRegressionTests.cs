using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class MetadataQuarantineRegressionTests
{
    public static async Task RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-metadata-quarantine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            QuarantinePreservesEvidenceAndPins(Path.Combine(root, "retention"));
            SessionQuarantineSurvivesRestart(Path.Combine(root, "sessions"));
            InvalidSessionIdentitiesRemainQuarantined(Path.Combine(root, "session-identities"));
            await EpisodeOwnerResumesRepairedMetadata(Path.Combine(root, "episodes"));
            await ReceiptFailureCanRecover(Path.Combine(root, "receipt"));
            await RestoredIdleMetadataClearsReadFailure(Path.Combine(root, "idle"));
            IntegrationResumesRepairedMetadata(Path.Combine(root, "integration"));
            await InspectionWaitsForInputCommit(Path.Combine(root, "commit"));
        }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }

    private static void InvalidSessionIdentitiesRemainQuarantined(string root)
    {
        var engine = new string('a', 64); Directory.CreateDirectory(Path.Combine(root, "engines", engine));
        var sessions = Path.Combine(root, "sessions"); var sources = new Dictionary<string, byte[]>();
        var corruptions = new Action<RuntimeSession>[]
        {
            session => session.Capture = Guid.Empty,
            session => session.Clock = Guid.Empty,
            session => session.Origin = -1,
            session => session.Frequency = 0
        };
        foreach (var corrupt in corruptions)
        {
            var id = Guid.NewGuid(); var directory = Path.Combine(sessions, id.ToString("N"));
            var identity = new RecoveryJournalIdentity(id, Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 0, 1000));
            var session = RuntimeSession.Create(directory, identity, engine);
            corrupt(session);
            var path = Path.Combine(directory, "runtime.json"); DurableFile.WriteJson(path, session);
            sources.Add(path, File.ReadAllBytes(path));
        }
        for (var scan = 0; scan < 2; scan++)
        {
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            var owner = new RuntimeRetentionOwner(root, "none", new EpisodeTransactionStore(Path.Combine(root, "episodes"), engine),
                (id, reason) => projection.Failure("storage.retention", id, reason), id => projection.Recovered("storage.retention", id));
            owner.Collect();
            Check(projection.Snapshot.Failure == null, "Invalid persisted runtime identity escaped quarantine as a service failure.");
            foreach (var source in sources)
                Check(File.ReadAllBytes(source.Key).SequenceEqual(source.Value) &&
                    RuntimeMetadataFailure.Unchanged(Path.GetDirectoryName(source.Key)!, "runtime.json"),
                    "Quarantine changed an invalid runtime identity or failed to retain its receipt.");
        }
    }

    private static void SessionQuarantineSurvivesRestart(string root)
    {
        var engine = new string('a', 64); Directory.CreateDirectory(Path.Combine(root, "engines", engine));
        var sessions = Path.Combine(root, "sessions"); var id = Guid.NewGuid(); var directory = Path.Combine(sessions, id.ToString("N"));
        var identity = new RecoveryJournalIdentity(id, Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 0, 1000));
        var path = Path.Combine(directory, "runtime.json"); var marker = Path.Combine(directory, "metadata-failure.json");
        RuntimeRetentionOwner Owner(RuntimePresentation projection) => new RuntimeRetentionOwner(root, "none",
            new EpisodeTransactionStore(Path.Combine(root, "episodes"), engine),
            (entity, reason) => projection.Failure("storage.retention", entity, reason),
            entity => projection.Recovered("storage.retention", entity));
        var first = new RuntimePresentation((_, __) => Task.CompletedTask);
        using (RuntimeSession.AcquireOwnership(directory))
        {
            Owner(first).Collect();
            Check(!File.Exists(marker), "Retention quarantined a runtime whose creator still owned it.");
            RuntimeSession.Create(directory, identity, engine);
        }
        var valid = File.ReadAllBytes(path); File.WriteAllText(path, "broken runtime metadata");
        var orphan = Path.Combine(sessions, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(orphan);
        Owner(first).Collect();
        Check(first.Snapshot.Failure == null && File.Exists(marker) && File.Exists(Path.Combine(orphan, "metadata-failure.json")),
            "Session quarantine left corrupt or missing runtime metadata as an active failure.");
        var receipt = File.ReadAllBytes(marker);
        var restarted = new RuntimePresentation((_, __) => Task.CompletedTask);
        Owner(restarted).Collect();
        Check(restarted.Snapshot.Failure == null && File.ReadAllBytes(marker).SequenceEqual(receipt) &&
            File.ReadAllText(path) == "broken runtime metadata", "Restart changed quarantined session evidence or revived its service error.");
        File.WriteAllBytes(path, valid); Owner(restarted).Collect();
        Check(RuntimeSessionDirectories.Validate(directory).Runtime == id && !RuntimeMetadataFailure.Unchanged(directory, "runtime.json"),
            "Repaired runtime metadata remained excluded from recovery.");
    }

    private static void QuarantinePreservesEvidenceAndPins(string root)
    {
        var engines = Path.Combine(root, "engines"); var deployment = Path.Combine(root, "deployment");
        Directory.CreateDirectory(deployment);
        ProcessingEngineLease Engine(byte value)
        {
            File.WriteAllBytes(Path.Combine(deployment, "fixture.dll"), new[] { value });
            return ProcessingEngineLease.CaptureFiles(engines, deployment, new[] { "fixture.dll" });
        }
        var current = Engine(1); var retained = Engine(2); var conservative = Engine(3);
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), current.Identity);
        var exact = Guid.NewGuid(); var legacy = Guid.NewGuid(); var empty = Guid.NewGuid();
        string DirectoryFor(Guid id) => Path.Combine(store.Root, id.ToString("N"));
        foreach (var id in new[] { exact, legacy, empty }) Directory.CreateDirectory(DirectoryFor(id));
        var original = new byte[] { 0, 255, 123, 13, 10, 0, 71 };
        File.WriteAllBytes(Path.Combine(DirectoryFor(exact), "manifest.json"), original);
        File.WriteAllBytes(Path.Combine(DirectoryFor(legacy), "interrupted-input.bin"), original);
        EngineReference.Write(DirectoryFor(exact), retained.Identity);

        Check(store.ReadForRecovery(exact, out var exactPins) == null && exactPins.SequenceEqual(new[] { retained.Identity }),
            "Malformed metadata lost its independent exact engine reference.");
        Check(store.ReadForRecovery(legacy, out var legacyPins) == null && legacyPins.OrderBy(value => value).SequenceEqual(
            new[] { current.Identity, retained.Identity, conservative.Identity }.OrderBy(value => value)),
            "Legacy evidence did not retain the engines present when quarantined.");
        Check(store.ReadForRecovery(empty, out var emptyPins) == null && emptyPins.Length == 0,
            "An empty interrupted episode pinned unrelated engines.");
        var receipts = new[] { exact, legacy, empty }.ToDictionary(id => id,
            id => File.ReadAllBytes(Path.Combine(DirectoryFor(id), "metadata-failure.json")));
        var future = Engine(4);

        // New projections and stores model application restart; a durable receipt
        // remains historical evidence and must not recreate a red service error.
        for (var scan = 0; scan < 2; scan++)
        {
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            var restarted = new EpisodeTransactionStore(store.Root, current.Identity);
            var owner = new RuntimeRetentionOwner(root, "none", restarted,
                (id, reason) => projection.Failure("storage.retention", id, reason),
                id => projection.Recovered("storage.retention", id));
            owner.Collect(); owner.Collect();
            Check(projection.Snapshot.Failure == null, "Retained metadata quarantine recreated an active service failure.");
            foreach (var id in receipts.Keys)
                Check(File.ReadAllBytes(Path.Combine(DirectoryFor(id), "metadata-failure.json")).SequenceEqual(receipts[id]),
                    "An unchanged metadata receipt was rewritten during a scan or restart.");
        }
        Check(File.ReadAllBytes(Path.Combine(DirectoryFor(exact), "manifest.json")).SequenceEqual(original) &&
            File.ReadAllBytes(Path.Combine(DirectoryFor(legacy), "interrupted-input.bin")).SequenceEqual(original) &&
            !File.Exists(Path.Combine(DirectoryFor(empty), "manifest.json")), "Quarantine altered source bytes or invented missing metadata.");
        Check(Directory.Exists(retained.DirectoryPath) && Directory.Exists(conservative.DirectoryPath) && !Directory.Exists(future.DirectoryPath),
            "Quarantine released a retained engine or pinned engines created after its receipt.");

        var repaired = new EpisodeWorkManifest { Id = exact, Engine = retained.Identity, ArchiveId = Guid.NewGuid() };
        DurableFile.WriteJson(Path.Combine(DirectoryFor(exact), "manifest.json"), repaired);
        Check(store.ReadForRecovery(exact, out _)?.Id == exact && !RuntimeMetadataFailure.Unchanged(DirectoryFor(exact), "manifest.json"),
            "Repaired metadata remained excluded by its old quarantine receipt.");
    }

    private static async Task EpisodeOwnerResumesRepairedMetadata(string root)
    {
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
        var id = Guid.NewGuid(); var manifest = ReadyReport(store, id, repository, gate);
        var directory = Path.Combine(store.Root, id.ToString("N")); var path = Path.Combine(directory, "manifest.json");
        var valid = File.ReadAllBytes(path); File.WriteAllText(path, "broken episode metadata");
        var orphan = Path.Combine(store.Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(orphan);
        var archives = new BugReportRepository(Path.Combine(root, "archives"));
        var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives.RootPath);
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask); var failures = 0;
        var processor = new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), repository, gate);
        EpisodeRuntimeOwner Owner(EpisodeTransactionStore source)
        {
            var value = new EpisodeRuntimeOwner(source, new EpisodeWorkCoordinator(source, processor, repository, gate), archives, outbox, projection.Episode);
            value.Failed += (episode, reason) => { Interlocked.Increment(ref failures); projection.Failure("analysis.episodes", episode, reason); };
            value.Recovered += episode => projection.Recovered("analysis.episodes", episode);
            return value;
        }
        var first = Owner(store);
        using (var generation = new ManagedComponentGeneration(1, first.RunAsync))
        {
            try
            {
                await generation.StartAsync(CancellationToken.None);
                await Until(() => File.Exists(Path.Combine(directory, "metadata-failure.json")) && File.Exists(Path.Combine(orphan, "metadata-failure.json")),
                    "Episode owner did not quarantine corrupt and missing metadata.");
            }
            finally { await generation.StopAsync(TimeSpan.FromSeconds(10)); }
        }
        Check(failures == 0 && projection.Snapshot.Failure == null && !store.HasFailed(id),
            "Metadata quarantine became an analysis error or a terminal episode marker.");

        var restartedStore = new EpisodeTransactionStore(store.Root, store.Engine); var restarted = Owner(restartedStore);
        using (var generation = new ManagedComponentGeneration(2, restarted.RunAsync))
        {
            try
            {
                await generation.StartAsync(CancellationToken.None);
                File.WriteAllBytes(path, valid); restarted.Wake();
                await Until(() => restartedStore.Read(id).Stage == EpisodeWorkStage.Completed,
                    "Episode owner did not resume the repaired transaction.");
            }
            finally { await generation.StopAsync(TimeSpan.FromSeconds(10)); }
        }
        Check(failures == 0 && projection.Snapshot.Failure == null && !restartedStore.HasFailed(id) &&
            restartedStore.Read(id).InputHash == manifest.InputHash && projection.Snapshot.Episodes.Any(value => value.Episode == id && value.ReportReady),
            "Repaired episode lost immutable inputs or failed to publish its completed report.");
    }

    private static async Task ReceiptFailureCanRecover(string root)
    {
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object(); var id = Guid.NewGuid();
        ReadyReport(store, id, repository, gate);
        var directory = Path.Combine(store.Root, id.ToString("N")); var path = Path.Combine(directory, "manifest.json");
        var valid = File.ReadAllBytes(path); File.WriteAllText(path, "broken metadata with inaccessible quarantine receipt");
        var receipt = Path.Combine(directory, "metadata-failure.json"); File.WriteAllText(receipt, "interrupted receipt");
        using var held = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.None);
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        var processor = new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), repository, gate);
        var archives = new BugReportRepository(Path.Combine(root, "archives"));
        var owner = new EpisodeRuntimeOwner(store, new EpisodeWorkCoordinator(store, processor, repository, gate), archives,
            new DurableOutbox(Path.Combine(root, "outbox"), archives.RootPath), projection.Episode);
        owner.Failed += (episode, reason) => projection.Failure("analysis.episodes", episode, reason);
        owner.Recovered += episode => projection.Recovered("analysis.episodes", episode);
        using var generation = new ManagedComponentGeneration(1, owner.RunAsync);
        try
        {
            await generation.StartAsync(CancellationToken.None);
            await Until(() => projection.Snapshot.Failure?.Entity == id, "Blocked quarantine did not expose its real storage failure.");
            Check(!store.HasFailed(id) && !File.Exists(Path.Combine(directory, "failure.json")),
                "Failed quarantine publication created a terminal episode marker.");
            held.Dispose(); owner.Wake();
            await Until(() => RuntimeMetadataFailure.Unchanged(directory, "manifest.json") && projection.Snapshot.Failure == null,
                "Successful quarantine did not retire the earlier storage failure.");
            Check(File.ReadAllText(path) == "broken metadata with inaccessible quarantine receipt" &&
                File.ReadAllText(Directory.GetFiles(directory, "metadata-failure.json.corrupt-*").Single()) == "interrupted receipt",
                "Quarantine recovery modified the original metadata or discarded its damaged receipt.");
            File.WriteAllBytes(path, valid); owner.Wake();
            await Until(() => store.Read(id).Stage == EpisodeWorkStage.Completed, "Receipt failure poisoned later metadata repair.");
            Check(projection.Snapshot.Failure == null && !store.HasFailed(id), "Repaired metadata retained an active or terminal failure.");
        }
        finally { held.Dispose(); await generation.StopAsync(TimeSpan.FromSeconds(10)); }
    }

    private static void IntegrationResumesRepairedMetadata(string root)
    {
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
        var id = Guid.NewGuid(); var manifest = ReadyReport(store, id, repository, gate);
        var path = Path.Combine(store.Root, id.ToString("N"), "manifest.json");
        var valid = File.ReadAllBytes(path); File.WriteAllText(path, "broken integration metadata");
        var events = Path.Combine(root, "events"); var failures = 0;
        void Scan()
        {
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            var data = new IntegrationApiData(events, repository, gate, new EpisodeTransactionStore(store.Root, store.Engine),
                () => projection.Snapshot, () => Array.Empty<SupervisionSnapshot>());
            for (var index = 0; index < 3; index++) data.ReconcileCommitted(CancellationToken.None,
                (episode, reason) => { failures++; projection.Failure("integration.api", episode, reason); },
                episode => projection.Recovered("integration.api", episode));
            Check(projection.Snapshot.Failure == null, "Integration scan promoted quarantined metadata to a persistent failure.");
        }
        Scan(); Scan();
        Check(failures == 0 && !new IntegrationEventStore(events).CoversCapture(id, manifest.TimestampUtc, 2),
            "Invalid metadata produced an integration event or service failure.");
        File.WriteAllBytes(path, valid); Scan();
        Check(failures == 0 && new IntegrationEventStore(events).CoversCapture(id, manifest.TimestampUtc, 2),
            "Integration reconciliation did not publish the repaired episode's final revision.");
    }

    private static async Task RestoredIdleMetadataClearsReadFailure(string root)
    {
        foreach (var stage in new[] { EpisodeWorkStage.Created, EpisodeWorkStage.Completed })
        {
            var scenario = Path.Combine(root, stage.ToString());
            var store = new EpisodeTransactionStore(Path.Combine(scenario, "episodes"), new string('a', 64));
            var repository = new LandingRepository(Path.Combine(scenario, "history")); var gate = new object(); var id = Guid.NewGuid();
            var directory = Path.Combine(store.Root, id.ToString("N")); var path = Path.Combine(directory, "manifest.json");
            var manifest = stage == EpisodeWorkStage.Created
                ? new EpisodeWorkManifest { Id = id, Engine = store.Engine, ArchiveId = Guid.NewGuid() }
                : ReadyReport(store, id, repository, gate);
            if (stage == EpisodeWorkStage.Completed)
            {
                store.CommitArchive(manifest, null, "capture_opted_out", 0); store.MarkOutboxEnqueued(manifest);
                // Model the durable completion marker before its independent
                // archive-directory move, so the recovery owner encounters it.
                manifest.Stage = EpisodeWorkStage.Completed;
            }
            DurableFile.WriteJson(path, manifest); var valid = File.ReadAllBytes(path);
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            var processor = new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), repository, gate);
            var archives = new BugReportRepository(Path.Combine(scenario, "archives")); var updates = 0;
            var owner = new EpisodeRuntimeOwner(store, new EpisodeWorkCoordinator(store, processor, repository, gate), archives,
                new DurableOutbox(Path.Combine(scenario, "outbox"), archives.RootPath), _ => Interlocked.Increment(ref updates));
            owner.Failed += (episode, reason) => projection.Failure("analysis.episodes", episode, reason);
            owner.Recovered += episode => projection.Recovered("analysis.episodes", episode);
            using var generation = new ManagedComponentGeneration(1, owner.RunAsync);
            try
            {
                await generation.StartAsync(CancellationToken.None);
                await Until(() => projection.Snapshot.Failure?.Entity == id, stage + " metadata read failure was not reported.");
                held.Dispose(); owner.Wake();
                await Until(() => projection.Snapshot.Failure == null, stage + " metadata retained a resolved read failure.");
                Check(store.Read(id).Stage == stage && File.ReadAllBytes(path).SequenceEqual(valid) && updates == 0 && !store.HasFailed(id),
                    stage + " metadata recovery rewrote its source, replayed completed work or created a terminal failure.");
            }
            finally { held.Dispose(); await generation.StopAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    private static async Task InspectionWaitsForInputCommit(string root)
    {
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
        var id = Guid.NewGuid(); var directory = Path.Combine(store.Root, id.ToString("N")); Directory.CreateDirectory(directory);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var samples = new BlockingSamples(entered, release);
        var commit = Task.Run(() => store.CommitInputs(Episode(id, samples), DateTime.UtcNow, Array.Empty<AirportFacility>(), false, 0));
        Task<EpisodeWorkManifest?>? inspect = null;
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "Input commit did not enter its owned section.");
            var inspectionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            inspect = Task.Run(() => { inspectionStarted.SetResult(true); return store.ReadForRecovery(id, out _); });
            await inspectionStarted.Task;
            // While the commit is deliberately held, inspection must await that
            // owner instead of stamping its not-yet-published manifest as missing.
            Check(await Task.WhenAny(inspect, Task.Delay(250)) != inspect && !File.Exists(Path.Combine(directory, "metadata-failure.json")),
                "Metadata inspection quarantined an episode whose input commit still owned it.");
        }
        finally { release.Set(); await commit; if (inspect != null) await inspect; }
        Check(inspect?.Result?.Stage == EpisodeWorkStage.InputsCommitted && !File.Exists(Path.Combine(directory, "metadata-failure.json")),
            "Inspection failed to observe the completed input commit after ownership was released.");
    }

    private sealed class BlockingSamples : IReadOnlyList<TelemetrySample>
    {
        private readonly ManualResetEventSlim _entered, _release;
        private readonly TelemetrySample _sample = new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true };
        public BlockingSamples(ManualResetEventSlim entered, ManualResetEventSlim release) { _entered = entered; _release = release; }
        public int Count { get { _entered.Set(); if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture commit was not released."); return 1; } }
        public TelemetrySample this[int index] => index == 0 ? _sample : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<TelemetrySample> GetEnumerator() { yield return _sample; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static EpisodeWorkManifest ReadyReport(EpisodeTransactionStore store, Guid id, LandingRepository repository, object gate)
    {
        var episode = Episode(id); var timestamp = DateTime.UtcNow;
        var manifest = store.CommitInputs(episode, timestamp, Array.Empty<AirportFacility>(), false, 0);
        var record = new LandingRecord { Id = id.ToString("N"), RecordingEpisodeId = "physical:" + id.ToString("N"),
            TimestampUtc = timestamp, AircraftTitle = "Metadata fixture", ContactNumber = 1, ContactCount = 1 };
        var result = ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { record }, new TelemetryDecisionJournal().Snapshot());
        store.CommitAnalysis(manifest, result); store.SaveHistory(manifest, repository, gate);
        store.CommitFacilities(new RuntimeFinalFacilities(id, episode.InitialFacilities, null));
        store.CommitReport(manifest, result, repository, gate);
        return manifest;
    }
    private static LandingEpisodeEventArgs Episode(Guid id, IReadOnlyList<TelemetrySample>? samples = null) =>
        new LandingEpisodeEventArgs(1, samples ?? new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true } },
            "MSFS", "Metadata fixture", "TEST", "TEST", Array.Empty<AirportFacility>(), Array.Empty<string>(), persistentId: id);
    private static async Task Until(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(message); await Task.Delay(25); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
