using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

internal static class ReplayOverlayStyle
{
    internal static readonly Brush Panel = Brush("#B310100F");
    internal static readonly Brush Line = Brush("#644A433B");
    internal static readonly Brush Muted = Brush("#AAA198");
    internal static readonly Brush Ink = Brush("#EAE5DE");
    internal static readonly Brush Orange = Brush("#FF7A45");
    internal static readonly Brush Blue = Brush("#5FA8F5");
    internal static readonly Brush Green = Brush("#8FD6A8");
    internal static readonly Brush Amber = Brush("#D9C46A");
    internal static readonly Brush Purple = Brush("#B58AF5");
    internal static readonly Brush Cyan = Brush("#6ECFD6");
    internal static Brush Brush(string color) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); b.Freeze(); return b; }
    private static readonly System.Collections.Generic.Dictionary<(Brush Brush, double Width), Pen> Pens = new System.Collections.Generic.Dictionary<(Brush, double), Pen>();
    internal static Pen Pen(Brush brush, double width)
    {
        var key = (brush, width);
        if (!Pens.TryGetValue(key, out var pen)) { pen = new Pen(brush, width); pen.Freeze(); Pens.Add(key, pen); }
        return pen;
    }
    internal static readonly Brush StickBackground = Brush("#1810100F");
    internal static readonly Brush StickTrail = Brush("#66FF7A45");
    internal static readonly Brush StickHalo = Brush("#28FF7A45");
    internal static readonly Brush PedalBackground = Brush("#22211E1A");
    internal static readonly Pen DashedLine = CreateDashed();
    private static Pen CreateDashed() { var pen = new Pen(Line, 1) { DashStyle = DashStyles.Dash }; pen.Freeze(); return pen; }
    internal static void Box(DrawingContext dc, Rect rect) => dc.DrawRoundedRectangle(Panel, ReplayOverlayStyle.Pen(Line, 1), rect, 5, 5);
    internal static void Text(Visual owner, DrawingContext dc, string text, double x, double y, Brush brush, double size = 10, bool right = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono, Consolas"), size, brush, VisualTreeHelper.GetDpi(owner).PixelsPerDip);
        dc.DrawText(formatted, new Point(right ? x - formatted.Width : x, y));
    }
    internal static string L(string key) => LocalizationManager.Text("Replay." + key);
    internal static ToolTip Hint(string text) => new ToolTip {
        Background = Brush("#1F1D1B"), Foreground = Ink, BorderBrush = Line, Padding = new Thickness(10),
        Content = new TextBlock { Text = text, MaxWidth = 340, TextWrapping = TextWrapping.Wrap, FontSize = 11 },
    };
    internal static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}

internal abstract class ReplayOverlayElement : FrameworkElement
{
    private DrawingGroup? _background;
    private string? _backgroundKey;
    private double _backgroundDpi;
    protected void InvalidateBackground() { _background = null; InvalidateVisual(); }
    protected void DrawBackground(DrawingContext context, string key, Action<DrawingContext> draw)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_background == null || _backgroundKey != key || _backgroundDpi != dpi)
        {
            _background = new DrawingGroup();
            using (var drawing = _background.Open()) draw(drawing);
            _background.Freeze(); _backgroundKey = key; _backgroundDpi = dpi;
        }
        context.DrawDrawing(_background);
    }
}

/// <summary>Cached, gap-aware mini charts; only the cursor and current values change during playback.</summary>
internal sealed class ReplayMetricsOverlay : ReplayOverlayElement
{
    private readonly Func<LandingSeriesPoint, double>[] _values = { p => -p.PitchDegrees, p => p.BankDegrees,
        p => p.HeadingTrueDegrees ?? double.NaN, p => p.GroundSpeedKnots, p => -p.InertialFpm, p => p.GForce };
    private readonly string[] _labels = { "Pitch", "Bank", "Yaw", "GroundSpeed", "VerticalSpeed", "VerticalG" };
    private readonly Brush[] _colors = { ReplayOverlayStyle.Orange, ReplayOverlayStyle.Blue, ReplayOverlayStyle.Purple,
        ReplayOverlayStyle.Amber, ReplayOverlayStyle.Cyan, ReplayOverlayStyle.Green };
    private readonly Geometry?[] _paths = new Geometry?[6];
    private readonly double[] _minimum = new double[6];
    private readonly double[] _maximum = new double[6];
    private double? _runwayHeading;
    private LandingReplayTrack? _track;
    private LandingReplayPose? _pose;
    private double _time;

