using System.Windows.Media;

namespace LandingStats.App.Controls;

// Compose dimensioned runway-paint outlines, not typeset text. Bounds retain the
// 6/9 tips outside the nominal row so neither digit is stretched to its neighbours.
internal static class RunwayDesignationGlyphs
{
    internal static Geometry Build(string text, bool faa, bool narrowSpacing = false)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2) return Geometry.Empty;
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        var x = 0d;
        var gap = faa ? text == "11" ? RunwayDesignationFaa.ElevenSpacingMeters :
            narrowSpacing ? RunwayDesignationFaa.NarrowDigitSpacingMeters : RunwayDesignationFaa.DigitSpacingMeters :
            RunwayDesignationIcao.Spacing(text);
        foreach (var character in text)
        {
            Geometry outline; double width, height;
            var found = faa
                ? RunwayDesignationFaa.TryGet(character, out outline, out width, out height, text == "1")
                : RunwayDesignationIcao.TryGet(character, out outline, out width, out height);
            if (!found) return Geometry.Empty;
            var placed = outline.Clone();
            var overhang = faa ? RunwayDesignationFaa.TipOverhangMeters : .5;
            placed.Transform = new TranslateTransform(x, character == '6' ? -overhang : 0);
            group.Children.Add(placed);
            x += width + gap;
        }
        group.Freeze();
        return group;
    }
}
