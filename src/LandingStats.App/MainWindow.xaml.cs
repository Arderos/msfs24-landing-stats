using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using LandingStats.App.Controls;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.TelemetryUpload;
using LandingStats.App.Updates;
using LandingStats.Core;

namespace LandingStats.App;

public partial class MainWindow : Window
{
    private readonly ApplicationSettingsRepository _settingsRepository;
    private ApplicationSettings _settings = new ApplicationSettings();
    private readonly SimulatorAutoStartManager _autoStartManager = new SimulatorAutoStartManager();
    private readonly LandingRepository _repository = new LandingRepository();
    private readonly RawCaptureRepository _rawCaptureRepository = new RawCaptureRepository(null, false);
    private readonly BugReportRepository _bugReportRepository;
    private readonly LastLandingBugReportBuffer _lastLandingBugReport = new LastLandingBugReportBuffer();
    private readonly AirportFacilityRepository _airportFacilityRepository = new AirportFacilityRepository();
    private readonly FlightModelGeometryResolver _flightModelGeometryResolver;
    private readonly ReleaseUpdater _releaseUpdater = new ReleaseUpdater();
    private readonly GoogleOAuthTokenStore _googleDriveTokenStore = new GoogleOAuthTokenStore();
    private readonly GoogleOAuthClient _googleOAuthClient;
    private readonly GoogleDriveApiClient _googleDriveApiClient;
    private readonly GoogleDriveBackupService _googleDriveBackupService;
    private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
    private CancellationTokenSource? _googleDriveAuthorizationCancellation;
    private readonly object _episodeProcessingGate = new object();
    private readonly object _landingRepositoryGate = new object();
    private readonly object _airportFacilityRepositoryGate = new object();
    private readonly object _airportMetadataPersistenceGate = new object();
    private readonly object _telemetryUploadClientGate = new object();
    private readonly object _bugReportRetryGate = new object();
    private readonly object _bugReportPersistenceGate = new object();
    private Task _episodePersistenceTask = Task.CompletedTask;
    private Task _airportMetadataPersistenceTask = Task.CompletedTask;
    private Task _bugReportRetryTask = Task.CompletedTask;
    private bool _bugReportRetryRunning;
    private bool _bugReportRetryRequested;
    private Task _bugReportPersistenceTask = Task.CompletedTask;
    private Task _googleDriveSyncTask = Task.CompletedTask;
    private Task _googleDriveDeleteTask = Task.CompletedTask;
    private TelemetryUploadClient? _telemetryUploadClient;
    private IReadOnlyList<LandingRecord> _landings = Array.Empty<LandingRecord>();
    private IReadOnlyList<AirportFacility> _airportFacilities = Array.Empty<AirportFacility>();
    private readonly Dictionary<string, LandingRecord> _loadedDetails = new Dictionary<string, LandingRecord>(StringComparer.Ordinal);
    private long _runwayRecordsEpisodeId;
    private IReadOnlyList<LandingRecord> _runwayRecords = Array.Empty<LandingRecord>();
    private HwndSource? _messageSource;
    private SimConnectLandingRecorder? _recorder;
    private LandingChart[] _charts = Array.Empty<LandingChart>();
    private bool _showFullApproach;
    private bool _showReplay;
    private int _primaryGearSeriesIndex;
    private int? _mainIsolatedSeriesIndex;
    private bool _changingLanguageSelection;
    private bool _changingAutoStartSelection;
    private bool _settingsPersistedAndReadable;
    private bool _autoStartInitialized;
    private bool _googleDriveAuthorizing;
    private bool _googleDriveAuthorizationCanceledByUser;
    private int _googleDrivePendingOperations;
    private bool _bugReportSubmitting;
    private bool _deleteLandingInProgress;
    private LandingRecord? _pendingDeleteRecord;
    private double? _zoomStartSeconds;
    private double? _zoomEndSeconds;
    private RecorderState _lastRecorderState = RecorderState.Waiting;
    private string _connectionStatusKey = "Top.Waiting";
    private object[] _connectionStatusArguments = Array.Empty<object>();
    private bool _connectionStatusIsWarning;
    private string _uploadStatusKey = "Footer.ReportWaiting";
    private object[] _uploadStatusArguments = Array.Empty<object>();
    private bool _uploadStatusIsError;
    private int _autoStartProfileCount;
    private string? _autoStartLegacyCleanupError;
    private string? _autoStartErrorDetail;
    private DateTime? _lastGoogleDriveSyncUtc;
    private string? _googleDriveErrorDetail;
    private string? _googleDriveWarningDetail;
    private bool _isClosed;

    private bool GoogleDriveBusy => _googleDriveAuthorizing || _googleDrivePendingOperations > 0;

    public MainWindow() : this(null)
    {
    }

