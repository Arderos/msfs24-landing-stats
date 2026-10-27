using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LandingStats.App.Models;

namespace LandingStats.App.Controls;

/// <summary>World-aligned wind rose. Geometry and textures survive camera and playback updates.</summary>
internal sealed class ReplayWindIndicator
{
    private const double RoseHalfSize = 1.25;
    private const double LabelHalfWidth = 1.4;
    private const double LabelHalfDepth = .43;
    private const double LabelGap = .18;
    private readonly GeometryModel3D _rose;
    private readonly GeometryModel3D _label;
    private readonly AxisAngleRotation3D _arrowRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
    private readonly ScaleTransform3D _arrowVisibility = new ScaleTransform3D(0, 0, 0);
    private string _labelText = "—/—";
    private string? _localizationKey;
    private static readonly Typeface Typeface = new Typeface("Cascadia Mono, Consolas");
    private static readonly Func<LandingSeriesPoint, double> WindEast = point => WindComponent(point, true);
    private static readonly Func<LandingSeriesPoint, double> WindNorth = point => WindComponent(point, false);
    internal Model3DGroup Model { get; } = new Model3DGroup();
    internal Point3D Anchor { get; }

    private ReplayWindIndicator(Point3D anchor, double radius, double landingHeading, double labelDistance)
    {
        Anchor = anchor;
        var placement = new TranslateTransform3D(anchor.X, anchor.Y, anchor.Z); placement.Freeze();
        Model.Transform = placement;
        _rose = new GeometryModel3D { Geometry = Plane(new Point3D(), new Vector3D(radius * RoseHalfSize, 0, 0), new Vector3D(0, 0, -radius * RoseHalfSize)) };
        Model.Children.Add(_rose);
        var arrowMesh = new MeshGeometry3D();
        var r = radius;
        // Local tip points north (-Z); rotating about +Y by -bearing keeps the east/up/south convention.
        AddTriangle(arrowMesh, new Point3D(-r * .022, .08, r * .72), new Point3D(r * .022, .08, r * .72), new Point3D(r * .022, .08, -r * .49));
        AddTriangle(arrowMesh, new Point3D(-r * .022, .08, r * .72), new Point3D(r * .022, .08, -r * .49), new Point3D(-r * .022, .08, -r * .49));
        AddTriangle(arrowMesh, new Point3D(-r * .11, .08, -r * .49), new Point3D(r * .11, .08, -r * .49), new Point3D(0, .08, -r * .72));
        arrowMesh.Freeze();
        var arrowMaterial = new MaterialGroup();
        arrowMaterial.Children.Add(new DiffuseMaterial(Brushes.Black));
        arrowMaterial.Children.Add(new EmissiveMaterial(ReplayOverlayStyle.Orange)); arrowMaterial.Freeze();
        var arrowTransform = new Transform3DGroup();
        arrowTransform.Children.Add(_arrowVisibility); arrowTransform.Children.Add(new RotateTransform3D(_arrowRotation));
        Model.Children.Add(new GeometryModel3D(arrowMesh, arrowMaterial) { BackMaterial = arrowMaterial, Transform = arrowTransform });
        var angle = landingHeading * Math.PI / 180;
        var forward = new Vector3D(Math.Sin(angle), 0, -Math.Cos(angle));
        var right = new Vector3D(Math.Cos(angle), 0, Math.Sin(angle));
        _label = new GeometryModel3D { Geometry = Plane(new Point3D() - forward * labelDistance, right * (radius * LabelHalfWidth), forward * (radius * LabelHalfDepth)) };
        Model.Children.Add(_label);
        RefreshLocalization();
    }

