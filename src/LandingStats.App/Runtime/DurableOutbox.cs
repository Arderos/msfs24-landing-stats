using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal enum OutboxState { Pending, Claimed, Revoked, Delivered, QuarantinePending, Quarantined }
[DataContract]
internal sealed class DeliveryPolicy
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public long Revision { get; set; }
    [DataMember(Order = 3)] public bool AutomaticEnabled { get; set; }
}
[DataContract]
internal sealed class OutboxEntry
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Id { get; set; }
    [DataMember(Order = 3)] public string Path { get; set; } = "";
    [DataMember(Order = 4)] public string Hash { get; set; } = "";
    [DataMember(Order = 5)] public bool Automatic { get; set; }
    [DataMember(Order = 6)] public OutboxState State { get; set; }
    [DataMember(Order = 7)] public long PolicyRevision { get; set; }
    [DataMember(Order = 8)] public Guid Attempt { get; set; }
    [DataMember(Order = 9)] public Guid AppSession { get; set; }
    [DataMember(Order = 10)] public Guid WorkerInstance { get; set; }
    [DataMember(Order = 11)] public long WorkerGeneration { get; set; }
    [DataMember(Order = 12)] public string QuarantinePath { get; set; } = "";
    [DataMember(Order = 13)] public long CapturedUtcTicks { get; set; }
    [DataMember(Order = 14)] public string LastFailure { get; set; } = "";
}
internal sealed class UploadClaim
{
    internal UploadClaim(OutboxEntry entry, WorkerIdentity worker)
    {
        ArchiveId = entry.Id; AttemptId = entry.Attempt; Worker = worker; ContentHash = entry.Hash;
        Path = entry.Path; PolicyRevision = entry.PolicyRevision; Automatic = entry.Automatic;
    }
    public Guid ArchiveId { get; }
    public Guid AttemptId { get; }
    public WorkerIdentity Worker { get; }
    public string ContentHash { get; }
    public string Path { get; }
    public long PolicyRevision { get; }
    public bool Automatic { get; }
}

