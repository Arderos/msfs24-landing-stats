using System;
using System.Collections.Generic;
using System.Diagnostics;
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
[DataContract]
internal sealed class OutboxRecoveryQuarantine
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Id { get; set; }
    [DataMember(Order = 3)] public string Failure { get; set; } = "";
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
    public event Action<Guid>? Recovered;
    public event Action<bool>? IdentityFailuresChanged;
    public event Action<bool>? QuarantineChanged;
    // Preserve the exact failed operation until it succeeds. A successful read
    // or delivery of another entry is not evidence that failed cleanup finished.
    private readonly Dictionary<Guid, Dictionary<string, RecoveryWork>> _pendingRecovery = new Dictionary<Guid, Dictionary<string, RecoveryWork>>();
    private readonly Dictionary<Guid, Guid> _identityFailures = new Dictionary<Guid, Guid>();
    private readonly Dictionary<Guid, Guid> _resolvedIdentityFailures = new Dictionary<Guid, Guid>();
    private readonly HashSet<Guid> _reportedRecoveryFailures = new HashSet<Guid>();
    private readonly HashSet<Guid> _quarantined = new HashSet<Guid>();
    public bool HasIdentityFailures { get { lock (_gate) return _identityFailures.Count != 0; } }
    public bool HasQuarantinedEntries { get { lock (_gate) return _quarantined.Count != 0; } }
    public bool HasRecoveryFailures { get { lock (_gate) return _pendingRecovery.Values.Any(work => work.Values.Any(value => !value.Quarantined)); } }
    public bool HasIdentityRecoveryFailures
    { get { lock (_gate) return _pendingRecovery.Any(work => _resolvedIdentityFailures.ContainsKey(work.Key) && work.Value.TryGetValue("identity", out var value) && !value.Quarantined); } }
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
            ObserveIdentityFailure(entry);
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
    public OutboxEntry? TryRead(Guid id, bool refresh = false)
    {
        lock (_gate)
        {
            OutboxEntry? entry = null;
            TryRecovery(id, "inspect", () => entry = Read(id), refresh);
            return entry;
        }
    }
    // Identity replacement invalidates only this dependency failure. Original
    // archives, manual intent, transport failures and policy remain unchanged.
    public IReadOnlyList<OutboxEntry> ClearIdentityFailures(Action<OutboxEntry>? changed = null)
    {
        lock (_gate)
        {
            var cleared = new List<OutboxEntry>();
            // Discover readable persisted failures, but do not attach identity
            // cleanup to unrelated malformed or inaccessible metadata.
            foreach (var id in Enumerate()) TryRecovery(id, "inspect", () => Read(id));
            foreach (var failure in _identityFailures.ToArray()) _resolvedIdentityFailures[failure.Key] = failure.Value;
            foreach (var id in _identityFailures.Keys.ToArray()) SetIdentityFailure(id, false);
            foreach (var failure in _resolvedIdentityFailures.ToArray())
            {
                var id = failure.Key; var failedAttempt = failure.Value;
                // A fresh key proof owns this attempt's cleanup, including a
                // running retry's inherited failure. Replace older deferred
                // cleanup now so retirement/restart cannot revive that label.
                TryRecovery(id, "identity", () =>
                {
                    var entry = Read(id);
                    if ((entry.State != OutboxState.Pending && entry.State != OutboxState.Revoked && entry.State != OutboxState.Claimed) ||
                        entry.LastFailure != "CryptographicException" || entry.Attempt != failedAttempt) return;
                    entry.LastFailure = ""; Save(entry); cleared.Add(entry); changed?.Invoke(entry);
                }, refresh: true);
            }
            return cleared;
        }
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
            if (_resolvedIdentityFailures.TryGetValue(id, out var resolved) && resolved == entry.Attempt) entry.LastFailure = "";
            entry.State = OutboxState.Claimed; entry.Attempt = Guid.NewGuid(); entry.AppSession = worker.AppSessionId;
            entry.WorkerInstance = worker.InstanceId; entry.WorkerGeneration = worker.Generation; entry.PolicyRevision = _policy.Revision;
            Save(entry); return new UploadClaim(entry, worker);
        }
    }
    // Called only after this exact old worker has exited. A timeout alone cannot
    // authorize overlapping ownership of an HTTP attempt.
    public void RetireConfirmedWorker(WorkerIdentity worker, Guid archiveId)
    {
        // The native owner knows its claimed archive. Never attach another
        // worker's retirement obligation to an unreadable neighboring entry.
        lock (_gate)
            TryRecovery(archiveId, "retire", () =>
            {
                var entry = Read(archiveId);
                if (entry.State != OutboxState.Claimed || !OwnedBy(entry, worker)) return;
                entry.State = entry.Automatic && !_policy.AutomaticEnabled ? OutboxState.Revoked : OutboxState.Pending;
                Save(entry);
            });
    }
    // Startup must first establish sole runtime ownership and termination of the
    // previous helper job. It then releases claims left by that dead app session.
    public void RecoverAfterPreviousRuntimeExited(Guid newAppSession)
    {
        if (newAppSession == Guid.Empty) throw new ArgumentException("An app session is required.");
        lock (_gate)
            foreach (var id in Enumerate().Concat(EnumerateQuarantines()).Distinct())
            {
                TryRecovery(id, "previous-runtime", () =>
                {
                    var entry = Read(id);
                    if (entry.State == OutboxState.Claimed && entry.AppSession != newAppSession)
                    { entry.State = entry.Automatic && !_policy.AutomaticEnabled ? OutboxState.Revoked : OutboxState.Pending; Save(entry); }
                    if (entry.State == OutboxState.Delivered) CleanupDelivered(entry);
                    else if (entry.State == OutboxState.QuarantinePending) FinishQuarantine(entry);
                    else if (entry.State == OutboxState.Quarantined) Compact(entry);
                });
            }
    }
    public void RecoverTerminalEntries()
    {
        lock (_gate)
            foreach (var id in Enumerate().Concat(EnumerateQuarantines()).Distinct())
            {
                TryRecovery(id, "terminal", () =>
                {
                    var entry = Read(id);
                    if (entry.State == OutboxState.Delivered) CleanupDelivered(entry);
                    else if (entry.State == OutboxState.QuarantinePending) FinishQuarantine(entry);
                    else if (entry.State == OutboxState.Quarantined) Compact(entry);
                });
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
            entry.LastFailure = Bound(reason); Save(entry);
            // A result accepted after a usable-key check is fresh evidence,
            // including when the already-running attempt reports it.
            _resolvedIdentityFailures.Remove(entry.Id); ObserveIdentityFailure(entry); return true;
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
            ObserveIdentityFailure(entry);
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
    private static bool IsMalformed(Exception error) => error is SerializationException || error is ArgumentException ||
        error is InvalidDataException || error is NotSupportedException;
    private static bool IsRecoverableStorageFailure(Exception error) => error is IOException || error is UnauthorizedAccessException || IsMalformed(error);
    private void ObserveIdentityFailure(OutboxEntry entry)
    {
        var blocked = (entry.State == OutboxState.Pending || entry.State == OutboxState.Revoked || entry.State == OutboxState.Claimed) && entry.LastFailure == "CryptographicException";
        if (_resolvedIdentityFailures.TryGetValue(entry.Id, out var resolved))
        {
            if (blocked && resolved == entry.Attempt) blocked = false;
            else _resolvedIdentityFailures.Remove(entry.Id);
        }
        SetIdentityFailure(entry.Id, blocked, entry.Attempt);
    }
    private void SetIdentityFailure(Guid id, bool blocked, Guid attempt = default)
    {
        var previous = _identityFailures.Count != 0;
        if (blocked) _identityFailures[id] = attempt; else _identityFailures.Remove(id);
        if (previous != (_identityFailures.Count != 0)) IdentityFailuresChanged?.Invoke(_identityFailures.Count != 0);
    }
    private string QuarantineReceipt(Guid id) => System.IO.Path.Combine(_root, "quarantine", id.ToString("N") + ".json");
    private void DeleteQuarantineReceipt(Guid id)
    {
        try { File.Delete(QuarantineReceipt(id)); }
        catch (DirectoryNotFoundException) { } // No quarantine directory means there is no receipt to remove.
    }
    private IEnumerable<Guid> EnumerateQuarantines()
    {
        var root = System.IO.Path.Combine(_root, "quarantine");
        return !Directory.Exists(root) ? Array.Empty<Guid>() : Directory.EnumerateFiles(root, "*.json")
            .Select(System.IO.Path.GetFileNameWithoutExtension).Where(name => Guid.TryParseExact(name, "N", out _)).Select(Guid.Parse).ToArray();
    }
    private bool EntryWasRemoved(Guid id)
    {
        // File.Exists also returns false for inaccessible paths. Only confirmed
        // absence may retire an obligation; a real lock must remain retryable.
        try { File.GetAttributes(EntryPath(id)); return false; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return !_receipts.Contains(id);
    }
    private void UpdateRecoveryNotice(Guid id, bool completed = false)
    {
        _pendingRecovery.TryGetValue(id, out var work);
        var wasQuarantined = _quarantined.Count != 0;
        if (work != null && work.Values.Any(value => value.Quarantined)) _quarantined.Add(id); else _quarantined.Remove(id);
        if (wasQuarantined != (_quarantined.Count != 0)) QuarantineChanged?.Invoke(_quarantined.Count != 0);
        if (work == null || !work.Values.Any(value => !value.Quarantined))
        {
            var reported = _reportedRecoveryFailures.Remove(id);
            if (reported || completed) Recovered?.Invoke(id);
        }
    }
    public void RetryRecoveryFailures()
    {
        lock (_gate)
        {
            foreach (var id in _identityFailures.Keys.Concat(_resolvedIdentityFailures.Keys).Distinct().ToArray())
            {
                try { if (EntryWasRemoved(id)) { _resolvedIdentityFailures.Remove(id); SetIdentityFailure(id, false); } }
                catch (Exception error) when (IsRecoverableStorageFailure(error)) { /* An unreadable record does not prove recovery. */ }
            }
            foreach (var entry in _pendingRecovery.ToArray())
                foreach (var operation in entry.Value.ToArray()) TryRecovery(entry.Key, operation.Key, operation.Value.Work);
        }
    }
    private void TryRecovery(Guid id, string operation, Action work, bool refresh = false)
    {
        _pendingRecovery.TryGetValue(id, out var pending);
        RecoveryWork? previous = null;
        pending?.TryGetValue(operation, out previous);
        if (!refresh && previous != null && Stopwatch.GetTimestamp() < previous.Retry.After) return;
        try
        {
            if (EntryWasRemoved(id))
            {
                DeleteQuarantineReceipt(id);
                _pendingRecovery.Remove(id); _resolvedIdentityFailures.Remove(id); SetIdentityFailure(id, false); UpdateRecoveryNotice(id, previous != null); return;
            }
            work();
            if (_pendingRecovery.TryGetValue(id, out var failures))
            {
                if (failures.Count == 1 && failures.ContainsKey(operation)) DeleteQuarantineReceipt(id);
                failures.Remove(operation);
                if (failures.Count == 0) _pendingRecovery.Remove(id);
            }
            else DeleteQuarantineReceipt(id);
            UpdateRecoveryNotice(id, previous != null);
        }
        catch (Exception error) when (IsRecoverableStorageFailure(error))
        {
            if (!_pendingRecovery.TryGetValue(id, out var failures)) _pendingRecovery[id] = failures = new Dictionary<string, RecoveryWork>();
            var reason = error.GetType().Name + ": " + error.Message;
            var quarantined = IsMalformed(error);
            if (quarantined)
            {
                try
                {
                    // Retain both source files in place. Untrusted metadata may
                    // not nominate an archive for moving, deletion or delivery.
                    if (!_quarantined.Contains(id)) DurableFile.WriteJson(QuarantineReceipt(id),
                        new OutboxRecoveryQuarantine { Id = id, Failure = Bound(reason) });
                }
                catch (Exception storage) when (IsRecoverableStorageFailure(storage))
                { error = storage; reason = storage.GetType().Name + ": " + storage.Message; quarantined = false; }
            }
            failures[operation] = new RecoveryWork(work, RuntimeWorkRetry.Next(previous?.Retry), reason, quarantined);
            if (previous == null || previous.Reason != reason || previous.Quarantined != quarantined) ReportRecoveryError(id, error, quarantined);
            UpdateRecoveryNotice(id);
        }
    }
    private sealed class RecoveryWork
    {
        public RecoveryWork(Action work, RuntimeWorkRetry retry, string reason, bool quarantined) { Work = work; Retry = retry; Reason = reason; Quarantined = quarantined; }
        public Action Work { get; }
        public RuntimeWorkRetry Retry { get; }
        public string Reason { get; }
        public bool Quarantined { get; }
    }
    private void ReportRecoveryError(Guid id, Exception error, bool quarantined)
    {
        if (_recoveryErrors.Count == 128) _recoveryErrors.RemoveAt(0);
        _recoveryErrors.Add(id.ToString("N") + ": " + Bound(error.Message));
        if (quarantined) Trace.TraceWarning("Outbox {0} retained in metadata quarantine: {1}", id, error);
        else { _reportedRecoveryFailures.Add(id); RecoveryFailed?.Invoke(id, error.GetType().Name); }
    }
}
