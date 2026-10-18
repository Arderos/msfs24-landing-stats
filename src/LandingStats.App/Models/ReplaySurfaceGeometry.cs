using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.ReplayMath;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Models;

/// <summary>
/// A neutral strip under the recorded track, with a separate runway footprint.
/// Facility data gives the runway plan, not its elevation profile: pavement and
/// markings share the ground elevations actually observed over that footprint.
/// No lateral slope is inferred and the recorded aircraft positions are untouched.
/// </summary>
internal sealed class ReplaySurfaceGeometry
{
    internal const double MarkingLiftMeters = .035;
    private const double HeightCellMeters = 20;
    private readonly Dictionary<(int X, int Z), List<int>> _heightCells = new Dictionary<(int, int), List<int>>();
    private readonly List<HeightTriangle> _heightTriangles = new List<HeightTriangle>();
    private readonly List<HeightTriangle> _largeHeightTriangles = new List<HeightTriangle>();
    private RunwayProfile? _profile;
    internal RunwayProfile? Profile => _profile;
    public ReplaySurfaceMesh Terrain { get; } = new ReplaySurfaceMesh();
    public ReplaySurfaceMesh Pavement { get; } = new ReplaySurfaceMesh();

    public static ReplaySurfaceGeometry Build(LandingReplayTrack track, RunwayFacility? runway, TelemetryDecisionJournal? journal = null)
    {
        var result = new ReplaySurfaceGeometry();
        var profile = runway != null && runway.HasValidGeometry && !runway.IsWater
            ? new RunwayProfile(track, runway, journal) : null;
        if (profile == null) journal?.Record(0, "replay.surface", "observed_ground_only", "No valid non-water runway geometry; render only the neutral observed-ground strip.");
        result._profile = profile;
        var sides = new Vector3D[track.Frames.Count];
        for (var i = 0; i < sides.Length; i++)
        {
            var frame = track.Frames[i];
            var previous = i > 0 && frame.TimeSeconds - track.Frames[i - 1].TimeSeconds <= LandingReplayTrack.MaximumInterpolationGapSeconds ? track.Frames[i - 1] : frame;
            var next = i + 1 < sides.Length && track.Frames[i + 1].TimeSeconds - frame.TimeSeconds <= LandingReplayTrack.MaximumInterpolationGapSeconds ? track.Frames[i + 1] : frame;
            var side = Vector3D.CrossProduct(next.Ground - previous.Ground, new Vector3D(0, 1, 0));
            if (side.LengthSquared < 1e-12)
            {
                var heading = frame.Sample.HeadingTrueDegrees!.Value * Math.PI / 180;
                side = new Vector3D(Math.Cos(heading), 0, Math.Sin(heading));
            }
            side.Normalize(); sides[i] = side * 100;
        }
        for (var i = 1; i < sides.Length; i++)
        {
            var a = track.Frames[i - 1]; var b = track.Frames[i];
            if (b.TimeSeconds - a.TimeSeconds > LandingReplayTrack.MaximumInterpolationGapSeconds) continue;
            var leftA = a.Ground - sides[i - 1]; var rightA = a.Ground + sides[i - 1];
            var leftB = b.Ground - sides[i]; var rightB = b.Ground + sides[i];
            AddTerrain(result.Terrain, new[] { leftA, rightA, rightB }, profile);
            AddTerrain(result.Terrain, new[] { leftA, rightB, leftB }, profile);
        }
        if (profile != null)
        {
            var stations = new[] { -profile.HalfLength, profile.HalfLength }.Concat(profile.Stations).Distinct().OrderBy(s => s).ToArray();
            for (var i = 1; i < stations.Length; i++)
                Strip(result.Pavement, profile, stations[i - 1], stations[i], profile.HalfWidth, 0);
        }
        result.IndexTerrainHeights();
        journal?.Record(0, "replay.surface", "surface_created", "Terrain is clipped around the runway footprint; paint shares the observed pavement profile with a small visual lift.",
            new Dictionary<string, double> { ["terrain_triangles"] = result.Terrain.TriangleIndices.Count / 3,
                ["pavement_triangles"] = result.Pavement.TriangleIndices.Count / 3, ["marking_lift_m"] = MarkingLiftMeters });
        return result;
    }

    /// <summary>The rendered pavement/terrain height, excluding decorative paint. Missing ground stays missing.</summary>
    internal double? HeightAt(double x, double z)
    {
        if (!LandingReplayTrack.Finite(x) || !LandingReplayTrack.Finite(z) ||
            Math.Abs(x / HeightCellMeters) >= int.MaxValue || Math.Abs(z / HeightCellMeters) >= int.MaxValue) return null;
        var pavement = _profile?.HeightAt(x, z);
        if (pavement.HasValue) return pavement;
        double? height = null;
        if (_heightCells.TryGetValue((Cell(x), Cell(z)), out var triangles))
            foreach (var triangle in triangles) height = Higher(height, _heightTriangles[triangle].HeightAt(x, z));
        foreach (var triangle in _largeHeightTriangles) height = Higher(height, triangle.HeightAt(x, z));
        return height;
    }

