using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.RegressionTests;

internal static class ReplayWindRegressionTests
{
    public static void RunAll()
    {
        LocalizationManager.Apply("en");
        try { PlacementAndDirections(); ContactFallback(); TerrainFootprintClearance(); MissingAndInterpolatedWind(); FrozenGeometryAndLocalization(); IntegratedResetAndGap(); }
        finally { LocalizationManager.Apply("en"); }
    }

    private static void PlacementAndDirections()
    {
        foreach (var secondary in new[] { false, true })
        foreach (var heading in new[] { 0d, 37d, 135d, 263d })
        foreach (var pianoKeys in new[] { false, true })
        {
            var record = Record(heading, secondary); var runway = record.RunwayGeometry!;
            runway.PrimaryThresholdOffsetMeters = 120; runway.SecondaryThresholdOffsetMeters = 80;
            if (pianoKeys)
            {
                runway.PrimaryPapiQueried = runway.SecondaryPapiQueried = true;
                runway.PrimaryPapi = new RunwayPapi { StationFromEndMeters = 460, AngleDegrees = 3 };
                runway.SecondaryPapi = new RunwayPapi { StationFromEndMeters = 470, AngleDegrees = 3 };
            }
            var indicator = Create(record, out var track);
            var projectedCenter = track.ProjectGeo(runway.LatitudeDegrees, runway.LongitudeDegrees, runway.AltitudeMeters / LandingReplayTrack.FeetToMeters);
            var center = new Point3D(projectedCenter.X, projectedCenter.Y, projectedCenter.Z);
            var landingHeading = heading + (secondary ? 180 : 0);
            var forward = Forward(landingHeading); var right = Right(landingHeading);
            var label = WorldPoints(indicator, Field<GeometryModel3D>(indicator, "_label"));
            var threshold = RunwayMarkingModel.Build(runway).Shapes.Where(p => p.Kind == "Threshold" && p.Secondary == secondary).SelectMany(p => p.Points).ToArray();
            Require((threshold.Length > 0) == pianoKeys, "Placement fixture must exercise both painted and physical thresholds.");
            var targetStation = threshold.Length > 0
                ? threshold.Min(p => p.Y * (secondary ? -1 : 1))
                : -runway.LengthMeters / 2 + (secondary ? 80 : 120);
            Near(label.Min(p => Vector3D.DotProduct(p - center, forward)), targetStation,
                "Numeric block approach edge must align with the near edge of selected piano keys or displaced physical threshold.");
            Require(label.All(p => Vector3D.DotProduct(p - center, right) > runway.WidthMeters / 2),
                "Complete numeric block must lie outside the selected runway on its right.");
            RequireFootprintGap(indicator, landingHeading);
            var rose = Field<GeometryModel3D>(indicator, "_rose"); var mesh = (MeshGeometry3D)rose.Geometry;
            var north = Midpoint(mesh.Positions[0], mesh.Positions[3]); var east = Midpoint(mesh.Positions[2], mesh.Positions[3]);
            Near(north.X, 0, "Compass texture north is not rotated with the runway.");
            Require(north.Z < 0 && east.X > 0 && rose.Transform.Value.IsIdentity,
                "Compass texture must align north with -Z and east with +X.");
            foreach (var direction in new[] { 0d, 90d, 180d, 270d })
            {
                foreach (var sample in record.Series) sample.WindDirectionDegrees = direction;
                track.TrySample(0, out var pose); indicator.Update(pose);
                var expectedHeading = direction == 0 ? 360 : direction;
                Require(Label(indicator) == expectedHeading.ToString("000", CultureInfo.InvariantCulture) + "/12", "Wind label must report its true FROM direction and knots.");
                var actual = new RotateTransform3D(Field<AxisAngleRotation3D>(indicator, "_arrowRotation")).Transform(new Vector3D(0, 0, -1));
                var toRadians = (direction + 180) * Math.PI / 180;
                Near(actual.X, Math.Sin(toRadians), "Wind arrow points downwind in world X.");
                Near(actual.Z, -Math.Cos(toRadians), "Wind arrow points downwind in world Z.");
                Require(ArrowVisible(indicator), "Recorded nonzero wind must display its arrow.");
            }
        }
    }

