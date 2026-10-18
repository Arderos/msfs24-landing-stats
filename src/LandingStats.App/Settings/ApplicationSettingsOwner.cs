using System;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;

namespace LandingStats.App.Settings;

// One service owns committed settings and serializes local/cloud mutations.
// Notifications never participate in committing a file or a cloud transaction.
internal sealed class ApplicationSettingsOwner
{
    private readonly ApplicationSettingsRepository _repository;
    private readonly SimulatorAutoStartManager _autoStart;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private ApplicationSettings? _current;
    private bool _readable;
    public ApplicationSettingsOwner(ApplicationSettingsRepository repository, SimulatorAutoStartManager autoStart)
    { _repository = repository; _autoStart = autoStart; }
    public bool AutoStartInitialized { get; set; }
    public AutoStartOperationResult? LastAutoStartResult { get; private set; }
    public (ApplicationSettings Settings, bool Readable) Snapshot => (Volatile.Read(ref _current)?.Clone() ?? new ApplicationSettings(), _readable);
    public event Action? Changed;
    public (ApplicationSettings Settings, bool Readable) Read()
    {
        _gate.Wait();
        try { EnsureLoaded(); return (_current!.Clone(), _readable); }
        finally { _gate.Release(); }
    }
    private void EnsureLoaded()
    {
        if (_current != null) return;
        var loaded = _repository.TryLoad(out _current);
        if (!loaded) _repository.TryMarkGoogleDriveRestorePending();
        _readable = loaded && !_repository.GoogleDriveRestorePending;
    }
    public Task SaveAsync(ApplicationSettings expected, ApplicationSettings desired, Action? apply = null, Action? rollback = null)
    {
        var baseline = expected.Clone(); var target = desired.Clone();
        return Task.Run(SaveCoreAsync);
        async Task SaveCoreAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureLoaded();
                var merged = _current!.Clone();
                if (baseline.Language != target.Language) merged.Language = target.Language;
                if (baseline.StartWithSimulator != target.StartWithSimulator) merged.StartWithSimulator = target.StartWithSimulator;
                if (baseline.GoogleDrivePromptAnswered != target.GoogleDrivePromptAnswered) merged.GoogleDrivePromptAnswered = target.GoogleDrivePromptAnswered;
                if (baseline.AutomaticTelemetryUpload != target.AutomaticTelemetryUpload) merged.AutomaticTelemetryUpload = target.AutomaticTelemetryUpload;
                if (baseline.IntegrationApiEnabled != target.IntegrationApiEnabled) merged.IntegrationApiEnabled = target.IntegrationApiEnabled;
                var previousAutoStart = _current.StartWithSimulator;
                var restore = baseline.StartWithSimulator != target.StartWithSimulator && rollback != null
                    ? () => _autoStart.SetEnabled(previousAutoStart == true) : rollback;
                await SettingsPersistence.SaveAsync(_repository, merged, apply, restore).ConfigureAwait(false);
                _current = merged; _readable = !_repository.GoogleDriveRestorePending; desired.CopyFrom(merged);
            }
            finally { _gate.Release(); }
            NotifyChanged();
        }
    }
    public async Task<GoogleDriveLocalSettings> ReadCloudAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { EnsureLoaded(); return new GoogleDriveLocalSettings(Cloud(_current!), _readable); }
        finally { _gate.Release(); }
    }
    public async Task<bool> ApplyCloudAsync(GoogleDriveCloudSettings cloud, string expectedHash)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            if (!string.Equals(GoogleDriveBackupService.Hash(GoogleDriveCloudSettings.Serialize(Cloud(_current!))), expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
            var target = cloud.Clone(); target.Normalize(); var merged = _current!.Clone();
            var previousAutoStart = merged.StartWithSimulator;
            merged.Language = target.Language; merged.StartWithSimulator = target.StartWithSimulator ?? previousAutoStart;
            var updateAutoStart = AutoStartInitialized && merged.StartWithSimulator.HasValue && previousAutoStart != merged.StartWithSimulator;
            await SettingsPersistence.SaveAsync(_repository, merged,
                updateAutoStart ? () => LastAutoStartResult = _autoStart.SetEnabled(merged.StartWithSimulator!.Value) : (Action?)null,
                updateAutoStart ? () => _autoStart.SetEnabled(previousAutoStart == true) : (Action?)null).ConfigureAwait(false);
            _current = merged; _readable = !_repository.GoogleDriveRestorePending;
        }
        finally { _gate.Release(); }
        NotifyChanged(); return true;
    }
    public async Task CompleteCloudRestoreAsync(GoogleDriveSyncResult result)
    {
        if (!result.DownloadedSettings && !result.UploadedSettings) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            if (_repository.GoogleDriveRestorePending) await Task.Run(() => _repository.CompleteGoogleDriveRestore(_current!)).ConfigureAwait(false);
            _readable = true;
        }
        finally { _gate.Release(); }
        NotifyChanged();
    }
    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine("Settings projection failed after commit: " + error); }
    }
    private static GoogleDriveCloudSettings Cloud(ApplicationSettings settings) => new GoogleDriveCloudSettings
    { Language = settings.Language, StartWithSimulator = settings.StartWithSimulator };
}
