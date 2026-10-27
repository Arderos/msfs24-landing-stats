using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ReviewUiPolicyRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunCharts()
    {
        LegacyPitchGaps();
        CatalogWithoutPitchSource();
        GearGapsAndKnownHelpers();
        LoadExtrema();
        FutureTimeline();
    }

    private static LandingRecord Basic()
    {
        var record = new LandingRecord { RawPitchInputSourceIndex = 3, RawPitchInputCorrelation = -1 };
        record.RawControllerSourceIndices.Add(3);
        for (var index = 0; index < 6; index++) record.Series.Add(new LandingSeriesPoint
        {
            TimeSeconds = index * .1, GForce = 1.2, InertialFpm = 100, IndicatedFpm = 120,
            PilotPitchPercent = 10 + index,
            RawControllerYAxisPercent = new[] { index * 10d }, RawControllerYAxisValid = new[] { true },
            RawControllerYAxisAgeSeconds = new[] { .01 }
        });
        return record;
    }

    private static void CatalogWithoutPitchSource()
    {
        var record = Basic(); record.RawPitchInputSourceIndex = -1;
        record.RawControllerSourceIndices.Clear();
        foreach (var point in record.Series)
        { point.RawControllerYAxisPercent = Array.Empty<double>(); point.RawControllerYAxisValid = Array.Empty<bool>(); point.RawControllerYAxisAgeSeconds = Array.Empty<double>(); }
        record.ControllerTimeline = new ControllerCatalogTimeline
        { SampleCount = record.Series.Count, Catalogs = new[] { string.Empty }, Transitions = new[] { new[] { 0, 0 } } };
        var restored = LandingRecordFile.FromRecord(record).ToRecord();
        var chart = new LandingChart { Record = restored, Mode = LandingChartMode.FlightControls };
        Check(ReplayPresentationData.PitchValues(restored) == null, "An empty catalog selected a physical pitch series.");
        foreach (var point in restored.Series)
            Check(Pitch(chart, point) == point.PilotPitchPercent, "No-source catalog lost simulator pitch.");
    }

    private static double Pitch(LandingChart chart, LandingSeriesPoint point) =>
        (double)typeof(LandingChart).GetMethod("PitchInput", Private)!.Invoke(chart, new object[] { point })!;

    private static void LegacyPitchGaps()
    {
        foreach (var invalid in new[] { "invalid", "stale", "negative age", "range", "missing" })
        {
            var record = Basic(); var point = record.Series[2];
            if (invalid == "invalid") point.RawControllerYAxisValid[0] = false;
            if (invalid == "stale") point.RawControllerYAxisAgeSeconds[0] = 1;
            if (invalid == "negative age") point.RawControllerYAxisAgeSeconds[0] = -1;
            if (invalid == "range") point.RawControllerYAxisPercent[0] = 120;
            var restored = LandingRecordFile.FromRecord(record).ToRecord();
            // Storage rejects inconsistent column widths. Missing arrays can
            // still reach the public model accessor before persistence.
            if (invalid == "missing") restored.Series[2].RawControllerYAxisAgeSeconds = Array.Empty<double>();
            var chart = new LandingChart { Record = restored, Mode = LandingChartMode.FlightControls };
            Check(double.IsNaN(Pitch(chart, restored.Series[2])), invalid + " physical pitch became simulator input.");
            Check(Pitch(chart, restored.Series[4]) == -40, "Legacy mapping or inverted axis changed.");
            Check(ReplayControlInputs.Resolve(restored).Sample(.2).PitchPercent == null, invalid + " disagrees with replay availability.");

            // Verify the real renderer emits disconnected figures, rather than
            // merely changing an accessor while continuing to bridge the gap.
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
                typeof(LandingChart).GetMethod("DrawSeries", BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(typeof(LandingSeriesPoint)).Invoke(null, new object?[]
                    {
                        context, restored.Series, new Func<LandingSeriesPoint, double>(p => p.TimeSeconds),
                        new Func<LandingSeriesPoint, double>(p => Pitch(chart, p)), new Rect(0, 0, 500, 100),
                        0d, .5, -100d, 100d, new Pen(Brushes.Red, 1), null, double.PositiveInfinity
                    });
            var geometry = PathGeometry.CreateFromGeometry(((GeometryDrawing)drawing.Children.Single()).Geometry);
            Check(geometry.Figures.Count == 2, invalid + " raw pitch gap was joined by chart geometry; figures=" + geometry.Figures.Count +
                "; values=" + string.Join(",", restored.Series.Select(p => Pitch(chart, p))));
        }
        var simulatorOnly = Basic(); simulatorOnly.RawPitchInputSourceIndex = -1;
        Check(simulatorOnly.PitchInputPercent(simulatorOnly.Series[2]) == simulatorOnly.Series[2].PilotPitchPercent,
            "Legacy records with no selected raw source lost their simulator control series.");
    }

    private static LandingContactSeries Contact(int index, bool active)
    {
        var contact = new LandingContactSeries { ContactPointIndex = index };
        contact.Points.Add(new LandingContactPoint { TimeSeconds = 0, OnGround = active, CompressionPercent = active ? 20 + index : 0 });
        contact.Points.Add(new LandingContactPoint { TimeSeconds = 1, OnGround = active, CompressionPercent = active ? 30 + index : 0 });
        return contact;
    }

    private static void GearGapsAndKnownHelpers()
    {
        foreach (var includeMiddle in new[] { false, true })
        {
            var record = Basic(); record.ContactPoints.Add(Contact(0, true));
            if (includeMiddle) record.ContactPoints.Add(Contact(1, false));
            record.ContactPoints.Add(Contact(2, true)); record.ContactPoints.Add(Contact(11, true));
            Check(LandingGearSeriesBuilder.Build(record).SelectMany(series => series.ContactPointIndices).SequenceEqual(new[] { 0, 2, 11 }),
                "An inactive or absent contact hid later unclassified measured contacts.");
        }
        foreach (var incomplete in new[] { false, true })
        {
            var record = Basic(); record.AircraftTitle = "ToLiss A340-600";
            for (var index = 0; index <= 8; index++) record.ContactPoints.Add(Contact(index, !incomplete || index != 2));
            record.ContactPoints.Add(Contact(11, true)); record.ContactPoints.Add(Contact(14, true));
            var gear = LandingGearSeriesBuilder.Build(record);
            Check(gear.Count == 4 && gear[0].Role == LandingGearRole.Nose && gear[3].Role == LandingGearRole.MainThree,
                "Known ToLiss groups changed after an unloaded member.");
            Check(gear.SelectMany(series => series.ContactPointIndices).All(index => index <= 8) && gear[1].ContactPointIndices.Contains(3),
                "Known ToLiss helpers appeared or a real wheel was lost.");
        }
    }

    private static void LoadExtrema()
    {
        for (var channel = 0; channel < 3; channel++)
        foreach (var peak in new[] { -3d, 4d })
        {
            var record = Basic();
            if (channel == 0) record.Series[2].GForce = peak;
            else if (channel == 1) record.Series[2].LongitudinalLoadG = peak;
            else record.Series[2].LateralLoadG = peak;
            var chart = new LandingChart { Record = record, Mode = LandingChartMode.LoadFactors };
            foreach (int? isolated in new int?[] { null, channel })
            {
                typeof(LandingChart).GetField("_isolatedSeriesIndex", Private)!.SetValue(chart, isolated);
                object[] args = { record.Series, 0d, 0d };
                typeof(LandingChart).GetMethod("GetValueRange", Private)!.Invoke(chart, args);
                Check((double)args[1] < peak && (double)args[2] > peak,
                    $"Load peak clipped: channel={channel}, peak={peak}, isolated={isolated}.");
            }
        }
    }

    private static void FutureTimeline()
    {
        foreach (var transitions in new[] { new[] { new[] { 0, -1 }, new[] { 100, -1 } }, new int[][] { null! } })
        {
            var record = Basic(); record.ControllerTimeline = new ControllerCatalogTimeline
            { Version = 2, SampleCount = record.Series.Count, Transitions = transitions };
            var restored = LandingRecordFile.FromRecord(record).ToRecord();
            var chart = new LandingChart { Record = restored, Mode = LandingChartMode.LoadFactors, Width = 800, Height = 300 };
            Check(restored.ControllerTimeline!.Version == 2 && restored.Series.Count == 6 && ReplayControlInputs.Resolve(restored).Pitch == null,
                "Future metadata damaged ordinary metrics or became interpreted physical input.");
            Check(double.IsNaN(Pitch(chart, restored.Series[2])), "Opaque metadata fell back to physical input interpretation.");
            chart.Measure(new Size(800, 300)); chart.Arrange(new Rect(0, 0, 800, 300));
            using var context = new DrawingGroup().Open();
            typeof(LandingChart).GetMethod("OnRender", Private)!.Invoke(chart, new object[] { context });
        }
    }

    public static void RunStartupPolicyRaces()
    {
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        var previousMode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            foreach (var duringStartup in new[] { false, true })
            foreach (var finalEnabled in new[] { false, true }) StartupRace(duringStartup, finalEnabled);
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            Application.Current.ShutdownMode = previousMode;
        }
    }

    private static void StartupRace(bool duringStartup, bool finalEnabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "pu-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        using var constructed = new ManualResetEventSlim(); using var releaseConstruction = new ManualResetEventSlim();
        var restoreEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rawEnableCommands = 0;
        var rawEffects = 0;
        LandingRuntime? runtime = null; Task? startup = null;
        var window = new MainWindow();
        try
        {
            void Preference(bool enabled) => typeof(MainWindow).GetMethod("ApplyTelemetryPreference", Private)!.Invoke(window, new object[] { enabled });
            Preference(!finalEnabled);
            startup = window.InitializeRuntimeAsync(() =>
            {
                runtime = new LandingRuntime(Path.Combine(root, "r"), AppDomain.CurrentDomain.BaseDirectory, Path.Combine(root, "i"),
                    new LandingRepository(Path.Combine(root, "h")), new object(), (_, __) => Task.CompletedTask,
                    (_, __) => { }, (role, _) => role == ComponentRole.Upload ? "upload-ok" : "normal");
                var acquisition = runtime.Acquisition;
                var journal = (DurableRuntimeDispatcher)typeof(ApplicationRuntime).GetField("_journal", Private)!.GetValue(acquisition)!;
                var restoreField = typeof(DurableRuntimeDispatcher).GetField("_restoreConsumer", Private)!;
                var originalRestore = (Func<byte[]?, JournalCursor?, Task>)restoreField.GetValue(journal)!;
                restoreField.SetValue(journal, new Func<byte[]?, JournalCursor?, Task>(async (bytes, cursor) =>
                {
                    restoreEntered.TrySetResult(true);
                    if (duringStartup) await releaseRestore.Task.ConfigureAwait(false);
                    await originalRestore(bytes, cursor).ConfigureAwait(false);
                }));
                var recording = (RecordingRuntimeOwner)typeof(ApplicationRuntime).GetField("_recording", Private)!.GetValue(acquisition)!;
                var commitField = typeof(RecordingRuntimeOwner).GetField("_commitEffects", Private)!;
                var originalCommit = (Func<DurableRuntimeInput, RecordingRuntimeEffects, Task>)commitField.GetValue(recording)!;
                bool? raw = null, automatic = null;
                commitField.SetValue(recording, new Func<DurableRuntimeInput, RecordingRuntimeEffects, Task>(async (input, effects) =>
                {
                    if (input.Input.Kind == JournalRecordKind.RuntimeCommand)
                    {
                        var command = RecordingCommand.Decode(input.Input.CopyContent());
                        if (command.Kind == RecordingCommandKind.DeliveryPolicy) automatic = command.Enabled;
                        if (command.Kind == RecordingCommandKind.RawCapture)
                        {
                            raw = command.Enabled;
                            if (command.Enabled) Interlocked.Increment(ref rawEnableCommands);
                        }
                    }
                    Interlocked.Add(ref rawEffects, effects.RawCapture.Count);
                    if (input.Input.Kind == JournalRecordKind.SourceMessage)
                        firstSource.TrySetResult(automatic == finalEnabled && raw == false);
                    await originalCommit(input, effects).ConfigureAwait(false);
                }));
                Directory.CreateDirectory(runtime.Archives);
                var id = Guid.NewGuid(); var archive = Path.Combine(runtime.Archives, id.ToString("N") + ".zip");
                File.WriteAllText(archive, "isolated upload fixture");
                runtime.Outbox.SetAutomaticEnabled(true);
                runtime.Outbox.Register(id, archive, EpisodeTransactionStore.Hash(archive), true, DateTime.UtcNow);
                constructed.Set();
                if (!duringStartup) Check(releaseConstruction.Wait(TimeSpan.FromSeconds(15)), "Construction fixture was not released.");
                return runtime;
            });
            Pump(() => constructed.IsSet || startup.IsCompleted, "Runtime fixture was not constructed.");
            if (startup.IsCompleted) startup.GetAwaiter().GetResult();
            if (duringStartup) Pump(() => restoreEntered.Task.IsCompleted, "Runtime did not enter delayed durable startup.");
            var watch = Stopwatch.StartNew();
            Preference(finalEnabled); Preference(finalEnabled); // Immediate opt-out and save continuation.
            Check(watch.Elapsed < TimeSpan.FromSeconds(1), "A startup policy change blocked the dispatcher.");
            if (duringStartup)
            {
                var uploads = typeof(LandingRuntime).GetField("_uploads", Private)!.GetValue(runtime)!;
                Check(!(bool)uploads.GetType().GetField("_automaticAllowed", Private)!.GetValue(uploads)!,
                    "Automatic delivery became active while startup policy was unresolved.");
                Check(runtime!.Acquisition.Source(ComponentRole.Simulator).CurrentHost == null,
                    "Simulator started before the latest durable policy.");
            }
            releaseConstruction.Set(); releaseRestore.TrySetResult(true);
            Pump(() => startup.IsCompleted, "Runtime startup did not reconcile its latest preference."); startup.GetAwaiter().GetResult();
            var policy = (Task)typeof(MainWindow).GetField("_runtimePolicyTask", Private)!.GetValue(window)!;
            Pump(() => policy.IsCompleted, "Latest policy did not complete."); policy.GetAwaiter().GetResult();
            Check(runtime!.Ready && runtime.Outbox.Policy.AutomaticEnabled == finalEnabled, "Startup retained a superseded automatic policy.");
            Pump(() => firstSource.Task.IsCompleted, "Fixture source never reached the actual recorder.");
            Check(firstSource.Task.Result, "The first source frame must see the final upload policy with continuous RAW capture disabled.");
            if (!finalEnabled)
                Check(runtime.Outbox.Enumerate().All(id => runtime.Outbox.Read(id).State != OutboxState.Delivered),
                    "A queued automatic archive was delivered despite startup opt-out.");
            foreach (var enabled in new[] { !finalEnabled, finalEnabled })
            {
                Preference(enabled);
                policy = (Task)typeof(MainWindow).GetField("_runtimePolicyTask", Private)!.GetValue(window)!;
                Pump(() => policy.IsCompleted, "Running upload policy change did not complete."); policy.GetAwaiter().GetResult();
                Check(runtime.Outbox.Policy.AutomaticEnabled == enabled, "Running upload policy was not persisted.");
                Check(Volatile.Read(ref rawEnableCommands) == 0 && Volatile.Read(ref rawEffects) == 0,
                    "Automatic upload consent enabled continuous RAW recording or produced RAW capture effects.");
            }
        }
        finally
        {
            releaseConstruction.Set(); releaseRestore.TrySetResult(true);
            if (startup != null) { Pump(() => startup.IsCompleted, "Startup cleanup timed out."); _ = startup.Exception; }
            if (runtime != null) { var stop = runtime.StopAsync(); Pump(() => stop.IsCompleted, "Runtime cleanup timed out."); stop.GetAwaiter().GetResult(); }
            window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
            window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Pump(Func<bool> complete, string message)
    {
        if (complete()) return;
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (complete() || clock.Elapsed > TimeSpan.FromSeconds(20)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(complete(), message);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