    private static double? Higher(double? a, double? b) => !a.HasValue ? b : !b.HasValue ? a : Math.Max(a.Value, b.Value);
    private static int Cell(double value) => (int)Math.Floor(value / HeightCellMeters);

    private void IndexTerrainHeights()
    {
        // Build once, then tyre queries only examine local faces rather than
        // scanning the full landing at every animation frame.
        for (var i = 0; i < Terrain.TriangleIndices.Count; i += 3)
        {
            var triangle = new HeightTriangle(Terrain.Positions[Terrain.TriangleIndices[i]],
                Terrain.Positions[Terrain.TriangleIndices[i + 1]], Terrain.Positions[Terrain.TriangleIndices[i + 2]]);
            if (!triangle.HasArea) continue;
            var minX = Cell(triangle.MinX); var maxX = Cell(triangle.MaxX);
            var minZ = Cell(triangle.MinZ); var maxZ = Cell(triangle.MaxZ);
            // A corrupt teleport must not allocate millions of grid cells.
            if (((long)maxX - minX + 1) * ((long)maxZ - minZ + 1) > 256)
            { _largeHeightTriangles.Add(triangle); continue; }
            var triangleIndex = _heightTriangles.Count;
            _heightTriangles.Add(triangle);
            for (var x = minX; x <= maxX; x++) for (var z = minZ; z <= maxZ; z++)
            {
                if (!_heightCells.TryGetValue((x, z), out var cell)) _heightCells.Add((x, z), cell = new List<int>());
                cell.Add(triangleIndex);
            }
        }
    }

    private readonly struct HeightTriangle
    {
        private readonly Point3D _a, _b, _c;
        private readonly double _determinant;
        public HeightTriangle(Point3D a, Point3D b, Point3D c)
        {
            _a = a; _b = b; _c = c;
            _determinant = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            MinX = Math.Min(a.X, Math.Min(b.X, c.X)); MaxX = Math.Max(a.X, Math.Max(b.X, c.X));
            MinZ = Math.Min(a.Z, Math.Min(b.Z, c.Z)); MaxZ = Math.Max(a.Z, Math.Max(b.Z, c.Z));
        }
        public double MinX { get; }
        public double MaxX { get; }
        public double MinZ { get; }
        public double MaxZ { get; }
        public bool HasArea => Math.Abs(_determinant) > 1e-12;
        public double? HeightAt(double x, double z)
        {
            if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return null;
            var u = ((_b.Z - _c.Z) * (x - _c.X) + (_c.X - _b.X) * (z - _c.Z)) / _determinant;
            var v = ((_c.Z - _a.Z) * (x - _c.X) + (_a.X - _c.X) * (z - _c.Z)) / _determinant;
            if (u < -1e-8 || v < -1e-8 || u + v > 1 + 1e-8) return null;
            return u * _a.Y + v * _b.Y + (1 - u - v) * _c.Y;
        }
    }

    private static void Strip(ReplaySurfaceMesh mesh, RunwayProfile p, double from, double to, double halfWidth, double lift)
    {
        var a = p.At(from, -halfWidth, lift); var b = p.At(from, halfWidth, lift);
        var c = p.At(to, halfWidth, lift); var d = p.At(to, -halfWidth, lift);
        Triangle(mesh, a, b, c); Triangle(mesh, a, c, d);
    }

    private static void AddTerrain(ReplaySurfaceMesh mesh, Point3D[] triangle, RunwayProfile? profile)
    {
        if (profile == null) { Triangle(mesh, triangle[0], triangle[1], triangle[2]); return; }
        // Partition each original ground triangle around the runway rectangle.
        // Unlike lowering one of two overlapping surfaces, this cannot produce
        // intersections when the observed terrain differs from facility altitude.
        var remaining = triangle.ToList();
        for (var plane = 0; plane < 4 && remaining.Count >= 3; plane++)
        {
            var outside = Clip(remaining, profile, plane, false);
            for (var i = 2; i < outside.Count; i++) Triangle(mesh, outside[0], outside[i - 1], outside[i]);
            remaining = Clip(remaining, profile, plane, true);
        }
    }

    private static List<Point3D> Clip(List<Point3D> polygon, RunwayProfile profile, int plane, bool keepInside)
    {
        var result = new List<Point3D>();
        var previous = polygon[polygon.Count - 1]; var previousDistance = profile.Distance(previous, plane);
        foreach (var current in polygon)
        {
            var distance = profile.Distance(current, plane);
            var previousKept = keepInside ? previousDistance >= 0 : previousDistance <= 0;
            var kept = keepInside ? distance >= 0 : distance <= 0;
            if (kept != previousKept)
                result.Add(previous + (current - previous) * (previousDistance / (previousDistance - distance)));
            if (kept) result.Add(current);
            previous = current; previousDistance = distance;
        }
        return result;
    }

