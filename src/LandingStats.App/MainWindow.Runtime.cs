using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Settings;

namespace LandingStats.App;

public partial class MainWindow
{
    private LandingRuntime? _runtime;
    private Task _runtimeStartupTask = Task.CompletedTask;
    private Task _runtimePolicyTask = Task.CompletedTask;
    private readonly object _runtimePolicyPublication = new object();
    private readonly Dictionary<Guid, bool> _runtimePresentedEpisodes = new Dictionary<Guid, bool>();
    private Guid _runtimeActiveEpisode, _runtimeReportEpisode, _runtimeSubmittedEpisode;
    private RecorderStatusEventArgs? _runtimeStatus;
    private bool _runtimeObservedSimulatorExit;
    private long _runtimeCloudRevision, _runtimeCloudHistoryRevision;
    private CloudSyncPhase _runtimeCloudPhase;
    private long _runtimeFailureRevision;
    private IReadOnlyList<AirportFacility> _runtimeKnownAirports = Array.Empty<AirportFacility>();
    internal GuardianStatus ReadGuardianHealth(long uiQpc)
    {
        var runtime = Volatile.Read(ref _runtime);
        var uiStalled = uiQpc == 0 || System.Diagnostics.Stopwatch.GetTimestamp() - uiQpc >= System.Diagnostics.Stopwatch.Frequency * GuardianStatus.DefaultUiStallSeconds;
        var failures = App.RecoveryRequests.Where(pair => runtime?.NeedsRecovery(pair.Key, pair.Value) != false).Select(pair => pair.Key).ToArray();
        var failure = failures.FirstOrDefault(component => runtime?.IsPresentationDependency(component, uiStalled) != true) ?? failures.FirstOrDefault();
        var reason = failure == null ? GuardianRecovery.None : runtime?.IsPresentationDependency(failure, uiStalled) == true ? GuardianRecovery.Presentation : GuardianRecovery.Runtime;
        return new GuardianStatus(_runtimeStartupFailed || runtime?.Ready == true,
            _runtimeStartupFailed ? System.Diagnostics.Stopwatch.GetTimestamp() : runtime?.ReadObservationQpc(uiStalled) ?? 0,
            uiQpc, _runtimeStartupFailed || runtime?.SafeToRestart == true, reason, failure ?? "", App.ShutdownProgressQpc);
    }
    internal async Task<bool> PrepareGuardianRecoveryAsync()
    {
        var runtime = Volatile.Read(ref _runtime);
        if (runtime?.SafeToRestart != true || Volatile.Read(ref _isClosed)) return false;
        await runtime.StopAsync().ConfigureAwait(false); return true;
    }

    private Task InitializeRuntimeAsync() => InitializeRuntimeAsync(
        () => _services.CreateRuntime(ApplyRuntimeSnapshotAsync, App.RequestRuntimeRecovery));