    internal static ReplayWindIndicator? Create(LandingRecord record, LandingReplayTrack track, LandingReplaySurface surface, double? landingHeading)
    {
        var runway = record.RunwayGeometry; var profile = surface.Geometry.Profile;
        Point3D target; double heading, radius, lateral;
        if (runway != null && runway.HasValidGeometry && !runway.IsWater && profile != null &&
            landingHeading.HasValue && LandingReplayTrack.Finite(landingHeading.Value))
        {
            heading = landingHeading.Value;
            var primary = Math.Cos((heading - runway.HeadingTrueDegrees) * Math.PI / 180) >= 0;
            var sign = primary ? 1d : -1d;
            var threshold = primary ? runway.PrimaryThresholdOffsetMeters : runway.SecondaryThresholdOffsetMeters;
            // Align to the rendered piano, whose ILS/PAPI-derived location can differ from facility offsets.
            var piano = RunwayMarkingModel.Build(runway).Shapes.Where(paint => paint.Kind == "Threshold" && paint.Secondary == !primary)
                .SelectMany(paint => paint.Points).Select(point => point.Y).ToArray();
            var station = piano.Length > 0 ? (primary ? piano.Min() : piano.Max()) : -sign * (profile.HalfLength - (threshold ?? 0));
            target = profile.At(station, 0, 0).ToWpf();
            radius = Math.Max(8, Math.Min(20, runway.WidthMeters * .3));
            lateral = profile.HalfWidth + radius * 1.9;
        }
        else
        {
            // Every selected landing has its own analysed t=0, including a selected bounce.
            // TrySample clamps to endpoints, so first require an actual recorded contact interval.
            if (track.StartSeconds > 0 || track.EndSeconds < 0 || !track.TrySample(0, out var contact)) return null;
            target = contact.Ground.ToWpf(); heading = contact.HeadingDegrees;
            radius = 12; lateral = radius * 3;
        }
        var angle = heading * Math.PI / 180;
        var forward = new Vector3D(Math.Sin(angle), 0, -Math.Cos(angle));
        var right = new Vector3D(Math.Cos(angle), 0, Math.Sin(angle));
        // A north-aligned square extends farther along a diagonal runway. Separate the complete
        // texture quads in plan; coplanar transparent pixels must never compete for depth.
        var roseExtent = radius * RoseHalfSize * (Math.Abs(forward.X) + Math.Abs(forward.Z));
        var labelDistance = roseExtent + radius * (LabelHalfDepth + LabelGap);
        var anchor = target + forward * (labelDistance + radius * LabelHalfDepth) + right * lateral;
        var labelCenter = anchor - forward * labelDistance;
        var height = Math.Max(target.Y,
            Math.Max(surface.Geometry.MaximumHeightOver(anchor.ToReplay(), new Vector3D(radius * RoseHalfSize, 0, 0).ToReplay(),
                new Vector3D(0, 0, -radius * RoseHalfSize).ToReplay()) ?? target.Y,
                surface.Geometry.MaximumHeightOver(labelCenter.ToReplay(), (right * (radius * LabelHalfWidth)).ToReplay(),
                    (forward * (radius * LabelHalfDepth)).ToReplay()) ?? target.Y));
        anchor.Y = height + .15;
        return new ReplayWindIndicator(anchor, radius, heading, labelDistance);
    }

    internal void Update(LandingReplayPose? pose)
    {
        var east = pose?.Value(WindEast) ?? double.NaN;
        var north = pose?.Value(WindNorth) ?? double.NaN;
        var available = LandingReplayTrack.Finite(east) && LandingReplayTrack.Finite(north);
        var speed = available ? Math.Sqrt(east * east + north * north) : double.NaN;
        var roundedSpeed = available ? Math.Round(speed, MidpointRounding.AwayFromZero) : 0;
        var direction = available ? (Math.Atan2(east, north) * 180 / Math.PI + 360) % 360 : 0;
        var showArrow = available && roundedSpeed > 0;
        var scale = showArrow ? 1d : 0d;
        if (_arrowVisibility.ScaleX != scale) _arrowVisibility.ScaleX = _arrowVisibility.ScaleY = _arrowVisibility.ScaleZ = scale;
        if (showArrow)
        {
            var rotation = -(direction + 180) % 360;
            if (_arrowRotation.Angle != rotation) _arrowRotation.Angle = rotation;
        }
        var heading = (int)Math.Round(direction, MidpointRounding.AwayFromZero) % 360;
        var label = !available ? "—/—" : roundedSpeed == 0 ? "000/00" :
            (heading == 0 ? 360 : heading).ToString("000", CultureInfo.InvariantCulture) + "/" + roundedSpeed.ToString("00", CultureInfo.InvariantCulture);
        if (_labelText != label) { _labelText = label; UpdateLabel(); }
    }