/// <summary>The parent owns archives. Helpers receive revocable claims, never deletion authority.</summary>
internal sealed class DurableOutbox
{
    private readonly object _gate = new object();
    private readonly string _root;
    private readonly string _archiveRoot;
    private DeliveryPolicy _policy;
    private readonly DurableReceiptIndex<OutboxEntry> _receipts;
    public event Action<Guid, string>? RecoveryFailed;
    private readonly List<string> _recoveryErrors = new List<string>();
    public IReadOnlyList<string> RecoveryErrors { get { lock (_gate) return _recoveryErrors.ToArray(); } }
    public DurableOutbox(string root, string archiveRoot)
    {
        _root = System.IO.Path.GetFullPath(root); _archiveRoot = System.IO.Path.GetFullPath(archiveRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        _receipts = new DurableReceiptIndex<OutboxEntry>(System.IO.Path.Combine(_root, "receipts"));
        var path = System.IO.Path.Combine(_root, "policy.json");
        _policy = File.Exists(path) ? DurableFile.ReadJson<DeliveryPolicy>(path, 4096) : new DeliveryPolicy();
        if (_policy.Version != 1 || _policy.Revision < 0) throw new InvalidDataException("Invalid delivery policy.");
    }
    public DeliveryPolicy Policy
    { get { lock (_gate) return new DeliveryPolicy { Revision = _policy.Revision, AutomaticEnabled = _policy.AutomaticEnabled }; } }

    public DeliveryPolicy SetAutomaticEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_policy.AutomaticEnabled == enabled) return Policy;
            var next = new DeliveryPolicy { Revision = checked(_policy.Revision + 1), AutomaticEnabled = enabled };
            DurableFile.WriteJson(System.IO.Path.Combine(_root, "policy.json"), next);
            _policy = next;
            // The revision alone fences old replies, even if a crash interrupts
            // rewriting individual claims. New attempts receive new identities.
            return Policy;
        }
    }
    public OutboxEntry Register(Guid id, string path, string hash, bool automatic, DateTime capturedUtc)
    {
        lock (_gate)
        {
            path = ValidateArchivePath(path); ValidateHash(hash);
            if (File.Exists(EntryPath(id)) || _receipts.Contains(id))
            {
                var existing = Read(id);
                if (!string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase) || existing.Hash != hash || existing.Automatic != automatic)
                    throw new InvalidDataException("An archive id already refers to other immutable bytes.");
                return existing;
            }
            Verify(path, hash);
            var entry = new OutboxEntry { Id = id, Path = path, Hash = hash, Automatic = automatic,
                CapturedUtcTicks = capturedUtc.ToUniversalTime().Ticks, PolicyRevision = _policy.Revision,
                State = automatic && !_policy.AutomaticEnabled ? OutboxState.Revoked : OutboxState.Pending };
            Save(entry); return entry;
        }
    }
    public OutboxEntry Read(Guid id)
    {
        lock (_gate)
        {
            var entry = File.Exists(EntryPath(id)) ? DurableFile.ReadJson<OutboxEntry>(EntryPath(id), 32768) : _receipts.Read(id);
            if ((entry.Version != 1 && entry.Version != 2) || entry.Id != id || !Enum.IsDefined(typeof(OutboxState), entry.State) || entry.PolicyRevision < 0 ||
                entry.WorkerGeneration < 0 || (entry.State == OutboxState.Claimed && (entry.Attempt == Guid.Empty ||
                    entry.AppSession == Guid.Empty || entry.WorkerInstance == Guid.Empty || entry.WorkerGeneration == 0)))
                throw new InvalidDataException("Invalid outbox entry; archive retained.");
            entry.Path = RestoreArchivePath(entry.Path, entry.Version); ValidateHash(entry.Hash);
            if (!string.IsNullOrEmpty(entry.QuarantinePath)) entry.QuarantinePath = RestoreArchivePath(entry.QuarantinePath, entry.Version);
            if (entry.State == OutboxState.QuarantinePending || entry.State == OutboxState.Quarantined) ValidateArchivePath(entry.QuarantinePath);
            return entry;
        }
    }
    public IReadOnlyList<Guid> Enumerate()
    {
        lock (_gate)
            return !Directory.Exists(_root) ? Array.Empty<Guid>() : Directory.EnumerateFiles(_root, "*.json")
                .Select(System.IO.Path.GetFileNameWithoutExtension).Where(name => Guid.TryParseExact(name, "N", out _))
                .Select(Guid.Parse).ToArray();
    }
    public UploadClaim? TryClaim(Guid id, WorkerIdentity worker)
    {
        if (worker.Role != ComponentRole.Upload) throw new ArgumentException("An upload worker is required.");
        lock (_gate)
        {
            var entry = Read(id);
            if (entry.State != OutboxState.Pending && entry.State != OutboxState.Revoked) return null;
            if (entry.Automatic && !_policy.AutomaticEnabled) return null;
            Verify(entry.Path, entry.Hash);
            entry.State = OutboxState.Claimed; entry.Attempt = Guid.NewGuid(); entry.AppSession = worker.AppSessionId;
            entry.WorkerInstance = worker.InstanceId; entry.WorkerGeneration = worker.Generation; entry.PolicyRevision = _policy.Revision;
            Save(entry); return new UploadClaim(entry, worker);
        }
    }
    // Called only after this exact old worker has exited. A timeout alone cannot
    // authorize overlapping ownership of an HTTP attempt.
    public void RetireConfirmedWorker(WorkerIdentity worker)
    {
        lock (_gate)
            foreach (var id in Enumerate())
            {
                try
                {
                    var entry = Read(id);
                    if (entry.State != OutboxState.Claimed || !OwnedBy(entry, worker)) continue;
                    entry.State = entry.Automatic && !_policy.AutomaticEnabled ? OutboxState.Revoked : OutboxState.Pending;
                    Save(entry);
                }
                catch (Exception error) when (IsRecoverableStorageFailure(error)) { ReportRecoveryError(id, error); }
            }
    }
    // Startup must first establish sole runtime ownership and termination of the
    // previous helper job. It then releases claims left by that dead app session.
    public void RecoverAfterPreviousRuntimeExited(Guid newAppSession)
    {
        if (newAppSession == Guid.Empty) throw new ArgumentException("An app session is required.");
        lock (_gate)
            foreach (var id in Enumerate())
            {
                try
                {
                    var entry = Read(id);
                    if (entry.State == OutboxState.Claimed && entry.AppSession != newAppSession)
                    { entry.State = entry.Automatic && !_policy.AutomaticEnabled ? OutboxState.Revoked : OutboxState.Pending; Save(entry); }
                    if (entry.State == OutboxState.Delivered) CleanupDelivered(entry);
                    else if (entry.State == OutboxState.QuarantinePending) FinishQuarantine(entry);
                    else if (entry.State == OutboxState.Quarantined) Compact(entry);
                }
                catch (Exception error) when (IsRecoverableStorageFailure(error)) { ReportRecoveryError(id, error); }
            }
    }
    public void RecoverTerminalEntries()
    {
        lock (_gate)
            foreach (var id in Enumerate())
            {
                try
                {
                    var entry = Read(id);
                    if (entry.State == OutboxState.Delivered) CleanupDelivered(entry);
                    else if (entry.State == OutboxState.QuarantinePending) FinishQuarantine(entry);
                    else if (entry.State == OutboxState.Quarantined) Compact(entry);
                }
                catch (Exception error) when (IsRecoverableStorageFailure(error)) { ReportRecoveryError(id, error); }
            }
    }
    public bool RecoverTerminalEntry(Guid id)
    {
        lock (_gate)
        {
            var entry = Read(id);
            if (entry.State == OutboxState.Delivered) CleanupDelivered(entry);
            else if (entry.State == OutboxState.QuarantinePending) FinishQuarantine(entry);
            else if (entry.State == OutboxState.Quarantined) Compact(entry);
            else return false;
            return true;
        }
    }
    public bool Acknowledge(UploadClaim claim)
    {
        lock (_gate)
        {
            var entry = Read(claim.ArchiveId);
            if (!Matches(entry, claim) || !PolicyAllows(entry)) return false;
            if (entry.State == OutboxState.Delivered) { CleanupDelivered(entry); return true; }
            if (entry.State != OutboxState.Claimed) return false;
            Verify(entry.Path, entry.Hash);
            entry.State = OutboxState.Delivered; Save(entry); // Receipt must survive before archive deletion.
            CleanupDelivered(entry); return true;
        }
    }
    public bool Retry(UploadClaim claim, string reason)
    {
        lock (_gate)
        {
            var entry = Read(claim.ArchiveId);
            if (entry.State != OutboxState.Claimed || !Matches(entry, claim)) return false;
            entry.State = entry.Automatic && !_policy.AutomaticEnabled ? OutboxState.Revoked : OutboxState.Pending;
            entry.LastFailure = Bound(reason); Save(entry); return true;
        }
    }
    public bool Reject(UploadClaim claim, int statusCode)
    {
        if (statusCode < 400 || statusCode > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));
        lock (_gate)
        {
            var entry = Read(claim.ArchiveId);
            if (entry.State != OutboxState.Claimed || !Matches(entry, claim) || !PolicyAllows(entry)) return false;
            Verify(entry.Path, entry.Hash);
            entry.QuarantinePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(entry.Path)!,
                System.IO.Path.GetFileNameWithoutExtension(entry.Path) + ".rejected-" + statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".zip");
            entry.State = OutboxState.QuarantinePending; Save(entry); FinishQuarantine(entry); return true;
        }
    }
    private void CleanupDelivered(OutboxEntry entry)
    {
        if (File.Exists(entry.Path)) { Verify(entry.Path, entry.Hash); File.Delete(entry.Path); }
        Compact(entry);
    }
    private void FinishQuarantine(OutboxEntry entry)
    {
        if (!File.Exists(entry.QuarantinePath)) { Verify(entry.Path, entry.Hash); File.Move(entry.Path, entry.QuarantinePath); }
        else
        {
            Verify(entry.QuarantinePath, entry.Hash);
            if (File.Exists(entry.Path)) { Verify(entry.Path, entry.Hash); File.Delete(entry.Path); }
        }
        entry.State = OutboxState.Quarantined; Save(entry); Compact(entry);
    }
    private void Compact(OutboxEntry entry)
    {
        CleanupHashSidecar(entry);
        var path = entry.Path; var quarantine = entry.QuarantinePath;
        try
        {
            entry.Version = 2; entry.Path = System.IO.Path.GetFileName(path);
            entry.QuarantinePath = string.IsNullOrEmpty(quarantine) ? "" : System.IO.Path.GetFileName(quarantine);
            _receipts.Write(entry.Id, entry);
            File.Delete(EntryPath(entry.Id));
        }
        finally { entry.Path = path; entry.QuarantinePath = quarantine; }
    }
    private void CleanupHashSidecar(OutboxEntry entry)
    {
        var sidecar = System.IO.Path.Combine(_archiveRoot, "." + entry.Id.ToString("N") + ".sha256");
        if (!File.Exists(sidecar)) return;
        // This exact receipt is durable before cleanup. Keep unknown or changed
        // metadata, and never race an active writer of the sidecar.
        using (var file = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            if (file.Length != 64) return;
            for (var i = 0; i < 64; i++) if (file.ReadByte() != entry.Hash[i]) return;
            File.Delete(sidecar);
        }
    }
    private bool PolicyAllows(OutboxEntry entry) => !entry.Automatic || _policy.AutomaticEnabled && entry.PolicyRevision == _policy.Revision;
    private static bool OwnedBy(OutboxEntry entry, WorkerIdentity worker) => entry.AppSession == worker.AppSessionId &&
        entry.WorkerInstance == worker.InstanceId && entry.WorkerGeneration == worker.Generation;
    private static bool Matches(OutboxEntry entry, UploadClaim claim) => entry.Id == claim.ArchiveId && entry.Attempt == claim.AttemptId &&
        entry.Hash == claim.ContentHash && entry.PolicyRevision == claim.PolicyRevision && entry.Automatic == claim.Automatic && OwnedBy(entry, claim.Worker);
    private string EntryPath(Guid id)
    { if (id == Guid.Empty) throw new ArgumentException("An archive id is required."); return System.IO.Path.Combine(_root, id.ToString("N") + ".json"); }
    private void Save(OutboxEntry entry)
    {
        var path = entry.Path; var quarantine = entry.QuarantinePath;
        try
        {
            entry.Version = 2; entry.Path = System.IO.Path.GetFileName(ValidateArchivePath(path));
            entry.QuarantinePath = string.IsNullOrEmpty(quarantine) ? "" : System.IO.Path.GetFileName(ValidateArchivePath(quarantine));
            DurableFile.WriteJson(EntryPath(entry.Id), entry);
        }
        finally { entry.Path = path; entry.QuarantinePath = quarantine; }
    }
    private string RestoreArchivePath(string path, int version)
    {
        if (version == 2)
        {
            if (string.IsNullOrEmpty(path) || path != System.IO.Path.GetFileName(path)) throw new InvalidDataException("Invalid relative archive path.");
            return ValidateArchivePath(System.IO.Path.Combine(_archiveRoot, path));
        }
        try { return ValidateArchivePath(path); }
        catch (InvalidDataException)
        {
            // Old entries stored an absolute profile path. The file's immutable
            // hash is still checked before any claim, delivery or deletion.
            if (!System.IO.Path.IsPathRooted(path)) throw;
            return ValidateArchivePath(System.IO.Path.Combine(_archiveRoot, System.IO.Path.GetFileName(path)));
        }
    }
    private string ValidateArchivePath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!full.StartsWith(_archiveRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(System.IO.Path.GetDirectoryName(full) + System.IO.Path.DirectorySeparatorChar, _archiveRoot, StringComparison.OrdinalIgnoreCase) ||
            !full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive is outside the owned queue.");
        return full;
    }
    private static void ValidateHash(string hash)
    { if (hash == null || hash.Length != 64 || hash.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))) throw new InvalidDataException("Invalid archive hash."); }
    private static void Verify(string path, string hash)
    { if (EpisodeTransactionStore.Hash(path) != hash) throw new InvalidDataException("Archive bytes changed; retained without acknowledgement."); }
    private static string Bound(string text) => text.Length <= 2048 ? text : text.Substring(0, 2048);
    private static bool IsRecoverableStorageFailure(Exception error) => error is IOException || error is UnauthorizedAccessException ||
        error is SerializationException || error is ArgumentException || error is InvalidDataException;
    private void ReportRecoveryError(Guid id, Exception error)
    {
        if (_recoveryErrors.Count == 128) _recoveryErrors.RemoveAt(0);
        _recoveryErrors.Add(id.ToString("N") + ": " + Bound(error.Message));
        RecoveryFailed?.Invoke(id, Bound(error.Message));
    }
}
