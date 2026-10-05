using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;

namespace LandingStats.App.Controls;

// Schematic paint, never recovered scenery textures. X is across the runway;
// Y is along its primary direction, with the facility centre at zero.
internal sealed class RunwayMarkingLayout
{
    internal sealed class Paint
    {
        public string Kind { get; set; } = "";
        public Point[] Points { get; set; } = Array.Empty<Point>();
        public string? Text { get; set; }
        public Geometry? Glyph { get; set; }
        public Rect DesignationRow { get; set; } = Rect.Empty;
        public bool Secondary { get; set; }
    }

    internal sealed class Approach
    {
        public double AimingFromEndMeters { get; }
        public double ThresholdFromEndMeters => AimingFromEndMeters - ReferenceApproachDistance;
        public bool UsesIls { get; }
        public Approach(double aiming, bool ils) { AimingFromEndMeters = aiming; UsesIls = ils; }
    }

    // Product reconstruction convention: normalize to 50 ft at 3 degrees,
    // even when the selected facility's actual glide-path angle is different.
    internal static readonly double ReferenceApproachDistance = 15.24 / Math.Tan(3 * Math.PI / 180);
    public List<Paint> Shapes { get; } = new List<Paint>();
    public bool UsesFaa { get; private set; }
    public static bool UsesFaaConvention(RunwayFacility runway) => RunwayMarkingConvention.UsesFaa(runway);

    internal static Approach? ResolveApproach(RunwayFacility runway, bool secondary)
    {
        if (!runway.HasValidGeometry) return null;
        var ils = secondary ? runway.SecondaryIlsGlideSlope : runway.PrimaryIlsGlideSlope;
        if (ils != null && ils.HasValidGeometry)
        {
            RunwayResolver.OffsetMeters(runway.LatitudeDegrees, runway.LongitudeDegrees,
                ils.LatitudeDegrees, ils.LongitudeDegrees, out var east, out var north);
            var heading = runway.HeadingTrueDegrees * Math.PI / 180;
            var along = (east * Math.Sin(heading) + north * Math.Cos(heading)) * (secondary ? -1 : 1);
            var across = east * Math.Cos(heading) - north * Math.Sin(heading);
            // Intersect the nominal descending line with the horizontal runway
            // facility-altitude plane. Neither a touchdown nor its height is input.
            var intersection = runway.LengthMeters / 2 + along +
                (ils.AltitudeMeters - runway.AltitudeMeters) / Math.Tan(ils.AngleDegrees * Math.PI / 180);
            if (RunwayNumber.IsFinite(intersection) && intersection >= 0 && intersection < runway.LengthMeters &&
                Math.Abs(across) <= runway.LengthMeters)
                return new Approach(intersection, true);
        }
        var papi = secondary ? runway.SecondaryPapi : runway.PrimaryPapi;
        if ((secondary ? runway.SecondaryPapiQueried : runway.PrimaryPapiQueried) &&
            papi != null && papi.HasValidGeometry(runway.LengthMeters))
            // The API has no optical elevation. The explicit PAPI fallback is
            // nominal/coplanar, NOT a measured intersection or an invented lamp height.
            return new Approach(papi.StationFromEndMeters, false);
        return null;
    }

    public static RunwayMarkingLayout Build(RunwayFacility runway)
    {
        var result = new RunwayMarkingLayout { UsesFaa = UsesFaaConvention(runway) };
        if (!runway.HasValidGeometry || !runway.HasPaintedSurface || runway.WidthMeters <= 6 || runway.LengthMeters < 180) return result;
        var primary = ResolveApproach(runway, false);
        var secondary = ResolveApproach(runway, true);
        var half = runway.LengthMeters / 2;
        result.Designation(runway, primary, secondary, false);
        result.Designation(runway, secondary, primary, true);
        // Without guidance retain known runway designations and the centreline,
        // but do not invent landing-zone markings.
        var start = Math.Max(-half, -half + (primary?.ThresholdFromEndMeters ?? 0)) + 60;
        var end = Math.Min(half, half - (secondary?.ThresholdFromEndMeters ?? 0)) - 60;
        var centerlineWidth = result.UsesFaa ? .4572 : .45;
        var centerlineLength = result.UsesFaa ? 36.576 : 30;
        var designationClearance = result.UsesFaa ? 12.192 : 12;
        for (; start + centerlineLength <= end; start += result.UsesFaa ? 60.96 : 60)
            if (!result.Shapes.Any(p => p.Text != null && p.DesignationRow.Top - designationClearance < start + centerlineLength &&
                p.DesignationRow.Bottom + designationClearance > start))
                result.Rectangle("Centerline", start, start + centerlineLength, -centerlineWidth / 2, centerlineWidth / 2, false);
        if (primary != null) result.End(runway, primary, secondary, false);
        if (secondary != null) result.End(runway, secondary, primary, true);
        return result;
    }

