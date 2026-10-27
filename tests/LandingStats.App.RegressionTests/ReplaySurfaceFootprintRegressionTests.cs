using System;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.ReplayMath;

namespace LandingStats.App.RegressionTests;

internal static class ReplaySurfaceFootprintRegressionTests
{
    public static void RunAll()
    {
        InteriorPeakIsIncluded();
        ClippedEdgesAndBoundaryContactsAreIncluded();
        RotatedSlopesUseTheWholeFootprint();
        TerrainAndPavementShareTheQuery();
        QueriesLeaveMeshesUnchanged();
    }

    private static void InteriorPeakIsIncluded()
    {
        var surface = new ReplaySurfaceGeometry();
        var peak = new Point3D(.31, 20, .23);
        var corners = new[] { new Point3D(-2, 0, -2), new Point3D(2, 0, -2),
            new Point3D(2, 0, 2), new Point3D(-2, 0, 2) };
        for (var i = 0; i < corners.Length; i++) Triangle(surface.Terrain, corners[i], corners[(i + 1) % corners.Length], peak);
        Near(surface.MaximumHeightOver(new Point3D(), new Vector3D(1, 0, 0), new Vector3D(0, 0, 1)), 20,
            "An interior mesh peak is included even when it misses the rectangle's center, corners and edge midpoints.");
        Near(surface.MaximumHeightOver(peak, new Vector3D(.0001, 0, 0), new Vector3D(0, 0, .0001)), 20,
            "A narrow footprint retains the exact interior peak without a sampling interval.");
    }

    private static void ClippedEdgesAndBoundaryContactsAreIncluded()
    {
        var surface = new ReplaySurfaceGeometry();
        // This thin triangle crosses the square without containing any of its
        // corners, and all three triangle vertices are outside the square.
        Triangle(surface.Terrain, new Point3D(-3, -5.5, -2.5), new Point3D(3, 6.5, 3.5), new Point3D(3, 6.7, 3.7));
        Require(surface.Terrain.Positions.All(p => Math.Abs(p.X) > 1 || Math.Abs(p.Z) > 1), "Crossing fixture has no interior vertices.");
        Near(surface.MaximumHeightOver(new Point3D(), new Vector3D(1, 0, 0), new Vector3D(0, 0, 1)), 1.5,
            "The maximum can lie at a newly clipped edge intersection when no input vertex or footprint corner is inside.");

        var enclosed = new ReplaySurfaceGeometry();
        Triangle(enclosed.Terrain, new Point3D(-10, -25, -10), new Point3D(10, -5, -10), new Point3D(0, 25, 10));
        Near(enclosed.MaximumHeightOver(new Point3D(), new Vector3D(1, 0, 0), new Vector3D(0, 0, 1)), 8,
            "A footprint wholly inside one sloping triangle is evaluated at its clipped corners.");

        var edge = new ReplaySurfaceGeometry();
        Triangle(edge.Terrain, new Point3D(1, 3, -.5), new Point3D(1, 7, .5), new Point3D(2, 100, 0));
        Near(edge.MaximumHeightOver(new Point3D(), new Vector3D(1, 0, 0), new Vector3D(0, 0, 1)), 7,
            "A shared boundary edge contributes its height but the higher vertex outside the footprint does not.");
        var corner = new ReplaySurfaceGeometry();
        Triangle(corner.Terrain, new Point3D(1, 9, 1), new Point3D(2, 10, 1), new Point3D(1, 11, 2));
        Near(corner.MaximumHeightOver(new Point3D(), new Vector3D(1, 0, 0), new Vector3D(0, 0, 1)), 9,
            "A single shared corner remains a valid rendered-surface intersection.");
    }

    private static void RotatedSlopesUseTheWholeFootprint()
    {
        var surface = new ReplaySurfaceGeometry();
        Plane(surface.Terrain, -100, 100, (x, z) => 3 * x - 2 * z + 7);
        var center = new Point3D(5, 500, -3);
        foreach (var degrees in new[] { 0d, 37, 90, 137, 270 })
        {
            var angle = degrees * Math.PI / 180;
            var right = new Vector3D(Math.Cos(angle) * 2, 0, Math.Sin(angle) * 2);
            var forward = new Vector3D(-Math.Sin(angle) * 3, 0, Math.Cos(angle) * 3);
            var expected = 3 * center.X - 2 * center.Z + 7 + Math.Abs(3 * right.X - 2 * right.Z) + Math.Abs(3 * forward.X - 2 * forward.Z);
            Near(surface.MaximumHeightOver(center, right, forward), expected,
                "A rotated rectangle uses its actual oriented extent on a slope.");
            Near(surface.MaximumHeightOver(new Point3D(center.X, -500, center.Z), -right, -forward), expected,
                "Center elevation and axis direction do not change the horizontal footprint.");
        }
    }

