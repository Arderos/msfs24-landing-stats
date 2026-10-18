using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal static class RuntimeSessionDirectories
{
    private sealed class Snapshot
    {
        public DateTime Stamp; public long ReadAt; public string[] Paths = Array.Empty<string>();
    }
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, Snapshot> Snapshots = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> Read(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var stamp = Directory.GetLastWriteTimeUtc(root); var now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            if (Snapshots.TryGetValue(root, out var cached) && cached.Stamp == stamp && now - cached.ReadAt < 5 * Stopwatch.Frequency)
                return cached.Paths;
        }
        var values = Directory.EnumerateDirectories(root).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _) && !Collected(path) && !RuntimeMetadataFailure.Unchanged(path, "runtime.json")).ToArray();
        lock (Gate)
        {
            if (Snapshots.Count >= 16) Snapshots.Clear();
            Snapshots[root] = new Snapshot { Stamp = stamp, ReadAt = now, Paths = values };
        }
        return values;
    }
    public static bool Collected(string root)
    {
        if (!File.Exists(Path.Combine(root, "collected.json"))) return false;
        try
        {
            var session = Validate(root); var receipt = DurableFile.ReadJson<RuntimeCollectionReceipt>(Path.Combine(root, "collected.json"), 4096);
            return receipt.Version == 1 && receipt.Runtime == session.Runtime && receipt.Engine == session.Engine;
        }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is SerializationException || error is UnauthorizedAccessException) { return false; }
    }
    public static RuntimeSession Validate(string root)
    {
        var session = RuntimeSession.Read(root);
        if (session.Runtime != Guid.ParseExact(Path.GetFileName(root), "N")) throw new InvalidDataException("Runtime directory belongs to another session.");
        return session;
    }
    public static bool Recovered(string root)
    {
        var session = Validate(root); var marker = Path.Combine(root, "recovered.json");
        if (!File.Exists(marker)) return false;
        ColdRecordingRecovery.Validate(DurableFile.ReadJson<RecordingRecoveryBundle>(marker, 64L * 1024 * 1024), session);
        return true;
    }
}
internal sealed class RuntimeWorkRetry
{
    public RuntimeWorkRetry(int count, long after) { Count = count; After = after; }
    public int Count { get; }
    public long After { get; }
    public static RuntimeWorkRetry Next(RuntimeWorkRetry? old)
    {
        var count = Math.Min(20, (old?.Count ?? 0) + 1);
        return new RuntimeWorkRetry(count, Stopwatch.GetTimestamp() + (long)(Math.Min(30, Math.Pow(2, count - 1)) * Stopwatch.Frequency));
    }
}

internal sealed class RuntimeSessionRecoveryOwner
{
    private readonly string _sessions, _current;
    private readonly RetainedAnalysisExecutor _executor;
    private readonly Action _recovered;
    private readonly Dictionary<string, RuntimeWorkRetry> _retry = new Dictionary<string, RuntimeWorkRetry>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public RuntimeSessionRecoveryOwner(string sessions, string current, RetainedAnalysisExecutor executor, Action recovered)
    { _sessions = sessions; _current = current; _executor = executor; _recovered = recovered; }
    public async Task RunAsync(ManagedComponentContext owner)
    {
        owner.MarkReady();
        while (true)
        {
            var worked = false;
            foreach (var session in RuntimeSessionDirectories.Read(_sessions))
            {
                owner.CancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(session, _current, StringComparison.OrdinalIgnoreCase) || _completed.Contains(session)) continue;
                if (_retry.TryGetValue(session, out var previous) && Stopwatch.GetTimestamp() < previous.After) continue;
                try
                {
                    RuntimeSessionDirectories.Validate(session);
                    owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.ParseExact(Path.GetFileName(session), "N"));
                    await _executor.RecoverRecordingAsync(session, owner.CancellationToken).ConfigureAwait(false);
                    _retry.Remove(session); _completed.Add(session); _recovered(); owner.Progress(true); worked = true;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is SerializationException)
                { _retry[session] = RuntimeWorkRetry.Next(previous); }
            }
            owner.Progress(worked, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }
}

[DataContract]
internal sealed class RuntimeRawCompletion
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Runtime { get; set; }
}
internal sealed class RuntimeRawExportOwner
{
    private readonly string _sessions, _current;
    private readonly RawEffectStore _live;
    private readonly RawChunkStore _chunks;
    private readonly Action _published;
    private readonly int _chunkSize;
    private readonly Action? _flush;
    private readonly Dictionary<string, RawExportOwner> _instances = new Dictionary<string, RawExportOwner>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeWorkRetry> _retry = new Dictionary<string, RuntimeWorkRetry>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private ManagedComponentContext? _owner;
    public RuntimeRawExportOwner(string sessions, string current, RawEffectStore live, RawChunkStore chunks, Action published, int chunkSize, Action? flush = null)
    { _sessions = sessions; _current = current; _live = live; _chunks = chunks; _published = published; _chunkSize = chunkSize; _flush = flush; }
    public void Wake() => Volatile.Read(ref _owner)?.SignalWork();
    public async Task RunAsync(ManagedComponentContext owner)
    {
        if (Interlocked.CompareExchange(ref _owner, owner, null) != null) throw new InvalidOperationException("RAW sessions already have an owner.");
        try
        {
            _instances.Clear(); owner.MarkReady();
            while (true)
            {
                _flush?.Invoke();
                var worked = false;
                foreach (var session in RuntimeSessionDirectories.Read(_sessions))
                {
                    owner.CancellationToken.ThrowIfCancellationRequested(); var current = string.Equals(session, _current, StringComparison.OrdinalIgnoreCase);
                    if (_completed.Contains(session)) continue;
                    if (_retry.TryGetValue(session, out var previous) && Stopwatch.GetTimestamp() < previous.After) continue;
                    try
                    {
                        using var ownership = current ? null : RuntimeSession.AcquireExistingOwnership(session);
                        if (!current && File.Exists(Path.Combine(session, "collected.json"))) continue;
                        if (!current && !_recovered.Contains(session))
                        {
                            if (!RuntimeSessionDirectories.Recovered(session)) continue;
                            _recovered.Add(session);
                        }
                        var id = Guid.ParseExact(Path.GetFileName(session), "N"); var completion = Path.Combine(session, "raw-completed.json");
                        if (!current && File.Exists(completion))
                        {
                            var saved = DurableFile.ReadJson<RuntimeRawCompletion>(completion, 4096);
                            if (saved.Version != 1 || saved.Runtime != id) throw new InvalidDataException("RAW completion belongs to another runtime.");
                            _completed.Add(session);
                            continue;
                        }
                        if (!_instances.TryGetValue(session, out var exporter))
                            _instances.Add(session, exporter = new RawExportOwner(current ? _live : new RawEffectStore(Path.Combine(session, "raw")), _chunks, (_, __, ___) => _published(), _chunkSize));
                        worked |= exporter.Advance(owner.CancellationToken, input => owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: input));
                        if (!current && exporter.CaughtUp)
                        { DurableFile.WriteJson(completion, new RuntimeRawCompletion { Runtime = id }); _instances.Remove(session); _completed.Add(session); }
                        _retry.Remove(session); owner.Progress(true);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is SerializationException)
                    { _instances.Remove(session); _retry[session] = RuntimeWorkRetry.Next(previous); }
                }
                owner.Progress(worked, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _owner, null); _instances.Clear(); }
    }
}
