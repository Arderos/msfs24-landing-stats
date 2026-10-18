using System;
using System.Threading.Tasks;
using System.Windows;
using LandingStats.App.Settings;

namespace LandingStats.App;

public partial class MainWindow
{
    private readonly SettingsOperationQueue _settingsOperations = new SettingsOperationQueue();
    private int _settingsPendingOperations;
    private bool _autoStartPromptBusy;
    private bool _googleDrivePromptBusy;
    private string? _settingsBusyText;
    private readonly DelayedBusyIndicator _settingsBusyIndicator = new DelayedBusyIndicator();
    private readonly DelayedBusyIndicator _settingsLoadingIndicator = new DelayedBusyIndicator();

    private Task PendingSettingsOperations => _settingsOperations.Pending;
    private bool SettingsBusy => _settingsPendingOperations > 0;

    private void StopAcceptingSettingsOperations()
    {
        _settingsOperations.StopAccepting();
        RefreshSettingsOperationPresentation();
    }

    private Task<bool> RunSettingsOperationAsync(Func<Task<bool>> operation)
    {
        Dispatcher.VerifyAccess();
        if (_settingsOperations.IsStopping || _isClosed) return Task.FromResult(false);

        _settingsPendingOperations++;
        RefreshSettingsOperationPresentation();
        return _settingsOperations.RunAsync(async () =>
        {
            try
            {
                return await operation();
            }
            finally
            {
                _settingsPendingOperations--;
                if (!_isClosed) RefreshSettingsPresentation();
            }
        }, false);
    }

    private void RefreshSettingsOperationPresentation()
    {
        if (SettingsLanguageAuto == null) return;
        var enabled = _startupReady && !SettingsBusy && !_settingsOperations.IsStopping;
        SettingsLanguageAuto.IsEnabled = enabled;
        SettingsLanguageEnglish.IsEnabled = enabled;
        SettingsLanguageRussian.IsEnabled = enabled;
        SettingsTelemetryCheckBox.IsEnabled = enabled;
        SettingsIntegrationApiCheckBox.IsEnabled = enabled;
        SettingsAutoStartCheckBox.IsEnabled = enabled && _autoStartInitialized && !_autoStartPromptBusy;
        DeclineAutoStartButton.IsEnabled = enabled && !_autoStartPromptBusy;
        EnableAutoStartButton.IsEnabled = enabled && !_autoStartPromptBusy;
        DeclineGoogleDriveButton.IsEnabled = enabled && !_googleDrivePromptBusy;
        EnableGoogleDriveButton.IsEnabled = enabled && !_googleDrivePromptBusy;
        if (_settingsBusyIndicator.Update(SettingsBusy && !_isClosed, null, RefreshSettingsPresentation))
        {
            var busyText = LocalizationManager.Text("Settings.Applying");
            _settingsBusyText = busyText;
            SettingsAutomaticLanguageText.Text = busyText;
            SettingsAutoStartStateText.Text = busyText;
            SettingsFileText.Text = busyText;
            if (AutoStartPromptOverlay.Visibility == Visibility.Visible)
            {
                AutoStartPromptErrorText.Text = busyText;
                AutoStartPromptErrorText.Visibility = Visibility.Visible;
            }
            if (GoogleDrivePromptOverlay.Visibility == Visibility.Visible)
            {
                GoogleDrivePromptErrorText.Text = busyText;
                GoogleDrivePromptErrorText.Visibility = Visibility.Visible;
            }
        }
        else if (GoogleDrivePromptErrorText.Text == _settingsBusyText)
        {
            GoogleDrivePromptErrorText.Text = string.Empty;
            GoogleDrivePromptErrorText.Visibility = Visibility.Collapsed;
        }
        RefreshGoogleDrivePresentation();
    }

    private void OnCommittedSettingsChanged()
    {
        // Committed state reaches the view asynchronously. Cloud/storage never
        // waits for a window dispatcher to read or mutate durable settings.
        if (_isClosed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed) return;
            var committed = _services.Settings.Snapshot;
            var languageChanged = _settings.Language != committed.Settings.Language;
            _settings.CopyFrom(committed.Settings); _settingsPersistedAndReadable = committed.Readable;
            if (_services.Settings.LastAutoStartResult is AutoStartOperationResult autoStart)
                _autoStartProfileCount = autoStart.ConfigurationPaths.Count;
            if (_settings.StartWithSimulator.HasValue && AutoStartPromptOverlay.Visibility == Visibility.Visible) HideAutoStartPrompt();
            if (languageChanged) { LocalizationManager.Apply(_settings.Language); RefreshLocalizedPresentation(); }
            else RefreshSettingsPresentation();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
}
