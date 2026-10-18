using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static partial class EpisodeReportLifecycleRegressionTests
{
    public static void RunAll()
    {
        PendingFacilitiesDoNotBlockHistoryOrLaterCaptures();
        OptOutWhileAwaitingFacilitiesPreventsAutomaticArchive();
        PublishedBeforeCrashIsAdoptedAfterOptOut();
        FailedAnalysisRetainsExactInputsForRetry();
        DiagnosticOnlyAnalysisSkipsConfigurationAndHistoryButArchivesRawAndDecisions();
        LateAirportDecisionsAreArchivedExactlyOnce();
        LateAirportReplyUpdatesArchivedCopyAndHistoryWithoutRunway();
        PhysicalSegmentsPersistIndependentBounceEpisodesAndExcludeInactiveTail();
        RuntimeStopSettlesAcceptedFinalScenery();
        RuntimeStopDuringApproachDoesNotInventLanding();
        FacilitiesCannotEnrichAnotherEpisode();
    }

    private static LandingEpisodeEventArgs Episode(long id, TelemetryDecisionJournal? journal = null) =>
        new LandingEpisodeEventArgs(id, new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = id, MotionSimulation = true } },
            "MSFS", "Report fixture", "TEST", "TEST", Array.Empty<AirportFacility>(), Array.Empty<string>(), decisionJournal: journal, persistentId: System.Guid.NewGuid());

    private static ProcessedEpisode Analysis(TelemetryDecisionJournal? journal = null, params LandingRecord[] records) =>
        ProcessedEpisode.Completed(Array.Empty<AirportFacility>(), records, (journal ?? new TelemetryDecisionJournal()).Snapshot());

    private static void PendingFacilitiesDoNotBlockHistoryOrLaterCaptures()
    {
        using var fixture = new ReportPipelineFixture();
        var first = Episode(1); var second = Episode(2);
        fixture.Accept(first, Analysis(null, new LandingRecord()));
        fixture.Accept(second, Analysis()); fixture.Finalize(second);
        Assert(!fixture.Advance(first.PersistentId) && fixture.Repository.LoadAll().Count == 1,
            "Pending scenery blocked committed landing history or finalized an incomplete report.");
        Assert(fixture.Advance(second.PersistentId) && File.Exists(fixture.Archive(second.PersistentId)),
            "A pending first report blocked a later accepted capture.");
        Assert(fixture.Store.Read(first.PersistentId).Stage == EpisodeWorkStage.HistorySaved,
            "Pending report lost its durable continuation.");
        fixture.Finalize(first);
        Assert(fixture.Advance(first.PersistentId) && fixture.Outbox.Enumerate().Count == 2,
            "Settling the first report did not preserve both accepted captures.");
    }

    private static void OptOutWhileAwaitingFacilitiesPreventsAutomaticArchive()
    {
        using var fixture = new ReportPipelineFixture(); var episode = Episode(3);
        fixture.Accept(episode, Analysis()); Assert(!fixture.Advance(episode.PersistentId), "Report skipped pending facilities.");
        fixture.Outbox.SetAutomaticEnabled(false); fixture.Finalize(episode); fixture.Advance(episode.PersistentId);
        Assert(fixture.Outbox.Enumerate().Count == 0 && fixture.Store.Read(episode.PersistentId).ArchiveSkipReason == "current_policy_opted_out",
            "A report waiting for facilities escaped later opt-out.");
        Assert(fixture.Store.ReadInputs(fixture.Store.Read(episode.PersistentId)).Episode.Samples.Count == 1,
            "Opt-out discarded the original capture.");
    }

    private static void PublishedBeforeCrashIsAdoptedAfterOptOut()
    {
        using var fixture = new ReportPipelineFixture(); var episode = Episode(30);
        var manifest = fixture.Accept(episode, Analysis()); fixture.Finalize(episode);
        var candidate = new BugReportCandidate(episode.EpisodeId, episode.Samples, episode.Simulator, episode.AircraftTitle,
            episode.AircraftType, episode.AircraftModel, episode.ControlInputSources, Array.Empty<LandingRecord>());
        var published = AutomaticTelemetryArchive.TryCreate(fixture.Archives, candidate, manifest.TimestampUtc, archiveId: manifest.ArchiveId)!;
        var bytes = File.ReadAllBytes(published); fixture.Outbox.SetAutomaticEnabled(false);
        fixture.Advance(episode.PersistentId);
        Assert(fixture.Outbox.Read(manifest.ArchiveId).State == OutboxState.Revoked && File.ReadAllBytes(published).SequenceEqual(bytes),
            "A published archive lost durable ownership or escaped opt-out after recovery.");
    }

    private static void FailedAnalysisRetainsExactInputsForRetry()
    {
        using var fixture = new ReportPipelineFixture(); var episode = Episode(4);
        var manifest = fixture.Accept(episode); var original = File.ReadAllBytes(Path.Combine(fixture.Store.Root, manifest.Id.ToString("N"), "inputs.bin.gz"));
        fixture.Finalize(episode);
        // Lock the frozen-geometry input, exercising an actual storage failure
        // in the production owner rather than relying on a null dereference.
        var geometryPath = Path.Combine(fixture.Store.Root, manifest.Id.ToString("N"), "geometry.json");
        using (var blocked = new FileStream(geometryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var stop = new System.Threading.CancellationTokenSource())
        {
            var context = new ManagedComponentContext(1, stop.Token);
            var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = fixture.CreateOwner(fixture.Processor);
            owner.Failed += (_, __) => failed.TrySetResult(true);
            var running = Task.Run(() => owner.RunAsync(context));
            try { if (!failed.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Analysis owner did not record the injected storage failure."); }
            finally { stop.Cancel(); try { running.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } finally { context.Dispose(); } }
        }
        File.Delete(geometryPath);
        manifest = fixture.Store.Read(manifest.Id);
        Assert(manifest.Stage == EpisodeWorkStage.InputsCommitted && manifest.FailureCount == 1 &&
            original.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Store.Root, manifest.Id.ToString("N"), "inputs.bin.gz"))) &&
            fixture.Repository.LoadAll().Count == 0 && fixture.Outbox.Enumerate().Count == 0,
            "Analysis failure lost/replaced its immutable inputs or published a false successful result.");
        // A restored analysis owner resumes the same transaction, without a new capture.
        Assert(fixture.Advance(manifest.Id) && fixture.Store.Read(manifest.Id).Stage == EpisodeWorkStage.Completed,
            "A failed analysis could not resume through the production pipeline.");
        Assert(fixture.Store.Enumerate().Count() == 1 && File.Exists(fixture.Archive(manifest.Id)), "Recovery duplicated or lost its diagnostic archive.");
    }

    private static void DiagnosticOnlyAnalysisSkipsConfigurationAndHistoryButArchivesRawAndDecisions()
    {
        using var fixture = new ReportPipelineFixture();
        var samples = new[] {
            new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 10, MotionSimulation = true, OnGround = false },
            new TelemetrySample { Sequence = 2, SimulationTimeSeconds = 11, MotionSimulation = false, OnGround = false },
            new TelemetrySample { Sequence = 3, SimulationTimeSeconds = 11, MotionSimulation = true, OnGround = true } };
        var episode = new LandingEpisodeEventArgs(5, samples, "MSFS", "Fixture", "TEST", "TEST", Array.Empty<AirportFacility>(),
            Array.Empty<string>(), decisionJournal: new TelemetryDecisionJournal(), isDiagnosticOnly: true, persistentId: System.Guid.NewGuid());
        var result = ReportPipelineFixture.Complete(new EpisodeProcessor(null!, null!, null!).ProcessAsync(episode, Array.Empty<AirportFacility>(), DateTime.UtcNow));
        Assert(result.Records.Count == 0, "Diagnostic-only analysis touched configuration/history or created a landing.");
        fixture.Accept(episode, result); fixture.Finalize(episode); fixture.Advance(episode.PersistentId);
        using var zip = ZipFile.OpenRead(fixture.Archive(episode.PersistentId));
        using (var reader = new StreamReader(zip.GetEntry("telemetry.csv")!.Open()))
        {
            Assert(reader.ReadLine() == TelemetryCsv.Header, "Diagnostic raw header changed.");
            foreach (var sample in samples) Assert(reader.ReadLine() == TelemetryCsv.Format(sample), "Diagnostic raw observations changed.");
            Assert(reader.ReadLine() == null, "Diagnostic raw observations were duplicated.");
        }
        using var decisions = new StreamReader(zip.GetEntry("decisions.json")!.Open()); var json = decisions.ReadToEnd();
        Assert(json.Contains("physics_discontinuity_rejected") && json.Contains("diagnostic_only") && !json.Contains("analysis_failed") &&
            fixture.Repository.LoadAll().Count == 0, "Diagnostic report lost its rejection or created false landing history.");
    }

    private static void LateAirportDecisionsAreArchivedExactlyOnce()
    {
        using var fixture = new ReportPipelineFixture(); var journal = new TelemetryDecisionJournal();
        journal.Record(1, "runway.airports", "requested", "Fresh airport list requested.");
        var episode = Episode(6, journal); fixture.Accept(episode, Analysis(journal)); fixture.Advance(episode.PersistentId);
        journal.Record(2, "runway.airports", "ready", "Fresh airport list received after analysis.");
        fixture.Finalize(episode); fixture.Advance(episode.PersistentId);
        using var zip = ZipFile.OpenRead(fixture.Archive(episode.PersistentId));
        using var reader = new StreamReader(zip.GetEntry("decisions.json")!.Open()); var json = reader.ReadToEnd();
        foreach (var code in new[] { "requested", "ready" })
            Assert(System.Text.RegularExpressions.Regex.Matches(json, "\"code\":\"" + code + "\"").Count == 1,
                "Early and late airport decisions must each be archived exactly once.");
    }

    private static void LateAirportReplyUpdatesArchivedCopyAndHistoryWithoutRunway()
    {
        using var fixture = new ReportPipelineFixture(); var episode = Episode(7);
        fixture.Accept(episode, Analysis(null, new LandingRecord { TouchdownLatitudeDegrees = 40, TouchdownLongitudeDegrees = 20 }));
        Assert(!fixture.Advance(episode.PersistentId), "Report did not wait for late facilities.");
        fixture.Finalize(episode, new EpisodeFacilitySnapshot(episode.EpisodeId, Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(),
            Array.Empty<TelemetrySample>(), Array.Empty<RunwayFacility>(), new[] { new AirportFacility { Ident = "LATE", LatitudeDegrees = 40, LongitudeDegrees = 20 } }, true));
        fixture.Advance(episode.PersistentId);
        Assert(fixture.Reports[episode.PersistentId].Records.Single().Airport == "LATE" &&
            fixture.Repository.LoadDetail(fixture.Repository.LoadAll().Single())!.Airport == "LATE", "History and report disagree on the late airport.");
        using var zip = ZipFile.OpenRead(fixture.Archive(episode.PersistentId)); using var reader = new StreamReader(zip.GetEntry("landing-results.json")!.Open());
        Assert(reader.ReadToEnd().Contains("\"Airport\":\"LATE\""), "Published archive retained stale airport metadata.");
    }

    internal static void FacilitiesCannotEnrichAnotherEpisode()
    {
        using var fixture = new ReportPipelineFixture(); var episode = Episode(8); var other = Episode(9);
        var historical = new LandingRecord { AircraftTitle = "Historical fixture" }; fixture.Repository.Save(historical);
        var bytes = fixture.Repository.Export(historical.Id);
        fixture.Accept(episode, Analysis()); fixture.Accept(other, Analysis());
        var wrong = new EpisodeFacilitySnapshot(other.EpisodeId, Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(),
            Array.Empty<TelemetrySample>(), new[] { new RunwayFacility { AirportIdent = "WRONG" } }, Array.Empty<AirportFacility>(), true);
        try { fixture.Finalize(episode, wrong); throw new InvalidOperationException("Wrong-episode scenery was accepted."); }
        catch (InvalidDataException) { }
        fixture.Finalize(other, wrong); fixture.Advance(other.PersistentId);
        Assert(fixture.Store.Read(episode.PersistentId).FacilityHash.Length == 0 && fixture.Repository.Export(historical.Id).SequenceEqual(bytes),
            "Today's scenery changed another episode or a browsed historical landing.");
    }

    public static void ManualIntentIsDurableBeforeNetworkPreparation()
    {
        using var fixture = new ReportPipelineFixture(); var episode = Episode(10);
        fixture.Outbox.SetAutomaticEnabled(false); fixture.Accept(episode, Analysis()); fixture.Finalize(episode); fixture.Advance(episode.PersistentId);
        var root = Path.Combine(fixture.Root, "requests");
        ArchiveIntakeOwner Intake() => new ArchiveIntakeOwner(root, fixture.Archives.RootPath, null, Path.Combine(fixture.Root, "identity"), fixture.Store, fixture.Outbox, () => { });
        var id = Intake().RequestManual(episode.PersistentId);
        var path = Path.Combine(root, id.ToString("N") + ".json"); var bytes = File.ReadAllBytes(path);
        Assert(Intake().RequestManual(episode.PersistentId) == id && File.ReadAllBytes(path).SequenceEqual(bytes) &&
            DurableFile.ReadJson<ArchiveIntakeRequest>(path, 8192).Episode == episode.PersistentId && fixture.Outbox.Enumerate().Count == 0,
            "Manual report intent was not durable and idempotent before any network worker started.");
    }

    private static void RuntimeStopSettlesAcceptedFinalScenery()
    {
        using var fixture = new ReportPipelineFixture(); var stream = new RecordingFixture();
        RuntimeCapturedEpisode? captured = null; SceneryCommandMessage? final = null;
        for (var index = 0; index < 180 && captured == null; index++)
        {
            var effects = stream.Frame(index, index != 0);
            captured = effects.Captures.LastOrDefault() ?? captured;
            final = effects.Commands.Select(value => value.Command).OfType<SceneryCommandMessage>().LastOrDefault(value => value.Stage == LookupStage.Final && !value.Cancel) ?? final;
        }
        Assert(captured != null && final != null && !captured.Episode.FacilitiesTask.IsCompleted, "Shutdown fixture did not hold pending final scenery.");
        fixture.Accept(captured!.Episode, Analysis(null, new LandingRecord { TouchdownLatitudeDegrees = 40, TouchdownLongitudeDegrees = 20 }));
        fixture.Advance(captured.Episode.PersistentId);
        var snapshot = new SceneryQuerySnapshot(new[] { new RunwayFacility { AirportIdent = "ACTUAL", PrimaryName = "09", SecondaryName = "27",
            LatitudeDegrees = 40, LongitudeDegrees = 20, HeadingTrueDegrees = 90, LengthMeters = 3000, WidthMeters = 45 } },
            new[] { new AirportFacility { Ident = "ACTUAL", LatitudeDegrees = 40, LongitudeDegrees = 20 } }, true, true, 1, 1);
        byte[] data; using (var memory = new MemoryStream()) { using var writer = new BinaryWriter(memory); RuntimeCheckpointCodec.WriteJson(writer, SceneryCheckpointData.Capture(snapshot)); data = memory.ToArray(); }
        using var sha = SHA256.Create();
        stream.Send(new SceneryChunkMessage(Guid.NewGuid(), 0, data.Length, false, sha.ComputeHash(data), data), final);
        stream.Restore();
        var stop = stream.Stop();
        Assert(stop.Captures.Count == 0 && stop.Facilities.Count == 1 && stop.Facilities[0].Snapshot.FinalRunways.Single().AirportIdent == "ACTUAL",
            "Runtime stop lost accepted final geometry or duplicated an already completed capture.");
        fixture.Store.CommitFacilities(stop.Facilities[0]); fixture.Advance(captured.Episode.PersistentId);
        Assert(fixture.Reports[captured.Episode.PersistentId].Records.Single().Airport == "ACTUAL" &&
            fixture.Repository.LoadDetail(fixture.Repository.LoadAll().Single())!.Airport == "ACTUAL",
            "Shutdown report and durable landing history disagree on accepted terminal scenery.");
    }

    private static void RuntimeStopDuringApproachDoesNotInventLanding()
    {
        var stream = new RecordingFixture(); stream.Frame(0, false); Assert(stream.State.IsRecording, "Approach fixture did not arm.");
        stream.Restore(); var stop = stream.Stop();
        Assert(!stream.State.IsRecording && stop.Captures.Count == 0 && stop.Retirements.Count == 1,
            "Stopping an airborne approach invented a physical landing or failed to acknowledge shutdown.");
    }

    private sealed class RecordingFixture
    {
        private readonly RecoveryJournalIdentity _identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 1000, 1000));
        private readonly WorkerIdentity _worker;
        private readonly ControllerInputPreparation _prepare;
        private long _message, _cursor;
        public RecordingRuntimeState State { get; private set; }
        public RecordingFixture()
        {
            State = new RecordingRuntimeState(_identity); _worker = new WorkerIdentity(_identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator);
            _prepare = new ControllerInputPreparation(new ControllerJoiner(_identity.Clock));
            Send(new WorkerHello("fixture", new string('a', 64), 1, _identity.Clock.Id, 1000)); Send(new LifecycleMessage(SourceLifecycle.Connected));
        }
        public RecordingRuntimeEffects Send(RuntimePayload payload, SceneryCommandMessage? scenery = null)
        {
            var envelope = new RuntimeEnvelope(_worker, payload is WorkerHello ? 0 : 1, ++_message, 1000 + _message * 100, payload,
                episodeId: scenery?.Episode ?? Guid.Empty, lookupId: scenery?.Lookup ?? Guid.Empty, lookupStage: scenery?.Stage ?? LookupStage.None);
            return Apply(JournalRecordKind.SourceMessage, RuntimeWire.Encode(envelope));
        }
        public RecordingRuntimeEffects Frame(int index, bool ground) => Send(new TelemetryFrameMessage(new TelemetrySample {
            Sequence = index + 1, SimulationTimeSeconds = 1 + index * .1, SimulationDeltaSeconds = .1, MotionSimulation = true,
            OnGround = ground, AboveGroundLevelFeet = ground ? 0 : 100, VelocityWorldYFps = ground ? 0 : -3, LatitudeDegrees = 40, LongitudeDegrees = 20, GForce = 1 }));
        public RecordingRuntimeEffects Stop() => Apply(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.Stop).Encode());
        public void Restore() => State = RecordingRuntimeState.RestoreCheckpoint(State.CaptureCheckpoint(), _identity);
        private RecordingRuntimeEffects Apply(JournalRecordKind kind, byte[] bytes)
        {
            var qpc = 1000 + ++_cursor * 100;
            var input = new RuntimeInput(Guid.NewGuid(), kind, qpc, qpc, new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(_cursor * 100), bytes);
            return State.Apply(new DurableRuntimeInput(_prepare.Prepare(input), new JournalCursor(1, _cursor)));
        }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