    public ReplayMetricsOverlay() { Width = 340; Height = 220; IsHitTestVisible = false; }
    internal void SetTrack(LandingReplayTrack? track, double? runwayHeading = null)
    {
        _track = track; _runwayHeading = runwayHeading;
        for (var i = 0; i < _values.Length; i++)
        {
            _paths[i] = null;
            if (track == null) continue;
            var values = track.Frames.Select(f => Value(i, f.Sample)).Where(LandingReplayTrack.Finite).ToArray();
            if (values.Length == 0) continue;
            var baseline = i == 5 ? 1d : 0d;
            var min = i == 3 ? values.Min() : Math.Min(baseline, values.Min());
            var max = i == 3 ? values.Max() : Math.Max(baseline, values.Max());
            var margin = Math.Max(i == 5 ? .05 : 1, (max - min) * .12);
            _minimum[i] = min - margin; _maximum[i] = max + margin;
            var path = new StreamGeometry();
            using (var context = path.Open())
            {
                var connected = false; var lastTime = double.NegativeInfinity; var lastValue = double.NaN;
                foreach (var frame in track.Frames)
                {
                    var value = Value(i, frame.Sample);
                    if (!LandingReplayTrack.Finite(value)) { connected = false; continue; }
                    var point = ChartPoint(i, frame.TimeSeconds, value);
                    if (!connected || frame.TimeSeconds - lastTime > LandingReplayTrack.MaximumInterpolationGapSeconds ||
                        (i == 2 && Math.Abs(value - lastValue) > 180))
                        context.BeginFigure(point, false, false);
                    else context.LineTo(point, true, false);
                    connected = true; lastTime = frame.TimeSeconds; lastValue = value;
                }
            }
            path.Freeze(); _paths[i] = path;
        }
        InvalidateBackground();
    }

