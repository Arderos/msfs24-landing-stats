using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LandingStats.App.Models;
using LandingStats.Core;
using MathPoint = LandingStats.App.ReplayMath.Point3D;

namespace LandingStats.App.Controls;

/// <summary>WPF adapter: numerical terrain and height queries are shared with worker diagnostics.</summary>
internal sealed class LandingReplaySurface
{
    internal const double MarkingLiftMeters = ReplaySurfaceGeometry.MarkingLiftMeters;
    internal ReplaySurfaceGeometry Geometry { get; }
    public MeshGeometry3D Terrain { get; }
    public MeshGeometry3D Pavement { get; }
    public MeshGeometry3D Markings { get; } = new MeshGeometry3D();
    public List<GeometryModel3D> Designations { get; } = new List<GeometryModel3D>();
    private LandingReplaySurface(ReplaySurfaceGeometry geometry)
    { Geometry = geometry; Terrain = geometry.Terrain.ToWpf(); Pavement = geometry.Pavement.ToWpf(); }
    internal double? HeightAt(double x, double z) => Geometry.HeightAt(x, z);
    public static LandingReplaySurface Build(LandingReplayTrack track, RunwayFacility? runway, TelemetryDecisionJournal? journal = null)
    {
        var result = new LandingReplaySurface(ReplaySurfaceGeometry.Build(track, runway, journal));
        var profile = result.Geometry.Profile;
        if (profile != null)
        {
            foreach (var paint in RunwayMarkingLayout.Build(runway!, journal).Shapes)
            {
                var start = paint.Points.Min(p => p.Y); var end = paint.Points.Max(p => p.Y);
                var left = paint.Points.Min(p => p.X); var right = paint.Points.Max(p => p.X);
                var designation = paint.Glyph != null;
                var mesh = designation ? new MeshGeometry3D() : result.Markings;
                // Paint uses the pavement's breakpoints. Flat quads would
                // slice through a curved runway.
                var cuts = new[] { start, end }.Concat(profile.Stations.Where(s => s > start && s < end)).OrderBy(s => s).ToArray();
                for (var i = 1; i < cuts.Length; i++)
                {
                    var polygon = ClipStation(ClipStation(paint.Points.ToList(), cuts[i - 1], true), cuts[i], false);
                    for (var j = 2; j < polygon.Count; j++)
                    {
                        var vertices = new[] { polygon[0], polygon[j - 1], polygon[j] };
                        var previousCount = mesh.Positions.Count;
                        Triangle(mesh, profile.At(vertices[0].Y, vertices[0].X, MarkingLiftMeters),
                            profile.At(vertices[1].Y, vertices[1].X, MarkingLiftMeters), profile.At(vertices[2].Y, vertices[2].X, MarkingLiftMeters));
                        if (designation && mesh.Positions.Count > previousCount)
                            foreach (var vertex in vertices)
                            {
                                var u = (vertex.X - left) / (right - left);
                                var v = (end - vertex.Y) / (end - start);
                                mesh.TextureCoordinates.Add(paint.Secondary ? new Point(1 - u, 1 - v) : new Point(u, v));
                            }
                    }
                }
                if (designation && mesh.Positions.Count > 0)
                    result.Designations.Add(Designation(mesh, paint.Glyph!));
            }
        }
        return result;
    }

    private static GeometryModel3D Designation(MeshGeometry3D mesh, Geometry glyph)
    {
        DrawingBrush GlyphBrush(Brush fill)
        {
            var brush = new DrawingBrush(new GeometryDrawing(fill, null, glyph))
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = glyph.Bounds,
                Stretch = Stretch.Fill, TileMode = TileMode.None,
            };
            RenderOptions.SetCachingHint(brush, CachingHint.Cache);
            return brush;
        }
        // Both passes use the glyph's alpha. An opaque diffuse base would
        // leave a solid rectangle around the otherwise transparent letters.
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(GlyphBrush(Brushes.Black)));
        material.Children.Add(new EmissiveMaterial(GlyphBrush(new SolidColorBrush(Color.FromRgb(194, 191, 181)))));
        material.Freeze(); mesh.Freeze();
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Freeze(); return model;
    }

    private static List<Point> ClipStation(List<Point> polygon, double station, bool after)
    {
        var output = new List<Point>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            var da = after ? a.Y - station : station - a.Y;
            var db = after ? b.Y - station : station - b.Y;
            if (da >= 0) output.Add(a);
            if ((da >= 0) != (db >= 0)) output.Add(a + (b - a) * (da / (da - db)));
        }
        return output;
    }

    private static void Triangle(MeshGeometry3D mesh, MathPoint a, MathPoint b, MathPoint c)
    {
        if (LandingStats.App.ReplayMath.Vector3D.CrossProduct(b - a, c - a).LengthSquared < 1e-16) return;
        var i = mesh.Positions.Count; mesh.Positions.Add(a.ToWpf()); mesh.Positions.Add(b.ToWpf()); mesh.Positions.Add(c.ToWpf());
        mesh.TriangleIndices.Add(i); mesh.TriangleIndices.Add(i + 1); mesh.TriangleIndices.Add(i + 2);
    }

}
