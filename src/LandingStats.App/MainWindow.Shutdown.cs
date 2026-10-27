using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;

namespace LandingStats.App;

public partial class MainWindow
{
    private readonly AsyncWindowShutdown _windowShutdown = new AsyncWindowShutdown();
    private bool _shutdownResourcesDisposed;
    private GuardianStopReason _stopReason = GuardianStopReason.UserExit;

    private async void OnWindowClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_windowShutdown.CanClose) return;
        eventArgs.Cancel = true;
        if (_windowShutdown.IsClosing) return;
        var shutdown = BeginWindowShutdown();
        try { await shutdown; }
        catch (Exception error) { Debug.WriteLine(error); }
        Close();
    }

    private Task BeginWindowShutdown()
    {
        var shutdown = _windowShutdown.BeginAsync(DrainWindowShutdownAsync);
        _isClosed = true;
        _settingsLoadingIndicator.Cancel();
        StopAcceptingSettingsOperations();
        StopHistoryOperations();
        ShowClosingPlaceholder();
        return shutdown;
    }

    internal bool DrainSessionEnding(TimeSpan budget)
    {
        _stopReason = GuardianStopReason.SessionEnd;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) ShutdownBlockReasonCreate(handle, LocalizationManager.Text("Lifecycle.Closing"));
        try
        {
            var drain = BeginWindowShutdown();
            if (!drain.IsCompleted)
            {
                // WM_QUERYENDSESSION is synchronous. Pump only for this bounded
                // drain so already accepted dispatcher continuations can finish.
                var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
                var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, __) => { if (drain.IsCompleted || elapsed.Elapsed >= budget) frame.Continue = false; };
                timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            }
            if (drain.IsFaulted) Debug.WriteLine(drain.Exception);
            return drain.IsCompleted;
        }
        finally { if (handle != IntPtr.Zero) ShutdownBlockReasonDestroy(handle); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);
    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);

    private void ShowClosingPlaceholder()
    {
        if (Content is Panel content)
        {
            foreach (UIElement child in content.Children)
            {
                if (!ReferenceEquals(child, ClosingOverlay)) child.IsEnabled = false;
            }
        }
        ClosingOverlay.Visibility = Visibility.Visible;
        if (IsActive)
        {
            Keyboard.ClearFocus();
            ClosingOverlay.Focus();
        }
    }

    private async Task DrainWindowShutdownAsync()
    {
        App.ReportShutdownProgress();
        var acknowledgement = App.AcknowledgeExitAsync(_stopReason);
        _googleDriveAuthorizationCancellation?.Cancel();
        Loaded -= OnWindowLoaded;
        ReplayView.TimeChanged -= OnChartHoverTimeChanged;
        foreach (var chart in _charts)
        {
            chart.HoverTimeChanged -= OnChartHoverTimeChanged;
            chart.ZoomRangeSelected -= OnChartZoomRangeSelected;
        }

        try { _lifetimeCancellation.Cancel(); } catch (AggregateException error) { Debug.WriteLine(error); }
        var runtimeShutdown = StopRuntimeAsync();
        Task reports;
        lock (_bugReportPersistenceGate) reports = _bugReportPersistenceTask;

        // Runtime owns source retirement, recording checkpoints and delivery.
        // Accepted UI writes remain part of the same shutdown boundary.
        var accepted = new[]
        {
            acknowledgement,
            reports,
            PendingSettingsOperations,
            PendingHistoryOperations,
            _initializationPersistenceTask,
            _localDeletionTask,
            runtimeShutdown,
            _runtimePolicyTask ?? Task.CompletedTask,
        };
        foreach (var operation in accepted)
            _ = operation.ContinueWith(_ => App.ReportShutdownProgress(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await AsyncWindowShutdown.DrainAcceptedWorkAsync(accepted);
    }

    private void DisposeShutdownResources()
    {
        if (_shutdownResourcesDisposed) return;
        _shutdownResourcesDisposed = true;
        _services.Settings.Changed -= OnCommittedSettingsChanged;
        _releaseUpdater.Dispose();

        // Canceled network continuations may still be unwinding. Do not dispose
        // their semaphore, HTTP clients or token source underneath those callers,
        // and do not hold shutdown hostage to an unresponsive remote service.
        if (!GoogleDriveBusy && _googleDriveSyncTask.IsCompleted && _localDeletionTask.IsCompleted)
        {
            _googleDriveBackupService.Dispose();
            if (_initializationTask.IsCompleted && !_bugReportSubmitting)
            {
                _lifetimeCancellation.Dispose();
            }
        }
    }
}