    internal MainWindow(FlightModelGeometryResolver? geometryResolver)
    {
        _flightModelGeometryResolver = geometryResolver ?? new FlightModelGeometryResolver();
        _bugReportRepository = new BugReportRepository(_rawCaptureRepository.RootPath);
        _settingsRepository = new ApplicationSettingsRepository();
        _googleOAuthClient = new GoogleOAuthClient(_googleDriveTokenStore);
        _googleDriveApiClient = new GoogleDriveApiClient(_googleOAuthClient);
        _googleDriveBackupService = new GoogleDriveBackupService(
            _googleDriveApiClient,
            _repository,
            _landingRepositoryGate,
            new GoogleDriveSyncStateRepository(),
            ReadCloudSettingsAsync,
            ApplyCloudSettingsAsync);
        LocalizationManager.Apply(_settings.Language);
        InitializeComponent();
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetName().Version;
        var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Evgeniy Zaytsev";
        VersionAuthorRun.Text = $"v{version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0} · {company}";
        _charts = new[] { MainChart, MiniGChart, MiniPowerChart, MiniGearChart };
        ReplayView.TimeChanged += OnChartHoverTimeChanged;
        foreach (var chart in _charts)
        {
            chart.HoverTimeChanged += OnChartHoverTimeChanged;
            chart.ZoomRangeSelected += OnChartZoomRangeSelected;
        }

        VerticalRateTab.IsChecked = true;
        MainChart.Mode = LandingChartMode.VerticalSpeed;
        _mainIsolatedSeriesIndex = null;
        MainChart.SetIsolatedSeries(null);
        MainChartDescription.Text = LocalizationManager.Text("Chart.DescriptionVertical");
        ModeUnitText.Text = "fpm";
        UpdateMainLegend(LandingChartMode.VerticalSpeed);
        RefreshHistoryLoadingPresentation();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_initializationStarted) return;
        _initializationStarted = true;
        _initializationTask = InitializeWindowAsync();
    }

    private async Task InitializeWindowAsync()
    {
        try { await InitializeWindowCoreAsync(); }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                SetConnectionWarning("Recorder.AnalysisFailedFormat", exception.Message);
                if (!_startupReady)
                {
                    _historyLoading = false;
                    _historyLoadError = exception.Message;
                    RefreshHistoryLoadingPresentation();
                }
            }
        }
    }

    private async Task InitializeWindowCoreAsync()
    {
        ReleaseUpdater.BeginCompletedUpdateCleanup(Environment.GetCommandLineArgs());
        var initialState = LoadInitialStateAsync();
        _initializationPersistenceTask = initialState;
        var state = await initialState;
        if (_isClosed) return;
        _settings = state.Settings;
        ApplyTelemetryPreference(_settings.AutomaticTelemetryUpload);
        _settingsPersistedAndReadable = state.SettingsReadable;
        LocalizationManager.Apply(_settings.Language);
        await InitializeHistoryAsync();
        if (_isClosed) return;
        _startupReady = true;
        SettingsButton.IsEnabled = true;
        RefreshLocalizedPresentation();
        if (state.PendingUploads)
        {
            StartPendingBugReportRetry();
        }
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var result = await _releaseUpdater.CheckAndInstallAsync(version, _lifetimeCancellation.Token);
        if (_isClosed) return;
        VersionAuthorText.ToolTip = result.Path == null ? result.Message : result.Message + "\n" + result.Path;
        if (result.State == ReleaseUpdateState.UpdateStarted && result.Version != null)
        {
            VersionAuthorRun.Text += LocalizationManager.Format("Update.StatusUpdatingFormat", result.Version);
            VersionAuthorText.Foreground = Brush("#8FD6A8");
            Close();
            return;
        }
        else if (result.State == ReleaseUpdateState.Rejected)
        {
            VersionAuthorRun.Text += LocalizationManager.Text("Update.StatusRejected");
            VersionAuthorText.Foreground = Brush("#FF8A6A");
        }

        Task googleDriveStartupSync = Task.CompletedTask;
        if (_googleOAuthClient.IsSignedIn)
        {
            googleDriveStartupSync = RunGoogleDriveSyncAsync(false);
            _googleDriveSyncTask = googleDriveStartupSync;
        }

        // If settings were deleted or unreadable, let an existing Drive backup
        // restore them before onboarding can persist fallback defaults.
        if (ShouldRestoreSettingsBeforeOnboarding(
                _googleOAuthClient.IsSignedIn,
                _settingsPersistedAndReadable))
        {
            await googleDriveStartupSync;
            if (_isClosed) return;
        }

        if (_isClosed) return;
        await InitializeAutoStartSettingAsync();
        if (_isClosed) return;
        _autoStartInitialized = true;
        ShowGoogleDrivePromptIfNeeded();
        await googleDriveStartupSync;
    }

    private void OnHistorySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        if (_updatingHistorySelection) return;
        if (LandingHistoryList.SelectedItem is not LandingRecord selected)
        {
            ClearSelectedRecord();
            RefreshHistoryLoadingPresentation();
            return;
        }

        SelectRecord(selected);
    }

    private void SelectRecord(LandingRecord selected)
    {
        _ = SelectRecordAsync(selected);
    }

    private void PresentRecord(LandingRecord detail, IReadOnlyList<LandingRecord> replayContacts)
    {
        if (ReferenceEquals(DataContext, detail))
        {
            // Airport resolution mutates cached details after they have already been bound.
            // LandingRecord is intentionally a storage DTO, not INotifyPropertyChanged, so
            // force a binding refresh when selecting the same instance again.
            DataContext = null;
        }

        DataContext = detail;
        MainChart.Record = detail;
        ReplayView.SetRecord(detail, replayContacts);
        UpdateControlTab(ReplayView.IsPlaybackAvailable, ReconstructionTab, ControlsTab);
        MiniGChart.Record = detail;
        MiniPowerChart.Record = detail;
        MiniGearChart.Record = detail;
        Timeline.Record = detail;
        MiniGChart.SetIsolatedSeries(0);
        MiniPowerChart.SetIsolatedSeries(detail.Engines.Count > 0 ? 0 : (int?)null);
        _primaryGearSeriesIndex = PrimaryGearSeriesIndex(detail);
        MiniGearChart.SetIsolatedSeries(detail.ContactPoints.Count > 0 ? _primaryGearSeriesIndex : (int?)null);
        _mainIsolatedSeriesIndex = null;
        MainChart.SetIsolatedSeries(null);
        UpdateMainLegend(MainChart.Mode);
        UpdateChartDescription(MainChart.Mode);
        ApplyZoom(_showFullApproach ? null : -6, _showFullApproach ? null : 8);
        UpdateLaneReadouts(detail, 0);
        RefreshRunwayLoadingPresentation();
    }

    private void OnChartHoverTimeChanged(object? sender, LandingChartHoverEventArgs eventArgs)
    {
        var time = eventArgs.TimeSeconds ?? (_showReplay ? ReplayView.CurrentTimeSeconds : (double?)null);
        if (time.HasValue)
        {
            if (!ReferenceEquals(sender, ReplayView)) ReplayView.SetTime(time.Value);
        }
        foreach (var chart in _charts)
        {
            chart.SetHoverTime(time);
        }

        if (DataContext is LandingRecord record)
        {
            UpdateLaneReadouts(record, time ?? 0);
        }
    }

    private void OnChartZoomRangeSelected(object? sender, LandingChartZoomEventArgs eventArgs)
    {
        _showFullApproach = !eventArgs.StartSeconds.HasValue || !eventArgs.EndSeconds.HasValue;
        ApplyZoom(eventArgs.StartSeconds, eventArgs.EndSeconds);
    }

    private void ApplyZoom(double? startSeconds, double? endSeconds)
    {
        _zoomStartSeconds = startSeconds;
        _zoomEndSeconds = endSeconds;
        foreach (var chart in _charts)
        {
            chart.SetZoomRange(_showReplay ? null : startSeconds, _showReplay ? null : endSeconds);
        }

        Timeline.SetZoomRange(_showReplay ? null : startSeconds, _showReplay ? null : endSeconds);
        if (startSeconds.HasValue && endSeconds.HasValue)
        {
            ChartWindowButton.Content = LocalizationManager.Format(
                "View.ContactFormat",
                startSeconds.Value,
                endSeconds.Value);
        }
        else if (DataContext is LandingRecord record && record.Series.Count > 1)
        {
            ChartWindowButton.Content = LocalizationManager.Format(
                "View.FullApproachFormat",
                record.Series[0].TimeSeconds);
        }
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs eventArgs)
    {
        _showFullApproach = true;
        ApplyZoom(null, null);
    }

    private void OnChartWindowClick(object sender, RoutedEventArgs eventArgs)
    {
        _showFullApproach = !_showFullApproach;
        ApplyZoom(_showFullApproach ? null : -6, _showFullApproach ? null : 8);
    }

    private void OnSessionFilterChanged(object sender, TextChangedEventArgs eventArgs)
    {
        ApplySessionFilter();
    }

    private void ApplySessionFilter(string? preferredSelectionId = null)
    {
        if (LandingHistoryList == null || HistoryCountText == null)
        {
            return;
        }

        var selectedId = preferredSelectionId ?? (LandingHistoryList.SelectedItem as LandingRecord)?.Id;
        var query = SessionFilterBox?.Text?.Trim() ?? string.Empty;
        var available = _landings.Where(record => !_locallyRemovedHistoryIds.Contains(record.Id)).ToArray();
        IReadOnlyList<LandingRecord> filtered = string.IsNullOrWhiteSpace(query)
            ? available
            : available.Where(record =>
                record.AircraftTitle.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                record.AircraftType.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                record.Airport.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                record.Runway.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

        var selected = selectedId == null ? null : filtered.FirstOrDefault(record => record.Id == selectedId);
        _updatingHistorySelection = true;
        try
        {
            LandingHistoryList.ItemsSource = filtered;
            HistoryCountText.Text = filtered.Count.ToString();
            LandingHistoryList.SelectedItem = selected ?? filtered.FirstOrDefault();
        }
        finally
        {
            _updatingHistorySelection = false;
        }
        if (LandingHistoryList.SelectedItem is LandingRecord current)
            SelectRecord(current);
        UpdateHistoryPresentation(filtered.Count);

        var monthlyAverage = MonthlyAverageDisplayedFpm(_landings, DateTime.Now);
        AverageRateText.Text = !monthlyAverage.HasValue
            ? "—"
            : $"{monthlyAverage.Value:+0;-0;0} fpm";
    }

    internal static double? MonthlyAverageDisplayedFpm(IEnumerable<LandingRecord> records, DateTime localNow)
    {
        var primaryContacts = records
            .Where(record => record.ContactNumber == 1)
            .Where(record =>
            {
                var local = record.TimestampUtc.ToLocalTime();
                return local.Year == localNow.Year && local.Month == localNow.Month;
            })
            .ToArray();
        return primaryContacts.Length == 0
            ? (double?)null
            : primaryContacts.Average(record => -record.InertialFpm);
    }

    private void UpdateHistoryPresentation(int visibleCount)
    {
        var hasVisibleLanding = visibleCount > 0;
        LandingContent.Visibility = hasVisibleLanding ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasVisibleLanding ? Visibility.Collapsed : Visibility.Visible;
        if (hasVisibleLanding)
        {
            RefreshHistoryLoadingPresentation();
            return;
        }

        if (_landings.Count == 0)
        {
            EmptyStateTitle.Text = LocalizationManager.Text("History.NoLandingsTitle");
            EmptyStateDescription.Text = LocalizationManager.Text("History.NoLandingsBody");
        }
        else
        {
            EmptyStateTitle.Text = LocalizationManager.Text("History.NoMatchesTitle");
            EmptyStateDescription.Text = LocalizationManager.Text("History.NoMatchesBody");
        }

        ClearSelectedRecord();
        RefreshHistoryLoadingPresentation();
    }

    private void ClearSelectedRecord()
    {
        _detailLoadVersion++;
        _detailLoading = false;
        _detailLoadError = null;
        ClearRecordPresentation();
    }

    private void ClearRecordPresentation()
    {
        _presentedReplayContacts = Array.Empty<LandingRecord>();
        DataContext = null;
        MainChart.Record = null;
        ReplayView.Record = null;
        UpdateControlTab(false, ReconstructionTab, ControlsTab);
        MiniGChart.Record = null;
        MiniPowerChart.Record = null;
        MiniGearChart.Record = null;
        Timeline.Record = null;
        RefreshRunwayLoadingPresentation();
    }

    private void OnDeleteLandingClick(object sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (sender is not Button button || button.Tag is not LandingRecord record)
        {
            return;
        }

        _pendingDeleteRecord = record;
        UpdateDeleteLandingDescription(record);
        DeleteLandingErrorText.Text = string.Empty;
        DeleteLandingErrorText.Visibility = Visibility.Collapsed;
        ConfirmDeleteLandingButton.IsEnabled = true;
        DeleteLandingOverlay.Visibility = Visibility.Visible;
        DeleteLandingOverlay.Focus();
        Keyboard.Focus(CancelDeleteLandingButton);
    }

    private void OnCancelDeleteLandingClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_deleteLandingInProgress)
        {
            return;
        }
        HideDeleteLandingConfirmation();
    }

    private void OnDeleteLandingOverlayKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape || _deleteLandingInProgress)
        {
            return;
        }

        eventArgs.Handled = true;
        HideDeleteLandingConfirmation();
    }

    private async void OnConfirmDeleteLandingClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_isClosed || _historyStopped || _deleteLandingInProgress) return;
        var record = _pendingDeleteRecord;
        if (record == null)
        {
            HideDeleteLandingConfirmation();
            return;
        }

        var visibleRecords = LandingHistoryList.Items.Cast<LandingRecord>().ToList();
        var selectedId = (LandingHistoryList.SelectedItem as LandingRecord)?.Id;
        string? preferredSelectionId;
        if (!string.Equals(selectedId, record.Id, StringComparison.Ordinal))
        {
            preferredSelectionId = selectedId;
        }
        else
        {
            var deletedIndex = visibleRecords.FindIndex(candidate =>
                string.Equals(candidate.Id, record.Id, StringComparison.Ordinal));
            var remainingVisible = visibleRecords
                .Where(candidate => !string.Equals(candidate.Id, record.Id, StringComparison.Ordinal))
                .ToList();
            var nextIndex = remainingVisible.Count == 0
                ? -1
                : Math.Min(Math.Max(0, deletedIndex), remainingVisible.Count - 1);
            preferredSelectionId = nextIndex >= 0 ? remainingVisible[nextIndex].Id : null;
        }

        var signedIn = _googleOAuthClient.IsSignedIn;
        Exception? queueError = null;
        try
        {
            _deleteLandingInProgress = true;
            ConfirmDeleteLandingButton.IsEnabled = false;
            CancelDeleteLandingButton.IsEnabled = false;
            // Reserve the ID before yielding so any earlier history/detail read
            // cannot republish the record while its deletion waits for storage.
            _locallyRemovedHistoryIds.Add(record.Id);
            ApplySessionFilter(preferredSelectionId);
            var deletion = Task.Run(async () =>
            {
                _googleDriveBackupService.DeleteLandingLocally(record.Id);
                try
                {
                    // Accepted local deletion metadata is durable work, not
                    // network activity; finish it even if the window is closing.
                    await _googleDriveBackupService.QueueLandingDeletionAsync(record.Id, CancellationToken.None)
                        .ConfigureAwait(false);
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            _localDeletionTask = deletion;
            _googleDriveDeleteTask = deletion;
            queueError = await deletion;
            if (_isClosed || _historyStopped) return;
            _loadedDetails.Remove(record.Id);
            _landings = _landings
                .Where(candidate => !string.Equals(candidate.Id, record.Id, StringComparison.Ordinal))
                .ToArray();
            HideDeleteLandingConfirmation(true);
            ApplySessionFilter();
        }
        catch (Exception exception)
        {
            _locallyRemovedHistoryIds.Remove(record.Id);
            if (_isClosed || _historyStopped) return;
            if (!_landings.Any(candidate => candidate.Id == record.Id))
                _landings = _landings.Concat(new[] { record }).OrderByDescending(candidate => candidate.TimestampUtc).ToArray();
            ApplySessionFilter();
            _deleteLandingInProgress = false;
            ConfirmDeleteLandingButton.IsEnabled = true;
            CancelDeleteLandingButton.IsEnabled = true;
            DeleteLandingErrorText.Text = LocalizationManager.Format("Delete.ErrorFormat", exception.Message);
            DeleteLandingErrorText.Visibility = Visibility.Visible;
            Keyboard.Focus(CancelDeleteLandingButton);
            return;
        }

        try
        {
            if (queueError != null) throw queueError;
            if (signedIn)
            {
                QueueGoogleDriveSync();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _googleDriveErrorDetail = exception.Message;
            RefreshGoogleDrivePresentation();
        }
        finally
        {
            _deleteLandingInProgress = false;
        }
    }

    private void HideDeleteLandingConfirmation(bool force = false)
    {
        if (_deleteLandingInProgress && !force)
        {
            return;
        }
        _pendingDeleteRecord = null;
        DeleteLandingOverlay.Visibility = Visibility.Collapsed;
        ConfirmDeleteLandingButton.IsEnabled = true;
        CancelDeleteLandingButton.IsEnabled = true;
        Keyboard.Focus(LandingHistoryList);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (!_startupReady || _isClosed) return;
        if (DeleteLandingOverlay.Visibility == Visibility.Visible)
        {
            HideDeleteLandingConfirmation();
        }

        RefreshAutoStartErrorPresentation();
        SetLanguageSelection(_settings.Language);
        RefreshSettingsPresentation();
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.Focus();
        Keyboard.Focus(CloseSettingsButton);
    }

    private void OnCloseSettingsClick(object sender, RoutedEventArgs eventArgs)
    {
        HideSettings();
    }

    private void OnSettingsOverlayKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape)
        {
            return;
        }

        eventArgs.Handled = true;
        HideSettings();
    }

    private void OnSettingsOverlayMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.OriginalSource, SettingsOverlay)) return;

        eventArgs.Handled = true;
        HideSettings();
    }

    private void HideSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        Keyboard.Focus(LandingHistoryList);
    }

    private async void OnLanguageSelectionChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_changingLanguageSelection || SettingsBusy || _settingsOperations.IsStopping ||
            sender is not RadioButton button ||
            button.IsChecked != true ||
            button.Tag is not string preference)
        {
            return;
        }

        preference = LocalizationManager.NormalizePreference(preference);
        if (string.Equals(_settings.Language, preference, StringComparison.Ordinal))
        {
            return;
        }

        string? error = null;
        var saved = await RunSettingsOperationAsync(async () =>
        {
            var snapshot = _settings.Clone();
            snapshot.Language = preference;
            try
            {
                await SettingsPersistence.SaveAsync(_settingsRepository, snapshot);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            _settings.CopyFrom(snapshot);
            if (!_isClosed)
            {
                LocalizationManager.Apply(preference);
                RefreshLocalizedPresentation();
            }
            return true;
        });
        if (_isClosed) return;
        if (!saved)
        {
            SetLanguageSelection(_settings.Language);
            if (error != null)
            {
                SettingsErrorText.Text = LocalizationManager.Format("Settings.SaveErrorFormat", error);
                SettingsErrorText.Visibility = Visibility.Visible;
            }
            return;
        }
        SettingsErrorText.Text = string.Empty;
        SettingsErrorText.Visibility = Visibility.Collapsed;
        QueueGoogleDriveSync();
    }

    private async void OnAutoStartSelectionChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_changingAutoStartSelection || SettingsBusy || _settingsOperations.IsStopping || !IsLoaded || sender is not CheckBox checkBox)
        {
            return;
        }

        var enabled = checkBox.IsChecked == true;
        if (_settings.StartWithSimulator.HasValue && _settings.StartWithSimulator.Value == enabled)
        {
            return;
        }

        if (!await ApplyAutoStartPreferenceAsync(enabled) && !_isClosed)
        {
            SetAutoStartSelection(_settings.StartWithSimulator == true);
        }
    }

    private async void OnEnableAutoStartClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SettingsBusy || _settingsOperations.IsStopping) return;
        SetAutoStartPromptBusy(true);
        var saved = await ApplyAutoStartPreferenceAsync(true);
        if (_isClosed) return;
        if (saved)
        {
            HideAutoStartPrompt();
        }
        else
        {
            SetAutoStartPromptBusy(false);
        }
    }

    private async void OnDeclineAutoStartClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SettingsBusy || _settingsOperations.IsStopping) return;
        SetAutoStartPromptBusy(true);
        var saved = await ApplyAutoStartPreferenceAsync(false);
        if (_isClosed) return;
        if (saved)
        {
            HideAutoStartPrompt();
        }
        else
        {
            SetAutoStartPromptBusy(false);
        }
    }

    private async Task InitializeAutoStartSettingAsync()
    {
        await RunSettingsOperationAsync(async () =>
        {
            _autoStartLegacyCleanupError = await RemoveLegacyAutoStartEntriesAsync();
            if (!_settings.StartWithSimulator.HasValue)
            {
                _autoStartErrorDetail = _autoStartLegacyCleanupError;
                _autoStartInitialized = true;
                return true;
            }
            var enabled = _settings.StartWithSimulator.Value;
            try
            {
                var result = await Task.Run(() => _autoStartManager.SetEnabled(enabled));
                _autoStartProfileCount = result.ConfigurationPaths.Count;
                _autoStartErrorDetail = _autoStartLegacyCleanupError;
            }
            catch (Exception exception)
            {
                _autoStartErrorDetail = CombineAutoStartErrors(_autoStartLegacyCleanupError, exception.Message);
            }
            _autoStartInitialized = true;
            return true;
        });
        if (!_isClosed && !_settings.StartWithSimulator.HasValue)
        {
            ShowAutoStartPrompt();
            RefreshAutoStartErrorPresentation();
        }
    }

    private Task<string?> RemoveLegacyAutoStartEntriesAsync() => Task.Run(() =>
    {
        try
        {
            _autoStartManager.RemoveLegacyUnsupportedEntries();
            return (string?)null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    });

    internal static string? CombineAutoStartErrors(string? legacyCleanupError, string? currentError)
    {
        if (string.IsNullOrWhiteSpace(legacyCleanupError))
        {
            return string.IsNullOrWhiteSpace(currentError) ? null : currentError;
        }

        return string.IsNullOrWhiteSpace(currentError)
            ? legacyCleanupError
            : legacyCleanupError + " " + currentError;
    }

    private async Task<bool> ApplyAutoStartPreferenceAsync(bool enabled)
    {
        var saved = await RunSettingsOperationAsync(async () =>
        {
            _autoStartLegacyCleanupError = await RemoveLegacyAutoStartEntriesAsync();
            var previous = _settings.StartWithSimulator;
            var snapshot = _settings.Clone();
            snapshot.StartWithSimulator = enabled;
            AutoStartOperationResult? result = null;
            try
            {
                await SettingsPersistence.SaveAsync(
                    _settingsRepository, snapshot,
                    () => result = _autoStartManager.SetEnabled(enabled),
                    () => _autoStartManager.SetEnabled(previous == true));
            }
            catch (Exception exception)
            {
                _autoStartErrorDetail = CombineAutoStartErrors(_autoStartLegacyCleanupError, exception.Message);
                return false;
            }
            _settings.CopyFrom(snapshot);
            _autoStartProfileCount = result!.ConfigurationPaths.Count;
            _autoStartErrorDetail = _autoStartLegacyCleanupError;
            return true;
        });
        if (saved) QueueGoogleDriveSync();
        return saved;
    }

    private void ShowAutoStartPrompt()
    {
        AutoStartPromptErrorText.Text = string.Empty;
        AutoStartPromptErrorText.Visibility = Visibility.Collapsed;
        SetAutoStartPromptBusy(false);
        AutoStartPromptOverlay.Visibility = Visibility.Visible;
        AutoStartPromptOverlay.Focus();
        Keyboard.Focus(DeclineAutoStartButton);
    }

    private void HideAutoStartPrompt()
    {
        AutoStartPromptOverlay.Visibility = Visibility.Collapsed;
        SetAutoStartPromptBusy(false);
        Keyboard.Focus(LandingHistoryList);
        ShowGoogleDrivePromptIfNeeded();
    }

    private void SetAutoStartPromptBusy(bool busy)
    {
        _autoStartPromptBusy = busy;
        RefreshSettingsOperationPresentation();
    }

    private void RefreshAutoStartErrorPresentation()
    {
        var hasError = !string.IsNullOrWhiteSpace(_autoStartErrorDetail);
        var text = hasError
            ? LocalizationManager.Format("AutoStart.ErrorFormat", _autoStartErrorDetail!)
            : string.Empty;
        SettingsErrorText.Text = text;
        SettingsErrorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        AutoStartPromptErrorText.Text = text;
        AutoStartPromptErrorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        if (_settingsBusyIndicator.IsVisible && AutoStartPromptOverlay.Visibility == Visibility.Visible)
        {
            AutoStartPromptErrorText.Text = LocalizationManager.Text("Settings.Applying");
            AutoStartPromptErrorText.Visibility = Visibility.Visible;
        }
    }

    private void SetLanguageSelection(string preference)
    {
        _changingLanguageSelection = true;
        try
        {
            var normalized = LocalizationManager.NormalizePreference(preference);
            SettingsLanguageAuto.IsChecked = normalized == LocalizationManager.AutomaticLanguage;
            SettingsLanguageEnglish.IsChecked = normalized == LocalizationManager.EnglishLanguage;
            SettingsLanguageRussian.IsChecked = normalized == LocalizationManager.RussianLanguage;
        }
        finally
        {
            _changingLanguageSelection = false;
        }
    }

    private void SetAutoStartSelection(bool enabled)
    {
        _changingAutoStartSelection = true;
        try
        {
            SettingsAutoStartCheckBox.IsChecked = enabled;
        }
        finally
        {
            _changingAutoStartSelection = false;
        }
    }

    private void RefreshSettingsPresentation()
    {
        var showLoading = _settingsLoadingIndicator.Update(
            !_isClosed && (!_startupReady || !_autoStartInitialized), null, RefreshSettingsPresentation);
        var effectiveName = LocalizationManager.Text(
            LocalizationManager.EffectiveLanguage == LocalizationManager.RussianLanguage
                ? "Settings.Russian"
                : "Settings.English");
        SettingsAutomaticLanguageText.Text = LocalizationManager.Format(
            "Settings.AutomaticResolvedFormat",
            effectiveName);
        SettingsFileText.Text = LocalizationManager.Format(
            "Settings.FileFormat",
            "%LOCALAPPDATA%\\MSFS Landing Stats\\settings.json");
        SettingsFileText.ToolTip = _settingsRepository.Path;
        SetAutoStartSelection(_settings.StartWithSimulator == true);
        RefreshTelemetrySettingsPresentation();
        SettingsAutoStartStateText.Text = !_autoStartInitialized
            ? (showLoading ? LocalizationManager.Text("Settings.Loading") : string.Empty)
            : _settings.StartWithSimulator == true
            ? LocalizationManager.Format("Settings.AutoStartEnabledFormat", _autoStartProfileCount)
            : LocalizationManager.Text("Settings.AutoStartDisabled");
        RefreshGoogleDrivePresentation();
        RefreshAutoStartErrorPresentation();
        RefreshSettingsOperationPresentation();
    }

    private async void OnGoogleDrivePrimaryClick(object sender, RoutedEventArgs eventArgs)
    {
        await ConnectOrSyncGoogleDriveAsync();
    }

    private async Task ConnectOrSyncGoogleDriveAsync()
    {
        if (GoogleDriveBusy || SettingsBusy || _settingsOperations.IsStopping || _isClosed)
        {
            return;
        }

        if (!_googleOAuthClient.IsSignedIn)
        {
            _googleDriveAuthorizing = true;
            _googleDriveErrorDetail = null;
            _googleDriveWarningDetail = null;
            RefreshGoogleDrivePresentation();
            using var authorizationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _googleDriveAuthorizationCancellation = authorizationCancellation;
            _googleDriveAuthorizationCanceledByUser = false;
            authorizationCancellation.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await RunSettingsOperationAsync(async () =>
                {
                    await Task.Run(() => _googleOAuthClient.SignInAsync(authorizationCancellation.Token));
                    return true;
                });
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                if (!_googleDriveAuthorizationCanceledByUser)
                {
                    _googleDriveErrorDetail = LocalizationManager.Text("Settings.DriveAuthorizationTimeout");
                }
                return;
            }
            catch (Exception exception)
            {
                _googleDriveErrorDetail = exception.Message;
                return;
            }
            finally
            {
                _googleDriveAuthorizationCancellation = null;
                _googleDriveAuthorizationCanceledByUser = false;
                _googleDriveAuthorizing = false;
                if (!_isClosed) RefreshGoogleDrivePresentation();
            }
        }

        if (_isClosed) return;
        _googleDriveSyncTask = RunGoogleDriveSyncAsync(true);
        await _googleDriveSyncTask;
        if (!_isClosed && _googleOAuthClient.IsSignedIn)
        {
            await TrySaveGoogleDrivePromptAnsweredAsync(false);
        }
    }

    private async void OnEnableGoogleDriveClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SettingsBusy || _settingsOperations.IsStopping || _isClosed) return;
        SetGoogleDrivePromptBusy(true);
        var saved = await TrySaveGoogleDrivePromptAnsweredAsync(true);
        if (_isClosed) return;
        if (!saved)
        {
            SetGoogleDrivePromptBusy(false);
            return;
        }

        HideGoogleDrivePrompt();
        await ConnectOrSyncGoogleDriveAsync();
    }

    private async void OnDeclineGoogleDriveClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SettingsBusy || _settingsOperations.IsStopping || _isClosed) return;
        SetGoogleDrivePromptBusy(true);
        var saved = await TrySaveGoogleDrivePromptAnsweredAsync(true);
        if (_isClosed) return;
        if (saved)
        {
            HideGoogleDrivePrompt();
        }
        else
        {
            SetGoogleDrivePromptBusy(false);
        }
    }

    private void ShowGoogleDrivePromptIfNeeded()
    {
        if (_isClosed || _settingsOperations.IsStopping) return;
        var signedIn = _googleOAuthClient.IsSignedIn;
        if (signedIn && !_settings.GoogleDrivePromptAnswered)
        {
            // The save itself joins PendingSettingsOperations synchronously.
            _ = TrySaveGoogleDrivePromptAnsweredAsync(false);
        }
        if (!ShouldShowGoogleDrivePrompt(
                _settings,
                signedIn,
                AutoStartPromptOverlay.Visibility == Visibility.Visible))
        {
            return;
        }

        GoogleDrivePromptErrorText.Text = string.Empty;
        GoogleDrivePromptErrorText.Visibility = Visibility.Collapsed;
        SetGoogleDrivePromptBusy(false);
        GoogleDrivePromptOverlay.Visibility = Visibility.Visible;
        GoogleDrivePromptOverlay.Focus();
        Keyboard.Focus(DeclineGoogleDriveButton);
    }

    internal static bool ShouldShowGoogleDrivePrompt(
        ApplicationSettings settings,
        bool signedIn,
        bool autoStartPromptVisible)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        return !settings.GoogleDrivePromptAnswered && !signedIn && !autoStartPromptVisible;
    }

    internal static bool ShouldRestoreSettingsBeforeOnboarding(
        bool signedIn,
        bool settingsPersistedAndReadable) =>
        signedIn && !settingsPersistedAndReadable;

    private async Task<bool> TrySaveGoogleDrivePromptAnsweredAsync(bool showPromptError)
    {
        string? error = null;
        var saved = await RunSettingsOperationAsync(async () =>
        {
            if (_settings.GoogleDrivePromptAnswered) return true;
            var snapshot = _settings.Clone();
            snapshot.GoogleDrivePromptAnswered = true;
            try
            {
                await SettingsPersistence.SaveAsync(_settingsRepository, snapshot);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            _settings.CopyFrom(snapshot);
            return true;
        });
        if (!saved && showPromptError && error != null && !_isClosed)
        {
            GoogleDrivePromptErrorText.Text = LocalizationManager.Format(
                "GoogleDrivePrompt.SaveErrorFormat", error);
            GoogleDrivePromptErrorText.Visibility = Visibility.Visible;
        }
        return saved;
    }

    private void HideGoogleDrivePrompt()
    {
        GoogleDrivePromptOverlay.Visibility = Visibility.Collapsed;
        SetGoogleDrivePromptBusy(false);
        Keyboard.Focus(LandingHistoryList);
    }

    private void SetGoogleDrivePromptBusy(bool busy)
    {
        _googleDrivePromptBusy = busy;
        RefreshSettingsOperationPresentation();
    }

    private async void OnGoogleDriveSignOutClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_googleDriveAuthorizing)
        {
            _googleDriveAuthorizationCanceledByUser = true;
            _googleDriveAuthorizationCancellation?.Cancel();
            return;
        }
        if (GoogleDriveBusy || SettingsBusy || _settingsOperations.IsStopping || _isClosed)
        {
            return;
        }
        await RunSettingsOperationAsync(async () =>
        {
            try
            {
                await Task.Run(() => _googleOAuthClient.SignOut());
                _lastGoogleDriveSyncUtc = null;
                _googleDriveErrorDetail = null;
                _googleDriveWarningDetail = null;
                return true;
            }
            catch (Exception exception)
            {
                _googleDriveErrorDetail = exception.Message;
                return false;
            }
        });
    }

    private void QueueGoogleDriveSync()
    {
        if (_isClosed || !_startupReady || _lifetimeCancellation.IsCancellationRequested || !_googleOAuthClient.IsSignedIn)
        {
            return;
        }
        _googleDriveSyncTask = RunGoogleDriveSyncAsync(false);
    }

    private async Task RunGoogleDriveSyncAsync(bool userInitiated)
    {
        if (_isClosed || !_startupReady || _lifetimeCancellation.IsCancellationRequested || !_googleOAuthClient.IsSignedIn)
        {
            return;
        }

        _googleDrivePendingOperations++;
        if (_googleDrivePendingOperations == 1)
        {
            _googleDriveErrorDetail = null;
            _googleDriveWarningDetail = null;
            RefreshGoogleDrivePresentation();
        }
        try
        {
            var selectedId = (LandingHistoryList.SelectedItem as LandingRecord)?.Id;
            // SyncAsync loads its local state before the first network await.
            // Start the whole operation off-dispatcher, including that prefix.
            var cancellationToken = _lifetimeCancellation.Token;
            var result = await Task.Run(() => _googleDriveBackupService.SyncAsync(cancellationToken));
            if (_isClosed)
            {
                return;
            }
            if (result.DownloadedSettings || result.UploadedSettings)
            {
                // A successful sync either restored cloud settings or established
                // the current local snapshot as the first cloud revision.
                await CompleteGoogleDriveSettingsRestoreAsync();
                if (_isClosed) return;
            }
            _lastGoogleDriveSyncUtc = DateTime.UtcNow;
            _googleDriveErrorDetail = null;
            var warnings = new List<string>();
            if (result.ConflictedLandingIds.Count > 0)
            {
                warnings.Add(LocalizationManager.Format(
                    "Settings.DriveLandingConflictFormat",
                    result.ConflictedLandingIds.Count));
            }
            if (result.SettingsConflict)
            {
                warnings.Add(LocalizationManager.Text("Settings.DriveSettingsConflict"));
            }
            if (result.SettingsChangedDuringSync)
            {
                warnings.Add(LocalizationManager.Text("Settings.DriveSettingsChangedDuringSync"));
            }
            if (result.UnsupportedSettingsFormatVersion.HasValue)
            {
                warnings.Add(LocalizationManager.Format(
                    "Settings.DriveUnsupportedSettingsFormat",
                    result.UnsupportedSettingsFormatVersion.Value));
            }
            _googleDriveWarningDetail = warnings.Count == 0 ? null : string.Join(" ", warnings);
            if (result.HistoryChanged)
            {
                InvalidateLoadedHistoryDetails();
                await LoadHistoryAsync(selectedId);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (GoogleDriveNotSignedInException)
        {
            _googleDriveErrorDetail = LocalizationManager.Text("Settings.DriveSignInAgain");
        }
        catch (Exception exception)
        {
            _googleDriveErrorDetail = exception.Message;
        }
        finally
        {
            _googleDrivePendingOperations--;
            if (_googleDrivePendingOperations == 0 && !_isClosed)
            {
                RefreshGoogleDrivePresentation();
            }
        }
    }

    private Task<GoogleDriveLocalSettings> ReadCloudSettingsAsync()
    {
        if (Dispatcher.CheckAccess())
        {
            return ReadCommittedCloudSettingsAsync();
        }
        return Dispatcher.InvokeAsync(ReadCommittedCloudSettingsAsync).Task.Unwrap();
    }

    private async Task<GoogleDriveLocalSettings> ReadCommittedCloudSettingsAsync()
    {
        // Do not expose an old snapshot while an accepted local save is pending.
        await PendingSettingsOperations;
        return CreateLocalCloudSettingsSnapshot();
    }

    private GoogleDriveLocalSettings CreateLocalCloudSettingsSnapshot() =>
        new GoogleDriveLocalSettings(CreateCloudSettingsSnapshot(), _settingsPersistedAndReadable);

    private GoogleDriveCloudSettings CreateCloudSettingsSnapshot() => new GoogleDriveCloudSettings
    {
        Language = _settings.Language,
        StartWithSimulator = _settings.StartWithSimulator,
    };

    private Task<bool> ApplyCloudSettingsAsync(
        GoogleDriveCloudSettings cloudSettings,
        string expectedLocalHash)
    {
        if (Dispatcher.CheckAccess())
        {
            return ApplyCloudSettingsIfUnchangedAsync(cloudSettings, expectedLocalHash);
        }
        return Dispatcher.InvokeAsync(() =>
            ApplyCloudSettingsIfUnchangedAsync(cloudSettings, expectedLocalHash)).Task.Unwrap();
    }

    private Task<bool> ApplyCloudSettingsIfUnchangedAsync(
        GoogleDriveCloudSettings cloudSettings,
        string expectedLocalHash) => RunSettingsOperationAsync(async () =>
    {
        var currentHash = GoogleDriveBackupService.Hash(
            GoogleDriveCloudSettings.Serialize(CreateCloudSettingsSnapshot()));
        if (!string.Equals(currentHash, expectedLocalHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await ApplyCloudSettingsCoreAsync(cloudSettings);
        return true;
    });

    private async Task ApplyCloudSettingsCoreAsync(GoogleDriveCloudSettings cloudSettings)
    {
        var cloudSnapshot = cloudSettings.Clone();
        cloudSnapshot.Normalize();
        var previousLanguage = _settings.Language;
        var previousAutoStart = _settings.StartWithSimulator;
        var targetAutoStart = cloudSnapshot.StartWithSimulator ?? previousAutoStart;
        AutoStartOperationResult? autoStartResult = null;
        var snapshot = _settings.Clone();
        snapshot.Language = cloudSnapshot.Language;
        snapshot.StartWithSimulator = targetAutoStart;
        var updateAutoStart = _autoStartInitialized && targetAutoStart.HasValue && targetAutoStart != previousAutoStart;
        await SettingsPersistence.SaveAsync(
            _settingsRepository, snapshot,
            updateAutoStart ? () => autoStartResult = _autoStartManager.SetEnabled(targetAutoStart!.Value) : null,
            updateAutoStart ? () => _autoStartManager.SetEnabled(previousAutoStart == true) : null);
        _settings.CopyFrom(snapshot);

        if (autoStartResult != null)
        {
            _autoStartProfileCount = autoStartResult.ConfigurationPaths.Count;
        }
        if (_isClosed) return;
        if (targetAutoStart.HasValue && AutoStartPromptOverlay.Visibility == Visibility.Visible)
        {
            HideAutoStartPrompt();
        }
        if (!string.Equals(previousLanguage, _settings.Language, StringComparison.Ordinal))
        {
            LocalizationManager.Apply(_settings.Language);
            RefreshLocalizedPresentation();
        }
        else
        {
            RefreshSettingsPresentation();
        }
    }

    private void RefreshGoogleDrivePresentation()
    {
        if (GoogleDrivePrimaryButton == null)
        {
            return;
        }
        if (!_startupReady)
        {
            GoogleDrivePrimaryButton.IsEnabled = false;
            GoogleDriveSignOutButton.IsEnabled = false;
            GoogleDriveStateText.Text = _settingsLoadingIndicator.IsVisible
                ? LocalizationManager.Text("Settings.Loading") : string.Empty;
            return;
        }
        var signedIn = _googleOAuthClient.IsSignedIn;
        GoogleDrivePrimaryButton.Content = LocalizationManager.Text(
            signedIn ? "Settings.DriveSyncNow" : "Settings.DriveConnect");
        GoogleDrivePrimaryButton.IsEnabled = !GoogleDriveBusy && !SettingsBusy && !_settingsOperations.IsStopping;
        GoogleDriveSignOutButton.Content = LocalizationManager.Text(
            _googleDriveAuthorizing ? "Common.Cancel" : "Settings.DriveDisconnect");
        GoogleDriveSignOutButton.Visibility = signedIn || _googleDriveAuthorizing
            ? Visibility.Visible
            : Visibility.Collapsed;
        GoogleDriveSignOutButton.IsEnabled = !_settingsOperations.IsStopping &&
            (_googleDriveAuthorizing || (!GoogleDriveBusy && !SettingsBusy));

        if (!string.IsNullOrWhiteSpace(_googleDriveErrorDetail))
        {
            GoogleDriveStateText.Text = LocalizationManager.Format(
                "Settings.DriveErrorFormat",
                _googleDriveErrorDetail!);
            GoogleDriveStateText.Foreground = Brush("#FF8A6A");
        }
        else if (!string.IsNullOrWhiteSpace(_googleDriveWarningDetail))
        {
            GoogleDriveStateText.Text = LocalizationManager.Format(
                "Settings.DriveWarningFormat",
                _googleDriveWarningDetail!);
            GoogleDriveStateText.Foreground = Brush("#D9C46A");
        }
        else if (GoogleDriveBusy)
        {
            GoogleDriveStateText.Text = LocalizationManager.Text(
                _googleDriveAuthorizing ? "Settings.DriveAuthorizing" : "Settings.DriveSyncing");
            GoogleDriveStateText.Foreground = Brush("#D9C46A");
        }
        else if (!signedIn)
        {
            GoogleDriveStateText.Text = LocalizationManager.Text("Settings.DriveDisconnected");
            GoogleDriveStateText.Foreground = Brush("#7E878F");
        }
        else if (_lastGoogleDriveSyncUtc.HasValue)
        {
            GoogleDriveStateText.Text = LocalizationManager.Format(
                "Settings.DriveLastSyncFormat",
                _lastGoogleDriveSyncUtc.Value.ToLocalTime());
            GoogleDriveStateText.Foreground = Brush("#8FD6A8");
        }
        else
        {
            GoogleDriveStateText.Text = LocalizationManager.Text("Settings.DriveConnected");
            GoogleDriveStateText.Foreground = Brush("#8FD6A8");
        }
    }

    private void RefreshLocalizedPresentation()
    {
        RefreshSettingsPresentation();
        SetLanguageSelection(_settings.Language);

        LandingHistoryList.Items.Refresh();
        if (DataContext is LandingRecord selected)
        {
            DataContext = null;
            DataContext = selected;
        }

        UpdateHistoryPresentation(LandingHistoryList.Items.Count);
        ReplayView.RefreshLocalization();
        UpdateChartDescription(MainChart.Mode);
        UpdateModeUnit(MainChart.Mode);
        UpdateMainLegend(MainChart.Mode);
        ApplyZoom(_zoomStartSeconds, _zoomEndSeconds);
        UpdateRecorderPresentation(_lastRecorderState);
        SetConnectionStatusCore(
            _connectionStatusKey,
            _connectionStatusArguments,
            _connectionStatusIsWarning);
        SetUploadStatusCore(_uploadStatusKey, _uploadStatusArguments, _uploadStatusIsError);
        RefreshAutoStartErrorPresentation();

        if (_pendingDeleteRecord != null)
        {
            UpdateDeleteLandingDescription(_pendingDeleteRecord);
        }
    }

    private void UpdateDeleteLandingDescription(LandingRecord record)
    {
        var contact = record.ContactCount > 1
            ? LocalizationManager.Format(
                "Delete.ContactSuffixFormat",
                record.ContactNumber,
                record.ContactCount)
            : string.Empty;
        DeleteLandingDescription.Text =
            $"{record.AircraftTitle}\n{record.LocationDisplay} · {record.TimestampDisplay}{contact}";
    }

    internal static void UpdateControlTab(bool hasReplay, RadioButton reconstructionTab, RadioButton controlsTab)
    {
        var selected = reconstructionTab.IsChecked == true || controlsTab.IsChecked == true;
        reconstructionTab.Visibility = hasReplay ? Visibility.Visible : Visibility.Collapsed;
        controlsTab.Visibility = hasReplay ? Visibility.Collapsed : Visibility.Visible;
        // Preserve the chosen tab when moving between old and new recordings.
        // Checked invokes the normal mode handler, restoring charts, legend and lanes.
        if (selected) (hasReplay ? reconstructionTab : controlsTab).IsChecked = true;
    }

    private void OnChartModeChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not RadioButton button ||
            button.IsChecked != true ||
            button.Tag is not string modeName ||
            MainChart == null)
        {
            return;
        }

        var replay = modeName == "Reconstruction";
        if (!replay && !Enum.TryParse(modeName, out LandingChartMode _)) return;
        _showReplay = replay;
        ReplayPanel.Visibility = replay ? Visibility.Visible : Visibility.Collapsed;
        MainChart.Visibility = replay ? Visibility.Collapsed : Visibility.Visible;
        ModeUnitText.Visibility = replay ? Visibility.Collapsed : Visibility.Visible;
        ChartWindowButton.Visibility = replay ? Visibility.Collapsed : Visibility.Visible;
        var mode = replay ? MainChart.Mode : (LandingChartMode)Enum.Parse(typeof(LandingChartMode), modeName);
        MainChart.Mode = mode;
        _mainIsolatedSeriesIndex = null;
        UpdateChartDescription(mode);
        UpdateModeUnit(mode);
        UpdateMainLegend(mode);
        ApplyZoom(_zoomStartSeconds, _zoomEndSeconds);
        if (replay) OnChartHoverTimeChanged(ReplayView, new LandingChartHoverEventArgs(ReplayView.CurrentTimeSeconds));
    }

    private void UpdateModeUnit(LandingChartMode mode)
    {
        ModeUnitText.Text = mode switch
        {
            LandingChartMode.VerticalSpeed => "fpm",
            LandingChartMode.LoadFactors => "G",
            LandingChartMode.FlightControls => LocalizationManager.Text("Chart.UnitTravel"),
            LandingChartMode.Attitude => LocalizationManager.Text("Chart.UnitDegrees"),
            LandingChartMode.Power => "% N1",
            LandingChartMode.Gear => LocalizationManager.Text("Chart.UnitStroke"),
            _ => string.Empty,
        };
    }

    private void UpdateChartDescription(LandingChartMode mode)
    {
        if (_showReplay)
        {
            MainChartDescription.Text = string.Empty;
            return;
        }
        var record = DataContext as LandingRecord;
        MainChartDescription.Text = mode switch
        {
            LandingChartMode.VerticalSpeed => LocalizationManager.Text("Chart.DescriptionVertical"),
            LandingChartMode.LoadFactors => LocalizationManager.Text("Chart.DescriptionLoads"),
            LandingChartMode.FlightControls when record?.HasRawPitchInput == true =>
                LocalizationManager.Format(
                    "Chart.DescriptionRawControlsFormat",
                    record.RawPitchInputSourceIndex,
                    record.RawPitchInputLagSeconds * 1000.0),
            LandingChartMode.FlightControls => LocalizationManager.Text("Chart.DescriptionControls"),
            LandingChartMode.Attitude => LocalizationManager.Text("Chart.DescriptionAttitude"),
            LandingChartMode.Power => LocalizationManager.Text("Chart.DescriptionPower"),
            LandingChartMode.Gear => LocalizationManager.Text("Chart.DescriptionGear"),
            _ => string.Empty,
        };
    }

    private void OnMainLegendClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button button ||
            !int.TryParse(button.Tag?.ToString(), out var seriesIndex))
        {
            return;
        }

        _mainIsolatedSeriesIndex = _mainIsolatedSeriesIndex == seriesIndex
            ? null
            : seriesIndex;
        MainChart.SetIsolatedSeries(_mainIsolatedSeriesIndex);
        UpdateMainLegend(MainChart.Mode);
    }

    private void UpdateMainLegend(LandingChartMode mode)
    {
        if (_showReplay)
        {
            MainLegendPanel.Children.Clear();
            MainLegendSeparator.Visibility = Visibility.Collapsed;
            return;
        }
        string[] labels;
        Brush[] brushes;
        bool[] dashed;
        var record = DataContext as LandingRecord;

        switch (mode)
        {
            case LandingChartMode.VerticalSpeed:
                labels = new[]
                {
                    LocalizationManager.Text("Chart.Aircraft"),
                    LocalizationManager.Text("Chart.VsiLagged"),
                    LocalizationManager.Text("Chart.SurfaceClosure"),
                };
                brushes = new[] { LandingChart.SeriesBrushAt(0), LandingChart.SeriesBrushAt(2), LandingChart.SeriesBrushAt(1) };
                dashed = new[] { false, false, true };
                if (record?.HasSurfaceLatchData != true)
                {
                    labels = labels.Take(2).ToArray();
                    brushes = brushes.Take(2).ToArray();
                    dashed = dashed.Take(2).ToArray();
                }
                break;
            case LandingChartMode.LoadFactors:
                labels = new[]
                {
                    LocalizationManager.Text("Chart.Vertical"),
                    LocalizationManager.Text("Chart.Longitudinal"),
                    LocalizationManager.Text("Chart.Lateral"),
                };
                brushes = new[] { LandingChart.SeriesBrushAt(0), LandingChart.SeriesBrushAt(1), LandingChart.SeriesBrushAt(2) };
                dashed = new[] { false, false, false };
                break;
            case LandingChartMode.FlightControls:
                labels = new[]
                {
                    LocalizationManager.Text(record?.HasRawPitchInput == true ? "Chart.PitchRaw" : "Chart.PitchSim"),
                    LocalizationManager.Text("Chart.Roll"),
                    LocalizationManager.Text("Chart.Yaw"),
                };
                brushes = new[] { LandingChart.SeriesBrushAt(0), LandingChart.SeriesBrushAt(1), LandingChart.SeriesBrushAt(2) };
                dashed = new[] { false, false, false };
                break;
            case LandingChartMode.Attitude:
                labels = new[]
                {
                    LocalizationManager.Text("Chart.Pitch"),
                    LocalizationManager.Text("Chart.Bank"),
                    LocalizationManager.Text("Chart.Aoa"),
                };
                brushes = new[] { LandingChart.SeriesBrushAt(0), LandingChart.SeriesBrushAt(1), LandingChart.SeriesBrushAt(2) };
                dashed = new[] { false, false, false };
                break;
            case LandingChartMode.Power:
                labels = record?.Engines
                    .Select(engine => LocalizationManager.Format("Chart.EngineFormat", engine.EngineNumber))
                    .ToArray() ?? Array.Empty<string>();
                brushes = labels.Select((_, index) => LandingChart.SeriesBrushAt(index)).ToArray();
                dashed = labels.Select(_ => false).ToArray();
                break;
            case LandingChartMode.Gear:
                var gearSeries = LandingGearSeriesBuilder.Build(record);
                labels = gearSeries.Select(LandingGearSeriesBuilder.DisplayName).ToArray();
                brushes = labels.Select((_, index) => LandingChart.SeriesBrushAt(index)).ToArray();
                dashed = labels.Select(_ => false).ToArray();
                break;
            default:
                return;
        }

        MainLegendPanel.Children.Clear();
        MainLegendSeparator.Visibility = labels.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        for (var index = 0; index < labels.Length; index++)
        {
            var line = new Line
            {
                X1 = 0,
                X2 = 14,
                Y1 = 0,
                Y2 = 0,
                Stroke = brushes[index],
                StrokeThickness = 2,
                StrokeDashArray = dashed[index] ? new DoubleCollection { 4, 3 } : null,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = labels[index],
                FontSize = 11,
                Foreground = (Brush)FindResource("MutedTextBrush"),
                Margin = new Thickness(7, 0, 0, 0),
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(line);
            content.Children.Add(label);
            var button = new Button
            {
                Tag = index,
                Style = (Style)FindResource("ChartLegendButtonStyle"),
                Margin = new Thickness(index == 0 ? 0 : 22, 0, 0, 0),
                Content = content,
                Opacity = !_mainIsolatedSeriesIndex.HasValue || _mainIsolatedSeriesIndex == index ? 1.0 : 0.32,
                ToolTip = labels[index],
            };
            button.Click += OnMainLegendClick;
            MainLegendPanel.Children.Add(button);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _messageSource = HwndSource.FromHwnd(handle);
        _messageSource?.AddHook(WindowProcedure);
        _recorder = new SimConnectLandingRecorder(handle);
        _recorder.StatusChanged += OnRecorderStatusChanged;
        if (Environment.GetEnvironmentVariable(App.SimulatorAutoStartEnvironmentVariable) == "1")
        {
            _recorder.SimulatorExited += OnSimulatorExited;
        }
        _recorder.EpisodeStarted += OnEpisodeStarted;
        _recorder.EpisodeCompleted += OnEpisodeCompleted;
        _recorder.AirportFacilitiesUpdated += OnAirportFacilitiesUpdated;
        // Episode-owned facility tasks update both history and the report using
        // one two-stage decision. Global runway notifications cannot do that.
        _recorder.AircraftGroundStateChanged += OnAircraftGroundStateChanged;
        _recorder.SeedAirportFacilities(_airportFacilities);
        _recorder.Start();
    }

    private void OnSimulatorExited(object? sender, EventArgs eventArgs)
    {
        // Leave the SimConnect callback before disposing the recorder and window.
        Dispatcher.BeginInvoke(new Action(() => { if (!_isClosed) Close(); }));
    }

    private void OnEpisodeStarted(object? sender, LandingEpisodeStartedEventArgs eventArgs)
    {
        _lastLandingBugReport.BeginEpisode(eventArgs.EpisodeId);
        SetUploadStatus("Footer.ReportWaiting");
        RefreshBugReportButton();
    }

    private IntPtr WindowProcedure(IntPtr windowHandle, int message, IntPtr wordParameter, IntPtr longParameter, ref bool handled)
    {
        return _recorder?.HandleWindowMessage(message, ref handled) ?? IntPtr.Zero;
    }

    private void OnRecorderStatusChanged(object? sender, RecorderStatusEventArgs eventArgs)
    {
        SetConnectionStatus(eventArgs.MessageKey, eventArgs.MessageArguments);
        UpdateRecorderPresentation(eventArgs.State);
        if (string.Equals(eventArgs.MessageKey, "Recorder.ReplayActive", StringComparison.Ordinal))
        {
            ShowReplayUnsupportedOverlay();
        }
        else if (_recorder?.IsAircraftAirborne == true)
        {
            HideReplayUnsupportedOverlay();
        }
    }

    private void OnAircraftGroundStateChanged(object? sender, AircraftGroundStateEventArgs eventArgs)
    {
        if (!eventArgs.OnGround)
        {
            HideReplayUnsupportedOverlay();
        }
    }

    private void ShowReplayUnsupportedOverlay()
    {
        ReplayUnsupportedOverlay.Visibility = Visibility.Visible;
        ReplayUnsupportedOverlay.Focus();
        Keyboard.Focus(CloseReplayUnsupportedButton);
    }

    private void HideReplayUnsupportedOverlay()
    {
        if (ReplayUnsupportedOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        ReplayUnsupportedOverlay.Visibility = Visibility.Collapsed;
        Keyboard.Focus(LandingHistoryList);
    }

    private void OnCloseReplayUnsupportedClick(object sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        HideReplayUnsupportedOverlay();
    }

    private void OnReplayOverlayBackgroundClick(object sender, MouseButtonEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        HideReplayUnsupportedOverlay();
    }

    private void OnReplayDialogClick(object sender, MouseButtonEventArgs eventArgs)
    {
        eventArgs.Handled = true;
    }

    private void OnReplayOverlayKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape)
        {
            return;
        }

        eventArgs.Handled = true;
        HideReplayUnsupportedOverlay();
    }

    private void UpdateRecorderPresentation(RecorderState state)
    {
        _lastRecorderState = state;
        RecorderModeText.Text = state switch
        {
            RecorderState.Connected => LocalizationManager.Text("Recorder.Armed"),
            RecorderState.Recording => LocalizationManager.Text("Recorder.Recording"),
            RecorderState.Error => LocalizationManager.Text("Recorder.Error"),
            _ => LocalizationManager.Text("Top.Offline"),
        };
        ConnectionStatusDot.Fill = state switch
        {
            RecorderState.Connected => Brush("#8FD6A8"),
            RecorderState.Recording => Brush("#FF7A45"),
            RecorderState.Error => Brush("#FF8A6A"),
            _ => Brush("#D9C46A"),
        };
    }

    private async void OnEpisodeCompleted(object? sender, LandingEpisodeEventArgs eventArgs)
    {
        var episodeTimestampUtc = DateTime.UtcNow;
        Task<ProcessedEpisode> processingTask;
        lock (_episodeProcessingGate)
        {
            var predecessor = _episodePersistenceTask;
            var knownFacilities = _airportFacilities;
            processingTask = ProcessEpisodeAfterAsync(
                predecessor,
                eventArgs,
                knownFacilities,
                episodeTimestampUtc);
            _episodePersistenceTask = processingTask;
        }
        var reportTask = QueueEpisodeReport(processingTask, eventArgs, episodeTimestampUtc);

        try
        {
            var processed = await processingTask;
            if (_isClosed || eventArgs.IsDiagnosticOnly)
            {
                return;
            }

            if (processed.ReplayLike)
            {
                UpdateRecorderPresentation(RecorderState.Connected);
                SetConnectionWarning("Recorder.ReplayIgnored");
                ShowReplayUnsupportedOverlay();
                return;
            }

            // A facility refresh can finish while the episode is being analyzed.
            // Keep the newest in-memory entries and resolve this just-saved landing
            // again so an earlier episode snapshot cannot roll the cache backward
            // or leave the landing as Unknown until the next application start.
            _airportFacilities = MergeAirportFacilities(
                processed.AirportFacilities,
                _airportFacilities);
            var airportUpdates = new List<LandingRecord>();
            if (eventArgs.EpisodeId >= _runwayRecordsEpisodeId)
            {
                _runwayRecordsEpisodeId = eventArgs.EpisodeId;
                _runwayRecords = processed.Records;
            }
            foreach (var record in processed.Records)
            {
                if (TryResolveAirport(record, _airportFacilities))
                {
                    airportUpdates.Add(record);
                }
            }
            if (airportUpdates.Count > 0)
            {
                Task persistence;
                lock (_airportMetadataPersistenceGate)
                {
                    persistence = AirportMetadataPersistence.SaveAfterAsync(_airportMetadataPersistenceTask,
                        _airportFacilityRepository, _airportFacilityRepositoryGate, _repository,
                        _landingRepositoryGate, _airportFacilities, airportUpdates);
                    _airportMetadataPersistenceTask = persistence;
                }
                await persistence;
                if (_isClosed) return;
            }
            if (processed.Records.Count == 0)
            {
                UpdateRecorderPresentation(RecorderState.Connected);
                SetConnectionWarning("Recorder.NoTouchdown");
                return;
            }

            foreach (var record in processed.Records.Where(record => !_locallyRemovedHistoryIds.Contains(record.Id)))
            {
                _loadedDetails[record.Id] = record;
            }

            AddLandingRecords(processed.Records);
            QueueGoogleDriveSync();
            if (processed.Records.Count == 1)
            {
                SetConnectionStatus("Recorder.LandingSaved");
            }
            else
            {
                SetConnectionStatus("Recorder.ContactsSavedFormat", processed.Records.Count);
            }
            UpdateRecorderPresentation(RecorderState.Connected);
            var candidate = await reportTask;
            if (!_isClosed && candidate != null)
            {
                // The report worker persisted this same terminal facility choice.
                // Update only this episode; a newer landing must not steal it.
                foreach (var update in candidate.Records)
                {
                    foreach (var record in _landings.Concat(processed.Records).Where(record => record.Id == update.Id))
                        CopyRunwayMetadata(update, record);
                    if (_loadedDetails.TryGetValue(update.Id, out var detail)) CopyRunwayMetadata(update, detail);
                }
                LandingHistoryList.Items.Refresh();
                if (LandingHistoryList.SelectedItem is LandingRecord selected && candidate.Records.Any(record => record.Id == selected.Id))
                    SelectRecord(selected);
                QueueGoogleDriveSync();
            }
            if (!_isClosed && candidate != null && _lastLandingBugReport.TryRetain(candidate))
            {
                SetUploadStatus("Footer.ReportReady");
                RefreshBugReportButton();
            }
        }
        catch (Exception exception)
        {
            if (_isClosed || eventArgs.IsDiagnosticOnly)
            {
                return;
            }

            SetConnectionStatus("Recorder.AnalysisFailedFormat", exception.Message);
            UpdateRecorderPresentation(RecorderState.Error);
        }
    }

    private async Task<ProcessedEpisode> ProcessEpisodeAfterAsync(
        Task predecessor,
        LandingEpisodeEventArgs eventArgs,
        IReadOnlyList<AirportFacility> knownFacilities,
        DateTime episodeTimestampUtc)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
        }
        catch
        {
            // A failed episode must not poison persistence of later landings.
        }

        return await Task.Run(() => ProcessEpisodeAsync(
                eventArgs,
                knownFacilities,
                episodeTimestampUtc))
            .ConfigureAwait(false);
    }

    private async Task<ProcessedEpisode> ProcessEpisodeAsync(
        LandingEpisodeEventArgs eventArgs,
        IReadOnlyList<AirportFacility> knownFacilities,
        DateTime episodeTimestampUtc)
    {
        // Keep the capture's raw frames untouched for diagnostics. Every consumer
        // below (geometry, contact/G analysis, controls and 3D storage) must use
        // the same active-time sequence with confirmed SDK pauses removed.
        var prepared = TelemetryAnalysisFrames.Prepare(eventArgs.Samples);
        var samples = TelemetryDeduplicator.Deduplicate(prepared);
        var journal = eventArgs.DecisionJournal ?? new TelemetryDecisionJournal();
        var time = samples.Count == 0 ? 0 : samples[samples.Count - 1].SimulationTimeSeconds;
        journal.Record(time, "analysis", "frames_deduplicated", "Excluded confirmed SDK pauses from the analysis timeline, then kept the latest same-time frame without erasing unknown physics interruptions.",
            new Dictionary<string, double> { ["received"] = eventArgs.Samples.Count, ["analysis_frames"] = prepared.Count, ["retained"] = samples.Count });
        if (eventArgs.IsDiagnosticOnly || samples.Count == 0)
        {
            // Record the rejection without loading aircraft configuration or touching landing history.
            TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal });
            journal.Record(time, "analysis", "diagnostic_only", "Retained raw and decisions; no landing is saved.");
            return ProcessedEpisode.Completed(knownFacilities, Array.Empty<LandingRecord>(), journal.Snapshot());
        }
        // The episode carries a point-in-time copy of the recorder's facility
        // list. Use it for this analysis, but never write it back to the shared
        // cache: a newer facilities event may already have been persisted while
        // geometry analysis was running, and this stale snapshot must not win.
        var facilities = MergeAirportFacilities(knownFacilities, eventArgs.AirportFacilities);

        var savedRecords = new List<LandingRecord>();
        var anyReplay = false;
        var anyAnalyzed = false;
        foreach (var rawSegment in TelemetryKinematicDiscontinuity.SplitPhysicalSegments(eventArgs.Samples))
        {
            var segment = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(rawSegment, journal));
            if (segment.Count == 0) continue;
            if (ReplayTelemetryDetector.IsReplayLike(segment))
            {
                journal.Record(TelemetryAnalysisFrames.TimeOf(segment[0]), "analysis", "replay_rejected",
                    "Kinematic consistency check rejected this physical segment as replay-like; other segments remain independent.");
                anyReplay = true;
                continue;
            }
            anyAnalyzed = true;
            // Datum recovery from telemetry is also segment-local: not just the
            // contact detector, but the geometry options themselves must never
            // be calibrated using another flight's positions or rotations.
            var analysisOptions = await _flightModelGeometryResolver.CreateAnalysisOptionsAsync(
                    eventArgs.AircraftTitle, eventArgs.AircraftType, eventArgs.AircraftModel, segment)
                .ConfigureAwait(false);
            analysisOptions ??= new TouchdownAnalysisOptions();
            analysisOptions.DecisionJournal = journal;
            var touchdowns = TouchdownAnalysis.Analyze(segment, analysisOptions);
            var segmentIdentity = Guid.NewGuid().ToString("N");
            foreach (var touchdown in touchdowns)
            {
                var aircraftType = eventArgs.AircraftType == "unknown" ? eventArgs.AircraftModel : eventArgs.AircraftType;
                var record = LandingRecordFactory.Create(
                    touchdown, rawSegment, eventArgs.AircraftTitle, aircraftType,
                    contactCount: touchdowns.Count(contact => contact.EpisodeNumber == touchdown.EpisodeNumber),
                    timestampUtc: episodeTimestampUtc,
                    episodeEndSimulationTimeSeconds: TelemetryAnalysisFrames.TimeOf(segment[segment.Count - 1]));
                record.RecordingEpisodeId = segmentIdentity + ":" + touchdown.EpisodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                record.Simulator = eventArgs.Simulator;
                record.ControlInputSources = eventArgs.ControlInputSources.ToList();
                TryResolveAirport(record, facilities);
                lock (_landingRepositoryGate) _repository.Save(record);
                savedRecords.Add(record);
            }
        }

        if (anyReplay && !anyAnalyzed) return ProcessedEpisode.Replay(journal.Snapshot());

        journal.Record(time, "analysis", "episode_analyzed", "Finished analysis of the retained landing episode.",
            new Dictionary<string, double> { ["contacts"] = savedRecords.Count });
        var decisions = journal.Snapshot();
        return ProcessedEpisode.Completed(facilities, savedRecords, decisions);
    }

    private void SetConnectionStatus(string key, params object[] arguments)
    {
        SetConnectionStatusCore(key, arguments, false);
    }

    private void SetConnectionWarning(string key, params object[] arguments)
    {
        SetConnectionStatusCore(key, arguments, true);
    }

    private void SetConnectionStatusCore(string key, object[]? arguments, bool isWarning)
    {
        _connectionStatusKey = key;
        _connectionStatusArguments = arguments ?? Array.Empty<object>();
        _connectionStatusIsWarning = isWarning;
        ConnectionStatusText.Text = LocalizationManager.Format(key, _connectionStatusArguments);
        if (isWarning)
        {
            ConnectionStatusDot.Fill = Brush("#FF8A6A");
        }
    }

    private void AddLandingRecords(IReadOnlyList<LandingRecord> records)
    {
        records = records.Where(record => !_locallyRemovedHistoryIds.Contains(record.Id)).ToArray();
        if (records.Count == 0)
        {
            return;
        }

        var addedIds = new HashSet<string>(records.Select(record => record.Id), StringComparer.Ordinal);
        var newestFirst = records
            .OrderByDescending(record => record.TimestampUtc)
            .ThenByDescending(record => record.ContactNumber)
            .ToArray();
        _landings = newestFirst
            .Concat(_landings.Where(record => !addedIds.Contains(record.Id)))
            .OrderByDescending(record => record.TimestampUtc)
            .ThenByDescending(record => record.ContactNumber)
            .ToArray();

        ApplySessionFilter((LandingHistoryList.SelectedItem as LandingRecord)?.Id ?? newestFirst[0].Id);
    }

    private async void OnRunwayFacilitiesUpdated(object? sender, RunwayFacilitiesEventArgs eventArgs)
    {
        if (_isClosed || _recorder == null || eventArgs.EpisodeId != _runwayRecordsEpisodeId) return;
        // Only the contacts whose landing requested this snapshot. Browsing an
        // older record with missing geometry must not silently rewrite history.
        var updates = await PersistRunwaysAsync(_runwayRecords, eventArgs.Runways);
        if (_isClosed) return;
        foreach (var update in updates)
        {
            foreach (var record in _landings.Concat(_runwayRecords).Where(record => record.Id == update.Id))
                CopyRunwayMetadata(update, record);
            if (_loadedDetails.TryGetValue(update.Id, out var detail)) CopyRunwayMetadata(update, detail);
        }
        if (updates.Count == 0) return;
        LandingHistoryList.Items.Refresh();
        if (LandingHistoryList.SelectedItem is LandingRecord selected) SelectRecord(selected);
        QueueGoogleDriveSync();
    }

    internal static bool TryResolveRunway(LandingRecord record, IReadOnlyList<RunwayFacility> facilities) =>
        RunwayResolver.TryApply(record, facilities);

    private async void OnAirportFacilitiesUpdated(object? sender, AirportFacilitiesEventArgs eventArgs)
    {
        if (_isClosed) return;
        _airportFacilities = MergeAirportFacilities(_airportFacilities, eventArgs.Facilities);

        var changedRecords = new List<LandingRecord>();
        foreach (var record in _landings)
        {
            if (!TryResolveAirport(record, _airportFacilities))
            {
                continue;
            }

            changedRecords.Add(record);
            if (_loadedDetails.TryGetValue(record.Id, out var detail))
            {
                detail.Airport = record.Airport;
                detail.Runway = record.Runway;
                detail.AirportDistanceNauticalMiles = record.AirportDistanceNauticalMiles;
            }
        }

        Task persistence;
        lock (_airportMetadataPersistenceGate)
        {
            persistence = AirportMetadataPersistence.SaveAfterAsync(
                _airportMetadataPersistenceTask,
                _airportFacilityRepository,
                _airportFacilityRepositoryGate,
                _repository,
                _landingRepositoryGate,
                eventArgs.Facilities,
                changedRecords);
            _airportMetadataPersistenceTask = persistence;
        }

        if (changedRecords.Count > 0)
        {
            var selected = LandingHistoryList.SelectedItem as LandingRecord;
            LandingHistoryList.Items.Refresh();
            if (selected != null)
            {
                SelectRecord(selected);
            }
        }

        try
        {
            await persistence;
        }
        catch (Exception exception)
        {
            if (!_isClosed) SetConnectionWarning("Recorder.AnalysisFailedFormat", exception.Message);
        }
    }

    private static bool TryResolveAirport(
        LandingRecord record,
        IReadOnlyList<AirportFacility> facilities)
    {
        if (!string.Equals(record.Airport, "Unknown airport", StringComparison.OrdinalIgnoreCase) ||
            !record.HasTouchdownCoordinates)
        {
            return false;
        }

        var airport = AirportResolver.FindNearest(
            record.TouchdownLatitudeDegrees!.Value,
            record.TouchdownLongitudeDegrees!.Value,
            facilities,
            out var distanceNauticalMiles);
        if (airport == null)
        {
            return false;
        }

        record.Airport = airport.Ident;
        record.AirportDistanceNauticalMiles = Math.Round(distanceNauticalMiles, 2);
        return true;
    }

    internal static IReadOnlyList<AirportFacility> MergeAirportFacilities(
        IEnumerable<AirportFacility> baseline,
        IEnumerable<AirportFacility> updates)
    {
        return (baseline ?? Enumerable.Empty<AirportFacility>())
            .Concat(updates ?? Enumerable.Empty<AirportFacility>())
            .GroupBy(facility => facility.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
    }

    private async void OnReportBugClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_isClosed || AutomaticTelemetryUploadEnabled) return;
        var cancellationToken = _lifetimeCancellation.Token;
        var candidate = _lastLandingBugReport.Available();
        if (candidate == null || _bugReportSubmitting)
        {
            return;
        }

        _bugReportSubmitting = true;
        RefreshBugReportButton();
        SetUploadStatus("BugReport.Preparing");
        string? path = null;
        try
        {
            // Persist first. Network preparation must never be able to destroy
            // the only copy of a user-initiated report when the app closes or
            // the next landing sequence replaces the in-memory candidate.
            Task<string> persistenceTask;
            lock (_bugReportPersistenceGate)
            {
                persistenceTask = Task.Run(() => PersistBugReport(
                    _bugReportRepository,
                    _lastLandingBugReport,
                    candidate,
                    DateTime.UtcNow));
                _bugReportPersistenceTask = Task.WhenAll(_bugReportPersistenceTask, persistenceTask);
            }
            path = await persistenceTask;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            TelemetryUploadClient uploadClient;
            try
            {
                uploadClient = await Task.Run(EnsureTelemetryUploadClient);
            }
            catch (Exception)
            {
                SetUploadStatus("BugReport.SavedForRetryFormat", System.IO.Path.GetFileName(path));
                return;
            }

            if (_isClosed || cancellationToken.IsCancellationRequested) return;
            var preparation = await Task.Run(() => uploadClient.PrepareAsync(cancellationToken));
            if (_isClosed || cancellationToken.IsCancellationRequested) return;
            if (preparation.State != TelemetryPreparationState.Ready)
            {
                SetUploadStatus("BugReport.SavedForRetryFormat", System.IO.Path.GetFileName(path));
                StartPendingBugReportRetry(uploadClient);
                return;
            }

            // Queue the newly submitted report before scanning any older
            // backlog. This keeps user-initiated reports ahead of legacy RAW.
            var queued = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var accepted = uploadClient.Enqueue(path);
                cancellationToken.ThrowIfCancellationRequested();
                uploadClient.EnqueueExisting();
                return accepted;
            });
            if (queued)
            {
                SetUploadStatus("BugReport.Queued");
            }
            else
            {
                SetUploadStatus("BugReport.SavedForRetryFormat", System.IO.Path.GetFileName(path));
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application shutdown owns cancellation and presentation teardown.
        }
        catch (Exception exception)
        {
            if (path != null && await Task.Run(() => System.IO.File.Exists(path)))
            {
                _lastLandingBugReport.MarkSubmitted(candidate.EpisodeId);
                SetUploadStatus("BugReport.SavedForRetryFormat", System.IO.Path.GetFileName(path));
            }
            else
            {
                SetUploadError("BugReport.FailedFormat", exception.Message);
            }
        }
        finally
        {
            _bugReportSubmitting = false;
            if (!_lastLandingBugReport.IsActiveEpisode(candidate.EpisodeId))
            {
                SetUploadStatus(
                    _lastLandingBugReport.Available() == null
                        ? "Footer.ReportWaiting"
                        : "Footer.ReportReady");
            }
            RefreshBugReportButton();
        }
    }

    internal static string PersistBugReport(
        BugReportRepository repository,
        LastLandingBugReportBuffer buffer,
        BugReportCandidate candidate,
        DateTime createdUtc)
    {
        var path = repository.Create(candidate, createdUtc);
        buffer.MarkSubmitted(candidate.EpisodeId);
        return path;
    }

    private void RefreshBugReportButton()
    {
        if (_isClosed || ReportBugButton == null)
        {
            return;
        }

        var available = _startupReady && !AutomaticTelemetryUploadEnabled && _lastLandingBugReport.Available() != null;
        UploadStatusText.Visibility = _startupReady && !AutomaticTelemetryUploadEnabled ? Visibility.Visible : Visibility.Collapsed;
        ReportBugButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        var enabled = !_bugReportSubmitting && available;
        ReportBugButton.IsEnabled = enabled;
        ReportBugButton.Opacity = enabled ? 1.0 : 0.42;
    }

    private TelemetryUploadClient EnsureTelemetryUploadClient()
    {
        lock (_telemetryUploadClientGate)
        {
            if (_isClosed || _lifetimeCancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException("The application is closing.");
            }
            if (_telemetryUploadClient != null)
            {
                return _telemetryUploadClient;
            }

            var client = new TelemetryUploadClient(_rawCaptureRepository.RootPath);
            client.StatusChanged += OnTelemetryUploadStatusChanged;
            _telemetryUploadClient = client;
            client.SetAutomaticUploadEnabled(AutomaticTelemetryUploadEnabled);
            return client;
        }
    }

    private void StartPendingBugReportRetry(TelemetryUploadClient? existingClient = null)
    {
        var cancellationToken = _lifetimeCancellation.Token;
        lock (_bugReportRetryGate)
        {
            if (cancellationToken.IsCancellationRequested) return;
            _bugReportRetryRequested = true;
            if (_bugReportRetryRunning) return;
            _bugReportRetryRunning = true;

            _bugReportRetryTask = Task.Run(async () =>
            {
                while (true)
                {
                    lock (_bugReportRetryGate) _bugReportRetryRequested = false;
                    try
                    {
                        var client = existingClient ?? EnsureTelemetryUploadClient();
                        await client.PreparePendingReportsUntilReadyAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Normal application shutdown.
                    }
                    catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
                    lock (_bugReportRetryGate)
                    {
                        // A rapid opt-out/opt-in can request a new scan while the
                        // cancelled enrollment is still unwinding. Coalesce it,
                        // and publish idle atomically with the decision to exit.
                        if (_bugReportRetryRequested && !cancellationToken.IsCancellationRequested) continue;
                        _bugReportRetryRunning = false;
                        return;
                    }
                }
            });
        }
    }

    private void OnTelemetryUploadStatusChanged(object? sender, TelemetryUploadStatusEventArgs eventArgs)
    {
        if (_isClosed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed) return;
            if (_lastLandingBugReport.Available() != null)
            {
                SetUploadStatus("Footer.ReportReady");
            }
            else
            {
                SetUploadStatusCore(eventArgs.MessageKey, eventArgs.MessageArguments, eventArgs.IsError);
            }
        }));
    }

    private void SetUploadStatus(string key, params object[] arguments)
    {
        SetUploadStatusCore(key, arguments, false);
    }

    private void SetUploadError(string key, params object[] arguments)
    {
        SetUploadStatusCore(key, arguments, true);
    }

    private void SetUploadStatusCore(string key, object[]? arguments, bool isError)
    {
        if (_isClosed) return;
        _uploadStatusKey = key;
        _uploadStatusArguments = arguments ?? Array.Empty<object>();
        _uploadStatusIsError = isError;
        UploadStatusText.Text = LocalizationManager.Format(key, _uploadStatusArguments);
        UploadStatusText.Foreground = isError ? Brush("#FF8A6A") : Brush("#AEB8C2");
    }

    private static int PrimaryGearSeriesIndex(LandingRecord record)
    {
        var gearSeries = LandingGearSeriesBuilder.Build(record);
        if (gearSeries.Count == 0)
        {
            return 0;
        }

        return gearSeries
            .Select((series, index) => new
            {
                Index = index,
                FirstContact = series.FirstContactSeconds,
                Peak = series.PeakCompressionPercent,
            })
            .OrderBy(candidate => candidate.FirstContact)
            .ThenByDescending(candidate => candidate.Peak)
            .First().Index;
    }

    private void UpdateLaneReadouts(LandingRecord record, double timeSeconds)
    {
        if (record.Series.Count == 0)
        {
            HoverTimeText.Text = "—";
            return;
        }

        var point = record.Series[ClosestSeriesPointIndex(record.Series, timeSeconds)];
        HoverTimeText.Text =
            $"{point.TimeSeconds:+0.00;-0.00;0.00}{LocalizationManager.Text("Unit.SecondSuffix")}";
    }

    internal static int ClosestSeriesPointIndex(IReadOnlyList<LandingSeriesPoint> points, double targetTime)
    {
        if (points == null || points.Count == 0)
        {
            throw new ArgumentException("At least one series point is required.", nameof(points));
        }

        if (points.Count == 1 || targetTime <= points[0].TimeSeconds)
        {
            return 0;
        }

        if (targetTime >= points[points.Count - 1].TimeSeconds)
        {
            return points.Count - 1;
        }

        var low = 0;
        var high = points.Count - 1;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].TimeSeconds < targetTime)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return targetTime - points[low].TimeSeconds <= points[high].TimeSeconds - targetTime
            ? low
            : high;
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        _isClosed = true;
        DisposeShutdownResources();
    }

    private sealed class ProcessedEpisode
    {
        private ProcessedEpisode(
            bool replayLike,
            IReadOnlyList<AirportFacility> airportFacilities,
            IReadOnlyList<LandingRecord> records,
            TelemetryDecisionSnapshot? decisions = null)
        {
            ReplayLike = replayLike;
            AirportFacilities = airportFacilities;
            Records = records;
            ReportRecords = records.Select(record => LandingRecordFile.FromRecord(record).ToRecord()).ToArray();
            Decisions = decisions;
        }

        public bool ReplayLike { get; }

        public IReadOnlyList<AirportFacility> AirportFacilities { get; }

        public IReadOnlyList<LandingRecord> Records { get; }
        public TelemetryDecisionSnapshot? Decisions { get; }
        public IReadOnlyList<LandingRecord> ReportRecords { get; }

        public static ProcessedEpisode Replay(TelemetryDecisionSnapshot decisions) => new ProcessedEpisode(
            true,
            Array.Empty<AirportFacility>(),
            Array.Empty<LandingRecord>(), decisions);

        public static ProcessedEpisode Completed(
            IReadOnlyList<AirportFacility> airportFacilities,
            IReadOnlyList<LandingRecord> records,
            TelemetryDecisionSnapshot decisions) =>
            new ProcessedEpisode(false, airportFacilities, records, decisions);
    }

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (eventArgs.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }

    private void OnReleasesLinkNavigate(object sender, RequestNavigateEventArgs eventArgs)
    {
        Process.Start(new ProcessStartInfo(eventArgs.Uri.AbsoluteUri) { UseShellExecute = true });
        eventArgs.Handled = true;
    }
}
