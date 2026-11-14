using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

// A complete replacement for one native compositor layer. Pixels are
// premultiplied BGRA; placement is in DIPs and dimensions are device pixels.
internal sealed class ChartPanelLayer
{
    internal int Id { get; set; }
    internal int Width { get; set; }
    internal int Height { get; set; }
    internal int Stride { get; set; }
    internal double XDip { get; set; }
    internal double YDip { get; set; }
    internal byte[] Bgra { get; set; } = Array.Empty<byte>();
    internal bool Remove { get; set; }
}

// Owns chart layout and interaction state, but neither a window nor a device.
// All WPF drawing objects remain offscreen. Static vector geometry belongs to
// LandingChart; this class rasterizes only changed presentation layers.
internal sealed class ChartPanelScene : IDisposable
{
    private readonly LandingChart[] _charts =
    {
        new() { Mode = LandingChartMode.VerticalSpeed, ShowLegend = false, ShowXAxisLabels = false },
        new() { Mode = LandingChartMode.LoadFactors, CompactLane = true, ShowLegend = false, ShowXAxisLabels = false },
        new() { Mode = LandingChartMode.HeightAboveGround, CompactLane = true, ShowLegend = false, ShowXAxisLabels = false }
    };
    private readonly LandingTimeline _timeline = new();
    private readonly LandingChartFrame?[] _frames = new LandingChartFrame?[3];
    private readonly Point[] _origins = new Point[3];
    private readonly Size[] _sizes = new Size[3];
    private readonly Brush _background, _text, _muted, _label;
    internal uint BackgroundArgb
    {
        get
        {
            var color = ((SolidColorBrush)_background).Color;
            return 0xff000000u | (uint)color.R << 16 | (uint)color.G << 8 | color.B;
        }
    }
    private readonly Pen _border, _laneBorder;
    private LandingRecord? _record;
    private double _width, _height, _dpiX = 96, _dpiY = 96;
    private double _mainHeight, _gHeight, _altitudeHeight, _timelineHeight = 26, _timelineTop;
    private double? _zoomStart, _zoomEnd, _hoverTime;
    private int? _isolatedSeries;
    private int _renderedIndex = int.MinValue, _selectionChart = -1;
    private Point _selectionAnchor, _selectionCurrent;
    private string? _footerText;
    private bool _replay, _selectionDirty, _disposed;

    internal ChartPanelScene()
    {
        var resources = (ResourceDictionary)Application.LoadComponent(new Uri(
            "/" + typeof(ChartPanelScene).Assembly.GetName().Name + ";component/Controls/ApplicationResources.xaml", UriKind.Relative));
        _background = (Brush)resources["AppBackgroundBrush"];
        _text = (Brush)resources["TextBrush"];
        _muted = (Brush)resources["MutedTextBrush"];
        _label = (Brush)resources["LabelTextBrush"];
        _border = new Pen((Brush)resources["BorderBrush"], 1); _border.Freeze();
        // Existing mini-lane separators from MainWindow.xaml.
        _laneBorder = new Pen(new SolidColorBrush(Color.FromRgb(0x1A, 0x19, 0x17)), 1); _laneBorder.Freeze();
        _charts[1].SetIsolatedSeries(0);
    }

    internal event EventHandler? Invalidated;
    internal event EventHandler<LandingChartHoverEventArgs>? HoverTimeChanged;
    internal event EventHandler<LandingChartZoomEventArgs>? ZoomRangeSelected;
    internal bool StaticDirty { get; private set; } = true;
    internal bool HasSelection => _selectionChart >= 0;
    internal double? HoverTimeSeconds => _hoverTime;
    internal LandingChartMode Mode => _charts[0].Mode;
    internal LandingRecord? Record => _record;

    internal void SetRecord(LandingRecord? record)
    {
        Verify();
        var same = record != null && record.Id == _record?.Id;
        _record = record;
        foreach (var chart in _charts) chart.Record = record;
        _charts[1].SetIsolatedSeries(0);
        _timeline.Record = record;
        if (!same)
        {
            _hoverTime = null; _isolatedSeries = null;
            _charts[0].SetIsolatedSeries(null);
        }
        // A refreshed detail can retain its identity while changing coverage.
        // Rebind normalized ranges even when the requested range is unchanged.
        LandingChart.NormalizeZoomRange(record?.Series, ref _zoomStart, ref _zoomEnd);
        foreach (var chart in _charts) chart.SetZoomRange(_zoomStart, _zoomEnd);
        _timeline.SetZoomRange(_zoomStart, _zoomEnd);
        CancelSelection();
        MarkStaticDirty();
    }