    private static void ContactFallback()
    {
        foreach (var kind in new[] { "missing", "water", "invalid", "unknown-end" })
        {
            var record = Record(63);
            record.Series = new List<LandingSeriesPoint> { Point(-.2, 63), Point(.2, 63) };
            if (kind == "missing") record.RunwayGeometry = null;
            else if (kind == "water") record.RunwayGeometry!.SurfaceType = 2;
            else if (kind == "invalid") record.RunwayGeometry!.WidthMeters = 0;
            else if (kind == "unknown-end") record.Runway = "";
            LandingReplayTrack.TryCreate(record, out var track); track!.TrySample(0, out var contact);
            var surface = LandingReplaySurface.Build(track, record.RunwayGeometry);
            var indicator = ReplayWindIndicator.Create(record, track, surface, kind == "unknown-end" ? null : (double?)63);
            Require(indicator != null, "Missing usable runway reference must retain a marker aligned to the selected contact: " + kind);
            var origin = new Point3D(contact.Ground.X, contact.Ground.Y, contact.Ground.Z);
            var label = WorldPoints(indicator!, Field<GeometryModel3D>(indicator!, "_label"));
            Near(label.Min(p => Vector3D.DotProduct(p - origin, Forward(contact.HeadingDegrees))), 0,
                "No-runway numeric block approach edge must align with exact selected-contact time zero.");
            Require(label.All(p => Vector3D.DotProduct(p - origin, Right(contact.HeadingDegrees)) > 0),
                "No-runway numeric block must remain to the right in the recorded contact heading.");
            RequireFootprintGap(indicator!, contact.HeadingDegrees);
        }
        foreach (var times in new[] { new[] { .1, .2 }, new[] { -.2, -.1 }, new[] { -1d, 1d } })
        {
            var record = Record(); record.RunwayGeometry = null;
            record.Series = times.Select(time => Point(time, 0)).ToList();
            LandingReplayTrack.TryCreate(record, out var track);
            Require(ReplayWindIndicator.Create(record, track!, LandingReplaySurface.Build(track!, null), null) == null,
                "Fallback must not clamp or interpolate across missing selected-contact time zero.");
        }
    }

    private static void TerrainFootprintClearance()
    {
        foreach (var heading in new[] { 37d, 135d })
        foreach (var useRunway in new[] { true, false })
        {
            var record = Record(heading); var runway = record.RunwayGeometry!;
            runway.LatitudeDegrees = runway.LongitudeDegrees = 0; runway.AltitudeMeters = 130;
            runway.PrimaryThresholdOffsetMeters = 120;
            record.Series.Clear();
            var forward = Forward(heading);
            for (var i = 0; i <= 60; i++)
            {
                var station = -430 + i * 3; var point = forward * station;
                // A steep, narrow interior ridge is missed by sparse outer-footprint corner samples.
                var groundMeters = 130 + .1 * (station + 430) + 32 * Math.Max(0, 1 - Math.Abs(station + 350) / 9);
                record.Series.Add(new LandingSeriesPoint
                {
                    TimeSeconds = (i - 15) * .1, LatitudeDegrees = -point.Z / 110574, LongitudeDegrees = point.X / 111319.49,
                    GroundAltitudeFeet = groundMeters / LandingReplayTrack.FeetToMeters,
                    PlaneAltitudeFeet = (groundMeters + 5) / LandingReplayTrack.FeetToMeters,
                    HeadingTrueDegrees = heading, WindDirectionDegrees = 140, WindSpeedKnots = 4,
                });
            }
            if (!useRunway) record.RunwayGeometry = null;
            LandingReplayTrack.TryCreate(record, out var track);
            var surface = LandingReplaySurface.Build(track!, record.RunwayGeometry);
            var indicator = ReplayWindIndicator.Create(record, track!, surface, useRunway ? (double?)heading : null) ??
                throw new InvalidOperationException("Sloped-terrain fixture must create its wind marker.");
            var checkedGround = 0;
            foreach (var field in new[] { "_rose", "_label" })
            {
                var model = Field<GeometryModel3D>(indicator, field); var mesh = (MeshGeometry3D)model.Geometry;
                var world = WorldPoints(indicator, model);
                for (var triangle = 0; triangle < mesh.TriangleIndices.Count; triangle += 3)
                {
                    var a = world[mesh.TriangleIndices[triangle]]; var b = world[mesh.TriangleIndices[triangle + 1]]; var c = world[mesh.TriangleIndices[triangle + 2]];
                    for (var i = 0; i <= 20; i++) for (var j = 0; j <= 20 - i; j++)
                    {
                        var point = a + (b - a) * (i / 20d) + (c - a) * (j / 20d);
                        var ground = surface.HeightAt(point.X, point.Z);
                        if (!ground.HasValue) continue;
                        checkedGround++;
                        Require(point.Y > ground.Value + .01, "Entire wind texture must stay above the rendered terrain, including an interior ridge.");
                    }
                }
            }
            Require(checkedGround > 100, "Terrain-clearance check must cover rendered ground beneath both marker quads.");
            RequireFootprintGap(indicator, heading);
        }
    }

