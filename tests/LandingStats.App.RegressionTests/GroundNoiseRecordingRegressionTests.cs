using System;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class GroundNoiseRecordingRegressionTests
{
    public static void RunAll()
    {
        var prefix = GroundNoiseRegressionTests.ReadGroundPrefix();
        foreach (var duration in new[] { 2.0, 20.0 })
        foreach (var control in new[] { false, true })
        foreach (var restoreAfter in new[] { -1, prefix.Length - 2, prefix.Length - 1, prefix.Length })
        {
            var samples = GroundNoiseRegressionTests.ContinueVertically(prefix, duration, control);
            var before = samples.Select(TelemetryCsv.Format).ToArray();
            var recorder = new ReplayRecordingRegressionTests.Harness();
            for (var index = 0; index < samples.Count; index++)
            {
                recorder.Feed(samples[index].CopyForAnalysis());
                if (index == restoreAfter) recorder.Restore(recorder.Coordinator.CaptureCheckpoint());
            }
            recorder.Coordinator.Stop();
            Check(recorder.Captures.Count > 0 && recorder.Captures.All(capture => !capture.IsDiagnosticOnly),
                "Recorded ground noise changed a coherent departure into a diagnostic-only capture.");
            Check(recorder.Captures.Sum(capture => TouchdownAnalysis.Analyze(capture.Samples).Count) == 1,
                "Recorded ground noise lost its coherent vertical contact during recording or recorder recovery.");
            Check(recorder.Captures.Sum(capture => capture.DecisionJournal!.Snapshot().Entries.Count(entry => entry.Code == "contact_flag_transition")) == 1,
                "The recorder's accepted ground-noise contact disagreed with the stored analysis.");
            Check(before.SequenceEqual(samples.Select(TelemetryCsv.Format)),
                "Recording a coherent departure changed the source ground-noise observations.");
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
