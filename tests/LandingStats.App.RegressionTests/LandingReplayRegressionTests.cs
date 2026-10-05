using System;
using System.Windows;
using System.Windows.Media.Media3D;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class LandingReplayRegressionTests
{
    public static void RunAll()
    {
        LandingReplaySurfaceRegressionTests.RunAll();
        ReplayContactMarkersTests.RunAll();
        var north = Rotate(0, 0, 0, new Vector3D(0, 0, -1));
        Near(north.Z, -1); Near(north.X, 0);
        var east = Rotate(90, 0, 0, new Vector3D(0, 0, -1)); Near(east.X, 1); Near(east.Z, 0);
        var up = Rotate(0, -10, 0, new Vector3D(0, 0, -1)); Require(up.Y > 0, "negative MSFS pitch must raise the nose");
        var right = Rotate(0, 0, 10, new Vector3D(1, 0, 0)); Require(right.Y > 0, "positive MSFS bank must raise the right wing");
        // Combined angles must follow the SDK's final body-to-earth transform,
        // not merely pass three independent single-axis sign checks.
        var h = 123 * Math.PI / 180; var p = -17 * Math.PI / 180; var b = 31 * Math.PI / 180;
        var noseCombined = Rotate(123, -17, 31, new Vector3D(0, 0, -1));
        Near(noseCombined.X, Math.Cos(p) * Math.Sin(h)); Near(noseCombined.Y, -Math.Sin(p)); Near(noseCombined.Z, -Math.Cos(p) * Math.Cos(h));
        var rightCombined = Rotate(123, -17, 31, new Vector3D(1, 0, 0));
        Near(rightCombined.X, Math.Cos(b) * Math.Cos(h) + Math.Sin(b) * Math.Sin(p) * Math.Sin(h));
        Near(rightCombined.Y, Math.Sin(b) * Math.Cos(p));
        Near(rightCombined.Z, Math.Cos(b) * Math.Sin(h) - Math.Sin(b) * Math.Sin(p) * Math.Cos(h));
        foreach (var heading in new[] { 0d, 90d, 180d, 270d })
        {
            var angle = heading * Math.PI / 180;
            var offset = LandingReplayTrack.FrontLeftCameraOffset(heading, 100);
            Require(offset.Y > 0, "camera above aircraft");
            Require(Vector3D.DotProduct(offset, new Vector3D(Math.Sin(angle), 0, -Math.Cos(angle))) > 0, "camera ahead");
            Require(Vector3D.DotProduct(offset, new Vector3D(-Math.Cos(angle), 0, -Math.Sin(angle))) > 0, "camera on aircraft left, not world west");
        }
        var record = Record();
        Require(LandingReplayTrack.TryCreate(record, out var track), "new pose data usable");
        Require(track!.TrySample(.05, out var middle), "midpoint sample");
        var direction = Matrix(middle.Orientation).Transform(new Vector3D(0, 0, -1));
        Near(direction.X, 0, .0001); Near(direction.Z, -1, .0001);
        Near(middle.Position.Y, 100 * .3048); Near(middle.Value(p => p.PilotPitchPercent), 50);
        Require(track.Frames[1].Position.Z < track.Frames[0].Position.Z, "northward GPS motion is negative Z");
        Near(track.Frames[1].Position.X, 0, .001);
        Require(track.TrySample(-100, out var start), "clamp start"); Near(start.TimeSeconds, 0);
        Require(track.TrySample(100, out var end), "clamp end"); Near(end.TimeSeconds, .1);
        Require(!track.TrySample(double.NaN, out _), "NaN time rejected");

        record.Series[1].TimeSeconds = 2;
        Require(LandingReplayTrack.TryCreate(record, out track), "gapped track retains samples");
        Require(!track!.TrySample(1, out _), "never invent motion over gaps");
        Require(track.TrySample(2, out _), "actual endpoint survives gap");
        record.Series[1].TimeSeconds = -.1;
        Require(!LandingReplayTrack.TryCreate(record, out _), "clock reset rejected");
        record = Record(); record.Series[1].LatitudeDegrees = null;
        Require(!LandingReplayTrack.TryCreate(record, out _), "missing position does not become zero");
        record = Record(); record.Series[1].HeadingTrueDegrees = double.NaN;
        Require(!LandingReplayTrack.TryCreate(record, out _), "nonfinite heading rejected");
        record = Record(); record.Series[1].LongitudeDegrees = 181;
        Require(!LandingReplayTrack.TryCreate(record, out _), "invalid longitude rejected");
        record = Record(); record.Series[0].LongitudeDegrees = 179.9999; record.Series[1].LongitudeDegrees = -179.9999;
        Require(LandingReplayTrack.TryCreate(record, out track), "date line track");
        Require(Math.Abs(track!.Frames[1].Position.X - track.Frames[0].Position.X) < 30, "date line crossing is not a world-spanning trail");
        record = Record(); record.Series[0].LatitudeDegrees = record.Series[1].LatitudeDegrees = 90;
        Require(LandingReplayTrack.TryCreate(record, out track), "polar projection");
        Require(!double.IsNaN(track!.Frames[1].Position.X), "polar projection finite");

        record = Record(); record.Series[1].TimeSeconds = 0; record.Series.Add(Point(.1, 0, 0));
        Require(LandingReplayTrack.TryCreate(record, out track), "equal timestamps retain latest");
        Require(track!.Frames.Count == 2 && ReferenceEquals(track.Frames[0].Sample, record.Series[1]), "last duplicate wins");
        record = Record(); record.Series[1].GroundAltitudeFeet = 950;
        Require(LandingReplayTrack.TryCreate(record, out track), "sloping surface");
        Near(track!.Frames[1].Position.Y - track.Frames[0].Position.Y, 0);
        Require(track.Frames[1].Ground.Y > track.Frames[0].Ground.Y, "terrain slope must not masquerade as aircraft descent");
        var legacy = new LandingRecord(); legacy.Series.Add(new LandingSeriesPoint()); legacy.Series.Add(new LandingSeriesPoint { TimeSeconds = 1 });
        Require(!LandingReplayTrack.TryCreate(legacy, out _), "legacy unavailable, never fake GS-integrated track");

        var view = new LandingReplayView { Record = Record(), Width = 800, Height = 400 };
        var events = 0; view.TimeChanged += (_, __) => events++;
        view.Measure(new Size(800, 400)); view.Arrange(new Rect(0, 0, 800, 400)); view.SetTime(.05);
        Require(view.IsPlaybackAvailable, "WPF native scene accepts pose data"); Near(view.CurrentTimeSeconds, .05);
        Require(events == 0, "programmatic chart synchronization cannot recurse");
        view.Record = legacy; Require(!view.IsPlaybackAvailable, "record switch clears old scene");
    }

    private static LandingRecord Record()
    {
        var record = new LandingRecord(); record.Series.Add(Point(0, 359, 0)); record.Series.Add(Point(.1, 1, 100)); return record;
    }
    private static LandingSeriesPoint Point(double time, double heading, double input) => new LandingSeriesPoint
    {
        TimeSeconds = time, LatitudeDegrees = 42 + time * .001, LongitudeDegrees = 23,
        PlaneAltitudeFeet = 1000, GroundAltitudeFeet = 900, HeadingTrueDegrees = heading,
        PitchDegrees = 0, BankDegrees = 0, GroundSpeedKnots = 140, GForce = 1, PilotPitchPercent = input,
    };
    private static Vector3D Rotate(double heading, double pitch, double bank, Vector3D vector) => Matrix(LandingReplayTrack.Orientation(heading, pitch, bank)).Transform(vector);
    private static Matrix3D Matrix(Quaternion rotation) { var matrix = Matrix3D.Identity; matrix.Rotate(rotation); return matrix; }
    private static void Near(double a, double b, double tolerance = .0000001) => Require(Math.Abs(a - b) <= tolerance, $"expected {b}, got {a}");
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
