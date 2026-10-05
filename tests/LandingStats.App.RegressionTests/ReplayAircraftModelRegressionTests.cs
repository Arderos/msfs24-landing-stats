using System;
using System.Linq;
using System.Windows.Media.Media3D;
using LandingStats.App.Controls;

namespace LandingStats.App.RegressionTests;

internal static class ReplayAircraftModelRegressionTests
{
    public static void RunAll()
    {
        var model = ReplayAircraftModel.Create();
        Require(ReferenceEquals(model, ReplayAircraftModel.Create()), "replay aircraft geometry must be shared between views");
        Require(model.IsFrozen && model.Children.Count == 8, "aircraft model and material batches are immutable");
        var batches = model.Children.Cast<GeometryModel3D>().ToArray();
        var triangleCount = 0;
        foreach (var batch in batches)
        {
            var mesh = (MeshGeometry3D)batch.Geometry;
            Require(batch.IsFrozen && mesh.IsFrozen && batch.Material.IsFrozen, "mesh and materials must be frozen");
            Require(mesh.Positions.Count > 0 && mesh.Normals.Count == mesh.Positions.Count, "every model vertex has an explicit normal");
            Require(mesh.TriangleIndices.Count % 3 == 0, "triangle indices are complete");
            triangleCount += mesh.TriangleIndices.Count / 3;
            for (var i = 0; i < mesh.Positions.Count; i++)
            {
                var p = mesh.Positions[i]; var n = mesh.Normals[i];
                Require(Finite(p.X) && Finite(p.Y) && Finite(p.Z), "model positions must be finite");
                Require(Finite(n.X) && Finite(n.Y) && Finite(n.Z) && Math.Abs(n.Length - 1) < 1e-8, "model normals must be finite unit vectors");
            }
            for (var i = 0; i < mesh.TriangleIndices.Count; i += 3)
            {
                var a = mesh.TriangleIndices[i]; var b = mesh.TriangleIndices[i + 1]; var c = mesh.TriangleIndices[i + 2];
                Require(a >= 0 && b >= 0 && c >= 0 && a < mesh.Positions.Count && b < mesh.Positions.Count && c < mesh.Positions.Count, "triangle indices stay in the vertex buffer");
                Require(Vector3D.CrossProduct(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]).LengthSquared > 1e-18,
                    "model triangles must not collapse to points or lines");
            }
        }
        Require(triangleCount >= 8000 && triangleCount < 40000, "aircraft detail stays within a modest fixed triangle budget");
        var allPositions = batches.SelectMany(value => ((MeshGeometry3D)value.Geometry).Positions).ToArray();
        Require(Math.Abs(allPositions.Min(value => value.Y) + ReplayAircraftModel.RestHeight) < 1e-9, "rest height places tyre bottoms on the ground");
        Require(allPositions.Max(value => value.X) > 19 && allPositions.Min(value => value.X) < -19, "generic aircraft retains a full symmetric wingspan");
        Require(allPositions.Min(value => value.Z) <= -22 && allPositions.Max(value => value.Z) >= 21 && allPositions.Max(value => value.Y) >= 7.9,
            "airliner silhouette includes nose, tail and vertical stabilizer");

        // Material batches: body, wings/fins, nacelles, metal, glass, tyres, engine interior, door outlines.
        CheckWingNormals((MeshGeometry3D)batches[1].Geometry);
        CheckTyreNormals((MeshGeometry3D)batches[5].Geometry);
        CheckSkinNormals((MeshGeometry3D)batches[4].Geometry);
        CheckSkinNormals((MeshGeometry3D)batches[7].Geometry);
    }

    private static void CheckWingNormals(MeshGeometry3D mesh)
    {
        var upperCount = 0; var finCount = 0;
        for (var i = 0; i < mesh.Positions.Count; i++)
        {
            var p = mesh.Positions[i]; var n = mesh.Normals[i];
            if (Math.Abs(Math.Abs(p.X) - 10) < 1e-9 && p.Y > .225 && p.Z > .5 && p.Z < 3.5)
            {
                upperCount++;
                Require(n.Y > .5, "both main wings must have outward/upward upper-surface normals");
            }
            // Mid-height vertical-fin section, away from leading/trailing edges and the end caps.
            if (Math.Abs(p.Y - 3.6) < 1e-9 && Math.Abs(p.X) > .04 && p.Z > 15 && p.Z < 17.5)
            {
                finCount++;
                Require(n.X * p.X > 0, "vertical fin normals point away from its centre plane");
            }
        }
        Require(upperCount >= 8 && finCount >= 8, "normal tests exercise both wings and both sides of the tail fin");
    }

    private static void CheckTyreNormals(MeshGeometry3D mesh)
    {
        var checkedCount = 0;
        for (var i = 0; i < mesh.Positions.Count; i++)
        {
            var p = mesh.Positions[i];
            if (p.Y < -3.17)
            {
                checkedCount++;
                Require(mesh.Normals[i].Y < -.5, "tyre tread normals at the ground face down, not into the wheel");
            }
        }
        Require(checkedCount >= 12, "tyre normal test covers nose and main wheels");
    }

    private static void CheckSkinNormals(MeshGeometry3D mesh)
    {
        for (var i = 0; i < mesh.Positions.Count; i++)
        {
            var p = mesh.Positions[i]; var n = mesh.Normals[i];
            Require(n.X * p.X + n.Y * p.Y > 0, "windows and door details face outward on both fuselage sides");
        }
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
