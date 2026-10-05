using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LandingStats.App.Controls;

/// <summary>A generic airliner, in metres, nose along -Z. No aircraft-specific geometry is implied.</summary>
internal static class ReplayAircraftModel
{
    internal const double RestHeight = 3.2;
    // Immutable shared geometry: scrubbing only changes the containing pose transform.
    private static readonly Model3DGroup Shared = Build(true);
    private static readonly Model3DGroup SharedAirframe = Build(false);
    internal static Model3DGroup Create() => Shared;
    internal static Model3DGroup CreateAirframe() => SharedAirframe;

    private static Model3DGroup Build(bool includeGear)
    {
        var body = new MeshGeometry3D(); var wings = new MeshGeometry3D();
        var nacelles = new MeshGeometry3D(); var metal = new MeshGeometry3D();
        var glass = new MeshGeometry3D(); var rubber = new MeshGeometry3D();
        var interior = new MeshGeometry3D(); var seams = new MeshGeometry3D();

        // Dense shared rings and averaged vertex normals remove the faceted nose and tube.
        Loft(body, FuselageProfile(), (z, r, a) => new Point3D(r * Math.Cos(a), r * Math.Sin(a), z), 48);
        foreach (var side in new[] { -1d, 1d })
        {
            Wing(wings, side, new[] {
                new Section(1.25, -.25, -6.6, 11.5, .54), new Section(5.0, -.08, -4.4, 8.6, .39),
                new Section(10, .22, -.5, 5.3, .24), new Section(16.8, .70, 4.6, 2.7, .12),
                new Section(18.6, 1.02, 6.0, 1.75, .075), new Section(19.15, 2.65, 6.9, 1.05, .045) });
            Wing(wings, side, new[] { new Section(.8, .52, 12.6, 5.4, .23),
                new Section(4.3, 1.05, 14.6, 3.15, .13), new Section(8.3, 1.5, 17.0, 1.15, .05) });
            // Pylon is a solid small airfoil, not a disconnected engine cylinder.
            Fin(wings, side * 6.1, new[] { new Section(0, -1.45, -4.1, 4.9, .17),
                new Section(0, .05, -2.0, 4.0, .13) });
            var engineX = side * 6.1;
            Point3D Engine(double z, double radius, double a) => new Point3D(engineX + radius * Math.Cos(a), -1.65 + radius * Math.Sin(a), z);
            Loft(nacelles, new[] { new Ring(-6.35, 1.02), new Ring(-6.18, 1.16), new Ring(-5.8, 1.23),
                new Ring(-4.7, 1.24), new Ring(-2.5, 1.10), new Ring(-1.05, .81) }, Engine, 40);
            Loft(metal, new[] { new Ring(-6.05, .90), new Ring(-6.35, .95), new Ring(-6.42, 1.02),
                new Ring(-6.32, 1.10), new Ring(-6.18, 1.16) }, Engine, 40);
            Loft(interior, new[] { new Ring(-6.05, .90), new Ring(-5.85, .86), new Ring(-5.72, .01) }, Engine, 40, true);
            Loft(metal, new[] { new Ring(-1.08, .80), new Ring(-.80, .71), new Ring(-.65, .62), new Ring(-.78, .54) }, Engine, 40);
            Loft(interior, new[] { new Ring(-.78, .54), new Ring(-1.15, .01) }, Engine, 32);
            // Recessed fan and spinner remain visible from the forward camera.
            for (var blade = 0; blade < 20; blade++)
            {
                var angle = blade * 2 * Math.PI / 20;
                Triangle(metal, Engine(-5.99, .24, angle), Engine(-5.96, .83, angle + .19), Engine(-5.96, .83, angle + .10));
            }
            Loft(metal, new[] { new Ring(-6.22, .01), new Ring(-6.12, .16), new Ring(-5.97, .25) }, Engine, 24);

            // Windows follow the curved skin, including the swept cockpit glazing.
            for (var i = 0; i < 30; i++)
            {
                var z = -13.8 + i * .80;
                Window(glass, side, z, .22, .31);
            }
            Cockpit(glass, side);
            foreach (var z in new[] { -14.9, 10.8 }) Door(seams, side, z);

            if (includeGear)
            {
                // Main gear: two tyres per axle, plus a visible strut and diagonal brace.
                var hub = new Point3D(side * 2.75, -2.66, 2.0);
                Cylinder(metal, new Point3D(side * 2.3, -.7, 1.2), hub, .105);
                Cylinder(metal, new Point3D(side * 1.65, -.8, 3.2), hub + new Vector3D(0, .25, 0), .065);
                Cylinder(metal, hub + new Vector3D(-.56, 0, 0), hub + new Vector3D(.56, 0, 0), .12);
                Wheel(rubber, metal, hub + new Vector3D(-.40, 0, 0), .54, .28);
                Wheel(rubber, metal, hub + new Vector3D(.40, 0, 0), .54, .28);
            }
        }
        Fin(wings, 0, new[] { new Section(0, .8, 10.4, 9.3, .29),
            new Section(0, 3.6, 13.7, 5.4, .18), new Section(0, 7.9, 16.5, 2.0, .065) });
        if (includeGear)
        {
            var noseHub = new Point3D(0, -2.82, -14.3);
            Cylinder(metal, new Point3D(0, -1.5, -14.0), noseHub, .085);
            Cylinder(metal, new Point3D(0, -1.45, -12.8), noseHub + new Vector3D(0, .35, 0), .05);
            foreach (var x in new[] { -.23, .23 }) Wheel(rubber, metal, noseHub + new Vector3D(x, 0, 0), .38, .18);
        }

        var result = new Model3DGroup();
        Add(result, body, "#E7E7E2", 28); Add(result, wings, "#B8C5CE", 35);
        Add(result, nacelles, "#DFE3E2", 35); Add(result, metal, "#858F98", 45);
        Add(result, glass, "#173645", 70); if (includeGear) Add(result, rubber, "#1B1D20", 0);
        Add(result, interior, "#14191E", 0); Add(result, seams, "#858E93", 0);
        result.Freeze(); return result;
    }

