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
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using LandingStats.App.Controls;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.Updates;
using LandingStats.Core;

namespace LandingStats.App;

public partial class MainWindow : Window
{
    private readonly ApplicationServices _services;
    private readonly ApplicationSettingsRepository _settingsRepository;
    private ApplicationSettings _settings = new ApplicationSettings();
    private readonly SimulatorAutoStartManager _autoStartManager;
    private readonly LandingRepository _repository;
    private readonly RawCaptureRepository _rawCaptureRepository;
    private readonly AirportFacilityRepository _airportFacilityRepository;
    private readonly ReleaseUpdater _releaseUpdater;
    private readonly GoogleOAuthTokenStore _googleDriveTokenStore;
    private readonly DeferredGoogleDriveApi _googleDriveApiClient;
    private readonly GoogleDriveBackupService _googleDriveBackupService;
    private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
    private CancellationTokenSource? _googleDriveAuthorizationCancellation;
    private readonly object _landingRepositoryGate;
    private readonly object _airportFacilityRepositoryGate;
    private readonly object _bugReportPersistenceGate = new object();
    private Task _bugReportPersistenceTask = Task.CompletedTask;
    private Task _googleDriveSyncTask = Task.CompletedTask;
    private IReadOnlyList<LandingRecord> _landings = Array.Empty<LandingRecord>();
    private IReadOnlyList<AirportFacility> _airportFacilities = Array.Empty<AirportFacility>();
    private readonly Dictionary<string, LandingRecord> _loadedDetails = new Dictionary<string, LandingRecord>(StringComparer.Ordinal);
    private LandingChart[] _charts = Array.Empty<LandingChart>();
    private bool _showFullApproach;
    private bool _showReplay;
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
    private CloudSyncSummary? _googleDriveWarningSummary;
    private string? _googleDriveErrorKey;
    private ReleaseUpdateResult? _releaseUpdateResult;
    private string _versionAuthorBase = string.Empty;
    private bool _isClosed;
    private StartupTimeline? _startupTimeline;
    private int _firstDetailPresented;

    private bool _googleDriveDisconnecting;
    private bool GoogleDriveBusy => _googleDriveAuthorizing || _googleDriveDisconnecting || _googleDrivePendingOperations > 0;

    public MainWindow() : this(new ApplicationServices()) { }

