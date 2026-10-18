using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace LandingStats.App.Runtime;

internal enum EpisodeTimelinePhase
{
    CaptureCommitted, GeometryStarted, GeometryCommitted, AnalysisStarted, AnalysisCommitted,
    HistoryCommitted, ReportStarted, ReportCommitted, UiEpisodeChanged, UiAnalyzing,
    UiHistoryApplied, UiSaved, UiDetailApplied,
}

// Best-effort wall-clock evidence across the pipeline and UI. Only IDs, phases
// and counts are recorded. Disk I/O never runs on a caller or dispatcher thread;
// a stuck writer can retain at most 128 small entries and cannot block recording.
internal static class EpisodeTimeline
{
    private static readonly object Gate = new object();
    private static readonly Queue<Tuple<string, string>> Pending = new Queue<Tuple<string, string>>();
    private static bool _writing;
    private static Task _writer = Task.CompletedTask;
    internal static void Mark(string runtimeRoot, Guid id, EpisodeTimelinePhase phase, int count = 0)
    {
        var line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " id=" + id.ToString("N") +
            " phase=" + phase + " count=" + count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;
        lock (Gate)
        {
            if (Pending.Count >= 128) return;
            Pending.Enqueue(Tuple.Create(runtimeRoot, line));
            if (_writing) return;
            _writing = true;
            _writer = Task.Run(WritePending);
        }
    }

    // Private test roots can wait for diagnostics before removing their files.
    // Production shutdown intentionally never waits for best-effort logging.
    internal static Task FlushAsync() { lock (Gate) return _writer; }

    private static void WritePending()
    {
        while (true)
        {
            Tuple<string, string> entry;
            lock (Gate)
            {
                if (Pending.Count == 0) { _writing = false; return; }
                entry = Pending.Dequeue();
            }
            try
            {
                Directory.CreateDirectory(entry.Item1);
                var path = Path.Combine(entry.Item1, "episode-timeline.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 512 * 1024) File.WriteAllText(path, entry.Item2);
                else File.AppendAllText(path, entry.Item2);
            }
            catch { /* Diagnostic failure cannot affect processing or presentation. */ }
        }
    }
}