    private static void TerrainAndPavementShareTheQuery()
    {
        var surface = new ReplaySurfaceGeometry();
        var right = new Vector3D(1, 0, 0); var forward = new Vector3D(0, 0, 1);
        Require(!surface.MaximumHeightOver(new Point3D(), right, forward).HasValue, "An empty surface has no height.");
        Plane(surface.Terrain, -5, 5, (_, __) => -4);
        Near(surface.MaximumHeightOver(new Point3D(), right, forward), -4, "Negative rendered elevation is not replaced with zero.");
        Triangle(surface.Pavement, new Point3D(-1, 12, -1), new Point3D(1, 12, -1), new Point3D(0, 12, 1));
        Triangle(surface.Pavement, new Point3D(6, 23, -1), new Point3D(10, 23, -1), new Point3D(8, 23, 2));
        Triangle(surface.Terrain, new Point3D(20, 1000, 20), new Point3D(22, 1000, 20), new Point3D(20, 1000, 22));
        Near(surface.MaximumHeightOver(new Point3D(), right, forward), 12, "Pavement participates alongside terrain and distant peaks are excluded.");
        Near(surface.MaximumHeightOver(new Point3D(8, 0, 0), right * .25, forward * .25), 23,
            "Rendered pavement is found where terrain is absent, without requiring a runway profile lookup.");
        Require(!surface.MaximumHeightOver(new Point3D(50, 0, 50), right, forward).HasValue,
            "A footprint outside both rendered meshes remains unavailable.");
        Require(!surface.MaximumHeightOver(new Point3D(double.NaN, 0, 0), right, forward).HasValue &&
                !surface.MaximumHeightOver(new Point3D(), new Vector3D(), forward).HasValue &&
                !surface.MaximumHeightOver(new Point3D(), right, new Vector3D(0, 0, double.PositiveInfinity)).HasValue,
            "Invalid or empty footprint axes remain unavailable.");
    }

    private static void QueriesLeaveMeshesUnchanged()
    {
        var surface = new ReplaySurfaceGeometry();
        Plane(surface.Terrain, -10, 10, (x, z) => x - z);
        Plane(surface.Pavement, -2, 2, (x, z) => x + z + 3);
        var terrainPositions = surface.Terrain.Positions.ToArray(); var terrainIndices = surface.Terrain.TriangleIndices.ToArray();
        var pavementPositions = surface.Pavement.Positions.ToArray(); var pavementIndices = surface.Pavement.TriangleIndices.ToArray();
        surface.MaximumHeightOver(new Point3D(), new Vector3D(2, 0, 1), new Vector3D(-1, 0, 2));
        surface.MaximumHeightOver(new Point3D(9, 0, 9), new Vector3D(3, 0, 0), new Vector3D(0, 0, 4));
        Require(surface.Terrain.Positions.SequenceEqual(terrainPositions) && surface.Terrain.TriangleIndices.SequenceEqual(terrainIndices) &&
                surface.Pavement.Positions.SequenceEqual(pavementPositions) && surface.Pavement.TriangleIndices.SequenceEqual(pavementIndices),
            "Clipping uses local polygons and preserves the rendered meshes and triangle topology.");
    }

    private static void Plane(ReplaySurfaceMesh mesh, double from, double to, Func<double, double, double> height)
    {
        var a = new Point3D(from, height(from, from), from); var b = new Point3D(to, height(to, from), from);
        var c = new Point3D(to, height(to, to), to); var d = new Point3D(from, height(from, to), to);
        Triangle(mesh, a, b, c); Triangle(mesh, a, c, d);
    }

    private static void Triangle(ReplaySurfaceMesh mesh, Point3D a, Point3D b, Point3D c)
    {
        var index = mesh.Positions.Count;
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
        mesh.TriangleIndices.Add(index); mesh.TriangleIndices.Add(index + 1); mesh.TriangleIndices.Add(index + 2);
    }

    private static void Near(double? actual, double expected, string message) =>
        Require(actual.HasValue && Math.Abs(actual.Value - expected) < 1e-9, message + $" Expected {expected}, got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
