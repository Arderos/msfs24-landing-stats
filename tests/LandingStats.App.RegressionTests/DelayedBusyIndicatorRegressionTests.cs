using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace LandingStats.App.RegressionTests;

internal static class DelayedBusyIndicatorRegressionTests
{
    public static void RunAll()
    {
        FastOperationsNeverShowOrRefreshLate();
        RepeatedRefreshesDoNotRestartTheGracePeriod();
        ANewOperationGetsItsOwnGracePeriod();
        CancelledOrCompletedCallbacksCannotReappear();
    }

    private static void FastOperationsNeverShowOrRefreshLate()
    {
        var indicator = new DelayedBusyIndicator();
        var refreshes = 0;
        Action refresh = () => refreshes++;
        try
        {
            Require(!indicator.Update(true, "fast", refresh), "Starting work does not show a placeholder immediately.");
            PumpFor(75);
            Require(!indicator.IsVisible && refreshes == 0, "A fast operation remains visually quiet.");
            Require(!indicator.Update(false, "fast", refresh), "Completion clears the pending indicator.");
            PumpFor(600);
            Require(!indicator.IsVisible && refreshes == 0, "Completed work cannot flash a stale placeholder later.");
        }
        finally { indicator.Cancel(); }
    }

    private static void RepeatedRefreshesDoNotRestartTheGracePeriod()
    {
        var indicator = new DelayedBusyIndicator();
        var refreshes = 0;
        var elapsed = Stopwatch.StartNew();
        Action refresh = () => refreshes++;
        try
        {
            indicator.Update(true, "continuous", refresh);
            PumpUntil(() =>
            {
                indicator.Update(true, "continuous", refresh);
                return indicator.IsVisible;
            });
            Require(elapsed.Elapsed >= TimeSpan.FromMilliseconds(480),
                "Repeated presentation refreshes cannot bypass the half-second grace period.");
            Require(refreshes == 1, "A continuously busy operation publishes its delayed presentation exactly once.");
            Require(indicator.Update(true, "continuous", refresh), "Refreshing a visible operation keeps it visible.");
            PumpFor(75);
            Require(refreshes == 1, "Refreshing a visible operation does not schedule extra callbacks.");
            Require(!indicator.Update(false, null, refresh), "Finishing visible work hides the placeholder immediately.");
        }
        finally { indicator.Cancel(); }
    }

    private static void ANewOperationGetsItsOwnGracePeriod()
    {
        var indicator = new DelayedBusyIndicator();
        var firstRefreshes = 0;
        var secondRefreshes = 0;
        var thirdRefreshes = 0;
        try
        {
            indicator.Update(true, "first", () => firstRefreshes++);
            PumpFor(300);
            Require(!indicator.IsVisible, "The first operation is still inside its grace period.");
            var secondElapsed = Stopwatch.StartNew();
            Require(!indicator.Update(true, "second", () => secondRefreshes++),
                "Changing selection starts a separate grace period.");
            PumpFor(300);
            Require(!indicator.IsVisible && firstRefreshes == 0 && secondRefreshes == 0,
                "The first operation's deadline cannot display the second operation early.");
            PumpUntil(() => indicator.IsVisible);
            Require(secondElapsed.Elapsed >= TimeSpan.FromMilliseconds(480) &&
                firstRefreshes == 0 && secondRefreshes == 1,
                "Only the current operation publishes after its own half-second delay.");
            Require(!indicator.Update(true, "third", () => thirdRefreshes++),
                "Replacing already visible work hides its old placeholder immediately.");
            PumpFor(75);
            indicator.Cancel();
            indicator.Cancel();
            PumpFor(600);
            Require(!indicator.IsVisible && thirdRefreshes == 0,
                "Closing or cancelling an operation prevents a later dispatcher popup.");
        }
        finally { indicator.Cancel(); }
    }

    private static void CancelledOrCompletedCallbacksCannotReappear()
    {
        var indicator = new DelayedBusyIndicator();
        var refreshes = 0;
        try
        {
            indicator.Update(true, "completion-race", () =>
            {
                refreshes++;
                indicator.Update(false, null, () => throw new InvalidOperationException("Unexpected late refresh."));
            });
            PumpUntil(() => refreshes == 1);
            Require(!indicator.IsVisible, "Completion during a delayed refresh wins over showing the placeholder.");
            PumpFor(600);
            Require(!indicator.IsVisible && refreshes == 1, "A completed callback is one-shot and stays cancelled.");
        }
        finally { indicator.Cancel(); }
    }

    private static void PumpFor(int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        PumpUntil(() => elapsed.ElapsedMilliseconds >= milliseconds);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, __) => { if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(condition(), "Delayed busy indicator dispatcher regression timed out.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