    internal void Update(double time, LandingReplayPose? pose) { _time = time; _pose = pose; InvalidateVisual(); }
    private double Yaw(double heading) => _runwayHeading.HasValue && LandingReplayTrack.Finite(heading)
        ? (heading - _runwayHeading.Value + 540) % 360 - 180 : double.NaN;
    private double Value(int i, LandingSeriesPoint point) => i == 2 ? Yaw(_values[i](point)) : _values[i](point);
    internal static Rect Card(int i) => new Rect(i / 3 * 176, i % 3 * 76, 164, 68);
    private static Rect Plot(int i) { var r = Card(i); return new Rect(r.X + 10, r.Y + 29, r.Width - 20, 29); }
    private Point ChartPoint(int i, double time, double value)
    {
        var plot = Plot(i);
        return new Point(plot.Left + (time - _track!.StartSeconds) / (_track.EndSeconds - _track.StartSeconds) * plot.Width,
            plot.Bottom - (value - _minimum[i]) / (_maximum[i] - _minimum[i]) * plot.Height);
    }
    protected override void OnRender(DrawingContext dc)
    {
        DrawBackground(dc, ReplayOverlayStyle.L("Pitch"), background =>
        {
            for (var i = 0; i < _values.Length; i++)
            {
                var card = Card(i); var plot = Plot(i);
                ReplayOverlayStyle.Box(background, card);
                ReplayOverlayStyle.Text(this, background, ReplayOverlayStyle.L(_labels[i]), card.X + 10, card.Y + 8, ReplayOverlayStyle.Muted, 9);
                if (_track == null || _paths[i] == null) continue;
                var baseline = ChartPoint(i, _track.StartSeconds, i == 5 ? 1 : 0).Y;
                if (baseline >= plot.Top && baseline <= plot.Bottom)
                {
                    background.DrawLine(ReplayOverlayStyle.Pen(ReplayOverlayStyle.Line, .7), new Point(plot.Left, baseline), new Point(plot.Right, baseline));
                    ReplayOverlayStyle.Text(this, background, i == 5 ? "1" : "0", plot.Left, baseline - 10, ReplayOverlayStyle.Muted, 8);
                }
                background.PushOpacity(.25);
                background.DrawGeometry(null, ReplayOverlayStyle.Pen(_colors[i], 1.3), _paths[i]); background.Pop();
            }
        });
        for (var i = 0; i < _values.Length; i++)
        {
            var card = Card(i); var plot = Plot(i); var color = _colors[i];
            var value = i == 2 ? Yaw(_pose?.HeadingDegrees ?? double.NaN) : _pose?.Value(_values[i]) ?? double.NaN;
            var text = !LandingReplayTrack.Finite(value) ? "—" : value.ToString(i < 3 ? "+0.0;-0.0;0.0" : i == 4 ? "+0;-0;0" : i == 3 ? "0" : "0.00", CultureInfo.CurrentCulture) +
                (i < 3 ? "°" : i == 3 ? " " + ReplayOverlayStyle.L("Knots") : i == 4 ? " fpm" : " G");
            ReplayOverlayStyle.Text(this, dc, text, card.Right - 10, card.Y + 6, color, 12, true);
            if (_track == null || _paths[i] == null) continue;
            var cursor = ChartPoint(i, _time, 0).X;
            var clip = new RectangleGeometry(new Rect(plot.Left, plot.Top, ReplayOverlayStyle.Clamp(cursor - plot.Left, 0, plot.Width), plot.Height));
            clip.Freeze(); dc.PushClip(clip);
            dc.DrawGeometry(null, ReplayOverlayStyle.Pen(color, 1.3), _paths[i]); dc.Pop();
            dc.DrawLine(ReplayOverlayStyle.Pen(ReplayOverlayStyle.Line, 1), new Point(cursor, plot.Top), new Point(cursor, plot.Bottom));
            if (LandingReplayTrack.Finite(value)) dc.DrawEllipse(color, null, ChartPoint(i, _time, value), 2.6, 2.6);
        }
    }

}

internal sealed class ReplayControlOverlay : ReplayOverlayElement
{
    private ReplayControlInputs? _inputs;
    private ReplayControlSample _sample;
    internal static readonly Rect StickBounds = new Rect(14, 32, 148, 148);
    internal static readonly Rect PedalBounds = new Rect(14, 220, 148, 12);