    private static double WindComponent(LandingSeriesPoint point, bool east)
    {
        var speed = point.WindSpeedKnots; var direction = point.WindDirectionDegrees;
        if (!LandingReplayTrack.Finite(speed) || speed < 0) return double.NaN;
        if (speed == 0) return 0;
        if (!LandingReplayTrack.Finite(direction) || direction < 0 || direction > 360) return double.NaN;
        var radians = direction * Math.PI / 180;
        // Interpolate measured wind vectors, so 359 -> 001 stays north and opposing winds can become calm.
        return speed * (east ? Math.Sin(radians) : Math.Cos(radians));
    }

    internal void RefreshLocalization()
    {
        var directions = new[] { ReplayOverlayStyle.L("WindNorth"), ReplayOverlayStyle.L("WindEast"), ReplayOverlayStyle.L("WindSouth"), ReplayOverlayStyle.L("WindWest") };
        var key = string.Join("|", directions) + ReplayOverlayStyle.L("WindUnits");
        if (_localizationKey == key) return;
        _localizationKey = key;
        SetMaterial(_rose, RasterMaterial(384, 384, dc =>
        {
            var center = new Point(192, 192); var line = ReplayOverlayStyle.Pen(ReplayOverlayStyle.Muted, 2);
            dc.DrawEllipse(ReplayOverlayStyle.Panel, line, center, 130, 130);
            for (var i = 0; i < 16; i++)
            {
                var angle = i * Math.PI / 8; var inner = i % 4 == 0 ? 105 : 119;
                dc.DrawLine(line, center + new Vector(Math.Sin(angle) * inner, -Math.Cos(angle) * inner),
                    center + new Vector(Math.Sin(angle) * 130, -Math.Cos(angle) * 130));
            }
            var positions = new[] { new Point(192, 24), new Point(357, 192), new Point(192, 356), new Point(26, 192) };
            for (var i = 0; i < directions.Length; i++) Text(dc, directions[i], positions[i], 34, i == 0 ? ReplayOverlayStyle.Orange : ReplayOverlayStyle.Ink);
        }));
        UpdateLabel();
    }

    private void UpdateLabel() => SetMaterial(_label, RasterMaterial(512, 156, dc =>
    {
        dc.DrawRoundedRectangle(ReplayOverlayStyle.Panel, null, new Rect(0, 0, 512, 156), 16, 16);
        Text(dc, _labelText, new Point(256, 57), 80, ReplayOverlayStyle.Orange);
        Text(dc, ReplayOverlayStyle.L("WindUnits"), new Point(256, 126), 28, ReplayOverlayStyle.Muted);
    }));

    private static void Text(DrawingContext dc, string value, Point center, double size, Brush brush)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, size, brush, 1);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
    private static Material RasterMaterial(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) draw(dc);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
        var brush = new ImageBrush(image); brush.Freeze();
        // Preserve texture alpha in both passes; an opaque diffuse base would leave a rectangular plate.
        var pixels = new byte[width * height * 4]; image.CopyPixels(pixels, width * 4, 0);
        for (var i = 0; i < pixels.Length; i += 4) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
        var alpha = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4); alpha.Freeze();
        var black = new ImageBrush(alpha); black.Freeze();
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(black)); material.Children.Add(new EmissiveMaterial(brush));
        material.Freeze(); return material;
    }
    private static void SetMaterial(GeometryModel3D model, Material material) { model.Material = material; model.BackMaterial = material; }
    private static MeshGeometry3D Plane(Point3D center, Vector3D right, Vector3D forward)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { center - right + forward, center - right - forward, center + right - forward, center + right + forward },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
            TextureCoordinates = new PointCollection { new Point(0, 0), new Point(0, 1), new Point(1, 1), new Point(1, 0) },
        };
        mesh.Freeze(); return mesh;
    }
    private static void AddTriangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c)
    {
        var i = mesh.Positions.Count; mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
        mesh.TriangleIndices.Add(i); mesh.TriangleIndices.Add(i + 1); mesh.TriangleIndices.Add(i + 2);
    }
}