    private static void RequireFootprintGap(ReplayWindIndicator indicator, double heading)
    {
        var forward = Forward(heading);
        var rose = WorldPoints(indicator, Field<GeometryModel3D>(indicator, "_rose"));
        var label = WorldPoints(indicator, Field<GeometryModel3D>(indicator, "_label"));
        Require(rose.Min(p => Vector3D.DotProduct((Vector3D)p, forward)) - label.Max(p => Vector3D.DotProduct((Vector3D)p, forward)) > .1,
            "Full numeric and compass quads must remain disjoint at every landing heading.");
    }
    private static Point3D[] WorldPoints(ReplayWindIndicator indicator, GeometryModel3D model) =>
        ((MeshGeometry3D)model.Geometry).Positions.Select(p => indicator.Model.Transform.Transform(model.Transform.Transform(p))).ToArray();
    private static Vector3D Forward(double heading) => new Vector3D(Math.Sin(heading * Math.PI / 180), 0, -Math.Cos(heading * Math.PI / 180));
    private static Vector3D Right(double heading) => new Vector3D(Math.Cos(heading * Math.PI / 180), 0, Math.Sin(heading * Math.PI / 180));
    private static void MissingAndInterpolatedWind()
    {
        var record = Record(); var indicator = Create(record, out var track);
        record.Series[0].WindDirectionDegrees = 350; record.Series[1].WindDirectionDegrees = 10;
        foreach (var sample in record.Series) sample.WindSpeedKnots = 20;
        track.TrySample(.1, out var midpoint); indicator.Update(midpoint);
        Require(Label(indicator) == "360/20", "350-to-10 interpolation must cross north instead of pointing south.");
        var arrow = new RotateTransform3D(Field<AxisAngleRotation3D>(indicator, "_arrowRotation")).Transform(new Vector3D(0, 0, -1));
        Near(arrow.X, 0, "Circular interpolation stays north/south."); Near(arrow.Z, 1, "A northerly wind blows south.");
        record.Series[0].WindDirectionDegrees = 0; record.Series[1].WindDirectionDegrees = 180;
        track.TrySample(.1, out midpoint); indicator.Update(midpoint);
        Require(Label(indicator) == "000/00" && !ArrowVisible(indicator), "Opposing equal winds interpolate as a calm vector.");
        foreach (var sample in record.Series) { sample.WindSpeedKnots = 0; sample.WindDirectionDegrees = double.NaN; }
        track.TrySample(0, out var pose); indicator.Update(pose);
        Require(Label(indicator) == "000/00" && !ArrowVisible(indicator), "Recorded calm does not require a direction.");
        foreach (var direction in new[] { double.NaN, double.PositiveInfinity, -1d, 361d })
        {
            foreach (var sample in record.Series) { sample.WindSpeedKnots = 12; sample.WindDirectionDegrees = direction; }
            track.TrySample(0, out pose); indicator.Update(pose);
            Require(Label(indicator) == "—/—" && !ArrowVisible(indicator), "Invalid wind direction must remain unavailable.");
        }
        foreach (var speed in new[] { double.NaN, double.PositiveInfinity, -1d })
        {
            foreach (var sample in record.Series) { sample.WindSpeedKnots = speed; sample.WindDirectionDegrees = 270; }
            track.TrySample(0, out pose); indicator.Update(pose);
            Require(Label(indicator) == "—/—" && !ArrowVisible(indicator), "Invalid wind speed must not become calm.");
        }
        indicator.Update(null);
        Require(Label(indicator) == "—/—" && !ArrowVisible(indicator), "A telemetry gap clears the last wind reading.");
    }

