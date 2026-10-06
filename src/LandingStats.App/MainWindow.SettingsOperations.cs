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

    private Task<bool> CompleteGoogleDriveSettingsRestoreAsync() => RunSettingsOperationAsync(async () =>
    {
        var snapshot = _settings.Clone();
        await Task.Run(() =>
        {
            if (_settingsRepository.GoogleDriveRestorePending)
                _settingsRepository.CompleteGoogleDriveRestore(snapshot);
        });
        _settingsPersistedAndReadable = true;
        return true;
    });
}
