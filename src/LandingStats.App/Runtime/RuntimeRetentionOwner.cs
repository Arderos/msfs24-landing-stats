using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class RuntimeCollectionReceipt
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Runtime { get; set; }
    [DataMember(Order = 3)] public string Engine { get; set; } = "";
}

/// <summary>Reclaims only redundant runtime data. Pending uploads, quarantined
/// evidence, episode inputs for manual reports and user history are not aged out.</summary>
internal sealed class RuntimeRetentionOwner
{
    private readonly string _root, _current;
    private readonly EpisodeTransactionStore _episodes;
    private readonly Action<Guid, string>? _failed;
    public RuntimeRetentionOwner(string root, string current, EpisodeTransactionStore episodes, Action<Guid, string>? failed = null)
    { _root = root; _current = current; _episodes = episodes; _failed = failed; }
    public async Task RunAsync(ManagedComponentContext owner)
    {
        owner.MarkReady(); long nextCollection = 0;
        while (true)
        {
            owner.CancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= nextCollection)
            {
                owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.NewGuid());
                Collect(); nextCollection = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
                owner.Progress(true, DependencyWait.Idle);
            }
            else owner.Progress(false, DependencyWait.Idle);
            await owner.WaitForWorkAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
    }
    public void Collect()
    {
        foreach (var root in new[] { Path.Combine(_root, "jobs"), Path.Combine(_root, "cloud", "jobs") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root).Take(65536))
            {
                try
                {
                    var name = Path.GetFileName(directory);
                    if (Guid.TryParseExact(name, "N", out _)) RuntimeScratchLease.Collect(root, directory);
                    else if (name.StartsWith(".retired-", StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(9), "N", out _))
                    {
                        // Only our atomic retirement rename creates this name.
                        RuntimeScratchLease.RequireUnlinked(directory);
                        using (var marker = new FileStream(Path.Combine(directory, "job.owner"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                            if (marker.Length != 3 || marker.ReadByte() != 1 || marker.ReadByte() != 1) continue;
                        Directory.Delete(directory, true);
                    }
                }
                catch (Exception error) when (Retain(error)) { /* In-use or unknown data stays in place. */ }
            }
        }
        var sessions = Path.Combine(_root, "sessions");
        if (Directory.Exists(sessions))
            foreach (var retired in Directory.EnumerateDirectories(sessions, ".collected-*"))
            {
                try
                {
                    if (!Guid.TryParseExact(Path.GetFileName(retired).Substring(11), "N", out var id)) continue;
                    RuntimeScratchLease.RequireUnlinked(retired);
                    var receipt = DurableFile.ReadJson<RuntimeCollectionReceipt>(Path.Combine(retired, "collected.json"), 4096);
                    if (receipt.Version != 1 || receipt.Runtime != id) continue;
                    Directory.Delete(retired, true);
                }
                catch (Exception error) when (Retain(error)) { }
            }
        foreach (var directory in (Directory.Exists(sessions) ? Directory.EnumerateDirectories(sessions) : Array.Empty<string>())
            .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)).Take(65536))
        {
            if (string.Equals(directory, _current, StringComparison.OrdinalIgnoreCase)) continue;
            try { CollectSession(directory); }
            catch (Exception error) when (Retain(error)) { /* No completion proof: retain every source byte. */ }
        }
        CollectEngines(sessions);
        foreach (var id in _episodes.EnumeratePending())
        {
            try { if (_episodes.Read(id).Stage == EpisodeWorkStage.Completed) _episodes.ArchiveCompleted(id); }
            catch (Exception error) when (Retain(error)) { }
        }
    }
    private void CollectEngines(string sessions)
    {
        var root = Path.Combine(_root, "engines"); if (!Directory.Exists(root)) return;
        var pinned = new HashSet<string>(StringComparer.Ordinal) { _episodes.Engine };
        try
        {
            foreach (var session in (Directory.Exists(sessions) ? Directory.EnumerateDirectories(sessions) : Array.Empty<string>()).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)))
            {
                try { pinned.Add(RuntimeSessionDirectories.Validate(session).Engine); }
                catch (Exception error) when (error is FileNotFoundException || error is InvalidDataException || error is SerializationException)
                {
                    using var owner = RuntimeSession.AcquireExistingOwnership(session);
                    RuntimeScratchLease.RequireUnlinked(session);
                    foreach (var engine in RuntimeMetadataFailure.Pin(session, "runtime.json", root, error, _failed)) pinned.Add(engine);
                }
            }
            foreach (var id in _episodes.EnumeratePending())
            {
                try
                {
                    var episode = _episodes.Read(id);
                    if (episode.Stage < EpisodeWorkStage.ReportReady) pinned.Add(episode.Engine);
                }
                catch (Exception error) when (error is FileNotFoundException || error is InvalidDataException || error is SerializationException)
                {
                    var directory = Path.Combine(_episodes.Root, id.ToString("N"));
                    if (!Directory.Exists(directory)) directory = Path.Combine(_root, "episode-history", id.ToString("N"));
                    RuntimeScratchLease.RequireUnlinked(directory);
                    foreach (var engine in RuntimeMetadataFailure.Pin(directory, "manifest.json", root, error, _failed)) pinned.Add(engine);
                }
            }
        }
        catch (Exception error) when (Retain(error)) { return; } // An unreadable reference may pin any retained engine.
        foreach (var directory in Directory.EnumerateDirectories(root).Take(65536))
        {
            try
            {
                var name = Path.GetFileName(directory);
                if (EpisodeTransactionStore.ValidHash(name) && !pinned.Contains(name)) ProcessingEngineLease.CollectUnused(root, name);
                else if (name.StartsWith(".retired-", StringComparison.Ordinal) && EpisodeTransactionStore.ValidHash(name.Substring(9)))
                { RuntimeScratchLease.RequireUnlinked(directory); Directory.Delete(directory, true); }
            }
            catch (Exception error) when (Retain(error)) { /* A running or unverifiable engine remains pinned. */ }
        }
    }
    private void CollectSession(string directory)
    {
        RuntimeScratchLease.RequireUnlinked(directory);
        using var ownership = RuntimeSession.AcquireExistingOwnership(directory);
        var session = RuntimeSessionDirectories.Validate(directory);
        var receipt = Path.Combine(directory, "collected.json");
        if (!File.Exists(receipt))
        {
            var recovery = DurableFile.ReadJson<RecordingRecoveryBundle>(Path.Combine(directory, "recovered.json"), 64L * 1024 * 1024);
            ColdRecordingRecovery.Validate(recovery, session);
            var raw = DurableFile.ReadJson<RuntimeRawCompletion>(Path.Combine(directory, "raw-completed.json"), 4096);
            if (raw.Version != 1 || raw.Runtime != session.Runtime) throw new InvalidDataException("RAW completion is not for this session.");
            foreach (var item in recovery.Episodes)
            {
                var manifest = _episodes.Read(item.Id);
                if (manifest.Stage < EpisodeWorkStage.InputsCommitted) throw new InvalidDataException("Recovered episode has no durable input.");
                _episodes.ReadInputs(manifest);
            }
            foreach (var item in recovery.Facilities) _episodes.ReadFacilities(_episodes.Read(item.Id));
            PreserveEvidence(directory, session, recovery.RecoveredTail);
            RuntimeScratchLease.RequireUnlinked(directory);
            DurableFile.WriteJson(receipt, new RuntimeCollectionReceipt { Runtime = session.Runtime, Engine = session.Engine });
        }
        else
        {
            var saved = DurableFile.ReadJson<RuntimeCollectionReceipt>(receipt, 4096);
            if (saved.Version != 1 || saved.Runtime != session.Runtime || saved.Engine != session.Engine) throw new InvalidDataException("Invalid runtime collection receipt.");
        }
        // Rename while exclusively owning the session: a late reader cannot
        // recreate it, and a crash leaves a self-identifying cleanup directory.
        RuntimeScratchLease.RequireUnlinked(directory);
        var retired = Path.Combine(Path.GetDirectoryName(directory)!, ".collected-" + session.Runtime.ToString("N"));
        Directory.Move(directory, retired);
        Directory.Delete(retired, true);
    }
    private void PreserveEvidence(string directory, RuntimeSession session, bool recoveredTail)
    {
        var tails = Directory.EnumerateFiles(directory, "*.tail-*.bin", SearchOption.AllDirectories).ToArray();
        var quarantine = Path.Combine(directory, "input-quarantine");
        var files = tails.Concat(Directory.Exists(quarantine) ? Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories) : Array.Empty<string>()).ToList();
        // Legacy recovery could report a repaired tail without naming it. Keep
        // its journal, instead of guessing which bytes may be discarded.
        var journal = Path.Combine(directory, "journal");
        if (recoveredTail && tails.Length == 0 && Directory.Exists(journal)) files.AddRange(Directory.EnumerateFiles(journal, "*", SearchOption.AllDirectories));
        if (files.Count == 0) return;
        files.Add(Path.Combine(directory, "runtime.json")); files.Add(Path.Combine(directory, "recovered.json"));
        var root = Path.Combine(_root, "evidence", session.Runtime.ToString("N"));
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var destination = Path.Combine(root, file.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar));
            if (!File.Exists(destination)) DurableFile.Write(destination, output => { using var input = File.OpenRead(file); input.CopyTo(output); });
            if (EpisodeTransactionStore.Hash(destination) != EpisodeTransactionStore.Hash(file)) throw new InvalidDataException("Retained evidence checksum mismatch.");
        }
    }
    private static bool Retain(Exception error) => error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is SerializationException;
}
