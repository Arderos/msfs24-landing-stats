using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LandingStats.App;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.Runtime;
using LandingStats.App.Updates;

namespace LandingStats.App.RegressionTests;

internal static class WindowStartupRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void DeferredUpdateResumes()
    {
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        var oldMode = Application.Current.ShutdownMode; var oldContext = SynchronizationContext.Current;
        Application.Current.Resources.MergedDictionaries.Add(resources); Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(); using var handler = new HeldUpdateHandler(); using var updater = new ReleaseUpdater(handler);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        try
        {
            ((ReleaseUpdater)typeof(MainWindow).GetField("_releaseUpdater", Private)!.GetValue(window)!).Dispose();
            Set("_releaseUpdater", updater); Set("_releaseUpdateResult", new ReleaseUpdateResult(ReleaseUpdateState.Unavailable, "Update.DeferredUnsafe"));
            var retry = typeof(MainWindow).GetMethod("RetryDeferredUpdateIfSafe", Private)!;
            retry.Invoke(window, null); Check(handler.Requests == 0, "Deferred update retried while runtime was not ready.");
            // Failed startup is an existing safe-restart state; no worker or
            // signed installer is needed to exercise the real retry entry point.
            Set("_runtimeStartupFailed", true); retry.Invoke(window, null);
            Pump(() => handler.Requests == 1, "Deferred update never resumed at a safe state.");
            retry.Invoke(window, null); retry.Invoke(window, null);
            Check(handler.Requests == 1, "Repeated ready notifications launched concurrent update checks.");
            handler.Release(); Pump(() => !(bool)typeof(MainWindow).GetField("_updateCheckRunning", Private)!.GetValue(window)!, "Update check did not complete.");
            retry.Invoke(window, null); Check(handler.Requests == 1, "A completed non-deferred check retried without a reason.");
        }
        finally
        {
            handler.Release(); Set("_isClosed", true);
            window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
            window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!); window.Close();
            Application.Current.Resources.MergedDictionaries.Remove(resources); Application.Current.ShutdownMode = oldMode;
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
    private sealed class HeldUpdateHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Requests); return _reply.Task; }
        public void Release() => _reply.TrySetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "startup-window-" + Guid.NewGuid().ToString("N"));
        var oldContext = SynchronizationContext.Current; var oldMode = Application.Current.ShutdownMode;
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        Application.Current.Resources.MergedDictionaries.Add(resources); Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            foreach (var fail in new[] { false, true })
            {
                var window = new MainWindow();
                var runtime = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var repository = new LandingRepository(Path.Combine(root, fail ? "failed" : "waiting"));
                var landing = new LandingRecord { AircraftTitle = "Local history before runtime" }; repository.Save(landing);
                void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
                Set("_repository", repository); Set("_airportFacilityRepository", new AirportFacilityRepository(Path.Combine(root, "airports.gz")));
                var started = false;
                var startup = window.InitializeWindowContentAsync(() => { started = true; return runtime.Task; });
                try
                {
                    Pump(() => window.DataContext is LandingRecord, "Local history waited for unavailable runtime.");
                    Check(started && !startup.IsCompleted && (window.DataContext as LandingRecord)?.Id == landing.Id,
                        "Local content did not become usable independently of runtime startup.");
                    if (fail) runtime.SetException(new IOException("Fixture runtime unavailable")); else runtime.SetResult(true);
                    Pump(() => startup.IsCompleted, "Startup did not finish after releasing runtime.");
                    Check(startup.IsFaulted == fail && (window.DataContext as LandingRecord)?.Id == landing.Id,
                        "Runtime failure hid or discarded the loaded logbook.");
                    _ = startup.Exception;
                    // A cloud owner can publish a new history revision without
                    // changing its ordinary sync revision after recovery.
                    Set("_runtimeCloudRevision", 7L); Set("_runtimeCloudHistoryRevision", 1L);
                    var snapshot = new RuntimePresentationSnapshot(10, null, null, Guid.Empty, 0, false,
                        Array.Empty<RuntimeEpisodeNotice>(), Array.Empty<SupervisionSnapshot>(), Guid.Empty, null,
                        new RuntimeCloudNotice(7, DateTime.UtcNow, 2, null));
                    Task Apply() => (Task)typeof(MainWindow).GetMethod("ApplyRuntimeSnapshotAsync", Private)!.Invoke(window,
                        new object[] { snapshot, CancellationToken.None })!;
                    var first = Apply(); Pump(() => first.IsCompleted, "Cloud history projection did not complete."); first.GetAwaiter().GetResult();
                    var appliedVersion = (long)typeof(MainWindow).GetField("_historyLoadVersion", Private)!.GetValue(window)!;
                    var second = Apply(); Pump(() => second.IsCompleted, "Repeated cloud projection did not complete."); second.GetAwaiter().GetResult();
                    Check((long)typeof(MainWindow).GetField("_historyLoadVersion", Private)!.GetValue(window)! == appliedVersion,
                        "Identical cloud history revision repeatedly reloaded history.");

                }
                finally
                {
                    runtime.TrySetResult(true); Pump(() => startup.IsCompleted, "Startup fixture did not drain."); _ = startup.Exception;
                    typeof(MainWindow).GetMethod("StopHistoryOperations", Private)!.Invoke(window, null);
                    var pending = (Task)typeof(MainWindow).GetProperty("PendingHistoryOperations", Private)!.GetValue(window)!;
                    Pump(() => pending.IsCompleted, "History did not drain.");
                    window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", Private)!);
                    window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", Private)!);
                    window.Close();
                }
            }
            SessionEndingPumpsAcceptedWork();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(oldContext); Application.Current.ShutdownMode = oldMode;
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void SessionEndingPumpsAcceptedWork()
    {
        foreach (var completesInsideBudget in new[] { true, false })
        {
            var window = new MainWindow();
            var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(MainWindow).GetField("_bugReportPersistenceTask", Private)!.SetValue(window, accepted.Task);
            try
            {
                if (completesInsideBudget) window.Dispatcher.BeginInvoke(new Action(() => accepted.TrySetResult(true)), DispatcherPriority.Background);
                var clock = Stopwatch.StartNew();
                var completed = window.DrainSessionEnding(completesInsideBudget ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(40));
                Check(completed == completesInsideBudget, "Session ending did not honor its accepted-work drain/budget boundary.");
                Check(clock.Elapsed < TimeSpan.FromSeconds(4), "Session ending exceeded its bounded wait.");
                accepted.TrySetResult(true);
                var state = (AsyncWindowShutdown)typeof(MainWindow).GetField("_windowShutdown", Private)!.GetValue(window)!;
                Pump(() => state.Completion.IsCompleted, "Accepted work could not finish after session-ending budget expired.");
                state.Completion.GetAwaiter().GetResult();
            }
            finally { accepted.TrySetResult(true); window.Close(); }
        }
    }

    private static void Pump(Func<bool> done, string error)
    {
        if (done()) return;
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (done() || clock.Elapsed.TotalSeconds > 4) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(done(), error);
    }
    private static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
}
