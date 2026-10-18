using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LandingStats.App.RegressionTests;

internal static class IntegrationApiDataRegressionTests
{
    private const string Engine = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public static void RunAll()
    {
        WithFixture(QueryContractsAndKeyset);
        WithFixture(DurableResultsBeforeHistoryAndRecovery);
        WithFixture(DeletionResetsAllClientsAndDoesNotResurrect);
        WithFixture(CorruptJournalRebuildsPendingAnalysis);
        WithFixture(RetentionAndCorruptionRequireResync);
    }
    private static void QueryContractsAndKeyset(Fixture f)
    {
        var time = DateTime.UtcNow;
        var first = Record("c", time); var second = Record("b", time); var third = Record("a", time.AddSeconds(-1));
        first.RawControllerSourceIndices.Add(0);
        first.Series.Add(new LandingSeriesPoint { TimeSeconds = -1, InertialFpm = double.NaN, LatitudeDegrees = null,
            RawControllerYAxisPercent = new[] { 37.0 }, RawControllerYAxisValid = new[] { false }, RawControllerYAxisAgeSeconds = new[] { double.PositiveInfinity } });
        first.Series.Add(new LandingSeriesPoint { TimeSeconds = 0, InertialFpm = -142, LatitudeDegrees = 0, LongitudeDegrees = 0,
            AxisElevatorSetPercent = 0, AxisElevatorSetValid = true,
            RawControllerYAxisPercent = new[] { 0.0 }, RawControllerYAxisValid = new[] { true }, RawControllerYAxisAgeSeconds = new[] { 0.0 } });
        first.Engines.Add(new LandingEngineSeries { EngineNumber = 2, Points = new List<LandingEnginePoint> { new LandingEnginePoint { TimeSeconds = 0, N1Percent = 24, Rpm = double.NaN } } });
        first.ContactPoints.Add(new LandingContactSeries { ContactPointIndex = 3, Points = new List<LandingContactPoint> { new LandingContactPoint { TimeSeconds = 0, CompressionPercent = 8, OnGround = true } } });
        foreach (var record in new[] { first, second, third }) f.Repository.Save(record);
        var hello = f.Call("hello");
        Require((int?)hello["protocolVersion"] == 1 && (bool?)hello["readOnly"] == true, "capabilities must identify a read-only versioned API");
        var page = f.Call("landings.list", new JObject { ["limit"] = 1 });
        Require((string?)page["items"]?[0]?["id"] == "c", "history must use stable timestamp/id ordering");
        var cursor = (string)page["nextCursor"]!;
        f.Repository.Save(Record("new", time.AddSeconds(5)));
        var next = f.Call("landings.list", new JObject { ["cursor"] = cursor, ["limit"] = 2 });
        Require(((JArray)next["items"]!).Select(item => (string)item["id"]!).SequenceEqual(new[] { "b", "a" }), "concurrent insertion shifted keyset pages");
        var detail = f.Call("landings.get", new JObject { ["id"] = "c" });
        Require(detail["metrics"]?["surfaceClosureRate"]?["value"]?.Type == JTokenType.Null &&
            (bool?)detail["metrics"]?["surfaceClosureRate"]?["available"] == false, "nonfinite unavailable metric leaked or became zero");
        Require((string?)detail["metrics"]?["inertialVerticalRate"]?["unit"] == "ft/min" && (int?)detail["series"]?["samples"] == 2, "rich detail lost units or series availability");
        var series = f.Call("landings.series", new JObject { ["id"] = "c", ["limit"] = 1 });
        Require((bool?)series["hasMore"] == true && series["points"]?[0]?["inertialFpm"]?.Type == JTokenType.Null &&
            series["points"]?[0]?["rawControllerYAxisPercent"]?[0]?.Type == JTokenType.Null, "series must page and distinguish unavailable physical axes");
        var tail = f.Call("landings.series", new JObject { ["id"] = "c", ["offset"] = 1 });
        Require((double?)tail["points"]?[0]?["latitudeDegrees"] == 0 && (double?)tail["points"]?[0]?["axisElevatorSetPercent"] == 0,
            "real zero coordinates and controls must not become missing");
        Require((double?)f.Call("landings.engines", new JObject { ["id"] = "c", ["engineNumber"] = 2 })["points"]?[0]?["n1Percent"] == 24,
            "engine channel query failed");
        Require((bool?)f.Call("landings.contactPoints", new JObject { ["id"] = "c", ["contactPointIndex"] = 3 })["points"]?[0]?["onGround"] == true,
            "contact point channel query failed");
        first.Airport = "EGLL"; f.Repository.UpdateSummary(first);
        Require((string?)f.Call("landings.get", new JObject { ["id"] = "c" })["site"]?["airport"] == "EGLL", "cached detail ignored updated site metadata");
        var status = f.Call("status.get").ToString(Formatting.None);
        Require(!status.Contains("private-path") && !status.Contains("secret-token"), "internal failure reasons must not leak through status");
        Require(f.Error("{\"id\":\"x\",\"protocolVersion\":2,\"method\":\"hello\"}") == "UnsupportedProtocol", "unsupported protocol accepted");
        Require(f.Error("{\"id\":\"x\",\"protocolVersion\":1,\"method\":\"hello\",\"method\":\"status.get\"}") == "InvalidRequest", "duplicate JSON member accepted");
        Require(f.Error(Request("landings.series", new JObject { ["id"] = "c", ["limit"] = 101 })) == "InvalidParams", "unbounded series page accepted");
        Require(f.Error(Request("landings.list", new JObject { ["cursor"] = "not-base64" })) == "InvalidParams", "invalid cursor accepted");
        Require(f.Error(Request("destroy.everything")) == "MethodNotFound", "unknown mutation method accepted");
    }
    private static void DurableResultsBeforeHistoryAndRecovery(Fixture f)
    {
        var capture = Guid.NewGuid(); var timestamp = DateTime.UtcNow;
        var records = new[] { Record("first", timestamp, "physical:1", 1, 2), Record("bounce", timestamp, "physical:1", 2, 2), Record("relocated", timestamp, "physical:2") };
        var manifest = Commit(f, capture, records);
        var observedBeforeHistory = false;
        var work = new EpisodeWorkCoordinator(f.Store,
            new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), f.Repository, f.RepositoryGate), f.Repository, f.RepositoryGate,
            resultCommitted: (id, result, final) =>
            {
                Require(!final && f.Store.Read(id).Stage == EpisodeWorkStage.Analysed, "API callback ran before the durable analysis marker");
                Require(f.Repository.LoadAll().Count == 0, "API result waited for history publication");
                f.Data.Publish(id, result, false);
                Require(((JArray)f.Call("landings.list")["items"]!).Count == 3, "resync omitted results already published before history compression");
                observedBeforeHistory = true;
            });
        work.ResumeAsync(capture).GetAwaiter().GetResult();
        Require(observedBeforeHistory && f.Repository.LoadAll().Count == 3, "normal history save must still finish");
        var events = (JArray)f.Call("events.read")["events"]!;
        Require(events.Count == 2 && ((JArray)events[0]!["payload"]!["contacts"]!).Count == 2 &&
            (string?)events[0]?["episodeId"] == "physical:1" && (string?)events[1]?["episodeId"] == "physical:2",
            "bounces must be atomic while independent physical segments remain distinct");
        var oldStream = (string)f.Call("hello")["events"]!["streamId"]!;
        f.Data = f.CreateData();
        f.Data.ReconcileCommitted();
        Require((long?)f.Call("hello")["events"]?["latestSequence"] == 2 && (string?)f.Call("hello")["events"]?["streamId"] == oldStream,
            "restarting after append republished duplicate results");

        var crashCapture = Guid.NewGuid(); Commit(f, crashCapture, new[] { Record("crash-gap", timestamp.AddSeconds(1), "physical:crash") });
        f.Data = f.CreateData(); f.Data.ReconcileCommitted();
        Require((long?)f.Call("hello")["events"]?["latestSequence"] == 3, "durable analysis lost in the append crash gap was not republished");
        var input = f.Store.ReadInputs(manifest);
        f.Store.CommitFacilities(new RuntimeFinalFacilities(capture, input.Episode.InitialFacilities, input.Episode.DecisionJournal?.Snapshot()));
        var report = f.Store.ReadAnalysis(f.Store.Read(capture)); foreach (var record in report.Records) record.Airport = "EGLL";
        f.Store.CommitReport(f.Store.Read(capture), report, f.Repository, f.RepositoryGate);
        f.Store.CommitArchive(f.Store.Read(capture), null, "capture_opted_out", 1); f.Store.MarkOutboxEnqueued(f.Store.Read(capture)); f.Store.Complete(f.Store.Read(capture));
        Require(!Directory.Exists(Path.Combine(f.Store.Root, capture.ToString("N"))), "fixture must archive completed episode");
        f.Data = f.CreateData(); f.Data.ReconcileCommitted();
        var finalEvents = (JArray)f.Call("events.read")["events"]!;
        Require(finalEvents.Count == 5 && finalEvents.Count(item => (int?)item["revision"] == 2) == 2,
            "archived completed reports must repair their missed metadata events");
        Require((string?)f.Call("episodes.get", new JObject { ["id"] = "physical:1" })["contacts"]?[0]?["site"]?["airport"] == "EGLL",
            "episode query did not return the latest metadata revision");
    }
    private static void DeletionResetsAllClientsAndDoesNotResurrect(Fixture f)
    {
        var contact = Record("delete-me", DateTime.UtcNow, "physical:delete");
        Commit(f, Guid.NewGuid(), new[] { contact }); f.Data.ReconcileCommitted(); f.Repository.Save(contact);
        var hello = f.Call("hello"); var oldStream = (string)hello["events"]!["streamId"]!;
        f.Repository.Delete(contact.Id);
        Require(f.Error(Request("events.read", new JObject { ["streamId"] = oldStream, ["afterSequence"] = 0 })) == "ResyncRequired", "deletion must invalidate pre-delete cursors");
        f.Data.ReconcileCommitted();
        Require(((JArray)f.Call("events.read")["events"]!).Count == 0 && f.Error(Request("landings.get", new JObject { ["id"] = contact.Id })) == "NotFound",
            "durable analysis replay resurrected a deleted contact");
        var beforeLegacy = (string)f.Call("hello")["events"]!["streamId"]!;
        f.Repository.Delete("legacy-never-in-journal");
        var afterLegacy = (string)f.Call("hello")["events"]!["streamId"]!;
        Require(beforeLegacy != afterLegacy, "deleting a legacy or expired contact must also invalidate subscribed caches");
        f.Data = f.CreateData();
        Require((string?)f.Call("hello")["events"]?["streamId"] == afterLegacy, "deletion fingerprint must survive restart without another spurious reset");
    }
    private static void RetentionAndCorruptionRequireResync(Fixture f)
    {
        var root = Path.Combine(f.Root, "journal-fixture"); var journal = new IntegrationEventStore(root);
        var stream = journal.StreamId; var path = Path.Combine(root, "events-v1.json");
        var state = JObject.Parse(File.ReadAllText(path)); var events = new JArray(); var time = DateTime.UtcNow.AddDays(-1);
        for (var i = 0; i < IntegrationEventStore.MaximumEvents; i++) events.Add(new JObject { ["sequence"] = i + 1,
            ["episodeId"] = "e" + i, ["revision"] = 1, ["timestampUtc"] = time.AddSeconds(i).ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = new JObject { ["contacts"] = new JArray() } });
        state["events"] = events; state["nextSequence"] = IntegrationEventStore.MaximumEvents + 1;
        state.Remove("checksum");
        // This fixture constructs a valid persisted journal at the retention limit.
        using (var sha = SHA256.Create()) state["checksum"] = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(state.ToString(Formatting.None))));
        File.WriteAllText(path, state.ToString(Formatting.None)); journal = new IntegrationEventStore(root);
        Require(journal.Publish("newest", time.AddDays(1), 1, new JObject { ["contacts"] = new JArray() }), "new result was not retained");
        Require(!journal.Publish("e0", time, 1, new JObject()), "expired episode was republished on reconciliation");
        ThrowsResync(() => journal.Read(stream, 0, 32));
        var resumed = journal.Read(stream, 1, 32); Require((long?)resumed["oldestSequence"] == 2, "retention advanced the wrong boundary");
        Require(new FileInfo(path).Length <= IntegrationEventStore.MaximumBytes, "journal byte limit was exceeded");
        File.WriteAllText(path, "{truncated"); journal = new IntegrationEventStore(root);
        Require(journal.StreamId != stream && Directory.GetFiles(root, "*.corrupt-*").Length == 1, "corruption must preserve evidence and reset stream identity");
        ThrowsResync(() => journal.Read(stream, 1, 1));
        Require(journal.Publish("durable-before-corruption", time, 2, new JObject { ["contacts"] = new JArray() }),
            "corruption recovery must not expire durable analyses whose history publication has not completed");
        var replacementStream = journal.StreamId;
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"nextSequence\":2", "\"nextSequence\":3"));
        journal = new IntegrationEventStore(root);
        Require(journal.StreamId != replacementStream && Directory.GetFiles(root, "*.corrupt-*").Length == 2,
            "valid JSON corruption with a stale checksum must not be accepted as a durable cursor");
        var malformed = JObject.Parse(File.ReadAllText(path)); malformed.Remove("nextSequence"); malformed.Remove("checksum");
        using (var sha = SHA256.Create()) malformed["checksum"] = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(malformed.ToString(Formatting.None))));
        File.WriteAllText(path, malformed.ToString(Formatting.None));
        Require(new IntegrationEventStore(root).LatestSequence == 0 && Directory.GetFiles(root, "*.corrupt-*").Length == 3,
            "a checksummed journal without a sequence boundary must be quarantined instead of permanently failing reads");
    }
    private static void CorruptJournalRebuildsPendingAnalysis(Fixture f)
    {
        var capture = Guid.NewGuid(); var record = Record("pending-history", DateTime.UtcNow.AddMinutes(-2), "physical:pending");
        var manifest = Commit(f, capture, new[] { record });
        var originalStream = (string)f.Call("hello")["events"]!["streamId"]!;
        File.WriteAllText(Path.Combine(f.Root, "integration", "events-v1.json"), "{broken");
        f.Data = f.CreateData();
        Require((string?)f.Call("hello")["events"]?["streamId"] != originalStream, "corruption fixture must establish a new event epoch");
        f.Data.ReconcileCommitted();
        Require(f.Repository.LoadAll().Count == 0 && ((JArray)f.Call("events.read")["events"]!).Count == 1 &&
            (string?)f.Call("landings.list")["items"]?[0]?["id"] == record.Id,
            "journal corruption recovery lost an already committed analysis that still lacks a history file");
        f.Store.SaveHistory(manifest, f.Repository, f.RepositoryGate);
        var input = f.Store.ReadInputs(manifest);
        f.Store.CommitFacilities(new RuntimeFinalFacilities(capture, input.Episode.InitialFacilities, input.Episode.DecisionJournal?.Snapshot()));
        var report = f.Store.ReadAnalysis(f.Store.Read(capture)); report.Records[0].Airport = "EGLL";
        f.Store.CommitReport(f.Store.Read(capture), report, f.Repository, f.RepositoryGate);
        f.Data = f.CreateData(); f.Data.ReconcileCommitted();
        Require((int?)f.Call("events.read")["events"]?[1]?["revision"] == 2,
            "a later final report for the recovered analysis was incorrectly expired");
    }
    private static EpisodeWorkManifest Commit(Fixture f, Guid id, IReadOnlyList<LandingRecord> records)
    {
        var episode = new LandingEpisodeEventArgs(1, new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 } }, "MSFS", "Test", "TEST", "TEST",
            Array.Empty<AirportFacility>(), Array.Empty<string>(), new TelemetryDecisionJournal(), persistentId: id);
        var manifest = f.Store.CommitInputs(episode, records[0].TimestampUtc, Array.Empty<AirportFacility>(), false, 1);
        f.Store.CommitAnalysis(manifest, ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), records, new TelemetryDecisionJournal().Snapshot())); return manifest;
    }
    private static LandingRecord Record(string id, DateTime time, string? episode = null, int contact = 1, int count = 1) => new LandingRecord
    { Id = id, TimestampUtc = time, RecordingEpisodeId = episode, AircraftTitle = "API Test Aircraft", AircraftType = "TEST", InertialFpm = -142,
        SurfaceFpm = double.NaN, ContactNumber = contact, ContactCount = count, TouchdownLatitudeDegrees = 0, TouchdownLongitudeDegrees = 0 };
    private static string Request(string method, JObject? parameters = null) => new JObject
    { ["id"] = "test", ["protocolVersion"] = 1, ["method"] = method, ["params"] = parameters ?? new JObject() }.ToString(Formatting.None);
    private static void ThrowsResync(Action action)
    { try { action(); } catch (IntegrationApiException error) when (error.Code == "ResyncRequired") { return; } throw new InvalidOperationException("Expected ResyncRequired."); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void WithFixture(Action<Fixture> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "integration-data-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { test(new Fixture(root)); }
        finally
        { if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture path."); Directory.Delete(root, true); }
    }
    private sealed class Fixture
    {
        public Fixture(string root) { Root = root; Repository = new LandingRepository(Path.Combine(root, "landings")); Store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), Engine); Data = CreateData(); }
        public string Root { get; }
        public LandingRepository Repository { get; }
        public object RepositoryGate { get; } = new object();
        public EpisodeTransactionStore Store { get; }
        public IntegrationApiData Data { get; set; }
        public IntegrationApiData CreateData() => new IntegrationApiData(Path.Combine(Root, "integration"), Repository, RepositoryGate, Store,
            () => new RuntimePresentationSnapshot(1, new RecorderStatusEventArgs(RecorderState.Connected, "connected", "secret-token"), false, Guid.Empty, 0, false,
                Array.Empty<RuntimeEpisodeNotice>(), Array.Empty<SupervisionSnapshot>(), Guid.Empty, null),
            () => new[] { new SupervisionSnapshot("test", 1, SupervisionState.Running, 0, "C:\\private-path\\secret-token") });
        public JObject Call(string method, JObject? parameters = null)
        {
            var response = JObject.Parse(Data.Handle(Request(method, parameters)));
            if (response["error"] != null) throw new InvalidOperationException(response.ToString()); return (JObject)response["result"]!;
        }
        public string? Error(string request) => (string?)JObject.Parse(Data.Handle(request))["error"]?["code"];
    }
}
