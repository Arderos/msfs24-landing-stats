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
    private readonly ApplicationSettings _settings;
    private readonly SimulatorAutoStartManager _autoStartManager = new SimulatorAutoStartManager();
    private readonly LandingRepository _repository = new LandingRepository();
    private readonly RawCaptureRepository _rawCaptureRepository = new RawCaptureRepository();
    private readonly BugReportRepository _bugReportRepository;
    private readonly LastLandingBugReportBuffer _lastLandingBugReport = new LastLandingBugReportBuffer();
    private readonly AirportFacilityRepository _airportFacilityRepository = new AirportFacilityRepository();
    private readonly FlightModelGeometryResolver _flightModelGeometryResolver = new FlightModelGeometryResolver();
    private readonly ReleaseUpdater _releaseUpdater = new ReleaseUpdater();
    private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
    private readonly object _episodeProcessingGate = new object();
    private readonly object _landingRepositoryGate = new object();
    private readonly object _airportFacilityRepositoryGate = new object();
    private readonly object _telemetryUploadClientGate = new object();
    private readonly object _bugReportRetryGate = new object();
    private readonly object _bugReportPersistenceGate = new object();
    private Task _episodePersistenceTask = Task.CompletedTask;
    private Task _bugReportRetryTask = Task.CompletedTask;
    private Task _bugReportPersistenceTask = Task.CompletedTask;
    private TelemetryUploadClient? _telemetryUploadClient;
    private IReadOnlyList<LandingRecord> _landings = Array.Empty<LandingRecord>();
    private IReadOnlyList<AirportFacility> _airportFacilities = Array.Empty<AirportFacility>();
    private readonly Dictionary<string, LandingRecord> _loadedDetails = new Dictionary<string, LandingRecord>(StringComparer.Ordinal);
    private HwndSource? _messageSource;
    private SimConnectLandingRecorder? _recorder;
    private LandingChart[] _charts = Array.Empty<LandingChart>();
    private bool _showFullApproach;
    private int _primaryGearSeriesIndex;
    private int? _mainIsolatedSeriesIndex;
    private bool _changingLanguageSelection;
    private bool _changingAutoStartSelection;
    private bool _bugReportSubmitting;
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
    private string? _autoStartErrorDetail;
    private bool _isClosed;

    public MainWindow()
    {
        _bugReportRepository = new BugReportRepository(_rawCaptureRepository.RootPath);
        _settingsRepository = new ApplicationSettingsRepository();
        _settings = _settingsRepository.Load();
        LocalizationManager.Apply(_settings.Language);
        InitializeComponent();
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetName().Version;
        var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Evgeniy Zaytsev";
        VersionAuthorRun.Text = $"v{version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0} · {company}";
        lock (_airportFacilityRepositoryGate)
        {
            _airportFacilities = _airportFacilityRepository.Load();
        }
        LoadHistory();
        _charts = new[] { MainChart, MiniGChart, MiniPowerChart, MiniGearChart };
        foreach (var chart in _charts)
        {
            chart.HoverTimeChanged += OnChartHoverTimeChanged;
            chart.ZoomRangeSelected += OnChartZoomRangeSelected;
        }

        if (LandingHistoryList.SelectedItem is LandingRecord selected)
        {
            SelectRecord(selected);
        }

        VerticalRateTab.IsChecked = true;
        MainChart.Mode = LandingChartMode.VerticalSpeed;
        _mainIsolatedSeriesIndex = null;
        MainChart.SetIsolatedSeries(null);
        MainChartDescription.Text = LocalizationManager.Text("Chart.DescriptionVertical");
        ModeUnitText.Text = "fpm";
        UpdateMainLegend(LandingChartMode.VerticalSpeed);
        RefreshSettingsPresentation();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs eventArgs)
    {
        ReleaseUpdater.BeginCompletedUpdateCleanup(Environment.GetCommandLineArgs());
        if (TelemetryUploadClient.HasPendingUploadFiles(_rawCaptureRepository.RootPath))
        {
            StartPendingBugReportRetry();
        }
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var result = await _releaseUpdater.CheckAndInstallAsync(version, _lifetimeCancellation.Token);
        VersionAuthorText.ToolTip = result.Path == null ? result.Message : result.Message + "\n" + result.Path;
        if (result.State == ReleaseUpdateState.UpdateStarted && result.Version != null)
        {
            VersionAuthorRun.Text += LocalizationManager.Format("Update.StatusUpdatingFormat", result.Version);
            VersionAuthorText.Foreground = Brush("#8FD6A8");
            Application.Current.Shutdown();
            return;
        }
        else if (result.State == ReleaseUpdateState.Rejected)
        {
            VersionAuthorRun.Text += LocalizationManager.Text("Update.StatusRejected");
            VersionAuthorText.Foreground = Brush("#FF8A6A");
        }

        InitializeAutoStartSetting();
    }

    private void LoadHistory()
    {
        List<LandingRecord> stored;
        lock (_landingRepositoryGate)
        {
            stored = _repository.LoadAll().ToList();
        }
        var changedRecords = new List<LandingRecord>();
        foreach (var record in stored)
        {
            var changed = TryResolveAirport(record, _airportFacilities);
            if (changed)
            {
                changedRecords.Add(record);
            }
        }

        lock (_landingRepositoryGate)
        {
            _repository.UpdateSummaries(changedRecords);
        }

        _landings = stored;
        ApplySessionFilter();
        StoragePathText.Text = "%LOCALAPPDATA%\\MSFS Landing Stats\\Landings";
        StoragePathText.ToolTip = _repository.RootPath;
    }

    private void OnHistorySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        if (LandingHistoryList.SelectedItem is not LandingRecord selected)
        {
            return;
        }

        SelectRecord(selected);
    }

    private void SelectRecord(LandingRecord selected)
    {
        var detail = selected;
        if (selected.IsSummaryOnly)
        {
            if (_loadedDetails.TryGetValue(selected.Id, out var cachedDetail))
            {
                detail = cachedDetail;
            }
            else
            {
                lock (_landingRepositoryGate)
                {
                    detail = _repository.LoadDetail(selected) ?? selected;
                }
                if (!detail.IsSummaryOnly)
                {
                    if (detail.FormatVersion >= 6 && LandingRecordFactory.RefreshRawPitchInputSelection(detail))
                    {
                        lock (_landingRepositoryGate)
                        {
                            _repository.Save(detail);
                        }
                    }

                    _loadedDetails[selected.Id] = detail;
                }
            }
        }

        if (ReferenceEquals(DataContext, detail))
        {
            // Airport resolution mutates cached details after they have already been bound.
            // LandingRecord is intentionally a storage DTO, not INotifyPropertyChanged, so
            // force a binding refresh when selecting the same instance again.
            DataContext = null;
        }

        DataContext = detail;
        MainChart.Record = detail;
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
    }

    private void OnChartHoverTimeChanged(object? sender, LandingChartHoverEventArgs eventArgs)
    {
        foreach (var chart in _charts)
        {
            chart.SetHoverTime(eventArgs.TimeSeconds);
        }

        if (DataContext is LandingRecord record)
        {
            UpdateLaneReadouts(record, eventArgs.TimeSeconds ?? 0);
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
            chart.SetZoomRange(startSeconds, endSeconds);
        }

        Timeline.SetZoomRange(startSeconds, endSeconds);
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
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _landings
            : _landings.Where(record =>
                record.AircraftTitle.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                record.AircraftType.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                record.Airport.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                record.Runway.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

        LandingHistoryList.ItemsSource = filtered;
        HistoryCountText.Text = filtered.Count.ToString();
        var selected = selectedId == null ? null : filtered.FirstOrDefault(record => record.Id == selectedId);
        LandingHistoryList.SelectedItem = selected ?? filtered.FirstOrDefault();
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
    }

    private void ClearSelectedRecord()
    {
        DataContext = null;
        MainChart.Record = null;
        MiniGChart.Record = null;
        MiniPowerChart.Record = null;
        MiniGearChart.Record = null;
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
        HideDeleteLandingConfirmation();
    }

    private void OnDeleteLandingOverlayKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape)
        {
            return;
        }

        eventArgs.Handled = true;
        HideDeleteLandingConfirmation();
    }

    private void OnConfirmDeleteLandingClick(object sender, RoutedEventArgs eventArgs)
    {
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

        try
        {
            ConfirmDeleteLandingButton.IsEnabled = false;
            lock (_landingRepositoryGate)
            {
                _repository.Delete(record.Id);
            }
            _loadedDetails.Remove(record.Id);
            _landings = _landings
                .Where(candidate => !string.Equals(candidate.Id, record.Id, StringComparison.Ordinal))
                .ToArray();
            HideDeleteLandingConfirmation();
            ApplySessionFilter(preferredSelectionId);
        }
        catch (Exception exception)
        {
            ConfirmDeleteLandingButton.IsEnabled = true;
            DeleteLandingErrorText.Text = LocalizationManager.Format("Delete.ErrorFormat", exception.Message);
            DeleteLandingErrorText.Visibility = Visibility.Visible;
            Keyboard.Focus(CancelDeleteLandingButton);
        }
    }

    private void HideDeleteLandingConfirmation()
    {
        _pendingDeleteRecord = null;
        DeleteLandingOverlay.Visibility = Visibility.Collapsed;
        ConfirmDeleteLandingButton.IsEnabled = true;
        Keyboard.Focus(LandingHistoryList);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
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

    private void HideSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        Keyboard.Focus(LandingHistoryList);
    }

    private void OnLanguageSelectionChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_changingLanguageSelection ||
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

        var previous = _settings.Language;
        _settings.Language = preference;
        try
        {
            _settingsRepository.Save(_settings);
        }
        catch (Exception exception)
        {
            _settings.Language = previous;
            SetLanguageSelection(previous);
            SettingsErrorText.Text = LocalizationManager.Format("Settings.SaveErrorFormat", exception.Message);
            SettingsErrorText.Visibility = Visibility.Visible;
            return;
        }

        LocalizationManager.Apply(preference);
        SettingsErrorText.Text = string.Empty;
        SettingsErrorText.Visibility = Visibility.Collapsed;
        RefreshLocalizedPresentation();
    }

    private void OnAutoStartSelectionChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_changingAutoStartSelection || !IsLoaded || sender is not CheckBox checkBox)
        {
            return;
        }

        var enabled = checkBox.IsChecked == true;
        if (_settings.StartWithSimulator.HasValue && _settings.StartWithSimulator.Value == enabled)
        {
            return;
        }

        if (!ApplyAutoStartPreference(enabled))
        {
            SetAutoStartSelection(_settings.StartWithSimulator == true);
        }
    }

    private void OnEnableAutoStartClick(object sender, RoutedEventArgs eventArgs)
    {
        SetAutoStartPromptBusy(true);
        if (ApplyAutoStartPreference(true))
        {
            HideAutoStartPrompt();
        }
        else
        {
            SetAutoStartPromptBusy(false);
        }
    }

    private void OnDeclineAutoStartClick(object sender, RoutedEventArgs eventArgs)
    {
        SetAutoStartPromptBusy(true);
        if (ApplyAutoStartPreference(false))
        {
            HideAutoStartPrompt();
        }
        else
        {
            SetAutoStartPromptBusy(false);
        }
    }

    private void InitializeAutoStartSetting()
    {
        if (!_settings.StartWithSimulator.HasValue)
        {
            ShowAutoStartPrompt();
            return;
        }

        try
        {
            var result = _autoStartManager.SetEnabled(_settings.StartWithSimulator.Value);
            _autoStartProfileCount = result.ConfigurationPaths.Count;
            _autoStartErrorDetail = null;
        }
        catch (Exception exception)
        {
            _autoStartErrorDetail = exception.Message;
        }

        RefreshSettingsPresentation();
    }

    private bool ApplyAutoStartPreference(bool enabled)
    {
        var previous = _settings.StartWithSimulator;
        AutoStartOperationResult result;
        try
        {
            result = _autoStartManager.SetEnabled(enabled);
        }
        catch (Exception exception)
        {
            _autoStartErrorDetail = exception.Message;
            RefreshAutoStartErrorPresentation();
            return false;
        }

        _settings.StartWithSimulator = enabled;
        try
        {
            _settingsRepository.Save(_settings);
        }
        catch (Exception saveException)
        {
            _settings.StartWithSimulator = previous;
            try
            {
                _autoStartManager.SetEnabled(previous == true);
            }
            catch (Exception rollbackException)
            {
                _autoStartErrorDetail = saveException.Message + " " + rollbackException.Message;
                RefreshAutoStartErrorPresentation();
                return false;
            }

            _autoStartErrorDetail = saveException.Message;
            RefreshAutoStartErrorPresentation();
            return false;
        }

        _autoStartProfileCount = result.ConfigurationPaths.Count;
        _autoStartErrorDetail = null;
        RefreshAutoStartErrorPresentation();
        RefreshSettingsPresentation();
        return true;
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
    }

    private void SetAutoStartPromptBusy(bool busy)
    {
        DeclineAutoStartButton.IsEnabled = !busy;
        EnableAutoStartButton.IsEnabled = !busy;
        SettingsAutoStartCheckBox.IsEnabled = !busy;
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
        SettingsAutoStartStateText.Text = _settings.StartWithSimulator == true
            ? LocalizationManager.Format("Settings.AutoStartEnabledFormat", _autoStartProfileCount)
            : LocalizationManager.Text("Settings.AutoStartDisabled");
        RefreshAutoStartErrorPresentation();
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

    private void OnChartModeChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not RadioButton button ||
            button.IsChecked != true ||
            button.Tag is not string modeName ||
            !Enum.TryParse(modeName, out LandingChartMode mode) ||
            MainChart == null)
        {
            return;
        }

        MainChart.Mode = mode;
        _mainIsolatedSeriesIndex = null;
        UpdateChartDescription(mode);
        UpdateModeUnit(mode);
        UpdateMainLegend(mode);
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
        _recorder.EpisodeStarted += OnEpisodeStarted;
        _recorder.EpisodeCompleted += OnEpisodeCompleted;
        _recorder.AirportFacilitiesUpdated += OnAirportFacilitiesUpdated;
        _recorder.AircraftGroundStateChanged += OnAircraftGroundStateChanged;
        _recorder.SeedAirportFacilities(_airportFacilities);
        _recorder.Start();
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

        try
        {
            var processed = await processingTask;
            if (_isClosed)
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
            foreach (var record in processed.Records)
            {
                if (TryResolveAirport(record, _airportFacilities))
                {
                    airportUpdates.Add(record);
                }
            }
            if (airportUpdates.Count > 0)
            {
                lock (_landingRepositoryGate)
                {
                    _repository.UpdateSummaries(airportUpdates);
                }
            }
            if (processed.Records.Count == 0)
            {
                UpdateRecorderPresentation(RecorderState.Connected);
                SetConnectionWarning("Recorder.NoTouchdown");
                return;
            }

            if (_lastLandingBugReport.TryRetain(new BugReportCandidate(
                eventArgs.EpisodeId,
                eventArgs.Samples,
                eventArgs.Simulator,
                eventArgs.AircraftTitle,
                eventArgs.AircraftType,
                eventArgs.AircraftModel,
                eventArgs.ControlInputSources,
                processed.Records)))
            {
                SetUploadStatus("Footer.ReportReady");
            }
            RefreshBugReportButton();

            foreach (var record in processed.Records)
            {
                _loadedDetails[record.Id] = record;
            }

            AddLandingRecords(processed.Records);
            if (processed.Records.Count == 1)
            {
                SetConnectionStatus("Recorder.LandingSaved");
            }
            else
            {
                SetConnectionStatus("Recorder.ContactsSavedFormat", processed.Records.Count);
            }
            UpdateRecorderPresentation(RecorderState.Connected);
        }
        catch (Exception exception)
        {
            if (_isClosed)
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
        var samples = TelemetryDeduplicator.Deduplicate(eventArgs.Samples);
        if (ReplayTelemetryDetector.IsReplayLike(samples))
        {
            return ProcessedEpisode.Replay();
        }

        // The episode carries a point-in-time copy of the recorder's facility
        // list. Use it for this analysis, but never write it back to the shared
        // cache: a newer facilities event may already have been persisted while
        // geometry analysis was running, and this stale snapshot must not win.
        var facilities = MergeAirportFacilities(knownFacilities, eventArgs.AirportFacilities);

        var analysisOptions = await _flightModelGeometryResolver.CreateAnalysisOptionsAsync(
                eventArgs.AircraftTitle,
                eventArgs.AircraftType,
                eventArgs.AircraftModel,
                samples)
            .ConfigureAwait(false);
        var touchdowns = TouchdownAnalysis.Analyze(samples, analysisOptions);
        var savedRecords = new List<LandingRecord>(touchdowns.Count);
        foreach (var touchdown in touchdowns)
        {
            var aircraftType = eventArgs.AircraftType == "unknown"
                ? eventArgs.AircraftModel
                : eventArgs.AircraftType;
            var record = LandingRecordFactory.Create(
                touchdown,
                samples,
                eventArgs.AircraftTitle,
                aircraftType,
                contactCount: touchdowns.Count,
                timestampUtc: episodeTimestampUtc);
            record.Simulator = eventArgs.Simulator;
            record.ControlInputSources = eventArgs.ControlInputSources.ToList();
            TryResolveAirport(record, facilities);
            lock (_landingRepositoryGate)
            {
                _repository.Save(record);
            }
            savedRecords.Add(record);
        }

        return ProcessedEpisode.Completed(facilities, savedRecords);
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

        ApplySessionFilter(newestFirst[0].Id);
    }

    private void OnAirportFacilitiesUpdated(object? sender, AirportFacilitiesEventArgs eventArgs)
    {
        try
        {
            lock (_airportFacilityRepositoryGate)
            {
                _airportFacilities = _airportFacilityRepository.MergeAndSave(eventArgs.Facilities);
            }
        }
        catch
        {
            _airportFacilities = MergeAirportFacilities(_airportFacilities, eventArgs.Facilities);
        }

        var changed = false;
        foreach (var record in _landings)
        {
            if (!TryResolveAirport(record, _airportFacilities))
            {
                continue;
            }

            lock (_landingRepositoryGate)
            {
                _repository.UpdateSummary(record);
            }
            if (_loadedDetails.TryGetValue(record.Id, out var detail))
            {
                detail.Airport = record.Airport;
                detail.Runway = record.Runway;
                detail.AirportDistanceNauticalMiles = record.AirportDistanceNauticalMiles;
            }
            changed = true;
        }

        if (changed)
        {
            var selected = LandingHistoryList.SelectedItem as LandingRecord;
            LandingHistoryList.Items.Refresh();
            if (selected != null)
            {
                SelectRecord(selected);
            }
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
                _bugReportPersistenceTask = persistenceTask;
            }
            path = await persistenceTask;
            if (_lifetimeCancellation.IsCancellationRequested)
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

            var preparation = await uploadClient.PrepareAsync(_lifetimeCancellation.Token);
            if (preparation.State != TelemetryPreparationState.Ready)
            {
                SetUploadStatus("BugReport.SavedForRetryFormat", System.IO.Path.GetFileName(path));
                StartPendingBugReportRetry(uploadClient);
                return;
            }

            // Queue the newly submitted report before scanning any older
            // backlog. This keeps user-initiated reports ahead of legacy RAW.
            var queued = uploadClient.Enqueue(path);
            uploadClient.EnqueueExisting();
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
            if (path != null && System.IO.File.Exists(path))
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

    internal static void DrainBugReportPersistence(Task persistenceTask)
    {
        persistenceTask.GetAwaiter().GetResult();
    }

    private void RefreshBugReportButton()
    {
        if (ReportBugButton == null)
        {
            return;
        }

        var available = _lastLandingBugReport.Available() != null;
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
            return client;
        }
    }

    private void StartPendingBugReportRetry(TelemetryUploadClient? existingClient = null)
    {
        var cancellationToken = _lifetimeCancellation.Token;
        lock (_bugReportRetryGate)
        {
            if (!_bugReportRetryTask.IsCompleted || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _bugReportRetryTask = Task.Run(async () =>
            {
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
            }, cancellationToken);
        }
    }

    private void OnTelemetryUploadStatusChanged(object? sender, TelemetryUploadStatusEventArgs eventArgs)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
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
        _lifetimeCancellation.Cancel();
        _releaseUpdater.Dispose();
        Loaded -= OnWindowLoaded;
        foreach (var chart in _charts)
        {
            chart.HoverTimeChanged -= OnChartHoverTimeChanged;
            chart.ZoomRangeSelected -= OnChartZoomRangeSelected;
        }

        if (_recorder != null)
        {
            _recorder.Dispose();
            _recorder.StatusChanged -= OnRecorderStatusChanged;
            _recorder.EpisodeStarted -= OnEpisodeStarted;
            _recorder.EpisodeCompleted -= OnEpisodeCompleted;
            _recorder.AirportFacilitiesUpdated -= OnAirportFacilitiesUpdated;
            _recorder = null;
        }

        Task pendingEpisodePersistence;
        lock (_episodeProcessingGate)
        {
            pendingEpisodePersistence = _episodePersistenceTask;
        }

        try
        {
            // Recorder.Dispose can complete an active rollout and enqueue its
            // landing here. Persistence runs off the dispatcher, so draining it
            // cannot deadlock UI shutdown and prevents losing that landing.
            pendingEpisodePersistence.GetAwaiter().GetResult();
        }
        catch
        {
            // The normal episode handler already owns diagnostics. Shutdown
            // must continue even if disk I/O or analysis failed.
        }

        Task pendingBugReportPersistence;
        lock (_bugReportPersistenceGate)
        {
            pendingBugReportPersistence = _bugReportPersistenceTask;
        }
        try
        {
            // The ZIP write is the durability boundary for a user-initiated
            // report. Network retry stays asynchronous, but closing the app
            // cannot terminate the atomic local write halfway through.
            DrainBugReportPersistence(pendingBugReportPersistence);
        }
        catch
        {
            // The click handler owns user-facing diagnostics. Shutdown must
            // continue after a genuine storage failure.
        }

        TelemetryUploadClient? telemetryUploadClient;
        lock (_telemetryUploadClientGate)
        {
            telemetryUploadClient = _telemetryUploadClient;
            _telemetryUploadClient = null;
        }
        if (telemetryUploadClient != null)
        {
            telemetryUploadClient.StatusChanged -= OnTelemetryUploadStatusChanged;
            telemetryUploadClient.Dispose();
        }

        if (_messageSource != null)
        {
            _messageSource.RemoveHook(WindowProcedure);
            _messageSource = null;
        }

        _lifetimeCancellation.Dispose();
    }

    private sealed class ProcessedEpisode
    {
        private ProcessedEpisode(
            bool replayLike,
            IReadOnlyList<AirportFacility> airportFacilities,
            IReadOnlyList<LandingRecord> records)
        {
            ReplayLike = replayLike;
            AirportFacilities = airportFacilities;
            Records = records;
        }

        public bool ReplayLike { get; }

        public IReadOnlyList<AirportFacility> AirportFacilities { get; }

        public IReadOnlyList<LandingRecord> Records { get; }

        public static ProcessedEpisode Replay() => new ProcessedEpisode(
            true,
            Array.Empty<AirportFacility>(),
            Array.Empty<LandingRecord>());

        public static ProcessedEpisode Completed(
            IReadOnlyList<AirportFacility> airportFacilities,
            IReadOnlyList<LandingRecord> records) =>
            new ProcessedEpisode(false, airportFacilities, records);
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