    private static void FrozenGeometryAndLocalization()
    {
        var record = Record(); var indicator = Create(record, out var track);
        record.Series[0].WindDirectionDegrees = .1; record.Series[1].WindDirectionDegrees = .2;
        record.Series[0].WindSpeedKnots = 12.1; record.Series[1].WindSpeedKnots = 12.2;
        track.TrySample(0, out var pose); indicator.Update(pose);
        var models = Geometries(indicator.Model).ToArray(); var meshes = models.Select(model => model.Geometry).ToArray();
        var materials = models.Select(model => model.Material).ToArray();
        Require(meshes.All(mesh => mesh.IsFrozen) && materials.All(material => material.IsFrozen), "Wind geometry and raster materials must be frozen.");
        var originalAngle = Field<AxisAngleRotation3D>(indicator, "_arrowRotation").Angle;
        for (var i = 0; i <= 100; i++) { track.TrySample(i * .002, out pose); indicator.Update(pose); }
        Require(models.Select(model => model.Geometry).SequenceEqual(meshes) && models.Select(model => model.Material).SequenceEqual(materials),
            "Playback with the same rounded label must reuse every mesh and material.");
        Require(Field<AxisAngleRotation3D>(indicator, "_arrowRotation").Angle != originalAngle,
            "Arrow must follow sub-degree wind changes even while its rounded text remains cached.");
        var label = Field<GeometryModel3D>(indicator, "_label"); var rose = Field<GeometryModel3D>(indicator, "_rose");
        var labelMaterial = label.Material; var roseMaterial = rose.Material;
        RequireUnlitAlpha(label.Material); RequireUnlitAlpha(rose.Material);
        RequireOrangeAccents(rose.Material, label.Material);
        foreach (var sample in record.Series) sample.WindSpeedKnots = 14;
        track.TrySample(0, out pose); indicator.Update(pose);
        Require(!ReferenceEquals(label.Material, labelMaterial) && ReferenceEquals(rose.Material, roseMaterial),
            "Changed numeric text must replace only its own raster material.");
        var arrowModel = models.Single(model => !ReferenceEquals(model, label) && !ReferenceEquals(model, rose));
        var arrowMaterial = arrowModel.Material; var englishPixels = Pixels(rose.Material);
        Require(new[] { "WindNorth", "WindEast", "WindSouth", "WindWest" }.Select(ReplayOverlayStyle.L).SequenceEqual(new[] { "N", "E", "S", "W" }),
            "English rose must name all four cardinal directions.");
        indicator.RefreshLocalization();
        Require(ReferenceEquals(rose.Material, roseMaterial), "Unchanged localization must reuse the compass raster.");
        LocalizationManager.Apply("ru"); indicator.RefreshLocalization();
        Require(new[] { "WindNorth", "WindEast", "WindSouth", "WindWest" }.Select(ReplayOverlayStyle.L).SequenceEqual(new[] { "С", "В", "Ю", "З" }),
            "Russian rose must name all four cardinal directions.");
        Require(!ReferenceEquals(rose.Material, roseMaterial) && !englishPixels.SequenceEqual(Pixels(rose.Material)) &&
            ReferenceEquals(arrowModel.Material, arrowMaterial) && models.Select(model => model.Geometry).SequenceEqual(meshes),
            "Localization updates compass letters while preserving arrow and all geometry.");
        RequireUnlitAlpha(label.Material); RequireUnlitAlpha(rose.Material);
        RequireOrangeAccents(rose.Material, label.Material);
        var russianMaterial = rose.Material; indicator.RefreshLocalization();
        Require(ReferenceEquals(rose.Material, russianMaterial), "Repeated Russian refresh must reuse the new raster.");
        LocalizationManager.Apply("en");
    }

    private static void IntegratedResetAndGap()
    {
        var record = Record();
        record.Series.Add(Point(1.2, 0)); record.Series.Add(Point(1.4, 0));
        var view = new LandingReplayView { Record = record };
        var wind = Field<ReplayWindIndicator>(view, "_wind");
        view.SetTime(0); Require(ArrowVisible(wind), "Integrated replay must show recorded wind.");
        view.SetTime(.7);
        Require(Label(wind) == "—/—" && !ArrowVisible(wind), "Integrated time gap must clear wind along with the aircraft.");
        view.SetTime(1.2); Require(ArrowVisible(wind), "Recorded endpoint after a gap must restore wind.");
        view.SetRecord(null);
        Require(Get(view, "_wind") == null && Field<ModelVisual3D>(view, "_scene").Content == null, "Clearing replay must release its world wind indicator.");
        view.SetRecord(record); view.SetRecord(new LandingRecord());
        Require(Get(view, "_wind") == null && Field<ModelVisual3D>(view, "_scene").Content == null, "Legacy/no-pose record must not retain the previous wind marker.");
    }

