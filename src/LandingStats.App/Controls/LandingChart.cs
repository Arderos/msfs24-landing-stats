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

    private static readonly Brush GridBrush = Brush("#22303C");
    private static readonly Brush MutedBrush = Brush("#7F8D9C");
    private static readonly Brush TextBrush = Brush("#EAF0F4");
    private static readonly Brush AccentBrush = Brush("#55DFC0");
    private static readonly Brush AmberBrush = Brush("#F5C66E");
    private static readonly Brush VioletBrush = Brush("#A78BFA");
    private static readonly Brush BlueBrush = Brush("#67B7F7");
    private static readonly Brush PinkBrush = Brush("#F58BA7");
    private static readonly Brush TooltipBrush = Brush("#F017202B");
    private static readonly Brush FlareBrush = Brush("#D5F5C66E");
    private static readonly Brush SelectionBrush = Brush("#3555DFC0");
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
    private static readonly Pen ContactPen = DashedPen(VioletBrush, 1.2, 3, 4);
    private static readonly Pen FlarePen = DashedPen(FlareBrush, 1.1, 2, 4);
    private static readonly Pen SurfacePen = DashedPen(VioletBrush, 1.4, 6, 4);
    private static readonly Pen[] SolidPens = { AccentPen, AmberPen, VioletPen, BluePen, PinkPen };
    private static readonly Pen[] DashedPens = { AccentDashedPen, AmberDashedPen, VioletDashedPen, BlueDashedPen };
    private static readonly Brush[] SeriesBrushes = { AccentBrush, AmberBrush, VioletBrush, BlueBrush, PinkBrush };

    private readonly List<LegendHitTarget> _legendHitTargets = new();
    private LandingRecord? _record;
    private int _hoveredIndex = -1;
    private int? _isolatedSeriesIndex;
    private double? _zoomStartSeconds;
    private double? _zoomEndSeconds;
    private bool _isSelecting;
    private Point _selectionStart;
    private Point _selectionCurrent;

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

    public LandingChartMode Mode { get; set; }

    public event EventHandler<LandingChartHoverEventArgs>? HoverTimeChanged;

    public event EventHandler<LandingChartZoomEventArgs>? ZoomRangeSelected;

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
        var points = _record?.Series;
        if (points == null || points.Count < 2 || ActualWidth < 140 || ActualHeight < 90)
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

        DrawLegend(context);
        DrawGrid(context, plot, minTime, maxTime, minValue, maxValue);
        context.PushClip(new RectangleGeometry(plot));
        DrawEventMarkers(context, plot, minTime, maxTime);
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
        const double top = 31.0;
        return new Rect(50, top, Math.Max(10, ActualWidth - 68), Math.Max(10, ActualHeight - top - 30));
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
                x = DrawLegendItem(context, x, "pitch", AccentPen, 0);
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

    private void GetValueRange(IReadOnlyList<LandingSeriesPoint> points, out double minimum, out double maximum)
    {
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
                    0 => points.SelectMany(point => new[] { point.PilotPitchPercent, point.ElevatorPercent }),
                    1 => points.SelectMany(point => new[] { point.PilotRollPercent, point.AileronPercent }),
                    2 => points.SelectMany(point => new[] { point.PilotYawPercent, point.RudderPercent }),
                    _ => points.SelectMany(point => new[]
                    {
                        point.PilotPitchPercent, point.PilotRollPercent, point.PilotYawPercent,
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
                    0 => points.Select(point => point.PitchDegrees),
                    1 => points.Select(point => point.BankDegrees),
                    2 => points.Select(point => point.AngleOfAttackDegrees),
                    _ => points.SelectMany(point => new[] { point.PitchDegrees, point.BankDegrees, point.AngleOfAttackDegrees }),
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
                        DrawSeries(context, points, point => point.TimeSeconds, point => point.LongitudinalLoadG, plot, minTime, maxTime, minValue, maxValue, AmberPen);
                    }
                    if (IsSeriesVisible(2))
                    {
                        DrawSeries(context, points, point => point.TimeSeconds, point => point.LateralLoadG, plot, minTime, maxTime, minValue, maxValue, VioletPen);
                    }
                    if (_isolatedSeriesIndex != 0)
                    {
                        DrawBaseline(context, plot, 0, minValue, maxValue);
                    }
                }
                if (IsSeriesVisible(0))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.GForce, plot, minTime, maxTime, minValue, maxValue, AccentPen);
                    DrawBaseline(context, plot, 1, minValue, maxValue);
                }
                break;
            case LandingChartMode.FlightControls:
                if (IsSeriesVisible(0))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.PilotPitchPercent, plot, minTime, maxTime, minValue, maxValue, AccentPen);
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.ElevatorPercent, plot, minTime, maxTime, minValue, maxValue, AccentDashedPen);
                }
                if (IsSeriesVisible(1))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.PilotRollPercent, plot, minTime, maxTime, minValue, maxValue, AmberPen);
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.AileronPercent, plot, minTime, maxTime, minValue, maxValue, AmberDashedPen);
                }
                if (IsSeriesVisible(2))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.PilotYawPercent, plot, minTime, maxTime, minValue, maxValue, VioletPen);
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.RudderPercent, plot, minTime, maxTime, minValue, maxValue, VioletDashedPen);
                }
                DrawBaseline(context, plot, 0, minValue, maxValue);
                break;
            case LandingChartMode.Attitude:
                if (IsSeriesVisible(1))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.BankDegrees, plot, minTime, maxTime, minValue, maxValue, AmberPen);
                }
                if (IsSeriesVisible(2))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.AngleOfAttackDegrees, plot, minTime, maxTime, minValue, maxValue, VioletPen);
                }
                if (IsSeriesVisible(0))
                {
                    DrawSeries(context, points, point => point.TimeSeconds, point => point.PitchDegrees, plot, minTime, maxTime, minValue, maxValue, AccentPen);
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
            var solid = SolidPens[index % SolidPens.Length];
            var dashed = DashedPens[index % DashedPens.Length];
            var hasN1 = engine.Points.Any(point => Math.Abs(point.N1Percent) > 0.01);
            var maximumRpm = engine.Points.Count == 0 ? 1 : Math.Max(1, engine.Points.Max(point => point.Rpm));
            DrawSeries(context, engine.Points, point => point.TimeSeconds,
                point => hasN1 ? point.N1Percent : point.Rpm / maximumRpm * 100.0,
                plot, minTime, maxTime, minValue, maxValue, solid);
            DrawSeries(context, engine.Points, point => point.TimeSeconds, point => point.ThrottlePercent,
                plot, minTime, maxTime, minValue, maxValue, dashed);
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
                plot, minTime, maxTime, minValue, maxValue, SolidPens[index % SolidPens.Length]);
        }
    }

    private void DrawEventMarkers(DrawingContext context, Rect plot, double minTime, double maxTime)
    {
        if (minTime <= 0 && maxTime >= 0)
        {
            var touchdownX = MapX(0, plot, minTime, maxTime);
            context.DrawLine(ContactPen, new Point(touchdownX, plot.Top), new Point(touchdownX, plot.Bottom));
        }

        if (_record?.FlareStartSeconds is double flare && flare >= minTime && flare <= maxTime)
        {
            var flareX = MapX(flare, plot, minTime, maxTime);
            context.DrawLine(FlarePen, new Point(flareX, plot.Top), new Point(flareX, plot.Bottom));
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
            DrawText(context, valueLabel, new Point(5, y - 8), MutedBrush, 11);
        }

        var tickInterval = maxTime - minTime > 35 ? 10.0 : 5.0;
        var firstTick = Math.Ceiling(minTime / tickInterval) * tickInterval;
        for (var tick = firstTick; tick <= maxTime + 0.001; tick += tickInterval)
        {
            var x = MapX(tick, plot, minTime, maxTime);
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = tick == 0 ? "TD" : $"{tick:+0;-0;0}s";
            DrawText(context, label, new Point(x - 10, plot.Bottom + 7), tick == 0 ? VioletBrush : MutedBrush, 11);
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
        context.DrawLine(new Pen(TextBrush, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
        DrawHoverMarkers(context, point, x, plot, minValue, maxValue);

        var text = HoverText(point);
        var formatted = Formatted(text, TextBrush, 11);
        var width = formatted.Width + 18;
        var height = formatted.Height + 12;
        var left = Math.Max(plot.Left + 4, Math.Min(plot.Right - width - 4, x - width / 2));
        var top = plot.Top + 6;
        context.DrawRoundedRectangle(TooltipBrush, new Pen(GridBrush, 1), new Rect(left, top, width, height), 7, 7);
        context.DrawText(formatted, new Point(left + 9, top + 6));
    }

    private void DrawHoverMarkers(
        DrawingContext context,
        LandingSeriesPoint point,
        double x,
        Rect plot,
        double minValue,
        double maxValue)
    {
        switch (Mode)
        {
            case LandingChartMode.VerticalSpeed:
                DrawHoverMarker(context, x, -point.InertialFpm, plot, minValue, maxValue, AccentBrush, IsSeriesVisible(0));
                DrawHoverMarker(context, x, -point.IndicatedFpm, plot, minValue, maxValue, AmberBrush, IsSeriesVisible(1));
                if (HasSurfaceLatchData)
                {
                    DrawHoverMarker(context, x, -_record!.SurfaceFpm, plot, minValue, maxValue, VioletBrush, IsSeriesVisible(2));
                }
                break;
            case LandingChartMode.LoadFactors:
                DrawHoverMarker(context, x, point.GForce, plot, minValue, maxValue, AccentBrush, IsSeriesVisible(0));
                if (HasHorizontalLoadData)
                {
                    DrawHoverMarker(context, x, point.LongitudinalLoadG, plot, minValue, maxValue, AmberBrush, IsSeriesVisible(1));
                    DrawHoverMarker(context, x, point.LateralLoadG, plot, minValue, maxValue, VioletBrush, IsSeriesVisible(2));
                }
                break;
            case LandingChartMode.FlightControls:
                DrawHoverMarker(context, x, point.PilotPitchPercent, plot, minValue, maxValue, AccentBrush, IsSeriesVisible(0));
                DrawHoverMarker(context, x, point.ElevatorPercent, plot, minValue, maxValue, AccentBrush, IsSeriesVisible(0), false);
                DrawHoverMarker(context, x, point.PilotRollPercent, plot, minValue, maxValue, AmberBrush, IsSeriesVisible(1));
                DrawHoverMarker(context, x, point.AileronPercent, plot, minValue, maxValue, AmberBrush, IsSeriesVisible(1), false);
                DrawHoverMarker(context, x, point.PilotYawPercent, plot, minValue, maxValue, VioletBrush, IsSeriesVisible(2));
                DrawHoverMarker(context, x, point.RudderPercent, plot, minValue, maxValue, VioletBrush, IsSeriesVisible(2), false);
                break;
            case LandingChartMode.Attitude:
                DrawHoverMarker(context, x, point.PitchDegrees, plot, minValue, maxValue, AccentBrush, IsSeriesVisible(0));
                DrawHoverMarker(context, x, point.BankDegrees, plot, minValue, maxValue, AmberBrush, IsSeriesVisible(1));
                DrawHoverMarker(context, x, point.AngleOfAttackDegrees, plot, minValue, maxValue, VioletBrush, IsSeriesVisible(2));
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
                    var brush = SeriesBrushes[index % SeriesBrushes.Length];
                    DrawHoverMarker(context, x, power, plot, minValue, maxValue, brush, true);
                    DrawHoverMarker(context, x, enginePoint.ThrottlePercent, plot, minValue, maxValue, brush, true, false);
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
                        DrawHoverMarker(context, x, contactPoint.CompressionPercent, plot, minValue, maxValue,
                            SeriesBrushes[index % SeriesBrushes.Length], true);
                    }
                }
                break;
        }
    }

    private static void DrawHoverMarker(
        DrawingContext context,
        double x,
        double value,
        Rect plot,
        double minValue,
        double maxValue,
        Brush brush,
        bool visible,
        bool filled = true)
    {
        if (!visible)
        {
            return;
        }

        var y = MapY(value, plot, minValue, maxValue);
        context.DrawEllipse(filled ? brush : null, filled ? null : new Pen(brush, 1.5), new Point(x, y), 4, 4);
    }

    private string HoverText(LandingSeriesPoint point)
    {
        var time = $"{point.TimeSeconds:+0.00;-0.00;0.00}s";
        switch (Mode)
        {
            case LandingChartMode.VerticalSpeed:
                return _isolatedSeriesIndex switch
                {
                    0 => $"{time}   inertial {-point.InertialFpm:+0;-0;0} fpm",
                    1 => $"{time}   VSI {-point.IndicatedFpm:+0;-0;0} fpm",
                    2 when HasSurfaceLatchData => $"{time}   MSFS latch {-_record!.SurfaceFpm:+0;-0;0} fpm",
                    _ when HasSurfaceLatchData => $"{time}   inertial {-point.InertialFpm:+0;-0;0}   VSI {-point.IndicatedFpm:+0;-0;0}   latch {-_record!.SurfaceFpm:+0;-0;0} fpm",
                    _ => $"{time}   inertial {-point.InertialFpm:+0;-0;0}   VSI {-point.IndicatedFpm:+0;-0;0}   latch n/a",
                };
            case LandingChartMode.LoadFactors:
                if (!HasHorizontalLoadData)
                {
                    return $"{time}   V {point.GForce:F3} G   horizontal G unavailable";
                }
                return _isolatedSeriesIndex switch
                {
                    0 => $"{time}   vertical {point.GForce:F3} G",
                    1 => $"{time}   longitudinal {point.LongitudinalLoadG:+0.000;-0.000;0.000} G",
                    2 => $"{time}   lateral {point.LateralLoadG:+0.000;-0.000;0.000} G",
                    _ => $"{time}   V {point.GForce:F3} G   LONG {point.LongitudinalLoadG:+0.000;-0.000;0.000}   LAT {point.LateralLoadG:+0.000;-0.000;0.000}",
                };
            case LandingChartMode.FlightControls:
                var controls = new List<string> { time };
                if (IsSeriesVisible(0)) controls.Add($"PITCH input {point.PilotPitchPercent:+0;-0;0}% · surface {point.ElevatorPercent:+0;-0;0}%");
                if (IsSeriesVisible(1)) controls.Add($"ROLL input {point.PilotRollPercent:+0;-0;0}% · surface {point.AileronPercent:+0;-0;0}%");
                if (IsSeriesVisible(2)) controls.Add($"YAW input {point.PilotYawPercent:+0;-0;0}% · surface {point.RudderPercent:+0;-0;0}%");
                return string.Join("\n", controls);
            case LandingChartMode.Attitude:
                var attitude = new List<string> { time };
                if (IsSeriesVisible(0)) attitude.Add($"PITCH {point.PitchDegrees:+0.0;-0.0;0.0}°");
                if (IsSeriesVisible(1)) attitude.Add($"BANK {point.BankDegrees:+0.0;-0.0;0.0}°");
                if (IsSeriesVisible(2)) attitude.Add($"AoA {point.AngleOfAttackDegrees:F1}°");
                return string.Join("   ", attitude);
            case LandingChartMode.Power:
                var power = new List<string> { time };
                if (_record != null)
                {
                    for (var index = 0; index < _record.Engines.Count; index++)
                    {
                        if (!IsSeriesVisible(index)) continue;
                        var engine = EnginePointAt(point.TimeSeconds, index);
                        if (engine != null)
                        {
                            power.Add($"ENG {_record.Engines[index].EngineNumber} · N1 {engine.N1Percent:F1}% · THR {engine.ThrottlePercent:F1}% · REV {engine.ReversePercent:F1}%");
                        }
                    }
                }
                return power.Count == 1 ? $"{time}   no engine data" : string.Join("\n", power);
            case LandingChartMode.Gear:
                var gear = new List<string> { time };
                if (_record != null)
                {
                    for (var index = 0; index < _record.ContactPoints.Count; index++)
                    {
                        if (!IsSeriesVisible(index)) continue;
                        var contact = ContactPointAt(point.TimeSeconds, index);
                        if (contact != null)
                        {
                            gear.Add($"CP {_record.ContactPoints[index].ContactPointIndex} · {contact.CompressionPercent:F1}% · {(contact.OnGround ? "CONTACT" : "AIR")}");
                        }
                    }
                }
                return gear.Count == 1 ? $"{time}   no gear data" : string.Join("\n", gear);
            default:
                return time;
        }
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
