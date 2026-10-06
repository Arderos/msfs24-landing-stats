using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

internal static class RecorderConnectionRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        SlowConnectionKeepsDispatcherFreeAndSerializesRetries();
        ClosingDoesNotWaitForNativeConnectionOrPublishLateErrors();
        DispatcherShutdownDoesNotStrandFailedAttempt();
    }

    private static void SlowConnectionKeepsDispatcherFreeAndSerializesRetries()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var uiThread = Thread.CurrentThread.ManagedThreadId;
        var workerThread = uiThread;
        var calls = 0;
        var wrongStatusThread = false;
        var errors = 0;
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero, () =>
        {
            workerThread = Thread.CurrentThread.ManagedThreadId;
            var call = Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test factory was not released.");
            if (call == 1) throw new COMException("Simulator is absent.");
            throw new InvalidOperationException("Unexpected connection failure.");
        });
        recorder.StatusChanged += (_, args) =>
        {
            wrongStatusThread |= Thread.CurrentThread.ManagedThreadId != uiThread;
            if (args.State == RecorderState.Error) errors++;
        };
        try
        {
            Attempt(recorder);
            Require(entered.Wait(TimeSpan.FromSeconds(5)), "Connection worker starts.");
            Require(workerThread != uiThread, "Native connection construction must not run on the UI thread.");
            var probeRan = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => probeRan = true));
            PumpUntil(() => probeRan);
            Require(!Pending(recorder).IsCompleted, "UI processes input while connection is blocked.");
            for (var index = 0; index < 2; index++)
            {
                var handled = false;
                recorder.HandleWindowMessage(SimConnectLandingRecorder.WindowMessage, ref handled);
                Require(handled, "Early native notifications are retained during connection construction.");
            }
            Require((int)typeof(SimConnectLandingRecorder).GetField("_pendingConnectionMessages", PrivateInstance)!.GetValue(recorder)! == 2,
                "All early notifications are retained, not only OPEN.");
            Attempt(recorder);
            Require(calls == 1, "Retry must not overlap an in-flight connection attempt.");
            release.Set();
            PumpUntil(() => Pending(recorder).IsCompleted);
            Pending(recorder).GetAwaiter().GetResult();
            Require(errors == 0, "Absent simulator remains Waiting, not an error.");
            Attempt(recorder);
            Require((int)typeof(SimConnectLandingRecorder).GetField("_pendingConnectionMessages", PrivateInstance)!.GetValue(recorder)! == 0,
                "A new attempt discards notifications from a failed connection.");
            PumpUntil(() => Pending(recorder).IsCompleted);
            Pending(recorder).GetAwaiter().GetResult();
            Require(calls == 2 && errors == 1, "Failed attempts release the gate and unexpected errors are reported.");
            Require(!wrongStatusThread, "Status events stay on the recorder's UI dispatcher.");
        }
        finally { release.Set(); }
    }

    private static void ClosingDoesNotWaitForNativeConnectionOrPublishLateErrors()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var statusCount = 0;
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero, () =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test factory was not released.");
            throw new COMException("Late native failure.");
        });
        recorder.StatusChanged += (_, __) => statusCount++;
        try
        {
            Attempt(recorder);
            Require(entered.Wait(TimeSpan.FromSeconds(5)), "Connection worker starts before closing.");
            recorder.Dispose();
            Require(!Pending(recorder).IsCompleted, "Dispose must return without waiting for the blocked constructor.");
            var before = statusCount;
            release.Set();
            Require(Pending(recorder).Wait(TimeSpan.FromSeconds(5)), "Canceled attempt completes without pumping the closed UI.");
            Attempt(recorder);
            Require(calls == 1 && statusCount == before, "No reconnect or late status is allowed after disposal.");
        }
        finally { release.Set(); }
    }

    private static void DispatcherShutdownDoesNotStrandFailedAttempt()
    {
        using var release = new ManualResetEventSlim();
        Task? pending = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var recorder = new SimConnectLandingRecorder(IntPtr.Zero, () =>
                {
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test factory was not released.");
                    throw new COMException("Dispatcher already stopped.");
                });
                Attempt(recorder);
                pending = Pending(recorder);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                release.Set();
                Require(pending.Wait(TimeSpan.FromSeconds(5)), "Shutdown must not strand an error notification on the dispatcher.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        try
        {
            Require(thread.Join(TimeSpan.FromSeconds(10)), "Shutdown diagnostic finishes.");
            if (failure != null) throw new InvalidOperationException("Dispatcher shutdown regression.", failure);
            Require(pending != null && pending.Status == TaskStatus.RanToCompletion, "Shutdown attempt completes successfully.");
        }
        finally { release.Set(); }
    }

    private static void Attempt(SimConnectLandingRecorder recorder) =>
        typeof(SimConnectLandingRecorder).GetMethod("TryConnect", PrivateInstance)!.Invoke(recorder, null);

    private static Task Pending(SimConnectLandingRecorder recorder) =>
        (Task)typeof(SimConnectLandingRecorder).GetField("_connectionTask", PrivateInstance)!.GetValue(recorder)!;

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, __) => { if (condition() || clock.Elapsed.TotalSeconds > 5) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(condition(), "Dispatcher operation timed out.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