    private static LandingRecord Record(double heading = 0, bool secondary = false) => new LandingRecord
    {
        Runway = secondary ? "18" : "36",
        RunwayGeometry = new RunwayFacility { AirportIdent = "TEST", LatitudeDegrees = 42, LongitudeDegrees = 23, AltitudeMeters = 304.8,
            HeadingTrueDegrees = heading, LengthMeters = 1000, WidthMeters = 45, PrimaryName = "36", SecondaryName = "18", SurfaceType = 0 },
        Series = new List<LandingSeriesPoint> { Point(0, heading), Point(.2, heading) },
    };
    private static LandingSeriesPoint Point(double time, double heading) => new LandingSeriesPoint
    { TimeSeconds = time, LatitudeDegrees = 42 + time * .0001, LongitudeDegrees = 23, PlaneAltitudeFeet = 1004,
        GroundAltitudeFeet = 1000, HeadingTrueDegrees = heading, WindDirectionDegrees = 0, WindSpeedKnots = 12, GForce = 1 };
    private static ReplayWindIndicator Create(LandingRecord record, out LandingReplayTrack track)
    {
        Require(LandingReplayTrack.TryCreate(record, out var found), "Valid replay fixture."); track = found!;
        Require(LandingReplayRunwayOffset.TryCreate(record, track, out var offset), "Valid selected runway direction.");
        return ReplayWindIndicator.Create(record, track, LandingReplaySurface.Build(track, record.RunwayGeometry), offset!.HeadingTrueDegrees) ??
            throw new InvalidOperationException("Valid runway and recorded wind did not create an indicator.");
    }
    private static Point3D Midpoint(Point3D a, Point3D b) => a + (b - a) * .5;
    private static bool ArrowVisible(ReplayWindIndicator indicator) => Field<ScaleTransform3D>(indicator, "_arrowVisibility").ScaleX != 0;
    private static string Label(ReplayWindIndicator indicator) => Field<string>(indicator, "_labelText");
    private static T Field<T>(object value, string name) => (T)Get(value, name)!;
    private static object? Get(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
    private static IEnumerable<GeometryModel3D> Geometries(Model3D model)
    {
        if (model is GeometryModel3D geometry) yield return geometry;
        if (model is Model3DGroup group) foreach (var child in group.Children) foreach (var nested in Geometries(child)) yield return nested;
    }
    private static void RequireOrangeAccents(Material rose, Material label)
    {
        foreach (var entry in new[] { (Material: rose, LastRow: 70), (Material: label, LastRow: 112) })
        {
            var image = (BitmapSource)((ImageBrush)((MaterialGroup)entry.Material).Children.OfType<EmissiveMaterial>().Single().Brush).ImageSource;
            var pixels = Pixels(entry.Material); var accentPixels = 0;
            for (var y = 0; y < entry.LastRow; y++) for (var x = 0; x < image.PixelWidth; x++)
            {
                var i = (y * image.PixelWidth + x) * 4;
                if (pixels[i + 3] >= 250 && pixels[i + 2] >= 245 && pixels[i + 1] >= 115 && pixels[i + 1] <= 130 && pixels[i] >= 60 && pixels[i] <= 80)
                    accentPixels++;
            }
            Require(accentPixels > 10, "North letter and numeric wind reading must use the app's orange accent in either locale.");
        }
    }
    private static void RequireUnlitAlpha(Material material)
    {
        var group = (MaterialGroup)material;
        var diffuse = (BitmapSource)((ImageBrush)group.Children.OfType<DiffuseMaterial>().Single().Brush).ImageSource;
        var emissive = (BitmapSource)((ImageBrush)group.Children.OfType<EmissiveMaterial>().Single().Brush).ImageSource;
        Require(diffuse.PixelWidth == emissive.PixelWidth && diffuse.PixelHeight == emissive.PixelHeight,
            "Wind texture lighting passes must have matching dimensions.");
        var darkPixels = new byte[diffuse.PixelWidth * diffuse.PixelHeight * 4];
        var colorPixels = new byte[darkPixels.Length];
        diffuse.CopyPixels(darkPixels, diffuse.PixelWidth * 4, 0); emissive.CopyPixels(colorPixels, emissive.PixelWidth * 4, 0);
        var hasTransparentPixel = false; var hasVisiblePixel = false;
        for (var i = 0; i < darkPixels.Length; i += 4)
        {
            Require(darkPixels[i] == 0 && darkPixels[i + 1] == 0 && darkPixels[i + 2] == 0,
                "Diffuse wind texture must remain black so illumination cannot double its displayed colors.");
            Require(darkPixels[i + 3] == colorPixels[i + 3],
                "Diffuse wind texture must preserve emissive alpha instead of leaving an opaque plate.");
            hasTransparentPixel |= colorPixels[i + 3] == 0;
            hasVisiblePixel |= colorPixels[i + 3] != 0;
        }
        Require(hasTransparentPixel && hasVisiblePixel, "Wind texture must retain visible artwork and a transparent exterior.");
    }
    private static byte[] Pixels(Material material)
    {
        var image = (BitmapSource)((ImageBrush)((MaterialGroup)material).Children.OfType<EmissiveMaterial>().Single().Brush).ImageSource;
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    private static void Near(double actual, double expected, string message) => Require(Math.Abs(actual - expected) < 1e-6, message + " Actual=" + actual + ", expected=" + expected);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
