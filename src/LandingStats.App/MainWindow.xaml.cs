using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
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
    private IReadOnlyList<LandingRecord> _landings = Array.Empty<LandingRecord>();
    private HwndSource? _messageSource;
    private SimConnectLandingRecorder? _recorder;
    private LandingChart[] _charts = Array.Empty<LandingChart>();

    public MainWindow()
    {
        InitializeComponent();
        LoadHistory();
        _charts = new[] { SpeedChart, GChart, ControlsChart, AttitudeChart, PowerChart, GearChart };
        foreach (var chart in _charts)
        {
            chart.HoverTimeChanged += OnChartHoverTimeChanged;
            chart.ZoomRangeSelected += OnChartZoomRangeSelected;
        }

        SourceInitialized += OnSourceInitialized;
        Closed += OnWindowClosed;
    }

    private void LoadHistory()
    {
        var stored = _repository.LoadAll();
        _landings = stored.Count == 0 ? LandingRecord.CreateDemoHistory() : stored;
        LandingHistoryList.ItemsSource = _landings;
        LandingHistoryList.SelectedIndex = _landings.Count == 0 ? -1 : 0;
        HistoryCountText.Text = stored.Count == 0 ? "DEMO" : _landings.Count.ToString();
        StoragePathText.Text = stored.Count == 0
            ? "Showing non-persistent demo data. Real records will be stored at:\n" + _repository.RootPath
            : _repository.RootPath;
    }

    private void OnHistorySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        if (LandingHistoryList.SelectedItem is not LandingRecord selected)
        {
            return;
        }

        DataContext = selected;
        SpeedChart.Record = selected;
        GChart.Record = selected;
        ControlsChart.Record = selected;
        AttitudeChart.Record = selected;
        PowerChart.Record = selected;
        GearChart.Record = selected;
        ApplyZoom(null, null);
    }

    private void OnChartHoverTimeChanged(object? sender, LandingChartHoverEventArgs eventArgs)
    {
        foreach (var chart in _charts)
        {
            chart.SetHoverTime(eventArgs.TimeSeconds);
        }
    }

    private void OnChartZoomRangeSelected(object? sender, LandingChartZoomEventArgs eventArgs)
    {
        ApplyZoom(eventArgs.StartSeconds, eventArgs.EndSeconds);
    }

    private void ApplyZoom(double? startSeconds, double? endSeconds)
    {
        foreach (var chart in _charts)
        {
            chart.SetZoomRange(startSeconds, endSeconds);
        }

        var zoomed = startSeconds.HasValue && endSeconds.HasValue;
        ResetZoomButton.Visibility = zoomed ? Visibility.Visible : Visibility.Collapsed;
        ZoomRangeText.Text = zoomed
            ? $"{startSeconds:+0.0;-0.0;0.0}s → {endSeconds:+0.0;-0.0;0.0}s"
            : "Drag across any chart to zoom";
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs eventArgs)
    {
        ApplyZoom(null, null);
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _messageSource = HwndSource.FromHwnd(handle);
        _messageSource?.AddHook(WindowProcedure);
        _recorder = new SimConnectLandingRecorder(handle);
        _recorder.StatusChanged += OnRecorderStatusChanged;
        _recorder.EpisodeCompleted += OnEpisodeCompleted;
        _recorder.Start();
    }

    private IntPtr WindowProcedure(IntPtr windowHandle, int message, IntPtr wordParameter, IntPtr longParameter, ref bool handled)
    {
        return _recorder?.HandleWindowMessage(message, ref handled) ?? IntPtr.Zero;
    }

    private void OnRecorderStatusChanged(object? sender, RecorderStatusEventArgs eventArgs)
    {
        ConnectionStatusText.Text = eventArgs.Message;
        ConnectionStatusDot.Fill = eventArgs.State switch
        {
            RecorderState.Connected => Brush("#55DFC0"),
            RecorderState.Recording => Brush("#A78BFA"),
            RecorderState.Error => Brush("#F58BA7"),
            _ => Brush("#F5C66E"),
        };
    }

    private void OnEpisodeCompleted(object? sender, LandingEpisodeEventArgs eventArgs)
    {
        try
        {
            var touchdowns = TouchdownAnalysis.Analyze(eventArgs.Samples);
            if (touchdowns.Count == 0)
            {
                ConnectionStatusText.Text = "Capture ended, but no touchdown was detected";
                ConnectionStatusDot.Fill = Brush("#F58BA7");
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
                    timestampUtc: DateTime.UtcNow);
                record.Simulator = eventArgs.Simulator;
                _repository.Save(record);
            }

            LoadHistory();
            ConnectionStatusText.Text = touchdowns.Count == 1
                ? "Landing analyzed and saved"
                : $"{touchdowns.Count} contacts analyzed and saved";
            ConnectionStatusDot.Fill = Brush("#55DFC0");
        }
        catch (Exception exception)
        {
            ConnectionStatusText.Text = $"Landing analysis failed: {exception.Message}";
            ConnectionStatusDot.Fill = Brush("#F58BA7");
        }
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
        if (eventArgs.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }
}