    internal MainWindow(ApplicationServices services)
    {
        _services = services;
        _settingsRepository = services.SettingsRepository; _autoStartManager = services.AutoStart;
        _repository = services.Landings; _rawCaptureRepository = services.LegacyRaw; _airportFacilityRepository = services.Airports;
        _releaseUpdater = services.Updater; _googleDriveTokenStore = services.Tokens;
        _googleDriveApiClient = services.CloudBinding; _googleDriveBackupService = services.Backup;
        _landingRepositoryGate = services.LandingGate; _airportFacilityRepositoryGate = services.AirportGate;
        services.Settings.Changed += OnCommittedSettingsChanged;
        LocalizationManager.Apply(_settings.Language);
        InitializeComponent();
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetName().Version;
        var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Evgeniy Zaytsev";
        VersionAuthorRun.Text = $"v{version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0} · {company}";
        _versionAuthorBase = VersionAuthorRun.Text;
        _charts = new[] { MainChart, MiniGChart, MiniAltitudeChart };
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
                SetConnectionWarning("Runtime.StartFailedFormat", exception.Message);
                if (!_startupReady && _historyLoading)
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
        _startupTimeline = new StartupTimeline();
        _startupTimeline.Mark("window_loaded");
        ReleaseUpdater.BeginCompletedUpdateCleanup(Environment.GetCommandLineArgs());
        var initialState = LoadInitialStateAsync();
        _initializationPersistenceTask = initialState;
        await InitializeWindowContentAsync(async () =>
        {
            var loaded = await initialState;
            _startupTimeline.Mark("initial_state_ready");
            if (_isClosed) return;
            _settings = loaded.Settings;
            ApplyTelemetryPreference(_settings.AutomaticTelemetryUpload);
            _settingsPersistedAndReadable = loaded.SettingsReadable;
            LocalizationManager.Apply(_settings.Language);
            // Local settings remain available even while engine startup waits or fails.
            _startupReady = true;
            SettingsButton.IsEnabled = true;
            RefreshLocalizedPresentation();
            _ = BeginRuntimeStartup();
        });
        if (_isClosed) return;
        var state = await initialState;
        _startupReady = true;
        _startupTimeline.Mark("startup_ready");
        SettingsButton.IsEnabled = true;
        RefreshLocalizedPresentation();
        if (state.PendingUploads)
        {
            StartPendingBugReportRetry();
        }
        var updateCheck = CheckForUpdateAsync();
        if (ShouldRestoreSettingsBeforeOnboarding(_googleDriveTokenStore.HasRefreshToken(), _settingsPersistedAndReadable))
            await _runtimeInitializationTask;

        Task googleDriveStartupSync = Task.CompletedTask;
        if (_runtime?.Ready == true && _googleDriveTokenStore.HasRefreshToken())
        {
            googleDriveStartupSync = RunGoogleDriveSyncAsync(false);
            _googleDriveSyncTask = googleDriveStartupSync;
        }

        // If settings were deleted or unreadable, let an existing Drive backup
        // restore them before onboarding can persist fallback defaults.
        if (ShouldRestoreSettingsBeforeOnboarding(
                _googleDriveTokenStore.HasRefreshToken(),
                _settingsPersistedAndReadable))
        {
            await googleDriveStartupSync;
            if (_isClosed) return;
        }

        if (_isClosed) return;
        await InitializeAutoStartSettingAsync();
        if (_isClosed) return;
        _autoStartInitialized = true; _services.Settings.AutoStartInitialized = true;
        ShowGoogleDrivePromptIfNeeded();
        await googleDriveStartupSync;
        await updateCheck;
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
        var sameRecord = DataContext is LandingRecord currentRecord && currentRecord.Id == detail.Id;
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
        MiniAltitudeChart.Record = detail;
        Timeline.Record = detail;
        if (!sameRecord)
        {
        MiniGChart.SetIsolatedSeries(0);
        _mainIsolatedSeriesIndex = null;
        MainChart.SetIsolatedSeries(null);
        UpdateMainLegend(MainChart.Mode);
        UpdateChartDescription(MainChart.Mode);
        ApplyZoom(_showFullApproach ? null : -6, _showFullApproach ? null : 8);
        }
        UpdateLaneReadouts(detail, sameRecord ? ReplayView.CurrentTimeSeconds : 0);
        if (Interlocked.Exchange(ref _firstDetailPresented, 1) == 0) _startupTimeline?.Mark("first_detail_presented");
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
        MiniAltitudeChart.Record = null;
        Timeline.Record = null;
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

        var signedIn = _googleDriveTokenStore.HasRefreshToken();
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
            var deletion = _services.DeleteLandingAsync(record.Id);
            _localDeletionTask = deletion;
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
            _googleDriveErrorDetail = exception.Message; _googleDriveErrorKey = null;
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
                await _services.Settings.SaveAsync(_settings, snapshot);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            _settings.CopyFrom(_services.Settings.Snapshot.Settings);
            if (!_isClosed)
            {
                LocalizationManager.Apply(_settings.Language);
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
                _autoStartInitialized = true; _services.Settings.AutoStartInitialized = true;
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
            _autoStartInitialized = true; _services.Settings.AutoStartInitialized = true;
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
                await _services.Settings.SaveAsync(
                    _settings, snapshot,
                    () => result = _autoStartManager.SetEnabled(enabled),
                    () => _autoStartManager.SetEnabled(previous == true));
            }
            catch (Exception exception)
            {
                _autoStartErrorDetail = CombineAutoStartErrors(_autoStartLegacyCleanupError, exception.Message);
                return false;
            }
            _settings.CopyFrom(_services.Settings.Snapshot.Settings);
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
        if (IsActive)
        {
            AutoStartPromptOverlay.Focus();
            Keyboard.Focus(DeclineAutoStartButton);
        }
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
        RefreshIntegrationApiSettingsPresentation();
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

        if (!_googleDriveTokenStore.HasRefreshToken())
        {
            _googleDriveAuthorizing = true;
            _googleDriveErrorDetail = null; _googleDriveErrorKey = null;
            _googleDriveWarningDetail = null; _googleDriveWarningSummary = null;
            RefreshGoogleDrivePresentation();
            using var authorizationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _googleDriveAuthorizationCancellation = authorizationCancellation;
            _googleDriveAuthorizationCanceledByUser = false;
            authorizationCancellation.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                var runtime = _runtime ?? throw new InvalidOperationException("Cloud runtime is not ready.");
                await runtime.SignInCloudAsync(authorizationCancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                if (!_googleDriveAuthorizationCanceledByUser)
                {
                    _googleDriveErrorKey = "Settings.DriveAuthorizationTimeout";
                    _googleDriveErrorDetail = LocalizationManager.Text(_googleDriveErrorKey);
                }
                return;
            }
            catch (Exception exception)
            {
                _googleDriveErrorDetail = exception.Message; _googleDriveErrorKey = null;
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
        if (!_isClosed && _googleDriveTokenStore.HasRefreshToken())
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
        var signedIn = _googleDriveTokenStore.HasRefreshToken();
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
        if (IsActive)
        {
            GoogleDrivePromptOverlay.Focus();
            Keyboard.Focus(DeclineGoogleDriveButton);
        }
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
                await _services.Settings.SaveAsync(_settings, snapshot);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            _settings.CopyFrom(_services.Settings.Snapshot.Settings);
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
        if (_googleDriveDisconnecting || SettingsBusy || _settingsOperations.IsStopping || _isClosed)
        {
            return;
        }
        _googleDriveDisconnecting = true; RefreshGoogleDrivePresentation();
        try
        {
            // Drain the sync scope before entering the settings queue: that scope
            // may itself be waiting for an already accepted settings mutation.
            if (_runtime != null) await _runtime.SignOutCloudAsync(_lifetimeCancellation.Token);
            await RunSettingsOperationAsync(async () =>
            {
                try
                {
                    await Task.Run(() => _googleDriveTokenStore.Clear());
                    _lastGoogleDriveSyncUtc = null;
                    _runtimeCloudPhase = CloudSyncPhase.Paused;
                    _googleDriveErrorDetail = null; _googleDriveErrorKey = null;
                    _googleDriveWarningDetail = null; _googleDriveWarningSummary = null;
                    return true;
                }
                catch (Exception exception)
                {
                    _googleDriveErrorDetail = exception.Message; _googleDriveErrorKey = null;
                    return false;
                }
            });
        }
        catch (Exception exception) { _googleDriveErrorDetail = exception.Message; _googleDriveErrorKey = null; }
        finally { _googleDriveDisconnecting = false; if (!_isClosed) RefreshGoogleDrivePresentation(); }
    }

    private void QueueGoogleDriveSync()
    {
        if (_isClosed || !_startupReady || _lifetimeCancellation.IsCancellationRequested || !_googleDriveTokenStore.HasRefreshToken())
        {
            return;
        }
        _googleDriveSyncTask = RunGoogleDriveSyncAsync(false);
    }

    private async Task RunGoogleDriveSyncAsync(bool userInitiated)
    {
        if (_isClosed || !_startupReady || _lifetimeCancellation.IsCancellationRequested || !_googleDriveTokenStore.HasRefreshToken())
        {
            return;
        }

        _googleDrivePendingOperations++;
        if (_googleDrivePendingOperations == 1)
        {
            _googleDriveErrorDetail = null; _googleDriveErrorKey = null;
            _googleDriveWarningDetail = null; _googleDriveWarningSummary = null;
            RefreshGoogleDrivePresentation();
        }
        try
        {
            var selectedId = (LandingHistoryList.SelectedItem as LandingRecord)?.Id;
            var runtime = Volatile.Read(ref _runtime) ?? throw new GoogleDriveNotSignedInException();
            if (userInitiated && !runtime.CloudEnabled) await runtime.SetCloudEnabledAsync(true);
            if (!runtime.CloudEnabled) return;
            var result = await runtime.RequestCloudSyncAsync();
            if (_isClosed)
            {
                return;
            }
            _lastGoogleDriveSyncUtc = DateTime.UtcNow;
            _googleDriveErrorDetail = null; _googleDriveErrorKey = null;
            _googleDriveWarningSummary = CloudSyncSummary.From(result);
            _googleDriveWarningDetail = CloudWarnings(_googleDriveWarningSummary);
            if (result.HistoryChanged)
            {
                InvalidateLoadedHistoryDetails();
                await LoadHistoryAsync(selectedId);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested || _googleDriveDisconnecting || !_googleDriveTokenStore.HasRefreshToken())
        {
        }
        catch (GoogleDriveNotSignedInException)
        {
            _googleDriveErrorKey = "Settings.DriveSignInAgain";
            _googleDriveErrorDetail = LocalizationManager.Text(_googleDriveErrorKey);
        }
        catch (Exception exception)
        {
            _googleDriveErrorDetail = exception.Message; _googleDriveErrorKey = null;
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
        var signedIn = _googleDriveTokenStore.HasRefreshToken();
        GoogleDrivePrimaryButton.Content = LocalizationManager.Text(
            signedIn ? "Settings.DriveSyncNow" : "Settings.DriveConnect");
        GoogleDrivePrimaryButton.IsEnabled = !GoogleDriveBusy && !SettingsBusy && !_settingsOperations.IsStopping;
        GoogleDriveSignOutButton.Content = LocalizationManager.Text(
            _googleDriveAuthorizing ? "Common.Cancel" : "Settings.DriveDisconnect");
        GoogleDriveSignOutButton.Visibility = signedIn || _googleDriveAuthorizing
            ? Visibility.Visible
            : Visibility.Collapsed;
        GoogleDriveSignOutButton.IsEnabled = !_settingsOperations.IsStopping &&
            (_googleDriveAuthorizing || (!_googleDriveDisconnecting && !SettingsBusy));
        GoogleDriveStateText.ToolTip = _lastGoogleDriveSyncUtc.HasValue && signedIn
            ? LocalizationManager.Format("Settings.DriveLastSyncFormat", _lastGoogleDriveSyncUtc.Value.ToLocalTime()) : null;

        if (!string.IsNullOrWhiteSpace(_googleDriveErrorDetail))
        {
            GoogleDriveStateText.Text = LocalizationManager.Format(
                _runtimeCloudPhase == CloudSyncPhase.Retrying ? "Settings.DriveRetryingFormat" : "Settings.DriveErrorFormat",
                _googleDriveErrorDetail!);
            GoogleDriveStateText.Foreground = Brush("#FF8A6A");
        }
        else if (GoogleDriveBusy || signedIn && _runtimeCloudPhase == CloudSyncPhase.Syncing)
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
        else if (_runtime?.CloudEnabled != true)
        {
            GoogleDriveStateText.Text = LocalizationManager.Text("Settings.DrivePaused");
            GoogleDriveStateText.Foreground = Brush("#D9C46A");
        }
        else if (_runtimeCloudPhase == CloudSyncPhase.Retrying)
        {
            GoogleDriveStateText.Text = LocalizationManager.Text("Settings.DriveRetrying");
            GoogleDriveStateText.Foreground = Brush("#D9C46A");
        }
        else if (!string.IsNullOrWhiteSpace(_googleDriveWarningDetail))
        {
            GoogleDriveStateText.Text = LocalizationManager.Format("Settings.DriveWarningFormat", _googleDriveWarningDetail!);
            GoogleDriveStateText.Foreground = Brush("#D9C46A");
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
        _googleDriveWarningDetail = CloudWarnings(_googleDriveWarningSummary);
        if (_googleDriveErrorKey != null) _googleDriveErrorDetail = LocalizationManager.Text(_googleDriveErrorKey);
        if (_telemetrySettingsError != null) SettingsTelemetryErrorText.Text = LocalizationManager.Format("Settings.SaveErrorFormat", _telemetrySettingsError);
        RefreshUpdatePresentation();
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
        if (DataContext is LandingRecord current) UpdateLaneReadouts(current, ReplayView.CurrentTimeSeconds);

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
            $"{record.AircraftTitle}\n{new LandingStats.App.Presentation.LandingPresentation(record).LocationDisplay} · {new LandingStats.App.Presentation.LandingPresentation(record).TimestampDisplay}{contact}";
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
            LandingChartMode.Power => "%",
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
            LandingChartMode.FlightControls when record?.ControllerTimeline != null && ReplayPresentationData.PitchValues(record) != null => LocalizationManager.Text("Chart.DescriptionCatalogControls"),
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
                    LocalizationManager.Text((record?.HasRawPitchInput == true || record?.ControllerTimeline != null && ReplayPresentationData.PitchValues(record) != null) ? "Chart.PitchRaw" : "Chart.PitchSim"),
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

    private void OnSimulatorExited()
    {
        // Finish the runtime snapshot projection before starting window shutdown.
        Dispatcher.BeginInvoke(new Action(() => { if (!_isClosed) { _stopReason = GuardianStopReason.SimulatorQuit; Close(); } }));
    }

    private void OnRecorderStatusChanged(RecorderStatusEventArgs eventArgs)
    {
        SetConnectionStatus(eventArgs.MessageKey, eventArgs.MessageArguments);
        UpdateRecorderPresentation(eventArgs.State);
        // Live replay detection belongs in the persistent status bar; do not
        // interrupt browsing an earlier landing with a modal overlay.
        if (!string.Equals(eventArgs.MessageKey, "Recorder.ReplayActive", StringComparison.Ordinal))
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



    internal static IReadOnlyList<AirportFacility> MergeAirportFacilities(
        IEnumerable<AirportFacility> baseline, IEnumerable<AirportFacility> updates) => AirportMetadata.Merge(baseline, updates);

    private async void OnReportBugClick(object sender, RoutedEventArgs eventArgs)
    {
        var runtime = Volatile.Read(ref _runtime);
        if (_isClosed || AutomaticTelemetryUploadEnabled || runtime == null) return;
        await SubmitRuntimeReportAsync(runtime);
    }

    private void RefreshBugReportButton()
    {
        if (_isClosed || ReportBugButton == null)
        {
            return;
        }

        var available = _startupReady && !AutomaticTelemetryUploadEnabled &&
            _runtimeReportEpisode != Guid.Empty;
        UploadStatusText.Visibility = _startupReady && !AutomaticTelemetryUploadEnabled ? Visibility.Visible : Visibility.Collapsed;
        ReportBugButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        var enabled = !_bugReportSubmitting && available;
        ReportBugButton.IsEnabled = enabled;
        ReportBugButton.Opacity = enabled ? 1.0 : 0.42;
    }

    private void StartPendingBugReportRetry() => Volatile.Read(ref _runtime)?.WakeDelivery();

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
        if (points == null || points.Count == 0) throw new ArgumentException("At least one series point is required.", nameof(points));
        return RecordedTimeSearch.Nearest(points.Count, index => points[index].TimeSeconds, targetTime);
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        _isClosed = true;
        DisposeShutdownResources();
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

        if (Mouse.LeftButton == MouseButtonState.Pressed)
            try { DragMove(); } catch (InvalidOperationException error) { Debug.WriteLine(error); }
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
        try { Process.Start(new ProcessStartInfo(eventArgs.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception || error is InvalidOperationException)
        { Debug.WriteLine(error); SetConnectionWarning("Common.OpenLinkFailedFormat", error.Message); }
        eventArgs.Handled = true;
    }
}
