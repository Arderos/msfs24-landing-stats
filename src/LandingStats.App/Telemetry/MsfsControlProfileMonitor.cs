using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Telemetry;

/// <summary>Runs profile I/O away from SimConnect dispatch. The simulator owner drains immutable
/// observations in order; an obsolete aircraft/profile load can never publish a newer binding.</summary>
internal sealed class MsfsControlProfileMonitor : IDisposable
{
    private readonly object _gate = new object();
    private readonly Func<string, MsfsControlProfileStore> _discover;
    private readonly Func<string, MsfsControlProfileStore> _load;
    private string _simulator = "", _path = "", _category = "";
    private long _generation;
    private bool _dirty, _reload, _working, _disposed;
    private MsfsControlProfileStore? _store;
    private FileSystemWatcher? _watcher;
    private AircraftControlsMessage? _pending;
    private string[] _diagnostics = Array.Empty<string>();

    public MsfsControlProfileMonitor() : this(MsfsControlProfileStore.Discover, MsfsControlProfileStore.Load) { }
    internal MsfsControlProfileMonitor(Func<string, MsfsControlProfileStore> discover, Func<string, MsfsControlProfileStore> load)
    { _discover = discover; _load = load; }

    public IReadOnlyList<string> Diagnostics { get { lock (_gate) return Array.AsReadOnly(_diagnostics); } }
    internal bool IsRefreshing { get { lock (_gate) return _working; } }

    public void Update(string simulator, string aircraftConfigPath, string category)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_simulator != simulator) { _store = null; DisposeWatcher(); }
            _simulator = simulator; _path = aircraftConfigPath; _category = category;
            _generation++; _dirty = true; _pending = null;
        }
        Start();
    }

    public bool TryTake(out AircraftControlsMessage? message)
    {
        Start();
        lock (_gate) { message = _pending; _pending = null; return message != null; }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++; _simulator = _path = _category = ""; _dirty = _reload = false;
            _pending = null; _store = null; _diagnostics = Array.Empty<string>(); DisposeWatcher();
        }
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_disposed || _working || !_dirty) return;
            _working = true; _dirty = false;
            var generation = _generation; var simulator = _simulator; var path = _path; var category = _category;
            // Keep the reload request until its result is accepted. An aircraft
            // change can supersede this read while the cached store is still old.
            var store = _store; var reload = _reload;
            Task.Run(() => Read(generation, simulator, path, category, store, reload));
        }
    }

    private void Read(long generation, string simulator, string path, string category, MsfsControlProfileStore? store, bool reload)
    {
        MsfsControlProfileResolution? resolution = null;
        string? error = null;
        var evidence = "";
        try
        {
            store = store == null || store.DirectoryPath.Length == 0 ? _discover(simulator) : reload ? _load(store.DirectoryPath) : store;
            // Install before the final read so a profile saved during the read invalidates its generation.
            var reread = false;
            lock (_gate)
            {
                if (!_disposed && generation == _generation && _watcher == null && Directory.Exists(store.DirectoryPath))
                {
                    _watcher = new FileSystemWatcher(store.DirectoryPath)
                    { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Deleted += Changed;
                    _watcher.Renamed += Renamed; _watcher.Error += WatchError;
                    _watcher.EnableRaisingEvents = true; reread = true;
                }
            }
            if (reread) store = _load(store.DirectoryPath);
            resolution = store.Resolve(path, category);
            evidence = MsfsControlProfileEvidence.WithResolution(store.ProfileEvidence, resolution);
        }
        catch (Exception failure)
        {
            // The task is the profile-reader failure boundary, never the simulator dispatcher.
            error = "MSFS input-profile refresh failed: " + failure.GetType().Name + ": " + failure.Message;
        }
        lock (_gate)
        {
            _working = false;
            if (_disposed || generation != _generation) return;
            _reload = false;
            _store = error == null ? store : null;
            _diagnostics = error == null ? resolution!.Diagnostics.ToArray() : new[] { error };
            var bindings = resolution?.Bindings.Select(binding => new ControllerBinding(
                (ControllerControlKind)binding.Control, binding.EngineIndex, binding.DeviceInstanceId,
                binding.AxisIndex, binding.Inverted, binding.Layer + ":" + binding.ProfileFile + ":" + binding.ActionName))
                ?? Enumerable.Empty<ControllerBinding>();
            var context = Context(path, category) + "|" + MsfsControlProfileEvidence.Hash(System.Text.Encoding.UTF8.GetBytes(evidence));
            try { _pending = new AircraftControlsMessage(path, context, bindings, evidence); }
            catch (ArgumentException failure)
            {
                _diagnostics = _diagnostics.Concat(new[] { "MSFS profile evidence could not be recorded: " + failure.Message }).ToArray();
                // Keep the resolved controls usable even if optional diagnostics exceed the wire budget.
                // This error is also published as a source diagnostic by the simulator owner.
                var failureEvidence = MsfsControlProfileEvidence.Failure(failure.Message, evidence);
                try { _pending = new AircraftControlsMessage(path, context, bindings, failureEvidence); }
                catch (ArgumentException bindingFailure)
                {
                    _diagnostics = _diagnostics.Concat(new[] { "MSFS control assignments could not fit the runtime message: " + bindingFailure.Message }).ToArray();
                    _pending = new AircraftControlsMessage(path, Context(path, category), Array.Empty<ControllerBinding>(), failureEvidence);
                }
            }
        }
    }

    private void Changed(object sender, FileSystemEventArgs args)
    { if (Relevant(args.Name)) Invalidate(); }
    private void Renamed(object sender, RenamedEventArgs args)
    { if (Relevant(args.Name) || Relevant(args.OldName)) Invalidate(); }
    private static bool Relevant(string? name) => name == "profile_00" || name?.StartsWith("inputprofile_", StringComparison.OrdinalIgnoreCase) == true;

    private void WatchError(object sender, ErrorEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _diagnostics = new[] { "MSFS input-profile watch failed: " + args.GetException().Message };
            DisposeWatcher();
        }
        Invalidate();
    }
    private void Invalidate()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++; _dirty = _reload = true;
            // Do not keep a previous explicit mapping while its saved profile is changing.
            _pending = new AircraftControlsMessage(_path, Context(_path, _category), Array.Empty<ControllerBinding>());
        }
    }

    private static string Context(string path, string category)
    {
        var context = category + "|" + path;
        if (System.Text.Encoding.UTF8.GetByteCount(context) <= 4096 - 65) return context;
        using var hash = System.Security.Cryptography.SHA256.Create();
        return "MSFS input context SHA256:" + BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(context))).Replace("-", "");
    }

    private void DisposeWatcher() { _watcher?.Dispose(); _watcher = null; }
    public void Dispose() { lock (_gate) { _disposed = true; _generation++; _pending = null; DisposeWatcher(); } }
}