    private static void Triangle(ReplaySurfaceMesh mesh, Point3D a, Point3D b, Point3D c)
    {
        if (Vector3D.CrossProduct(b - a, c - a).LengthSquared < 1e-16) return;
        var i = mesh.Positions.Count; mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
        mesh.TriangleIndices.Add(i); mesh.TriangleIndices.Add(i + 1); mesh.TriangleIndices.Add(i + 2);
    }

    internal sealed class RunwayProfile
    {
        private readonly Point3D _center;
        private readonly Vector3D _forward;
        private readonly Vector3D _right;
        private readonly (double Station, double Height)[] _samples;
        public double HalfLength { get; }
        public double HalfWidth { get; }
        public IEnumerable<double> Stations => _samples.Select(p => p.Station);

        public RunwayProfile(LandingReplayTrack track, RunwayFacility runway, TelemetryDecisionJournal? journal)
        {
            _center = track.ProjectGeo(runway.LatitudeDegrees, runway.LongitudeDegrees, runway.AltitudeMeters / LandingReplayTrack.FeetToMeters);
            var angle = runway.HeadingTrueDegrees * Math.PI / 180;
            _forward = new Vector3D(Math.Sin(angle), 0, -Math.Cos(angle));
            _right = new Vector3D(Math.Cos(angle), 0, Math.Sin(angle));
            HalfLength = runway.LengthMeters / 2; HalfWidth = runway.WidthMeters / 2;
            _samples = track.Frames.Where(f => Math.Abs(Vector3D.DotProduct(f.Ground - _center, _right)) <= HalfWidth)
                .Select(f => (Station: Vector3D.DotProduct(f.Ground - _center, _forward), Height: f.Ground.Y))
                .Where(p => Math.Abs(p.Station) <= HalfLength)
                // A stationary/revisited point is one spatial observation, not
                // a vertical face. Retain a robust height for coincident stations.
                .GroupBy(p => Math.Round(p.Station, 3))
                .Select(g => (Station: g.Average(p => p.Station), Height: g.Select(p => p.Height).OrderBy(h => h).ElementAt(g.Count() / 2)))
                .OrderBy(p => p.Station).ToArray();
            journal?.Record(0, "replay.surface", _samples.Length == 0 ? "facility_altitude_fallback" : "observed_runway_profile",
                _samples.Length == 0 ? "No ground samples lie inside the runway footprint; use facility altitude." : "Runway height follows observed ground stations; unobserved ends keep endpoint height, without lateral slope inference.",
                new Dictionary<string, double> { ["stations"] = _samples.Length, ["width_m"] = runway.WidthMeters, ["length_m"] = runway.LengthMeters });
        }

        public Point3D At(double station, double lateral, double lift)
        {
            var point = _center + _forward * station + _right * lateral;
            point.Y = Height(station) + lift; return point;
        }

        public double? HeightAt(double x, double z)
        {
            var delta = new Vector3D(x - _center.X, 0, z - _center.Z);
            var along = Vector3D.DotProduct(delta, _forward);
            var across = Vector3D.DotProduct(delta, _right);
            return Math.Abs(along) <= HalfLength && Math.Abs(across) <= HalfWidth ? Height(along) : (double?)null;
        }

        private double Height(double station)
        {
            if (_samples.Length == 0) return _center.Y;
            // Beyond observations use a constant endpoint, never extrapolate a
            // measured approach slope through the rest of an unobserved runway.
            if (station <= _samples[0].Station) return _samples[0].Height;
            if (station >= _samples[_samples.Length - 1].Station) return _samples[_samples.Length - 1].Height;
            var lo = 0; var hi = _samples.Length - 1;
            while (hi - lo > 1) { var mid = (lo + hi) / 2; if (_samples[mid].Station < station) lo = mid; else hi = mid; }
            var a = _samples[lo]; var b = _samples[hi];
            return a.Height + (b.Height - a.Height) * ((station - a.Station) / (b.Station - a.Station));
        }

        public double Distance(Point3D point, int plane)
        {
            var delta = point - _center;
            var along = Vector3D.DotProduct(delta, _forward); var across = Vector3D.DotProduct(delta, _right);
            return plane == 0 ? along + HalfLength : plane == 1 ? HalfLength - along : plane == 2 ? across + HalfWidth : HalfWidth - across;
        }
    }
}

internal sealed class ReplaySurfaceMesh
{
    internal List<Point3D> Positions { get; } = new List<Point3D>();
    internal List<int> TriangleIndices { get; } = new List<int>();
}
