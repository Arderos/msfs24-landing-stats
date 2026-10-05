using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Media3D;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class ReplayContactMarkersTests
{
    public static void RunAll()
    {
        var records = new[] { Record(1), Record(2), Record(3) };
        foreach (var selected in records)
        {
            Require(LandingReplayTrack.TryCreate(selected, out var track), "Contact fixture track.");
            var points = LandingReplayContacts.GroundPositions(selected, track!, records);
            Require(points.Count == 3, "Every selected contact displays the whole three-contact sequence.");
            for (var i = 0; i < points.Count; i++)
                Require((points[i] - track!.ProjectGeo(42 + (i + 1) * .001, 23, 1000)).Length < .00001,
                    "Each marker uses its own exact contact time projected into the shared scene.");
            var view = new LandingReplayView(); view.SetRecord(selected, records);
            Require(Marker(view).Positions.Count == 36, "All three crosses are in the scene immediately.");
            view.SetTime(-.1); Require(Marker(view).Positions.Count == 36, "Future contacts remain visible before touchdown.");
            view.SetTime(.1); Require(Marker(view).Positions.Count == 36, "Past contacts remain visible after touchdown.");
            view.Record = selected;
            Require(Marker(view).Positions.Count == 12, "A record switch cannot retain another scene's contacts.");
        }
        var same = LandingReplayContacts.ForEpisode(records[0], records.Concat(new[] { records[1] }));
        Require(same.Count == 3, "Repeated references do not duplicate markers.");
        var otherFlight = Record(2); otherFlight.TimestampUtc = otherFlight.TimestampUtc.AddSeconds(1);
        var otherAircraft = Record(2); otherAircraft.AircraftTitle = "Other aircraft";
        var otherCount = Record(2); otherCount.ContactCount = 2;
        Require(LandingReplayContacts.ForEpisode(records[0], new[] { otherFlight, otherAircraft, otherCount }).Count == 1,
            "Nearby flights, other aircraft and incompatible contact sets are not merged.");
        Require(LandingReplayContacts.ForEpisode(records[0], records.Concat(new[] { Record(2) })).Count == 2,
            "Conflicting records for the same contact number do not create extra markers.");
        var broken = Record(2); broken.Series[1].TimeSeconds = 2;
        LandingReplayTrack.TryCreate(records[0], out var origin);
        Require(LandingReplayContacts.GroundPositions(records[0], origin!, new[] { broken }).Count == 1,
            "A missing contact interval is not interpolated or replaced with a nearby sample.");
        var missing = Record(2); missing.Series.Clear();
        Require(LandingReplayContacts.GroundPositions(records[0], origin!, new[] { missing }).Count == 1,
            "A missing sibling detail cannot create a fictitious marker.");
        var dateLine = Record(2); dateLine.Series[0].LongitudeDegrees = 179.9999; dateLine.Series[1].LongitudeDegrees = -179.9999;
        var across = LandingReplayContacts.GroundPositions(records[0], origin!, new[] { dateLine });
        Require((across[1] - origin!.ProjectGeo(42.002, 180, 1000)).Length < .00001,
            "Longitude interpolation takes the short path across the date line.");
    }

    private static MeshGeometry3D Marker(LandingReplayView view)
    {
        var scene = (ModelVisual3D)typeof(LandingReplayView).GetField("_scene", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
        var group = (Model3DGroup)scene.Content;
        return (MeshGeometry3D)((GeometryModel3D)group.Children[group.Children.Count - 2]).Geometry;
    }
    private static LandingRecord Record(int number)
    {
        var record = new LandingRecord { TimestampUtc = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
            ContactNumber = number, ContactCount = 3, AircraftTitle = "Test aircraft" };
        foreach (var time in new[] { -.1, .1 })
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = time, LatitudeDegrees = 42 + number * .001 + time * .0001,
                LongitudeDegrees = 23, HeadingTrueDegrees = 0, PlaneAltitudeFeet = 1010, GroundAltitudeFeet = 1000, OnGround = time > 0 });
        return record;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
