using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class StationaryFreezeRawRegressionTests
{
    internal static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "freeze-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            EncodingBudget(Path.Combine(root, "encoding-budget"));
            LiveByteBudget(Path.Combine(root, "live-byte-budget"));
            foreach (var held in new[] { 7, 8, 30, LandingEpisodeCoordinator.MaximumRawSamplesPerInput - 1 })
                Live(Path.Combine(root, "live-" + held), held);
            GapPrefix(Path.Combine(root, "gap-prefix"));
            foreach (var failure in new[] { 0, 1, 2, 3 }) Cold(Path.Combine(root, "cold-" + failure), failure);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void EncodingBudget(string root)
    {
        var input = BudgetInput(1);
        var sample = new TelemetrySample { Sequence = 17, SimulationTimeSeconds = 3 };
        sample.ResizeControllers(20, 8);
        var effects = new object[] { new RawDebugSampleEventArgs(sample) };
        var expected = RawEffectStore.Encode(input, effects, out _);
        var exact = RawEffectStore.Encode(input, effects, out _, expected.Length);
        Check(exact.SequenceEqual(expected), "Encoding at the exact byte budget changed the RAW format.");
        var store = new RawEffectStore(root);
        store.Commit(input, effects);
        Check(File.ReadAllBytes(store.PathFor(input.Input.Id)).SequenceEqual(exact) &&
            TelemetryCsv.Format(((RawDebugSampleEventArgs)new RawEffectStore(root).Read(input.Input.Id).Effects.Single()).Sample) == TelemetryCsv.Format(sample),
            "A byte-bounded RAW input did not preserve its durable round trip.");

        // The stop is a single byte. This exercises WriteByte at the limit;
        // trimming the sample budget exercises the bulk Write path separately.
        RejectBudget(() => RawEffectStore.Encode(input, new object[] { effects[0], EventArgs.Empty }, out _, exact.Length));
        RejectBudget(() => RawEffectStore.Encode(input, effects, out _, exact.Length - 1));
        // If the check ran only after complete serialization, it would reach
        // the invalid trailing effect instead of failing on the first extra byte.
        RejectBudget(() => RawEffectStore.Encode(input, new[] { effects[0], EventArgs.Empty, new object() }, out _, exact.Length));
        Check(store.Enumerate().Single() == input.Input.Id && File.ReadAllBytes(store.PathFor(input.Input.Id)).SequenceEqual(exact),
            "Rejected RAW encoding changed a previously durable input.");
    }

    private static void LiveByteBudget(string root)
    {
        var store = new RawEffectStore(root); var batches = new RawBatchPersistence(store);
        var first = BudgetInput(1);
        batches.Commit(first, new object[] { new RawDebugCaptureStartedEventArgs(Array.Empty<TelemetrySample>(), first.Input.TimestampUtc,
            "MSFS", "Byte budget fixture", "T", "M", Array.Empty<string>()) });
        batches.Flush();
        var sample = new TelemetrySample { Sequence = 1 }; sample.ResizeControllers(128, 8);
        var effect = new RawDebugSampleEventArgs(sample);
        var rejected = BudgetInput(2);
        var one = RawEffectStore.Encode(rejected, new object[] { effect }, out _).Length;
        var perEffect = RawEffectStore.Encode(rejected, new object[] { effect, effect }, out _).Length - one;
        var count = (RawBatchPersistence.MaximumPendingBytes - one) / perEffect + 2;
        Check(count <= LandingEpisodeCoordinator.MaximumRawSamplesPerInput,
            "The live byte-budget fixture exceeds the producer's frame bound.");
        batches.Commit(rejected, Enumerable.Repeat<object>(effect, count).ToArray());
        batches.Flush();
        var gap = DurableFile.ReadJson<RawCaptureGap>(Path.Combine(root, "gap.json"), 8192);
        Check(batches.PendingCount == 0 && gap.DroppedInputs == 1 && gap.Cursor!.CompareTo(rejected.Cursor) == 0 &&
            gap.Reason.Contains("RAW effect batch exceeds its byte budget"),
            "Live encoding did not stop at its byte budget with an explicit durable gap.");
        Check(new RawEffectStore(root).Enumerate().Single() == first.Input.Id && !File.Exists(store.PathFor(rejected.Input.Id)),
            "An oversized RAW input left a partial durable transaction or lost the preceding prefix.");
        var resumed = BudgetInput(3);
        batches.Commit(resumed, new object[] { effect }); batches.Flush();
        var restored = new RawEffectStore(root).Read(resumed.Input.Id).Effects;
        Check(restored.Count == 3 && restored[0] is RawCaptureGap && restored[1] is RawDebugCaptureStartedEventArgs && restored[2] is RawDebugSampleEventArgs,
            "A byte-budget failure prevented a later bounded RAW fragment from restarting.");
    }

    private static DurableRuntimeInput BudgetInput(int sequence) => new DurableRuntimeInput(new RuntimeInput(Guid.NewGuid(),
        JournalRecordKind.RuntimeCommand, sequence, sequence, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new RecordingCommand(RecordingCommandKind.Tick).Encode()), new JournalCursor(1, sequence));

    private static void RejectBudget(Action action)
    {
        try { action(); }
        catch (InvalidDataException error) when (error.Message.Contains("RAW effect batch exceeds its byte budget")) { return; }
        throw new InvalidOperationException("An oversized RAW encoding did not fail at its write budget.");
    }

    private static void Live(string root, int held)
    {
        using var flight = new Flight(root);
        flight.Begin();
        for (var n = 0; n < 60; n++) flight.Frame();
        flight.Freeze(held);
        var checkpoint = flight.State.CaptureCheckpoint();
        flight.State = RecordingRuntimeState.RestoreCheckpoint(checkpoint, flight.Identity);
        Check(checkpoint.SequenceEqual(flight.State.CaptureCheckpoint()), "Pending freeze checkpoint was not stable.");
        var released = flight.Release();
        Check(released.RawCapture.Count == held + 1, "The reducer did not release the entire held frame transaction.");
        flight.Command(RecordingCommandKind.RawCapture, false);
        flight.Batches.Flush();
        Check(flight.Batches.LastGap == null, "Stationary freeze dropped the live RAW buffer.");

        // Verify the disk reader, not only the write-side list. A reopened owner
        // must preserve the original input identity and its full effect order.
        var reopened = new RawEffectStore(flight.Raw.Root);
        Check(reopened.Read(flight.ReleasedInput!.Input.Id).Effects.Count == held + 1,
            "The RAW reader truncated a multi-frame input.");
        var archives = Path.Combine(root, "archives");
        var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives);
        var chunks = new RawChunkStore(Path.Combine(root, "chunks"), archives, outbox);
        var interrupted = false;
        var owner = new RawExportOwner(reopened, chunks, (_, __, ___) =>
        {
            // A chunk boundary inside the released input persists NextEffect
            // above the former eight-effect limit. Restart from that cursor.
            if (!interrupted) { interrupted = true; throw new IOException("Fixture publication callback interrupted after commit."); }
        }, chunkSize: 70);
        try { owner.Advance(); throw new InvalidOperationException("Expected a RAW publication interruption."); }
        catch (IOException) { }
        var saved = DurableFile.ReadJson<RawExportCheckpoint>(Path.Combine(reopened.Root, "export.json"), 8 * 1024 * 1024);
        if (held >= 30) Check(saved.NextEffect > 8, "The interruption did not exercise a cursor inside the released transaction.");
        owner = new RawExportOwner(new RawEffectStore(reopened.Root), chunks, (_, __, ___) => { }, chunkSize: 70);
        owner.Advance();
        Check(owner.CaughtUp, "RAW export failed to complete after restarting inside a released transaction.");
        Check(ReadRows(outbox, root).SequenceEqual(flight.Emitted.Select(TelemetryCsv.Format)),
            "Live RAW release/restart lost, duplicated or reordered telemetry.");
        new RawExportOwner(new RawEffectStore(reopened.Root), chunks,
            (_, __, ___) => throw new InvalidOperationException("Completed RAW capture was published twice."), 70).Advance();
    }

    private static void GapPrefix(string root)
    {
        using var flight = new Flight(root);
        flight.Begin();
        for (var n = 0; n < 10; n++) flight.Frame();
        Directory.CreateDirectory(flight.Raw.PathFor(flight.RawStart!.Input.Id).Replace(".bin", ".batch"));
        flight.Batches.Flush();
        Check(flight.Batches.LastGap != null, "The fixture did not create a preceding optional storage gap.");
        Directory.Delete(flight.Raw.PathFor(flight.RawStart.Input.Id).Replace(".bin", ".batch"));
        var held = LandingEpisodeCoordinator.MaximumRawSamplesPerInput - 1;
        flight.Freeze(held);
        flight.Release();
        flight.Command(RecordingCommandKind.RawCapture, false);
        flight.Batches.Flush();
        var stored = new RawEffectStore(flight.Raw.Root).Read(flight.ReleasedInput!.Input.Id);
        Check(stored.Effects.Count == RawEffectStore.MaximumEffectsPerInput && stored.Effects[0] is RawCaptureGap &&
            stored.Effects[1] is RawDebugCaptureStartedEventArgs,
            "A full released transaction did not fit alongside the gap and capture restart prefix.");
    }

    private static void Cold(string root, int failure)
    {
        using var flight = new Flight(root, journal: true);
        flight.Begin();
        for (var n = 0; n < 60; n++) flight.Frame();
        flight.Freeze(30);
        flight.Batches.Flush();
        flight.Journal!.Commit();
        flight.Journal.SaveCheckpoint(flight.LastInput!.Cursor, flight.State.CaptureCheckpoint());
        flight.PersistRaw = false;
        flight.Release();
        Guid middleInput = Guid.Empty;
        for (var n = 0; n < 600; n++)
        { flight.Frame(ground: true); if (n == 5) middleInput = flight.LastInput!.Input.Id; }
        flight.Journal.Commit();
        flight.Journal.Dispose();
        Check(flight.Captures.Count == 1, "The cold fixture did not produce one actual landing after the freeze.");
        var expected = flight.Captures.Single();
        var output = Path.Combine(root, "recovery");
        Directory.CreateDirectory(output);
        if (failure == 1) File.WriteAllText(Path.Combine(output, "raw"), "blocked optional RAW destination");
        if (failure == 3) Directory.CreateDirectory(Path.Combine(output, "raw", middleInput.ToString("N") + ".bin"));
        var result = ColdRecordingRecovery.Recover(flight.SessionRoot, output, Flight.Engine, flight.Identity.RuntimeId);
        Check(result.Episodes.Length == 1 && (result.RawGap != null) == (failure == 1 || failure == 3),
            "Optional RAW recovery failure blocked the landing or was not reported.");
        var priorDropped = result.RawGap?.DroppedInputs ?? 0;
        var invalidGap = new RecordingRecoveryBundle { Runtime = result.Runtime, Engine = result.Engine,
            LastSegment = result.LastSegment, LastSequence = result.LastSequence,
            RawGap = new RawCaptureGap(1, "future gap", new JournalCursor(result.LastSegment, result.LastSequence + 2)) };
        try { ColdRecordingRecovery.Validate(invalidGap, RuntimeSession.Read(flight.SessionRoot)); throw new InvalidOperationException("A future recovery gap cursor was accepted."); }
        catch (InvalidDataException) { }
        var destination = new EpisodeTransactionStore(Path.Combine(root, "landings"), new string('b', 64));
        if (failure == 2 || failure == 3)
        {
            // Fail optional adoption after the retained engine recovered all
            // data. The mandatory episode and final completion marker survive.
            var blocked = Path.Combine(flight.Raw.Root, result.Raw.First().Id.ToString("N") + ".bin");
            Directory.CreateDirectory(blocked);
        }
        var imported = ColdRecordingRecovery.Import(flight.SessionRoot, output, destination);
        Check(File.Exists(Path.Combine(flight.SessionRoot, "recovered.json")) && destination.Enumerate().Count() == 1,
            "Optional RAW adoption prevented durable landing recovery completion.");
        var manifest = destination.Read(expected.Episode.PersistentId);
        var actual = destination.ReadInputs(manifest).Episode;
        Check(manifest.Engine == Flight.Engine && actual.Samples.Select(TelemetryCsv.Format).SequenceEqual(expected.Episode.Samples.Select(TelemetryCsv.Format)),
            "Cold recovery changed the retained engine identity or accepted landing samples.");
        Check((imported.RawGap != null) == (failure != 0), "Recovery did not retain the optional RAW failure diagnostic.");
        if (failure != 0)
        {
            var diagnostic = DurableFile.ReadJson<RecordingRecoveryBundle>(Path.Combine(flight.SessionRoot, "recovered.json"), 64 * 1024 * 1024).RawGap;
            Check(diagnostic != null && diagnostic.DroppedInputs > 0 && diagnostic.Reason.Length > 0,
                "RAW loss was absent from the mandatory recovery marker.");
            if (failure == 3) Check(diagnostic!.DroppedInputs == priorDropped + result.Raw.Length && diagnostic.Reason.Contains(" | "),
                "RAW adoption discarded the preceding replay failure count or reason.");
            var prefixArchives = Path.Combine(root, "archives");
            var prefixOutbox = new DurableOutbox(Path.Combine(root, "outbox"), prefixArchives);
            var prefixChunks = new RawChunkStore(Path.Combine(root, "chunks"), prefixArchives, prefixOutbox);
            var prefixOwner = new RawExportOwner(new RawEffectStore(flight.Raw.Root), prefixChunks, (_, __, ___) => { }, 69);
            prefixOwner.Advance();
            Check(prefixOwner.CaughtUp && ReadRows(prefixOutbox, root).SequenceEqual(flight.Emitted.Take(60).Select(TelemetryCsv.Format)),
                "The recovery gap did not close and export the preserved RAW prefix.");
            new RawExportOwner(new RawEffectStore(flight.Raw.Root), prefixChunks,
                (_, __, ___) => throw new InvalidOperationException("The recovered RAW prefix was published twice."), 69).Advance();
            return;
        }
        var archives = Path.Combine(root, "archives");
        var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives);
        var chunks = new RawChunkStore(Path.Combine(root, "chunks"), archives, outbox);
        new RawExportOwner(new RawEffectStore(flight.Raw.Root), chunks, (_, __, ___) => { }, 69).Advance();
        Check(ReadRows(outbox, root).SequenceEqual(flight.Emitted.Select(TelemetryCsv.Format)),
            "Actual journal recovery lost or reordered RAW telemetry held across its checkpoint.");
        var originalHash = manifest.InputHash;
        ColdRecordingRecovery.Import(flight.SessionRoot, output, destination);
        Check(destination.Read(manifest.Id).InputHash == originalHash && destination.Enumerate().Count() == 1,
            "Repeated recovery adoption rewrote or duplicated the landing.");
    }

    private static IEnumerable<string> ReadRows(DurableOutbox outbox, string root)
    {
        var manifests = Directory.GetFiles(Path.Combine(root, "chunks"), "manifest.json", SearchOption.AllDirectories)
            .Select(path => DurableFile.ReadJson<RawChunkManifest>(path, 8192)).OrderBy(value => value.Ordinal);
        foreach (var manifest in manifests)
        {
            using var archive = ZipFile.OpenRead(outbox.Read(manifest.Id).Path);
            using var csv = new StreamReader(archive.GetEntry("telemetry.csv")!.Open());
            Check(csv.ReadLine() == TelemetryCsv.Header, "Export changed the telemetry schema.");
            string? line; while ((line = csv.ReadLine()) != null) yield return line;
        }
    }

    private sealed class Flight : IDisposable
    {
        internal const string Engine = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal readonly RecoveryJournalIdentity Identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 1000, 30000));
        internal readonly RawEffectStore Raw;
        internal readonly RawBatchPersistence Batches;
        internal readonly string SessionRoot;
        internal readonly RollingRuntimeJournal? Journal;
        internal RecordingRuntimeState State;
        internal DurableRuntimeInput? RawStart, ReleasedInput, LastInput;
        internal readonly List<TelemetrySample> Emitted = new List<TelemetrySample>();
        internal readonly List<RuntimeCapturedEpisode> Captures = new List<RuntimeCapturedEpisode>();
        internal bool PersistRaw = true;
        private readonly ControllerInputPreparation _prepare;
        private readonly WorkerIdentity _worker;
        private long _order, _sourceSequence, _frame;
        private readonly DateTime _utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        internal Flight(string root, bool journal = false)
        {
            SessionRoot = Path.Combine(root, "session");
            Raw = new RawEffectStore(Path.Combine(SessionRoot, "raw")); Batches = new RawBatchPersistence(Raw);
            State = new RecordingRuntimeState(Identity);
            _prepare = new ControllerInputPreparation(new ControllerJoiner(Identity.Clock));
            _worker = new WorkerIdentity(Identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator);
            if (journal)
            {
                RuntimeSession.Create(SessionRoot, Identity, Engine);
                Journal = RollingRuntimeJournal.Open(Path.Combine(SessionRoot, "journal"), Identity);
            }
        }
        internal void Begin()
        {
            Sim(new WorkerHello("fixture", Engine, 1, Identity.Clock.Id, Identity.Clock.Frequency));
            Sim(new LifecycleMessage(SourceLifecycle.Connected));
            Sim(new AircraftMessage("MSFS", "Stationary freeze fixture", "T", "M"));
            Command(RecordingCommandKind.DeliveryPolicy, true);
            Command(RecordingCommandKind.RawCapture, true); RawStart = LastInput;
        }
        internal void Command(RecordingCommandKind kind, bool enabled) => Apply(JournalRecordKind.RuntimeCommand,
            new RecordingCommand(kind, enabled, kind == RecordingCommandKind.DeliveryPolicy ? 1 : 0).Encode());
        internal RecordingRuntimeEffects Frame(bool ground = false) => Sim(new TelemetryFrameMessage(new TelemetrySample
        {
            Sequence = ++_frame, SimulationTimeSeconds = _frame / 30.0, SimulationDeltaSeconds = 1.0 / 30,
            MotionSimulation = true, OnGround = ground, AboveGroundLevelFeet = ground ? 0 : 100,
            PlaneAltitudeFeet = 1000, VelocityWorldYFps = ground ? 0 : -3, GForce = 1,
            LatitudeDegrees = 42, LongitudeDegrees = 23,
        }));
        internal void Freeze(int count)
        {
            for (var n = 0; n < count; n++)
            {
                if (n % 8 == 0) Observe(7);
                Check(Frame().RawCapture.Count == 0, "Frozen frame escaped the reducer's pending replay buffer.");
            }
        }
        internal RecordingRuntimeEffects Release()
        { Observe(0); var effects = Frame(); ReleasedInput = LastInput; return effects; }
        private void Observe(int mask) => Sim(new SourceObservationMessage(new ReplaySourceObservation(
            SourceObservationAvailability.Available, SourceObservationReason.None, mask, 0, (uint)_order,
            simulationTime: _frame / 30.0, latitude: 42, longitude: 23, altitudeFeet: 1000, pitch: 0, bank: 0, heading: 0)));
        private RecordingRuntimeEffects Sim(RuntimePayload payload) => Apply(JournalRecordKind.SourceMessage, RuntimeWire.Encode(
            new RuntimeEnvelope(_worker, payload is WorkerHello ? 0 : 1, ++_sourceSequence, 1000 + (_order + 1) * 1000, payload)));
        private RecordingRuntimeEffects Apply(JournalRecordKind kind, byte[] bytes)
        {
            _order++;
            var input = _prepare.Prepare(new RuntimeInput(StableRuntimeIdentity.Command(Identity.RuntimeId, checked((int)_order)), kind,
                1000 + _order * 1000, 1000 + _order * 1000, _utc.AddSeconds(_order / 30.0), bytes));
            var cursor = Journal?.Append(input.Kind, input.AcceptedQpc, input.Encode()) ?? new JournalCursor(1, _order);
            var durable = new DurableRuntimeInput(input, cursor); LastInput = durable;
            var effects = State.Apply(durable);
            Check(effects.QuarantineReason == null, "The fixture generated a rejected source input.");
            Emitted.AddRange(effects.RawCapture.OfType<RawDebugSampleEventArgs>().Select(value => value.Sample));
            Captures.AddRange(effects.Captures);
            if (PersistRaw) Batches.Commit(durable, effects.RawCapture);
            return effects;
        }
        public void Dispose() => Journal?.Dispose();
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