    public ReplayControlOverlay() { Width = 176; Height = 264; }
    internal void SetInputs(ReplayControlInputs? inputs) { _inputs = inputs; RefreshLocalization(); }
    internal void RefreshLocalization()
    {
        string Source(string label, ReplayControlAxis? axis) => ReplayOverlayStyle.L(label) + ": " + (axis?.SourceLabel ?? ReplayOverlayStyle.L("NoAxis"));
        ToolTip = ReplayOverlayStyle.Hint(ReplayOverlayStyle.L("ControlHint") + "\n\n" + Source("Pitch", _inputs?.Pitch) + "\n" +
            Source("Roll", _inputs?.Roll) + "\n" + Source("Pedals", _inputs?.Yaw));
        InvalidateVisual();
    }
    internal void Update(double time, bool hasPose)
    {
        var revision = _inputs?.BindingRevision;
        // Source attribution follows time even when recorded pose is missing.
        var sample = _inputs?.Sample(time) ?? default;
        _sample = hasPose ? sample : default;
        if (_inputs?.BindingRevision != revision) RefreshLocalization();
        else InvalidateVisual();
    }
    internal static Point StickPoint(double pitch, double roll) => new Point(
        StickBounds.X + StickBounds.Width * (.5 + ReplayOverlayStyle.Clamp(roll, -100, 100) / 200),
        StickBounds.Y + StickBounds.Height * (.5 + ReplayOverlayStyle.Clamp(pitch, -100, 100) / 200));
    internal static Rect PedalFill(double yaw)
    {
        var center = PedalBounds.X + PedalBounds.Width / 2;
        var delta = ReplayOverlayStyle.Clamp(yaw, -100, 100) / 200 * PedalBounds.Width;
        return new Rect(Math.Min(center, center + delta), PedalBounds.Y, Math.Abs(delta), PedalBounds.Height);
    }
    protected override void OnRender(DrawingContext dc)
    {
        var bounds = StickBounds; var center = StickPoint(0, 0);
        DrawBackground(dc, ReplayOverlayStyle.L("Stick"), background =>
        {
        ReplayOverlayStyle.Box(background, new Rect(0, 0, Width, Height));
        ReplayOverlayStyle.Text(this, background, ReplayOverlayStyle.L("Stick"), 14, 10, ReplayOverlayStyle.Muted, 10);
        background.DrawRectangle(ReplayOverlayStyle.StickBackground, ReplayOverlayStyle.Pen(ReplayOverlayStyle.Line, 1), bounds);
        var dashed = ReplayOverlayStyle.DashedLine;
        background.DrawLine(dashed, new Point(center.X, bounds.Top), new Point(center.X, bounds.Bottom));
        background.DrawLine(dashed, new Point(bounds.Left, center.Y), new Point(bounds.Right, center.Y));
        background.DrawEllipse(null, ReplayOverlayStyle.Pen(ReplayOverlayStyle.Muted, 1), center, 3, 3);
        ReplayOverlayStyle.Text(this, background, ReplayOverlayStyle.L("Push"), bounds.Left + 6, bounds.Top + 5, ReplayOverlayStyle.Muted, 8);
        ReplayOverlayStyle.Text(this, background, ReplayOverlayStyle.L("Pull"), bounds.Left + 6, bounds.Bottom - 16, ReplayOverlayStyle.Muted, 8);
        ReplayOverlayStyle.Text(this, background, ReplayOverlayStyle.L("Pedals"), 14, 204, ReplayOverlayStyle.Muted, 9);
        background.DrawRectangle(ReplayOverlayStyle.PedalBackground, ReplayOverlayStyle.Pen(ReplayOverlayStyle.Line, 1), PedalBounds);
        background.DrawLine(ReplayOverlayStyle.Pen(ReplayOverlayStyle.Muted, 1), new Point(center.X, 217), new Point(center.X, 235));
        ReplayOverlayStyle.Text(this, background, ReplayOverlayStyle.L("LeftShort"), 14, 242, ReplayOverlayStyle.Muted, 9);
        ReplayOverlayStyle.Text(this, background, ReplayOverlayStyle.L("RightShort"), 162, 242, ReplayOverlayStyle.Muted, 9, true);
        });
        if (_sample.PitchPercent.HasValue || _sample.RollPercent.HasValue)
        {
            // A missing dimension uses the centre guide for display only. Keep
            // the sample nullable and explicitly label the unavailable axis below.
            var position = StickPoint(_sample.PitchPercent ?? 0, _sample.RollPercent ?? 0);
            dc.DrawLine(ReplayOverlayStyle.Pen(ReplayOverlayStyle.StickTrail, 1), center, position);
            dc.DrawEllipse(ReplayOverlayStyle.StickHalo, null, position, 11, 11);
            dc.DrawEllipse(ReplayOverlayStyle.Orange, ReplayOverlayStyle.Pen(ReplayOverlayStyle.Ink, 1), position, 4, 4);
        }
        else ReplayOverlayStyle.Text(this, dc, "—", center.X - 4, center.Y + 14, ReplayOverlayStyle.Muted, 12);
        var status = !_sample.PitchPercent.HasValue && !_sample.RollPercent.HasValue ? ReplayOverlayStyle.L("NoAxes") :
            !_sample.RollPercent.HasValue ? ReplayOverlayStyle.L("PitchOnly") : !_sample.PitchPercent.HasValue ? ReplayOverlayStyle.L("RollOnly") : "";
        ReplayOverlayStyle.Text(this, dc, status, 14, 185, ReplayOverlayStyle.Muted, 9);
        if (_sample.YawPercent.HasValue) dc.DrawRectangle(ReplayOverlayStyle.Purple, null, PedalFill(_sample.YawPercent.Value));
        var yaw = _sample.YawPercent.HasValue ? _sample.YawPercent.Value.ToString("+0;-0;0", CultureInfo.CurrentCulture) + "%" : "—";
        ReplayOverlayStyle.Text(this, dc, yaw, 105, 240, _sample.YawPercent.HasValue ? ReplayOverlayStyle.Purple : ReplayOverlayStyle.Muted, 10, true);
    }
}