    internal void SetMode(LandingChartMode mode)
    {
        Verify();
        if (mode == Mode) return;
        _charts[0].Mode = mode; _isolatedSeries = null;
        _charts[0].SetIsolatedSeries(null);
        CancelSelection(); MarkStaticDirty();
    }

    internal void SetIsolatedSeries(int? index)
    {
        Verify();
        if (_isolatedSeries == index) return;
        _isolatedSeries = index; _charts[0].SetIsolatedSeries(index); MarkStaticDirty();
    }

    internal void SetZoomRange(double? start, double? end)
    {
        Verify();
        LandingChart.NormalizeZoomRange(_record?.Series, ref start, ref end);
        if (_zoomStart == start && _zoomEnd == end) return;
        _zoomStart = start; _zoomEnd = end;
        foreach (var chart in _charts) chart.SetZoomRange(start, end);
        _timeline.SetZoomRange(start, end);
        CancelSelection(); MarkStaticDirty();
    }

    internal void SetReplayVisible(bool visible)
    {
        Verify();
        if (_replay == visible) return;
        _replay = visible; CancelSelection(); MarkStaticDirty();
    }

    internal void RefreshLocalization()
    {
        Verify();
        foreach (var chart in _charts) chart.RefreshSurfaceLocalization();
        MarkStaticDirty();
    }

    // The host supplies actual WPF RowDefinition heights, retaining the existing
    // star sizing, minimums, maximums and replay collapse without duplicating it.
    internal void SetLaneHeights(double main, double g, double altitude, double timeline)
    {
        Verify();
        if (!NonNegative(main) || !NonNegative(g) || !NonNegative(altitude) || !NonNegative(timeline))
            throw new ArgumentOutOfRangeException(nameof(main));
        if (_mainHeight == main && _gHeight == g && _altitudeHeight == altitude && _timelineHeight == timeline) return;
        _mainHeight = main; _gHeight = g; _altitudeHeight = altitude; _timelineHeight = timeline;
        CancelSelection(); MarkStaticDirty();
    }