    private static Ring[] FuselageProfile()
    {
        // Shape-preserving eased intervals: rounded radome, parallel cabin, tapered tail cone.
        var controls = new[] { new Ring(-22, .015), new Ring(-21.5, .43), new Ring(-20.5, .92),
            new Ring(-19.1, 1.40), new Ring(-17.5, 1.70), new Ring(-15.4, 1.85),
            new Ring(-12, 1.90), new Ring(9.8, 1.90), new Ring(12.6, 1.70),
            new Ring(15.0, 1.32), new Ring(17.7, .79), new Ring(20.0, .27), new Ring(21.0, .015) };
        var rings = new List<Ring>();
        for (var i = 0; i < controls.Length - 1; i++)
        {
            var a = controls[i]; var b = controls[i + 1];
            var da = i == 0 ? (b.Radius - a.Radius) / (b.Z - a.Z) : Slope(controls[i - 1], a, b);
            var db = i + 2 == controls.Length ? (b.Radius - a.Radius) / (b.Z - a.Z) : Slope(a, b, controls[i + 2]);
            for (var k = 0; k < 4; k++)
            {
                var t = k / 4d; var t2 = t * t; var t3 = t2 * t; var dz = b.Z - a.Z;
                rings.Add(new Ring(a.Z + dz * t, (2 * t3 - 3 * t2 + 1) * a.Radius +
                    (t3 - 2 * t2 + t) * dz * da + (-2 * t3 + 3 * t2) * b.Radius + (t3 - t2) * dz * db));
            }
        }
        rings.Add(controls[controls.Length - 1]); return rings.ToArray();
    }

    private static double Slope(Ring a, Ring b, Ring c)
    {
        var left = (b.Radius - a.Radius) / (b.Z - a.Z); var right = (c.Radius - b.Radius) / (c.Z - b.Z);
        return left * right <= 0 ? 0 : 2 * left * right / (left + right);
    }

    private static double Radius(double z)
    {
        // Used only at construction time for surface details.
        var profile = FuselageProfile();
        for (var i = 1; i < profile.Length; i++)
            if (z <= profile[i].Z) return profile[i - 1].Radius + (profile[i].Radius - profile[i - 1].Radius) *
                (z - profile[i - 1].Z) / (profile[i].Z - profile[i - 1].Z);
        return profile[profile.Length - 1].Radius;
    }

    private static Point3D Skin(double side, double z, double angle)
    { var r = Radius(z) + .018; return new Point3D(side * r * Math.Cos(angle), r * Math.Sin(angle), z); }

