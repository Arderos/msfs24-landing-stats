using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Media3D;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class ReplayGearRegistrationTests
{
    public static void RunAll()
    {
        var record = Record();
        Require(LandingReplayTrack.TryCreate(record, out var track), "contact fixture");
        var originals = track!.Frames.Select(f => (f.Position, f.Orientation, f.TimeSeconds)).ToArray();
        var surface = LandingReplaySurface.Build(track, null);
        var gear = new ReplayLandingGear();
        var registration = new ReplayGearRegistration(gear, track, surface, 0);
        Require(registration.IsCalibrated, "known contact fits generic suspension");
        Require(registration.UnloadedExtension > .5 && registration.UnloadedExtension < 3, "substantial old floating error is corrected by gear, not body");
        track.TrySample(0, out var contact); registration.Update(contact);
        Near(gear.Legs.Min(leg => registration.ClearanceAt(leg, contact)), 0, .00001, "lowest wheel touches at analysed zero, before delayed boolean");
        Require(registration.ClearanceAt(gear.Legs[2], contact) > .5, "nose remains airborne at nose-up touchdown");
        Require(gear.Legs.Take(2).Max(leg => registration.ClearanceAt(leg, contact)) > .03, "bank leaves high-side main above ground");

        track.TrySample(-.1, out var before); registration.Update(before);
        Require(gear.Legs.Min(leg => registration.ClearanceAt(leg, before)) > .05, "no premature touch immediately before contact");
        track.TrySample(2, out var settled); registration.Update(settled);
        foreach (var leg in gear.Legs)
        {
            Near(registration.ClearanceAt(leg, settled), 0, .00001, "settled wheels remain on surface, not below it");
            Require(leg.Extension < registration.UnloadedExtension - .5, "suspension compresses while body follows recorded descent");
        }
        track.TrySample(3, out var bounce); registration.Update(bounce);
        foreach (var leg in gear.Legs)
        {
            Near(leg.Extension, registration.UnloadedExtension, 1e-10, "airborne suspension returns to unloaded extension");
            Require(registration.ClearanceAt(leg, bounce) > 1, "bounce remains visibly airborne");
        }
        for (var i = 0; i < originals.Length; i++)
            Require(originals[i].Position == track.Frames[i].Position && originals[i].Orientation == track.Frames[i].Orientation && originals[i].TimeSeconds == track.Frames[i].TimeSeconds,
                "registration never changes position, attitude or time");

        // Updating suspension only transforms immutable tyre geometry, without
        // resizing wheels or sharing mutable animation state between two views.
        var independent = new ReplayLandingGear(); var points = gear.Legs[0].TyrePoints.ToArray();
        gear.Legs[0].SetExtension(2.5);
        Require(points.SequenceEqual(gear.Legs[0].TyrePoints), "tyre geometry stays rigid");
        Near(independent.Legs[0].Extension, 0, 1e-10, "per-view gear state is independent");
        var wheels = (Model3DGroup)gear.Legs[0].Model.Children[0];
        Near(wheels.Transform.Value.M22, 1, 1e-10, "tyres translate instead of scaling");
        Require(wheels.Children[0].IsFrozen, "animated wheel instance shares frozen geometry");

        var view = new LandingReplayView { Record = record };
        var scene = (ModelVisual3D)typeof(LandingReplayView).GetField("_scene", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
        var group = (Model3DGroup)scene.Content;
        var marker = (MeshGeometry3D)((GeometryModel3D)group.Children[group.Children.Count - 2]).Geometry;
        var markerX = (marker.Positions.Min(p => p.X) + marker.Positions.Max(p => p.X)) / 2;
        var markerZ = (marker.Positions.Min(p => p.Z) + marker.Positions.Max(p => p.Z)) / 2;
        Near(markerX, contact.Ground.X, 1e-8, "marker interpolates exact zero X");
        Near(markerZ, contact.Ground.Z, 1e-8, "marker interpolates exact zero Z, not nearest sample");
        var nearest = track.Frames.OrderBy(f => Math.Abs(f.TimeSeconds)).First();
        Require((nearest.Ground - contact.Ground).Length > .5, "fixture catches significant nearest-frame marker error");

        var noContact = Record(); foreach (var point in noContact.Series) point.OnGround = false;
        LandingReplayTrack.TryCreate(noContact, out var airborne);
        Require(!new ReplayGearRegistration(new ReplayLandingGear(), airborne!, LandingReplaySurface.Build(airborne!, null), 0).IsCalibrated,
            "no ground evidence cannot invent a suspension calibration");
        var impossible = new ReplayGearRegistration(new ReplayLandingGear(), track, surface, 100);
        Require(!impossible.IsCalibrated, "unreasonable extension is not silently applied");

        var gapped = Record(); gapped.Series.RemoveAll(p => p.TimeSeconds > 2 && p.TimeSeconds < 4);
        gapped.Series.Add(new LandingSeriesPoint { TimeSeconds = 2.1, LatitudeDegrees = 42.00105, LongitudeDegrees = 23,
            GroundAltitudeFeet = 1000, PlaneAltitudeFeet = 1000 + 7 / .3048, HeadingTrueDegrees = 0 });
        gapped.Series.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        foreach (var point in gapped.Series.Where(p => p.TimeSeconds >= 4)) point.OnGround = true;
        LandingReplayTrack.TryCreate(gapped, out var gapTrack);
        var gapGear = new ReplayLandingGear(); var gapRegistration = new ReplayGearRegistration(gapGear, gapTrack!, LandingReplaySurface.Build(gapTrack!, null), 0);
        gapTrack!.TrySample(2.1, out var gapPose); gapRegistration.Update(gapPose);
        foreach (var leg in gapGear.Legs) Near(leg.Extension, gapRegistration.UnloadedExtension, 1e-8, "missing telemetry cannot introduce a future suspension anchor");

        // A very shallow bounce must not be erased by suspension support. Also
        // exercise a mismatched hull datum below the generic fixed-wheel height:
        // only the symbolic gear may adapt, never the recorded airborne pose.
        foreach (var airborneHeight in new[] { 3.22, 3.195 })
        {
            var shallow = new LandingRecord();
            foreach (var time in new[] { -.1, 0, .05, .1, .15, .2 })
                shallow.Series.Add(new LandingSeriesPoint { TimeSeconds = time, LatitudeDegrees = 42 + time * .001, LongitudeDegrees = 23,
                    PlaneAltitudeFeet = 1000 + (time == .1 ? airborneHeight : time < 0 ? 3.3 : 3.2) / .3048,
                    GroundAltitudeFeet = 1000, HeadingTrueDegrees = 0, OnGround = time >= 0 && time != .1 });
            LandingReplayTrack.TryCreate(shallow, out var shallowTrack);
            var shallowGear = new ReplayLandingGear();
            var shallowRegistration = new ReplayGearRegistration(shallowGear, shallowTrack!, LandingReplaySurface.Build(shallowTrack!, null), 0);
            shallowTrack!.TrySample(.1, out var shallowPose); var originalHeight = shallowPose.Position.Y;
            shallowRegistration.Update(shallowPose);
            Require(shallowGear.Legs.Min(leg => shallowRegistration.ClearanceAt(leg, shallowPose)) > 0, "tiny airborne phase remains visibly separate from surface");
            Near(shallowPose.Position.Y, originalHeight, 1e-12, "air clearance adaptation never changes recorded bounce height");
            shallowTrack.TrySample(.15, out var nextContact); shallowRegistration.Update(nextContact);
            Near(shallowGear.Legs.Min(leg => shallowRegistration.ClearanceAt(leg, nextContact)), 0, .00001, "shallow bounce returns to surface at next contact");
        }
    }

    private static LandingRecord Record()
    {
        var record = new LandingRecord();
        var times = new[] { -1d, -.1, .05, .25, .5, .75, 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3, 3.25, 3.5, 3.75, 4, 4.1, 4.3 };
        foreach (var t in times)
        {
            var h = t <= .05 ? 5.2 - t * 1.5 : t < 2 ? 5.125 + (3.2 - 5.125) * (t - .05) / 1.95 : t == 2 ? 3.2 : 7;
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = t, LatitudeDegrees = 42 + t * .0005, LongitudeDegrees = 23,
                PlaneAltitudeFeet = 1000 + h / .3048, GroundAltitudeFeet = 1000, HeadingTrueDegrees = 0,
                PitchDegrees = t < 2 ? -5 : 0, BankDegrees = t < 2 ? -1 : 0, OnGround = t >= .05 && t <= 2 });
        }
        return record;
    }
    private static void Near(double actual, double expected, double tolerance, string message) => Require(Math.Abs(actual - expected) <= tolerance, message + ": " + actual);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
