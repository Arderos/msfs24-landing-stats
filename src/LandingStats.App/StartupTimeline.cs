using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace LandingStats.App;

// Bounded best-effort timings, without user content, paths, tokens or network.
// Never makes a dispatcher callback wait for diagnostics I/O.
internal sealed class StartupTimeline
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new object();
    private readonly string _id = Guid.NewGuid().ToString("N");
    private Task _pending = Task.CompletedTask;
    private int _count;
    internal void Mark(string phase)
    {
        lock (_gate)
        {
            if (_count++ >= 32) return;
            var line = DateTime.UtcNow.ToString("o") + " startup=" + _id + " elapsed_ms=" +
                _clock.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + " " + phase + Environment.NewLine;
            _pending = _pending.ContinueWith(_ =>
            {
                try
                {
                    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSFS Landing Stats", "Runtime");
                    Directory.CreateDirectory(root); var path = Path.Combine(root, "startup.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.WriteAllText(path, line);
                    else File.AppendAllText(path, line);
                }
                catch { /* Startup diagnostics cannot prevent startup or shutdown. */ }
            }, TaskScheduler.Default);
        }
    }
}