    private static void Window(MeshGeometry3D mesh, double side, double z, double halfWidth, double halfHeight)
    {
        var firstIndex = mesh.TriangleIndices.Count;
        const int count = 12; var center = Skin(side, z, .27); var start = mesh.Positions.Count;
        mesh.Positions.Add(center);
        for (var i = 0; i < count; i++)
        {
            var angle = i * Math.PI * 2 / count;
            mesh.Positions.Add(Skin(side, z + halfWidth * Math.Cos(angle), .27 + halfHeight / Radius(z) * Math.Sin(angle)));
        }
        for (var i = 0; i < count; i++) Indices(mesh, start, start + 1 + i, start + 1 + (i + 1) % count);
        if (side > 0) Reverse(mesh, firstIndex);
    }

    private static void Cockpit(MeshGeometry3D mesh, double side)
    {
        var firstIndex = mesh.TriangleIndices.Count;
        // Each pane is tessellated on the rounded skin so it cannot sink through the nose.
        foreach (var bounds in new[] { new[] { -20.05, -19.35, .52, .96 }, new[] { -19.24, -18.36, .37, .78 }, new[] { -18.25, -17.52, .28, .62 } })
        {
            for (var i = 0; i < 6; i++) for (var j = 0; j < 3; j++)
            {
                var z0 = bounds[0] + (bounds[1] - bounds[0]) * i / 6; var z1 = bounds[0] + (bounds[1] - bounds[0]) * (i + 1) / 6;
                var a0 = bounds[2] + (bounds[3] - bounds[2]) * j / 3; var a1 = bounds[2] + (bounds[3] - bounds[2]) * (j + 1) / 3;
                Quad(mesh, Skin(side, z0, a0), Skin(side, z1, a0), Skin(side, z1, a1), Skin(side, z0, a1));
            }
        }
        if (side > 0) Reverse(mesh, firstIndex);
    }

    private static void Door(MeshGeometry3D mesh, double side, double z)
    {
        var firstIndex = mesh.TriangleIndices.Count;
        for (var i = 0; i < 16; i++)
        {
            var a0 = -.58 + i * 1.22 / 16; var a1 = -.58 + (i + 1) * 1.22 / 16;
            foreach (var edge in new[] { -.42, .42 })
                Quad(mesh, Skin(side, z + edge - .018, a0), Skin(side, z + edge + .018, a0), Skin(side, z + edge + .018, a1), Skin(side, z + edge - .018, a1));
        }
        foreach (var a in new[] { -.58, .64 }) Quad(mesh, Skin(side, z - .42, a), Skin(side, z + .42, a), Skin(side, z + .42, a + .02), Skin(side, z - .42, a + .02));
        if (side > 0) Reverse(mesh, firstIndex);
    }

    private static void Wing(MeshGeometry3D mesh, double side, Section[] sections) => Airfoil(mesh, sections,
        (s, chord, height) => new Point3D(side * s.Span, s.Height + height, s.Leading + s.Chord * chord), side < 0);
    private static void Fin(MeshGeometry3D mesh, double x, Section[] sections) => Airfoil(mesh, sections,
        (s, chord, height) => new Point3D(x + height, s.Height, s.Leading + s.Chord * chord), true);

    private static void Airfoil(MeshGeometry3D mesh, Section[] sections, Func<Section, double, double, Point3D> point, bool reverse)
    {
        var firstIndex = mesh.TriangleIndices.Count;
        const int steps = 18; const int ringCount = steps * 2; var start = mesh.Positions.Count;
        foreach (var section in sections) for (var i = 0; i < ringCount; i++)
        {
            var angle = Math.PI * 2 * i / ringCount; var chord = (1 - Math.Cos(angle)) / 2;
            // Rounded leading edge, thin trailing edge. Camber is visual only.
            var thickness = 5 * section.Thickness * (.2969 * Math.Sqrt(chord) - .1260 * chord - .3516 * chord * chord +
                .2843 * chord * chord * chord - .1036 * chord * chord * chord * chord);
            mesh.Positions.Add(point(section, chord, (i <= steps ? 1 : -1) * thickness));
        }
        Stitch(mesh, start, sections.Length, ringCount);
        Cap(mesh, start, ringCount, true); Cap(mesh, start + (sections.Length - 1) * ringCount, ringCount, false);
        if (reverse) Reverse(mesh, firstIndex);
    }

    internal static void Wheel(MeshGeometry3D tyres, MeshGeometry3D hubs, Point3D center, double radius, double width)
    {
        Point3D Map(double x, double r, double a) => center + new Vector3D(x, r * Math.Sin(a), r * Math.Cos(a));
        Loft(tyres, new[] { new Ring(-width * .5, radius * .46), new Ring(-width * .57, radius * .78),
            new Ring(-width * .35, radius), new Ring(width * .35, radius), new Ring(width * .57, radius * .78), new Ring(width * .5, radius * .46) }, Map, 24, true);
        Loft(hubs, new[] { new Ring(-width * .52, .005), new Ring(-width * .52, radius * .46),
            new Ring(width * .52, radius * .46), new Ring(width * .52, .005) }, Map, 24, true);
    }

