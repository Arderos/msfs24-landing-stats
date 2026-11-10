using System;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using System.Windows;
using LandingStats.App.Settings;

namespace LandingStats.App;

public partial class MainWindow
{
    // Stay disabled until persisted settings are loaded (including a previous opt-out).
    private int _automaticTelemetryEnabled;
    private bool _changingTelemetrySelection;
    private string? _telemetrySettingsError;
    private string _telemetryDeliveryKey = "Delivery.Idle";
    private bool _telemetryDeliveryAutomatic;
    private bool _telemetryIdentityUnavailable;
    private string? _telemetryRecoveryKey;
    private string? _telemetryRecoveryError;
    private CancellationTokenSource? _telemetryRecoveryCancellation;
    private bool AutomaticTelemetryUploadEnabled => Volatile.Read(ref _automaticTelemetryEnabled) != 0;

    private void ApplyTelemetryPreference(bool enabled)
    {
        lock (_runtimePolicyPublication)
        {
            Volatile.Write(ref _automaticTelemetryEnabled, enabled ? 1 : 0);
            var runtime = Volatile.Read(ref _runtime);
            if (runtime != null)
            {
                var applying = ApplyRuntimePolicyAsync(runtime, enabled);
                _runtimePolicyTask = _runtimePolicyTask.IsCompleted ? applying : System.Threading.Tasks.Task.WhenAll(_runtimePolicyTask, applying);
            }
        }
        if (!_isClosed) RefreshBugReportButton();
    }

    private void RefreshTelemetrySettingsPresentation()
    {
        _changingTelemetrySelection = true;
        try { SettingsTelemetryCheckBox.IsChecked = _settings.AutomaticTelemetryUpload; }
        finally { _changingTelemetrySelection = false; }
        RefreshDeliverySettingsPresentation();
        RefreshBugReportButton();
    }

    private void ApplyDeliveryPresentation(RuntimePresentationSnapshot snapshot)
    {
        var previousFooterKey = _telemetryDeliveryKey == "Delivery.Delivered" ? "Telemetry.UploadedFormat" : _telemetryDeliveryKey;
        _telemetryDeliveryAutomatic = snapshot.UploadAutomatic;
        _telemetryIdentityUnavailable = IdentityUnavailable(snapshot);
        _telemetryDeliveryKey = snapshot.DeliveryFailure != null
            ? "Delivery.StoragePending" : snapshot.DeliveryQuarantined ? "Delivery.Damaged"
            : snapshot.UploadState == OutboxState.Delivered ? "Delivery.Delivered"
            : snapshot.UploadState == OutboxState.QuarantinePending || snapshot.UploadState == OutboxState.Quarantined ? "Delivery.Rejected"
            : snapshot.UploadState.HasValue ? DeliveryReasonKey(snapshot.UploadReason) : "Delivery.Idle";
        if (_telemetryIdentityUnavailable) _telemetryDeliveryKey = "Delivery.IdentityUnavailable";
        // The footer describes the report beside its manual-send button. The
        // independent queue's current status always remains visible in Settings.
        if (snapshot.Failure == null && _runtimeReportEpisode == Guid.Empty && (_telemetryIdentityUnavailable || snapshot.DeliveryFailure != null || snapshot.DeliveryQuarantined || snapshot.UploadState.HasValue))
        {
            if (_telemetryDeliveryKey == "Delivery.Delivered") SetUploadStatus("Telemetry.UploadedFormat", snapshot.Upload.ToString("N"));
            else SetUploadStatus(_telemetryDeliveryKey);
        }
        else if (snapshot.Failure == null && _runtimeReportEpisode == Guid.Empty && _uploadStatusKey == previousFooterKey)
        {
            // Retire the delivery notice only while it still owns the footer.
            // A newer manual-send or runtime failure message keeps its status.
            SetUploadStatus("Footer.ReportWaiting");
        }
        RefreshDeliverySettingsPresentation();
        RefreshSettingsOperationPresentation();
    }

    private static bool IdentityUnavailable(RuntimePresentationSnapshot snapshot) => snapshot.UploadIdentityUnavailable;

    private static string DeliveryReasonKey(string reason) => reason switch
    {
        "Telemetry.RegistrationClosed" => "Delivery.RegistrationClosed",
        "Telemetry.RegistrationRejected" => "Delivery.RegistrationRejected",
        "HttpRequestException" or "OperationCanceledException" => "Delivery.NetworkPending",
        "UnauthorizedAccessException" => "Delivery.StoragePending",
        "InvalidDataException" or "SerializationException" => "Delivery.DataPending",
        _ => "Delivery.Pending",
    };

