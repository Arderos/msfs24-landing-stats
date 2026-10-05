using System.Collections.Generic;
using System.Windows.Media;

namespace LandingStats.App.Controls;

// FAA AC 150/5340-1M, Figure A-6 (printed page A-6), dimensioned outlines:
// https://www.faa.gov/documentLibrary/media/Advisory_Circular/150-5340-1M-Chg-1-Airport-Markings.pdf
// Dimensions, not the schematic PDF scale, govern the 5-ft vertical/10-ft
// horizontal strokes and explicitly dimensioned counters, notches and terminals.
// Undimensioned serif corners and the longitudinal position of 2's diagonal follow
// the figure; diagonals in 2/3/4/6/7/9/R retain the nominal 5-ft normal thickness.
// Coordinates are top-left based (X right, Y down). No installed font is involved.
internal static class RunwayDesignationFaa
{
    internal const double NominalHeightMeters = 18.288; // 60 ft
    internal const double TipOverhangMeters = .9144; // 6 above / 9 below the nominal row
    internal const double DigitSpacingMeters = 4.572; // 15 ft
    internal const double ElevenStemSpacingMeters = 8.2296; // Figure A-6: 27 ft between the vertical stems
    internal const double ElevenSpacingMeters = 7.62; // 25 ft between glyph bounds, excluding the second 1's 2-ft serif
    internal const double NarrowDigitSpacingMeters = 3.048; // 10 ft before scaling
    internal const double EdgeClearanceMeters = .6096; // 2 ft, paragraph 2.3

