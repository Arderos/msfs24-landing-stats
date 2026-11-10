using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.Models;
using LandingStats.Core;
using LandingStats.App.TelemetryUpload;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RuntimeFailureLifecycleRegressionTests
{
    public static void RunAll() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-failure-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await IndependentLegacyFailures(Path.Combine(root, "legacy"));
            await LegacyJournalCompatibility(Path.Combine(root, "legacy-journal"));
            ConsentSnapshot(Path.Combine(root, "consent"));
            await OutboxRecovery(Path.Combine(root, "outbox"));
            await RetirementOwnership(Path.Combine(root, "retirement"));
            RetentionRecovery(Path.Combine(root, "retention"));
            await CatalogRecovery();
            await IntegrationRecovery(Path.Combine(root, "integration"));
            IntegrationEpisodeRecovery(Path.Combine(root, "integration-episodes"));
        }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }).GetAwaiter().GetResult();

    private static RuntimePresentation Projection() => new RuntimePresentation((_, __) => Task.CompletedTask);
    private static string Archive(string root, string name, byte value)
    { Directory.CreateDirectory(root); var path = Path.Combine(root, name); File.WriteAllBytes(path, new[] { value, (byte)2, (byte)3 }); return path; }
    private static async Task IndependentLegacyFailures(string root)
    {
        var legacy = Path.Combine(root, "legacy");
        var first = Archive(legacy, "first_bug_raw.zip", 1); var second = Archive(legacy, "second_bug_raw.zip", 4);
        var firstHash = EpisodeTransactionStore.Hash(first); var secondHash = EpisodeTransactionStore.Hash(second);
        var box = new DurableOutbox(Path.Combine(root, "outbox"), Path.Combine(root, "archives"));
        var projection = Projection(); var failed = new ConcurrentQueue<Guid>(); var recovered = new ConcurrentQueue<Guid>();
        var intake = new ArchiveIntakeOwner(Path.Combine(root, "intake"), Path.Combine(root, "archives"), legacy, Path.Combine(root, "identity"),
            new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64)), box, () => { });
        intake.Failed += (id, reason) => { failed.Enqueue(id); projection.Failure("storage.reports", id, reason); };
        intake.Recovered += id => { recovered.Enqueue(id); projection.DeliveryRecovered(id); };
        var lockedFirst = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None);
        var lockedSecond = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.None);
        using var owner = new ManagedComponentGeneration(1, intake.RunAsync);
        try
        {
            await owner.StartAsync(CancellationToken.None);
            await Until(() => failed.Count == 2, "Two real blocked legacy files did not publish independently.");
            Check(failed.Distinct().Count() == 2 && failed.All(id => id != Guid.Empty), "Legacy errors share an empty identity.");
            lockedFirst.Dispose();
            await Until(() => !File.Exists(first) && recovered.Count == 1, "First legacy file did not recover.");
            Check(projection.Snapshot.DeliveryFailure != null && projection.Snapshot.DeliveryFailure.Entity != recovered.Single(), "One recovery erased the other blocked file.");
            lockedSecond.Dispose();
            await Until(() => !File.Exists(second) && projection.Snapshot.DeliveryFailure == null, "Resolved legacy errors remained visible.");
            Check(box.Read(Guid.ParseExact(firstHash.Substring(0, 32), "N")).Hash == firstHash && box.Read(Guid.ParseExact(secondHash.Substring(0, 32), "N")).Hash == secondHash, "Intake changed retained bytes.");
            Archive(legacy, "first_bug_raw.zip", 9);
            await Until(() => !File.Exists(first) && box.Enumerate().Count == 3, "Reused legacy path retained an obsolete hash.");
            Check(EpisodeTransactionStore.Hash(box.Read(Guid.ParseExact(firstHash.Substring(0, 32), "N")).Path) == firstHash &&
                box.Enumerate().Select(box.Read).All(entry => !entry.Automatic && entry.Path.EndsWith("_bug_raw.zip", StringComparison.OrdinalIgnoreCase)),
                "Reused legacy name overwrote older archive bytes or changed report classification.");
        }
        finally { lockedFirst.Dispose(); lockedSecond.Dispose(); await owner.StopAsync(TimeSpan.FromSeconds(10)); }
    }
    private static async Task LegacyJournalCompatibility(string root)
    {
        var legacy = Path.Combine(root, "legacy"); var archives = Path.Combine(root, "archives"); var requests = Path.Combine(root, "intake");
        var box = new DurableOutbox(Path.Combine(root, "outbox"), archives); var expected = new ArchiveIntakeRequest[3];
        var suffixes = new[] { "_bug_raw.zip", "_auto_raw.zip", "_raw.zip" };
        for (var stage = 0; stage < expected.Length; stage++)
        {
            var name = "earlier-" + stage + suffixes[stage]; var source = Archive(legacy, name, (byte)(stage + 10));
            var destination = Archive(archives, name, (byte)(stage + 10)); var hash = EpisodeTransactionStore.Hash(source);
            var id = Guid.ParseExact(hash.Substring(0, 32), "N");
            expected[stage] = new ArchiveIntakeRequest { Id = id, Hash = hash, LegacyName = name, ArchiveName = stage == 0 ? "" : name,
                Automatic = stage != 0, Stage = stage, TimestampUtcTicks = DateTime.UtcNow.Ticks };
            DurableFile.WriteJson(Path.Combine(requests, id.ToString("N") + ".json"), expected[stage]);
            if (stage == 2) box.Register(id, destination, hash, true, DateTime.UtcNow);
        }
        var intake = new ArchiveIntakeOwner(requests, archives, legacy, Path.Combine(root, "identity"),
            new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64)), box, () => { });
        using var owner = new ManagedComponentGeneration(1, intake.RunAsync); await owner.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => !Directory.GetFiles(legacy, "*_raw.zip").Any() && !Directory.GetFiles(requests, "*.json").Any(), "Earlier intake journals did not finish.");
            Check(Directory.GetFiles(archives, "*.zip").Length == 3, "Stage 0 crash recovery duplicated a previously copied archive.");
            foreach (var request in expected)
            {
                var entry = box.Read(request.Id);
                Check(Path.GetFileName(entry.Path) == request.LegacyName && entry.Hash == request.Hash && entry.Automatic == request.Automatic,
                    "Earlier Stage 0/1/2 archive reference or report kind changed.");
            }
        }
        finally { await owner.StopAsync(TimeSpan.FromSeconds(10)); }
    }
    private static void ConsentSnapshot(string root)
    {
        var legacy = Path.Combine(root, "legacy");
        for (var index = 0; index < 64; index++) Archive(legacy, index + "_raw.zip", (byte)index);
        var identity = new TelemetryUploadIdentityStore(Path.Combine(root, "identity")); var reads = 0;
        var intake = new ArchiveIntakeOwner(Path.Combine(root, "intake"), Path.Combine(root, "archives"), legacy, Path.Combine(root, "identity"),
            new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64)),
            new DurableOutbox(Path.Combine(root, "outbox"), Path.Combine(root, "archives")), () => { },
            readLegacyConsent: () => { reads++; return identity.ConsentAccepted; });
        var context = new ManagedComponentContext(1, CancellationToken.None);
        var sweep = typeof(ArchiveIntakeOwner).GetMethod("AdoptLegacy", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            sweep.Invoke(intake, new object[] { context });
            Check(reads == 1 && !Directory.Exists(Path.Combine(root, "intake")), "Opted-out sweep reread consent per file or adopted unapproved files.");
            identity.AcceptConsent(); sweep.Invoke(intake, new object[] { context });
            Check(reads == 2 && Directory.GetFiles(Path.Combine(root, "intake"), "*.json").Length == 64, "Next sweep did not read fresh consent exactly once.");
        }
        finally { context.Dispose(); }
    }
    private static async Task OutboxRecovery(string root)
    {
        var archives = Path.Combine(root, "archives"); var box = new DurableOutbox(Path.Combine(root, "entries"), archives);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        foreach (var id in new[] { first, second })
        {
            var path = Archive(archives, id.ToString("N") + ".zip", 7);
            box.Register(id, path, EpisodeTransactionStore.Hash(path), false, DateTime.UtcNow);
        }
        var projection = Projection();
        box.RecoveryFailed += (id, error) => projection.Failure("delivery.outbox", id, error);
        var recovered = new ConcurrentQueue<Guid>();
        box.Recovered += id => { recovered.Enqueue(id); projection.Recovered("delivery.outbox", id); };
        string Entry(Guid id) => Path.Combine(root, "entries", id.ToString("N") + ".json");
        var originalFirst = File.ReadAllBytes(Entry(first)); var originalSecond = File.ReadAllBytes(Entry(second));
        File.WriteAllText(Entry(first), "broken first"); File.WriteAllText(Entry(second), "broken second");
        box.RecoverTerminalEntries();
        Check(box.HasQuarantinedEntries && !box.HasRecoveryFailures && projection.Snapshot.DeliveryFailure == null, "Damaged metadata was presented as retryable file access.");
        var diagnostics = box.RecoveryErrors.Count;
        for (var index = 0; index < 10; index++) box.RetryRecoveryFailures();
        Check(box.RecoveryErrors.Count == diagnostics, "Unchanged corruption bypassed retry pacing/deduplication.");
        File.WriteAllBytes(Entry(first), originalFirst);
        await Until(() => { box.RetryRecoveryFailures(); return recovered.Contains(first); }, "Repair of one outbox entry did not complete its own work.");
        Check(box.HasQuarantinedEntries && File.Exists(Entry(second)), "Pending corrupt entry was dropped.");
        File.WriteAllBytes(Entry(second), originalSecond);
        await Until(() => { box.RetryRecoveryFailures(); return !box.HasQuarantinedEntries && !box.HasRecoveryFailures; }, "Repaired outbox did not complete its failed operations.");
        Check(projection.Snapshot.DeliveryFailure == null, "Resolved outbox failure remained visible.");
        var claim = box.TryClaim(first, new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload))!;
        File.SetAttributes(claim.Path, FileAttributes.ReadOnly);
        try
        {
            try { box.Acknowledge(claim); throw new InvalidOperationException("Read-only archive unexpectedly allowed delivery cleanup."); }
            catch (UnauthorizedAccessException) { }
            box.RecoverTerminalEntries(); Check(box.HasRecoveryFailures, "Real terminal cleanup denial was not retained.");
            Check(box.Read(first).State == OutboxState.Delivered && box.HasRecoveryFailures, "Successful read cleared failed cleanup.");
            File.SetAttributes(claim.Path, FileAttributes.Normal);
            await Until(() => { box.RetryRecoveryFailures(); return !box.HasRecoveryFailures; }, "Terminal cleanup did not recover.");
            Check(!File.Exists(claim.Path) && projection.Snapshot.DeliveryFailure == null, "Terminal cleanup did not clear its exact notice.");
        }
        finally { if (File.Exists(claim.Path)) File.SetAttributes(claim.Path, FileAttributes.Normal); }
    }
    private static async Task RetirementOwnership(string root)
    {
        var box = new DurableOutbox(Path.Combine(root, "outbox"), Path.Combine(root, "archives"));
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        foreach (var id in new[] { first, second })
        {
            var path = Archive(Path.Combine(root, "archives"), id.ToString("N") + ".zip", 3);
            box.Register(id, path, EpisodeTransactionStore.Hash(path), false, DateTime.UtcNow);
        }
        var session = Guid.NewGuid();
        var firstWorker = new WorkerIdentity(session, Guid.NewGuid(), 1, ComponentRole.Upload);
        var secondWorker = new WorkerIdentity(session, Guid.NewGuid(), 2, ComponentRole.Upload);
        box.TryClaim(first, firstWorker); box.TryClaim(second, secondWorker);
        var pathToEntry = Path.Combine(root, "outbox", first.ToString("N") + ".json"); var original = File.ReadAllBytes(pathToEntry);
        File.WriteAllText(pathToEntry, "broken claimed entry");
        box.RetireConfirmedWorker(firstWorker, first);
        Check(box.HasQuarantinedEntries && !box.HasRecoveryFailures, "Broken claim was not retained in quarantine with its retirement obligation.");
        // Pass the first backoff boundary so the former bulk API would replace
        // the first worker's pending action while retiring its healthy neighbor.
        await Task.Delay(1100);
        box.RetireConfirmedWorker(secondWorker, second);
        Check(box.Read(second).State == OutboxState.Pending && box.HasQuarantinedEntries, "Unrelated retirement erased the damaged claim obligation.");
        File.WriteAllBytes(pathToEntry, original);
        var successor = new WorkerIdentity(session, Guid.NewGuid(), 3, ComponentRole.Upload);
        Check(box.TryClaim(first, successor) == null, "Repaired old claim admitted a new owner before retirement completed.");
        await Until(() => { box.RetryRecoveryFailures(); return !box.HasQuarantinedEntries && !box.HasRecoveryFailures; }, "Confirmed first-worker retirement did not retry.");
        Check(box.Read(first).State == OutboxState.Pending && box.TryClaim(first, successor) != null, "Retirement lost the original worker proof or fenced its successor forever.");
        box.RetryRecoveryFailures();
        Check(box.Read(first).State == OutboxState.Claimed && box.Read(first).WorkerInstance == successor.InstanceId, "Completed old recovery revoked a successor's claim.");
    }
    private static void RetentionRecovery(string root)
    {
        var engine = new string('a', 64); Directory.CreateDirectory(Path.Combine(root, "engines", engine));
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), engine); var projection = Projection();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) { var directory = Path.Combine(store.Root, id.ToString("N")); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "manifest.json"), "broken"); }
        var owner = new RuntimeRetentionOwner(root, "none", store, (id, reason) => projection.Failure("storage.retention", id, reason), id => projection.Recovered("storage.retention", id));
        // A completed quarantine is retained evidence. Only inability to persist
        // that isolation is an active retention failure.
        var ordered = store.EnumeratePending().ToArray();
        string Receipt(Guid id) => Path.Combine(store.Root, id.ToString("N"), "metadata-failure.json");
        using var second = new FileStream(Receipt(ordered[1]), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        owner.Collect(); Check(projection.Snapshot.Failure?.Entity == ordered[1], "Blocked quarantine was not visible.");
        using var first = new FileStream(Receipt(ordered[0]), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        owner.Collect(); Check(projection.Snapshot.Failure?.Entity == ordered[0], "Independent quarantine failure was lost.");
        first.Dispose(); owner.Collect(); Check(projection.Snapshot.Failure?.Entity == ordered[1], "Isolating one source hid another blocked source.");
        second.Dispose(); owner.Collect(); Check(projection.Snapshot.Failure == null, "Successful quarantine retained a stale service error.");
    }
    private static async Task CatalogRecovery()
    {
        var projection = Projection(); var calls = 0;
        var catalog = new GeometryCatalogRuntimeOwner((_, __) => ++calls == 1 ? Task.FromException(new IOException("transient catalog read")) : Task.CompletedTask,
            error => projection.Failure("analysis.catalog", Guid.Empty, error.GetType().Name), () => projection.Recovered("analysis.catalog", Guid.Empty));
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Simulator); long sequence = 0;
        void Observe(RuntimePayload payload) => catalog.Observe(new RecordingRuntimeEffects { SourceObservation = new RuntimeEnvelope(worker, 1, ++sequence, Stopwatch.GetTimestamp(), payload) });
        using var generation = new ManagedComponentGeneration(1, catalog.RunAsync); await generation.StartAsync(CancellationToken.None);
        try
        {
            void Connect() { Observe(new LifecycleMessage(SourceLifecycle.Connected)); Observe(new AircraftMessage("fixture", "Aircraft", "", "")); Observe(new LifecycleMessage(SourceLifecycle.PhysicsReady)); }
            Connect(); Check(!await catalog.WaitReadyAsync("fixture", CancellationToken.None), "Failed catalog became ready.");
            await Until(() => projection.Snapshot.Failure != null, "Catalog failure was not published.");
            projection.Failure("analysis.episodes", Guid.NewGuid(), "persistent episode");
            Observe(new LifecycleMessage(SourceLifecycle.Disconnected)); Connect();
            Check(await catalog.WaitReadyAsync("fixture", CancellationToken.None), "Second catalog refresh did not finish.");
            Check(projection.Snapshot.Failure?.Message == "persistent episode", "Catalog recovery erased unrelated failed analysis.");
            projection.Recovered("analysis.episodes", projection.Snapshot.Failure!.Entity);
            Check(projection.Snapshot.Failure == null && calls == 2, "Catalog error resurfaced after proven recovery.");
        }
        finally { await generation.StopAsync(TimeSpan.FromSeconds(10)); }
    }
    private static async Task IntegrationRecovery(string root)
    {
        var journalRoot = Path.Combine(root, "events"); _ = new IntegrationEventStore(journalRoot).StreamId;
        var held = new FileStream(Path.Combine(journalRoot, "events-v1.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        var projection = Projection(); var recovered = 0;
        var repository = new LandingRepository(Path.Combine(root, "history"));
        var data = new IntegrationApiData(journalRoot, repository, new object(), new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64)),
            () => projection.Snapshot, () => Array.Empty<SupervisionSnapshot>());
        var api = new IntegrationApiRuntimeOwner(() => data, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), Guid.NewGuid(),
            new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency), (_, __) => { }, _ => { },
            error => projection.Failure("integration.api", Guid.Empty, error), new ComponentRecoveryOptions(), _ => "review-api-" + Guid.NewGuid().ToString("N"),
            () => { projection.Recovered("integration.api", Guid.Empty); Interlocked.Increment(ref recovered); });
        using var generation = new ManagedComponentGeneration(1, api.RunAsync);
        try
        {
            await generation.StartAsync(CancellationToken.None); await api.SetEnabledAsync(true);
            await Until(() => projection.Snapshot.Failure?.Component == "integration.api", "Real integration journal lock did not publish its failure.");
            var independent = Guid.NewGuid(); projection.Failure("storage.retention", independent, "persistent metadata");
            held.Dispose(); await Until(() => Volatile.Read(ref recovered) > 0, "Successful journal reconciliation did not retire its failure.");
            Check(projection.Snapshot.Failure?.Entity == independent, "Journal recovery erased independent metadata failure.");
            projection.Recovered("storage.retention", independent);
            Check(projection.Snapshot.Failure == null, "Integration service error resurfaced after recovery.");
        }
        finally { held.Dispose(); await api.StopAsync(); await generation.StopAsync(TimeSpan.FromSeconds(10)); }
    }
    private static void IntegrationEpisodeRecovery(string root)
    {
        var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object(); var projection = Projection();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }; var bytes = new byte[2][];
        string Analysis(Guid id) => Path.Combine(store.Root, id.ToString("N"), "analysis.json");
        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index]; var record = new LandingRecord { Id = id.ToString("N"), RecordingEpisodeId = "physical:" + id.ToString("N"),
                TimestampUtc = DateTime.UtcNow, AircraftTitle = "Fixture", ContactNumber = 1, ContactCount = 1 };
            var episode = new LandingEpisodeEventArgs(1, new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 } }, "MSFS", "Fixture", "TEST", "TEST",
                Array.Empty<AirportFacility>(), Array.Empty<string>(), new TelemetryDecisionJournal(), persistentId: id);
            var manifest = store.CommitInputs(episode, record.TimestampUtc, Array.Empty<AirportFacility>(), false, 1);
            store.CommitAnalysis(manifest, ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { record }, new TelemetryDecisionJournal().Snapshot()));
            bytes[index] = File.ReadAllBytes(Analysis(id)); File.WriteAllText(Analysis(id), "broken");
        }
        void ReconcileGeneration()
        {
            var data = new IntegrationApiData(Path.Combine(root, "events"), repository, gate, store, () => projection.Snapshot, () => Array.Empty<SupervisionSnapshot>());
            for (var index = 0; index < 3; index++) data.ReconcileCommitted(CancellationToken.None,
                (id, error) => projection.Failure("integration.api", id, error), id => projection.Recovered("integration.api", id));
        }
        ReconcileGeneration(); Check(projection.Snapshot.Failure != null, "Corrupt persisted integration episode was hidden.");
        File.WriteAllBytes(Analysis(ids[0]), bytes[0]); ReconcileGeneration();
        Check(projection.Snapshot.Failure?.Entity == ids[1], "One reconciled capture cleared another failed capture.");
        File.WriteAllBytes(Analysis(ids[1]), bytes[1]); ReconcileGeneration();
        Check(projection.Snapshot.Failure == null, "Durably reconciled capture retained its failure.");
    }
    private static async Task Until(Func<bool> condition, string message)
    { var until = Stopwatch.StartNew(); while (!condition()) { if (until.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(message); await Task.Delay(25); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