    private void Designation(RunwayFacility runway, Approach? approach, Approach? opposite, bool secondary)
    {
        var name = (secondary ? runway.SecondaryName : runway.PrimaryName)?.Trim().ToUpperInvariant();
        if (name == null || (name.Length != 2 && name.Length != 3) ||
            !int.TryParse(name.Substring(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number < 1 || number > 36 || (name.Length == 3 && "LRC".IndexOf(name[2]) < 0)) return;
        var half = runway.LengthMeters / 2;
        var threshold = -half + (approach?.ThresholdFromEndMeters ??
            (secondary ? runway.SecondaryThresholdOffsetMeters : runway.PrimaryThresholdOffsetMeters) ?? 0);
        var farThreshold = half - (opposite?.ThresholdFromEndMeters ??
            (secondary ? runway.PrimaryThresholdOffsetMeters : runway.SecondaryThresholdOffsetMeters) ?? 0);
        var digits = number.ToString(UsesFaa ? "0" : "00", CultureInfo.InvariantCulture);
        var glyph = RunwayDesignationGlyphs.Build(digits, UsesFaa);
        var availableWidth = runway.WidthMeters - (UsesFaa ? 2 * RunwayDesignationFaa.EdgeClearanceMeters : 6);
        if (UsesFaa && glyph.Bounds.Width > availableWidth)
            glyph = RunwayDesignationGlyphs.Build(digits, true, narrowSpacing: true);
        // ICAO L-2 dimensions are minima (9 m nominal height); use a uniform
        // enlargement to 18 m, preserving the diagram's proportions. On narrow
        // pavement fit the entire designation uniformly, never just its width.
        var scale = Math.Min(UsesFaa ? 1 : 2, availableWidth / glyph.Bounds.Width);
        var nominalHeight = UsesFaa ? RunwayDesignationFaa.NominalHeightMeters : 9;
        // L-1 / A-1: 12 m / 40 ft beyond the piano keys, then the letter
        // (when present), followed by a 6 m / 20 ft gap before the number.
        var firstStart = threshold + (UsesFaa ? 6.096 + 45.72 + 12.192 : 6 + 30 + 12);
        var numberStart = firstStart + (name.Length == 3 ? nominalHeight * scale + (UsesFaa ? 6.096 : 6) : 0);
        Label(digits, glyph, numberStart);
        if (name.Length == 3)
            Label(name.Substring(2), RunwayDesignationGlyphs.Build(name.Substring(2), UsesFaa), firstStart);
        void Label(string text, Geometry outline, double nominalStart)
        {
            // The longer 6/9 tips do not change nominal-row spacing (FAA 2.3.6).
            // Keep ink bounds separate for the texture and pavement clipping.
            var bounds = outline.Bounds;
            var from = nominalStart + (nominalHeight - bounds.Bottom) * scale;
            var length = bounds.Height * scale;
            var width = bounds.Width * scale;
            if (from < -half || from + length > Math.Min(half, (threshold + farThreshold) / 2)) return;
            Rectangle("Designation", from, from + length, -width / 2, width / 2, secondary);
            Shapes[Shapes.Count - 1].Text = text;
            Shapes[Shapes.Count - 1].Glyph = outline;
            Shapes[Shapes.Count - 1].DesignationRow = new Rect(-width / 2,
                secondary ? -nominalStart - nominalHeight * scale : nominalStart, width, nominalHeight * scale);
        }
    }

    private void End(RunwayFacility runway, Approach approach, Approach? opposite, bool secondary)
    {
        var width = runway.WidthMeters;
        var half = runway.LengthMeters / 2;
        var threshold = -half + approach.ThresholdFromEndMeters;
        var oppositeThreshold = half - (opposite?.ThresholdFromEndMeters ?? 0);
        var between = oppositeThreshold - threshold;
        if (between <= 0) return;
        // Geometric usable pavement, not declared LDA. Retain the inferred
        // threshold coordinate even if it is outside the footprint; never shift
        // the lattice to make a questionable scenery reference appear correct.
        var available = half - Math.Max(-half, threshold);
        var usableBetween = Math.Min(half, oppositeThreshold) - Math.Max(-half, threshold);
        var limit = opposite == null ? half : threshold + between / 2;
        void Bar(string kind, double distance, double length, double left, double right)
        {
            var from = threshold + distance;
            var to = from + length;
            if (from < -half || to > half || to > limit || left < -width / 2 || right > width / 2) return;
            Rectangle(kind, from, to, left, right, secondary);
        }

        var count = width >= 59.5 ? 16 : width >= 44.5 ? 12 : width >= 29.5 ? 8 : width >= 22.5 ? 6 : 4;
        if (UsesFaa)
        {
            // FAA standard widths have explicit counts (75 ft is an exception
            // to the edge clearance used for nonstandard widths).
            var widths = new[] { 18.288, 22.86, 30.48, 45.72, 60.96 };
            var countsByWidth = new[] { 4, 6, 8, 12, 16 };
            var standard = Array.FindIndex(widths, value => Math.Abs(width - value) < .01);
            count = standard >= 0 ? countsByWidth[standard] :
                Math.Max(0, (int)Math.Floor(Math.Min(width / 2 - 1.2192, 28.0416) / (2 * 1.7526) + 1e-9)) * 2;
        }
        var pairs = count / 2;
        var stripe = UsesFaa ? 1.7526 : Math.Min(1.8, Math.Max(.2, Math.Min(width / 2 - 3, 27)) / (pairs * 2));
        for (var i = 0; i < pairs; i++)
        {
            var inner = stripe * (1 + i * 2);
            Bar("Threshold", UsesFaa ? 6.096 : 6, UsesFaa ? 45.72 : 30, inner, inner + stripe);
            Bar("Threshold", UsesFaa ? 6.096 : 6, UsesFaa ? 45.72 : 30, -inner - stripe, -inner);
        }

        var aiming = ReferenceApproachDistance;
        var aimingLength = UsesFaa ? (usableBetween < 1280.16 ? 30.48 : 45.72) : available < 1200 ? 30 : 45;
        var gap = UsesFaa ? 21.9456 : available < 800 ? 6 : available < 1200 ? 9 : 18;
        var barWidth = UsesFaa ? 9.144 : available < 800 ? 4 : 6;
        var scale = UsesFaa ? Math.Min(1, width / 45.72) : Math.Min(1, width / (gap + 2 * barWidth));
        gap *= scale; barWidth *= scale;
        Bar("Aiming", aiming, aimingLength, gap / 2, gap / 2 + barWidth);
        Bar("Aiming", aiming, aimingLength, -gap / 2 - barWidth, -gap / 2);

        var barLength = UsesFaa ? 22.86 : 22.5;
        // ICAO allows pattern A or B. On narrow strips use A rather than
        // shrinking pattern B below its specified minimum bar width/spacing.
        var patternB = UsesFaa || gap + 2 * (3 * 1.8 + 2 * 1.5) <= width;
        var smallWidth = UsesFaa ? 1.8288 * scale : patternB ? 1.8 : 3;
        var spacing = UsesFaa ? 1.524 * scale : 1.5;
        var slots = UsesFaa ? new[] { 158.496, 463.296, 615.696, 768.096, 920.496 } : new[] { 150d, 300, 450, 600, 750, 900 };
        var counts = UsesFaa ? new[] { 3, 2, 2, 1, 1 } : new[] { 3, 3, 2, 2, 1, 1 };
        var lengthForCount = opposite == null ? available : usableBetween;
        var pairCount = UsesFaa ? slots.Length : lengthForCount < 900 ? 1 : lengthForCount < 1200 ? 2 : lengthForCount < 1500 ? 3 : lengthForCount < 2400 ? 4 : 6;
        // FAA 2.7.3.1 / Tables 2-3, 2-4: retain 900 ft from the midpoint for
        // reciprocal patterns. For one marked end, reserve the opposing
        // standard aiming footprint and both 900 ft clearance allowances.
        var tdzLimit = !UsesFaa ? limit : opposite != null ? limit - 274.32 :
            half - 310.896 - aimingLength - 548.64;
        var rows = new List<int>();
        for (var i = 0; i < pairCount; i++)
        {
            var from = threshold + slots[i];
            var to = from + barLength;
            var clear = Math.Max(aiming - slots[i] - barLength, slots[i] - aiming - aimingLength);
            if (clear <= (UsesFaa ? 48.768 : 50) || from < -half || to > tdzLimit) continue;
            rows.Add(i);
        }
        foreach (var i in rows)
        {
            // EASA GM1 L.545(a): the last retained pair is one stripe per side.
            var bars = !patternB || (!UsesFaa && i == rows[rows.Count - 1]) ? 1 : counts[i];
            for (var j = 0; j < bars; j++)
            {
                var inner = gap / 2 + j * (smallWidth + spacing);
                Bar("TouchdownZone", slots[i], barLength, inner, inner + smallWidth);
                Bar("TouchdownZone", slots[i], barLength, -inner - smallWidth, -inner);
            }
        }
    }

    private void Rectangle(string kind, double from, double to, double left, double right, bool secondary)
    {
        var points = new[] { new Point(left, from), new Point(right, from), new Point(right, to), new Point(left, to) };
        if (secondary) points = points.Select(p => new Point(-p.X, -p.Y)).ToArray();
        Shapes.Add(new Paint { Kind = kind, Points = points, Secondary = secondary });
    }
}
