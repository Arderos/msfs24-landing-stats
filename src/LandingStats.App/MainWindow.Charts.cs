using System;
using System.Linq;
using System.Windows;
using LandingStats.App.Settings;

namespace LandingStats.App;

public partial class MainWindow
{
    private FrameworkElement[] _chartOverlays = Array.Empty<FrameworkElement>();
    private Exception? _chartRenderingFailure;
    private bool _chartPresentationAvailable = true;

    private void InitializeChartPresentation()
    {
        _chartOverlays = new FrameworkElement[]
        {
            WorkerWarningOverlay, ReplayUnsupportedOverlay, AutoStartPromptOverlay,
            GoogleDrivePromptOverlay, SettingsOverlay, DeleteLandingOverlay, ClosingOverlay,
        };
        foreach (var overlay in _chartOverlays)
            overlay.IsVisibleChanged += (_, __) => RefreshChartVisibility();
        ChartPanel.RenderingFailed += (_, error) =>
        {
            _chartRenderingFailure = error;
            RefreshChartVisibility();
        };
        ChartPanel.RenderingAvailabilityChanged += (_, available) =>
        {
            _chartPresentationAvailable = available;
            UpdateChartDescription(ChartPanel.Mode);
        };
        // Grid owns the original star/min/max sizing. Only changed row geometry
        // invalidates the detached chart scene; ordinary hover leaves WPF alone.
        LandingContent.LayoutUpdated += (_, __) => ChartPanel.SetLaneHeights(
            _showReplay ? 0 : MainChartRow.ActualHeight, GChartRow.ActualHeight,
            _showReplay ? 0 : AltitudeChartRow.ActualHeight, TimelineRow.ActualHeight);
        RefreshChartVisibility();
    }

    private void RefreshChartVisibility()
    {
        var overlayVisible = _chartOverlays.Any(overlay => overlay.IsVisible);
        // A native child would cover WPF dialogs. The chart supplies a frozen
        // WPF backdrop while any overlay is open, including nested prompts.
        ChartPanel.SetOverlayVisible(overlayVisible);
        ChartRenderingError.Visibility = !overlayVisible && _chartRenderingFailure != null
            ? Visibility.Visible : Visibility.Collapsed;
        if (_chartRenderingFailure != null)
            ChartRenderingError.Text = LocalizationManager.Format("Chart.RenderingFailedFormat", _chartRenderingFailure.Message);
    }
}