    internal async Task InitializeRuntimeAsync(Func<LandingRuntime> createRuntime)
    {
        _runtimeStartupTask = Task.Run(async () =>
        {
            _startupTimeline?.Mark("runtime_prepare_begin");
            var runtime = createRuntime();
            _startupTimeline?.Mark("runtime_constructed");
            try
            {
                Task start;
                lock (_runtimePolicyPublication)
                {
                    Volatile.Write(ref _runtime, runtime);
                    if (Volatile.Read(ref _isClosed)) return;
                    var automatic = AutomaticTelemetryUploadEnabled;
                    // Publication and synchronous startup-policy registration
                    // form one boundary with preference changes. No await or I/O
                    // is performed while holding this window gate.
                    // Upload consent covers landing reports. Continuous RAW capture
                    // is a separate diagnostic operation and stays off at startup.
                    start = runtime.StartAsync(automatic, cancellationToken: _lifetimeCancellation.Token);
                }
                await start.ConfigureAwait(false);
                _startupTimeline?.Mark("runtime_ready");
                RuntimeDiagnosticLog.Write("runtime", "runtime_ready");
                if (_services.Settings.Snapshot.Settings.IntegrationApiEnabled)
                    await runtime.SetIntegrationApiEnabledAsync(true).ConfigureAwait(false);
            }
            catch (Exception error) { RuntimeDiagnosticLog.Write("runtime", "runtime_start_failed", error); await runtime.StopAsync().ConfigureAwait(false); throw; }
            finally { if (Volatile.Read(ref _isClosed)) await runtime.StopAsync().ConfigureAwait(false); }
        });
        await _runtimeStartupTask;
    }
    private async Task StopRuntimeAsync()
    {
        try { await _runtimeStartupTask.ConfigureAwait(false); } catch { /* Startup already reports its own failure. */ }
        var runtime = Volatile.Read(ref _runtime);
        if (runtime != null) await runtime.StopAsync().ConfigureAwait(false);
    }
    private async Task ApplyRuntimeSnapshotAsync(RuntimePresentationSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isClosed)) return;
        var changed = snapshot.Episodes.Where(value => !_runtimePresentedEpisodes.TryGetValue(value.Episode, out var ready) || ready != value.ReportReady).ToArray();
        LandingRecord[]? stored = null;
        var cloudChanged = snapshot.Cloud != null && snapshot.Cloud.Revision != _runtimeCloudRevision;
        var cloudHistoryChanged = snapshot.Cloud != null && snapshot.Cloud.HistoryRevision != _runtimeCloudHistoryRevision;
        var episodeHistoryChanged = changed.Any(value => !value.DiagnosticOnly && value.RecordCount > 0);
        var changedHistoryIds = new HashSet<string>(changed.Where(value => !value.DiagnosticOnly && value.RecordCount > 0)
            .SelectMany(value => value.RecordIds), StringComparer.Ordinal);
        var finalMetadataIds = new HashSet<string>(changed.Where(value => !value.DiagnosticOnly && value.RecordCount > 0 && value.ReportReady &&
            _runtimePresentedEpisodes.ContainsKey(value.Episode)).SelectMany(value => value.RecordIds), StringComparer.Ordinal);
        var knownAirports = Volatile.Read(ref _runtime)?.KnownAirports ?? Array.Empty<AirportFacility>();
        var airportsChanged = !ReferenceEquals(knownAirports, _runtimeKnownAirports);
        if (episodeHistoryChanged || cloudHistoryChanged || airportsChanged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_landingRepositoryGate) stored = _repository.LoadAll().Select(LandingRepository.CreateSummary).ToArray();
        }
        // Cancellation can abandon a queued presentation. It never abandons a
        // storage operation or recording effect, which have already committed.
        await Dispatcher.InvokeAsync(() =>
        {
            if (_isClosed || _historyStopped) return;
            var status = snapshot.Status;
            var captureSuppressed = status?.MessageKey == "Recorder.ReplayActive" || status?.MessageKey == "Recorder.ReplayChecking";
            var activeEpisodeChanged = _runtimeActiveEpisode != snapshot.ActiveEpisode;
            var sourceStatusChanged = status != null && (_runtimeStatus == null || status.State != _runtimeStatus.State || status.MessageKey != _runtimeStatus.MessageKey ||
                !status.MessageArguments.SequenceEqual(_runtimeStatus.MessageArguments));
            // A previous episode's saved message overrides the raw status. If
            // capture transitions were coalesced, that same raw status must
            // still replace the old success at the new episode boundary.
            if (status != null && (activeEpisodeChanged || sourceStatusChanged))
            { OnRecorderStatusChanged(status); _runtimeStatus = status; }
            if (snapshot.Airborne == true) HideReplayUnsupportedOverlay();
            if (activeEpisodeChanged)
            {
                _runtimeActiveEpisode = snapshot.ActiveEpisode; _runtimeReportEpisode = Guid.Empty; _runtimeSubmittedEpisode = Guid.Empty;
                SetUploadStatus("Footer.ReportWaiting");
                RecordPresentationPhase(snapshot.ActiveEpisode, EpisodeTimelinePhase.UiEpisodeChanged);
            }
            if (stored != null)
            {
                if (!cloudHistoryChanged && snapshot.Episodes.Count < RuntimePresentation.EpisodeNoticeCapacity)
                {
                    // Finalizing B must not replace A's equivalent geometry
                    // references and rebuild the replay the user is watching.
                    // This also applies when C's first history notice coalesces
                    // with B's final metadata in the same snapshot.
                    // At capacity an evicted notice may have changed an older
                    // record, so retain the complete fresh storage snapshot.
                    var previous = _landings.ToDictionary(record => record.Id, StringComparer.Ordinal);
                    foreach (var record in stored)
                        if (!changedHistoryIds.Contains(record.Id) && previous.TryGetValue(record.Id, out var old))
                        { old.Airport = record.Airport; old.AirportDistanceNauticalMiles = record.AirportDistanceNauticalMiles; }
                    stored = stored.Select(record => !changedHistoryIds.Contains(record.Id) && previous.TryGetValue(record.Id, out var unchanged)
                        ? unchanged : record).ToArray();
                }
                // Rebuild from all committed summaries: coalescing 32 notices
                // must not drop older landings while the dispatcher was stalled.
                _landings = stored.Where(record => !_locallyRemovedHistoryIds.Contains(record.Id)).ToArray();
                _historyLoadVersion++; _historyLoading = false; _historyLoadError = null;
                if (cloudHistoryChanged) InvalidateLoadedHistoryDetails();
                else
                {
                    foreach (var summary in stored)
                        if (_loadedDetails.TryGetValue(summary.Id, out var detail))
                        { detail.Airport = summary.Airport; detail.AirportDistanceNauticalMiles = summary.AirportDistanceNauticalMiles; }
                    // Finalization changes scenery metadata, not telemetry.
                    // Keep prepared replay objects and the user's view intact.
                    foreach (var summary in stored.Where(record => finalMetadataIds.Contains(record.Id)))
                        if (_loadedDetails.TryGetValue(summary.Id, out var detail)) CopyRunwayMetadata(summary, detail);
                }
                StoragePathText.Text = "%LOCALAPPDATA%\\MSFS Landing Stats\\Landings";
                StoragePathText.ToolTip = _repository.RootPath;
                _airportFacilities = MergeAirportFacilities(_airportFacilities, Volatile.Read(ref _runtime)?.KnownAirports ?? Array.Empty<AirportFacility>());
                ApplySessionFilter((LandingHistoryList.SelectedItem as LandingRecord)?.Id);
                _runtimeKnownAirports = knownAirports;
                RecordPresentationPhase(snapshot.ActiveEpisode, EpisodeTimelinePhase.UiHistoryApplied, LandingHistoryList.Items.Count);
                // Runtime triggers backup on committed report changes; a cloud
                // refresh must not recursively schedule another identical sync.
            }
            if (status?.State == RecorderState.Connected && !captureSuppressed && snapshot.AnalysisPending)
            {
                if (snapshot.AnalysisFailed) SetConnectionWarning("Recorder.AnalysisIncomplete");
                else
                {
                    if (_connectionStatusKey != "Recorder.AnalyzingLanding")
                        RecordPresentationPhase(snapshot.ActiveEpisode, EpisodeTimelinePhase.UiAnalyzing);
                    SetConnectionStatus("Recorder.AnalyzingLanding");
                }
            }
            foreach (var episode in snapshot.Episodes.Where(value => status?.State == RecorderState.Connected && !captureSuppressed && !snapshot.AnalysisPending &&
                !value.DiagnosticOnly && value.Episode == snapshot.ActiveEpisode &&
                (activeEpisodeChanged || sourceStatusChanged || changed.Any(item => item.Episode == value.Episode))))
            {
                if (episode.ReplayLike) { SetConnectionWarning("Recorder.ReplayIgnored"); ShowReplayUnsupportedOverlay(); }
                else if (episode.RecordCount == 0) SetConnectionWarning("Recorder.NoTouchdown");
                else
                {
                    SetConnectionStatus(episode.RecordCount == 1 ? "Recorder.LandingSaved" : "Recorder.ContactsSavedFormat", episode.RecordCount);
                    RecordPresentationPhase(snapshot.ActiveEpisode, EpisodeTimelinePhase.UiSaved, episode.RecordCount);
                }
            }
            var report = snapshot.Episodes.LastOrDefault(value => value.Episode == snapshot.ActiveEpisode && value.ReportReady && !value.ReplayLike && !value.DiagnosticOnly && value.RecordCount != 0);
            if (report != null && _runtimeSubmittedEpisode != report.Episode)
            { _runtimeReportEpisode = report.Episode; SetUploadStatus("Footer.ReportReady"); }
            else if (snapshot.UploadState.HasValue && _runtimeReportEpisode == Guid.Empty)
            {
                if (snapshot.UploadState.Value == OutboxState.Delivered) SetUploadStatus("Telemetry.UploadedFormat", snapshot.Upload.ToString("N"));
                else if (snapshot.UploadState.Value == OutboxState.Pending || snapshot.UploadState.Value == OutboxState.Revoked) SetUploadStatus("BugReport.Queued");
            }
            if (snapshot.Failure != null && snapshot.Failure.Revision > _runtimeFailureRevision)
            {
                _runtimeFailureRevision = snapshot.Failure.Revision;
                SetUploadError("Runtime.ProcessingFailedFormat", snapshot.Failure.Entity.ToString("N"), snapshot.Failure.Message);
            }
            RefreshBugReportButton();
            if (snapshot.Cloud != null) _runtimeCloudHistoryRevision = snapshot.Cloud.HistoryRevision;
            if (cloudChanged)
            {
                ApplyCloudNotice(snapshot.Cloud!);
            }
            if (snapshot.SimulatorExited && !_runtimeObservedSimulatorExit)
            {
                _runtimeObservedSimulatorExit = true;
                if (Environment.GetEnvironmentVariable(App.SimulatorAutoStartEnvironmentVariable) == "1") OnSimulatorExited();
            }
        }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
        _runtimePresentedEpisodes.Clear();
        foreach (var episode in snapshot.Episodes) _runtimePresentedEpisodes[episode.Episode] = episode.ReportReady;
    }
    private void ApplyCloudNotice(RuntimeCloudNotice notice)
    {
        _runtimeCloudRevision = notice.Revision; _runtimeCloudHistoryRevision = notice.HistoryRevision;
        _runtimeCloudPhase = notice.Phase;
        if (notice.CompletedUtc != DateTime.MinValue && _googleDriveTokenStore.HasRefreshToken())
            _lastGoogleDriveSyncUtc = notice.CompletedUtc;
        _googleDriveWarningSummary = notice.Summary;
        _googleDriveWarningDetail = CloudWarnings(_googleDriveWarningSummary);
        // Recovery must clear both the rendered text and its localization key;
        // otherwise a language refresh can resurrect an obsolete auth error.
        _googleDriveErrorKey = notice.Phase == CloudSyncPhase.AuthorizationRequired ? "Settings.DriveSignInAgain" : null;
        _googleDriveErrorDetail = _googleDriveErrorKey != null ? LocalizationManager.Text(_googleDriveErrorKey) :
            notice.Phase == CloudSyncPhase.Retrying ? notice.Error : null;
        RefreshGoogleDrivePresentation();
    }
    private static string? CloudWarnings(CloudSyncSummary? summary)
    {
        if (summary == null) return null;
        var warnings = new List<string>();
        if (summary.FailedLandingIds.Length > 0) warnings.Add(LocalizationManager.Format("Settings.DriveFailedLandingsFormat", summary.FailedLandingIds.Length, string.Join(", ", summary.FailedLandingIds.Take(8))));
        if (summary.LandingConflicts > 0) warnings.Add(LocalizationManager.Format("Settings.DriveLandingConflictFormat", summary.LandingConflicts));
        if (summary.SettingsConflict) warnings.Add(LocalizationManager.Text("Settings.DriveSettingsConflict"));
        if (summary.SettingsChanged) warnings.Add(LocalizationManager.Text("Settings.DriveSettingsChangedDuringSync"));
        if (summary.UnsupportedSettingsVersion.HasValue) warnings.Add(LocalizationManager.Format("Settings.DriveUnsupportedSettingsFormat", summary.UnsupportedSettingsVersion.Value));
        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }
    private async Task SubmitRuntimeReportAsync(LandingRuntime runtime)
    {
        var episode = _runtimeReportEpisode;
        if (episode == Guid.Empty || _bugReportSubmitting) return;
        _bugReportSubmitting = true; RefreshBugReportButton(); SetUploadStatus("BugReport.Preparing");
        try
        {
            Task<Guid> accepted;
            lock (_bugReportPersistenceGate)
            {
                accepted = runtime.RequestManualReportAsync(episode);
                _bugReportPersistenceTask = Task.WhenAll(_bugReportPersistenceTask, accepted);
            }
            await accepted;
            if (_runtimeActiveEpisode == episode)
            { _runtimeSubmittedEpisode = episode; _runtimeReportEpisode = Guid.Empty; SetUploadStatus("BugReport.Queued"); }
        }
        catch (Exception error) { if (!_isClosed) SetUploadError("BugReport.FailedFormat", error.Message); }
        finally { _bugReportSubmitting = false; RefreshBugReportButton(); }
    }
    private async Task ApplyRuntimePolicyAsync(LandingRuntime runtime, bool enabled)
    {
        try { await runtime.SetAutomaticDeliveryAsync(enabled).ConfigureAwait(false); }
        catch (Exception error)
        {
            if (Volatile.Read(ref _isClosed)) return;
            System.Diagnostics.Debug.WriteLine(error);
            await Dispatcher.InvokeAsync(() => { if (!_isClosed) SetUploadError("Settings.TelemetryPolicyErrorFormat", error.Message); }).Task.ConfigureAwait(false);
        }
    }
}
