using System.Collections.Generic;
using System.Windows.Media;

namespace LandingStats.App.Controls;

// Dimension-led outlines from EASA CS ADR-DSN Figure L-2 (2026 PDF p.649).
// EASA embeds the figure as a raster, not a font/vector asset. Stated dimensions
// are retained below; undimensioned diagonal joins/chamfers are schematic.
// Coordinates are metres, top-left origin. Nominal row height is 9 m; the 6
// extends 0.5 m above that row and the 9 extends 0.5 m below it (height 9.5 m).
// Source: https://www.easa.europa.eu/en/downloads/98016/en
internal static class RunwayDesignationIcao
{
    private sealed class Glyph
    {
        public Geometry Geometry { get; }
        public double Width { get; }
        public double Height { get; }
        public Glyph(string path, double width, double height = 9)
        {
            Geometry = Geometry.Parse("F0 " + path);
            Geometry.Freeze(); Width = width; Height = height;
        }
    }

    private static readonly IReadOnlyDictionary<char, Glyph> Glyphs = new Dictionary<char, Glyph>
    {
        ['0'] = new Glyph("M0,0 L3,0 3,9 0,9 Z M.8,1.5 L2.2,1.5 2.2,7.5 .8,7.5 Z", 3),
        ['1'] = new Glyph("M.3,0 L1.1,0 1.1,9 .3,9 .3,1.5 0,1.5 0,.3 Z", 1.1),
        ['2'] = new Glyph("M0,0 L3,0 3,3.2 .8,6.7 .8,7.5 3,7.5 3,9 0,9 0,6.4 2.2,2.9 2.2,1.5 .8,1.5 .8,2.4 0,2.4 Z", 3),
        ['3'] = new Glyph("M0,0 L3,0 3,2.5 1.9,3.6 3,4.7 3,9 0,9 0,7.5 2.2,7.5 2.2,5 .8,3.6 2.2,2.2 2.2,1.5 0,1.5 Z", 3),
        ['4'] = new Glyph("M1.3,0 L2.1,0 1,5.6 2.4,5.6 2.4,2.7 3.2,2.7 3.2,5.6 3.9,5.6 3.9,7.1 3.2,7.1 3.2,9 2.4,9 2.4,7.1 0,7.1 Z", 3.9),
        ['5'] = new Glyph("M0,0 L3,0 3,1.5 .8,1.5 .8,2.7 3,2.7 3,9 0,9 0,7.5 2.2,7.5 2.2,4.2 0,4.2 Z", 3),
        ['6'] = new Glyph("M0,2 L2.2,0 2.2,1 .8,2.4 .8,4 3,4 3,9.5 0,9.5 Z M.8,5.5 L2.2,5.5 2.2,8 .8,8 Z", 3, 9.5),
        ['7'] = new Glyph("M0,0 L3.5,0 1.1,9 .3,9 2.4,1.5 0,1.5 Z", 3.5),
        ['8'] = new Glyph("M0,0 L3,0 3,3.15 2.2,3.9 3,4.65 3,9 0,9 0,4.65 .8,3.9 0,3.15 Z M.8,1.5 L2.2,1.5 2.2,3.35 .8,3.35 Z M.8,4.45 L2.2,4.45 2.2,7.5 .8,7.5 Z", 3),
        ['9'] = new Glyph("M0,0 L3,0 3,7.5 .8,9.5 .8,8.5 2.2,7.1 2.2,5.5 0,5.5 Z M.8,1.5 L2.2,1.5 2.2,4 .8,4 Z", 3, 9.5),
        ['L'] = new Glyph("M0,0 L.8,0 .8,7.5 3,7.5 3,9 0,9 Z", 3),
        ['C'] = new Glyph("M0,0 L3,0 3,2.1 2.2,2.1 2.2,1.5 .8,1.5 .8,7.5 2.2,7.5 2.2,6.9 3,6.9 3,9 0,9 Z", 3),
        ['R'] = new Glyph("M0,0 L3,0 3,5.3 2.2,5.3 3,9 2.2,9 1.5,5.3 .8,5.3 .8,9 0,9 Z M.8,1.5 L2.2,1.5 2.2,3.8 .8,3.8 Z", 3),
    };

    internal static bool TryGet(char character, out Geometry geometry, out double widthMeters, out double heightMeters)
    {
        if (Glyphs.TryGetValue(character, out var glyph))
        { geometry = glyph.Geometry; widthMeters = glyph.Width; heightMeters = glyph.Height; return true; }
        geometry = Geometry.Empty; widthMeters = heightMeters = 0; return false;
    }

    // L-2 explicitly dimensions 10 and 11. Its total-width dimension starts at
    // the first 1's stem, excluding the 0.3 m left tab. Other pairs use a 1.5 m
    // clear-gap reconstruction convention; L-2 does not prescribe that gap.
    internal static double Spacing(string text) => text == "10" ? 2.2 : text == "11" ? 3.8 : 1.5;
}
