using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LandingStats.App.RegressionTests;

internal static class AsyncShutdownRegressionTests
{
    public static void RunAll()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            ClosingYieldsAndRejectsReentryWhileDurableWorkDrains();
            EvenAnIdleCloseUnwindsTheFirstClosingEvent();
            OneStorageFailureDoesNotAbandonOtherAcceptedWrites();
            FailedPersistenceDoesNotMarkAnIncompleteDrainComplete();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void ClosingYieldsAndRejectsReentryWhileDurableWorkDrains()
    {
        var shutdown = new AsyncWindowShutdown();
        var episode = new TaskCompletionSource<bool>();
        var settings = new TaskCompletionSource<bool>();
        var migration = new TaskCompletionSource<bool>();
        var stoppedRecorder = false;
        var drainCalls = 0;
        var uiThread = Thread.CurrentThread.ManagedThreadId;
        var wrongThread = false;
        var drain = shutdown.BeginAsync(async () =>
        {
            drainCalls++;
            stoppedRecorder = true;
            wrongThread |= Thread.CurrentThread.ManagedThreadId != uiThread;
            await AsyncWindowShutdown.DrainAcceptedWorkAsync(episode.Task, settings.Task, migration.Task);
            wrongThread |= Thread.CurrentThread.ManagedThreadId != uiThread;
        });

        Require(shutdown.IsClosing && !shutdown.CanClose, "The first close is canceled before accepted work drains.");
        Require(!stoppedRecorder, "The first Closing event unwinds before cleanup begins.");
        var duplicate = shutdown.BeginAsync(() => throw new InvalidOperationException("Duplicate drain."));
        Require(ReferenceEquals(drain, duplicate), "Repeated Close shares the original shutdown operation.");

        var heartbeats = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, __) => heartbeats++;
        timer.Start();
        try
        {
            PumpUntil(() => heartbeats >= 3);
            Require(stoppedRecorder && !drain.IsCompleted && !shutdown.CanClose,
                "The dispatcher remains responsive while all local writes are delayed.");
            episode.SetResult(true);
            settings.SetResult(true);
            var previousHeartbeats = heartbeats;
            PumpUntil(() => heartbeats >= previousHeartbeats + 3);
            Require(!drain.IsCompleted && !shutdown.CanClose, "History migration remains part of the durability boundary.");
            migration.SetResult(true);
            PumpUntil(() => drain.IsCompleted);
            drain.GetAwaiter().GetResult();
            Require(shutdown.CanClose && drainCalls == 1 && !wrongThread,
                "Only the completed drain allows final close, with dispatcher affinity retained.");
            Require(ReferenceEquals(drain, shutdown.BeginAsync(() => Task.CompletedTask)),
                "A final close cannot start a second cleanup.");
        }
        finally
        {
            timer.Stop();
            episode.TrySetResult(true);
            settings.TrySetResult(true);
            migration.TrySetResult(true);
        }
    }

    private static void EvenAnIdleCloseUnwindsTheFirstClosingEvent()
    {
        var shutdown = new AsyncWindowShutdown();
        var cleanup = false;
        var completion = shutdown.BeginAsync(() => { cleanup = true; return Task.CompletedTask; });
        Require(!cleanup && !completion.IsCompleted && !shutdown.CanClose,
            "An idle close must not reenter Window.Close during the original Closing event.");
        PumpUntil(() => completion.IsCompleted);
        completion.GetAwaiter().GetResult();
        Require(cleanup && shutdown.CanClose, "Idle cleanup still completes without blocking.");
    }

    private static void FailedPersistenceDoesNotMarkAnIncompleteDrainComplete()
    {
        var shutdown = new AsyncWindowShutdown();
        var completion = shutdown.BeginAsync(() => Task.FromException(new InvalidOperationException("Synthetic failure.")));
        PumpUntil(() => completion.IsCompleted);
        Require(completion.IsFaulted && shutdown.CanClose, "A reported cleanup failure must not trap a closed intake in the Closing state.");
        _ = completion.Exception;
    }

    private static void OneStorageFailureDoesNotAbandonOtherAcceptedWrites()
    {
        var shutdown = new AsyncWindowShutdown();
        var pending = new TaskCompletionSource<bool>();
        var failed = Task.FromException(new InvalidOperationException("Synthetic disk failure."));
        var canceled = Task.FromCanceled(new CancellationToken(true));
        var completion = shutdown.BeginAsync(() => AsyncWindowShutdown.DrainAcceptedWorkAsync(failed, canceled, pending.Task));
        var heartbeat = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => heartbeat = true));
        PumpUntil(() => heartbeat);
        Require(!completion.IsCompleted && !shutdown.CanClose,
            "Failed or canceled writes cannot abandon another accepted local write.");
        pending.SetResult(true);
        PumpUntil(() => completion.IsCompleted);
        completion.GetAwaiter().GetResult();
        Require(shutdown.CanClose, "Reported disk errors do not strand completed cleanup.");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, __) => { if (condition() || clock.Elapsed.TotalSeconds > 5) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(condition(), "Shutdown dispatcher regression timed out.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
