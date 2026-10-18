using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

public enum LandingChartMode
{
    VerticalSpeed,
    LoadFactors,
    FlightControls,
    Attitude,
    Power,
    Gear,
}

public sealed class LandingChartHoverEventArgs : EventArgs
{
    public LandingChartHoverEventArgs(double? timeSeconds)
    {
        TimeSeconds = timeSeconds;
    }

    public double? TimeSeconds { get; }
}

public sealed class LandingChartZoomEventArgs : EventArgs
{
    public LandingChartZoomEventArgs(double? startSeconds, double? endSeconds)
    {
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
    }

    public double? StartSeconds { get; }

    public double? EndSeconds { get; }
}

public sealed partial class LandingChart : FrameworkElement
{
    private sealed class LegendHitTarget
    {
        public LegendHitTarget(Rect bounds, int seriesIndex)
        {
            Bounds = bounds;
            SeriesIndex = seriesIndex;
        }

        public Rect Bounds { get; }

        public int SeriesIndex { get; }
    }

    private sealed class HoverFlag
    {
        public HoverFlag(string value, Brush brush, double y)
        {
            Value = value;
            Brush = brush;
            Y = y;
        }

        public string Value { get; }

        public Brush Brush { get; }

        public double Y { get; set; }
    }

    private static readonly Brush GridBrush = Brush("#201E1C");
    private static readonly Brush MutedBrush = Brush("#8A837B");
    private static readonly Brush TextBrush = Brush("#F5F3F0");
    private static readonly Brush AccentBrush = Brush("#FF7A45");
    private static readonly Brush AmberBrush = Brush("#D9C46A");
    private static readonly Brush BlueBrush = Brush("#5FA8F5");
    private static readonly Brush PinkBrush = Brush("#8FD6A8");
    private static readonly Brush PurpleBrush = Brush("#B58AF5");
    private static readonly Brush CoralBrush = Brush("#F08C8C");
    private static readonly Brush CyanBrush = Brush("#59C6D2");
    private static readonly Brush LimeBrush = Brush("#A8C66C");
    private static readonly Brush RoseBrush = Brush("#E78AB2");
    private static readonly Brush SkyBrush = Brush("#82B8FF");
    private static readonly Brush TanBrush = Brush("#C69A72");
    private static readonly Brush SilverBrush = Brush("#AEB8C2");
    private static readonly Brush TooltipBrush = Brush("#1F1D1B");
    private static readonly Brush FlareBrush = Brush("#D5A08050");
    private static readonly Brush SelectionBrush = Brush("#33FF7A45");
    private static readonly Brush LaneContactBrush = Brush("#2A2724");
    private static readonly Pen GridPen = Pen(GridBrush, 1);
    private static readonly Pen AccentPen = Pen(AccentBrush, 2.1);
    private static readonly Pen AmberPen = Pen(AmberBrush, 1.8);
    private static readonly Pen BluePen = Pen(BlueBrush, 1.8);
    private static readonly Pen PinkPen = Pen(PinkBrush, 1.8);
    private static readonly Pen AccentDashedPen = DashedPen(AccentBrush, 1.3, 5, 4);
    private static readonly Pen AmberDashedPen = DashedPen(AmberBrush, 1.3, 5, 4);
    private static readonly Pen BlueDashedPen = DashedPen(BlueBrush, 1.3, 5, 4);
    private static readonly Pen ContactPen = Pen(Brush("#33302C"), 1);
    private static readonly Pen LaneContactPen = Pen(LaneContactBrush, 1);
    private static readonly Pen FlarePen = DashedPen(FlareBrush, 1.1, 2, 4);
    private static readonly Pen SurfacePen = DashedPen(BlueBrush, 1.4, 6, 4);
    private static readonly Pen HoverPen = Pen(Brush("#73F5F3F0"), 1);
    private static readonly Pen[] SolidPens =
    {
        AccentPen,
        BluePen,
        AmberPen,
        PinkPen,
        Pen(PurpleBrush, 1.8),
        Pen(CoralBrush, 1.8),
        Pen(CyanBrush, 1.8),
        Pen(LimeBrush, 1.8),
        Pen(RoseBrush, 1.8),
        Pen(SkyBrush, 1.8),
        Pen(TanBrush, 1.8),
        Pen(SilverBrush, 1.8),
    };
    private static readonly Pen[] DashedPens =
    {
        AccentDashedPen,
        BlueDashedPen,
        AmberDashedPen,
        DashedPen(PinkBrush, 1.3, 5, 4),
        DashedPen(PurpleBrush, 1.3, 5, 4),
        DashedPen(CoralBrush, 1.3, 5, 4),
        DashedPen(CyanBrush, 1.3, 5, 4),
        DashedPen(LimeBrush, 1.3, 5, 4),
        DashedPen(RoseBrush, 1.3, 5, 4),
        DashedPen(SkyBrush, 1.3, 5, 4),
        DashedPen(TanBrush, 1.3, 5, 4),
        DashedPen(SilverBrush, 1.3, 5, 4),
    };
    private static readonly Brush[] SeriesBrushes =
    {
        AccentBrush,
        BlueBrush,
        AmberBrush,
        PinkBrush,
        PurpleBrush,
        CoralBrush,
        CyanBrush,
        LimeBrush,
        RoseBrush,
        SkyBrush,
        TanBrush,
        SilverBrush,
    };