internal sealed class ReplayRunwayOverlay : ReplayOverlayElement
{
    private double? _meters;
    private double _halfWidth;
    private RunwayFacility? _runway;
    internal ReplayRunwayOverlay() { Width = 240; Height = 60; }
    internal void SetRunway(RunwayFacility? runway) { _runway = runway; RefreshLocalization(); }
    internal void Update(double? meters, double width) { _meters = meters; _halfWidth = Math.Max(1, width / 2); InvalidateVisual(); }
    internal void RefreshLocalization()
    {
        var hint = ReplayOverlayStyle.L("CenterlineHint");
        if (_runway != null && _runway.HasValidGeometry)
        {
            hint += "\n\n" + string.Format(CultureInfo.CurrentCulture, ReplayOverlayStyle.L("RunwaySize"), _runway.LengthMeters, _runway.WidthMeters);
            if (_runway.HasPaintedSurface)
            {
                hint += "\n" + string.Format(CultureInfo.CurrentCulture, ReplayOverlayStyle.L("MarkingsHint"), RunwayMarkingLayout.UsesFaaConvention(_runway) ? "FAA" : "ICAO/EASA");
            }
        }
        ToolTip = ReplayOverlayStyle.Hint(hint); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        DrawBackground(dc, ReplayOverlayStyle.L("Centerline") + _meters.HasValue, background =>
        {
            ReplayOverlayStyle.Box(background, new Rect(0, 0, Width, Height));
            ReplayOverlayStyle.Text(this, background, ReplayOverlayStyle.L("Centerline"), 12, 8, ReplayOverlayStyle.Muted, 9);
            if (!_meters.HasValue) return;
            background.DrawLine(ReplayOverlayStyle.Pen(ReplayOverlayStyle.Line, 2), new Point(26, 41), new Point(214, 41));
            foreach (var x in new[] { 26d, 120, 214 })
                background.DrawLine(ReplayOverlayStyle.Pen(ReplayOverlayStyle.Muted, 1), new Point(x, 36), new Point(x, 46));
            ReplayOverlayStyle.Text(this, background, "0", 117, 47, ReplayOverlayStyle.Muted, 8);
        });
        if (!_meters.HasValue)
        {
            ReplayOverlayStyle.Text(this, dc, "—", Width - 12, 6, ReplayOverlayStyle.Amber, 13, true);
            ReplayOverlayStyle.Text(this, dc, ReplayOverlayStyle.L("NoOffset"), 12, 34, ReplayOverlayStyle.Muted, 9);
            return;
        }
        var meters = _meters.Value;
        var label = Math.Abs(meters) < .05 ? ReplayOverlayStyle.L("OnCenterline") :
            Math.Abs(meters).ToString("0.0", CultureInfo.CurrentCulture) + " " + ReplayOverlayStyle.L("Meters") +
            (meters < 0 ? " ←" : " →");
        ReplayOverlayStyle.Text(this, dc, label, Width - 12, 6, ReplayOverlayStyle.Amber, 13, true);
        const double center = 120; const double half = 94;
        var position = center + ReplayOverlayStyle.Clamp(meters / _halfWidth, -1, 1) * half;
        dc.DrawLine(ReplayOverlayStyle.Pen(ReplayOverlayStyle.Amber, 2), new Point(center, 41), new Point(position, 41));
        dc.DrawEllipse(ReplayOverlayStyle.Amber, null, new Point(position, 41), 3.5, 3.5);
    }
}
