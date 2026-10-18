using System;
using System.Collections.Generic;
using LandingStats.App.ReplayMath;

namespace LandingStats.App.Models;

internal sealed class ReplayGearState
{
    internal const double RestHeight = 3.2;
    internal IReadOnlyList<Leg> Legs { get; } = Array.AsReadOnly(new[] { new Leg(-1), new Leg(1), new Leg(0) });
    internal sealed class Leg
    {
        internal Leg(int side)
        {
            var nose = side == 0;
            var hub = nose ? new Point3D(0, -2.82, -14.3) : new Point3D(side * 2.75, -2.66, 2.0);
            var points = new List<Point3D>();
            foreach (var direction in new[] { -1, 1 })
                points.AddRange(ReplayTyreGeometry.Points(hub + new Vector3D(direction * (nose ? .23 : .40), 0, 0), nose ? .38 : .54, nose ? .18 : .28));
            TyrePoints = points.AsReadOnly();
        }
        internal IReadOnlyList<Point3D> TyrePoints { get; }
        internal double Extension { get; private set; }
        internal void SetExtension(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Extension = value;
        }
    }
}
internal static class ReplayTyreGeometry
{
    internal const int Sides = 24;
    internal static IEnumerable<Point3D> Points(Point3D center, double radius, double width)
    {
        var profile = new[] { (-width * .5, radius * .46), (-width * .57, radius * .78),
            (-width * .35, radius), (width * .35, radius), (width * .57, radius * .78), (width * .5, radius * .46) };
        foreach (var ring in profile)
            for (var i = 0; i < Sides; i++)
            {
                var angle = i * 2 * Math.PI / Sides;
                yield return center + new Vector3D(ring.Item1, ring.Item2 * Math.Sin(angle), ring.Item2 * Math.Cos(angle));
            }
    }
}
