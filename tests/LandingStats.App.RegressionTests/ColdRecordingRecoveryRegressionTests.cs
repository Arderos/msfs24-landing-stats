using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ColdRecordingRecoveryRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cr-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(root);
        var done = false;
        try
        {
            var engines = Path.Combine(root, "e"); var lease = ProcessingEngineLease.Capture(engines, AppDomain.CurrentDomain.BaseDirectory);
            for (var scenario = 0; scenario < 4; scenario++) await Scenario(Path.Combine(root, scenario.ToString()), engines, lease.Identity, scenario);
            done = true;
        }
        finally { if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
    private static async Task Scenario(string root, string engines, string engine, int scenario)
    {
        Directory.CreateDirectory(root);
        var sessionRoot = Path.Combine(root, "s"); var episodes = Path.Combine(root, "p");
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 1000, 1000));
        RuntimeSession.Create(sessionRoot, identity, engine);
        var oldStore = new EpisodeTransactionStore(episodes, engine); var newStore = new EpisodeTransactionStore(episodes, new string('b', 64));
        var raw = new RawEffectStore(Path.Combine(sessionRoot, "raw"));
        var state = new RecordingRuntimeState(identity); var prepare = new ControllerInputPreparation(new ControllerJoiner(identity.Clock));
        var worker = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator);
        long order = 0, sourceSequence = 0, frames = 0; var originalTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var lastEffects = new List<RuntimeCapturedEpisode>(); var persisted = new List<Guid>();
        JournalCursor? last = null; var persist = true;
        using (var journal = RollingRuntimeJournal.Open(Path.Combine(sessionRoot, "journal"), identity))
        {
            void Feed(JournalRecordKind kind, byte[] bytes)
            {
                order++;
                var input = prepare.Prepare(new RuntimeInput(StableRuntimeIdentity.Command(identity.RuntimeId, checked((int)order)), kind,
                    1000 + order * 100, 1000 + order * 100, originalTime.AddMilliseconds(order * 100), bytes));
                last = journal.Append(input.Kind, input.AcceptedQpc, input.Encode()); journal.Commit();
                var durable = new DurableRuntimeInput(input, last); var effects = state.Apply(durable);
                lastEffects.AddRange(effects.Captures);
                if (!persist) return;
                raw.Commit(durable, effects.RawCapture);
                foreach (var capture in effects.Captures)
                {
                    oldStore.CommitInputs(capture.Episode, capture.TimestampUtc, new[] { new AirportFacility { Ident = "LBSF", LatitudeDegrees = 42, LongitudeDegrees = 23 } }, capture.AutomaticAllowed, capture.PolicyRevision); persisted.Add(capture.Episode.PersistentId);
                    if (capture.Episode.IsDiagnosticOnly) oldStore.CommitFacilities(new RuntimeFinalFacilities(capture.Episode.PersistentId, capture.Episode.InitialFacilities, capture.Episode.DecisionJournal?.Snapshot()));
                }
                foreach (var final in effects.Facilities) oldStore.CommitFacilities(final);
            }
            void Sim(RuntimePayload payload) => Feed(JournalRecordKind.SourceMessage, RuntimeWire.Encode(new RuntimeEnvelope(worker,
                payload is WorkerHello ? 0 : 1, ++sourceSequence, 1000 + (order + 1) * 100, payload)));
            void Frame(double time, bool ground) => Sim(new TelemetryFrameMessage(new TelemetrySample
            {
                Sequence = ++frames, SimulationTimeSeconds = time, SimulationDeltaSeconds = .1, MotionSimulation = true,
                OnGround = ground, AboveGroundLevelFeet = ground ? 0 : 100, VelocityWorldYFps = ground ? 0 : -3,
                GForce = 1, LatitudeDegrees = 42, LongitudeDegrees = 23
            }));
            Feed(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.DeliveryPolicy, true, 1).Encode());
            Feed(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.RawCapture, true).Encode());
            Sim(new WorkerHello("fixture", new string('a', 64), 1, identity.Clock.Id, identity.Clock.Frequency));
            Sim(new LifecycleMessage(SourceLifecycle.Connected)); Sim(new AircraftMessage("MSFS", "Original flight", "T", "M"));
            if (scenario == 3) journal.SaveCheckpoint(last!, state.CaptureCheckpoint());
            if (scenario == 1 || scenario == 3)
            {
                for (var i = 0; i < 220; i++) Frame(1 + i * .1, i >= 20);
                Check(persisted.Count == 1, "Fixture failed to reach the captured landing before its checkpoint.");
            }
            else { Frame(1, false); Frame(1.1, scenario == 0); }
            if (scenario != 3) journal.SaveCheckpoint(last!, state.CaptureCheckpoint());
            persist = false;
            Frame(scenario == 1 || scenario == 3 ? 23.1 : 1.2, scenario != 2); Frame(scenario == 1 || scenario == 3 ? 23.2 : 1.3, scenario != 2);
        }
        var end = new RuntimeInput(StableRuntimeIdentity.Command(identity.RuntimeId, 0), JournalRecordKind.RuntimeCommand,
            1000 + order * 100, 1000 + order * 100, originalTime.AddMilliseconds(order * 100), new RecordingCommand(RecordingCommandKind.PreviousRuntimeEnded).Encode());
        var expected = state.Apply(new DurableRuntimeInput(end, new JournalCursor(last!.Segment, last.Sequence + 1)));
        lastEffects.AddRange(expected.Captures);
        Check(lastEffects.Count == 1, "Cold-recovery fixture has no unique episode.");
        var original = lastEffects.Single();
        var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        var executor = new RetainedAnalysisExecutor(engines, Path.Combine(root, "j"), newStore, Guid.NewGuid(), clock);
        var starts = 0; executor.WorkerStarted += _ => Interlocked.Increment(ref starts);
        using (RuntimeSession.AcquireOwnership(sessionRoot))
        {
            try { await executor.RecoverRecordingAsync(sessionRoot, CancellationToken.None); throw new InvalidOperationException("Recovery stole a live runtime."); }
            catch (IOException) { }
        }
        Check(starts == 0, "Live runtime conflict unnecessarily launched a recovery process.");
        Guid acceptedArchive = Guid.Empty;
        if (scenario == 0)
        {
            // Everything is adopted, then the completion marker fails. Retry
            // must keep the first archive identity and exact bytes already saved.
            Directory.CreateDirectory(Path.Combine(sessionRoot, "recovered.json"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await executor.RecoverRecordingAsync(sessionRoot, timeout.Token); throw new InvalidOperationException("Blocked recovery marker was ignored."); }
            catch (IOException) { }
            acceptedArchive = newStore.Read(original.Episode.PersistentId).ArchiveId;
            Directory.Delete(Path.Combine(sessionRoot, "recovered.json"));
        }
        RecordingRecoveryBundle result;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30))) result = await executor.RecoverRecordingAsync(sessionRoot, timeout.Token);
        var manifest = newStore.Read(original.Episode.PersistentId); var inputEpisode = newStore.ReadInputs(manifest).Episode;
        Check(scenario != 3 || newStore.ReadInputs(manifest).Metadata.KnownAirports.Single().Ident == "LBSF", "Recovery replaced the authoritative airport cache.");
        Check(newStore.Enumerate().Count() == 1 && manifest.Engine == engine && manifest.TimestampUtc == original.TimestampUtc &&
            manifest.AutomaticAllowedAtCapture == original.AutomaticAllowed && manifest.PolicyRevision == 1 &&
            (acceptedArchive == Guid.Empty || manifest.ArchiveId == acceptedArchive), "Cold recovery duplicated the episode or used the new runtime's engine, time or policy.");
        Check(inputEpisode.Samples.Count == original.Episode.Samples.Count && inputEpisode.Samples.Zip(original.Episode.Samples,
            (a, b) => TelemetryBinaryCodec.Encode(a).SequenceEqual(TelemetryBinaryCodec.Encode(b))).All(equal => equal), "Recovery changed accepted telemetry.");
        Check(inputEpisode.IsDiagnosticOnly == (scenario == 2) && inputEpisode.AircraftTitle == "Original flight", "Recovery invented a contact or borrowed new-flight metadata.");
        var facilities = newStore.ReadFacilities(manifest);
        Check(!facilities.Snapshot.FinalLookupCompleted, "Cold recovery invented a fresh scenery reply.");
        Check(scenario != 1 || result.Episodes.Length == 0 && result.Facilities.Length == 1, "Recovery did not settle the final facilities of an already checkpointed landing.");
        var recoveredRaw = new RawEffectStore(raw.Root);
        var batches = recoveredRaw.Enumerate().Select(recoveredRaw.Read).OrderBy(item => item.Cursor).ToArray();
        Check(batches.SelectMany(item => item.Effects).OfType<RawDebugCaptureStartedEventArgs>().Count() == 1 &&
            batches.Last().Effects.Any(item => ReferenceEquals(item, EventArgs.Empty)), "Recovery lost the RAW start or final stop boundary.");
        Check(batches.SelectMany(item => item.Effects).OfType<RawDebugSampleEventArgs>().Count() >= 2, "Recovery lost RAW samples accepted after the checkpoint.");
        var count = starts; var hash = manifest.InputHash;
        await executor.RecoverRecordingAsync(sessionRoot, CancellationToken.None);
        Check(starts == count && newStore.Read(manifest.Id).InputHash == hash, "Completed recovery reran its old engine or rewrote its inputs.");
        Check(File.Exists(Path.Combine(sessionRoot, "recovered.json")) && Directory.EnumerateFiles(Path.Combine(sessionRoot, "journal"), "*.wal").Any(),
            "Recovery removed evidence before the caller's retention decision.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
