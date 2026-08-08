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
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.TelemetryUpload;
using LandingStats.App.Updates;
using LandingStats.Core;

namespace LandingStats.App;

public partial class MainWindow : Window
{
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
    private bool _telemetryUploadAllowed;
    private LandingRecord? _pendingDeleteRecord;

    public MainWindow()
    {
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
        MainChartDescription.Text = "vertical speed · climb + / descent −";
        ModeUnitText.Text = "fpm";
        UpdateMainLegend(LandingChartMode.VerticalSpeed);

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
            VersionAuthorRun.Text += $" · updating to v{result.Version}";
            VersionAuthorText.Foreground = Brush("#8FD6A8");
            Application.Current.Shutdown();
        }
        else if (result.State == ReleaseUpdateState.Rejected)
        {
            VersionAuthorRun.Text += " · update rejected";
            VersionAuthorText.Foreground = Brush("#FF8A6A");
        }
    }

    private void LoadHistory()
    {
        var stored = _repository.LoadAll().ToList();
        foreach (var record in stored)
        {
            var changed = TryResolveAirport(record, _airportFacilities);
            if (changed)
            {
                _repository.UpdateSummary(record);
            }
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
        foreach (var chart in _charts)
        {
            chart.SetZoomRange(startSeconds, endSeconds);
        }

        Timeline.SetZoomRange(startSeconds, endSeconds);
        if (startSeconds.HasValue && endSeconds.HasValue)
        {
            ChartWindowButton.Content = $"Contact view  {startSeconds:+0;-0;0} … {endSeconds:+0;-0;0}s";
        }
        else if (DataContext is LandingRecord record && record.Series.Count > 1)
        {
            ChartWindowButton.Content = $"Full approach  {record.Series[0].TimeSeconds:+0;-0;0} … +15s";
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

        var now = DateTime.Now;
        var monthly = _landings
            .Where(record =>
            {
                var local = record.TimestampUtc.ToLocalTime();
                return local.Year == now.Year && local.Month == now.Month;
            })
            .ToArray();
        AverageRateText.Text = monthly.Length == 0
            ? "—"
            : $"{monthly.Average(record => -record.InertialFpm):+0;-0;0} fpm";
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
            EmptyStateTitle.Text = "NO LANDING SESSIONS";
            EmptyStateDescription.Text = "Start MSFS and descend through 500 ft AGL. Recording, analysis and storage happen automatically after touchdown.";
        }
        else
        {
            EmptyStateTitle.Text = "NO MATCHING SESSIONS";
            EmptyStateDescription.Text = "No saved landings match the current filter.";
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
        var contact = record.ContactCount > 1
            ? $" · contact {record.ContactNumber}/{record.ContactCount}"
            : string.Empty;
        DeleteLandingDescription.Text =
            $"{record.AircraftTitle}\n{record.LocationDisplay} · {record.TimestampDisplay}{contact}";
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
            DeleteLandingErrorText.Text = $"Could not delete this landing. {exception.Message}";
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
        ModeUnitText.Text = mode switch
        {
            LandingChartMode.VerticalSpeed => "fpm",
            LandingChartMode.LoadFactors => "G",
            LandingChartMode.FlightControls => "% travel",
            LandingChartMode.Attitude => "degrees",
            LandingChartMode.Power => "% N1",
            LandingChartMode.Gear => "% stroke",
            _ => string.Empty,
        };
        UpdateMainLegend(mode);
    }

    private void UpdateChartDescription(LandingChartMode mode)
    {
        var record = DataContext as LandingRecord;
        MainChartDescription.Text = mode switch
        {
            LandingChartMode.VerticalSpeed => "vertical speed · climb + / descent −",
            LandingChartMode.LoadFactors => "three axes at the strut",
            LandingChartMode.FlightControls when record?.HasRawPitchInput == true =>
                $"raw pitch C{record.RawPitchInputSourceIndex} · {record.RawPitchInputLagSeconds * 1000.0:F0} ms · dashed surfaces",
            LandingChartMode.FlightControls => "processed SimConnect commands · dashed surfaces",
            LandingChartMode.Attitude => "pitch, bank and AoA",
            LandingChartMode.Power => "solid N1, dashed lever",
            LandingChartMode.Gear => "strut compression per point",
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
                labels = new[] { "aircraft", "VSI (lagged)", "surface closure" };
                colors = new[] { "#FF7A45", "#D9C46A", "#5FA8F5" };
                dashed = new[] { false, false, true };
                count = record?.HasSurfaceLatchData == true ? 3 : 2;
                break;
            case LandingChartMode.LoadFactors:
                labels = new[] { "vertical", "longitudinal", "lateral" };
                colors = new[] { "#FF7A45", "#5FA8F5", "#D9C46A" };
                dashed = new[] { false, false, false };
                break;
            case LandingChartMode.FlightControls:
                labels = new[] { record?.HasRawPitchInput == true ? "pitch raw" : "pitch (sim)", "roll", "yaw" };
                colors = new[] { "#FF7A45", "#5FA8F5", "#D9C46A" };
                dashed = new[] { false, false, false };
                break;
            case LandingChartMode.Attitude:
                labels = new[] { "pitch", "bank", "AoA" };
                colors = new[] { "#FF7A45", "#5FA8F5", "#D9C46A" };
                dashed = new[] { false, false, false };
                break;
            case LandingChartMode.Power:
                labels = new[] { "engine 1", "engine 2", "engine 3" };
                colors = new[] { "#FF7A45", "#5FA8F5", "#D9C46A" };
                dashed = new[] { false, false, false };
                count = Math.Min(3, record?.Engines.Count ?? 0);
                break;
            case LandingChartMode.Gear:
                labels = new[] { "point 1", "point 2", "point 3" };
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
        ConnectionStatusText.Text = eventArgs.Message;
        RecorderModeText.Text = eventArgs.State switch
        {
            RecorderState.Connected => "armed",
            RecorderState.Recording => "recording",
            RecorderState.Error => "error",
            _ => "offline",
        };
        ConnectionStatusDot.Fill = eventArgs.State switch
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
                ConnectionStatusText.Text = "Capture ended, but no touchdown was detected";
                ConnectionStatusDot.Fill = Brush("#FF8A6A");
                RecorderModeText.Text = "armed";
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
            var landingStatus = touchdowns.Count == 1
                ? "Landing analyzed and saved"
                : $"{touchdowns.Count} contacts analyzed and saved";
            ConnectionStatusText.Text = landingStatus;
            ConnectionStatusDot.Fill = Brush("#8FD6A8");
            RecorderModeText.Text = "armed";
        }
        catch (Exception exception)
        {
            ConnectionStatusText.Text = $"Landing analysis failed: {exception.Message}";
            ConnectionStatusDot.Fill = Brush("#FF8A6A");
            RecorderModeText.Text = "error";
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
        RawDebugToggle.Content = enabled ? "DEBUG RAW · ON" : "DEBUG RAW · OFF";
        if (!enabled)
        {
            var wasEnabled = _recorder?.RawDebugEnabled == true;
            _recorder?.SetRawDebugEnabled(false);
            if (!wasEnabled)
            {
                RawDebugStatusText.Text = "Full-rate capture is disabled";
            }
            return;
        }

        _recorder?.SetRawDebugEnabled(true);
        RawDebugStatusText.Text = _recorder == null
            ? "Local full-rate capture armed · starts after SimConnect initializes"
            : "LIVE · saved locally · every SIM_FRAME sample · 15 s pre-roll";

        TelemetryUploadClient uploadClient;
        try
        {
            uploadClient = EnsureTelemetryUploadClient();
        }
        catch (Exception exception)
        {
            RawDebugStatusText.Text = $"Local DEBUG RAW is active · upload unavailable: {exception.Message}";
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
            RawDebugStatusText.Text = $"LIVE · saved locally · upload identity unavailable: {exception.Message}";
            return;
        }

        if (!consentAccepted)
        {
            var consent = MessageBox.Show(
                this,
                "DEBUG RAW is already recording locally. It can also send full-rate flight telemetry " +
                "to the MSFS Landing Stats maintainer, including aircraft state, coordinates and " +
                "controller/input channels. A local queue copy is removed only after the server accepts it.\n\n" +
                "Yes: allow upload. No: keep recording locally only.",
                "Enable telemetry upload",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (consent != MessageBoxResult.Yes)
            {
                _telemetryUploadAllowed = false;
                RawDebugStatusText.Text = "LIVE · saved locally · telemetry upload is disabled";
                return;
            }
            try
            {
                uploadClient.AcceptConsent();
            }
            catch (Exception exception)
            {
                _telemetryUploadAllowed = false;
                RawDebugStatusText.Text = $"LIVE · saved locally · upload consent could not be saved: {exception.Message}";
                return;
            }
        }
        _telemetryUploadAllowed = true;

        RawDebugStatusText.Text = "LIVE · saved locally · preparing secure upload…";
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
                RawDebugStatusText.Text = $"LIVE · saved locally · upload unavailable: {exception.Message}";
            }
            return;
        }
        if (RawDebugToggle.IsChecked != true)
        {
            return;
        }
        RawDebugStatusText.Text = preparation.State == TelemetryPreparationState.Ready
            ? "LIVE · saved locally · secure upload ready"
            : $"LIVE · saved locally · {preparation.Message}";
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
            RawDebugStatusText.Text = "LIVE · saved locally · every SIM_FRAME sample · 15 s pre-roll";
        }
        catch (Exception exception)
        {
            RawDebugStatusText.Text = $"Raw capture failed: {exception.Message}";
        }
    }

    private void OnRawDebugSampleReceived(object? sender, RawDebugSampleEventArgs eventArgs)
    {
        _rawCaptureSession?.Write(eventArgs.Sample);
    }

    private void OnRawDebugCaptureStopped(object? sender, EventArgs eventArgs)
    {
        StopRawCaptureSession();
        RawDebugStatusText.Text = "Full-rate capture is disabled";
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
            RawDebugStatusText.Text = $"Raw capture failed: {exception.Message}";
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
            RawDebugStatusText.Text = !uploadAllowed
                ? $"Saved {eventArgs.SampleCount:N0} frames locally · telemetry upload is disabled"
                : queued
                    ? $"Saved {eventArgs.SampleCount:N0} frames locally · queued for secure upload"
                    : $"Saved {eventArgs.SampleCount:N0} frames locally · upload unavailable · {eventArgs.Path}";
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
            RawDebugStatusText.Text = eventArgs.Message;
            RawDebugStatusText.Foreground = eventArgs.IsError ? Brush("#FF8A6A") : Brush("#AEB8C2");
        }));
    }

    private void SetRawDebugToggle(bool enabled)
    {
        _changingRawDebugToggle = true;
        try
        {
            RawDebugToggle.IsChecked = enabled;
            RawDebugToggle.Content = enabled ? "DEBUG RAW · ON" : "DEBUG RAW · OFF";
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
            RawDebugStatusText.Text = $"Raw capture failed: {exception.Message}";
        }));
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

        var point = record.Series.OrderBy(candidate => Math.Abs(candidate.TimeSeconds - timeSeconds)).First();
        HoverTimeText.Text = $"{point.TimeSeconds:+0.00;-0.00;0.00}s";
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
