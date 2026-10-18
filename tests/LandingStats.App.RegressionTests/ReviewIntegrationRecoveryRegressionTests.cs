using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.TelemetryUpload;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LandingStats.App.RegressionTests;

internal static class ReviewIntegrationRecoveryRegressionTests
{
    public static void DataTransactions()
    {
        WithRoot(root =>
        {
            InterruptedImports(Path.Combine(root, "imports"));
            StaleImportMarkersDoNotOwnTheNextCommit(Path.Combine(root, "stale-markers"));
            IndexGenerationsPreserveMetadata(Path.Combine(root, "generations"));
            DeletionAtAppend(Path.Combine(root, "deletion"));
            LateRevisionRetention(Path.Combine(root, "retention"));
            RetainedOlderEpisodeReceivesFinalMetadata(Path.Combine(root, "retained-final"));
            ProvenancePages(Path.Combine(root, "provenance"));
        });
    }
    private static void IndexGenerationsPreserveMetadata(string root)
    {
        foreach (var legacy in new[] { false, true })
        foreach (var boundary in new[] { "later-reversion", "batch-reversion", "metadata-first", "missing-index" })
        {
            var path = Path.Combine(root, (legacy ? "legacy-" : "current-") + boundary);
            var repository = new LandingRepository(path); var original = Record("generation"); original.Airport = "ORIGINAL";
            repository.Save(original);
            if (legacy)
            {
                // Existing gzip/JSON-array indexes remain readable and recoverable.
                byte[] json;
                using (var file = File.OpenRead(repository.IndexPath))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var copy = new MemoryStream()) { gzip.CopyTo(copy); json = copy.ToArray(); }
                using (var file = File.Create(repository.IndexPath))
                using (var gzip = new GZipStream(file, CompressionLevel.Optimal)) { gzip.Write(json, 0, json.Length); }
            }
            if (boundary == "missing-index")
            {
                // A brand-new history has no index baseline until first commit.
                path = Path.Combine(path, "empty");
            }
            repository = new LandingRepository(path); repository.LoadAll();
            var incoming = Record(original.Id); incoming.TimestampUtc = original.TimestampUtc; incoming.Airport = "IMPORTED";
            var bytes = repository.SerializeForBackup(incoming);
            if (boundary == "later-reversion")
            {
                using (var held = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ThrowsIo(() => repository.Import(bytes, incoming.Id, true));
                var marker = Directory.GetFiles(Path.Combine(path, ".pending-imports"), "*.json").Single();
                using (var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    repository.Import(bytes, incoming.Id, true);
                    repository.UpdateSummary(original);
                }
            }
            else if (boundary == "batch-reversion")
            {
                var batch = repository.BeginBatch(); repository.Import(bytes, incoming.Id, true); repository.UpdateSummary(original);
                var marker = Directory.GetFiles(Path.Combine(path, ".pending-imports"), "*.json").Single();
                using (var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read)) batch.Dispose();
            }
            else if (boundary == "metadata-first")
            {
                using (var held = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var batch = repository.BeginBatch(); var summary = repository.LoadAll().Single(); summary.Airport = "LATE";
                    repository.UpdateSummary(summary); repository.Import(bytes, incoming.Id, true); ThrowsIo(batch.Dispose);
                }
            }
            else
            {
                // An abandoned batch models process exit before its first index write.
                repository.BeginBatch(); repository.Import(bytes, incoming.Id, true);
            }
            repository = new LandingRepository(path);
            var expected = boundary.EndsWith("reversion", StringComparison.Ordinal) ? "ORIGINAL" : "IMPORTED";
            Check(repository.LoadAll().Single().Airport == expected, "Generation recovery lost index metadata at " + Path.GetFileName(path));
            Check(repository.Export(incoming.Id).SequenceEqual(bytes), "Generation recovery rewrote imported bytes.");
            repository.Import(bytes, incoming.Id, true);
            Check(repository.LoadAll().Single().Airport == expected, "Identical retry lost the generation's metadata.");
            // An unchanged ordinary reader must still see the original JSON-array schema.
            using (var file = File.OpenRead(repository.IndexPath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                var values = (List<LandingRecord>)new DataContractJsonSerializer(typeof(List<LandingRecord>)).ReadObject(gzip)!;
                Check(values.Count == 1 && values[0].Airport == expected, "Generation changed the ordinary gzip/DataContract index reader.");
            }
        }
        IndexGenerationHeaderCompatibility();
    }
    private static void IndexGenerationHeaderCompatibility()
    {
        var payload = Encoding.UTF8.GetBytes("[\"legacy gzip reader\"]"); byte[] compressed;
        using (var memory = new MemoryStream())
        {
            using (var gzip = new GZipStream(memory, CompressionLevel.Optimal, true)) gzip.Write(payload, 0, payload.Length);
            compressed = memory.ToArray();
        }
        var generation = Guid.NewGuid();
        foreach (var chunk in new[] { 1, 3, 9, 10, 31 })
        {
            using var memory = new MemoryStream();
            using (var generated = LandingIndexGeneration.Wrap(memory, generation))
                for (var offset = 0; offset < compressed.Length; offset += chunk)
                    generated.Write(compressed, offset, Math.Min(chunk, compressed.Length - offset));
            memory.Position = 0;
            Check(LandingIndexGeneration.Read(memory) == LandingIndexGeneration.Name(generation), "Fragmented gzip header lost its generation.");
            using var gzip = new GZipStream(memory, CompressionMode.Decompress, true);
            using var decoded = new MemoryStream(); gzip.CopyTo(decoded);
            Check(decoded.ToArray().SequenceEqual(payload), "Generated gzip failed an ordinary legacy decompressor.");
        }
        using var source = new MemoryStream();
        using (var generated = LandingIndexGeneration.Wrap(source, generation)) generated.Write(compressed, 0, compressed.Length);
        var valid = source.ToArray();
        // An unrelated standard subfield before ours must not hide the generation.
        var extra = new byte[valid.Length + 7]; Array.Copy(valid, extra, 12); extra[10] = 27;
        Array.Copy(new byte[] { (byte)'X', (byte)'Y', 3, 0, 1, 2, 3 }, 0, extra, 12, 7);
        Array.Copy(valid, 12, extra, 19, valid.Length - 12);
        using (var fragmented = new FragmentedIndexStream(extra))
            Check(LandingIndexGeneration.Read(fragmented) == LandingIndexGeneration.Name(generation), "Unknown gzip subfield hid the index generation.");
        extra[14] = 255; extra[15] = 255;
        using (var malformed = new FragmentedIndexStream(extra))
            Check(LandingIndexGeneration.Read(malformed).StartsWith("legacy:", StringComparison.Ordinal), "Malformed extra field was trusted as a generation.");
    }
    private sealed class FragmentedIndexStream : MemoryStream
    {
        internal FragmentedIndexStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }
    private static void RetainedOlderEpisodeReceivesFinalMetadata(string root)
    {
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object(); var data = Data(root, repository, gate);
        var store = (EpisodeTransactionStore)typeof(IntegrationApiData).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(data)!;
        var capture = Guid.NewGuid(); var time = DateTime.UtcNow.AddDays(-1); var record = Record("retained-old"); record.TimestampUtc = time.AddSeconds(-1);
        var episode = new LandingEpisodeEventArgs(1, new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 } }, "MSFS", "Fixture", "TEST", "TEST",
            Array.Empty<AirportFacility>(), Array.Empty<string>(), new TelemetryDecisionJournal(), persistentId: capture);
        var manifest = store.CommitInputs(episode, record.TimestampUtc, Array.Empty<AirportFacility>(), false, 1);
        var initial = ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { record }, new TelemetryDecisionJournal().Snapshot());
        store.CommitAnalysis(manifest, initial); data.Publish(capture, initial, false);
        var actualEvent = Result(data, "events.read")["events"]![0]!.DeepClone(); actualEvent["sequence"] = 2;
        var directory = Path.Combine(root, "events"); var path = Path.Combine(directory, "events-v1.json"); var state = JObject.Parse(File.ReadAllText(path));
        var events = new JArray();
        for (var i = 1; i <= IntegrationEventStore.MaximumEvents; i++)
            events.Add(i == 2 ? actualEvent : new JObject { ["sequence"] = i, ["episodeId"] = "filler" + i, ["revision"] = 1,
                ["timestampUtc"] = time.AddSeconds(i - 1).ToString("O", CultureInfo.InvariantCulture), ["payload"] = new JObject() });
        state["events"] = events; state["nextSequence"] = IntegrationEventStore.MaximumEvents + 1; SaveJournal(path, state);
        var journal = new IntegrationEventStore(directory); var stream = journal.StreamId;
        Check(journal.Publish("newest", time.AddDays(1), 1, new JObject()), "Fixture failed to trim the leading newer episode.");
        Check(journal.CoversCapture(capture, record.TimestampUtc, 1) && !journal.CoversCapture(capture, record.TimestampUtc, 2),
            "Timestamp floor treated a retained initial revision as a completed metadata result.");
        Check(journal.CoversCapture(Guid.NewGuid(), time.AddSeconds(-2), 2) && !journal.Publish("wholly-expired", time.AddSeconds(-2), 2, new JObject()),
            "Wholly expired history started an unbounded republish loop.");
        var cursor = journal.LatestSequence;
        store.SaveHistory(store.Read(capture), repository, gate);
        var inputs = store.ReadInputs(store.Read(capture)); store.CommitFacilities(new RuntimeFinalFacilities(capture, inputs.Episode.InitialFacilities, inputs.Episode.DecisionJournal?.Snapshot()));
        var final = store.ReadAnalysis(store.Read(capture)); final.Records[0].Airport = "FINAL";
        store.CommitReport(store.Read(capture), final, repository, gate);
        data = Data(root, repository, gate); data.ReconcileCommitted();
        var page = Result(data, "events.read", new JObject { ["streamId"] = stream, ["afterSequence"] = cursor });
        Check(((JArray)page["events"]!).Count == 1 && (int?)page["events"]?[0]?["revision"] == 2 &&
            (string?)page["events"]?[0]?["payload"]?["contacts"]?[0]?["site"]?["airport"] == "FINAL",
            "Reconciliation dropped final metadata for an older episode still retained by sequence.");
        var latest = (long)page["latestSequence"]!;
        data = Data(root, repository, gate); data.ReconcileCommitted();
        Check((long?)Result(data, "hello")["events"]?["latestSequence"] == latest, "Restart duplicated retained final metadata.");
    }
    private static void StaleImportMarkersDoNotOwnTheNextCommit(string root)
    {
        foreach (var reopen in new[] { false, true })
        foreach (var batch in new[] { false, true })
        foreach (var refine in new[] { false, true })
        {
            var path = Path.Combine(root, (reopen ? "reopen" : "same") + (batch ? "-batch" : "-single") + (refine ? "-refined" : ""));
            var repository = new LandingRepository(path); var original = Record("replace-twice"); original.Airport = "OLD";
            repository.Save(original);
            var incoming = Record(original.Id); incoming.TimestampUtc = original.TimestampUtc; incoming.Airport = "FIRST";
            var first = repository.SerializeForBackup(incoming);
            using (var held = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                ThrowsIo(() => repository.Import(first, incoming.Id, true));
            var marker = Directory.GetFiles(Path.Combine(path, ".pending-imports"), "*.json").Single();
            using (var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                repository.Import(first, incoming.Id, true);
                Check(File.Exists(marker) && repository.LoadAll().Single().Airport == "FIRST", "First import did not commit while retaining its stale marker.");
                if (refine)
                {
                    var later = repository.LoadAll().Single(); later.Airport = "LATE"; later.Runway = "09L";
                    repository.UpdateSummary(later);
                }
            }
            if (reopen) repository = new LandingRepository(path);
            Check(repository.LoadAll().Single().Airport == (refine ? "LATE" : "FIRST"), "Reopen changed the committed first import or its late metadata.");
            incoming.Airport = "SECOND"; var second = repository.SerializeForBackup(incoming);
            using (var held = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (batch)
                {
                    var transaction = repository.BeginBatch(); repository.Import(second, incoming.Id, true);
                    incoming.Airport = "THIRD"; second = repository.SerializeForBackup(incoming);
                    repository.Import(second, incoming.Id, true);
                    ThrowsIo(transaction.Dispose);
                }
                else ThrowsIo(() => repository.Import(second, incoming.Id, true));
            }
            repository = new LandingRepository(path);
            var summary = repository.LoadAll().Single();
            Check(summary.Airport == incoming.Airport && repository.LoadDetail(summary)!.Airport == incoming.Airport,
                "A stale committed intent hid the next interrupted import: " + Path.GetFileName(path));
            Check(repository.Export(incoming.Id).SequenceEqual(second), "Second import recovery changed the exact detail bytes.");
            repository.Import(second, incoming.Id, true);
            Check(repository.LoadAll().Single().Airport == incoming.Airport, "Identical second retry failed to retain the recovered summary.");
        }
    }
    private static LandingRecord Record(string id) => new LandingRecord { Id = id, RecordingEpisodeId = "physical:" + id,
        TimestampUtc = DateTime.UtcNow, AircraftTitle = "Fixture", AircraftType = "TEST", ContactCount = 1, ContactNumber = 1 };
    private static void InterruptedImports(string root)
    {
        foreach (var boundary in new[] { "before-detail", "before-index", "after-index", "batch", "new-record" })
        {
            var repository = new LandingRepository(Path.Combine(root, boundary));
            var old = Record("replace"); old.Airport = "OLD"; old.InertialFpm = -99;
            var detailPath = repository.Save(old);
            var remote = new LandingRepository(Path.Combine(root, "remote-" + boundary));
            var incoming = Record(boundary == "new-record" ? "new-record" : old.Id);
            incoming.TimestampUtc = old.TimestampUtc; incoming.Airport = "NEW"; incoming.InertialFpm = -123;
            remote.Save(incoming); var bytes = remote.Export(incoming.Id);
            if (boundary == "before-detail")
            {
                using (var held = new FileStream(detailPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ThrowsIo(() => repository.Import(bytes, incoming.Id, true));
                repository = new LandingRepository(repository.RootPath);
                Check(repository.LoadAll().Single().Airport == "OLD", "An unpublished detail changed the recovered index.");
                repository.Import(bytes, incoming.Id, true);
            }
            else if (boundary == "batch")
            {
                using (var held = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var batch = repository.BeginBatch(); repository.Import(bytes, incoming.Id, true);
                    incoming.Airport = "NEWER"; bytes = remote.SerializeForBackup(incoming);
                    repository.Import(bytes, incoming.Id, true);
                    ThrowsIo(batch.Dispose);
                }
            }
            else
            {
                using (var held = new FileStream(repository.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ThrowsIo(() => repository.Import(bytes, incoming.Id, true));
                Check(repository.Export(incoming.Id).SequenceEqual(bytes), "Fixture did not cross detail publication.");
                if (boundary == "after-index")
                {
                    var marker = Directory.GetFiles(Path.Combine(repository.RootPath, ".pending-imports"), "*.json").Single();
                    using (var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        // The retry commits the index; a locked marker models failed cleanup
                        // or a process exit after commit but before intent removal.
                        repository.Import(bytes, incoming.Id, true);
                        var later = repository.LoadAll().Single(); later.Airport = "LATE"; later.Runway = "09L";
                        repository.UpdateSummary(later);
                    }
                    incoming.Airport = "LATE";
                }
            }
            repository = new LandingRepository(repository.RootPath);
            var summary = repository.LoadAll().Single(item => item.Id == incoming.Id);
            Check(summary.Airport == incoming.Airport && repository.LoadDetail(summary)!.Airport == incoming.Airport && summary.InertialFpm == -123,
                "Import recovery lost the new summary or a later index-only refinement at " + boundary);
            repository.Import(bytes, incoming.Id, true);
            Check(repository.LoadAll().Single(item => item.Id == incoming.Id).Airport == incoming.Airport,
                "Identical-byte retry overwrote later metadata at " + boundary);
            Check(repository.Export(incoming.Id).SequenceEqual(bytes), "Recovery rewrote imported detail bytes.");
        }
    }
    private static IntegrationApiData Data(string root, LandingRepository repository, object gate) => new IntegrationApiData(
        Path.Combine(root, "events"), repository, gate, new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64)),
        () => new RuntimePresentation((_, __) => Task.CompletedTask).Snapshot, () => Array.Empty<SupervisionSnapshot>());
    private static JObject Call(IntegrationApiData data, string method, JObject? parameters = null)
    {
        var text = data.Handle(new JObject { ["id"] = "test", ["protocolVersion"] = 1, ["method"] = method,
            ["params"] = parameters ?? new JObject() }.ToString(Formatting.None));
        Check(Encoding.UTF8.GetByteCount(text) <= IntegrationApiData.MaximumResponseBytes, "Response exceeded its bound.");
        return JObject.Parse(text);
    }
    private static JObject Result(IntegrationApiData data, string method, JObject? parameters = null)
    { var response = Call(data, method, parameters); Check(response["error"] == null, response.ToString()); return (JObject)response["result"]!; }
    private sealed class ScheduledRecords : IReadOnlyList<LandingRecord>
    {
        private readonly LandingRecord _record;
        public ScheduledRecords(LandingRecord record) { _record = record; }
        public Action? Captured;
        public int Count => 1;
        public LandingRecord this[int index] => index == 0 ? _record : throw new IndexOutOfRangeException();
        public IEnumerator<LandingRecord> GetEnumerator() { yield return _record; Captured?.Invoke(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static void DeletionAtAppend(string root)
    {
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object(); var data = Data(root, repository, gate);
        var record = Record("deleted-at-append"); repository.Save(record);
        var before = (string)Result(data, "hello")["events"]!["streamId"]!;
        var records = new ScheduledRecords(record);
        var result = ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), records, new TelemetryDecisionJournal().Snapshot());
        var capture = Guid.NewGuid();
        var store = (EpisodeTransactionStore)typeof(IntegrationApiData).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(data)!;
        var episode = new LandingEpisodeEventArgs(1, new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 } }, "MSFS", "Fixture", "TEST", "TEST",
            Array.Empty<AirportFacility>(), Array.Empty<string>(), new TelemetryDecisionJournal(), persistentId: capture);
        var manifest = store.CommitInputs(episode, record.TimestampUtc, Array.Empty<AirportFacility>(), false, 1); store.CommitAnalysis(manifest, result);
        var journal = typeof(IntegrationApiData).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(data)!;
        var eventGate = journal.GetType().GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(journal)!;
        using var captured = new ManualResetEventSlim(); using var appendBlocked = new ManualResetEventSlim();
        records.Captured = () => { captured.Set(); Check(appendBlocked.Wait(5000), "Append fixture was not held."); };
        var deletion = Task.Run(() =>
        {
            Check(captured.Wait(5000), "Publication never captured its records.");
            lock (eventGate)
            {
                appendBlocked.Set(); lock (gate) repository.Delete(record.Id);
                // Old code reset the stream here and subsequently appended its stale
                // snapshot to the new stream. A bounded busy reply is also valid.
                var racingHello = Call(data, "hello");
                Check(racingHello["error"] == null || (string?)racingHello["error"]?["code"] == "TemporarilyUnavailable", "Unexpected race response.");
                Check((string?)racingHello["id"] == "test", "Projection contention lost request correlation.");
            }
        });
        data.Publish(capture, result, false); deletion.GetAwaiter().GetResult(); records.Captured = null;
        var after = (string)Result(data, "hello")["events"]!["streamId"]!;
        Check(after != before, "Deletion did not change the API epoch.");
        data.ReconcileCommitted(); data.Publish(Guid.NewGuid(), result, true);
        Check(((JArray)Result(data, "events.read")["events"]!).Count == 0, "Deleted contact survived reconciliation in the new stream.");
        Check((string?)Call(data, "episodes.get", new JObject { ["id"] = record.RecordingEpisodeId })["error"]?["code"] == "NotFound", "Deleted episode remained exposed.");
        Check((string?)Call(data, "landings.get", new JObject { ["id"] = record.Id })["error"]?["code"] == "NotFound", "Canonical deleted record remained exposed.");
    }
    private static void SaveJournal(string path, JObject state)
    {
        state.Remove("checksum"); using var sha = SHA256.Create();
        state["checksum"] = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(state.ToString(Formatting.None))));
        File.WriteAllText(path, state.ToString(Formatting.None));
    }
    private static void LateRevisionRetention(string root)
    {
        foreach (var lateTimestamp in new[] { false, true })
        {
            var directory = Path.Combine(root, lateTimestamp.ToString()); var store = new IntegrationEventStore(directory);
            var stream = store.StreamId; var path = Path.Combine(directory, "events-v1.json"); var state = JObject.Parse(File.ReadAllText(path));
            var time = DateTime.UtcNow.AddDays(-1); var events = new JArray();
            for (var i = 1; i <= IntegrationEventStore.MaximumEvents; i++) events.Add(new JObject { ["sequence"] = i,
                ["episodeId"] = i == 3 ? "e1" : "e" + i, ["revision"] = i == 3 ? 2 : 1,
                ["timestampUtc"] = (i == 3 ? time.AddSeconds(lateTimestamp ? -1 : 0) : time.AddSeconds(i - 1)).ToString("O", CultureInfo.InvariantCulture),
                ["payload"] = new JObject { ["contacts"] = new JArray() } });
            state["events"] = events; state["nextSequence"] = IntegrationEventStore.MaximumEvents + 1; SaveJournal(path, state);
            store = new IntegrationEventStore(directory); store.Publish("new", time.AddDays(1), 1, new JObject());
            Check((long?)store.Read(stream, 2, 1)["events"]?[0]?["sequence"] == 3, "Retention silently removed an unread late revision.");
            store = new IntegrationEventStore(directory);
            Check((long?)store.Read(stream, 2, 1)["events"]?[0]?["sequence"] == 3, "Restart changed the retention cursor.");
            // A journal already written by the old hole-producing policy must reset.
            state = JObject.Parse(File.ReadAllText(path)); ((JArray)state["events"]!)[1]!.Remove(); SaveJournal(path, state);
            store = new IntegrationEventStore(directory); Check(store.StreamId != stream, "A pre-existing sequence hole remained silently readable.");
        }
    }
    private static void ProvenancePages(string root)
    {
        var repository = new LandingRepository(Path.Combine(root, "history")); var data = Data(root, repository, new object());
        var record = Record("long-record");
        var evidence = new ReplaySourceEvidence(Guid.NewGuid(), 1, 1, 0, 0, 1000, SdkReplayState.Unknown,
            new ReplaySourceObservation(SourceObservationAvailability.Pending, SourceObservationReason.AwaitingReply));
        var controllerSession = Guid.NewGuid();
        var catalogs = Enumerable.Range(1, 20).Select(index => RuntimeWire.Encode(new RuntimeEnvelope(
            new WorkerIdentity(controllerSession, Guid.NewGuid(), index, ComponentRole.Controllers), 0, 2, 100,
            new ControllerCatalogMessage(1, new[] { new ControllerDescriptor(0, 7, "Stick " + index, "driver", 63,
                new uint[6], Enumerable.Repeat(65535u, 6).ToArray()) })))).ToArray();
        var samples = Enumerable.Range(0, 3000).Select(i => new TelemetrySample { Sequence = i + 1, SimulationTimeSeconds = i * .05, SourceEvidence = evidence,
            ControllerCatalog = catalogs[i % catalogs.Length] }).ToArray();
        record.Series = samples.Select(sample => new LandingSeriesPoint { TimeSeconds = sample.SimulationTimeSeconds,
            RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(), RawControllerYAxisAgeSeconds = Array.Empty<double>() }).ToList();
        record.SourceEvidenceTimeline = ReplaySourceTimelineCodec.EncodeText(samples); record.ControllerTimeline = ControllerCatalogTimeline.Capture(samples);
        repository.Save(record);
        Check((string?)Call(data, "landings.provenance", new JObject { ["id"] = record.Id })["error"]?["code"] == "ResponseTooLarge", "Fixture no longer exercises an oversized default response.");
        var metadata = Result(data, "landings.provenance", new JObject { ["id"] = record.Id, ["section"] = "metadata" });
        Check((int?)metadata["sections"]?["sourceEvidenceTimeline"] == record.SourceEvidenceTimeline!.Length, "Metadata omitted timeline counts.");
        var text = new StringBuilder(); var offset = 0;
        do
        {
            var page = Result(data, "landings.provenance", new JObject { ["id"] = record.Id, ["section"] = "sourceEvidenceTimeline", ["offset"] = offset, ["limit"] = 65533 });
            text.Append((string)page["data"]!); offset = (int)page["nextOffset"]!; if (!(bool)page["hasMore"]!) break;
        } while (true);
        Check(text.ToString() == record.SourceEvidenceTimeline, "Paged base64 did not reconstruct the exact stored provenance.");
        Check(ReplaySourceTimelineCodec.DecodeText(text.ToString(), samples.Length).All(item => item != null), "Reconstructed provenance did not decode.");
        foreach (var section in new[] { "controllerCatalogs", "controllerTransitions" })
        {
            var reconstructed = new JArray(); offset = 0;
            do
            {
                var page = Result(data, "landings.provenance", new JObject { ["id"] = record.Id, ["section"] = section, ["offset"] = offset,
                    ["limit"] = section == "controllerCatalogs" ? 16 : 200 });
                Check((int?)page["samples"] == samples.Length, "Controller section omitted its sample count.");
                foreach (var item in (JArray)page["items"]!) reconstructed.Add(item.DeepClone());
                offset = (int)page["nextOffset"]!; if (!(bool)page["hasMore"]!) break;
            } while (true);
            var expected = section == "controllerCatalogs" ? JToken.FromObject(record.ControllerTimeline!.Catalogs) : JToken.FromObject(record.ControllerTimeline!.Transitions);
            Check(JToken.DeepEquals(reconstructed, expected), "Controller section changed opaque provenance.");
            var maximum = section == "controllerCatalogs" ? 16 : 200;
            Check((string?)Call(data, "landings.provenance", new JObject { ["id"] = record.Id, ["section"] = section, ["limit"] = maximum + 1 })["error"]?["code"] == "InvalidParams", "Unbounded controller page accepted.");
        }
        Check((string?)Call(data, "landings.provenance", new JObject { ["id"] = record.Id, ["section"] = "sourceEvidenceTimeline", ["limit"] = 65537 })["error"]?["code"] == "InvalidParams", "Unbounded provenance page accepted.");
    }
    public static void TelemetryConnection()
    {
        var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), clock);
        var state = new RecordingRuntimeState(identity); var prepare = new ControllerInputPreparation(new ControllerJoiner(clock));
        var projection = new IntegrationTelemetrySnapshot(); var worker = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator);
        long sequence = 0;
        RecordingRuntimeEffects Feed(RuntimePayload payload)
        {
            if (payload is SourceGapMessage gap) sequence = gap.LastMissing;
            var qpc = Stopwatch.GetTimestamp(); var message = new RuntimeEnvelope(worker, payload is WorkerHello ? 0 : 1, ++sequence, qpc, payload);
            var input = prepare.Prepare(new RuntimeInput(Guid.NewGuid(), JournalRecordKind.SourceMessage, qpc, qpc, DateTime.UtcNow, RuntimeWire.Encode(message)));
            var effects = state.Apply(new DurableRuntimeInput(input, new JournalCursor(1, sequence))); projection.Observe(effects, DateTime.UtcNow); return effects;
        }
        Feed(new WorkerHello("fixture", new string('a', 64), 1, clock.Id, clock.Frequency)); Feed(new LifecycleMessage(SourceLifecycle.Connected));
        var overflow = Feed(new SourceGapMessage(SourceGapReason.BufferOverflow, 3, 4, true));
        Check(overflow.SimulatorConnected == true && overflow.Presentation.OfType<RecorderStatusEventArgs>().Any(item => item.State == RecorderState.Waiting), "Fixture did not exercise same-connection recovery.");
        // WorkerChannel restores the latest SDK state before post-gap frames.
        Feed(new SimulatorLifecycleSnapshotMessage(connected: true, known: true, running: 1));
        Feed(new AircraftMessage("MSFS", "Recovered aircraft", "TEST", "TEST"));
        Feed(new TelemetryFrameMessage(new TelemetrySample { SimulationTimeSeconds = 100, MotionSimulation = true, GroundSpeedKnots = 123 }));
        Check((bool?)projection.Read()["connected"] == true && (bool?)projection.Read()["fullFrame"]?["stale"] == false &&
            (double?)projection.Read()["fullFrame"]?["groundSpeedKnots"] == 123, "Recovered overflow remained disconnected or stale.");
        Feed(new LifecycleMessage(SourceLifecycle.Disconnected)); Check((bool?)projection.Read()["connected"] == false, "Genuine disconnect became connected.");
        Feed(new LifecycleMessage(SourceLifecycle.Connected)); Feed(new LifecycleMessage(SourceLifecycle.SimulatorQuit));
        Check((bool?)projection.Read()["connected"] == false, "Simulator quit remained connected.");
    }
    public static void OwnerRecovery() => WithRoot(root => Task.Run(async () =>
    {
        await ApiContention(Path.Combine(root, "api"));
        await UploadCleanup(Path.Combine(root, "delivered"), false);
        await UploadCleanup(Path.Combine(root, "quarantine"), true);
        await UploadCleanup(Path.Combine(root, "startup-terminal"), false, true);
        await UploadCleanup(Path.Combine(root, "quarantine-compaction"), true, false, true);
    }).GetAwaiter().GetResult());
    private static readonly string Executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe");
    private static async Task ApiContention(string root)
    {
        var gate = new object(); var repository = new LandingRepository(Path.Combine(root, "history")); var data = Data(root, repository, gate);
        var escalations = new ConcurrentQueue<string>(); var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        var options = new ComponentRecoveryOptions { OperationDeadline = TimeSpan.FromSeconds(30) };
        var api = new IntegrationApiRuntimeOwner(() => data, Executable, Guid.NewGuid(), clock, (component, _) => escalations.Enqueue(component), _ => { }, _ => { }, options,
            _ => "review-api-" + Guid.NewGuid().ToString("N"));
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => { lock (gate) { held.Set(); release.Wait(); } }); held.Wait();
        using var supervisor = new ComponentSupervisor("integration.data", generation => new ManagedComponentGeneration(generation, api.RunAsync),
            _ => Task.CompletedTask, (component, _) => escalations.Enqueue(component), options);
        try
        {
            supervisor.Start(); await api.SetEnabledAsync(true);
            var wait = Stopwatch.StartNew(); var response = Call(data, "hello");
            Check((string?)response["error"]?["code"] == "TemporarilyUnavailable" && (string?)response["id"] == "test" && wait.Elapsed < TimeSpan.FromSeconds(2), "Busy repository did not bound optional query wait.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); wait.Restart();
            data.Handle("{\"id\":\"cancelled\",\"protocolVersion\":1,\"method\":\"hello\"}", cancelled.Token); Check(wait.Elapsed < TimeSpan.FromMilliseconds(250), "Cancelled API query waited for repository ownership.");
            await Task.Delay(12000);
            Check(escalations.IsEmpty && supervisor.Snapshot.Failures == 0, "Optional bookkeeping restarted during a legitimate storage hold.");
            // Stop while still held: cancellation must release the managed owner.
            await api.StopAsync(); await supervisor.StopAsync();
            Check(escalations.IsEmpty, "Stopping a contended optional owner escalated recorder recovery.");
        }
        finally { release.Set(); await writer; await api.StopAsync(); await supervisor.StopAsync(); }
    }
    private static async Task UploadCleanup(string root, bool reject, bool terminalAtStartup = false, bool blockCompaction = false)
    {
        var archives = Path.Combine(root, "archives"); Directory.CreateDirectory(archives);
        var id = Guid.NewGuid(); var path = Path.Combine(archives, id.ToString("N") + "_bug_raw.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var session = new StreamWriter(zip.CreateEntry("session.txt").Open())) session.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
            using var csv = new StreamWriter(zip.CreateEntry("telemetry.csv").Open()); csv.WriteLine(TelemetryCsv.Header);
            csv.WriteLine(TelemetryCsv.Format(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true }));
        }
        var box = new DurableOutbox(Path.Combine(root, "outbox"), archives); var hash = EpisodeTransactionStore.Hash(path);
        box.Register(id, path, hash, false, DateTime.UtcNow); var identity = Path.Combine(root, "identity"); _ = new TelemetryUploadIdentityStore(identity).Identity();
        var count = 0; var owner = new UploadRuntimeOwner(box, Executable, identity, Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency),
            _ => { }, _ => reject ? "upload-reject" : "upload-ok"); owner.WorkerStarted += _ => Interlocked.Increment(ref count);
        using var generation = new ManagedComponentGeneration(1, owner.RunAsync);
        var lockedPath = path;
        if (blockCompaction) { lockedPath = Path.Combine(archives, "." + id.ToString("N") + ".sha256"); File.WriteAllText(lockedPath, hash, new UTF8Encoding(false)); }
        var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (terminalAtStartup)
            {
                var claim = box.TryClaim(id, new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload))!;
                ThrowsIo(() => box.Acknowledge(claim));
            }
            await generation.StartAsync(CancellationToken.None);
            await Until(() => box.Read(id).State == (blockCompaction ? OutboxState.Quarantined : reject ? OutboxState.QuarantinePending : OutboxState.Delivered), "Worker never committed its terminal receipt.");
            Check(blockCompaction ? File.Exists(lockedPath) && !File.Exists(path) : File.Exists(path), "Fixture did not block terminal cleanup.");
            // Keep the failure present until the attempt drains and schedules backoff.
            await Task.Delay(1000); locked.Dispose(); owner.Wake();
            await Until(() => !File.Exists(Path.Combine(root, "outbox", id.ToString("N") + ".json")), "Running owner did not retry terminal cleanup after its backoff.");
            var final = box.Read(id); Check(count == (terminalAtStartup ? 0 : 1) && !File.Exists(path), "Cleanup retried the upload or retained delivered bytes.");
            Check(final.State == (reject ? OutboxState.Quarantined : OutboxState.Delivered), "Cleanup changed the terminal receipt.");
            if (reject) Check(EpisodeTransactionStore.Hash(final.QuarantinePath) == hash, "Quarantine cleanup changed rejected bytes.");
        }
        finally { locked.Dispose(); await generation.StopAsync(TimeSpan.FromSeconds(10)); }
    }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(message); await Task.Delay(25); } }
    private static void ThrowsIo(Action action)
    { try { action(); } catch (IOException) { return; } throw new InvalidOperationException("Expected storage failure was absent."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "review-integration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var complete = false; try { action(root); complete = true; }
        finally { if (complete && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
