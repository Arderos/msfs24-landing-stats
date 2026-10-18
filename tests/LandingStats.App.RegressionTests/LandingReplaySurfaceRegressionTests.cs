using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class LandingReplaySurfaceRegressionTests
{
    public static void RunAll()
    {
        var glyphTransparencyChecked = false;
        foreach (var airport in new[] { "TEST", "KJFK" })
        foreach (var heading in new[] { 0d, 75.1, 240d })
        foreach (var width in new[] { 18d, 30, 49.84298, 60.41475 })
        {
            var runway = Runway(heading);
            runway.AirportIdent = airport;
            runway.WidthMeters = width;
            runway.PrimaryName = "09L"; runway.SecondaryName = "27R";
            runway.PrimaryThresholdOffsetMeters = 108; runway.SecondaryThresholdOffsetMeters = 25;
            var record = Record(heading, new[] { -450d, -400, -374, -355, -330, -270, -130, -10, 140, 310, 335, 350, 375, 450 }, 8);
            Require(LandingReplayTrack.TryCreate(record, out var track), "measured slope fixture");
            var originalPositions = track!.Frames.Select(f => f.Position).ToArray();
            var surface = LandingReplaySurface.Build(track, runway);
            var center = track.ProjectGeo(runway.LatitudeDegrees, runway.LongitudeDegrees, runway.AltitudeMeters / LandingReplayTrack.FeetToMeters).ToWpf();
            var forward = Forward(heading); var right = Right(heading);
            Near(surface.Pavement.Positions.Max(p => Vector3D.DotProduct(p - center, right)) -
                surface.Pavement.Positions.Min(p => Vector3D.DotProduct(p - center, right)), width, .000001,
                "rendered lateral width exactly matches native metres, not a standard-size approximation");
            Require(surface.Pavement.Positions.Count > 6, "sloping runway is tessellated, not one facility-altitude plane");
            Near(PlanArea(surface.Pavement), runway.LengthMeters * runway.WidthMeters, .0001, "no holes in runway footprint");
            QueriesMatchRenderedFaces(surface);
            var outside = center + right * 1100;
            Require(!surface.HeightAt(outside.X, outside.Z).HasValue, "height query must not invent a plane outside recorded terrain/runway");
            Require(!surface.HeightAt(double.NaN, 0).HasValue && !surface.HeightAt(0, double.PositiveInfinity).HasValue,
                "invalid query coordinates stay unavailable");
            foreach (var f in track.Frames)
            {
                var distance = Vector3D.DotProduct(f.Ground.ToWpf() - center, forward);
                var cross = center + forward * distance;
                Require(TryHeight(surface.Pavement, cross, out var height), "measured station exists on pavement");
                Near(height, f.Ground.Y, .000001, "pavement uses measured ground, not conflicting facility altitude");
            }
            for (var i = 0; i < surface.Terrain.Positions.Count; i += 3)
            {
                var polygon = new List<Point3D>();
                for (var k = 0; k < 3; k++)
                {
                    var delta = surface.Terrain.Positions[i + k] - center;
                    polygon.Add(new Point3D(Vector3D.DotProduct(delta, right), 0, Vector3D.DotProduct(delta, forward)));
                }
                polygon = ClipRectangle(polygon, runway.WidthMeters / 2, runway.LengthMeters / 2);
                Near(Area(polygon), 0, .000001, "ground never overlaps runway, even where original elevations cross");
            }
            Require(surface.Markings.Positions.Count > 0, "approach-derived runway paint exists");
            Require(RunwayMarkingLayout.Build(runway).Shapes.Any(p => p.Kind == "Aiming"), "surface fixture exercises landing paint, not only centerline");
            var labels = RunwayMarkingLayout.Build(runway).Shapes.Where(p => !string.IsNullOrEmpty(p.Text)).ToArray();
            Require(labels.Length == 4 && surface.Designations.Count == 4, "both runway numbers and L/R suffixes are rendered");
            Require(surface.Designations.Any(d => ((MeshGeometry3D)d.Geometry).Positions.Count > 6), "glyph fixture crosses a pavement profile breakpoint");
            for (var i = 0; i < labels.Length; i++)
            {
                var mesh = (MeshGeometry3D)surface.Designations[i].Geometry;
                Require(mesh.Positions.Count == mesh.TextureCoordinates.Count, "each glyph vertex has a texture coordinate");
                var left = labels[i].Points.Min(p => p.X); var rightEdge = labels[i].Points.Max(p => p.X);
                var start = labels[i].Points.Min(p => p.Y); var end = labels[i].Points.Max(p => p.Y);
                for (var j = 0; j < mesh.Positions.Count; j++)
                {
                    var delta = mesh.Positions[j] - center;
                    var u = (Vector3D.DotProduct(delta, right) - left) / (rightEdge - left);
                    var v = (end - Vector3D.DotProduct(delta, forward)) / (end - start);
                    Near(mesh.TextureCoordinates[j].X, labels[i].Secondary ? 1 - u : u, .000001, "glyph reads left-to-right from its approach");
                    Near(mesh.TextureCoordinates[j].Y, labels[i].Secondary ? 1 - v : v, .000001, "reciprocal glyph top faces into runway");
                }
            }
            if (!glyphTransparencyChecked)
            {
                GlyphHasTransparentBackground(surface.Designations[0]);
                glyphTransparencyChecked = true;
            }
            foreach (var mesh in new[] { surface.Markings }.Concat(surface.Designations.Select(d => (MeshGeometry3D)d.Geometry)))
            for (var i = 0; i < mesh.Positions.Count; i += 3)
            {
                var a = mesh.Positions[i]; var b = mesh.Positions[i + 1]; var c = mesh.Positions[i + 2];
                foreach (var p in new[] { a, b, c, new Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3) })
                {
                    Require(TryHeight(surface.Pavement, p, out var pavementHeight), "dash lies over pavement");
                    Near(p.Y - pavementHeight, LandingReplaySurface.MarkingLiftMeters, .000001, "dash follows identical profile at every bend");
                }
            }
            for (var i = 0; i < originalPositions.Length; i++) Require(track.Frames[i].Position == originalPositions[i], "terrain display does not shift aircraft trajectory");

            runway.PrimaryPapi = null; runway.SecondaryPapi = null;
            var noGuidanceLayout = RunwayMarkingLayout.Build(runway);
            Require(noGuidanceLayout.Shapes.Any(p => p.Kind == "Centerline") && noGuidanceLayout.Shapes.Where(p => p.Text == null).All(p => p.Kind == "Centerline"),
                "without approach guidance solid paint is centerline only");
            var noGuidanceSurface = LandingReplaySurface.Build(track, runway);
            Require(noGuidanceSurface.Markings.Positions.Count > 0 && noGuidanceSurface.Markings.Positions.Count < surface.Markings.Positions.Count,
                "centerline-only surface does not retain previous landing paint");
            Require(noGuidanceSurface.Designations.Count == 4, "known runway numbers remain visible without ILS/PAPI");
        }

        var fallbackRecord = Record(0, new[] { -100d, 0, 100 }, 150);
        Require(LandingReplayTrack.TryCreate(fallbackRecord, out var fallback), "off-runway fixture");
        var fallbackRunway = Runway(0); var fallbackSurface = LandingReplaySurface.Build(fallback!, fallbackRunway);
        var facilityHeight = fallback!.ProjectGeo(0, 0, fallbackRunway.AltitudeMeters / LandingReplayTrack.FeetToMeters).Y;
        Require(fallbackSurface.Pavement.Positions.All(p => Math.Abs(p.Y - facilityHeight) < .000001), "off-runway terrain must not define runway slope");
        QueriesMatchRenderedFaces(fallbackSurface);

        var gapRecord = Record(0, new[] { -350d, -300, 300, 350 }, 0);
        gapRecord.Series[2].TimeSeconds = 2; gapRecord.Series[3].TimeSeconds = 2.1;
        Require(LandingReplayTrack.TryCreate(gapRecord, out var gap), "ground gap fixture");
        var gapSurface = LandingReplaySurface.Build(gap!, null);
        QueriesMatchRenderedFaces(gapSurface);
        Require(gapSurface.Terrain.Positions.Count == 12, "no ground strip spans missing telemetry");
        for (var i = 0; i < gapSurface.Terrain.Positions.Count; i += 3)
            Require(gapSurface.Terrain.Positions.Skip(i).Take(3).Max(p => p.Z) - gapSurface.Terrain.Positions.Skip(i).Take(3).Min(p => p.Z) < 55, "gap is not triangulated");
        var missingGround = gap!.Frames[1].Ground + (gap.Frames[2].Ground - gap.Frames[1].Ground) * .5;
        Require(!gapSurface.HeightAt(missingGround.X, missingGround.Z).HasValue, "height query preserves unrendered telemetry gap");
        var water = Runway(0); water.SurfaceType = 2;
        var waterSurface = LandingReplaySurface.Build(gap!, water);
        Require(waterSurface.Pavement.Positions.Count == 0 && waterSurface.Markings.Positions.Count == 0 && waterSurface.Designations.Count == 0, "water contacts never invent a paved runway");
        Require(waterSurface.Terrain.Positions.Count == gapSurface.Terrain.Positions.Count, "water has same measured neutral ground strip");
        QueriesMatchRenderedFaces(waterSurface);
        Require(!waterSurface.HeightAt(missingGround.X, missingGround.Z).HasValue, "water does not fill missing ground with a synthetic runway");

        var stationary = Record(0, new[] { 0d, 0, 0 }, 0);
        Require(LandingReplayTrack.TryCreate(stationary, out var still), "stationary fixture");
        var stillSurface = LandingReplaySurface.Build(still!, Runway(0));
        Require(stillSurface.Pavement.Positions.All(p => !double.IsNaN(p.Y) && !double.IsInfinity(p.Y)), "coincident observations cannot divide by zero");
        QueriesMatchRenderedFaces(stillSurface);
        var stillNoRunway = LandingReplaySurface.Build(still!, null);
        Require(!stillNoRunway.HeightAt(0, 0).HasValue, "a point observation alone does not invent a ground surface");

        var longRecord = Record(0, new[] { -20000d, 20000 }, 0);
        Require(LandingReplayTrack.TryCreate(longRecord, out var longTrack), "long triangle fixture");
        QueriesMatchRenderedFaces(LandingReplaySurface.Build(longTrack!, null));
    }

    private static void GlyphHasTransparentBackground(GeometryModel3D model)
    {
        var material = model.Material as MaterialGroup;
        Require(material != null && material.Children.Count == 2, "glyph material has alpha-masked diffuse and emissive passes");
        foreach (var child in material!.Children)
        {
            var brush = (child is DiffuseMaterial diffuse ? diffuse.Brush : ((EmissiveMaterial)child).Brush) as DrawingBrush;
            Require(brush?.Drawing is GeometryDrawing, "glyph texture has no opaque background drawing");
            var drawing = (GeometryDrawing)brush!.Drawing;
            Require(!(drawing.Geometry is RectangleGeometry), "textured designation is not a solid marking rectangle");
            if (child is EmissiveMaterial)
                Require(((SolidColorBrush)drawing.Brush).Color == Color.FromRgb(194, 191, 181), "designation uses the runway marking color");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(0, 0, 128, 128));
            var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[128 * 128 * 4]; bitmap.CopyPixels(pixels, 128 * 4, 0);
            var visible = 0; var transparent = 0;
            for (var i = 3; i < pixels.Length; i += 4)
            { if (pixels[i] > 240) visible++; else if (pixels[i] < 10) transparent++; }
            Require(visible > 1000 && transparent > 1000, "actual glyph raster keeps both painted strokes and transparent surroundings");
        }
    }

    private static void QueriesMatchRenderedFaces(LandingReplaySurface surface)
    {
        foreach (var mesh in new[] { surface.Pavement, surface.Terrain })
            for (var i = 0; i < mesh.TriangleIndices.Count; i += 3)
            {
                var a = mesh.Positions[mesh.TriangleIndices[i]];
                var b = mesh.Positions[mesh.TriangleIndices[i + 1]];
                var c = mesh.Positions[mesh.TriangleIndices[i + 2]];
                if (Area(new[] { a, b, c }) < 1e-9) continue;
                var center = new Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                foreach (var p in new[] { center, a + (center - a) * .02, b + (center - b) * .02, c + (center - c) * .02 })
                {
                    var height = surface.HeightAt(p.X, p.Z);
                    Require(height.HasValue, "rendered ground triangle must have a queryable height");
                    Near(height!.Value, p.Y, .000001, "height queries use exactly the rendered sloping/rotated triangle surface");
                }
            }
    }

    private static RunwayFacility Runway(double heading) => new RunwayFacility
    {
        AirportIdent = "TEST", LatitudeDegrees = 0, LongitudeDegrees = 0, AltitudeMeters = 130,
        HeadingTrueDegrees = heading, LengthMeters = 1000, WidthMeters = 60,
        PrimaryPapiQueried = true, SecondaryPapiQueried = true,
        PrimaryPapi = new RunwayPapi { StationFromEndMeters = 350, AngleDegrees = 3 },
        SecondaryPapi = new RunwayPapi { StationFromEndMeters = 370, AngleDegrees = 3 },
    };

    private static LandingRecord Record(double heading, double[] stations, double cross)
    {
        var result = new LandingRecord(); var f = Forward(heading); var r = Right(heading);
        for (var i = 0; i < stations.Length; i++)
        {
            var delta = f * stations[i] + r * cross;
            // Small local flight close to equator. Ground profile deliberately
            // crosses the facility altitude: reproduces large missing patches,
            // not only co-planar z-fighting or a constant vertical offset.
            result.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = i * .1, LatitudeDegrees = -delta.Z / 110574, LongitudeDegrees = delta.X / 111319.49,
                GroundAltitudeFeet = (130 + stations[i] * .025 + Math.Sin(stations[i] / 80)) / LandingReplayTrack.FeetToMeters,
                PlaneAltitudeFeet = 200 / LandingReplayTrack.FeetToMeters,
                HeadingTrueDegrees = heading, PitchDegrees = 0, BankDegrees = 0,
            });
        }
        return result;
    }

    private static Vector3D Forward(double heading) => new Vector3D(Math.Sin(heading * Math.PI / 180), 0, -Math.Cos(heading * Math.PI / 180));
    private static Vector3D Right(double heading) => new Vector3D(Math.Cos(heading * Math.PI / 180), 0, Math.Sin(heading * Math.PI / 180));
    private static double PlanArea(MeshGeometry3D mesh)
    {
        double sum = 0; for (var i = 0; i < mesh.Positions.Count; i += 3) sum += Area(new[] { mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2] }); return sum;
    }
    private static double Area(IReadOnlyList<Point3D> polygon)
    {
        double sum = 0; for (var i = 0; i < polygon.Count; i++) { var next = polygon[(i + 1) % polygon.Count]; sum += polygon[i].X * next.Z - next.X * polygon[i].Z; } return Math.Abs(sum) / 2;
    }
    private static List<Point3D> ClipRectangle(List<Point3D> polygon, double halfWidth, double halfLength)
    {
        foreach (var distance in new Func<Point3D, double>[] { p => p.X + halfWidth, p => halfWidth - p.X, p => p.Z + halfLength, p => halfLength - p.Z })
        {
            var next = new List<Point3D>();
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count]; var da = distance(a); var db = distance(b);
                if (da >= 0) next.Add(a);
                if ((da >= 0) != (db >= 0)) next.Add(a + (b - a) * (da / (da - db)));
            }
            polygon = next;
        }
        return polygon;
    }
    private static bool TryHeight(MeshGeometry3D mesh, Point3D p, out double height)
    {
        for (var i = 0; i < mesh.Positions.Count; i += 3)
        {
            var a = mesh.Positions[i]; var b = mesh.Positions[i + 1]; var c = mesh.Positions[i + 2];
            var determinant = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (Math.Abs(determinant) < 1e-12) continue;
            var u = ((b.Z - c.Z) * (p.X - c.X) + (c.X - b.X) * (p.Z - c.Z)) / determinant;
            var v = ((c.Z - a.Z) * (p.X - c.X) + (a.X - c.X) * (p.Z - c.Z)) / determinant;
            if (u >= -1e-8 && v >= -1e-8 && u + v <= 1 + 1e-8) { height = u * a.Y + v * b.Y + (1 - u - v) * c.Y; return true; }
        }
        height = double.NaN; return false;
    }
    private static void Near(double actual, double expected, double tolerance, string message) => Require(Math.Abs(actual - expected) <= tolerance, message + ": " + actual + " != " + expected);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
