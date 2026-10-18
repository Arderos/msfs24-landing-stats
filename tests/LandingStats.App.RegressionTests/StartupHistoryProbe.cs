using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LandingStats.App;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

// Opt-in timing of the real history/UI path against a private data copy. Never
// shows the window or runs startup, simulator, cloud, updater or user mutations.
internal static class StartupHistoryProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static int Run(string directory, bool runtimeFirst = false, bool slowRuntime = false)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var input = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSFS Landing Stats");
        var history = Path.Combine(directory, "Landings"); Directory.CreateDirectory(history);
        foreach (var file in Directory.GetFiles(Path.Combine(input, "Landings"), "*.gz"))
            File.Copy(file, Path.Combine(history, Path.GetFileName(file)), false);
        var airports = Path.Combine(directory, "airports.gz");
        File.Copy(Path.Combine(input, "airport-facilities.json.gz"), airports, false);
        using var log = new StreamWriter(Path.Combine(directory, "history-startup.tsv")) { AutoFlush = true };
        var clock = Stopwatch.StartNew();
        void Mark(string name) { var text = clock.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "\t" + name; log.WriteLine(text); Console.WriteLine(text); }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources.MergedDictionaries.Add(Review089UiRegressionTests.LoadApplicationResources());
        Mark("resources");
        var window = new MainWindow();
        Mark("window_created");
        void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        object? Invoke(string name) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, null);
        Set("_repository", new LandingRepository(history)); Set("_airportFacilityRepository", new AirportFacilityRepository(airports));
        Set("_startupReady", true);
        if (runtimeFirst)
        {
            var construction = Task.Run(async () =>
            {
                Mark("runtime_construction_start");
                using var runtime = new LandingRuntime(Path.Combine(directory, "Runtime"), AppDomain.CurrentDomain.BaseDirectory,
                    Path.Combine(directory, "Upload"), new LandingRepository(history), new object(), (_, __) => Task.CompletedTask,
                    (name, generation) => Mark("escalation:" + name + ":" + generation),
                    (role, _) => role == ComponentRole.Analysis ? "analysis-local" : role == ComponentRole.Upload ? "upload-ok" : "normal",
                    airportRepository: new AirportFacilityRepository(airports));
                Mark("runtime_constructed"); await runtime.StartAsync(false); Mark("runtime_started");
                var stopping = runtime.StopAsync();
                for (var wait = 0; wait < 10 && !stopping.IsCompleted; wait++)
                {
                    await Task.WhenAny(stopping, Task.Delay(1000));
                    if (!stopping.IsCompleted) Mark(string.Join(";", runtime.Health.Select(value => value.Component + ":" + value.State + ":" + value.Reason)));
                }
                if (!stopping.IsCompleted) throw new TimeoutException("Immediate runtime shutdown did not drain.");
                await stopping; Mark("runtime_stopped");
            });
            var waiting = new DispatcherFrame(); var waitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            waitTimer.Tick += (_, _) => { if (construction.IsCompleted || clock.Elapsed.TotalSeconds > 60) waiting.Continue = false; };
            waitTimer.Start(); try { Dispatcher.PushFrame(waiting); } finally { waitTimer.Stop(); }
            if (!construction.IsCompleted) throw new TimeoutException("Runtime startup probe timed out.");
            construction.GetAwaiter().GetResult();
        }
        var load = slowRuntime ? window.InitializeWindowContentAsync(async () =>
        { Mark("runtime_hold_begin"); await Task.Delay(5000); Mark("runtime_hold_released"); }) : (Task)Invoke("InitializeHistoryAsync")!;
        var dispatcher = new DispatcherFrame(); var seenHistory = false; var seenDetail = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (!seenHistory && load.IsCompleted) { seenHistory = true; Mark("history_completed"); }
            if (!seenDetail && window.DataContext is LandingRecord record) { seenDetail = true; Mark("detail_presented:" + record.Id); }
            if (seenHistory && seenDetail || clock.Elapsed.TotalSeconds > 90) dispatcher.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(dispatcher); load.GetAwaiter().GetResult(); }
        finally
        {
            timer.Stop(); Invoke("StopHistoryOperations");
            window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
            window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
            window.Close();
        }
        Mark("finished"); return seenHistory && seenDetail ? 0 : 2;
    }
}