    private void RefreshDeliverySettingsPresentation()
    {
        SettingsTelemetryStatusText.Text = LocalizationManager.Text(_telemetryDeliveryAutomatic && !AutomaticTelemetryUploadEnabled &&
            (_telemetryDeliveryKey == "Delivery.Pending" || _telemetryDeliveryKey == "Delivery.NetworkPending") ? "Delivery.Paused" : _telemetryDeliveryKey);
        RecoverTelemetryIdentityButton.Visibility = _telemetryIdentityUnavailable ? Visibility.Visible : Visibility.Collapsed;
        CancelTelemetryRecoveryButton.Visibility = _telemetryRecoveryCancellation == null ? Visibility.Collapsed : Visibility.Visible;
        CancelTelemetryRecoveryButton.IsEnabled = _telemetryRecoveryCancellation != null && !_telemetryRecoveryCancellation.IsCancellationRequested && !_isClosed;
        SettingsTelemetryRecoveryText.Visibility = _telemetryRecoveryKey == null ? Visibility.Collapsed : Visibility.Visible;
        if (_telemetryRecoveryKey != null)
            SettingsTelemetryRecoveryText.Text = LocalizationManager.Format(_telemetryRecoveryKey, _telemetryRecoveryError ?? "");
    }

    private async void OnRecoverTelemetryIdentityClick(object sender, RoutedEventArgs args) => await RecoverTelemetryIdentityAsync(() =>
        MessageBox.Show(this, LocalizationManager.Text("Settings.TelemetryRecoverConfirm"),
            LocalizationManager.Text("Settings.TelemetryRecover"), MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes);

    private void OnCancelTelemetryRecoveryClick(object sender, RoutedEventArgs args)
    {
        _telemetryRecoveryCancellation?.Cancel();
        RefreshDeliverySettingsPresentation();
    }

    // The confirmation boundary is shared by the real button and integration tests.
    internal async Task<bool> RecoverTelemetryIdentityAsync(Func<bool> confirm)
    {
        if (!_startupReady || SettingsBusy || _settingsOperations.IsStopping || _isClosed) return false;
        var runtime = Volatile.Read(ref _runtime);
        if (runtime == null) return false;
        var snapshot = runtime.Snapshot;
        if (!IdentityUnavailable(snapshot))
        {
            _telemetryRecoveryKey = "Settings.TelemetryRecoveryNotNeeded"; _telemetryRecoveryError = null;
            ApplyDeliveryPresentation(snapshot);
            return false;
        }
        if (!confirm()) return false;
        _telemetryRecoveryKey = null; _telemetryRecoveryError = null;
        return await RunSettingsOperationAsync(async () =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            _telemetryRecoveryCancellation = cancellation;
            RefreshDeliverySettingsPresentation();
            try
            {
                var result = await runtime.RecoverUnavailableTelemetryIdentityAsync(true, cancellation.Token);
                _telemetryRecoveryKey = result.StatusRefreshFailure != null ? "Settings.TelemetryRecoveredPending"
                    : result.Replaced || result.Created ? "Settings.TelemetryRecovered" : "Settings.TelemetryAlreadyAvailable";
                // Identity replacement has its own result. Queue quarantine and
                // other storage failures still belong to the runtime snapshot.
                ApplyDeliveryPresentation(runtime.Snapshot);
                return true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                _telemetryRecoveryKey = "Settings.TelemetryRecoveryCancelled";
                return false;
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("Telemetry identity recovery: {0}", error);
                _telemetryRecoveryKey = "Settings.TelemetryRecoveryFailedFormat";
                _telemetryRecoveryError = error.Message;
                return false;
            }
            finally { _telemetryRecoveryCancellation = null; if (!_isClosed) RefreshDeliverySettingsPresentation(); }
        });
    }

    private async void OnTelemetrySelectionChanged(object sender, RoutedEventArgs args)
    {
        if (_changingTelemetrySelection || !_startupReady || SettingsBusy || _settingsOperations.IsStopping) return;
        var enabled = SettingsTelemetryCheckBox.IsChecked == true;
        if (enabled == _settings.AutomaticTelemetryUpload) return;
        _telemetrySettingsError = null;
        SettingsTelemetryErrorText.Visibility = Visibility.Collapsed;
        // Opt-out takes effect before disk I/O. Opt-in starts only after durable save.
        if (!enabled) ApplyTelemetryPreference(false);
        string? error = null;
        await RunSettingsOperationAsync(async () =>
        {
            var snapshot = _settings.Clone();
            snapshot.AutomaticTelemetryUpload = enabled;
            try
            {
                await _services.Settings.SaveAsync(_settings, snapshot);
                _settings.CopyFrom(_services.Settings.Snapshot.Settings);
            }
            catch (Exception exception) { error = exception.Message; }
            ApplyTelemetryPreference(_settings.AutomaticTelemetryUpload);
            // Keep the existing settings "Applying" state until the active
            // automatic sender has actually retired, including after save failure.
            await _runtimePolicyTask;
            return error == null;
        });
        if (_isClosed) return;
        if (error != null)
        {
            _telemetrySettingsError = error;
            SettingsTelemetryErrorText.Text = LocalizationManager.Format("Settings.SaveErrorFormat", error);
            SettingsTelemetryErrorText.Visibility = Visibility.Visible;
        }
        if (AutomaticTelemetryUploadEnabled) StartPendingBugReportRetry();
    }

}