    internal static void Cylinder(MeshGeometry3D mesh, Point3D a, Point3D b, double radius)
    {
        var axis = b - a; axis.Normalize(); var u = Vector3D.CrossProduct(axis, new Vector3D(0, 0, 1));
        if (u.LengthSquared < 1e-6) u = Vector3D.CrossProduct(axis, new Vector3D(0, 1, 0));
        u.Normalize(); var v = Vector3D.CrossProduct(axis, u);
        Loft(mesh, new[] { new Ring(0, radius), new Ring(1, radius) },
            (t, r, angle) => a + (b - a) * t + u * (r * Math.Cos(angle)) + v * (r * Math.Sin(angle)), 12);
    }

    private static void Loft(MeshGeometry3D mesh, Ring[] profile, Func<double, double, double, Point3D> point, int sides, bool reverse = false)
    {
        var firstIndex = mesh.TriangleIndices.Count;
        var start = mesh.Positions.Count;
        foreach (var ring in profile) for (var i = 0; i < sides; i++) mesh.Positions.Add(point(ring.Z, ring.Radius, i * 2 * Math.PI / sides));
        Stitch(mesh, start, profile.Length, sides);
        if (reverse) Reverse(mesh, firstIndex);
    }
    private static void Stitch(MeshGeometry3D mesh, int start, int rings, int sides)
    {
        for (var s = 1; s < rings; s++) for (var i = 0; i < sides; i++)
        {
            var a = start + (s - 1) * sides + i; var b = start + (s - 1) * sides + (i + 1) % sides;
            var c = start + s * sides + (i + 1) % sides; var d = start + s * sides + i;
            Indices(mesh, a, b, c); Indices(mesh, a, c, d);
        }
    }
    private static void Cap(MeshGeometry3D mesh, int ring, int count, bool reverse)
    {
        for (var i = 1; i < count - 1; i++)
            if (reverse) Indices(mesh, ring, ring + i + 1, ring + i); else Indices(mesh, ring, ring + i, ring + i + 1);
    }
    private static void Quad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d)
    { Triangle(mesh, a, b, c); Triangle(mesh, a, c, d); }
    private static void Triangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c)
    { var i = mesh.Positions.Count; mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); Indices(mesh, i, i + 1, i + 2); }
    private static void Indices(MeshGeometry3D mesh, int a, int b, int c)
    { mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); }
    private static void Reverse(MeshGeometry3D mesh, int firstIndex)
    {
        for (var i = firstIndex; i < mesh.TriangleIndices.Count; i += 3)
        { var b = mesh.TriangleIndices[i + 1]; mesh.TriangleIndices[i + 1] = mesh.TriangleIndices[i + 2]; mesh.TriangleIndices[i + 2] = b; }
    }

    internal static void Add(Model3DGroup group, MeshGeometry3D mesh, string color, double gloss)
    {
        var normals = new Vector3D[mesh.Positions.Count];
        for (var i = 0; i < mesh.TriangleIndices.Count; i += 3)
        {
            var a = mesh.TriangleIndices[i]; var b = mesh.TriangleIndices[i + 1]; var c = mesh.TriangleIndices[i + 2];
            var n = Vector3D.CrossProduct(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        foreach (var value in normals) { var n = value; if (n.LengthSquared > 1e-16) n.Normalize(); else n = new Vector3D(0, 1, 0); mesh.Normals.Add(n); }
        mesh.Freeze();
        var materials = new MaterialGroup(); materials.Children.Add(new DiffuseMaterial(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))));
        if (gloss > 0) materials.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(65, 69, 72)), gloss));
        materials.Freeze(); var model = new GeometryModel3D(mesh, materials) { BackMaterial = materials }; model.Freeze(); group.Children.Add(model);
    }
    private readonly struct Ring
    { internal Ring(double z, double radius) { Z = z; Radius = radius; } internal double Z { get; } internal double Radius { get; } }
    private readonly struct Section
    {
        internal Section(double span, double height, double leading, double chord, double thickness)
        { Span = span; Height = height; Leading = leading; Chord = chord; Thickness = thickness; }
        internal double Span { get; } internal double Height { get; } internal double Leading { get; }
        internal double Chord { get; } internal double Thickness { get; }
    }
}
