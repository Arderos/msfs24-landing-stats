using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Storage;
using LandingStats.App.TelemetryUpload;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class ArchiveIntakeRequest
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Id { get; set; }
    [DataMember(Order = 3)] public Guid Episode { get; set; }
    [DataMember(Order = 4)] public long TimestampUtcTicks { get; set; }
    [DataMember(Order = 5)] public string LegacyName { get; set; } = "";
    [DataMember(Order = 6)] public string Hash { get; set; } = "";
    [DataMember(Order = 7)] public string ArchiveName { get; set; } = "";
    [DataMember(Order = 8)] public bool Automatic { get; set; }
    [DataMember(Order = 9)] public int Stage { get; set; }
}

/// <summary>Durable manual intent and one-way adoption of the former queue.
/// Publication and registration precede original-file cleanup. A delivered
/// archive is never recreated after a crash between those stages.</summary>
internal sealed class ArchiveIntakeOwner
{
    private readonly object _gate = new object();
    private readonly string _root, _archives;
    private readonly string? _legacy;
    private readonly Func<bool> _readLegacyConsent;
    private readonly EpisodeTransactionStore _episodes;
    private readonly DurableOutbox _outbox;
    private readonly Action _ready;
    private readonly Func<bool>? _useZstd;
    private ManagedComponentContext? _owner;
    private readonly DurableReceiptIndex<ArchiveIntakeRequest> _receipts;
    private readonly Dictionary<Guid, RuntimeWorkRetry> _retry = new Dictionary<Guid, RuntimeWorkRetry>();
    private RuntimeWorkRetry? _identityRetry;
    private int _identityChanged;
    private readonly Dictionary<string, RuntimeWorkRetry> _legacyRetry = new Dictionary<string, RuntimeWorkRetry>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Tuple<Guid, string>> _legacyInputs = new Dictionary<string, Tuple<Guid, string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public event Action<Guid>? Recovered;
    public void Wake() { Interlocked.Exchange(ref _identityChanged, 1); Volatile.Read(ref _owner)?.SignalWork(); }
    public event Action<Guid, string>? Failed;
    public ArchiveIntakeOwner(string root, string archives, string? legacy, string identityRoot, EpisodeTransactionStore episodes, DurableOutbox outbox, Action ready, Func<bool>? useZstd = null, Func<bool>? readLegacyConsent = null)
    { _root = Path.GetFullPath(root); _archives = Path.GetFullPath(archives); _legacy = legacy == null ? null : Path.GetFullPath(legacy);
        _receipts = new DurableReceiptIndex<ArchiveIntakeRequest>(Path.Combine(_root, "receipts"));
        var identity = new TelemetryUploadIdentityStore(identityRoot); _readLegacyConsent = readLegacyConsent ?? (() => identity.ConsentAccepted); _episodes = episodes; _outbox = outbox; _ready = ready; _useZstd = useZstd; }
    private string PathFor(Guid id) => Path.Combine(_root, id.ToString("N") + ".json");
    private bool Exists(Guid id) => File.Exists(PathFor(id)) || _receipts.Contains(id);
    private void Save(ArchiveIntakeRequest request) => DurableFile.WriteJson(PathFor(request.Id), request);
    private ArchiveIntakeRequest Read(Guid id)
    {
        var item = File.Exists(PathFor(id)) ? DurableFile.ReadJson<ArchiveIntakeRequest>(PathFor(id), 8192) : _receipts.Read(id);
        bool Name(string value) => !string.IsNullOrWhiteSpace(value) && value == Path.GetFileName(value) && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        if (item.Version != 1 || item.Id != id || item.Stage < 0 || item.Stage > 2 || item.TimestampUtcTicks <= 0 || item.TimestampUtcTicks > DateTime.MaxValue.Ticks ||
            (item.Episode == Guid.Empty ? !Name(item.LegacyName) || !EpisodeTransactionStore.ValidHash(item.Hash) : item.LegacyName.Length != 0 || item.Automatic) ||
            (item.Stage >= 1 && (!Name(item.ArchiveName) || !EpisodeTransactionStore.ValidHash(item.Hash))))
            throw new InvalidDataException("Invalid archive intake request; its inputs remain preserved.");
        return item;
    }
    public Guid RequestManual(Guid episode)
    {
        var id = StableRuntimeIdentity.Command(episode, int.MaxValue);
        lock (_gate)
        {
            if (Exists(id)) { if (Read(id).Episode != episode) throw new InvalidDataException("Manual report identity was reused."); return id; }
            var manifest = _episodes.Read(episode);
            if (manifest.Stage < EpisodeWorkStage.ReportReady) throw new InvalidOperationException("The episode report is still pending.");
            Save(new ArchiveIntakeRequest { Id = id, Episode = episode, TimestampUtcTicks = DateTime.UtcNow.Ticks });
        }
        Volatile.Read(ref _owner)?.SignalWork(); return id;
    }
    public async Task RunAsync(ManagedComponentContext owner)
    {
        if (Interlocked.CompareExchange(ref _owner, owner, null) != null) throw new InvalidOperationException("Archive intake already has an owner.");
        try
        {
            owner.MarkReady();
            while (true)
            {
                owner.CancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref _identityChanged, 0) != 0) _identityRetry = null;
                AdoptLegacy(owner);
                var files = Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.json").Take(65536).ToArray() : Array.Empty<string>();
                var worked = false;
                foreach (var file in files)
                {
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)) continue;
                    owner.CancellationToken.ThrowIfCancellationRequested();
                    if (_retry.TryGetValue(id, out var previous) && Stopwatch.GetTimestamp() < previous.After) continue;
                    try
                    {
                        owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: id);
                        lock (_gate) worked |= Advance(Read(id));
                        _retry.Remove(id); RecoveredFailure(id.ToString("N"), id); owner.Progress(true);
                    }
                    catch (Exception error) when (Recoverable(error))
                    { _retry[id] = RuntimeWorkRetry.Next(previous); ReportFailure(id.ToString("N"), id, error); }
                }
                owner.Progress(worked, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _owner, null); }
    }
    private void AdoptLegacy(ManagedComponentContext owner)
    {
        if (_legacy == null || !Directory.Exists(_legacy)) return;
        var files = Directory.EnumerateFiles(_legacy, "*_raw.zip").Take(65536).ToArray();
        var existing = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _legacyInputs.Keys.Where(path => !existing.Contains(path)).ToArray()) _legacyInputs.Remove(gone);
        foreach (var gone in _legacyRetry.Keys.Where(path => !existing.Contains(path)).ToArray())
        { _legacyRetry.Remove(gone); RecoveredFailure(gone, FailureIdentity(gone)); }
        // One fresh consent snapshot per sweep, lazy so classified files need
        // no identity I/O. Wake invalidates error backoff; the next sweep rereads.
        bool? consentAccepted = null; var consentRead = false;
        foreach (var file in files)
        {
            owner.CancellationToken.ThrowIfCancellationRequested();
            _legacyRetry.TryGetValue(file, out var previous);
            try
            {
                var name = Path.GetFileName(file); var manual = name.EndsWith("_bug_raw.zip", StringComparison.OrdinalIgnoreCase);
                var automatic = name.EndsWith("_auto_raw.zip", StringComparison.OrdinalIgnoreCase);
                if (!manual && !automatic)
                {
                    if (!consentRead) { consentAccepted = LegacyConsentAccepted(); consentRead = true; }
                    if (consentAccepted != true)
                    {
                        // Explicit opt-out retires this optional adoption. An
                        // unreadable consent decision proves no such retirement.
                        if (consentAccepted == false) { _legacyRetry.Remove(file); RecoveredFailure(file, FailureIdentity(file)); }
                        continue;
                    }
                }
                if (previous != null && Stopwatch.GetTimestamp() < previous.After) continue;
                owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.NewGuid());
                if (!_legacyInputs.TryGetValue(file, out var input) || !File.Exists(PathFor(input.Item1)))
                {
                    var hash = EpisodeTransactionStore.Hash(file);
                    _legacyInputs[file] = input = Tuple.Create(Guid.ParseExact(hash.Substring(0, 32), "N"), hash);
                }
                lock (_gate)
                {
                    // An active durable request exclusively owns its cleanup and retry.
                    // Adoption must not bypass that backoff, even after Stage 2.
                    if (File.Exists(PathFor(input.Item1))) continue;
                    if (_receipts.Contains(input.Item1))
                    {
                        var request = Read(input.Item1);
                        if (request.Hash != input.Item2) throw new InvalidDataException("Legacy archive identity collision.");
                        // A reappearing identical source has no active request. Its
                        // cleanup is owned here, with the same per-source retry.
                        EpisodeTransactionStore.Verify(file, input.Item2); File.Delete(file);
                        _legacyInputs.Remove(file);
                    }
                    else Save(new ArchiveIntakeRequest { Id = input.Item1, TimestampUtcTicks = File.GetLastWriteTimeUtc(file).Ticks,
                        LegacyName = name, Hash = input.Item2, Automatic = !manual });
                }
                _legacyRetry.Remove(file); RecoveredFailure(file, FailureIdentity(file));
            }
            catch (Exception error) when (Recoverable(error))
            {
                _legacyRetry[file] = RuntimeWorkRetry.Next(previous);
                ReportFailure(file, FailureIdentity(file), error);
            }
        }
    }
    private static Guid FailureIdentity(string key)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("archive-intake/" + key.ToUpperInvariant()));
        var identity = new byte[16]; Array.Copy(hash, identity, identity.Length); return new Guid(identity);
    }
    private void ReportFailure(string key, Guid id, Exception error)
    {
        var reason = error.GetType().Name;
        if (_failures.TryGetValue(key, out var previous) && previous == reason) return;
        _failures[key] = reason;
        Trace.TraceWarning("Archive intake {0}: {1}", key, error);
        Failed?.Invoke(id, reason);
    }
    private void RecoveredFailure(string key, Guid id)
    { if (_failures.Remove(key)) Recovered?.Invoke(id); }
    private bool? LegacyConsentAccepted()
    {
        // All unclassified legacy archives share this optional dependency.
        // A temporarily unavailable key must not restart intake or trigger one
        // decrypt/report attempt per file while independent reports await adoption.
        if (_identityRetry != null && Stopwatch.GetTimestamp() < _identityRetry.After) return null;
        try
        {
            var accepted = _readLegacyConsent();
            _identityRetry = null; RecoveredFailure("identity", FailureIdentity("identity"));
            return accepted;
        }
        catch (Exception error) when (Recoverable(error))
        {
            _identityRetry = RuntimeWorkRetry.Next(_identityRetry);
            ReportFailure("identity", FailureIdentity("identity"), error);
            return null;
        }
    }
    private bool Advance(ArchiveIntakeRequest item)
    {
        if (item.Stage == 2) { CleanupLegacy(item); Compact(item); return false; }
        if (item.Stage == 0)
        {
            string path;
            if (item.Episode == Guid.Empty)
            {
                if (_legacy == null) throw new InvalidDataException("Legacy queue is not configured.");
                // Legacy names can be reused for different captures. The
                // durable content identity owns the new archive's filename;
                // keep the report-kind suffix used by the upload client.
                var suffix = item.LegacyName.EndsWith("_bug_raw.zip", StringComparison.OrdinalIgnoreCase) ? "_bug_raw.zip"
                    : item.LegacyName.EndsWith("_auto_raw.zip", StringComparison.OrdinalIgnoreCase) ? "_auto_raw.zip" : "_raw.zip";
                path = Path.Combine(_archives, item.Id.ToString("N") + suffix);
                // Earlier versions copied under the legacy basename before
                // journaling Stage 1. Reuse that exact verified crash artifact.
                var previousPath = Path.Combine(_archives, item.LegacyName);
                if (File.Exists(previousPath) && EpisodeTransactionStore.Hash(previousPath) == item.Hash) path = previousPath;
                EpisodeTransactionStore.CopyImmutable(Path.Combine(_legacy, item.LegacyName), path, item.Hash);
            }
            else
            {
                var manifest = _episodes.Read(item.Episode); var source = _episodes.ReadInputs(manifest).Episode; var report = _episodes.ReadReport(manifest);
                var candidate = new BugReportCandidate(source.EpisodeId, source.Samples, source.Simulator, source.AircraftTitle, source.AircraftType,
                    source.AircraftModel, source.ControlInputSources, report.ReportRecords, report.Decisions);
                path = new BugReportRepository(_archives, _useZstd).CreateManual(candidate, new DateTime(item.TimestampUtcTicks, DateTimeKind.Utc), item.Id);
            }
            item.ArchiveName = Path.GetFileName(path); item.Hash = EpisodeTransactionStore.Hash(path); item.Stage = 1; Save(item);
        }
        if (item.Stage == 1)
        {
            _outbox.Register(item.Id, Path.Combine(_archives, item.ArchiveName), item.Hash, item.Automatic, new DateTime(item.TimestampUtcTicks, DateTimeKind.Utc));
            item.Stage = 2; Save(item); _ready();
        }
        CleanupLegacy(item); Compact(item); return true;
    }
    private void Compact(ArchiveIntakeRequest item)
    { _receipts.Write(item.Id, item); File.Delete(PathFor(item.Id)); }
    private void CleanupLegacy(ArchiveIntakeRequest item)
    {
        if (_legacy == null || item.LegacyName.Length == 0) return;
        var file = Path.Combine(_legacy, item.LegacyName);
        if (!File.Exists(file)) return;
        EpisodeTransactionStore.Verify(file, item.Hash); File.Delete(file);
    }
    private static bool Recoverable(Exception error) => error is IOException || error is UnauthorizedAccessException || error is InvalidDataException ||
        error is SerializationException || error is System.Security.Cryptography.CryptographicException;
}
