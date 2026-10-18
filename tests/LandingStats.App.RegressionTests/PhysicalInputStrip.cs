using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.Core;

namespace LandingStats.App.Controls;

// Show the physical device channels as recorded, without guessing that a twist axis is a pedal.
public sealed class PhysicalInputStrip : UserControl
{
    private static readonly string[] AxisNames = { "X", "Y", "Z", "R", "U", "V" };
    private readonly StackPanel _items = new StackPanel { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll = new ScrollViewer();
    private readonly List<TextBlock> _values = new List<TextBlock>();
    private LandingRecord? _record;
    private bool _legacy;
    private int[] _sourceIndices = Array.Empty<int>();
    private int[] _storedIndices = Array.Empty<int>();
    private double _time;

    public PhysicalInputStrip()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _scroll.Content = _items;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Padding = new Thickness(0, 4, 0, 0);
        _scroll.PreviewMouseWheel += (_, e) => { _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta); e.Handled = true; };
        grid.Children.Add(_scroll);
        var back = Arrow("‹", -220); var forward = Arrow("›", 220);
        Grid.SetColumn(back, 1); Grid.SetColumn(forward, 2); grid.Children.Add(back); grid.Children.Add(forward);
        _scroll.ScrollChanged += (_, __) => {
            back.IsEnabled = _scroll.HorizontalOffset > 0;
            forward.IsEnabled = _scroll.HorizontalOffset < _scroll.ScrollableWidth;
            back.Visibility = forward.Visibility = _scroll.ScrollableWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        Content = grid;
    }

    private Button Arrow(string text, double offset)
    {
        var button = new Button { Content = text, Width = 22, Height = 23, Foreground = Brushes.DarkGray,
            Background = new SolidColorBrush(Color.FromRgb(30, 28, 26)), BorderThickness = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };
        button.Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Button\"><Border Background=\"{TemplateBinding Background}\" CornerRadius=\"3\"><ContentPresenter HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" /></Border></ControlTemplate>");
        button.Click += (_, __) => _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + offset);
        return button;
    }

    public LandingRecord? Record
    {
        get => _record;
        set { _record = value; _time = 0; RefreshLocalization(); }
    }

    public void RefreshLocalization()
    {
        _items.Children.Clear();
        _values.Clear();
        _legacy = _record?.RawControllerAxesSourceIndices.Count == 0;
        var legacySources = _record == null ? Array.Empty<int>() : _record.RawControllerSourceIndices.Count > 0
            ? _record.RawControllerSourceIndices.ToArray()
            : Enumerable.Range(0, TelemetrySample.CapturedControllerCount)
                .Where(i => _record.Series.Any(p => p.RawControllerYAxisValid.Length > i && p.RawControllerYAxisValid[i])).ToArray();
        var indices = _record == null ? Array.Empty<int>() : _legacy
            ? legacySources.Select(i => i * TelemetrySample.CapturedControllerAxisCount + 1).ToArray()
            : _record.RawControllerAxesSourceIndices.ToArray();
        _sourceIndices = indices;
        _storedIndices = _legacy && _record?.RawControllerSourceIndices.Count == 0 ? legacySources : Enumerable.Range(0, indices.Length).ToArray();
        var heading = Label(LocalizationManager.Text(indices.Length == 0 ? "Replay.NoPhysicalInputs" : "Replay.PhysicalInputs"), "#8A8279");
        heading.Margin = new Thickness(0, 0, 16, 0);
        _items.Children.Add(heading);
        var previousDevice = -1;
        foreach (var index in indices)
        {
            var device = index / TelemetrySample.CapturedControllerAxisCount;
            if (device != previousDevice)
            {
                var name = _record != null && device < _record.ControlInputSources.Count ? _record.ControlInputSources[device] : "Controller " + (device + 1);
                var title = Label(name, "#B8AEA4");
                title.MaxWidth = 170;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                title.ToolTip = name;
                title.Margin = new Thickness(previousDevice < 0 ? 0 : 16, 0, 10, 0);
                _items.Children.Add(title);
                previousDevice = device;
            }
            var value = Label(AxisNames[index % TelemetrySample.CapturedControllerAxisCount] + " —", "#75B9E7");
            value.Width = 73;
            value.ToolTip = LocalizationManager.Text("Replay.PhysicalInputHint");
            _values.Add(value);
            _items.Children.Add(value);
        }
        SetTime(_time);
    }

    public void SetTime(double time)
    {
        _time = time;
        var point = Nearest(_record?.Series, time);
        for (var i = 0; i < _values.Count; i++)
        {
            var index = _sourceIndices[i];
            var column = _storedIndices[i];
            var values = _legacy ? point?.RawControllerYAxisPercent : point?.RawControllerAxesPercent;
            var valid = _legacy ? point?.RawControllerYAxisValid : point?.RawControllerAxesValid;
            var ages = _legacy ? point?.RawControllerYAxisAgeSeconds : point?.RawControllerAxesAgeSeconds;
            var available = point != null && Math.Abs(point.TimeSeconds - time) <= 0.25 && values != null && valid != null && ages != null &&
                            column < values.Length && column < valid.Length && column < ages.Length && valid[column] &&
                            !double.IsNaN(values[column]) && !double.IsInfinity(values[column]) && ages[column] >= 0 && ages[column] <= 0.25;
            _values[i].Text = AxisNames[index % TelemetrySample.CapturedControllerAxisCount] + (available ? $" {values![column]:+0;-0;0}%" : " —");
        }
    }

    private static LandingSeriesPoint? Nearest(IReadOnlyList<LandingSeriesPoint>? points, double time)
    {
        if (points == null || points.Count == 0) return null;
        var low = 0;
        var high = points.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].TimeSeconds < time) low = middle + 1; else high = middle;
        }
        return low > 0 && Math.Abs(points[low - 1].TimeSeconds - time) < Math.Abs(points[low].TimeSeconds - time) ? points[low - 1] : points[low];
    }

    private static TextBlock Label(string text, string color) => new TextBlock {
        Text = text, FontSize = 10, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        Foreground = (Brush)new BrushConverter().ConvertFromString(color)!, VerticalAlignment = VerticalAlignment.Center,
    };
}
