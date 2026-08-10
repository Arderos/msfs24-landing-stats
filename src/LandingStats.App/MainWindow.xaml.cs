using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
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
    private readonly LandingRepository _repository = new LandingRepository();
    private readonly RawCaptureRepository _rawCaptureRepository = new RawCaptureRepository();
    private readonly AirportFacilityRepository _airportFacilityRepository = new AirportFacilityRepository();
    private readonly ReleaseUpdater _releaseUpdater = new ReleaseUpdater();
    private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
    private TelemetryUploadClient? _telemetryUploadClient;
    private IReadOnlyList<LandingRecord> _landings = Array.Empty<LandingRecord>();
    private IReadOnlyList<AirportFacility> _airportFacilities = Array.Empty<AirportFacility>();
    private readonly Dictionary<string, LandingRecord> _loadedDetails = new Dictionary<string, LandingRecord>(StringComparer.Ordinal);
    private HwndSource? _messageSource;
    private SimConnectLandingRecorder? _recorder;
    private RawCaptureSession? _rawCaptureSession;
    private LandingChart[] _charts = Array.Empty<LandingChart>();
    private bool _showFullApproach;
    private int _primaryGearSeriesIndex;
    private int? _mainIsolatedSeriesIndex;
    private bool _changingRawDebugToggle;
    private bool _changingLanguageSelection;
    private bool _telemetryUploadAllowed;
    private LandingRecord? _pendingDeleteRecord;
    private double? _zoomStartSeconds;
    private double? _zoomEndSeconds;
    private RecorderState _lastRecorderState = RecorderState.Waiting;
    private string _connectionStatusKey = "Top.Waiting";
    private object[] _connectionStatusArguments = Array.Empty<object>();
    private bool _connectionStatusIsWarning;
    private string _rawStatusKey = "Footer.UploadDisabled";
    private object[] _rawStatusArguments = Array.Empty<object>();
    private bool _rawStatusIsError;

    public MainWindow()
    {
        _settingsRepository = new ApplicationSettingsRepository();
        _settings = _settingsRepository.Load();
        LocalizationManager.Apply(_settings.Language);
        InitializeComponent();
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetName().Version;
        var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Evgeniy Zaytsev";
        VersionAuthorRun.Text = $"v{version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0} · {company}";
        _airportFacilities = _airportFacilityRepository.Load();
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
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var result = await _releaseUpdater.CheckAndInstallAsync(version, _lifetimeCancellation.Token);
        VersionAuthorText.ToolTip = result.Path == null ? result.Message : result.Message + "\n" + result.Path;
        if (result.State == ReleaseUpdateState.UpdateStarted && result.Version != null)
        {
            VersionAuthorRun.Text += LocalizationManager.Format("Update.StatusUpdatingFormat", result.Version);
            VersionAuthorText.Foreground = Brush("#8FD6A8");
            Application.Current.Shutdown();
        }
        else if (result.State == ReleaseUpdateState.Rejected)
        {
            VersionAuthorRun.Text += LocalizationManager.Text("Update.StatusRejected");
            VersionAuthorText.Foreground = Brush("#FF8A6A");
        }
    }

    private void LoadHistory()
    {
        var stored = _repository.LoadAll().ToList();
        var changedRecords = new List<LandingRecord>();
        foreach (var record in stored)
        {
            var changed = TryResolveAirport(record, _airportFacilities);
            if (changed)
            {
                changedRecords.Add(record);
            }
        }

        _repository.UpdateSummaries(changedRecords);

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
                detail = _repository.LoadDetail(selected) ?? selected;
                if (!detail.IsSummaryOnly)
                {
                    if (detail.FormatVersion >= 6 && LandingRecordFactory.RefreshRawPitchInputSelection(detail))
                    {
                        _repository.Save(detail);
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
            _repository.Delete(record.Id);
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

        SettingsErrorText.Text = string.Empty;
        SettingsErrorText.Visibility = Visibility.Collapsed;
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
        RawDebugToggle.Content = LocalizationManager.Text(
            RawDebugToggle.IsChecked == true ? "Footer.DebugOn" : "Footer.DebugOff");
        SetRawStatusCore(_rawStatusKey, _rawStatusArguments, _rawStatusIsError);

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
        string[] colors;
        bool[] dashed;
        var record = DataContext as LandingRecord;
        var count = 3;

        switch (mode)
        {
            case LandingChartMode.VerticalSpeed:
                labels = new[]
                {
                    LocalizationManager.Text("Chart.Aircraft"),
                    LocalizationManager.Text("Chart.VsiLagged"),
                    LocalizationManager.Text("Chart.SurfaceClosure"),
                };
                colors = new[] { "#FF7A45", "#D9C46A", "#5FA8F5" };
                dashed = new[] { false, false, true };
                count = record?.HasSurfaceLatchData == true ? 3 : 2;
                break;
            case LandingChartMode.LoadFactors:
                labels = new[]
                {
                    LocalizationManager.Text("Chart.Vertical"),
                    LocalizationManager.Text("Chart.Longitudinal"),
                    LocalizationManager.Text("Chart.Lateral"),
                };
                colors = new[] { "#FF7A45", "#5FA8F5", "#D9C46A" };
                dashed = new[] { false, false, false };
                break;
            case LandingChartMode.FlightControls:
                labels = new[]
                {
                    LocalizationManager.Text(record?.HasRawPitchInput == true ? "Chart.PitchRaw" : "Chart.PitchSim"),
                    LocalizationManager.Text("Chart.Roll"),
                    LocalizationManager.Text("Chart.Yaw"),
                };
                colors = new[] { "#FF7A45", "#5FA8F5", "#D9C46A" };
                dashed = new[] { false, false, false };
                break;
            case LandingChartMode.Attitude:
                labels = new[]
                {
                    LocalizationManager.Text("Chart.Pitch"),
                    LocalizationManager.Text("Chart.Bank"),
                    LocalizationManager.Text("Chart.Aoa"),
                };
                colors = new[] { "#FF7A45", "#5FA8F5", "#D9C46A" };
                dashed = new[] { false, false, false };
                break;
            case LandingChartMode.Power:
                labels = new[]
                {
                    LocalizationManager.Format("Chart.EngineFormat", 1),
                    LocalizationManager.Format("Chart.EngineFormat", 2),
                    LocalizationManager.Format("Chart.EngineFormat", 3),
                };
                colors = new[] { "#FF7A45", "#5FA8F5", "#D9C46A" };
                dashed = new[] { false, false, false };
                count = Math.Min(3, record?.Engines.Count ?? 0);
                break;
            case LandingChartMode.Gear:
                labels = new[]
                {
                    LocalizationManager.Format("Chart.ContactPointFormat", 1),
                    LocalizationManager.Format("Chart.ContactPointFormat", 2),
                    LocalizationManager.Format("Chart.ContactPointFormat", 3),
                };
                colors = new[] { "#FF7A45", "#5FA8F5", "#D9C46A" };
                dashed = new[] { false, false, false };
                count = Math.Min(3, record?.ContactPoints.Count ?? 0);
                break;
            default:
                return;
        }

        var buttons = new[] { LegendButton0, LegendButton1, LegendButton2 };
        var lines = new[] { LegendLine0, LegendLine1, LegendLine2 };
        var textBlocks = new[] { LegendLabel0, LegendLabel1, LegendLabel2 };
        for (var index = 0; index < buttons.Length; index++)
        {
            var visible = index < count;
            buttons[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                continue;
            }

            lines[index].Stroke = Brush(colors[index]);
            lines[index].StrokeDashArray = dashed[index]
                ? new DoubleCollection { 4, 3 }
                : null;
            textBlocks[index].Text = labels[index];
            buttons[index].Opacity = !_mainIsolatedSeriesIndex.HasValue || _mainIsolatedSeriesIndex == index
                ? 1.0
                : 0.32;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _messageSource = HwndSource.FromHwnd(handle);
        _messageSource?.AddHook(WindowProcedure);
        _recorder = new SimConnectLandingRecorder(handle);
        _recorder.StatusChanged += OnRecorderStatusChanged;
        _recorder.EpisodeCompleted += OnEpisodeCompleted;
        _recorder.AirportFacilitiesUpdated += OnAirportFacilitiesUpdated;
        _recorder.RawDebugCaptureStarted += OnRawDebugCaptureStarted;
        _recorder.RawDebugSampleReceived += OnRawDebugSampleReceived;
        _recorder.RawDebugCaptureStopped += OnRawDebugCaptureStopped;
        _recorder.SeedAirportFacilities(_airportFacilities);
        _recorder.Start();
        if (RawDebugToggle.IsChecked == true)
        {
            _recorder.SetRawDebugEnabled(true);
        }
    }

    private IntPtr WindowProcedure(IntPtr windowHandle, int message, IntPtr wordParameter, IntPtr longParameter, ref bool handled)
    {
        return _recorder?.HandleWindowMessage(message, ref handled) ?? IntPtr.Zero;
    }

    private void OnRecorderStatusChanged(object? sender, RecorderStatusEventArgs eventArgs)
    {
        SetConnectionStatus(eventArgs.MessageKey, eventArgs.MessageArguments);
        UpdateRecorderPresentation(eventArgs.State);
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

    private void OnEpisodeCompleted(object? sender, LandingEpisodeEventArgs eventArgs)
    {
        var episodeTimestampUtc = DateTime.UtcNow;
        try
        {
            _airportFacilities = _airportFacilityRepository.MergeAndSave(eventArgs.AirportFacilities);
        }
        catch
        {
            _airportFacilities = _airportFacilities
                .Concat(eventArgs.AirportFacilities)
                .GroupBy(facility => facility.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray();
        }

        try
        {
            var samples = TelemetryDeduplicator.Deduplicate(eventArgs.Samples);
            var touchdowns = TouchdownAnalysis.Analyze(samples);
            if (touchdowns.Count == 0)
            {
                UpdateRecorderPresentation(RecorderState.Connected);
                SetConnectionWarning("Recorder.NoTouchdown");
                return;
            }

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
                TryResolveAirport(record, _airportFacilities);
                _repository.Save(record);
                _loadedDetails[record.Id] = record;
                savedRecords.Add(record);
            }

            AddLandingRecords(savedRecords);
            if (touchdowns.Count == 1)
            {
                SetConnectionStatus("Recorder.LandingSaved");
            }
            else
            {
                SetConnectionStatus("Recorder.ContactsSavedFormat", touchdowns.Count);
            }
            UpdateRecorderPresentation(RecorderState.Connected);
        }
        catch (Exception exception)
        {
            SetConnectionStatus("Recorder.AnalysisFailedFormat", exception.Message);
            UpdateRecorderPresentation(RecorderState.Error);
        }
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
            _airportFacilities = _airportFacilityRepository.MergeAndSave(eventArgs.Facilities);
        }
        catch
        {
            _airportFacilities = _airportFacilities
                .Concat(eventArgs.Facilities)
                .GroupBy(facility => facility.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray();
        }

        var changed = false;
        foreach (var record in _landings)
        {
            if (!TryResolveAirport(record, _airportFacilities))
            {
                continue;
            }

            _repository.UpdateSummary(record);
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

    private async void OnRawDebugModeChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_changingRawDebugToggle)
        {
            return;
        }
        var enabled = RawDebugToggle.IsChecked == true;
        RawDebugToggle.Content = LocalizationManager.Text(enabled ? "Footer.DebugOn" : "Footer.DebugOff");
        if (!enabled)
        {
            var wasEnabled = _recorder?.RawDebugEnabled == true;
            _recorder?.SetRawDebugEnabled(false);
            if (!wasEnabled)
            {
                SetRawStatus("Raw.Disabled");
            }
            return;
        }

        _recorder?.SetRawDebugEnabled(true);
        SetRawStatus(_recorder == null ? "Raw.Armed" : "Raw.Live");

        TelemetryUploadClient uploadClient;
        try
        {
            uploadClient = EnsureTelemetryUploadClient();
        }
        catch (Exception exception)
        {
            SetRawError("Raw.UploadUnavailableFormat", exception.Message);
            return;
        }

        bool consentAccepted;
        try
        {
            consentAccepted = uploadClient.ConsentAccepted;
        }
        catch (Exception exception)
        {
            _telemetryUploadAllowed = false;
            SetRawError("Raw.IdentityUnavailableFormat", exception.Message);
            return;
        }

        if (!consentAccepted)
        {
            var consent = MessageBox.Show(
                this,
                LocalizationManager.Text("Raw.ConsentBody"),
                LocalizationManager.Text("Raw.ConsentTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (consent != MessageBoxResult.Yes)
            {
                _telemetryUploadAllowed = false;
                SetRawStatus("Raw.UploadDisabled");
                return;
            }
            try
            {
                uploadClient.AcceptConsent();
            }
            catch (Exception exception)
            {
                _telemetryUploadAllowed = false;
                SetRawError("Raw.ConsentSaveFailedFormat", exception.Message);
                return;
            }
        }
        _telemetryUploadAllowed = true;

        SetRawStatus("Raw.Preparing");
        TelemetryPreparationResult preparation;
        try
        {
            preparation = await uploadClient.PrepareAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            if (RawDebugToggle.IsChecked == true)
            {
                SetRawError("Raw.LiveUploadUnavailableFormat", exception.Message);
            }
            return;
        }
        if (RawDebugToggle.IsChecked != true)
        {
            return;
        }
        if (preparation.State == TelemetryPreparationState.Ready)
        {
            SetRawStatus("Raw.Ready");
        }
        else
        {
            SetRawError(preparation.MessageKey, preparation.MessageArguments);
        }
    }

    private void OnRawDebugCaptureStarted(object? sender, RawDebugCaptureStartedEventArgs eventArgs)
    {
        try
        {
            StopRawCaptureSession();
            _rawCaptureSession = _rawCaptureRepository.StartCapture(
                eventArgs.InitialSamples,
                eventArgs.Simulator,
                eventArgs.AircraftTitle,
                eventArgs.AircraftType,
                eventArgs.AircraftModel,
                eventArgs.ControlInputSources,
                eventArgs.StartedUtc);
            _rawCaptureSession.ChunkCompleted += OnRawCaptureChunkCompleted;
            _rawCaptureSession.Failed += OnRawCaptureFailed;
            if (_rawCaptureSession.Failure != null)
            {
                OnRawCaptureFailed(_rawCaptureSession.Failure);
            }
            SetRawStatus("Raw.Live");
        }
        catch (Exception exception)
        {
            SetRawError("Raw.CaptureFailedFormat", exception.Message);
        }
    }

    private void OnRawDebugSampleReceived(object? sender, RawDebugSampleEventArgs eventArgs)
    {
        _rawCaptureSession?.Write(eventArgs.Sample);
    }

    private void OnRawDebugCaptureStopped(object? sender, EventArgs eventArgs)
    {
        StopRawCaptureSession();
        SetRawStatus("Raw.Disabled");
    }

    private void StopRawCaptureSession()
    {
        var session = _rawCaptureSession;
        _rawCaptureSession = null;
        if (session == null)
        {
            return;
        }

        try
        {
            session.Dispose();
        }
        catch (Exception exception)
        {
            SetRawError("Raw.CaptureFailedFormat", exception.Message);
        }
        finally
        {
            session.ChunkCompleted -= OnRawCaptureChunkCompleted;
            session.Failed -= OnRawCaptureFailed;
        }
    }

    private void OnRawCaptureChunkCompleted(object? sender, RawCaptureChunkEventArgs eventArgs)
    {
        var uploadAllowed = _telemetryUploadAllowed;
        var queued = uploadAllowed && _telemetryUploadClient?.Enqueue(eventArgs.Path) == true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!uploadAllowed)
            {
                SetRawStatus("Raw.SavedDisabledFormat", eventArgs.SampleCount);
            }
            else if (queued)
            {
                SetRawStatus("Raw.SavedQueuedFormat", eventArgs.SampleCount);
            }
            else
            {
                SetRawError("Raw.SavedUnavailableFormat", eventArgs.SampleCount, eventArgs.Path);
            }
        }));
    }

    private TelemetryUploadClient EnsureTelemetryUploadClient()
    {
        if (_telemetryUploadClient != null)
        {
            return _telemetryUploadClient;
        }

        var client = new TelemetryUploadClient(_rawCaptureRepository.RootPath);
        client.StatusChanged += OnTelemetryUploadStatusChanged;
        _telemetryUploadClient = client;
        return client;
    }

    private void OnTelemetryUploadStatusChanged(object? sender, TelemetryUploadStatusEventArgs eventArgs)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetRawStatusCore(eventArgs.MessageKey, eventArgs.MessageArguments, eventArgs.IsError);
        }));
    }

    private void SetRawDebugToggle(bool enabled)
    {
        _changingRawDebugToggle = true;
        try
        {
            RawDebugToggle.IsChecked = enabled;
            RawDebugToggle.Content = LocalizationManager.Text(enabled ? "Footer.DebugOn" : "Footer.DebugOff");
            if (!enabled)
            {
                _recorder?.SetRawDebugEnabled(false);
            }
        }
        finally
        {
            _changingRawDebugToggle = false;
        }
    }

    private void OnRawCaptureFailed(Exception exception)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (RawDebugToggle.IsChecked == true)
            {
                RawDebugToggle.IsChecked = false;
            }
            SetRawError("Raw.CaptureFailedFormat", exception.Message);
        }));
    }

    private void SetRawStatus(string key, params object[] arguments)
    {
        SetRawStatusCore(key, arguments, false);
    }

    private void SetRawError(string key, params object[] arguments)
    {
        SetRawStatusCore(key, arguments, true);
    }

    private void SetRawStatusCore(string key, object[]? arguments, bool isError)
    {
        _rawStatusKey = key;
        _rawStatusArguments = arguments ?? Array.Empty<object>();
        _rawStatusIsError = isError;
        RawDebugStatusText.Text = LocalizationManager.Format(key, _rawStatusArguments);
        RawDebugStatusText.Foreground = isError ? Brush("#FF8A6A") : Brush("#AEB8C2");
    }

    private static int PrimaryGearSeriesIndex(LandingRecord record)
    {
        if (record.ContactPoints.Count == 0)
        {
            return 0;
        }

        return record.ContactPoints
            .Select((series, index) => new
            {
                Index = index,
                FirstContact = series.Points.Where(point => point.OnGround).Select(point => point.TimeSeconds).DefaultIfEmpty(double.MaxValue).Min(),
                Peak = series.Points.Select(point => point.CompressionPercent).DefaultIfEmpty(0).Max(),
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
        _lifetimeCancellation.Cancel();
        _releaseUpdater.Dispose();
        _lifetimeCancellation.Dispose();
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
            _recorder.EpisodeCompleted -= OnEpisodeCompleted;
            _recorder.AirportFacilitiesUpdated -= OnAirportFacilitiesUpdated;
            _recorder.RawDebugCaptureStarted -= OnRawDebugCaptureStarted;
            _recorder.RawDebugSampleReceived -= OnRawDebugSampleReceived;
            _recorder.RawDebugCaptureStopped -= OnRawDebugCaptureStopped;
            _recorder = null;
        }

        StopRawCaptureSession();
        if (_telemetryUploadClient != null)
        {
            _telemetryUploadClient.StatusChanged -= OnTelemetryUploadStatusChanged;
            _telemetryUploadClient.Dispose();
            _telemetryUploadClient = null;
        }

        if (_messageSource != null)
        {
            _messageSource.RemoveHook(WindowProcedure);
            _messageSource = null;
        }
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
