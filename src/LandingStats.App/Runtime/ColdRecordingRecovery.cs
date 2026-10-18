using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using LandingStats.App.Models;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class RecoveryArtifact
{
    [DataMember(Order = 1)] public Guid Id { get; set; }
    [DataMember(Order = 2)] public string Hash { get; set; } = "";
}

[DataContract]
internal sealed class RecordingRecoveryBundle
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Runtime { get; set; }
    [DataMember(Order = 3)] public string Engine { get; set; } = "";
    [DataMember(Order = 4)] public RecoveryArtifact[] Episodes { get; set; } = Array.Empty<RecoveryArtifact>();
    [DataMember(Order = 5)] public long LastSegment { get; set; }
    [DataMember(Order = 6)] public long LastSequence { get; set; }
    [DataMember(Order = 7)] public bool RecoveredTail { get; set; }
    [DataMember(Order = 8)] public RecoveryArtifact[] Facilities { get; set; } = Array.Empty<RecoveryArtifact>();
    [DataMember(Order = 9)] public RecoveryArtifact[] Raw { get; set; } = Array.Empty<RecoveryArtifact>();
}

/// <summary>Runs in the exact retained engine after the former app/job has
/// exited. It replays its old clock epoch and writes a private recovery bundle;
/// no live source or new-flight scenery is consulted.</summary>
internal static class ColdRecordingRecovery
{
    public static RecordingRecoveryBundle Recover(string sessionRoot, string output, string engine, Guid expectedRuntime)
    {
        var session = RuntimeSession.Read(sessionRoot);
        if (session.Runtime != expectedRuntime || session.Engine != engine) throw new InvalidDataException("Recovery engine/session differs from its durable source.");
        using var journal = RollingRuntimeJournal.Open(Path.Combine(sessionRoot, "journal"), session.Identity);
        var checkpoint = journal.ReadCheckpoint();
        var state = checkpoint == null ? new RecordingRuntimeState(journal.Identity) : RecordingRuntimeState.RestoreCheckpoint(checkpoint, journal.Identity);
        if ((state.Cursor == null) != (journal.CheckpointCursor == null) || (state.Cursor != null && state.Cursor.CompareTo(journal.CheckpointCursor) != 0))
            throw new InvalidDataException("Recording checkpoint cursor differs from the journal marker.");
        var store = new EpisodeTransactionStore(Path.Combine(output, "episodes"), engine);
        var raw = new RawEffectStore(Path.Combine(output, "raw"));
        var pending = new Dictionary<Guid, RuntimeFinalFacilities>();
        void Persist(DurableRuntimeInput input, RecordingRuntimeEffects effects)
        {
            if (effects.QuarantineReason != null) RecordingEffectPersistence.PersistQuarantine(Path.Combine(sessionRoot, "input-quarantine"), input, effects.QuarantineReason);
            raw.Commit(input, effects.RawCapture);
            foreach (var capture in effects.Captures)
            {
                store.CommitInputs(capture.Episode, capture.TimestampUtc, Array.Empty<AirportFacility>(), capture.AutomaticAllowed, capture.PolicyRevision);
                if (capture.Episode.IsDiagnosticOnly)
                    store.CommitFacilities(new RuntimeFinalFacilities(capture.Episode.PersistentId, capture.Episode.InitialFacilities, capture.Episode.DecisionJournal?.Snapshot()));
            }
            foreach (var final in effects.Facilities) pending[final.Episode] = final;
            if (pending.Count > 4096) throw new InvalidDataException("Recovery facility count exceeds its bound.");
            // A capture may already be behind the recording checkpoint, while
            // its final scenery is still in this tail. Preserve that final result
            // separately for import into the parent's already committed episode.
        }
        long lastReceipt = journal.Identity.Clock.OriginQpc;
        var last = journal.CheckpointCursor;
        foreach (var segment in journal.RetainedSegments)
            foreach (var item in journal.ReadSegment(segment))
            {
                lastReceipt = Math.Max(lastReceipt, item.Record.ReceiptQpc);
                if (last == null || item.Cursor.CompareTo(last) > 0) last = item.Cursor;
                if (journal.CheckpointCursor == null || item.Cursor.CompareTo(journal.CheckpointCursor) > 0)
                { var durable = new DurableRuntimeInput(RuntimeInput.Decode(item.Record), item.Cursor); Persist(durable, state.Apply(durable)); }
            }
        var boundary = new JournalCursor(last?.Segment ?? 1, checked((last?.Sequence ?? 0) + 1));
        var input = new RuntimeInput(StableRuntimeIdentity.Command(session.Runtime, 0), JournalRecordKind.RuntimeCommand, lastReceipt, lastReceipt,
            state.LastTimestampUtc, new RecordingCommand(RecordingCommandKind.PreviousRuntimeEnded).Encode());
        var ended = new DurableRuntimeInput(input, boundary); Persist(ended, state.Apply(ended));
        var episodes = store.Enumerate().ToArray();
        foreach (var id in episodes)
            if (pending.TryGetValue(id, out var final)) { store.CommitFacilities(final); pending.Remove(id); }
        foreach (var final in pending.Values)
        {
            using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
            writer.Write(0x31464652); writer.Write(final.Episode.ToByteArray()); writer.Write(final.Snapshot.EpisodeId);
            RuntimeCheckpointCodec.WriteJson(writer, FacilityPersistenceData.Capture(final.Snapshot));
            RuntimeCheckpointCodec.WriteSamples(writer, final.Snapshot.ApproachSamples);
            RuntimeCheckpointCodec.WriteJournal(writer, final.Decisions == null ? null : new LandingStats.Core.TelemetryDecisionJournal(final.Decisions));
            DurableFile.Write(Path.Combine(output, "facilities", final.Episode.ToString("N") + ".bin"), stream => stream.Write(memory.ToArray(), 0, (int)memory.Length));
        }
        if (episodes.Length > 4096) throw new InvalidDataException("Recovery episode count exceeds its bound.");
        var bundle = new RecordingRecoveryBundle { Runtime = session.Runtime, Engine = engine,
            Episodes = episodes.Select(id => new RecoveryArtifact { Id = id, Hash = EpisodeTransactionStore.Hash(Path.Combine(store.Root, id.ToString("N"), "manifest.json")) }).ToArray(),
            Facilities = pending.Keys.Select(id => new RecoveryArtifact { Id = id, Hash = EpisodeTransactionStore.Hash(FacilityPath(output, id)) }).ToArray(),
            Raw = raw.Enumerate().Select(id => new RecoveryArtifact { Id = id, Hash = EpisodeTransactionStore.Hash(raw.PathFor(id)) }).ToArray(),
            LastSegment = last?.Segment ?? 1, LastSequence = last?.Sequence ?? 0, RecoveredTail = journal.RecoveredTail };
        Validate(bundle, session);
        DurableFile.WriteJson(Path.Combine(output, "result.json"), bundle); return bundle;
    }
    internal static string FacilityPath(string output, Guid id) => Path.Combine(output, "facilities", id.ToString("N") + ".bin");
    internal static RuntimeFinalFacilities ReadFacility(string output, RecoveryArtifact item)
    {
        var path = FacilityPath(output, item.Id); EpisodeTransactionStore.Verify(path, item.Hash);
        using var file = File.OpenRead(path);
        if (file.Length > 256L * 1024 * 1024) throw new InvalidDataException("Recovered facilities exceed their bound.");
        using var reader = new BinaryReader(file);
        if (reader.ReadInt32() != 0x31464652 || new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)) != item.Id) throw new InvalidDataException("Invalid recovered facility identity.");
        var ordinal = reader.ReadInt64(); var snapshot = RuntimeCheckpointCodec.ReadJson<FacilityPersistenceData>(reader);
        var approach = RuntimeCheckpointCodec.ReadSamples(reader); var decisions = RuntimeCheckpointCodec.ReadJournal(reader);
        if (file.Position != file.Length || ordinal < 1) throw new InvalidDataException("Invalid recovered facility data.");
        return new RuntimeFinalFacilities(item.Id, snapshot.Restore(ordinal, approach), decisions?.Snapshot());
    }
    internal static void Validate(RecordingRecoveryBundle bundle, RuntimeSession session)
    {
        if (bundle.Version != 1 || bundle.Runtime != session.Runtime || bundle.Engine != session.Engine || bundle.LastSegment < 1 || bundle.LastSequence < 0)
            throw new InvalidDataException("Recovery bundle differs from its original runtime.");
        void ValidateEntries(RecoveryArtifact[] entries, int limit)
        {
            if (entries == null || entries.Length > limit || entries.Any(item => item == null || item.Id == Guid.Empty || !EpisodeTransactionStore.ValidHash(item.Hash)) ||
                entries.Select(item => item.Id).Distinct().Count() != entries.Length) throw new InvalidDataException("Invalid recovery bundle artifacts.");
        }
        ValidateEntries(bundle.Episodes, 4096); ValidateEntries(bundle.Facilities, 4096); ValidateEntries(bundle.Raw, 262144);
    }
    internal static RecordingRecoveryBundle Import(string sessionRoot, string output, EpisodeTransactionStore destination)
    {
        var session = RuntimeSession.Read(sessionRoot);
        var bundle = DurableFile.ReadJson<RecordingRecoveryBundle>(Path.Combine(output, "result.json"), 64L * 1024 * 1024); Validate(bundle, session);
        var source = new EpisodeTransactionStore(Path.Combine(output, "episodes"), session.Engine);
        foreach (var item in bundle.Episodes)
        {
            EpisodeTransactionStore.Verify(Path.Combine(source.Root, item.Id.ToString("N"), "manifest.json"), item.Hash);
            destination.ImportInputs(source, item.Id);
        }
        foreach (var item in bundle.Facilities) destination.CommitFacilities(ReadFacility(output, item));
        var raw = new RawEffectStore(Path.Combine(sessionRoot, "raw")); var recoveredRaw = new RawEffectStore(Path.Combine(output, "raw"));
        foreach (var item in bundle.Raw) raw.Import(recoveredRaw, item.Id, item.Hash);
        // Each import above is idempotent. Only this final flushed marker permits
        // the session's old recording journal to be collected.
        DurableFile.WriteJson(Path.Combine(sessionRoot, "recovered.json"), bundle); return bundle;
    }
}
