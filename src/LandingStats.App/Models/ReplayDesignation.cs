using System;

namespace LandingStats.App.ReplayMath;

internal readonly struct Point2D
{
    public Point2D(double x, double y) { X = x; Y = y; }
    public double X { get; }
    public double Y { get; }
}
internal readonly struct Rectangle2D
{
    public Rectangle2D(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
    public static Rectangle2D Empty => default;
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public double Top => Y;
    public double Bottom => Y + Height;
}

// Dimensioned bounds of the existing FAA/ICAO polygon outlines. No font or
// presentation geometry is loaded by the recording/diagnostics worker.
internal sealed class ReplayDesignation
{
    internal const double EdgeClearanceMeters = .6096;
    internal const double NominalHeightMeters = 18.288;
    public string Text { get; private set; } = "";
    public bool Narrow { get; private set; }
    public Rectangle2D Bounds { get; private set; }
    public static ReplayDesignation Build(string text, bool faa, bool narrowSpacing = false)
    {
        var nominal = faa ? NominalHeightMeters : 9;
        var overhang = faa ? .9144 : .5;
        var gap = faa ? text == "11" ? 7.62 : narrowSpacing ? 3.048 : 4.572 : text == "10" ? 2.2 : text == "11" ? 3.8 : 1.5;
        var width = 0d; var top = 0d; var bottom = nominal;
        foreach (var c in text)
        {
            width += faa ? c == '1' && text != "1" ? 2.1336 : c == '4' ? 7.62 : c == '7' ? 7.0104 : 6.096
                : c == '1' ? 1.1 : c == '4' ? 3.9 : c == '7' ? 3.5 : 3;
            width += gap;
            if (c == '6') top = -overhang;
            if (c == '9') bottom = nominal + overhang;
        }
        return new ReplayDesignation { Text = text, Narrow = narrowSpacing, Bounds = new Rectangle2D(0, top, Math.Max(0, width - gap), bottom - top) };
    }
}