    private readonly List<LegendHitTarget> _legendHitTargets = new();
    private LandingRecord? _record;
    private IReadOnlyList<LandingGearSeries> _gearSeries = Array.Empty<LandingGearSeries>();
    private int _hoveredIndex = -1;
    private int? _isolatedSeriesIndex;
    private double? _zoomStartSeconds;
    private double? _zoomEndSeconds;
    private bool _isSelecting;
    private Point _selectionStart;
    private Point _selectionCurrent;
    private LandingChartMode _mode;
    private bool _showLegend = true;
    private bool _showHoverTooltip = true;
    private bool _showXAxisLabels = true;
    private bool _compactLane;
    private bool _powerShowThrottle = true;
    private LandingRecord? _gearRangeRecord;
    private int? _gearRangeIsolation;
    private double _gearRangeMinimumTime = double.NaN;
    private double _gearRangeMaximumTime = double.NaN;
    private double _gearRangeMaximum = double.NaN;

    public LandingRecord? Record
    {
        get => _record;
        set
        {
            var sameRecord = value != null && _record?.Id == value.Id;
            _record = value;
            _catalogStarts = value?.ControllerTimeline?.IsSupported != true ? null : new HashSet<double>(value.ControllerTimeline.Transitions.Skip(1).Select(transition => value.Series[transition[0]].TimeSeconds));
            _catalogPitch = value?.ControllerTimeline == null ? null : ReplayPresentationData.PitchValues(value);
            _gearSeries = LandingGearSeriesBuilder.Build(value);
            InvalidateGearRange();
            if (!sameRecord) { _hoveredIndex = -1; _isolatedSeriesIndex = null; }
            InvalidateChart();
        }
    }

