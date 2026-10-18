using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace LandingStats.App.Controls;

// Explicit local diagnostic session only. No per-move I/O, forced rendering,
// global mouse hook, networking, or change to camera scheduling.
internal sealed class ReplayInteractionDiagnostics
{
    internal const string DirectoryVariable = "MSFS_LANDING_STATS_REPLAY_DIAGNOSTICS";
    private readonly object _gate = new object();
    private readonly Queue<string> _pending = new Queue<string>();
    private readonly Dispatcher _dispatcher;
    private readonly string _path;
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly Dictionary<int, double> _threadCpu = new Dictionary<int, double>();
    private readonly System.Threading.Timer _timer;
    private readonly EventHandler _commitHandler;
    private readonly uint _uiThread;
    private int _writing, _sampling, _probePending, _gesture, _moves, _dropped;
    private long _probePosted, _lastCamera, _lastTimeInput, _lastTimeApplied, _commits;
    private string _record = "";
    private object? _media;
    private EventInfo? _commitEvent;

    private ReplayInteractionDiagnostics(string directory, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher; _uiThread = GetCurrentThreadId();
        _path = Path.Combine(directory, "replay-" + Process.GetCurrentProcess().Id + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".tsv");
        _commitHandler = (_, __) =>
        {
            Interlocked.Increment(ref _commits);
            var camera = Interlocked.Exchange(ref _lastCamera, 0);
            if (camera != 0) Add("commit", _gesture, Milliseconds(Stopwatch.GetTimestamp() - camera));
            var time = Interlocked.Exchange(ref _lastTimeApplied, 0);
            if (time != 0) Add("time_commit", Milliseconds(Stopwatch.GetTimestamp() - time));
        };
        try
        {
            // Observe WPF commits without CompositionTarget.Rendering, which
            // requests continuous animation and would alter the reproduction.
            var type = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext");
            _media = type?.GetMethod("From", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, new object[] { dispatcher });
            _commitEvent = type?.GetEvent("CommittingBatch", BindingFlags.NonPublic | BindingFlags.Instance);
            _commitEvent?.GetAddMethod(true)?.Invoke(_media, new object[] { _commitHandler });
        }
        catch { _media = null; _commitEvent = null; }
        Add("start", typeof(LandingReplayView).Assembly.Location, typeof(LandingReplayView).Assembly.ManifestModule.ModuleVersionId,
            _uiThread, RenderCapability.Tier >> 16, _media != null, Environment.ProcessorCount);
        _timer = new System.Threading.Timer(_ => Sample(), null, 1000, 1000);
    }

    internal static ReplayInteractionDiagnostics? Create(Dispatcher dispatcher)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try { return new ReplayInteractionDiagnostics(Path.GetFullPath(directory), dispatcher); }
        catch { return null; }
    }

    internal void Record(string? id, int points) { _record = id ?? ""; Add("record", _record, points); }
    internal void Begin(int timestamp, double width, double height)
    { _gesture++; _moves = 0; Add("down", _gesture, _record, InputAge(timestamp), width, height); }
    internal void Move(int timestamp, double x, double y, double dx, double dy, long started, double azimuth, double elevation)
    {
        var end = Stopwatch.GetTimestamp(); Interlocked.Exchange(ref _lastCamera, end); _moves++;
        Add("move", _gesture, timestamp, InputAge(timestamp), x, y, dx, dy, Milliseconds(end - started), azimuth, elevation);
    }
    internal void End(string reason) { Add("end", _gesture, _moves, reason); }
    internal void TimeInput(double time, long started)
    {
        Interlocked.Exchange(ref _lastTimeInput, started);
        Add("time_input", time, Milliseconds(Stopwatch.GetTimestamp() - started));
    }
    internal void TimeApplied(double time, long started)
    {
        var now = Stopwatch.GetTimestamp(); Interlocked.Exchange(ref _lastTimeApplied, now);
        var input = Interlocked.Exchange(ref _lastTimeInput, 0);
        Add("time_applied", time, Milliseconds(now - started), input == 0 ? 0 : Milliseconds(now - input));
    }
    internal void TimeNotified(double time, long started) => Add("time_notified", time, Milliseconds(Stopwatch.GetTimestamp() - started));
    private static uint InputAge(int timestamp) => unchecked((uint)(Environment.TickCount - timestamp));
    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0) return;
        try
        {
            if (_dispatcher.HasShutdownStarted)
            {
                _timer.Dispose(); Add("stop", _dropped); return;
            }
            using var process = Process.GetCurrentProcess();
            var changes = new List<KeyValuePair<int, double>>();
            foreach (ProcessThread thread in process.Threads)
            {
                try
                {
                    var cpu = thread.TotalProcessorTime.TotalMilliseconds;
                    if (_threadCpu.TryGetValue(thread.Id, out var before)) changes.Add(new KeyValuePair<int, double>(thread.Id, cpu - before));
                    _threadCpu[thread.Id] = cpu;
                }
                catch (InvalidOperationException) { }
                finally { thread.Dispose(); }
            }
            Add("sample", process.TotalProcessorTime.TotalMilliseconds, process.PrivateMemorySize64, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                Interlocked.Read(ref _commits), _probePending == 0 ? 0 : Milliseconds(Stopwatch.GetTimestamp() - Interlocked.Read(ref _probePosted)),
                string.Join(",", changes.OrderByDescending(value => value.Value).Take(6).Select(value => value.Key + ":" + value.Value.ToString("F2", CultureInfo.InvariantCulture))), _dropped);
            if (Interlocked.CompareExchange(ref _probePending, 1, 0) == 0)
            {
                var posted = Stopwatch.GetTimestamp(); Interlocked.Exchange(ref _probePosted, posted);
                _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    Add("input_probe", Milliseconds(Stopwatch.GetTimestamp() - posted)); Interlocked.Exchange(ref _probePending, 0);
                }));
            }
        }
        catch (Exception error) { Add("diagnostic_error", error.GetType().Name); }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    private void Add(string kind, params object[] values)
    {
        var line = Milliseconds(Stopwatch.GetTimestamp() - _start).ToString("F3", CultureInfo.InvariantCulture) + "\t" + kind + "\t" +
            string.Join("\t", values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')));
        lock (_gate)
        {
            if (_pending.Count >= 8192) { _dropped++; return; }
            _pending.Enqueue(line);
            if (_writing != 0) return;
            _writing = 1;
        }
        _ = Task.Run(WritePending);
    }
    private async Task WritePending()
    {
        // Batch pointer bursts instead of opening a file for each input event.
        await Task.Delay(250).ConfigureAwait(false);
        while (true)
        {
            string[] lines;
            lock (_gate)
            {
                if (_pending.Count == 0) { _writing = 0; return; }
                lines = _pending.ToArray(); _pending.Clear();
            }
            try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.AppendAllLines(_path, lines); }
            catch { lock (_gate) { _pending.Clear(); _writing = 0; } return; }
            await Task.Delay(250).ConfigureAwait(false);
        }
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
