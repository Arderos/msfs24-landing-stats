using System;
using System.Threading;
using System.Windows;
using LandingStats.App.Settings;

namespace LandingStats.App;

public partial class MainWindow
{
    private bool _changingIntegrationApiSelection;
    private string? _integrationApiSettingsError;

    private void RefreshIntegrationApiSettingsPresentation()
    {
        _changingIntegrationApiSelection = true;
        try { SettingsIntegrationApiCheckBox.IsChecked = _settings.IntegrationApiEnabled; }
        finally { _changingIntegrationApiSelection = false; }
        SettingsIntegrationApiErrorText.Text = _integrationApiSettingsError == null ? string.Empty :
            LocalizationManager.Format("Settings.IntegrationApiErrorFormat", _integrationApiSettingsError);
        SettingsIntegrationApiErrorText.Visibility = _integrationApiSettingsError == null
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnIntegrationApiSelectionChanged(object sender, RoutedEventArgs args)
    {
        if (_changingIntegrationApiSelection || !_startupReady || SettingsBusy || _settingsOperations.IsStopping) return;
        var enabled = SettingsIntegrationApiCheckBox.IsChecked == true;
        if (enabled == _settings.IntegrationApiEnabled) return;
        _integrationApiSettingsError = null;
        await RunSettingsOperationAsync(async () =>
        {
            try
            {
                await _runtimeInitializationTask;
                var runtime = Volatile.Read(ref _runtime) ?? throw new InvalidOperationException("Runtime is not available.");
                var previous = _services.Settings.Snapshot.Settings;
                var desired = previous.Clone();
                desired.IntegrationApiEnabled = enabled;
                // SettingsOwner invokes these callbacks on a background worker.
                // A failed save restores both the persisted preference and the live API.
                await _services.Settings.SaveAsync(previous, desired,
                    () => runtime.SetIntegrationApiEnabledAsync(enabled).GetAwaiter().GetResult(),
                    () => runtime.SetIntegrationApiEnabledAsync(previous.IntegrationApiEnabled).GetAwaiter().GetResult());
                _settings.CopyFrom(_services.Settings.Snapshot.Settings);
                return true;
            }
            catch (Exception error)
            {
                _integrationApiSettingsError = error.Message;
                return false;
            }
        });
    }
}
