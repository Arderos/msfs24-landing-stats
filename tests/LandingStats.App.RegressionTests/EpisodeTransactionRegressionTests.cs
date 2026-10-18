using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class EpisodeTransactionRegressionTests
{
    private const string Engine = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public static void RunAll()
    {
        WithStore(RecoverEveryPublishedStage);
        WithStore(DeleteWinsAfterCrash);
        WithStore(ValidateInputsAndPolicy);
        WithStore(RetainExactEngine);
        WithStore(ArchivePublicationIsStable);
        WithStore(FinalScenerySurvivesIndependentWriters);
        WithStore((store, root) => CompletePipeline(store, root).GetAwaiter().GetResult());
    }

    private static LandingEpisodeEventArgs Episode(Guid id)
    {
        var sample = new TelemetrySample { Sequence = 37, SimulationTimeSeconds = 2.5, MotionSimulation = true,
            VerticalSpeedFps = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000042)),
            HostElapsedSeconds = BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000)) };
        sample.ContactPointCompression[3] = 0.12345678901234567;
        var decisions = new TelemetryDecisionJournal(); decisions.Record(2.5, "test", "captured", "Original decision.");
        decisions.RestoreDroppedEntryCount(7);
        return new LandingEpisodeEventArgs(42, new[] { sample }, "MSFS", "Original aircraft", "TEST", "Model",
            Array.Empty<AirportFacility>(), new[] { "Stick" }, decisions, persistentId: id);
    }
    private static ProcessedEpisode Analysis(Guid id, DateTime timestamp, string title = "Original analysis") =>
        ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), new[] { new LandingRecord
        { Id = id.ToString("N"), TimestampUtc = timestamp, AircraftTitle = title } }, new TelemetryDecisionJournal().Snapshot());

    private static void RecoverEveryPublishedStage(EpisodeTransactionStore store, string root)
    {
        var id = Guid.NewGuid(); var episode = Episode(id); var timestamp = new DateTime(2026, 10, 17, 2, 3, 4, DateTimeKind.Utc);
        var manifest = store.CommitInputs(episode, timestamp, Array.Empty<AirportFacility>(), true, 1);
        var inputHash = manifest.InputHash; var archiveId = manifest.ArchiveId;
        var directory = Path.Combine(store.Root, id.ToString("N"));
        // Crash after publishing input bytes, before publishing their marker.
        manifest.Stage = EpisodeWorkStage.Created; manifest.InputHash = "";
        DurableFile.WriteJson(Path.Combine(directory, "manifest.json"), manifest);
        episode.Samples[0].SimulationTimeSeconds = 999;
        manifest = store.CommitInputs(episode, timestamp.AddYears(1), Array.Empty<AirportFacility>(), false, 99);
        Check(manifest.InputHash == inputHash && manifest.TimestampUtc == timestamp && manifest.ArchiveId == archiveId &&
            manifest.AutomaticAllowedAtCapture && manifest.PolicyRevision == 1, "Interrupted input commit changed its identity/policy/bytes.");
        var input = store.ReadInputs(manifest);
        Check(input.Episode.Samples[0].SimulationTimeSeconds == 2.5 && input.Episode.PersistentId == id &&
            input.Episode.DecisionJournal!.Snapshot().DroppedEntryCount == 7, "Binary inputs or decisions did not survive recovery.");
        Check(TelemetryBinaryCodec.Encode(input.Episode.Samples[0]).SequenceEqual(TelemetryBinaryCodec.Encode(Episode(id).Samples[0])),
            "Persistence changed a floating-point bit, NaN payload, signed zero or fixed-array member.");

        store.CommitAnalysis(manifest, Analysis(id, timestamp));
        var analysisHash = manifest.AnalysisHash;
        // Crash after publishing analysis bytes, before publishing their marker.
        manifest.Stage = EpisodeWorkStage.InputsCommitted; manifest.AnalysisHash = "";
        DurableFile.WriteJson(Path.Combine(directory, "manifest.json"), manifest);
        var recovered = store.CommitAnalysis(manifest, Analysis(id, timestamp, "Must not replace committed output"));
        Check(recovered.Records.Single().AircraftTitle == "Original analysis" && manifest.AnalysisHash == analysisHash,
            "A repeated analysis replaced the frozen result.");
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
        var saved = store.SaveHistory(manifest, repository, gate);
        var original = repository.Export(id.ToString("N"));
        manifest.Stage = EpisodeWorkStage.Analysed;
        DurableFile.WriteJson(Path.Combine(directory, "manifest.json"), manifest);
        File.Delete(repository.IndexPath);
        repository = new LandingRepository(repository.RootPath);
        store.SaveHistory(manifest, repository, gate);
        Check(repository.LoadAll().Count == 1 && repository.Export(id.ToString("N")).SequenceEqual(original),
            "Recovery duplicated or rewrote a committed landing, or failed to repair its index.");
        var archive = Path.Combine(root, "original.zip"); File.WriteAllBytes(archive, new byte[] { 1, 2, 3 });
        ReadyReport(store, manifest, repository, gate);
        store.CommitArchive(manifest, archive, null, 1); store.MarkOutboxEnqueued(manifest); store.Complete(manifest);
        Check(store.Read(id).Stage == EpisodeWorkStage.Completed && store.Enumerate().Single() == id,
            "Valid stage graph did not finish durably.");
    }

    private static void DeleteWinsAfterCrash(EpisodeTransactionStore store, string root)
    {
        var id = Guid.NewGuid(); var timestamp = DateTime.UtcNow;
        var manifest = store.CommitInputs(Episode(id), timestamp, Array.Empty<AirportFacility>(), false, 3);
        store.CommitAnalysis(manifest, Analysis(id, timestamp));
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
        store.SaveHistory(manifest, repository, gate);
        var path = Directory.GetFiles(repository.RootPath, "*.landing.json.gz").Single(); var bytes = File.ReadAllBytes(path);
        var staleIndex = File.ReadAllBytes(repository.IndexPath);
        repository.Delete(id.ToString("N"));
        // Represents a crash after durable delete intent, before deleting detail/index.
        File.WriteAllBytes(path, bytes); File.WriteAllBytes(repository.IndexPath, staleIndex);
        repository = new LandingRepository(repository.RootPath);
        Check(repository.LoadAll().Count == 0 && !repository.Contains(id.ToString("N")), "Index reconciliation resurrected a deleted landing.");
        Check(store.SaveHistory(manifest, repository, gate).Records.Count == 0 && repository.LoadAll().Count == 0,
            "Delayed history recovery resurrected a deleted landing.");
        Throws<InvalidOperationException>(() => repository.Import(bytes));
        ReadyReport(store, manifest, repository, gate);
        Check(repository.LoadAll().Count == 0, "A late report resurrected a deleted landing.");
        store.CommitArchive(manifest, null, "capture_opted_out", 3); store.MarkOutboxEnqueued(manifest); store.Complete(manifest);
        Check(store.Read(id).ArchiveSkipReason == "capture_opted_out", "Legal skip did not finish with its durable reason.");
    }

    private static void ValidateInputsAndPolicy(EpisodeTransactionStore store, string root)
    {
        var id = Guid.NewGuid(); var timestamp = DateTime.UtcNow;
        var manifest = store.CommitInputs(Episode(id), timestamp, Array.Empty<AirportFacility>(), false, 3);
        var other = new EpisodeTransactionStore(store.Root, new string('b', 64));
        Throws<InvalidOperationException>(() => other.CommitAnalysis(manifest, Analysis(id, timestamp)));
        Throws<InvalidOperationException>(() => store.Complete(manifest));
        store.CommitAnalysis(manifest, Analysis(id, timestamp));
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
        store.SaveHistory(manifest, repository, gate); ReadyReport(store, manifest, repository, gate);
        var archive = Path.Combine(root, "blocked.zip"); File.WriteAllBytes(archive, new byte[] { 7 });
        Throws<InvalidOperationException>(() => store.CommitArchive(manifest, archive, null, 3));
        Throws<ArgumentException>(() => store.CommitArchive(manifest, null, "opted_out", 2));
        var input = Path.Combine(store.Root, id.ToString("N"), "inputs.bin.gz");
        var corrupt = File.ReadAllBytes(input); corrupt[corrupt.Length / 2] ^= 8; File.WriteAllBytes(input, corrupt);
        Throws<InvalidDataException>(() => store.ReadInputs(manifest));
        Check(File.ReadAllBytes(input).SequenceEqual(corrupt), "Invalid inputs were discarded or silently repaired.");
    }

    private static void ReadyReport(EpisodeTransactionStore store, EpisodeWorkManifest manifest, LandingRepository repository, object gate)
    {
        var input = store.ReadInputs(manifest);
        store.CommitFacilities(new RuntimeFinalFacilities(manifest.Id, input.Episode.InitialFacilities, input.Episode.DecisionJournal?.Snapshot()));
        store.CommitReport(manifest, store.ReadAnalysis(manifest), repository, gate);
    }
    private static async Task CompletePipeline(EpisodeTransactionStore store, string root)
    {
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
        var work = new EpisodeWorkCoordinator(store, new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), repository, gate), repository, gate);
        var archives = new BugReportRepository(Path.Combine(root, "private-archives"));
        var outboxRoot = Path.Combine(root, "outbox"); var outbox = new DurableOutbox(outboxRoot, archives.RootPath); var policy = outbox.SetAutomaticEnabled(true);
        var updates = 0; var owner = new EpisodeRuntimeOwner(store, work, archives, outbox, _ => updates++);
        var id = Guid.NewGuid(); var episode = Episode(id); var time = DateTime.UtcNow;
        var manifest = store.CommitInputs(episode, time, Array.Empty<AirportFacility>(), true, policy.Revision);
        store.CommitAnalysis(manifest, Analysis(id, time));
        Check(!await owner.AdvanceAsync(id) && store.Read(id).Stage == EpisodeWorkStage.HistorySaved && repository.LoadAll().Count == 1 && updates == 1,
            "History waited for final scenery or report publication began without a terminal snapshot.");
        store.CommitFacilities(new RuntimeFinalFacilities(id, episode.InitialFacilities, null));
        // A temporarily unavailable publication destination keeps the episode
        // pending, instead of turning a storage failure into a successful skip.
        var archivePath = archives.AutomaticPath(time, manifest.ArchiveId); Directory.CreateDirectory(archivePath);
        try { await owner.AdvanceAsync(id); throw new InvalidOperationException("Archive publication unexpectedly succeeded."); } catch (IOException) { }
        Check(store.Read(id).Stage == EpisodeWorkStage.ReportReady && outbox.Enumerate().Count == 0, "Archive storage failure skipped or discarded pending evidence.");
        Directory.Delete(archivePath);
        // Interrupt the next boundary after the archive, before registration.
        Directory.CreateDirectory(Path.Combine(outboxRoot, manifest.ArchiveId.ToString("N") + ".json"));
        try { await owner.AdvanceAsync(id); throw new InvalidOperationException("Outbox registration unexpectedly succeeded."); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        manifest = store.Read(id); var bytes = File.ReadAllBytes(archivePath);
        Check(manifest.Stage == EpisodeWorkStage.ArchivePublished && manifest.ArchiveHash == EpisodeTransactionStore.Hash(archivePath), "Published archive lost its recovery marker.");
        Directory.Delete(Path.Combine(outboxRoot, manifest.ArchiveId.ToString("N") + ".json"));
        Check(await owner.AdvanceAsync(id) && store.Read(id).Stage == EpisodeWorkStage.Completed && outbox.Enumerate().Single() == manifest.ArchiveId &&
            bytes.SequenceEqual(File.ReadAllBytes(archivePath)) && updates == 2, "Pipeline recovery changed archive bytes, repeated presentation or failed to register the original archive.");
        var worker = new LandingStats.Runtime.Contracts.WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, LandingStats.Runtime.Contracts.ComponentRole.Upload);
        var claim = outbox.TryClaim(manifest.ArchiveId, worker)!; Check(outbox.Acknowledge(claim), "Fixture delivery did not acknowledge its archive.");
        Check(!await owner.AdvanceAsync(id) && !File.Exists(archivePath) && updates == 2, "A completed episode recreated an already delivered archive.");
    }
    private static void FinalScenerySurvivesIndependentWriters(EpisodeTransactionStore store, string root)
    {
        var id = Guid.NewGuid(); var timestamp = DateTime.UtcNow;
        var staleAnalysisManifest = store.CommitInputs(Episode(id), timestamp, Array.Empty<AirportFacility>(), true, 1);
        var episode = store.ReadInputs(staleAnalysisManifest).Episode;
        var final = new EpisodeFacilitySnapshot(episode.EpisodeId, Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), episode.Samples,
            new[] { new RunwayFacility { AirportIdent = "FINAL", WidthMeters = 45 } }, new[] { new AirportFacility { Ident = "FINAL" } }, true, false, true);
        store.CommitFacilities(new RuntimeFinalFacilities(id, final, new TelemetryDecisionJournal().Snapshot()));
        var first = store.Read(id); var facilityHash = first.FacilityHash;
        // Recover the immutable facility file published before its manifest marker.
        first.FacilityHash = ""; var directory = Path.Combine(store.Root, id.ToString("N"));
        DurableFile.WriteJson(Path.Combine(directory, "manifest.json"), first);
        store.CommitFacilities(new RuntimeFinalFacilities(id, episode.InitialFacilities, null));
        store.CommitAnalysis(staleAnalysisManifest, Analysis(id, timestamp));
        Check(staleAnalysisManifest.FacilityHash == facilityHash && store.ReadFacilities(staleAnalysisManifest).Snapshot.FinalRunways.Single().WidthMeters == 45,
            "An independent analysis writer erased final scenery or an interrupted marker replaced its published bytes.");
        var repository = new LandingRepository(Path.Combine(root, "history")); var gate = new object();
        store.SaveHistory(staleAnalysisManifest, repository, gate);
        var report = Analysis(id, timestamp, "Frozen report"); report.Records[0].Airport = "FINAL";
        store.CommitReport(staleAnalysisManifest, report, repository, gate);
        var reportHash = staleAnalysisManifest.ReportHash;
        staleAnalysisManifest.Stage = EpisodeWorkStage.HistorySaved; staleAnalysisManifest.ReportHash = "";
        DurableFile.WriteJson(Path.Combine(directory, "manifest.json"), staleAnalysisManifest);
        var recovered = store.CommitReport(staleAnalysisManifest, Analysis(id, timestamp, "Must not replace"), repository, gate);
        Check(staleAnalysisManifest.ReportHash == reportHash && recovered.Records.Single().Airport == "FINAL", "Report recovery changed its frozen outcome.");
        store.SaveHistory(first, repository, gate);
        var current = repository.LoadDetail(repository.LoadAll().Single())!;
        Check(current.Airport == "FINAL", "Late history recovery replaced final scenery with the early analysis result.");
        var bytes = File.ReadAllBytes(Path.Combine(directory, "facilities.bin.gz")); bytes[bytes.Length / 2] ^= 16;
        File.WriteAllBytes(Path.Combine(directory, "facilities.bin.gz"), bytes);
        Throws<InvalidDataException>(() => store.ReadFacilities(store.Read(id)));
        Check(File.ReadAllBytes(Path.Combine(directory, "facilities.bin.gz")).SequenceEqual(bytes), "Invalid final scenery was discarded.");
    }

    private static void RetainExactEngine(EpisodeTransactionStore store, string root)
    {
        var source = Path.Combine(root, "build"); var retained = Path.Combine(root, "engines");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "engine.dll"), new byte[] { 1, 2, 3 });
        var first = ProcessingEngineLease.CaptureFiles(retained, source, new[] { "engine.dll" });
        var repeated = ProcessingEngineLease.CaptureFiles(retained, source, new[] { "engine.dll" });
        Check(first.Identity == repeated.Identity && first.DirectoryPath == repeated.DirectoryPath, "Same engine bytes created another lease.");
        File.WriteAllBytes(Path.Combine(source, "engine.dll"), new byte[] { 4, 5, 6 });
        var newer = ProcessingEngineLease.CaptureFiles(retained, source, new[] { "engine.dll" });
        File.Delete(Path.Combine(source, "engine.dll")); Directory.Delete(source);
        Check(first.Identity != newer.Identity &&
            File.ReadAllBytes(Path.Combine(ProcessingEngineLease.Open(retained, first.Identity).DirectoryPath, "engine.dll")).SequenceEqual(new byte[] { 1, 2, 3 }),
            "An update or removal of the source runtime changed the retained engine.");
        File.WriteAllBytes(Path.Combine(first.DirectoryPath, "engine.dll"), new byte[] { 8 });
        Throws<InvalidDataException>(() => ProcessingEngineLease.Open(retained, first.Identity));
        Throws<InvalidDataException>(() => ProcessingEngineLease.CaptureFiles(retained, root, new[] { "../outside.dll" }));
    }
    private static void ArchivePublicationIsStable(EpisodeTransactionStore store, string root)
    {
        var queue = Path.Combine(root, "queue"); Directory.CreateDirectory(queue);
        var repository = new BugReportRepository(queue); var id = Guid.NewGuid(); var time = DateTime.UtcNow;
        var candidate = new BugReportCandidate(1, new[] { new TelemetrySample { Sequence = 1 } }, "MSFS", "Test", "T", "T",
            Array.Empty<string>(), Array.Empty<LandingRecord>());
        var path = repository.AutomaticPath(time, id);
        File.WriteAllBytes(path + ".tmp", new byte[] { 8, 9 });
        var published = AutomaticTelemetryArchive.TryCreate(repository, candidate, time, maximumQueuedFiles: 1, archiveId: id)!;
        var original = File.ReadAllBytes(published);
        Check(Directory.GetFiles(queue, "*.part").Length == 1 &&
            File.ReadAllBytes(Directory.GetFiles(queue, "*.part").Single()).SequenceEqual(new byte[] { 8, 9 }), "Interrupted archive bytes were removed.");
        var repeated = AutomaticTelemetryArchive.TryCreate(repository, candidate, time, maximumQueuedFiles: 1, archiveId: id);
        Check(repeated == published && File.ReadAllBytes(published).SequenceEqual(original), "Retry rebuilt a published archive or failed because its queue was full.");
        File.WriteAllBytes(published, new byte[] { 7 });
        Throws<InvalidDataException>(() => repository.ExistingAutomatic(time, id));
        Check(File.ReadAllBytes(published).SequenceEqual(new byte[] { 7 }), "Changed archive evidence was overwritten.");
    }
    private static void WithStore(Action<EpisodeTransactionStore, string> test)
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(parent, "et-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(new EpisodeTransactionStore(Path.Combine(root, "episodes"), Engine), root); }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test path.");
            Directory.Delete(root, true);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
