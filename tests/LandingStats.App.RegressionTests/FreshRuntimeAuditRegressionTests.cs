using System;
using System.Collections;
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

internal static class FreshRuntimeAuditRegressionTests
{
    public static void BusyIntegrationCaptureKeepsItsFailureUntilPublished()
    {
        WithRoot(root =>
        {
            var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
            var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
            var id = Guid.NewGuid(); var independent = Guid.NewGuid(); ReadyReport(store, id, repository, gate);
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            projection.Failure("integration.api", independent, "Independent retained failure.");
            var data = new IntegrationApiData(Path.Combine(root, "events"), repository, gate, store,
                () => projection.Snapshot, () => Array.Empty<SupervisionSnapshot>());
            var failures = 0; var recoveries = 0;
            void Failed(Guid episode, string error) { projection.Failure("integration.api", episode, error); if (episode == id) failures++; }
            void Recovered(Guid episode) { projection.Recovered("integration.api", episode); if (episode == id) recoveries++; }
            var scan = new BlockingRepositoryEnumerator(id, gate);
            typeof(IntegrationApiData).GetField("_scan", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(data, scan);
            Exception? escaped = null; var completed = false;
            try { completed = data.ReconcileCommitted(CancellationToken.None, Failed, Recovered); }
            catch (IntegrationApiException error) { escaped = error; }
            finally { scan.Release(); }
            Check(escaped == null, "A busy capture escaped as an owner-level API failure after the iterator had consumed it.");
            if (!completed) completed = data.ReconcileCommitted(CancellationToken.None, Failed, Recovered);
            Check(completed && failures == 1 && recoveries == 0 && projection.Snapshot.Failure?.Entity == id,
                "Completing the scan hid an unpublished capture or incorrectly declared it recovered.");
            var pending = JObject.Parse(data.Handle("{\"id\":\"test\",\"protocolVersion\":1,\"method\":\"events.read\"}"));
            Check(((JArray)pending["result"]!["events"]!).Count == 0, "Blocked capture unexpectedly published before its gate became available.");
            Check(!data.ReconcileCommitted(CancellationToken.None, Failed, Recovered) && recoveries == 0,
                "An idle scan tick retired an unpublished capture's failure.");
            // Make the scheduled next scan due without sleeping for its interval.
            typeof(IntegrationApiData).GetField("_nextScan", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(data, DateTime.MinValue);
            data.ReconcileCommitted(CancellationToken.None, Failed, Recovered);
            Check(recoveries == 1 && projection.Snapshot.Failure?.Entity == independent,
                "Publishing the repaired capture did not retire only its own failure.");
            var published = JObject.Parse(data.Handle("{\"id\":\"test\",\"protocolVersion\":1,\"method\":\"events.read\"}"));
            Check(((JArray)published["result"]!["events"]!).Count == 2, "Retry failed to publish both durable capture revisions.");
        });
    }

    private sealed class BlockingRepositoryEnumerator : IEnumerator<Guid>
    {
        private readonly Guid _id;
        private readonly object _gate;
        private readonly ManualResetEventSlim _entered = new ManualResetEventSlim();
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
        private Task? _holder;
        public BlockingRepositoryEnumerator(Guid id, object gate) { _id = id; _gate = gate; }
        public Guid Current => _id;
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            if (_holder != null) return false;
            // CheckDeletions has already released the repository gate. A real
            // history writer can acquire it here, before this capture publishes.
            _holder = Task.Run(() => { lock (_gate) { _entered.Set(); _release.Wait(); } });
            if (!_entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Repository blocker did not acquire its gate.");
            return true;
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { }
        public void Release()
        {
            _release.Set();
            if (_holder != null && !_holder.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Repository blocker did not retire.");
            _entered.Dispose(); _release.Dispose();
        }
    }

    public static void QuarantineRecognizesTimestampPreservingRepair()
    {
        WithRoot(root =>
        {
            var engine = new string('a', 64); var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(Path.Combine(root, "engines", engine));
            var id = Guid.NewGuid(); var directory = Path.Combine(sessions, id.ToString("N"));
            RuntimeSession.Create(directory, new RecoveryJournalIdentity(id, Guid.NewGuid(),
                new RuntimeClockEpoch(Guid.NewGuid(), 0, 1000)), engine);
            var source = Path.Combine(directory, "runtime.json"); var original = File.ReadAllBytes(source);
            var corrupt = (byte[])original.Clone(); corrupt[0] = (byte)'[';
            File.WriteAllBytes(source, corrupt); var stamp = File.GetLastWriteTimeUtc(source);
            var retention = new RuntimeRetentionOwner(root, "none", new EpisodeTransactionStore(Path.Combine(root, "episodes"), engine));
            retention.Collect();
            Check(RuntimeMetadataFailure.Unchanged(directory, "runtime.json"), "Corrupt source did not acquire a durable quarantine receipt.");
            File.WriteAllBytes(source, original); File.SetLastWriteTimeUtc(source, stamp);
            Check(new FileInfo(source).Length == corrupt.Length && File.GetLastWriteTimeUtc(source) == stamp,
                "Fixture did not preserve the source length and timestamp.");
            retention.Collect();
            Check(RuntimeSessionDirectories.Validate(directory).Runtime == id && !RuntimeMetadataFailure.Unchanged(directory, "runtime.json"),
                "Timestamp-preserving repair remained excluded by the old quarantine receipt.");
            Check(RuntimeSessionDirectories.Read(sessions).Contains(directory) && File.ReadAllBytes(source).SequenceEqual(original),
                "Repaired runtime was not eligible for recovery or its source bytes changed.");

            var oversizedRoot = Path.Combine(root, "oversized"); var oversizedSessions = Path.Combine(oversizedRoot, "sessions");
            Directory.CreateDirectory(Path.Combine(oversizedRoot, "engines", engine));
            var oversizedId = Guid.NewGuid(); var oversizedDirectory = Path.Combine(oversizedSessions, oversizedId.ToString("N"));
            RuntimeSession.Create(oversizedDirectory, new RecoveryJournalIdentity(oversizedId, Guid.NewGuid(),
                new RuntimeClockEpoch(Guid.NewGuid(), 0, 1000)), engine);
            var oversizedSource = Path.Combine(oversizedDirectory, "runtime.json"); var validSmall = File.ReadAllBytes(oversizedSource);
            using (var output = new FileStream(oversizedSource, FileMode.Open, FileAccess.Write, FileShare.None))
                output.SetLength(EpisodeTransactionStore.MaximumManifestBytes + 1L);
            var oversizedBytes = File.ReadAllBytes(oversizedSource); var oversizedStamp = File.GetLastWriteTimeUtc(oversizedSource);
            var oversizedRetention = new RuntimeRetentionOwner(oversizedRoot, "none",
                new EpisodeTransactionStore(Path.Combine(oversizedRoot, "episodes"), engine));
            oversizedRetention.Collect();
            Check(RuntimeMetadataFailure.Unchanged(oversizedDirectory, "runtime.json") && File.ReadAllBytes(oversizedSource).SequenceEqual(oversizedBytes),
                "Oversized corrupt metadata was not quarantined intact.");
            File.WriteAllBytes(oversizedSource, validSmall); File.SetLastWriteTimeUtc(oversizedSource, oversizedStamp);
            oversizedRetention.Collect();
            Check(!RuntimeMetadataFailure.Unchanged(oversizedDirectory, "runtime.json") &&
                RuntimeSessionDirectories.Read(oversizedSessions).Contains(oversizedDirectory) && File.ReadAllBytes(oversizedSource).SequenceEqual(validSmall),
                "Repair from oversized metadata to a valid-sized session remained quarantined or changed restored bytes.");
        });
    }

    public static void QuarantineUpgradesLegacyReceiptsWithoutNewEnginePins()
    {
        WithRoot(root =>
        {
            var directory = Path.Combine(root, "sessions", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "runtime.json"); File.WriteAllText(source, "broken metadata");
            var engines = Path.Combine(root, "engines"); var retained = new string('a', 64); var future = new string('b', 64);
            Directory.CreateDirectory(Path.Combine(engines, retained));
            var marker = Path.Combine(directory, "metadata-failure.json");
            File.WriteAllText(marker, new JObject { ["Version"] = 1,
                ["SourceStamp"] = new FileInfo(source).Length + ":" + File.GetLastWriteTimeUtc(source).Ticks,
                ["Engines"] = new JArray(retained), ["Reason"] = "Legacy quarantined metadata." }.ToString());
            Directory.CreateDirectory(Path.Combine(engines, future));
            Check(!RuntimeMetadataFailure.Unchanged(directory, "runtime.json"), "Legacy timestamp-only receipt suppressed fresh inspection.");
            var pinned = RuntimeMetadataFailure.Pin(directory, "runtime.json", engines, new InvalidDataException("Still corrupt."));
            Check(pinned.SequenceEqual(new[] { retained }) && RuntimeMetadataFailure.Unchanged(directory, "runtime.json"),
                "Receipt upgrade pinned engines created after quarantine or lost its source identity.");
            Check((int?)JObject.Parse(File.ReadAllText(marker))["Version"] == 2, "Legacy receipt did not acquire a content-bound identity.");
        });
    }

    public static void QuarantineReadFailureRemainsVisibleUntilAccessRecovers()
    {
        WithRoot(root =>
        {
            var engine = new string('a', 64); var engines = Path.Combine(root, "engines"); Directory.CreateDirectory(Path.Combine(engines, engine));
            var id = Guid.NewGuid(); var directory = Path.Combine(root, "sessions", id.ToString("N")); Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "runtime.json"); File.WriteAllText(source, "broken metadata");
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            var retention = new RuntimeRetentionOwner(root, "none", new EpisodeTransactionStore(Path.Combine(root, "episodes"), engine),
                (entity, reason) => projection.Failure("storage.retention", entity, reason), entity => projection.Recovered("storage.retention", entity));
            retention.Collect(); var marker = Path.Combine(directory, "metadata-failure.json"); var receipt = File.ReadAllBytes(marker);
            using (var blocked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Check(!RuntimeMetadataFailure.Unchanged(directory, "runtime.json"), "A locked metadata source was declared unchanged without reading it.");
                var threw = false;
                try { RuntimeMetadataFailure.Pin(directory, "runtime.json", engines, new InvalidDataException("Inspection failed.")); }
                catch (IOException) { threw = true; }
                Check(threw && File.ReadAllBytes(marker).SequenceEqual(receipt), "A source read failure was treated as missing or rewrote its evidence receipt.");
                retention.Collect();
                Check(projection.Snapshot.Failure?.Entity == id, "Blocked session metadata inspection did not remain visible as a storage failure.");
            }
            retention.Collect();
            Check(projection.Snapshot.Failure == null && RuntimeMetadataFailure.Unchanged(directory, "runtime.json") && File.ReadAllBytes(marker).SequenceEqual(receipt),
                "Restored access failed to retire the storage notice or changed the existing quarantine receipt.");
        });
    }

    public static async Task EpisodeCompletionRetiresRecoveredFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-fresh-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
            var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
            var id = Guid.NewGuid(); var independent = Guid.NewGuid();
            var manifest = ReadyReport(store, id, repository, gate);
            var archives = new BugReportRepository(Path.Combine(root, "archives"));
            var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives.RootPath);
            var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
            var failedAttempts = 0; var recoveries = 0; var reportNotices = 0;
            FileStream? blocked = null;
            var processor = new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), repository, gate);
            var pipeline = new EpisodeRuntimeOwner(store, new EpisodeWorkCoordinator(store, processor, repository, gate), archives, outbox, update =>
            {
                projection.Episode(update);
                if (update.Report != null)
                {
                    Interlocked.Increment(ref reportNotices);
                    // The committed report is readable, but the next stage cannot
                    // atomically replace its manifest until this reader retires.
                    blocked = new FileStream(Path.Combine(store.Root, id.ToString("N"), "manifest.json"),
                        FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            });
            pipeline.Failed += (episode, reason) =>
            {
                projection.Failure("analysis.episodes", episode, reason);
                if (reason.Contains("diagnostic persistence")) Interlocked.Increment(ref failedAttempts);
            };
            pipeline.Recovered += episode =>
            {
                projection.Recovered("analysis.episodes", episode);
                if (episode == id) Interlocked.Increment(ref recoveries);
            };
            using var generation = new ManagedComponentGeneration(1, pipeline.RunAsync);
            try
            {
                await generation.StartAsync(CancellationToken.None);
                await Until(() => Volatile.Read(ref failedAttempts) >= 1, "Blocked post-report stage did not report its storage failure.");
                projection.Failure("analysis.episodes", independent, "Independent retained failure.");
                await Until(() => Volatile.Read(ref failedAttempts) >= 2, "Blocked post-report stage did not retry.");
                Check(store.Read(id).Stage == EpisodeWorkStage.ReportReady && Volatile.Read(ref recoveries) == 0,
                    "A still-blocked archive commit was declared recovered.");
                blocked!.Dispose(); blocked = null;
                pipeline.Wake();
                await Until(() => store.Read(id).Stage == EpisodeWorkStage.Completed, "Repaired archive commit did not complete.");
            }
            finally { blocked?.Dispose(); await generation.StopAsync(TimeSpan.FromSeconds(10)); }
            Check(Volatile.Read(ref reportNotices) == 1, "Retry duplicated the already published report notice.");
            Check(Volatile.Read(ref recoveries) == 1, "Completed retry did not retire its episode failure exactly once.");
            Check(projection.Snapshot.Failure?.Entity == independent, "Completed retry retained its failure or erased an independent episode failure.");
            projection.Recovered("analysis.episodes", independent);
            Check(projection.Snapshot.Failure == null, "Completed episode failure resurfaced after the independent failure retired.");
        }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("ls-fresh-runtime-", StringComparison.Ordinal), "Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }

    private static EpisodeWorkManifest ReadyReport(EpisodeTransactionStore store, Guid id, LandingRepository repository, object gate)
    {
        var episode = new LandingEpisodeEventArgs(1, new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true } },
            "MSFS", "Fresh runtime fixture", "TEST", "TEST", Array.Empty<AirportFacility>(), Array.Empty<string>(), persistentId: id);
        var timestamp = DateTime.UtcNow;
        var manifest = store.CommitInputs(episode, timestamp, Array.Empty<AirportFacility>(), false, 0);
        var record = new LandingRecord { Id = id.ToString("N"), RecordingEpisodeId = "physical:" + id.ToString("N"),
            TimestampUtc = timestamp, AircraftTitle = "Fresh runtime fixture", ContactNumber = 1, ContactCount = 1 };
        var result = ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { record }, new TelemetryDecisionJournal().Snapshot());
        store.CommitAnalysis(manifest, result); store.SaveHistory(manifest, repository, gate);
        store.CommitFacilities(new RuntimeFinalFacilities(id, episode.InitialFacilities, null));
        store.CommitReport(manifest, result, repository, gate);
        return manifest;
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(message); await Task.Delay(25); }
    }
    private static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-fresh-runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("ls-fresh-runtime-", StringComparison.Ordinal), "Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
