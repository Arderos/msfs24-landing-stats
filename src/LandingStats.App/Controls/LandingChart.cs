using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LandingStats.App.Models;

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

public sealed class LandingChart : FrameworkElement
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
    private static readonly Brush VioletBrush = Brush("#5FA8F5");
    private static readonly Brush BlueBrush = Brush("#5FA8F5");
    private static readonly Brush PinkBrush = Brush("#8FD6A8");
    private static readonly Brush TooltipBrush = Brush("#1F1D1B");
    private static readonly Brush FlareBrush = Brush("#D5A08050");
    private static readonly Brush SelectionBrush = Brush("#33FF7A45");
    private static readonly Brush LaneContactBrush = Brush("#2A2724");
    private static readonly Pen GridPen = Pen(GridBrush, 1);
    private static readonly Pen AccentPen = Pen(AccentBrush, 2.1);
    private static readonly Pen AmberPen = Pen(AmberBrush, 1.8);
    private static readonly Pen VioletPen = Pen(VioletBrush, 1.8);
    private static readonly Pen BluePen = Pen(BlueBrush, 1.8);
    private static readonly Pen PinkPen = Pen(PinkBrush, 1.8);
    private static readonly Pen AccentDashedPen = DashedPen(AccentBrush, 1.3, 5, 4);
    private static readonly Pen AmberDashedPen = DashedPen(AmberBrush, 1.3, 5, 4);
    private static readonly Pen VioletDashedPen = DashedPen(VioletBrush, 1.3, 5, 4);
    private static readonly Pen BlueDashedPen = DashedPen(BlueBrush, 1.3, 5, 4);
    private static readonly Pen ContactPen = Pen(Brush("#33302C"), 1);
    private static readonly Pen LaneContactPen = Pen(LaneContactBrush, 1);
    private static readonly Pen FlarePen = DashedPen(FlareBrush, 1.1, 2, 4);
    private static readonly Pen SurfacePen = DashedPen(VioletBrush, 1.4, 6, 4);
    private static readonly Pen HoverPen = Pen(Brush("#73F5F3F0"), 1);
    private static readonly Pen[] SolidPens = { AccentPen, BluePen, AmberPen, PinkPen };
    private static readonly Pen[] DashedPens = { AccentDashedPen, BlueDashedPen, AmberDashedPen };
    private static readonly Brush[] SeriesBrushes = { AccentBrush, BlueBrush, AmberBrush, PinkBrush };

    private readonly List<LegendHitTarget> _legendHitTargets = new();
    private LandingRecord? _record;
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

    public LandingRecord? Record
    {
        get => _record;
        set
        {
            _record = value;
            _hoveredIndex = -1;
            _isolatedSeriesIndex = null;
            InvalidateVisual();
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
            InvalidateVisual();
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
            InvalidateVisual();
        }
    }

    public bool ShowHoverTooltip
    {
        get => _showHoverTooltip;
        set
        {
            _showHoverTooltip = value;
            InvalidateVisual();
        }
    }

    public bool ShowXAxisLabels
    {
        get => _showXAxisLabels;
        set
        {
            _showXAxisLabels = value;
            InvalidateVisual();
        }
    }

    public bool CompactLane
    {
        get => _compactLane;
        set
        {
            _compactLane = value;
            InvalidateVisual();
        }
    }

    public bool PowerShowThrottle
    {
        get => _powerShowThrottle;
        set
        {
            _powerShowThrottle = value;
            InvalidateVisual();
        }
    }

    public event EventHandler<LandingChartHoverEventArgs>? HoverTimeChanged;

    public event EventHandler<LandingChartZoomEventArgs>? ZoomRangeSelected;

    public void SetIsolatedSeries(int? seriesIndex)
    {
        _isolatedSeriesIndex = seriesIndex;
        InvalidateVisual();
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
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var points = _record?.Series;
        var minimumHeight = CompactLane ? 18 : 90;
        if (points == null || points.Count < 2 || ActualWidth < 140 || ActualHeight < minimumHeight)
        {
            DrawText(context, "No chart data", new Point(18, 18), MutedBrush, 12);
            return;
        }

        GetTimeRange(points, out var minTime, out var maxTime);
        var visiblePoints = points
            .Where(point => point.TimeSeconds >= minTime && point.TimeSeconds <= maxTime)
            .ToArray();
        if (visiblePoints.Length < 2)
        {
            visiblePoints = points.ToArray();
            minTime = points[0].TimeSeconds;
            maxTime = points[points.Count - 1].TimeSeconds;
        }

        var plot = PlotRect();
        GetValueRange(visiblePoints, out var minValue, out var maxValue);

        if (ShowLegend && !CompactLane)
        {
            DrawLegend(context);
        }
        else
        {
            _legendHitTargets.Clear();
        }
        if (CompactLane)
        {
            context.DrawLine(GridPen, new Point(plot.Left, plot.Bottom - 0.5), new Point(plot.Right, plot.Bottom - 0.5));
        }
        else
        {
            DrawGrid(context, plot, minTime, maxTime, minValue, maxValue);
        }
        context.PushClip(new RectangleGeometry(plot));
        DrawEventMarkers(context, plot, minTime, maxTime, !CompactLane);
        DrawMode(context, visiblePoints, plot, minTime, maxTime, minValue, maxValue);

        if (_hoveredIndex >= 0 && _hoveredIndex < points.Count)
        {
            DrawHover(context, points[_hoveredIndex], plot, minTime, maxTime, minValue, maxValue);
        }

        if (_isSelecting)
        {
            DrawSelection(context, plot);
        }

        context.Pop();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        return new Rect(RenderSize).Contains(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        var points = _record?.Series;
        if (points == null || points.Count == 0)
        {
            return;
        }

        var plot = PlotRect();
        var position = eventArgs.GetPosition(this);
        var overLegend = _legendHitTargets.Any(target => target.Bounds.Contains(position));
        Cursor = overLegend ? Cursors.Hand : plot.Contains(position) ? Cursors.Cross : Cursors.Arrow;
        if (overLegend)
        {
            SetHoverTime(null);
            HoverTimeChanged?.Invoke(this, new LandingChartHoverEventArgs(null));
            return;
        }

        if (_isSelecting)
        {
            _selectionCurrent = position;
            InvalidateVisual();
        }

        if (!plot.Contains(position))
        {
            SetHoverTime(null);
            HoverTimeChanged?.Invoke(this, new LandingChartHoverEventArgs(null));
            return;
        }

        GetTimeRange(points, out var minTime, out var maxTime);
        var fraction = Math.Max(0, Math.Min(1, (position.X - plot.Left) / plot.Width));
        var targetTime = minTime + fraction * (maxTime - minTime);
        SetHoverTime(targetTime);
        HoverTimeChanged?.Invoke(this, new LandingChartHoverEventArgs(targetTime));
    }

    protected override void OnMouseLeave(MouseEventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        if (!_isSelecting)
        {
            SetHoverTime(null);
            HoverTimeChanged?.Invoke(this, new LandingChartHoverEventArgs(null));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseLeftButtonDown(eventArgs);
        var position = eventArgs.GetPosition(this);
        var legendTarget = _legendHitTargets.FirstOrDefault(target => target.Bounds.Contains(position));
        if (legendTarget != null)
        {
            _isolatedSeriesIndex = _isolatedSeriesIndex == legendTarget.SeriesIndex
                ? (int?)null
                : legendTarget.SeriesIndex;
            InvalidateVisual();
            eventArgs.Handled = true;
            return;
        }

        if (_record?.Series.Count > 1 && PlotRect().Contains(position))
        {
            _isSelecting = true;
            _selectionStart = position;
            _selectionCurrent = position;
            CaptureMouse();
            eventArgs.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseLeftButtonUp(eventArgs);
        var record = _record;
        if (!_isSelecting || record == null || record.Series.Count < 2)
        {
            return;
        }

        _selectionCurrent = eventArgs.GetPosition(this);
        _isSelecting = false;
        ReleaseMouseCapture();
        var plot = PlotRect();
        var selectedPixels = Math.Abs(_selectionCurrent.X - _selectionStart.X);
        if (selectedPixels >= 8)
        {
            GetTimeRange(record.Series, out var minTime, out var maxTime);
            var left = Math.Max(plot.Left, Math.Min(plot.Right, Math.Min(_selectionStart.X, _selectionCurrent.X)));
            var right = Math.Max(plot.Left, Math.Min(plot.Right, Math.Max(_selectionStart.X, _selectionCurrent.X)));
            var start = minTime + (left - plot.Left) / plot.Width * (maxTime - minTime);
            var end = minTime + (right - plot.Left) / plot.Width * (maxTime - minTime);
            SetZoomRange(start, end);
            ZoomRangeSelected?.Invoke(this, new LandingChartZoomEventArgs(start, end));
        }

        InvalidateVisual();
        eventArgs.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseRightButtonDown(eventArgs);
        SetZoomRange(null, null);
        ZoomRangeSelected?.Invoke(this, new LandingChartZoomEventArgs(null, null));
        eventArgs.Handled = true;
    }

    private Rect PlotRect()
    {
        if (CompactLane)
        {
            return new Rect(0, 0, Math.Max(10, ActualWidth), Math.Max(10, ActualHeight));
        }

        var top = ShowLegend ? 31.0 : 0.0;
        var bottom = ShowXAxisLabels ? 30.0 : 0.0;
        return new Rect(96, top, Math.Max(10, ActualWidth - 96), Math.Max(10, ActualHeight - top - bottom));
    }

    private void GetTimeRange(IReadOnlyList<LandingSeriesPoint> points, out double minimum, out double maximum)
    {
        minimum = _zoomStartSeconds ?? points[0].TimeSeconds;
        maximum = _zoomEndSeconds ?? points[points.Count - 1].TimeSeconds;
        minimum = Math.Max(points[0].TimeSeconds, minimum);
        maximum = Math.Min(points[points.Count - 1].TimeSeconds, maximum);
        if (maximum - minimum < 0.05)
        {
            minimum = points[0].TimeSeconds;
            maximum = points[points.Count - 1].TimeSeconds;
        }
    }

    private void DrawSelection(DrawingContext context, Rect plot)
    {
        var left = Math.Max(plot.Left, Math.Min(plot.Right, Math.Min(_selectionStart.X, _selectionCurrent.X)));
        var right = Math.Max(plot.Left, Math.Min(plot.Right, Math.Max(_selectionStart.X, _selectionCurrent.X)));
        context.DrawRectangle(SelectionBrush, AccentPen, new Rect(left, plot.Top, Math.Max(1, right - left), plot.Height));
    }

    private void DrawLegend(DrawingContext context)
    {
        _legendHitTargets.Clear();
        var x = 50.0;
        switch (Mode)
        {
            case LandingChartMode.VerticalSpeed:
                x = DrawLegendItem(context, x, "inertial", AccentPen, 0);
                x = DrawLegendItem(context, x, "VSI", AmberPen, 1);
                if (HasSurfaceLatchData)
                {
                    DrawLegendItem(context, x, "MSFS latch", SurfacePen, 2);
                }
                else
                {
                    context.PushOpacity(0.45);
                    context.DrawLine(SurfacePen, new Point(x, 9), new Point(x + 14, 9));
                    DrawText(context, "MSFS latch n/a", new Point(x + 19, 2), MutedBrush, 10);
                    context.Pop();
                }
                break;
            case LandingChartMode.LoadFactors:
                x = DrawLegendItem(context, x, "vertical", AccentPen, 0);
                if (HasHorizontalLoadData)
                {
                    x = DrawLegendItem(context, x, "longitudinal", AmberPen, 1);
                    DrawLegendItem(context, x, "lateral", VioletPen, 2);
                }
                else
                {
                    DrawText(context, "horizontal G was not recorded in this landing", new Point(x + 4, 2), MutedBrush, 10);
                }
                break;
            case LandingChartMode.FlightControls:
                x = DrawLegendItem(context, x, _record?.HasRawPitchInput == true ? "pitch raw" : "pitch (sim)", AccentPen, 0);
                x = DrawLegendItem(context, x, "roll", AmberPen, 1);
                x = DrawLegendItem(context, x, "yaw", VioletPen, 2);
                DrawText(context, "solid input · dashed surface", new Point(x + 4, 2), MutedBrush, 10);
                break;
            case LandingChartMode.Attitude:
                x = DrawLegendItem(context, x, "pitch", AccentPen, 0);
                x = DrawLegendItem(context, x, "bank", AmberPen, 1);
                DrawLegendItem(context, x, "AoA", VioletPen, 2);
                break;
            case LandingChartMode.Power:
                if (_record != null)
                {
                    for (var index = 0; index < _record.Engines.Count; index++)
                    {
                        x = DrawLegendItem(context, x, $"ENG {_record.Engines[index].EngineNumber}", SolidPens[index % SolidPens.Length], index);
                    }
                }

                DrawText(context, "solid N1/RPM · dashed throttle", new Point(x + 4, 2), MutedBrush, 10);
                break;
            case LandingChartMode.Gear:
                if (_record != null)
                {
                    for (var index = 0; index < _record.ContactPoints.Count; index++)
                    {
                        x = DrawLegendItem(context, x, $"CP {_record.ContactPoints[index].ContactPointIndex}", SolidPens[index % SolidPens.Length], index);
                    }
                }
                break;
        }
    }

    private double DrawLegendItem(DrawingContext context, double x, string label, Pen pen, int seriesIndex)
    {
        var isolated = _isolatedSeriesIndex.HasValue;
        var active = !isolated || _isolatedSeriesIndex == seriesIndex;
        var formatted = Formatted(label, active ? TextBrush : MutedBrush, 10);
        var width = 25 + formatted.Width;
        var bounds = new Rect(x - 5, 0, width + 5, 20);
        if (_isolatedSeriesIndex == seriesIndex)
        {
            context.DrawRoundedRectangle(SelectionBrush, null, bounds, 4, 4);
        }

        context.PushOpacity(active ? 1.0 : 0.28);
        context.DrawLine(pen, new Point(x, 9), new Point(x + 14, 9));
        context.DrawText(formatted, new Point(x + 19, 2));
        context.Pop();
        _legendHitTargets.Add(new LegendHitTarget(bounds, seriesIndex));
        return x + width;
    }

    private bool IsSeriesVisible(int seriesIndex) => !_isolatedSeriesIndex.HasValue || _isolatedSeriesIndex == seriesIndex;

    private double PitchInput(LandingSeriesPoint point) => _record?.PitchInputPercent(point) ?? point.PilotPitchPercent;

    private void GetValueRange(IReadOnlyList<LandingSeriesPoint> points, out double minimum, out double maximum)
    {
        if (CompactLane)
        {
            switch (Mode)
            {
                case LandingChartMode.LoadFactors:
                    minimum = 0.8;
                    maximum = 1.6;
                    return;
                case LandingChartMode.Power:
                    minimum = 0;
                    maximum = 100;
                    return;
                case LandingChartMode.Gear:
                    minimum = 0;
                    maximum = 70;
                    return;
            }
        }

        switch (Mode)
        {
            case LandingChartMode.VerticalSpeed:
                IEnumerable<double> verticalValues = _isolatedSeriesIndex switch
                {
                    0 => points.Select(point => -point.InertialFpm),
                    1 => points.Select(point => -point.IndicatedFpm),
                    2 when HasSurfaceLatchData => new[] { -_record!.SurfaceFpm },
                    _ => HasSurfaceLatchData
                        ? points.SelectMany(point => new[] { -point.InertialFpm, -point.IndicatedFpm })
                            .Concat(new[] { -_record!.SurfaceFpm })
                        : points.SelectMany(point => new[] { -point.InertialFpm, -point.IndicatedFpm }),
                };
                var verticalMinimum = Math.Min(0, verticalValues.Min());
                var verticalMaximum = Math.Max(0, verticalValues.Max());
                var verticalPadding = Math.Max(30, (verticalMaximum - verticalMinimum) * 0.05);
                minimum = Math.Floor((verticalMinimum - verticalPadding) / 100.0) * 100.0;
                maximum = Math.Ceiling((verticalMaximum + verticalPadding) / 100.0) * 100.0;
                break;
            case LandingChartMode.LoadFactors:
                if (HasHorizontalLoadData && (_isolatedSeriesIndex == 1 || _isolatedSeriesIndex == 2))
                {
                    var horizontalValues = _isolatedSeriesIndex == 1
                        ? points.Select(point => point.LongitudinalLoadG)
                        : points.Select(point => point.LateralLoadG);
                    var horizontalMinimum = Math.Min(0, horizontalValues.Min());
                    var horizontalMaximum = Math.Max(0, horizontalValues.Max());
                    var horizontalPadding = Math.Max(0.04, (horizontalMaximum - horizontalMinimum) * 0.12);
                    minimum = Math.Floor((horizontalMinimum - horizontalPadding) * 10) / 10.0;
                    maximum = Math.Ceiling((horizontalMaximum + horizontalPadding) * 10) / 10.0;
                }
                else
                {
                    minimum = HasHorizontalLoadData && !_isolatedSeriesIndex.HasValue
                        ? Math.Min(-0.3, points.Min(point => Math.Min(point.LongitudinalLoadG, point.LateralLoadG)) - 0.08)
                        : Math.Min(0.8, points.Min(point => point.GForce) - 0.08);
                    maximum = Math.Max(1.2, points.Max(point => point.GForce) + 0.08);
                }
                break;
            case LandingChartMode.FlightControls:
                IEnumerable<double> controlValues = _isolatedSeriesIndex switch
                {
                    0 => points.SelectMany(point => new[] { PitchInput(point), point.ElevatorPercent }),
                    1 => points.SelectMany(point => new[] { point.PilotRollPercent, point.AileronPercent }),
                    2 => points.SelectMany(point => new[] { point.PilotYawPercent, point.RudderPercent }),
                    _ => points.SelectMany(point => new[]
                    {
                        PitchInput(point), point.PilotRollPercent, point.PilotYawPercent,
                        point.ElevatorPercent, point.AileronPercent, point.RudderPercent,
                    }),
                };
                var controlMinimum = controlValues.Min();
                var controlMaximum = controlValues.Max();
                controlMinimum = Math.Min(0, controlMinimum);
                controlMaximum = Math.Max(0, controlMaximum);
                var controlSpan = Math.Max(8, controlMaximum - controlMinimum);
                var controlPadding = Math.Max(2, controlSpan * 0.12);
                minimum = Math.Max(-100, Math.Floor((controlMinimum - controlPadding) / 5.0) * 5.0);
                maximum = Math.Min(100, Math.Ceiling((controlMaximum + controlPadding) / 5.0) * 5.0);
                if (maximum - minimum < 10)
                {
                    minimum -= 5;
                    maximum += 5;
                }
                break;
            case LandingChartMode.Attitude:
                IEnumerable<double> attitudeValues = _isolatedSeriesIndex switch
                {
                    0 => points.Select(point => -point.PitchDegrees),
                    1 => points.Select(point => point.BankDegrees),
                    2 => points.Select(point => point.AngleOfAttackDegrees),
                    _ => points.SelectMany(point => new[] { -point.PitchDegrees, point.BankDegrees, point.AngleOfAttackDegrees }),
                };
                minimum = Math.Floor(Math.Min(0, attitudeValues.Min()) - 1);
                maximum = Math.Ceiling(Math.Max(0, attitudeValues.Max()) + 1);
                if (maximum - minimum < 10)
                {
                    maximum = minimum + 10;
                }
                break;
            case LandingChartMode.Power:
                minimum = 0;
                maximum = 110;
                break;
            case LandingChartMode.Gear:
                minimum = 0;
                maximum = 100;
                break;
            default:
                minimum = 0;
                maximum = 1;
                break;
        }
    }

    private void DrawMode(
        DrawingContext context,
        IReadOnlyList<LandingSeriesPoint> points,
        Rect plot,
        double minTime,
        double maxTime,
        double minValue,
        double maxValue)
    {
        switch (Mode)
        {
            case LandingChartMode.VerticalSpeed:
                if (IsSeriesVisible(1))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => -point.IndicatedFpm, plot, minTime, maxTime, minValue, maxValue, AmberPen);
                }
                if (IsSeriesVisible(0))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => -point.InertialFpm, plot, minTime, maxTime, minValue, maxValue, AccentPen);
                }
                if (HasSurfaceLatchData && IsSeriesVisible(2))
                {
                    var surfaceY = MapY(-_record!.SurfaceFpm, plot, minValue, maxValue);
                    context.DrawLine(SurfacePen, new Point(plot.Left, surfaceY), new Point(plot.Right, surfaceY));
                }
                DrawBaseline(context, plot, 0, minValue, maxValue);
                break;
            case LandingChartMode.LoadFactors:
                if (HasHorizontalLoadData)
                {
                    if (IsSeriesVisible(1))
                    {
                        DrawSeries(context, points, point => point.TimeSeconds, point => point.LongitudinalLoadG, plot, minTime, maxTime, minValue, maxValue, BluePen);
                    }
                    if (IsSeriesVisible(2))
                    {
                        DrawSeries(context, points, point => point.TimeSeconds, point => point.LateralLoadG, plot, minTime, maxTime, minValue, maxValue, AmberPen);
                    }
                    if (_isolatedSeriesIndex != 0)
                    {
                        DrawBaseline(context, plot, 0, minValue, maxValue);
                    }
                }
                if (IsSeriesVisible(0))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.GForce, plot, minTime, maxTime, minValue, maxValue, CompactLane ? PinkPen : AccentPen);
                    DrawBaseline(context, plot, 1, minValue, maxValue);
                }
                break;
            case LandingChartMode.FlightControls:
                if (IsSeriesVisible(0))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, PitchInput, plot, minTime, maxTime, minValue, maxValue, AccentPen);
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.ElevatorPercent, plot, minTime, maxTime, minValue, maxValue, AccentDashedPen);
                }
                if (IsSeriesVisible(1))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.PilotRollPercent, plot, minTime, maxTime, minValue, maxValue, BluePen);
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.AileronPercent, plot, minTime, maxTime, minValue, maxValue, BlueDashedPen);
                }
                if (IsSeriesVisible(2))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.PilotYawPercent, plot, minTime, maxTime, minValue, maxValue, AmberPen);
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.RudderPercent, plot, minTime, maxTime, minValue, maxValue, AmberDashedPen);
                }
                DrawBaseline(context, plot, 0, minValue, maxValue);
                break;
            case LandingChartMode.Attitude:
                if (IsSeriesVisible(1))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.BankDegrees, plot, minTime, maxTime, minValue, maxValue, BluePen);
                }
                if (IsSeriesVisible(2))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.AngleOfAttackDegrees, plot, minTime, maxTime, minValue, maxValue, AmberPen);
                }
                if (IsSeriesVisible(0))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => -point.PitchDegrees, plot, minTime, maxTime, minValue, maxValue, AccentPen);
                }
                DrawBaseline(context, plot, 0, minValue, maxValue);
                break;
            case LandingChartMode.Power:
                DrawPower(context, plot, minTime, maxTime, minValue, maxValue);
                break;
            case LandingChartMode.Gear:
                DrawGear(context, plot, minTime, maxTime, minValue, maxValue);
                break;
        }
    }

    private void DrawPower(DrawingContext context, Rect plot, double minTime, double maxTime, double minValue, double maxValue)
    {
        if (_record == null)
        {
            return;
        }

        for (var index = 0; index < _record.Engines.Count; index++)
        {
            if (!IsSeriesVisible(index))
            {
                continue;
            }

            var engine = _record.Engines[index];
            var solid = CompactLane ? BluePen : SolidPens[index % SolidPens.Length];
            var dashed = DashedPens[index % DashedPens.Length];
            var hasN1 = engine.Points.Any(point => Math.Abs(point.N1Percent) > 0.01);
            var maximumRpm = engine.Points.Count == 0 ? 1 : Math.Max(1, engine.Points.Max(point => point.Rpm));
            DrawSeries(context, engine.Points, point => point.TimeSeconds,
                point => hasN1 ? point.N1Percent : point.Rpm / maximumRpm * 100.0,
                plot, minTime, maxTime, minValue, maxValue, solid);
            if (PowerShowThrottle)
            {
                DrawSeries(context, engine.Points, point => point.TimeSeconds, point => point.ThrottlePercent,
                    plot, minTime, maxTime, minValue, maxValue, dashed);
            }
        }
    }

    private void DrawGear(DrawingContext context, Rect plot, double minTime, double maxTime, double minValue, double maxValue)
    {
        if (_record == null)
        {
            return;
        }

        for (var index = 0; index < _record.ContactPoints.Count; index++)
        {
            if (!IsSeriesVisible(index))
            {
                continue;
            }

            var contact = _record.ContactPoints[index];
            DrawSeries(context, contact.Points, point => point.TimeSeconds, point => point.CompressionPercent,
                plot, minTime, maxTime, minValue, maxValue, CompactLane ? AmberPen : SolidPens[index % SolidPens.Length]);
        }
    }

    private void DrawEventMarkers(DrawingContext context, Rect plot, double minTime, double maxTime, bool drawLabels)
    {
        if (minTime <= 0 && maxTime >= 0)
        {
            var touchdownX = MapX(0, plot, minTime, maxTime);
            context.DrawLine(CompactLane ? LaneContactPen : ContactPen, new Point(touchdownX, plot.Top), new Point(touchdownX, plot.Bottom));
            if (drawLabels)
            {
                DrawText(context, "TOUCHDOWN", new Point(touchdownX + 6, plot.Top + 3), MutedBrush, 9);
            }
        }

        if (!CompactLane && _record?.FlareStartSeconds is double flare && flare >= minTime && flare <= maxTime)
        {
            var flareX = MapX(flare, plot, minTime, maxTime);
            context.DrawLine(FlarePen, new Point(flareX, plot.Top), new Point(flareX, plot.Bottom));
            if (drawLabels)
            {
                DrawText(context, "FLARE", new Point(flareX + 6, plot.Top + 3), FlareBrush, 9);
            }
        }
    }

    private void DrawGrid(DrawingContext context, Rect plot, double minTime, double maxTime, double minValue, double maxValue)
    {
        for (var index = 0; index <= 4; index++)
        {
            var y = plot.Top + index * plot.Height / 4.0;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var value = maxValue - index * (maxValue - minValue) / 4.0;
            var valueLabel = Mode switch
            {
                LandingChartMode.LoadFactors => value.ToString("F1", CultureInfo.CurrentCulture),
                LandingChartMode.VerticalSpeed => value.ToString("+0;-0;0", CultureInfo.CurrentCulture),
                _ => value.ToString("F0", CultureInfo.CurrentCulture),
            };
            DrawText(context, valueLabel, new Point(plot.Left, y - 8), MutedBrush, 10);
        }

        var tickInterval = maxTime - minTime > 35 ? 10.0 : 5.0;
        var firstTick = Math.Ceiling(minTime / tickInterval) * tickInterval;
        for (var tick = firstTick; tick <= maxTime + 0.001; tick += tickInterval)
        {
            var x = MapX(tick, plot, minTime, maxTime);
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = tick == 0 ? "TD" : $"{tick:+0;-0;0}s";
            if (ShowXAxisLabels)
            {
                DrawText(context, label, new Point(x - 10, plot.Bottom + 7), tick == 0 ? TextBrush : MutedBrush, 10);
            }
        }
    }

    private static void DrawBaseline(DrawingContext context, Rect plot, double value, double minValue, double maxValue)
    {
        if (value >= minValue && value <= maxValue)
        {
            var y = MapY(value, plot, minValue, maxValue);
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
    }

    private static void DrawSeries<T>(
        DrawingContext context,
        IReadOnlyList<T> points,
        Func<T, double> selectTime,
        Func<T, double> selectValue,
        Rect plot,
        double minTime,
        double maxTime,
        double minValue,
        double maxValue,
        Pen pen)
    {
        if (points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            for (var index = 0; index < points.Count; index++)
            {
                var x = MapX(selectTime(points[index]), plot, minTime, maxTime);
                var y = MapY(selectValue(points[index]), plot, minValue, maxValue);
                if (index == 0)
                {
                    geometryContext.BeginFigure(new Point(x, y), false, false);
                }
                else
                {
                    geometryContext.LineTo(new Point(x, y), true, false);
                }
            }
        }

        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawHover(
        DrawingContext context,
        LandingSeriesPoint point,
        Rect plot,
        double minTime,
        double maxTime,
        double minValue,
        double maxValue)
    {
        var x = MapX(point.TimeSeconds, plot, minTime, maxTime);
        context.DrawLine(HoverPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

        if (!ShowHoverTooltip)
        {
            return;
        }

        DrawHoverFlags(context, point, x, plot, minValue, maxValue);
    }

    private void DrawHoverFlags(
        DrawingContext context,
        LandingSeriesPoint point,
        double x,
        Rect plot,
        double minValue,
        double maxValue)
    {
        var flags = new List<HoverFlag>();

        void AddFlag(double value, Brush brush, bool visible)
        {
            if (visible)
            {
                flags.Add(new HoverFlag(FormatFlagValue(value), brush, MapY(value, plot, minValue, maxValue)));
            }
        }

        switch (Mode)
        {
            case LandingChartMode.VerticalSpeed:
                AddFlag(-point.InertialFpm, AccentBrush, IsSeriesVisible(0));
                AddFlag(-point.IndicatedFpm, AmberBrush, IsSeriesVisible(1));
                if (HasSurfaceLatchData)
                {
                    AddFlag(-_record!.SurfaceFpm, BlueBrush, IsSeriesVisible(2));
                }
                break;
            case LandingChartMode.LoadFactors:
                AddFlag(point.GForce, CompactLane ? PinkBrush : AccentBrush, IsSeriesVisible(0));
                if (HasHorizontalLoadData)
                {
                    AddFlag(point.LongitudinalLoadG, BlueBrush, IsSeriesVisible(1));
                    AddFlag(point.LateralLoadG, AmberBrush, IsSeriesVisible(2));
                }
                break;
            case LandingChartMode.FlightControls:
                AddFlag(PitchInput(point), AccentBrush, IsSeriesVisible(0));
                AddFlag(point.PilotRollPercent, BlueBrush, IsSeriesVisible(1));
                AddFlag(point.PilotYawPercent, AmberBrush, IsSeriesVisible(2));
                break;
            case LandingChartMode.Attitude:
                AddFlag(-point.PitchDegrees, AccentBrush, IsSeriesVisible(0));
                AddFlag(point.BankDegrees, BlueBrush, IsSeriesVisible(1));
                AddFlag(point.AngleOfAttackDegrees, AmberBrush, IsSeriesVisible(2));
                break;
            case LandingChartMode.Power:
                if (_record == null)
                {
                    break;
                }
                for (var index = 0; index < _record.Engines.Count; index++)
                {
                    if (!IsSeriesVisible(index))
                    {
                        continue;
                    }

                    var enginePoint = EnginePointAt(point.TimeSeconds, index);
                    if (enginePoint == null)
                    {
                        continue;
                    }

                    var engine = _record.Engines[index];
                    var hasN1 = engine.Points.Any(candidate => Math.Abs(candidate.N1Percent) > 0.01);
                    var maximumRpm = engine.Points.Count == 0 ? 1 : Math.Max(1, engine.Points.Max(candidate => candidate.Rpm));
                    var power = hasN1 ? enginePoint.N1Percent : enginePoint.Rpm / maximumRpm * 100.0;
                    AddFlag(power, CompactLane ? BlueBrush : SeriesBrushes[index % SeriesBrushes.Length], true);
                }
                break;
            case LandingChartMode.Gear:
                if (_record == null)
                {
                    break;
                }
                for (var index = 0; index < _record.ContactPoints.Count; index++)
                {
                    if (!IsSeriesVisible(index))
                    {
                        continue;
                    }

                    var contactPoint = ContactPointAt(point.TimeSeconds, index);
                    if (contactPoint != null)
                    {
                        AddFlag(contactPoint.CompressionPercent, CompactLane ? AmberBrush : SeriesBrushes[index % SeriesBrushes.Length], true);
                    }
                }
                break;
        }

        if (flags.Count == 0)
        {
            return;
        }

        const double gap = 22;
        flags.Sort((left, right) => left.Y.CompareTo(right.Y));
        for (var index = 1; index < flags.Count; index++)
        {
            if (flags[index].Y - flags[index - 1].Y < gap)
            {
                flags[index].Y = flags[index - 1].Y + gap;
            }
        }

        var maximumY = plot.Bottom - gap / 2;
        var overflow = flags[flags.Count - 1].Y - maximumY;
        if (overflow > 0)
        {
            foreach (var flag in flags)
            {
                flag.Y -= overflow;
            }
        }

        var minimumY = plot.Top + gap / 2;
        var underflow = minimumY - flags[0].Y;
        if (underflow > 0)
        {
            foreach (var flag in flags)
            {
                flag.Y += underflow;
            }
        }

        foreach (var flag in flags)
        {
            var formatted = FormattedMono(flag.Value, flag.Brush, 10);
            var width = formatted.Width + 12;
            var height = formatted.Height + 4;
            var left = Math.Max(plot.Left, Math.Min(plot.Right - width, x - width / 2));
            var top = flag.Y - height / 2;
            context.DrawRoundedRectangle(TooltipBrush, null, new Rect(left, top, width, height), 4, 4);
            context.DrawText(formatted, new Point(left + 6, top + 2));
        }
    }

    private string FormatFlagValue(double value)
    {
        return Mode switch
        {
            LandingChartMode.LoadFactors => value.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture),
            LandingChartMode.Attitude => value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture),
            LandingChartMode.Power => value.ToString("0.0", CultureInfo.CurrentCulture),
            LandingChartMode.Gear => value.ToString("0.0", CultureInfo.CurrentCulture),
            _ => value.ToString("+0;-0;0", CultureInfo.CurrentCulture),
        };
    }

    private bool HasSurfaceLatchData => _record?.HasSurfaceLatchData == true;

    private bool HasHorizontalLoadData => (_record?.FormatVersion ?? 0) >= 3;

    private LandingEnginePoint? EnginePointAt(double time, int engineIndex)
    {
        var points = _record != null && engineIndex >= 0 && engineIndex < _record.Engines.Count
            ? _record.Engines[engineIndex].Points
            : null;
        if (points == null || points.Count == 0)
        {
            return null;
        }

        return points[ClosestIndex(points.Count, index => points[index].TimeSeconds, time)];
    }

    private LandingContactPoint? ContactPointAt(double time, int contactIndex)
    {
        var points = _record != null && contactIndex >= 0 && contactIndex < _record.ContactPoints.Count
            ? _record.ContactPoints[contactIndex].Points
            : null;
        if (points == null || points.Count == 0)
        {
            return null;
        }

        return points[ClosestIndex(points.Count, index => points[index].TimeSeconds, time)];
    }

    private static int ClosestIndex(int count, Func<int, double> timeAt, double target)
    {
        var nearest = 0;
        var distance = double.MaxValue;
        for (var index = 0; index < count; index++)
        {
            var candidate = Math.Abs(timeAt(index) - target);
            if (candidate < distance)
            {
                distance = candidate;
                nearest = index;
            }
        }

        return nearest;
    }

    private static double MapX(double value, Rect plot, double minimum, double maximum)
    {
        return plot.Left + (value - minimum) / Math.Max(0.000001, maximum - minimum) * plot.Width;
    }

    private static double MapY(double value, Rect plot, double minimum, double maximum)
    {
        var normalized = (value - minimum) / Math.Max(0.000001, maximum - minimum);
        return plot.Bottom - Math.Max(0, Math.Min(1, normalized)) * plot.Height;
    }

    private static void DrawText(DrawingContext context, string text, Point origin, Brush brush, double size)
    {
        context.DrawText(Formatted(text, brush, size), origin);
    }

    private static FormattedText Formatted(string text, Brush brush, double size)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            1.0);
    }

    private static FormattedText FormattedMono(string text, Brush brush, double size)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono"),
            size,
            brush,
            1.0);
    }

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen DashedPen(Brush brush, double thickness, double dash, double gap)
    {
        var pen = new Pen(brush, thickness) { DashStyle = new DashStyle(new[] { dash, gap }, 0) };
        pen.Freeze();
        return pen;
    }
}
