using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace LandingStats.App.Controls;

/// <summary>
/// Per-view suspension transforms around shared rigid wheels. Extension is a
/// visual body-local displacement, not a change to the recorded aircraft pose.
/// </summary>
internal sealed class ReplayLandingGear
{
    private static readonly Model3DGroup UnitRod = CreateUnitRod();
    private static readonly Template[] Templates = { new Template(-1), new Template(1), new Template(0) };

    internal ReplayLandingGear()
    {
        var legs = new[] { new Leg(0), new Leg(1), new Leg(2) };
        Legs = Array.AsReadOnly(legs);
        foreach (var leg in legs) Model.Children.Add(leg.Model);
    }

    internal Model3DGroup Model { get; } = new Model3DGroup();
    // Left main, right main, nose. Geometry and tyre support points are in the
    // same aircraft-local coordinates as ReplayAircraftModel, nose along -Z.
    internal IReadOnlyList<Leg> Legs { get; }

    internal sealed class Leg
    {
        private readonly Template _template;
        private readonly TranslateTransform3D _wheelTranslation = new TranslateTransform3D();
        private readonly Rod _strut;
        private readonly Rod _brace;

        internal Leg(int index)
        {
            _template = Templates[index];
            var wheels = new Model3DGroup { Transform = _wheelTranslation };
            wheels.Children.Add(_template.Wheels);
            Model.Children.Add(wheels);
            _strut = new Rod(_template.StrutRadius); _brace = new Rod(_template.BraceRadius);
            Model.Children.Add(_strut.Model); Model.Children.Add(_brace.Model);
            SetExtension(0);
        }

        internal Model3DGroup Model { get; } = new Model3DGroup();
        internal Point3D BaseHub => _template.Hub;
        internal IReadOnlyList<Point3D> TyrePoints => _template.TyrePoints;
        internal bool IsNose => _template.IsNose;
        internal double Extension { get; private set; }

        /// <summary>Positive extends down local Y; negative compresses. Tyres never scale.</summary>
        internal void SetExtension(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) throw new ArgumentOutOfRangeException(nameof(metres));
            Extension = metres;
            _wheelTranslation.OffsetY = -metres;
            var hub = BaseHub + new Vector3D(0, -metres, 0);
            _strut.SetEndpoints(_template.StrutMount, hub);
            _brace.SetEndpoints(_template.BraceMount, hub + new Vector3D(0, _template.BraceHubOffset, 0));
        }
    }

    private sealed class Template
    {
        internal Template(int side)
        {
            IsNose = side == 0;
            Hub = IsNose ? new Point3D(0, -2.82, -14.3) : new Point3D(side * 2.75, -2.66, 2.0);
            StrutMount = IsNose ? new Point3D(0, -1.5, -14.0) : new Point3D(side * 2.3, -.7, 1.2);
            BraceMount = IsNose ? new Point3D(0, -1.45, -12.8) : new Point3D(side * 1.65, -.8, 3.2);
            BraceHubOffset = IsNose ? .35 : .25;
            StrutRadius = IsNose ? .085 : .105; BraceRadius = IsNose ? .05 : .065;

            var tyres = new MeshGeometry3D(); var hubs = new MeshGeometry3D();
            if (!IsNose) ReplayAircraftModel.Cylinder(hubs, Hub + new Vector3D(-.56, 0, 0), Hub + new Vector3D(.56, 0, 0), .12);
            foreach (var direction in new[] { -1, 1 })
                ReplayAircraftModel.Wheel(tyres, hubs, Hub + new Vector3D(direction * (IsNose ? .23 : .40), 0, 0),
                    IsNose ? .38 : .54, IsNose ? .18 : .28);
            var points = new Point3D[tyres.Positions.Count]; tyres.Positions.CopyTo(points, 0);
            TyrePoints = Array.AsReadOnly(points);
            Wheels = new Model3DGroup();
            ReplayAircraftModel.Add(Wheels, tyres, "#1B1D20", 0);
            ReplayAircraftModel.Add(Wheels, hubs, "#858F98", 45);
            Wheels.Freeze();
        }

        internal bool IsNose { get; }
        internal Point3D Hub { get; }
        internal Point3D StrutMount { get; }
        internal Point3D BraceMount { get; }
        internal double BraceHubOffset { get; }
        internal double StrutRadius { get; }
        internal double BraceRadius { get; }
        internal Model3DGroup Wheels { get; }
        internal IReadOnlyList<Point3D> TyrePoints { get; }
    }

    private sealed class Rod
    {
        private readonly double _radius;
        private readonly MatrixTransform3D _transform = new MatrixTransform3D();

        internal Rod(double radius)
        {
            _radius = radius;
            Model = new Model3DGroup { Transform = _transform };
            Model.Children.Add(UnitRod);
        }

        internal Model3DGroup Model { get; }

        internal void SetEndpoints(Point3D from, Point3D to)
        {
            var direction = to - from; var axis = direction;
            if (axis.LengthSquared < 1e-16) axis = new Vector3D(0, 1, 0); else axis.Normalize();
            var across = Vector3D.CrossProduct(axis, new Vector3D(0, 0, 1));
            if (across.LengthSquared < 1e-12) across = Vector3D.CrossProduct(axis, new Vector3D(1, 0, 0));
            across.Normalize(); var depth = Vector3D.CrossProduct(across, axis);
            // Row-vector Matrix3D convention: local Y spans the endpoints,
            // local X/Z stay round with a constant radius. Only this matrix changes.
            _transform.Matrix = new Matrix3D(
                across.X * _radius, across.Y * _radius, across.Z * _radius, 0,
                direction.X, direction.Y, direction.Z, 0,
                depth.X * _radius, depth.Y * _radius, depth.Z * _radius, 0,
                from.X, from.Y, from.Z, 1);
        }
    }

    private static Model3DGroup CreateUnitRod()
    {
        var mesh = new MeshGeometry3D();
        ReplayAircraftModel.Cylinder(mesh, new Point3D(0, 0, 0), new Point3D(0, 1, 0), 1);
        var model = new Model3DGroup(); ReplayAircraftModel.Add(model, mesh, "#858F98", 45);
        model.Freeze(); return model;
    }
}
