using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LandingStats.App.ReplayMath;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.Models;

// Schematic paint, never recovered scenery textures. X is across the runway;
// Y is along its primary direction, with the facility centre at zero.
internal sealed class RunwayMarkingModel
{
    internal sealed class Paint
    {
        public string Kind { get; set; } = "";
        public Point2D[] Points { get; set; } = Array.Empty<Point2D>();
        public string? Text { get; set; }
        public ReplayDesignation? Glyph { get; set; }
        public Rectangle2D DesignationRow { get; set; } = Rectangle2D.Empty;
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
    private TelemetryDecisionJournal? _journal;
    public static bool UsesFaaConvention(RunwayFacility runway) => RunwayMarkingConvention.UsesFaa(runway);

    internal static Approach? ResolveApproach(RunwayFacility runway, bool secondary, TelemetryDecisionJournal? journal = null)
    {
        if (!runway.HasValidGeometry)
        { journal?.Record(0, "replay.markings", "invalid_runway", "Guidance cannot be projected without valid runway geometry."); return null; }
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
            {
                journal?.Record(0, "replay.markings", "ils_selected", "ILS nominal line intersects the runway-altitude plane; this sets aiming-mark start, not the observed touchdown.",
                    new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0, ["aiming_from_end_m"] = intersection,
                        ["angle_deg"] = ils.AngleDegrees, ["altitude_delta_m"] = ils.AltitudeMeters - runway.AltitudeMeters });
                return new Approach(intersection, true);
            }
            journal?.Record(0, "replay.markings", "ils_intersection_rejected", "ILS intersection lies outside the supported runway bounds; trying PAPI.",
                new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0, ["intersection_m"] = intersection, ["across_m"] = across });
        }
        else if (ils != null) journal?.Record(0, "replay.markings", "invalid_ils", "ILS geometry is invalid; trying PAPI.", new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0 });
        var papi = secondary ? runway.SecondaryPapi : runway.PrimaryPapi;
        if ((secondary ? runway.SecondaryPapiQueried : runway.PrimaryPapiQueried) &&
            papi != null && papi.HasValidGeometry(runway.LengthMeters))
        {
            // The API has no optical elevation. The explicit PAPI fallback is
            // nominal/coplanar, NOT a measured intersection or an invented lamp height.
            journal?.Record(0, "replay.markings", "papi_coplanar_fallback", "Optical elevation is unavailable; approved coplanar approximation uses PAPI station as aiming-mark start.",
                new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0, ["aiming_from_end_m"] = papi.StationFromEndMeters });
            return new Approach(papi.StationFromEndMeters, false);
        }
        journal?.Record(0, "replay.markings", "no_usable_guidance", "No usable ILS or queried valid PAPI; omit threshold, aiming and touchdown-zone markings for this end.",
            new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0 });
        return null;
    }

    public static RunwayMarkingModel Build(RunwayFacility runway, TelemetryDecisionJournal? journal = null)
    {
        var result = new RunwayMarkingModel { UsesFaa = UsesFaaConvention(runway), _journal = journal };
        journal?.Record(0, "replay.markings", "regional_convention", result.UsesFaa ? "FAA dimensions and glyphs selected from facility region/ICAO family." : "ICAO dimensions and glyphs selected from facility region/ICAO family.",
            new Dictionary<string, double> { ["faa"] = result.UsesFaa ? 1 : 0, ["length_m"] = runway.LengthMeters, ["width_m"] = runway.WidthMeters });
        if (!runway.HasValidGeometry || !runway.HasPaintedSurface || runway.WidthMeters <= 6 || runway.LengthMeters < 180)
        { journal?.Record(0, "replay.markings", "paint_unavailable", "Runway geometry, surface or dimensions do not support painted markings."); return result; }
        var primary = ResolveApproach(runway, false, journal);
        var secondary = ResolveApproach(runway, true, journal);
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
        journal?.Record(0, "replay.markings", "layout_created", "Runway paint is schematic and independent of recorded touchdown positions.",
            new Dictionary<string, double> { ["shapes"] = result.Shapes.Count, ["centerline"] = result.Shapes.Count(p => p.Kind == "Centerline"),
                ["designation"] = result.Shapes.Count(p => p.Kind == "Designation"), ["threshold"] = result.Shapes.Count(p => p.Kind == "Threshold"),
                ["aiming"] = result.Shapes.Count(p => p.Kind == "Aiming"), ["touchdown_zone"] = result.Shapes.Count(p => p.Kind == "TouchdownZone") });
        return result;
    }

    private void Designation(RunwayFacility runway, Approach? approach, Approach? opposite, bool secondary)
    {
        var name = (secondary ? runway.SecondaryName : runway.PrimaryName)?.Trim().ToUpperInvariant();
        if (name == null || (name.Length != 2 && name.Length != 3) ||
            !int.TryParse(name.Substring(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number < 1 || number > 36 || (name.Length == 3 && "LRC".IndexOf(name[2]) < 0))
        { _journal?.Record(0, "replay.markings", "designation_invalid", "Runway designation is missing or not a supported number/suffix.", new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0 }); return; }
        var half = runway.LengthMeters / 2;
        var threshold = -half + (approach?.ThresholdFromEndMeters ??
            (secondary ? runway.SecondaryThresholdOffsetMeters : runway.PrimaryThresholdOffsetMeters) ?? 0);
        var farThreshold = half - (opposite?.ThresholdFromEndMeters ??
            (secondary ? runway.PrimaryThresholdOffsetMeters : runway.SecondaryThresholdOffsetMeters) ?? 0);
        var digits = number.ToString(UsesFaa ? "0" : "00", CultureInfo.InvariantCulture);
        var glyph = ReplayDesignation.Build(digits, UsesFaa);
        var availableWidth = runway.WidthMeters - (UsesFaa ? 2 * ReplayDesignation.EdgeClearanceMeters : 6);
        if (UsesFaa && glyph.Bounds.Width > availableWidth)
            glyph = ReplayDesignation.Build(digits, true, narrowSpacing: true);
        // ICAO L-2 dimensions are minima (9 m nominal height); use a uniform
        // enlargement to 18 m, preserving the diagram's proportions. On narrow
        // pavement fit the entire designation uniformly, never just its width.
        var scale = Math.Min(UsesFaa ? 1 : 2, availableWidth / glyph.Bounds.Width);
        var nominalHeight = UsesFaa ? ReplayDesignation.NominalHeightMeters : 9;
        // L-1 / A-1: 12 m / 40 ft beyond the piano keys, then the letter
        // (when present), followed by a 6 m / 20 ft gap before the number.
        var firstStart = threshold + (UsesFaa ? 6.096 + 45.72 + 12.192 : 6 + 30 + 12);
        var numberStart = firstStart + (name.Length == 3 ? nominalHeight * scale + (UsesFaa ? 6.096 : 6) : 0);
        Label(digits, glyph, numberStart);
        if (name.Length == 3)
            Label(name.Substring(2), ReplayDesignation.Build(name.Substring(2), UsesFaa), firstStart);
        void Label(string text, ReplayDesignation outline, double nominalStart)
        {
            // The longer 6/9 tips do not change nominal-row spacing (FAA 2.3.6).
            // Keep ink bounds separate for the texture and pavement clipping.
            var bounds = outline.Bounds;
            var from = nominalStart + (nominalHeight - bounds.Bottom) * scale;
            var length = bounds.Height * scale;
            var width = bounds.Width * scale;
            if (from < -half || from + length > Math.Min(half, (threshold + farThreshold) / 2))
            { _journal?.Record(0, "replay.markings", "designation_clipped", "Designation would cross pavement or reciprocal-end limits.", new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0 }); return; }
            Rectangle("Designation", from, from + length, -width / 2, width / 2, secondary);
            Shapes[Shapes.Count - 1].Text = text;
            Shapes[Shapes.Count - 1].Glyph = outline;
            Shapes[Shapes.Count - 1].DesignationRow = new Rectangle2D(-width / 2,
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
        if (between <= 0)
        { _journal?.Record(0, "replay.markings", "thresholds_overlap", "No positive usable distance remains between reconstructed thresholds.", new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0 }); return; }
        _journal?.Record(0, "replay.markings", "threshold_reconstructed", "Product convention: subtract the distance corresponding to 50 ft at 3 degrees from aiming-mark start.",
            new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0, ["aiming_from_end_m"] = approach.AimingFromEndMeters,
                ["threshold_from_end_m"] = approach.ThresholdFromEndMeters, ["reference_distance_m"] = ReferenceApproachDistance });
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
            if (clear <= (UsesFaa ? 48.768 : 50) || from < -half || to > tdzLimit)
            {
                _journal?.Record(0, "replay.markings", "tdz_row_omitted", clear <= (UsesFaa ? 48.768 : 50) ? "Touchdown-zone row overlaps aiming-mark exclusion clearance." : "Touchdown-zone row crosses runway or reciprocal-pattern limits.",
                    new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0, ["distance_from_threshold_m"] = slots[i] });
                continue;
            }
            rows.Add(i);
        }
        _journal?.Record(0, "replay.markings", "tdz_pattern", "Touchdown-zone rows selected using regional spacing, usable runway length and reciprocal clearance.",
            new Dictionary<string, double> { ["secondary"] = secondary ? 1 : 0, ["pattern_b"] = patternB ? 1 : 0,
                ["length_for_count_m"] = lengthForCount, ["candidate_rows"] = pairCount, ["retained_rows"] = rows.Count });
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
        var points = new[] { new Point2D(left, from), new Point2D(right, from), new Point2D(right, to), new Point2D(left, to) };
        if (secondary) points = points.Select(p => new Point2D(-p.X, -p.Y)).ToArray();
        Shapes.Add(new Paint { Kind = kind, Points = points, Secondary = secondary });
    }
}