    private static readonly Dictionary<char, (double Width, double Height, string Path)> Outlines =
        new Dictionary<char, (double, double, string)>
    {
        ['0'] = (6.096, 18.288, "F0 M 0.000000,0.000000 L 6.096000,0.000000 L 6.096000,18.288000 L 0.000000,18.288000 Z M 1.524000,3.048000 L 4.572000,3.048000 L 4.572000,15.240000 L 1.524000,15.240000 Z"),
        ['1'] = (2.1336, 18.288, "F0 M 0.609600,18.288000 L 0.609600,3.048000 L 0.000000,3.048000 L 0.000000,0.609600 L 0.609600,0.000000 L 2.133600,0.000000 L 2.133600,18.288000 Z"),
        ['2'] = (6.096, 18.288, "F0 M 0.000000,18.288000 L 0.000000,12.855730 L 4.572000,5.791200 L 4.572000,3.048000 L 1.524000,3.048000 L 1.524000,4.876800 L 0.000000,4.876800 L 0.000000,0.000000 L 6.096000,0.000000 L 6.096000,6.241328 L 1.524000,13.305858 L 1.524000,15.240000 L 6.096000,15.240000 L 6.096000,18.288000 Z"),
        ['3'] = (6.096, 18.288, "F0 M 0.000000,18.288000 L 0.000000,15.240000 L 4.572000,15.240000 L 4.572000,10.077041 L 1.828800,7.315200 L 4.572000,4.553359 L 4.572000,3.048000 L 0.000000,3.048000 L 0.000000,0.000000 L 6.096000,0.000000 L 6.096000,5.181600 L 3.976800,7.315200 L 6.096000,9.448800 L 6.096000,18.288000 Z"),
        ['4'] = (7.62, 18.288, "F0 M 4.572000,18.288000 L 4.572000,14.325600 L 0.000000,14.325600 L 0.000000,11.277600 L 2.995168,0.000000 L 4.572000,0.000000 L 1.576832,11.277600 L 4.572000,11.277600 L 4.572000,5.791200 L 6.096000,5.791200 L 6.096000,11.277600 L 7.620000,11.277600 L 7.620000,14.325600 L 6.096000,14.325600 L 6.096000,18.288000 Z"),
        ['5'] = (6.096, 18.288, "F0 M 0.000000,18.288000 L 0.000000,15.240000 L 4.572000,15.240000 L 4.572000,8.534400 L 0.000000,8.534400 L 0.000000,0.000000 L 6.096000,0.000000 L 6.096000,3.048000 L 1.524000,3.048000 L 1.524000,5.486400 L 6.096000,5.486400 L 6.096000,18.288000 Z"),
        ['6'] = (6.096, 19.2024, "F0 M 0.000000,19.202400 L 0.000000,3.962400 L 4.572000,0.000000 L 4.572000,2.016702 L 1.524000,4.658302 L 1.524000,7.924800 L 6.096000,7.924800 L 6.096000,19.202400 Z M 1.524000,10.972800 L 4.572000,10.972800 L 4.572000,16.154400 L 1.524000,16.154400 Z"),
        ['7'] = (7.0104, 18.288, "F0 M 0.609600,18.288000 L 4.630148,3.048000 L 0.000000,3.048000 L 0.000000,0.000000 L 7.010400,0.000000 L 2.185742,18.288000 Z"),
        ['8'] = (6.096, 18.288, "F0 M 0.000000,18.288000 L 0.000000,8.991600 L 1.066800,7.924800 L 0.000000,6.858000 L 0.000000,0.000000 L 6.096000,0.000000 L 6.096000,6.858000 L 5.029200,7.924800 L 6.096000,8.991600 L 6.096000,18.288000 Z M 1.524000,9.448800 L 4.572000,9.448800 L 4.572000,15.240000 L 1.524000,15.240000 Z M 1.524000,3.352800 L 4.572000,3.352800 L 4.572000,6.400800 L 1.524000,6.400800 Z"),
        ['9'] = (6.096, 19.2024, "F0 M 6.096000,0.000000 L 6.096000,15.240000 L 1.524000,19.202400 L 1.524000,17.185698 L 4.572000,14.544098 L 4.572000,11.277600 L 0.000000,11.277600 L 0.000000,0.000000 Z M 1.524000,3.048000 L 4.572000,3.048000 L 4.572000,8.229600 L 1.524000,8.229600 Z"),
        ['L'] = (6.096, 18.288, "F0 M 0.000000,18.288000 L 0.000000,0.000000 L 1.524000,0.000000 L 1.524000,15.240000 L 6.096000,15.240000 L 6.096000,18.288000 Z"),
        ['C'] = (6.096, 18.288, "F0 M 0.000000,18.288000 L 0.000000,0.000000 L 6.096000,0.000000 L 6.096000,4.267200 L 4.572000,4.267200 L 4.572000,3.048000 L 1.524000,3.048000 L 1.524000,15.240000 L 4.572000,15.240000 L 4.572000,14.020800 L 6.096000,14.020800 L 6.096000,18.288000 Z"),
        ['R'] = (6.096, 18.288, "F0 M 4.542948,18.288000 L 3.048000,10.668000 L 1.524000,10.668000 L 1.524000,18.288000 L 0.000000,18.288000 L 0.000000,0.000000 L 6.096000,0.000000 L 6.096000,10.668000 L 4.601052,10.668000 L 6.096000,18.288000 Z M 1.524000,3.048000 L 4.572000,3.048000 L 4.572000,7.620000 L 1.524000,7.620000 Z"),
    };
    private const string SingleOne = "F0 M 0.000000,18.288000 L 0.000000,15.240000 L 2.286000,15.240000 L 2.286000,3.048000 L 1.676400,3.048000 L 1.676400,0.609600 L 2.286000,0.000000 L 3.810000,0.000000 L 3.810000,15.240000 L 6.096000,15.240000 L 6.096000,18.288000 Z";

    public static bool TryGet(char glyph, out Geometry geometry, out double widthMeters,
        out double heightMeters, bool singleOne = false)
    {
        if (!Outlines.TryGetValue(char.ToUpperInvariant(glyph), out var outline))
        {
            geometry = Geometry.Empty; widthMeters = heightMeters = 0; return false;
        }
        var standaloneOne = glyph == '1' && singleOne;
        geometry = Geometry.Parse(standaloneOne ? SingleOne : outline.Path);
        geometry.Freeze();
        widthMeters = standaloneOne ? 6.096 : outline.Width;
        heightMeters = outline.Height;
        return true;
    }
}
