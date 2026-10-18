using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.ReplayMath;
using LandingStats.Core;

namespace LandingStats.App.Models;

/// <summary>
/// Recorded positions, not a flight-dynamics reconstruction. World axes are
/// east / up / south in metres; the generic model points along local -Z.
/// </summary>
public sealed class LandingReplayTrack
{
    public const double MaximumInterpolationGapSeconds = 0.5;
    public const double FeetToMeters = 0.3048;
    private readonly LandingReplayFrame[] _frames;
    private readonly double _latitude;
    private readonly double _longitude;
    private readonly double _groundFeet;
    private readonly Vector3D _origin;

    private LandingReplayTrack(LandingSeriesPoint[] points)
    {
        var origin = points.OrderBy(p => Math.Abs(p.TimeSeconds)).First();
        _latitude = origin.LatitudeDegrees!.Value * Math.PI / 180;
        _longitude = origin.LongitudeDegrees!.Value * Math.PI / 180;
        _groundFeet = origin.GroundAltitudeFeet!.Value;
        _origin = EarthPosition(_latitude, _longitude);
        _frames = points.Select(p => new LandingReplayFrame(p,
            ProjectGeo(p.LatitudeDegrees!.Value, p.LongitudeDegrees!.Value, p.PlaneAltitudeFeet!.Value),
            ProjectGeo(p.LatitudeDegrees!.Value, p.LongitudeDegrees!.Value, p.GroundAltitudeFeet!.Value),
            Orientation(p.HeadingTrueDegrees!.Value, p.PitchDegrees, p.BankDegrees))).ToArray();
    }

    public IReadOnlyList<LandingReplayFrame> Frames => _frames;
    public double StartSeconds => _frames[0].TimeSeconds;
    public double EndSeconds => _frames[_frames.Length - 1].TimeSeconds;

    public static bool TryCreate(LandingRecord? record, out LandingReplayTrack? track, TelemetryDecisionJournal? journal = null)
    {
        track = null;
        if (record?.Series == null || record.Series.Count < 2)
        { journal?.Record(0, "replay.track", "insufficient_samples", "3D replay requires at least two recorded positions."); return false; }
        var points = new List<LandingSeriesPoint>(record.Series.Count);
        var duplicates = 0;
        foreach (var p in record.Series)
        {
            if (!Finite(p.TimeSeconds) || !Finite(p.PitchDegrees) || !Finite(p.BankDegrees) ||
                !Present(p.LatitudeDegrees) || Math.Abs(p.LatitudeDegrees!.Value) > 90 ||
                !Present(p.LongitudeDegrees) || Math.Abs(p.LongitudeDegrees!.Value) > 180 ||
                !Present(p.PlaneAltitudeFeet) || !Present(p.GroundAltitudeFeet) || !Present(p.HeadingTrueDegrees))
            { journal?.Record(p.TimeSeconds, "replay.track", "invalid_pose", "Missing or invalid position, altitude, heading or attitude prevents 3D replay."); return false; }
            if (points.Count > 0)
            {
                var previous = points[points.Count - 1];
                if (p.TimeSeconds < previous.TimeSeconds)
                { journal?.Record(p.TimeSeconds, "replay.track", "clock_reversed", "Recorded time went backwards; no replay track is created."); return false; }
                if (p.TimeSeconds == previous.TimeSeconds) { points[points.Count - 1] = p; duplicates++; continue; }
                if (p.TimeSeconds - previous.TimeSeconds > MaximumInterpolationGapSeconds)
                    journal?.Record(p.TimeSeconds, "replay.track", "interpolation_gap", "Recorded endpoints remain available but no motion or trail is invented across this gap.",
                        new Dictionary<string, double> { ["from_seconds"] = previous.TimeSeconds, ["to_seconds"] = p.TimeSeconds });
            }
            points.Add(p);
        }
        if (points.Count < 2)
        { journal?.Record(0, "replay.track", "insufficient_unique_times", "Duplicate simulator times leave fewer than two recorded positions."); return false; }
        track = new LandingReplayTrack(points.ToArray());
        journal?.Record(0, "replay.track", "track_created", "Recorded geodetic positions projected east/up/south; recorded attitudes retained without dynamics reconstruction.",
            new Dictionary<string, double> { ["frames"] = points.Count, ["duplicates_replaced"] = duplicates,
                ["start_seconds"] = track.StartSeconds, ["end_seconds"] = track.EndSeconds });
        return true;
    }