    internal void Resize(double widthDip, double heightDip, double dpiX, double dpiY)
    {
        Verify();
        if (!NonNegative(widthDip) || !NonNegative(heightDip) || !Finite(dpiX) || !Finite(dpiY) || dpiX <= 0 || dpiY <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthDip));
        if (_width == widthDip && _height == heightDip && _dpiX == dpiX && _dpiY == dpiY) return;
        _width = widthDip; _height = heightDip; _dpiX = dpiX; _dpiY = dpiY;
        CancelSelection(); MarkStaticDirty();
    }

    internal IReadOnlyList<ChartPanelLayer> BuildStaticLayers()
    {
        Verify();
        if (_width <= 0 || _height <= 0) return Array.Empty<ChartPanelLayer>();
        var drawing = BuildStaticDrawing();
        StaticDirty = false; _renderedIndex = int.MinValue; _footerText = null;
        var layers = new List<ChartPanelLayer> { Rasterize(0, drawing, new Rect(0, 0, _width, _height)), Removed(5) };
        layers.AddRange(SetHoverTime(_hoverTime));
        if (HasSelection) layers.Add(SelectionLayer());
        _selectionDirty = false;
        return layers;
    }

    // A modal WPF overlay needs a WPF backdrop while the child HWND is hidden.
    // Compose it from the same drawings without consuming pending native layers.
    internal BitmapSource? CreateSnapshot()
    {
        Verify();
        if (_width <= 0 || _height <= 0) return null;
        var background = BuildStaticDrawing();
        var index = _hoverTime.HasValue ? _charts[0].NearestSurfaceIndex(_hoverTime.Value) : -1;
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, _width, _height)));
            dc.DrawDrawing(background);
            if (index >= 0)
                for (var i = 0; i < _charts.Length; i++)
                {
                    var frame = _frames[i];
                    if (frame == null || !frame.HasData || _sizes[i].Width <= 0 || _sizes[i].Height <= 0) continue;
                    dc.PushTransform(new TranslateTransform(_origins[i].X, _origins[i].Y));
                    dc.DrawDrawing(_charts[i].DrawSurfaceHover(frame, index));
                    dc.Pop();
                }
            dc.DrawDrawing(BuildFooterDrawing(FooterText(index < 0 ? _charts[0].NearestSurfaceIndex(0) : index)));
            if (HasSelection)
            {
                var selected = _selectionChart; var origin = _origins[selected];
                dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
                dc.DrawDrawing(_charts[selected].DrawSurfaceSelection(_frames[selected]!,
                    _selectionAnchor.X - origin.X, _selectionCurrent.X - origin.X));
                dc.Pop();
            }
            dc.Pop();
        }
        drawing.Freeze();
        return RenderBitmap(drawing, new Rect(0, 0, _width, _height), out _);
    }

    private DrawingGroup BuildStaticDrawing()
    {
        var main = _replay ? 0 : _mainHeight;
        var altitude = _replay ? 0 : _altitudeHeight;
        _origins[0] = new Point(0, 12);
        _origins[1] = new Point(96, main + 9);
        _origins[2] = new Point(96, main + _gHeight + 9);
        _sizes[0] = new Size(_width, Math.Max(0, main - 18));
        _sizes[1] = new Size(Math.Max(0, _width - 96), Math.Max(0, _gHeight - 17));
        _sizes[2] = new Size(Math.Max(0, _width - 96), Math.Max(0, altitude - 17));
        _timelineTop = main + _gHeight + altitude;
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(_background, null, new Rect(0, 0, _width, _height));
            for (var i = 0; i < _charts.Length; i++)
            {
                var frame = _charts[i].PrepareSurface(_sizes[i], _dpiY / 96);
                _frames[i] = frame;
                if (_sizes[i].Height <= 0 || _sizes[i].Width <= 0) continue;
                dc.PushTransform(new TranslateTransform(_origins[i].X, _origins[i].Y));
                dc.PushClip(new RectangleGeometry(new Rect(_sizes[i])));
                dc.DrawDrawing(frame.Drawing); dc.Pop(); dc.Pop();
            }
            if (main > 0)
            {
                var unit = Format(ModeUnit(), _label, 10, true);
                dc.DrawText(unit, new Point(24, main - 6 - 8 - unit.Height));
            }
            DrawLaneLabels(dc, main, false);
            if (altitude > 0) DrawLaneLabels(dc, main + _gHeight, true);
            if (_timelineHeight > 0)
            {
                dc.DrawLine(_border, new Point(0, _timelineTop + .5), new Point(_width, _timelineTop + .5));
                dc.PushTransform(new TranslateTransform(96, _timelineTop + 1));
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(0, _width - 96), Math.Max(0, _timelineHeight - 1))));
                dc.DrawDrawing(_timeline.PrepareSurface(new Size(Math.Max(0, _width - 96), Math.Max(0, _timelineHeight - 1)), _dpiY / 96));
                dc.Pop(); dc.Pop();
            }
        }
        drawing.Freeze();
        return drawing;
    }

    internal IReadOnlyList<ChartPanelLayer> SetHoverTime(double? time)
    {
        Verify();
        if (time.HasValue && !Finite(time.Value)) time = null;
        _hoverTime = time;
        if (StaticDirty) return Array.Empty<ChartPanelLayer>();
        var index = time.HasValue ? _charts[0].NearestSurfaceIndex(time.Value) : -1;
        var layers = new List<ChartPanelLayer>();
        if (index != _renderedIndex)
        {
            _renderedIndex = index;
            for (var i = 0; i < _charts.Length; i++)
            {
                var frame = _frames[i];
                if (index < 0 || frame == null || !frame.HasData || _sizes[i].Width <= 0 || _sizes[i].Height <= 0)
                { layers.Add(Removed(i + 1)); continue; }
                layers.Add(PlacedDrawing(i + 1, _charts[i].DrawSurfaceHover(frame, index), _origins[i]));
            }
            var footer = Footer(index < 0 ? _charts[0].NearestSurfaceIndex(0) : index);
            if (footer != null) layers.Add(footer);
        }
        if (_selectionDirty) { layers.Add(SelectionLayer()); _selectionDirty = false; }
        return layers;
    }

    internal IReadOnlyList<ChartPanelLayer> UpdateHover(double xDip, double yDip, bool leave = false)
    {
        Verify();
        if (StaticDirty) return Array.Empty<ChartPanelLayer>();
        if (HasSelection && !leave) UpdateSelection(xDip, yDip);
        var hit = leave ? -1 : HitPlot(new Point(xDip, yDip));
        var time = hit < 0 ? (double?)null : _frames[hit]!.TimeAt(xDip - _origins[hit].X);
        var previous = _hoverTime;
        // Commit both selection and hover before notifying consumers. A replay
        // callback can synchronously set the same time without rebuilding layers.
        var layers = SetHoverTime(time);
        if (previous != time) HoverTimeChanged?.Invoke(this, new LandingChartHoverEventArgs(time));
        return layers;
    }

    internal bool IsOverPlot(double xDip, double yDip) => !StaticDirty && HitPlot(new Point(xDip, yDip)) >= 0;
    internal bool ContainsPlot(double xDip, double yDip) => IsOverPlot(xDip, yDip);

    internal string? GetHelpText(double xDip, double yDip)
    {
        if (_replay || _altitudeHeight <= 0) return null;
        var box = new Rect(24, _mainHeight + _gHeight + 10, 64, Math.Max(0, _altitudeHeight - 10));
        return box.Contains(new Point(xDip, yDip)) ? LocalizationManager.Text("Chart.HeightAboveGroundHelp") : null;
    }

    internal bool BeginSelection(double xDip, double yDip)
    {
        Verify();
        if (StaticDirty) return false;
        var point = new Point(xDip, yDip); var hit = HitPlot(point);
        if (hit < 0) return false;
        _selectionChart = hit; _selectionAnchor = _selectionCurrent = point; _selectionDirty = true;
        return true;
    }

    internal bool UpdateSelection(double xDip, double yDip)
    {
        Verify();
        if (!HasSelection) return false;
        var point = new Point(xDip, yDip);
        if (_selectionCurrent == point) return false;
        _selectionCurrent = point; _selectionDirty = true; return true;
    }

    internal bool EndSelection(double xDip, double yDip)
    {
        Verify();
        if (!HasSelection) return false;
        UpdateSelection(xDip, yDip);
        var index = _selectionChart; var start = _selectionAnchor.X; var end = _selectionCurrent.X;
        CancelSelection();
        // Preserve the existing LandingChart drag gesture threshold.
        if (Math.Abs(end - start) < LandingChart.SelectionDragThreshold) return false;
        var frame = _frames[index]!;
        var minimum = frame.TimeAt(Math.Min(start, end) - _origins[index].X);
        var maximum = frame.TimeAt(Math.Max(start, end) - _origins[index].X);
        SetZoomRange(minimum, maximum);
        ZoomRangeSelected?.Invoke(this, new LandingChartZoomEventArgs(_zoomStart, _zoomEnd));
        return true;
    }

    internal void CancelSelection()
    {
        Verify();
        if (!HasSelection) return;
        _selectionChart = -1; _selectionDirty = true;
    }

    internal void ResetZoom()
    {
        Verify(); CancelSelection(); SetZoomRange(null, null);
        ZoomRangeSelected?.Invoke(this, new LandingChartZoomEventArgs(null, null));
    }

    private ChartPanelLayer SelectionLayer()
    {
        if (!HasSelection) return Removed(5);
        var index = _selectionChart; var origin = _origins[index];
        return PlacedDrawing(5, _charts[index].DrawSurfaceSelection(_frames[index]!,
            _selectionAnchor.X - origin.X, _selectionCurrent.X - origin.X), origin);
    }

    private void DrawLaneLabels(DrawingContext dc, double top, bool altitude)
    {
        var height = altitude ? _altitudeHeight : _gHeight;
        if (height <= 0) return;
        dc.DrawLine(_laneBorder, new Point(0, top + .5), new Point(_width, top + .5));
        var title = Format(LocalizationManager.Text(altitude ? "Chart.HeightAboveGround" : "Chart.VerticalG"), _muted, 11, false);
        if (altitude) title.MaxTextWidth = 64;
        dc.PushClip(new RectangleGeometry(new Rect(24, top + 1, 72, Math.Max(0, height - 1))));
        dc.DrawText(title, new Point(24, top + 10)); dc.Pop();
        var unit = Format(altitude ? LocalizationManager.Text("Chart.UnitFeet") : "0.8 – 1.6", _label, 9, true);
        dc.DrawText(unit, new Point(Math.Max(96, _width - unit.Width), top + 9));
    }

    private ChartPanelLayer? Footer(int index)
    {
        var text = FooterText(index);
        if (_footerText == text) return null;
        _footerText = text;
        if (_timelineHeight <= 0) return Removed(4);
        return PlacedDrawing(4, BuildFooterDrawing(text), new Point());
    }

    private string FooterText(int index) => index < 0 || _record == null ? "—" :
        _record.Series[index].TimeSeconds.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + LocalizationManager.Text("Unit.SecondSuffix");

    private DrawingGroup BuildFooterDrawing(string text)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            if (_timelineHeight > 0)
            {
                var formatted = Format(text, _text, 11, true);
                dc.PushClip(new RectangleGeometry(new Rect(24, _timelineTop + 1, 72, Math.Max(0, _timelineHeight - 1))));
                dc.DrawText(formatted, new Point(24, _timelineTop + 1 + (_timelineHeight - 1 - formatted.Height) / 2));
                dc.Pop();
            }
        }
        drawing.Freeze();
        return drawing;
    }

    private string ModeUnit() => Mode switch
    {
        LandingChartMode.VerticalSpeed => "fpm",
        LandingChartMode.LoadFactors => "G",
        LandingChartMode.FlightControls => LocalizationManager.Text("Chart.UnitTravel"),
        LandingChartMode.Attitude => LocalizationManager.Text("Chart.UnitDegrees"),
        LandingChartMode.Power => "%",
        LandingChartMode.Gear => LocalizationManager.Text("Chart.UnitStroke"),
        _ => string.Empty
    };

    private FormattedText Format(string text, Brush brush, double size, bool mono) => new(text,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(mono ? "Cascadia Mono, Consolas" : "Segoe UI"), size, brush, _dpiY / 96);

    private ChartPanelLayer PlacedDrawing(int id, DrawingGroup source, Point origin)
    {
        if (source.Bounds.IsEmpty) return Removed(id);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
            dc.DrawDrawing(source); dc.Pop();
        }
        drawing.Freeze();
        var bounds = drawing.Bounds; bounds.Intersect(new Rect(0, 0, _width, _height));
        return bounds.IsEmpty ? Removed(id) : Rasterize(id, drawing, bounds);
    }

    private ChartPanelLayer Rasterize(int id, DrawingGroup drawing, Rect bounds)
    {
        var bitmap = RenderBitmap(drawing, bounds, out var origin);
        var stride = checked(bitmap.PixelWidth * 4); var bytes = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(bytes, stride, 0);
        return new ChartPanelLayer { Id = id, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Stride = stride,
            XDip = origin.X, YDip = origin.Y, Bgra = bytes };
    }

    private RenderTargetBitmap RenderBitmap(DrawingGroup drawing, Rect bounds, out Point origin)
    {
        var sx = _dpiX / 96; var sy = _dpiY / 96;
        var left = Math.Floor(bounds.Left * sx); var top = Math.Floor(bounds.Top * sy);
        var right = Math.Ceiling(bounds.Right * sx); var bottom = Math.Ceiling(bounds.Bottom * sy);
        var width = checked((int)Math.Max(1, right - left)); var height = checked((int)Math.Max(1, bottom - top));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-left / sx, -top / sy));
            dc.DrawDrawing(drawing); dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, _dpiX, _dpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        origin = new Point(left / sx, top / sy);
        return bitmap;
    }

    private int HitPlot(Point point)
    {
        for (var i = 0; i < _frames.Length; i++)
        {
            var frame = _frames[i];
            if (frame == null || !frame.HasData || _sizes[i].Width <= 0 || _sizes[i].Height <= 0) continue;
            if (frame.Plot.Contains(new Point(point.X - _origins[i].X, point.Y - _origins[i].Y))) return i;
        }
        return -1;
    }

    private static ChartPanelLayer Removed(int id) => new() { Id = id, Remove = true };
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool NonNegative(double value) => Finite(value) && value >= 0;
    private void MarkStaticDirty() { StaticDirty = true; Invalidated?.Invoke(this, EventArgs.Empty); }
    private void Verify()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChartPanelScene));
        _charts[0].VerifyAccess();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Verify();
        foreach (var chart in _charts) chart.Record = null;
        _timeline.Record = null; _record = null; _disposed = true;
    }
}