    public LandingChartMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            _isolatedSeriesIndex = null;
            InvalidateGearRange();
            InvalidateChart();
        }
    }

    public bool ShowLegend
    {
        get => _showLegend;
        set
        {
            if (_showLegend == value)
            {
                return;
            }

            _showLegend = value;
            InvalidateChart();
        }
    }

    public bool ShowHoverTooltip
    {
        get => _showHoverTooltip;
        set
        {
            _showHoverTooltip = value;
            InvalidateChart();
        }
    }

    public bool ShowXAxisLabels
    {
        get => _showXAxisLabels;
        set
        {
            _showXAxisLabels = value;
            InvalidateChart();
        }
    }

    public bool CompactLane
    {
        get => _compactLane;
        set
        {
            _compactLane = value;
            InvalidateChart();
        }
    }

    public bool PowerShowThrottle
    {
        get => _powerShowThrottle;
        set
        {
            _powerShowThrottle = value;
            InvalidateChart();
        }
    }

    public event EventHandler<LandingChartHoverEventArgs>? HoverTimeChanged;

    public event EventHandler<LandingChartZoomEventArgs>? ZoomRangeSelected;

    public void SetIsolatedSeries(int? seriesIndex)
    {
        _isolatedSeriesIndex = seriesIndex;
        InvalidateGearRange();
        InvalidateChart();
    }

    public void SetHoverTime(double? timeSeconds)
    {
        var points = _record?.Series;
        if (!timeSeconds.HasValue || points == null || points.Count == 0)
        {
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                InvalidateVisual();
            }

            return;
        }

        var nearest = ClosestIndex(points.Count, index => points[index].TimeSeconds, timeSeconds.Value);
        if (_hoveredIndex != nearest)
        {
            _hoveredIndex = nearest;
            InvalidateVisual();
        }
    }

    public void SetZoomRange(double? startSeconds, double? endSeconds)
    {
        if (!startSeconds.HasValue || !endSeconds.HasValue || endSeconds.Value - startSeconds.Value < 0.05)
        {
            _zoomStartSeconds = null;
            _zoomEndSeconds = null;
        }
        else
        {
            _zoomStartSeconds = Math.Min(startSeconds.Value, endSeconds.Value);
            _zoomEndSeconds = Math.Max(startSeconds.Value, endSeconds.Value);
        }

        _hoveredIndex = -1;
        InvalidateChart();
    }

    private DrawingGroup? _staticDrawing;
    private Size _drawingSize;
    private string? _drawingLanguage;
    private double _drawingDpi;
    private Rect _drawingPlot;
    private double _drawingMinTime, _drawingMaxTime, _drawingMinValue, _drawingMaxValue;
    private bool _drawingHasData;
    private void InvalidateChart() { _staticDrawing = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var language = LocalizationManager.Text("Chart.NoData");
        if (_staticDrawing == null || _drawingSize != RenderSize || _drawingLanguage != language || _drawingDpi != dpi)
        {
            _drawingSize = RenderSize; _drawingLanguage = language; _drawingDpi = dpi;
            _staticDrawing = new DrawingGroup();
            using (var drawing = _staticDrawing.Open()) BuildStaticDrawing(drawing);
            _staticDrawing.Freeze();
        }
        context.DrawDrawing(_staticDrawing);
        var points = _record?.Series;
        if (!_drawingHasData || points == null) return;
        context.PushClip(new RectangleGeometry(_drawingPlot));
        if (_hoveredIndex >= 0 && _hoveredIndex < points.Count)
            DrawHover(context, points[_hoveredIndex], _drawingPlot, _drawingMinTime, _drawingMaxTime, _drawingMinValue, _drawingMaxValue);
        if (_isSelecting) DrawSelection(context, _drawingPlot);
        context.Pop();
    }

    private void BuildStaticDrawing(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var points = _record?.Series;
        _drawingHasData = points != null && points.Count >= 2 && ActualWidth >= 140 && ActualHeight >= (CompactLane ? 18 : 90);
        if (!_drawingHasData)
        { DrawText(context, LocalizationManager.Text("Chart.NoData"), new Point(18, 18), MutedBrush, 12); return; }
        GetTimeRange(points!, out _drawingMinTime, out _drawingMaxTime);
        var visible = points!.Where(point => point.TimeSeconds >= _drawingMinTime && point.TimeSeconds <= _drawingMaxTime).ToArray();
        if (visible.Length < 2)
        { visible = points!.ToArray(); _drawingMinTime = points[0].TimeSeconds; _drawingMaxTime = points[points.Count - 1].TimeSeconds; }
        _drawingPlot = PlotRect();
        GetValueRange(visible, out _drawingMinValue, out _drawingMaxValue);
        if (ShowLegend && !CompactLane) DrawLegend(context); else _legendHitTargets.Clear();
        if (CompactLane) context.DrawLine(GridPen, new Point(_drawingPlot.Left, _drawingPlot.Bottom - .5), new Point(_drawingPlot.Right, _drawingPlot.Bottom - .5));
        else DrawGrid(context, _drawingPlot, _drawingMinTime, _drawingMaxTime, _drawingMinValue, _drawingMaxValue);
        context.PushClip(new RectangleGeometry(_drawingPlot));
        DrawEventMarkers(context, _drawingPlot, _drawingMinTime, _drawingMaxTime, !CompactLane);
        DrawMode(context, visible, _drawingPlot, _drawingMinTime, _drawingMaxTime, _drawingMinValue, _drawingMaxValue);
        context.Pop();
    }


}
