using System;
using System.Threading;
using System.Windows;
using LandingStats.App.Settings;

namespace LandingStats.App;

public partial class MainWindow
{
    // Stay disabled until persisted settings are loaded (including a previous opt-out).
    private int _automaticTelemetryEnabled;
    private bool _changingTelemetrySelection;
    private string? _telemetrySettingsError;
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
        RefreshBugReportButton();
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
