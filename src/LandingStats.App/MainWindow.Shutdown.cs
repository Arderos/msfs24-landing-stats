using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LandingStats.App.TelemetryUpload;

namespace LandingStats.App;

public partial class MainWindow
{
    private readonly AsyncWindowShutdown _windowShutdown = new AsyncWindowShutdown();
    private bool _shutdownResourcesDisposed;
    private readonly DelayedBusyIndicator _closingBusyIndicator = new DelayedBusyIndicator();

    private async void OnWindowClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_windowShutdown.CanClose) return;
        eventArgs.Cancel = true;
        if (_windowShutdown.IsClosing) return;
        var shutdown = _windowShutdown.BeginAsync(DrainWindowShutdownAsync);

        // Logical closing rejects callbacks/new work immediately, while the
        // dispatcher and the actual window remain alive for accepted writes.
        _isClosed = true;
        _settingsLoadingIndicator.Cancel();
        StopAcceptingSettingsOperations();
        StopHistoryOperations();
        _runwayBusyIndicator.Cancel();
        ShowClosingPlaceholder();
        await shutdown;
        _closingBusyIndicator.Cancel();
        Close();
    }

    private void ShowClosingPlaceholder()
    {
        if (Content is Panel content)
        {
            foreach (UIElement child in content.Children)
            {
                if (!ReferenceEquals(child, ClosingOverlay)) child.IsEnabled = false;
            }
        }
        Keyboard.ClearFocus();
        _closingBusyIndicator.Update(true, null, () =>
        {
            ClosingOverlay.Visibility = Visibility.Visible;
            ClosingOverlay.Focus();
        });
    }

    private async Task DrainWindowShutdownAsync()
    {
        _lifetimeCancellation.Cancel();
        _googleDriveAuthorizationCancellation?.Cancel();
        Loaded -= OnWindowLoaded;
        SourceInitialized -= OnSourceInitialized;
        ReplayView.TimeChanged -= OnChartHoverTimeChanged;
        foreach (var chart in _charts)
        {
            chart.HoverTimeChanged -= OnChartHoverTimeChanged;
            chart.ZoomRangeSelected -= OnChartZoomRangeSelected;
        }

        if (_recorder != null)
        {
            var recorder = _recorder;
            recorder.SimulatorExited -= OnSimulatorExited;
            try
            {
                // Dispose completes an active rollout synchronously and queues
                // its persistence. Snapshot the durable workers only AFTER it.
                recorder.Dispose();
            }
            catch (Exception exception)
            {
                // Native cleanup failure must not skip already accepted writes.
                Debug.WriteLine(exception);
            }
            finally
            {
                recorder.StatusChanged -= OnRecorderStatusChanged;
                recorder.EpisodeStarted -= OnEpisodeStarted;
                recorder.EpisodeCompleted -= OnEpisodeCompleted;
                recorder.AirportFacilitiesUpdated -= OnAirportFacilitiesUpdated;
                recorder.RunwayFacilitiesUpdated -= OnRunwayFacilitiesUpdated;
                recorder.AircraftGroundStateChanged -= OnAircraftGroundStateChanged;
                _recorder = null;
            }
        }
        if (_messageSource != null)
        {
            _messageSource.RemoveHook(WindowProcedure);
            _messageSource = null;
        }

        Task episodes;
        Task metadata;
        Task reports;
        lock (_episodeProcessingGate) episodes = _episodePersistenceTask;
        lock (_airportMetadataPersistenceGate) metadata = _airportMetadataPersistenceTask;
        lock (_bugReportPersistenceGate) reports = _bugReportPersistenceTask;

        // Local data has no arbitrary shutdown timeout. Network retries do not
        // belong to this durability boundary and retain their on-disk backlog.
        // Upload disposal has a bounded worker wait; keep even that off the UI.
        var uploadDisposal = Task.Run(() =>
        {
            TelemetryUploadClient? uploadClient;
            // A startup retry can still be constructing its client (including
            // the identity directory) under this gate. Take ownership off the
            // dispatcher, after the same gate has published that client. New
            // creation is already rejected by the close/cancellation guard.
            lock (_telemetryUploadClientGate)
            {
                uploadClient = _telemetryUploadClient;
                _telemetryUploadClient = null;
            }
            if (uploadClient == null) return;
            uploadClient.StatusChanged -= OnTelemetryUploadStatusChanged;
            uploadClient.Dispose();
        });
        await AsyncWindowShutdown.DrainAcceptedWorkAsync(
            episodes,
            metadata,
            reports,
            PendingSettingsOperations,
            PendingHistoryOperations,
            _initializationPersistenceTask,
            _localDeletionTask,
            _runwayPersistenceTask,
            uploadDisposal);
    }

    private void DisposeShutdownResources()
    {
        if (_shutdownResourcesDisposed) return;
        _shutdownResourcesDisposed = true;
        _releaseUpdater.Dispose();

        // Canceled network continuations may still be unwinding. Do not dispose
        // their semaphore, HTTP clients or token source underneath those callers,
        // and do not hold shutdown hostage to an unresponsive remote service.
        if (!GoogleDriveBusy && _googleDriveSyncTask.IsCompleted && _googleDriveDeleteTask.IsCompleted)
        {
            _googleDriveBackupService.Dispose();
            _googleDriveApiClient.Dispose();
            _googleOAuthClient.Dispose();
            if (_initializationTask.IsCompleted && _bugReportRetryTask.IsCompleted && !_bugReportSubmitting)
            {
                _lifetimeCancellation.Dispose();
            }
        }
    }
}
