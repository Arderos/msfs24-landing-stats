using System.Threading.Tasks;
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
    private Task _runwayPersistenceTask = Task.CompletedTask;

    private Task<InitialState> LoadInitialStateAsync() => Task.Run(() =>
    {
        var loaded = _settingsRepository.TryLoad(out var settings);
        if (!loaded) _settingsRepository.TryMarkGoogleDriveRestorePending();
        var readable = loaded && !_settingsRepository.GoogleDriveRestorePending;
        // Warm protected-token discovery before any UI presentation queries it.
        _ = _googleOAuthClient.IsSignedIn;
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
