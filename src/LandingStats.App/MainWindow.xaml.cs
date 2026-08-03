using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App;

public partial class MainWindow : Window
{
    private readonly LandingRepository _repository = new LandingRepository();
    private readonly RawCaptureRepository _rawCaptureRepository = new RawCaptureRepository();
    private readonly AirportFacilityRepository _airportFacilityRepository = new AirportFacilityRepository();
    private IReadOnlyList<LandingRecord> _landings = Array.Empty<LandingRecord>();
    private IReadOnlyList<AirportFacility> _airportFacilities = Array.Empty<AirportFacility>();
    private HwndSource? _messageSource;
    private SimConnectLandingRecorder? _recorder;
    private LandingChart[] _charts = Array.Empty<LandingChart>();
    private bool _showFullApproach;
    private int _primaryGearSeriesIndex;
    private int? _mainIsolatedSeriesIndex;

    public MainWindow()
    {
        InitializeComponent();
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetName().Version;
        var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Evgeniy Zaytsev";
        VersionAuthorText.Text = $"v{version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0} · {company}";
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
        MainChartDescription.Text = "signed velocity around contact";
        ModeUnitText.Text = "fpm";
        UpdateMainLegend(LandingChartMode.VerticalSpeed);

        SourceInitialized += OnSourceInitialized;
        Closed += OnWindowClosed;
    }

    private void LoadHistory()
    {
        var stored = _repository.LoadAll().ToList();
        foreach (var record in stored)
        {
            var changed = TryResolveAirport(record, _airportFacilities);
            if (record.FormatVersion >= 6 && LandingRecordFactory.RefreshRawPitchInputSelection(record))
            {
                changed = true;
            }

            if (changed)
            {
                _repository.Save(record);
            }
        }

        _landings = stored;
        ApplySessionFilter();
        StoragePathText.Text = "%LOCALAPPDATA%\\MSFS Landing Stats\\Landings";
        StoragePathText.ToolTip = _repository.RootPath;
        LandingContent.Visibility = _landings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = _landings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_landings.Count == 0)
        {
            DataContext = null;
        }
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
        DataContext = selected;
        MainChart.Record = selected;
        MiniGChart.Record = selected;
        MiniPowerChart.Record = selected;
        MiniGearChart.Record = selected;
        Timeline.Record = selected;
        MiniGChart.SetIsolatedSeries(0);
        MiniPowerChart.SetIsolatedSeries(selected.Engines.Count > 0 ? 0 : (int?)null);
        _primaryGearSeriesIndex = PrimaryGearSeriesIndex(selected);
        MiniGearChart.SetIsolatedSeries(selected.ContactPoints.Count > 0 ? _primaryGearSeriesIndex : (int?)null);
        _mainIsolatedSeriesIndex = null;
        MainChart.SetIsolatedSeries(null);
        UpdateMainLegend(MainChart.Mode);
        UpdateChartDescription(MainChart.Mode);
        ApplyZoom(_showFullApproach ? null : -6, _showFullApproach ? null : 8);
        UpdateLaneReadouts(selected, 0);
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

    private void ApplySessionFilter()
    {
        if (LandingHistoryList == null || HistoryCountText == null)
        {
            return;
        }

        var selectedId = (LandingHistoryList.SelectedItem as LandingRecord)?.Id;
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
            LandingChartMode.VerticalSpeed => "signed velocity around contact",
            LandingChartMode.LoadFactors => "three axes at the strut",
            LandingChartMode.FlightControls when record?.HasRawPitchInput == true =>
                $"pitch raw controller {record.RawPitchInputSourceIndex} · r={Math.Abs(record.RawPitchInputCorrelation):F2} · lag {record.RawPitchInputLagSeconds * 1000.0:F0} ms · dashed surfaces",
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
                labels = new[] { "inertial", "VSI", "MSFS surface" };
                colors = new[] { "#FF7A45", "#D9C46A", "#5FA8F5" };
                dashed = new[] { false, false, true };
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
        _recorder.RawDebugCaptureCompleted += OnRawDebugCaptureCompleted;
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
            var touchdowns = TouchdownAnalysis.Analyze(eventArgs.Samples);
            if (touchdowns.Count == 0)
            {
                ConnectionStatusText.Text = "Capture ended, but no touchdown was detected";
                ConnectionStatusDot.Fill = Brush("#FF8A6A");
                return;
            }

            foreach (var touchdown in touchdowns)
            {
                var aircraftType = eventArgs.AircraftType == "unknown"
                    ? eventArgs.AircraftModel
                    : eventArgs.AircraftType;
                var record = LandingRecordFactory.Create(
                    touchdown,
                    eventArgs.Samples,
                    eventArgs.AircraftTitle,
                    aircraftType,
                    contactCount: touchdowns.Count,
                    timestampUtc: episodeTimestampUtc);
                record.Simulator = eventArgs.Simulator;
                record.ControlInputSources = eventArgs.ControlInputSources.ToList();
                TryResolveAirport(record, _airportFacilities);
                _repository.Save(record);
            }

            LoadHistory();
            var landingStatus = touchdowns.Count == 1
                ? "Landing analyzed and saved"
                : $"{touchdowns.Count} contacts analyzed and saved";
            ConnectionStatusText.Text = landingStatus;
            ConnectionStatusDot.Fill = Brush("#8FD6A8");
        }
        catch (Exception exception)
        {
            ConnectionStatusText.Text = $"Landing analysis failed: {exception.Message}";
            ConnectionStatusDot.Fill = Brush("#FF8A6A");
        }
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

            _repository.Save(record);
            changed = true;
        }

        if (changed)
        {
            var selected = LandingHistoryList.SelectedItem;
            LandingHistoryList.Items.Refresh();
            DataContext = null;
            DataContext = selected;
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

    private void OnRawDebugModeChanged(object sender, RoutedEventArgs eventArgs)
    {
        var enabled = RawDebugToggle.IsChecked == true;
        RawDebugToggle.Content = enabled ? "DEBUG RAW · ON" : "DEBUG RAW · OFF";
        if (_recorder == null)
        {
            RawDebugStatusText.Text = enabled
                ? "Live capture will start after SimConnect initializes"
                : "Full-rate capture is disabled";
            return;
        }

        var wasEnabled = _recorder.RawDebugEnabled;
        _recorder.SetRawDebugEnabled(enabled);
        if (enabled)
        {
            RawDebugStatusText.Text = "LIVE · every SIM_FRAME sample · includes previous 15 seconds\n" + _rawCaptureRepository.RootPath;
        }
        else if (!wasEnabled)
        {
            RawDebugStatusText.Text = "Full-rate capture is disabled";
        }
    }

    private void OnRawDebugCaptureCompleted(object? sender, RawDebugCaptureEventArgs eventArgs)
    {
        try
        {
            var path = _rawCaptureRepository.Save(
                eventArgs.Samples,
                eventArgs.Simulator,
                eventArgs.AircraftTitle,
                eventArgs.AircraftType,
                eventArgs.AircraftModel,
                eventArgs.ControlInputSources,
                eventArgs.StartedUtc);
            RawDebugStatusText.Text = $"Saved {eventArgs.Samples.Count:N0} frames · {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            RawDebugStatusText.Text = $"Raw capture failed: {exception.Message}";
        }
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
            _recorder.RawDebugCaptureCompleted -= OnRawDebugCaptureCompleted;
            _recorder = null;
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
}
