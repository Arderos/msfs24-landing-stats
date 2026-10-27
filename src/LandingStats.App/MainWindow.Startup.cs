using System;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using LandingStats.App.Updates;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.TelemetryUpload;

namespace LandingStats.App;

public partial class MainWindow
{
    private bool _initializationStarted;
    private bool _startupReady;
    private Task _initializationTask = Task.CompletedTask;
    private Task _initializationPersistenceTask = Task.CompletedTask;
    private Task _localDeletionTask = Task.CompletedTask;

    internal async Task InitializeWindowContentAsync(Func<Task> startRuntime)
    {
        // Local history has no dependency on engine retention, journal recovery,
        // SDK startup or cloud credentials. Observe both tasks, independently.
        var history = LoadHistory();
        var runtime = StartRuntime();
        await Task.WhenAll(history, runtime);
        async Task LoadHistory()
        { _startupTimeline?.Mark("history_begin"); await InitializeHistoryAsync(); _startupTimeline?.Mark("history_ready"); }
        async Task StartRuntime() => await startRuntime();
    }

    private Task _runtimeInitializationTask = Task.CompletedTask;
    private bool _runtimeStartupFailed;
    private bool _runtimeRetrying;
    private readonly object _runtimeStartupGate = new object();

    private Task BeginRuntimeStartup()
    {
        lock (_runtimeStartupGate) return _runtimeInitializationTask = TryStartRuntimeAsync();
    }

    private async Task TryStartRuntimeAsync()
    {
        _runtimeRetrying = true; _runtimeStartupFailed = false; RetryRuntimeButton.IsEnabled = false;
        try
        {
            await InitializeRuntimeAsync();
            _runtimeStartupFailed = false;
            RetryRuntimeButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            _runtimeStartupFailed = true;
            if (!_isClosed)
            {
                SetConnectionWarning("Runtime.StartFailedFormat", error.Message);
                RetryRuntimeButton.Visibility = Visibility.Visible;
            }
        }
        finally { _runtimeRetrying = false; RetryRuntimeButton.IsEnabled = !_isClosed; }
    }

    private async void OnRetryRuntimeClick(object sender, RoutedEventArgs args)
    {
        if (_isClosed || _runtimeRetrying) return;
        await BeginRuntimeStartup();
        if (!_isClosed) RefreshSettingsPresentation();
    }

    private bool _updateCheckRunning;
    private string? _updatePhase;
    private bool CanInstallUpdate()
    {
        lock (_runtimeStartupGate)
            return _runtimeInitializationTask.IsCompleted && !Volatile.Read(ref _runtimeRetrying) &&
                (Volatile.Read(ref _runtimeStartupFailed) || Volatile.Read(ref _runtime)?.SafeToRestart == true);
    }

    private async Task WaitForUpdateRestartAsync(CancellationToken cancellationToken)
    {
        // A manual startup retry replaces the task; never accept its stale failure.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task startup;
            lock (_runtimeStartupGate) startup = _runtimeInitializationTask;
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                await Task.WhenAny(startup, cancelled.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await startup.ConfigureAwait(false);
            LandingRuntime runtime;
            lock (_runtimeStartupGate)
            {
                if (!ReferenceEquals(startup, _runtimeInitializationTask)) continue;
                if (Volatile.Read(ref _runtimeStartupFailed)) return;
                runtime = Volatile.Read(ref _runtime) ?? throw new InvalidOperationException("Runtime startup did not publish a runtime.");
            }
            await runtime.WaitForSafeRestartAsync(cancellationToken).ConfigureAwait(false);
            lock (_runtimeStartupGate)
                if (ReferenceEquals(startup, _runtimeInitializationTask) && ReferenceEquals(runtime, Volatile.Read(ref _runtime))) return;
        }
    }

    private void OnUpdatePhaseChanged(string phase)
    {
        _startupTimeline?.Mark(phase);
        RuntimeDiagnosticLog.Write("runtime", "update_phase=" + phase);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed || !_updateCheckRunning) return;
            _updatePhase = phase;
            RefreshUpdatePresentation();
        }));
    }

    private async Task CheckForUpdateAsync()
    {
        if (_isClosed || _updateCheckRunning) return;
        _updateCheckRunning = true;
        try
        {
            var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
            _releaseUpdateResult = await _releaseUpdater.CheckAndInstallAsync(version, _lifetimeCancellation.Token,
                WaitForUpdateRestartAsync, CanInstallUpdate, OnUpdatePhaseChanged);
            if (_isClosed) return;
            _updatePhase = null;
            RefreshUpdatePresentation();
            if (_releaseUpdateResult.State == ReleaseUpdateState.UpdateStarted)
            { _stopReason = GuardianStopReason.Update; Close(); }
        }
        finally { _updateCheckRunning = false; _updatePhase = null; }
    }

    private void RefreshUpdatePresentation()
    {
        if (_updateCheckRunning && _updatePhase != null)
        {
            var key = _updatePhase == "update_wait_safe" ? "Update.StatusWaiting" :
                _updatePhase == "update_handoff" ? "Update.StatusInstalling" : "Update.StatusChecking";
            VersionAuthorRun.Text = _versionAuthorBase + LocalizationManager.Text(key);
            VersionAuthorText.ToolTip = _updatePhase == "update_wait_safe" ?
                LocalizationManager.Text("Update.DeferredUnsafe") : LocalizationManager.Text(key).Trim(' ', '·');
            return;
        }
        var result = _releaseUpdateResult;
        if (result == null) return;
        VersionAuthorText.ToolTip = result.Path == null ? result.Message : result.Message + "\n" + result.Path;
        VersionAuthorRun.Text = _versionAuthorBase;
        if (result.State == ReleaseUpdateState.UpdateStarted && result.Version != null)
        {
            VersionAuthorRun.Text += LocalizationManager.Format("Update.StatusUpdatingFormat", result.Version);
            VersionAuthorText.Foreground = Brush("#8FD6A8");
        }
        else if (result.State == ReleaseUpdateState.Rejected)
        {
            VersionAuthorRun.Text += LocalizationManager.Text("Update.StatusRejected");
            VersionAuthorText.Foreground = Brush("#FF8A6A");
        }
    }

    private Task<InitialState> LoadInitialStateAsync() => Task.Run(() =>
    {
        var committed = _services.Settings.Read();
        var settings = committed.Settings;
        var readable = committed.Readable;
        _startupTimeline?.Mark("settings_read");
        // Warm protected-token discovery before any UI presentation queries it.
        _ = _googleDriveTokenStore.HasRefreshToken();
        _startupTimeline?.Mark("credentials_read");
        _rawCaptureRepository.CleanupAbandonedTemporaryFiles();
        return new InitialState(settings, readable,
            TelemetryUploadClient.HasPendingUploadFiles(_rawCaptureRepository.RootPath));
    });

    private sealed class InitialState
    {
        public InitialState(ApplicationSettings settings, bool settingsReadable, bool pendingUploads)
        {
            Settings = settings;
            SettingsReadable = settingsReadable;
            PendingUploads = pendingUploads;
        }
        public ApplicationSettings Settings { get; }
        public bool SettingsReadable { get; }
        public bool PendingUploads { get; }
    }
}
