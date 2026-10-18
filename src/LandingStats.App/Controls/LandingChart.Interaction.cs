using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LandingStats.App.Controls;

public sealed partial class LandingChart
{
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

    protected override void OnLostMouseCapture(MouseEventArgs eventArgs)
    {
        base.OnLostMouseCapture(eventArgs);
        if (!_isSelecting) return;
        _isSelecting = false;
        SetHoverTime(null);
        HoverTimeChanged?.Invoke(this, new LandingChartHoverEventArgs(null));
        InvalidateVisual();
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
            InvalidateGearRange();
            InvalidateChart();
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

        InvalidateChart();
        eventArgs.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseRightButtonDown(eventArgs);
        SetZoomRange(null, null);
        ZoomRangeSelected?.Invoke(this, new LandingChartZoomEventArgs(null, null));
        eventArgs.Handled = true;
    }

}
