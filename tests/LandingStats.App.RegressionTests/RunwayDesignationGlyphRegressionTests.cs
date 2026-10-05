using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class RunwayDesignationGlyphRegressionTests
{
    public static void RunAll()
    {
        foreach (var faa in new[] { false, true })
        {
            var nominal = faa ? 18.288 : 9;
            var tip = faa ? .9144 : .5;
            foreach (var character in "0123456789LCR")
            {
                var glyph = RunwayDesignationGlyphs.Build(character.ToString(), faa);
                Require(glyph.IsFrozen && glyph.GetArea() > 0, "frozen painted outline for every designation character");
                Near(glyph.Bounds.Height, nominal + (character == '6' || character == '9' ? tip : 0));
                Near(glyph.Bounds.Top, character == '6' ? -tip : 0);
                foreach (var figure in glyph.GetFlattenedPathGeometry().Figures)
                    Require(figure.IsClosed, "outline and counters must be closed");
            }
            var zero = RunwayDesignationGlyphs.Build("0", faa);
            Require(!zero.FillContains(new Point(zero.Bounds.Width / 2, nominal / 2)), "zero has an unpainted counter");
            Require(zero.FillContains(new Point(zero.Bounds.Width * .1, nominal / 2)), "zero has a painted side stroke");
            var sideStroke = faa ? 1.524 : .8;
            var endStroke = faa ? 3.048 : 1.5;
            var counter = zero.GetFlattenedPathGeometry().Figures.Select(f => new PathGeometry(new[] { f }).Bounds)
                .Single(b => b.Width < zero.Bounds.Width);
            Near(counter.Left, sideStroke); Near(counter.Right, zero.Bounds.Width - sideStroke);
            Near(counter.Top, endStroke); Near(counter.Bottom, nominal - endStroke);
            var sixteen = (GeometryGroup)RunwayDesignationGlyphs.Build("16", faa);
            Near(sixteen.Children[0].Bounds.Top, 0);
            Near(sixteen.Children[1].Bounds.Top, -tip);
            Near(sixteen.Children[0].Bounds.Bottom, sixteen.Children[1].Bounds.Bottom);
            var nineteen = (GeometryGroup)RunwayDesignationGlyphs.Build("19", faa);
            Near(nineteen.Children[0].Bounds.Top, nineteen.Children[1].Bounds.Top);
            Near(nineteen.Children[1].Bounds.Bottom - nineteen.Children[0].Bounds.Bottom, tip);
            Require(RunwayDesignationGlyphs.Build("?", faa).IsEmpty() && RunwayDesignationGlyphs.Build("123", faa).IsEmpty(),
                "unknown text does not silently fall back to a desktop font");

            foreach (var width in new[] { 7d, 18, 45, 60 })
            foreach (var number in new[] { "01", "04", "06", "09", "10", "11", "24", "27", "36" })
            {
                var runway = new RunwayFacility { AirportIdent = faa ? "KJFK" : "EDDM", PrimaryName = number + "L",
                    SecondaryName = "29R", WidthMeters = width, LengthMeters = 4000, SurfaceType = 4 };
                var layout = RunwayMarkingLayout.Build(runway);
                var labels = layout.Shapes.Where(p => p.Text != null).ToArray();
                Require(labels.Length == 4, "number and suffix survive narrow-strip fitting at both ends");
                foreach (var label in labels)
                {
                    var bounds = label.Glyph!.Bounds;
                    var paintWidth = label.Points.Max(p => p.X) - label.Points.Min(p => p.X);
                    var paintHeight = label.Points.Max(p => p.Y) - label.Points.Min(p => p.Y);
                    Near(paintWidth / bounds.Width, paintHeight / bounds.Height);
                    Require(paintWidth < width, "standard shape stays inside actual pavement");
                    var clearance = faa ? 12.192 : 12;
                    foreach (var dash in layout.Shapes.Where(p => p.Kind == "Centerline"))
                        Require(dash.Points.Min(p => p.Y) >= label.DesignationRow.Bottom + clearance - 1e-6 ||
                            dash.Points.Max(p => p.Y) <= label.DesignationRow.Top - clearance + 1e-6,
                            "centreline leaves the designation clearance at both runway ends");
                    var paintScale = paintWidth / bounds.Width;
                    Near(label.DesignationRow.Height, nominal * paintScale);
                    var nominalNear = label.Secondary ? -label.DesignationRow.Bottom : label.DesignationRow.Top;
                    var inkNear = label.Secondary ? -label.Points.Max(p => p.Y) : label.Points.Min(p => p.Y);
                    Near(inkNear + (bounds.Bottom - nominal) * paintScale, nominalNear);
                }
                var digit = labels.First(); var suffix = labels[1];
                Near((digit.Points.Max(p => p.X) - digit.Points.Min(p => p.X)) / digit.Glyph!.Bounds.Width,
                    (suffix.Points.Max(p => p.X) - suffix.Points.Min(p => p.X)) / suffix.Glyph!.Bounds.Width);
                Require(suffix.Points.Max(p => p.Y) < digit.Points.Min(p => p.Y), "suffix precedes number without collision");
                Near(digit.DesignationRow.Top - suffix.DesignationRow.Bottom, faa ? 6.096 : 6);
            }
        }
        Near(RunwayDesignationGlyphs.Build("1", true).Bounds.Width, 6.096);
        Near(((GeometryGroup)RunwayDesignationGlyphs.Build("11", true)).Children[0].Bounds.Width, 2.1336);
        Near(RunwayDesignationGlyphs.Build("11", true).Bounds.Width, 11.8872); // 7 + 25 + 7 ft
        var standard = RunwayDesignationGlyphs.Build("24", true);
        var narrow = RunwayDesignationGlyphs.Build("24", true, true);
        Near(standard.Bounds.Width - narrow.Bounds.Width, 1.524); // 15 ft -> 10 ft
        Near(standard.Bounds.Height, narrow.Bounds.Height);
        Near(RunwayDesignationGlyphs.Build("10", false).Bounds.Width, 6.3);
        Near(RunwayDesignationGlyphs.Build("11", false).Bounds.Width, 6);
    }

    private static void Near(double actual, double expected) => Require(Math.Abs(actual - expected) < 1e-6, $"{actual} != {expected}");
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