    public bool TrySample(double timeSeconds, out LandingReplayPose pose)
    {
        pose = default;
        if (!Finite(timeSeconds)) return false;
        var time = Math.Max(StartSeconds, Math.Min(EndSeconds, timeSeconds));
        var lo = RecordedTimeSearch.LowerBound(_frames.Length, index => _frames[index].TimeSeconds, time);
        if (lo == 0 || _frames[lo].TimeSeconds == time)
        {
            pose = new LandingReplayPose(_frames[lo], _frames[lo], 0, time);
            return true;
        }
        var a = _frames[lo - 1]; var b = _frames[lo];
        if (b.TimeSeconds - a.TimeSeconds > MaximumInterpolationGapSeconds) return false;
        pose = new LandingReplayPose(a, b, (time - a.TimeSeconds) / (b.TimeSeconds - a.TimeSeconds), time);
        return true;
    }

    public Point3D ProjectGeo(double latitudeDegrees, double longitudeDegrees, double altitudeFeet)
    {
        var difference = EarthPosition(latitudeDegrees * Math.PI / 180, longitudeDegrees * Math.PI / 180) - _origin;
        var east = -Math.Sin(_longitude) * difference.X + Math.Cos(_longitude) * difference.Y;
        var north = -Math.Sin(_latitude) * Math.Cos(_longitude) * difference.X -
                    Math.Sin(_latitude) * Math.Sin(_longitude) * difference.Y + Math.Cos(_latitude) * difference.Z;
        return new Point3D(east, (altitudeFeet - _groundFeet) * FeetToMeters, -north);
    }

    public static Quaternion Orientation(double headingTrue, double rawPitch, double rawBank)
    {
        // MSFS: negative pitch = nose up, positive bank = left wing down.
        // With model nose=-Z: apply roll, then pitch, then true heading.
        var rotation = new Quaternion(new Vector3D(0, 1, 0), -headingTrue) *
                       new Quaternion(new Vector3D(1, 0, 0), -rawPitch) *
                       new Quaternion(new Vector3D(0, 0, 1), rawBank);
        rotation.Normalize();
        return rotation;
    }

    public static Vector3D FrontLeftCameraOffset(double headingTrue, double distance, double elevationDegrees = 27)
    {
        var angle = (headingTrue - 45) * Math.PI / 180;
        var elevation = elevationDegrees * Math.PI / 180;
        return new Vector3D(Math.Sin(angle) * Math.Cos(elevation) * distance,
            Math.Sin(elevation) * distance, -Math.Cos(angle) * Math.Cos(elevation) * distance);
    }

    private static Vector3D EarthPosition(double latitude, double longitude)
    {
        const double radius = 6378137;
        const double eccentricitySquared = 0.0066943799901413165;
        var sin = Math.Sin(latitude);
        var n = radius / Math.Sqrt(1 - eccentricitySquared * sin * sin);
        return new Vector3D(n * Math.Cos(latitude) * Math.Cos(longitude),
            n * Math.Cos(latitude) * Math.Sin(longitude), n * (1 - eccentricitySquared) * sin);
    }

    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool Present(double? value) => value.HasValue && Finite(value.Value);
}

public sealed class LandingReplayFrame
{
    internal LandingReplayFrame(LandingSeriesPoint sample, Point3D position, Point3D ground, Quaternion orientation)
    { Sample = sample; Position = position; Ground = ground; Orientation = orientation; }
    public LandingSeriesPoint Sample { get; }
    public double TimeSeconds => Sample.TimeSeconds;
    public Point3D Position { get; }
    public Point3D Ground { get; }
    public Quaternion Orientation { get; }
}

public readonly struct LandingReplayPose
{
    private readonly LandingReplayFrame _a;
    private readonly LandingReplayFrame _b;
    private readonly double _fraction;
    internal LandingReplayPose(LandingReplayFrame a, LandingReplayFrame b, double fraction, double time)
    {
        _a = a; _b = b; _fraction = fraction; TimeSeconds = time;
        Position = a.Position + (b.Position - a.Position) * fraction;
        Ground = a.Ground + (b.Ground - a.Ground) * fraction;
        Orientation = Quaternion.Slerp(a.Orientation, b.Orientation, fraction, true);
        var difference = (b.Sample.HeadingTrueDegrees!.Value - a.Sample.HeadingTrueDegrees!.Value + 540) % 360 - 180;
        HeadingDegrees = (a.Sample.HeadingTrueDegrees.Value + fraction * difference + 360) % 360;
    }
    public Point3D Position { get; }
    public Point3D Ground { get; }
    public Quaternion Orientation { get; }
    public double TimeSeconds { get; }
    public double HeadingDegrees { get; }
    public double Value(Func<LandingSeriesPoint, double> selector)
    {
        var a = selector(_a.Sample); var b = selector(_b.Sample);
        return LandingReplayTrack.Finite(a) && LandingReplayTrack.Finite(b) ? a + (b - a) * _fraction : double.NaN;
    }
}
