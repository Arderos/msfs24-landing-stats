using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Media.Media3D;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.Core;

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

        EveryEpisodeContactUsesItsAnalysedTime();
        MissingMotionCannotAcquireAnEpisodeContact();
        EpisodeContactClocksUseSharedSamples();
        FactoryContactClocksSurviveIndependentSampling();
        ContactClockSerializationAndInvalidValues();
    }

    private static void EveryEpisodeContactUsesItsAnalysedTime()
    {
        // Either contact can be selected. The other contact's analysed time is
        // still authoritative, not the later downsampled OnGround transition.
        foreach (var selectedTime in new[] { 0d, 3d })
        {
            var record = TwoContacts(selectedTime);
            Require(LandingReplayTrack.TryCreate(record, out var track), "two-contact episode fixture");
            var originals = track!.Frames.Select(f => (f.Position, f.Orientation, f.TimeSeconds)).ToArray();
            var gear = new ReplayLandingGear();
            var registration = new ReplayGearRegistration(gear, track, LandingReplaySurface.Build(track, null), 0,
                journal: null, contactTimesSeconds: new[] { -selectedTime, 3 - selectedTime });
            Require(registration.IsCalibrated, "each selected contact retains supported gear registration");

            foreach (var absoluteTime in new[] { 0d, 3d })
            {
                var time = absoluteTime - selectedTime;
                Require(track.TrySample(time - .005, out var before), "motion immediately before each analysed contact exists");
                registration.Update(before);
                Require(gear.Legs.Min(leg => registration.ClearanceAt(leg, before)) > 0,
                    "episode anchors must not ground the tyres before either analysed contact");

                Require(track.TrySample(time, out var contact), "each analysed contact has an interpolated pose");
                registration.Update(contact);
                Near(gear.Legs.Min(leg => registration.ClearanceAt(leg, contact)), 0, .00001,
                    "both contacts touch at their analysed time regardless of which contact is selected");
                Require(registration.ClearanceAt(gear.Legs[2], contact) > .5,
                    "episode contact anchors do not force the raised nose onto the surface");
            }

            Require(track.TrySample(2 - selectedTime, out var bounce), "intermediate bounce pose exists");
            registration.Update(bounce);
            Require(gear.Legs.Min(leg => registration.ClearanceAt(leg, bounce)) > .5,
                "calibrating all contacts does not flatten the intermediate airborne arc");
            for (var i = 0; i < originals.Length; i++)
                Require(originals[i].Position == track.Frames[i].Position && originals[i].Orientation == track.Frames[i].Orientation &&
                        originals[i].TimeSeconds == track.Frames[i].TimeSeconds,
                    "episode registration never changes recorded position, attitude or time");
        }
    }

    private static void MissingMotionCannotAcquireAnEpisodeContact()
    {
        var record = TwoContacts(0);
        record.Series.RemoveAll(p => p.TimeSeconds > 2.4 && p.TimeSeconds < 3.2);
        Require(LandingReplayTrack.TryCreate(record, out var track), "gapped episode endpoints remain usable");
        Require(!track!.TrySample(3, out _), "the second analysed contact falls inside a motion gap");
        var surface = LandingReplaySurface.Build(track, null);
        var baselineGear = new ReplayLandingGear();
        var baseline = new ReplayGearRegistration(baselineGear, track, surface, 0);
        var episodeGear = new ReplayLandingGear();
        var episode = new ReplayGearRegistration(episodeGear, track, surface, 0,
            journal: null, contactTimesSeconds: new[] { 0d, 3d });
        Require(track.TrySample(2.4, out var beforeGap), "last recorded airborne endpoint remains available");
        baseline.Update(beforeGap); episode.Update(beforeGap);
        for (var i = 0; i < episodeGear.Legs.Count; i++)
            Near(episodeGear.Legs[i].Extension, baselineGear.Legs[i].Extension, 1e-8,
                "a supplied contact inside missing motion cannot introduce a suspension anchor before the gap");
        Require(episodeGear.Legs.Min(leg => episode.ClearanceAt(leg, beforeGap)) > .5,
            "a missing future contact does not invent support under an airborne endpoint");
    }

    private static LandingRecord TwoContacts(double selectedTime)
    {
        var record = new LandingRecord();
        foreach (var time in new[] { -1d, -.5, -.1, .04, .2, .6, 1, 1.2, 1.5, 2, 2.4, 2.9, 3.04, 3.2, 3.6, 4 })
        {
            var height = time < .04 ? 5.2 - time * 1.5 : time <= 1 ? 5.14 + (3.2 - 5.14) * (time - .04) / .96 :
                time <= 2.4 ? 6 : time < 3.2 ? 4.802 - (time - 3) * 1.2 : 3.2;
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = time - selectedTime, LatitudeDegrees = 42 + time * .0005, LongitudeDegrees = 23,
                PlaneAltitudeFeet = 1000 + height / .3048, GroundAltitudeFeet = 1000, HeadingTrueDegrees = 0,
                PitchDegrees = time < 1 ? -5 : time < 3.2 ? -4 : 0,
                BankDegrees = time < 3.2 ? -1 : 0,
                OnGround = (time >= .04 && time <= 1) || time >= 3.04,
            });
        }
        return record;
    }

    private static void EpisodeContactClocksUseSharedSamples()
    {
        var first = TwoContacts(0);
        var second = TwoContacts(3);
        first.ContactCount = second.ContactCount = 2;
        second.ContactNumber = 2;
        second.TimestampUtc = first.TimestampUtc;
        Near(first.ApproachGateSeconds, second.ApproachGateSeconds, 0,
            "fixture deliberately shares the default gate instead of using it as an episode clock");
        LandingReplayTrack.TryCreate(first, out var firstTrack);
        LandingReplayTrack.TryCreate(second, out var secondTrack);
        var firstTimes = LandingReplayContacts.ContactTimes(first, firstTrack!, new[] { first, second });
        var secondTimes = LandingReplayContacts.ContactTimes(second, secondTrack!, new[] { first, second });
        Require(firstTimes.Count == 2 && secondTimes.Count == 2, "both contact clocks map into either selected scene");
        Near(firstTimes[0], 0, 1e-10, "selected first contact zero");
        Near(firstTimes[1], 3, 1e-10, "second contact mapped from identical bracketing pose samples");
        Near(secondTimes[0], -3, 1e-10, "first contact mapped backwards from the selected second contact");
        Near(secondTimes[1], 0, 1e-10, "selected second contact zero");

        second.TimestampUtc = first.TimestampUtc.AddMinutes(1);
        Require(LandingReplayContacts.ContactTimes(first, firstTrack!, new[] { second }).Count == 1,
            "identical poses from a different episode must not add a contact");
        second.TimestampUtc = first.TimestampUtc;
        second.Series.RemoveAll(p => p.TimeSeconds > -.6 && p.TimeSeconds < .2);
        Require(LandingReplayContacts.ContactTimes(first, firstTrack!, new[] { second }).Count == 1,
            "a contact zero inside missing motion has no usable clock anchor");
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

    private static void FactoryContactClocksSurviveIndependentSampling()
    {
        var records = FactoryContacts().Select(RoundTrip).ToArray();
        foreach (var selected in records)
        {
            Near(selected.ApproachGateSeconds, -15, 0, "native fixture uses each contact's fallback window");
            Require(LandingReplayTrack.TryCreate(selected, out var track), "native factory contact track");
            var clocks = records.Select(record => record.EstimatedContactTimeSeconds).ToArray();
            foreach (var record in records) record.EstimatedContactTimeSeconds = null;
            Require(LandingReplayContacts.ContactTimes(selected, track!, records).Count == 1,
                "fixture must reproduce independently downsampled contacts with no shared bracketing poses");
            for (var i = 0; i < records.Length; i++) records[i].EstimatedContactTimeSeconds = clocks[i];

            var times = LandingReplayContacts.ContactTimes(selected, track!, records);
            Require(times.Count == 2, "original clocks map both native contacts despite downsampling phase mismatch");
            var gear = new ReplayLandingGear();
            var registration = new ReplayGearRegistration(gear, track!, LandingReplaySurface.Build(track!, null), 0,
                contactTimesSeconds: times);
            foreach (var record in records)
            {
                var time = record.EstimatedContactTimeSeconds!.Value - selected.EstimatedContactTimeSeconds!.Value;
                Require(times.Any(value => Math.Abs(value - time) < 1e-10), "mapped time is the original clock difference");
                Require(track!.TrySample(time, out var pose), "mapped native contact pose");
                registration.Update(pose);
                Near(gear.Legs.Min(leg => registration.ClearanceAt(leg, pose)), 0, 1e-8,
                    "lowest tyre touches at either native analysed contact");
            }
            var bounceTime = 101.8 - selected.EstimatedContactTimeSeconds!.Value;
            Require(track!.TrySample(bounceTime, out var bounce), "native bounce remains recorded");
            registration.Update(bounce);
            Require(gear.Legs.Min(leg => registration.ClearanceAt(leg, bounce)) > .5,
                "clock mapping does not flatten the recorded bounce");
        }

        var first = records[0];
        var second = records[1];
        LandingReplayTrack.TryCreate(first, out var firstTrack);
        second.TimestampUtc = first.TimestampUtc.AddMinutes(1);
        Require(LandingReplayContacts.ContactTimes(first, firstTrack!, records).Count == 1,
            "clocks do not bypass episode identity");
        second.TimestampUtc = first.TimestampUtc;
        second.EstimatedContactTimeSeconds = 10000;
        Require(LandingReplayContacts.ContactTimes(first, firstTrack!, records).Count == 1,
            "out-of-track contact clocks must not clamp to an endpoint");
        second.EstimatedContactTimeSeconds = 103.2;
        first.Series.RemoveAll(point => point.TimeSeconds > 2.8 && point.TimeSeconds < 3.6);
        LandingReplayTrack.TryCreate(first, out firstTrack);
        Require(LandingReplayContacts.ContactTimes(first, firstTrack!, records).Count == 1,
            "recorded clocks do not bridge missing selected-scene motion");
    }

    private static void ContactClockSerializationAndInvalidValues()
    {
        var legacy = FactoryContacts()[0];
        legacy.EstimatedContactTimeSeconds = null;
        var serializer = new DataContractJsonSerializer(typeof(LandingRecordFile));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, LandingRecordFile.FromRecord(legacy));
            Require(!Encoding.UTF8.GetString(stream.ToArray()).Contains("EstimatedContactTimeSeconds"),
                "older records omit the optional contact clock without migration");
            stream.Position = 0;
            Require(((LandingRecordFile)serializer.ReadObject(stream)!).ToRecord().EstimatedContactTimeSeconds == null,
                "absent legacy contact clock must not become a fabricated zero");
        }
        legacy.EstimatedContactTimeSeconds = 0;
        Require(RoundTrip(legacy).EstimatedContactTimeSeconds == 0, "a real telemetry-clock zero survives serialization");

        var records = FactoryContacts();
        Require(LandingRepository.CreateSummary(records[0]).EstimatedContactTimeSeconds == 100,
            "summary copy preserves original contact clock");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, LandingRecord.NonFiniteStorageSentinel })
        {
            records[1].EstimatedContactTimeSeconds = invalid;
            var restored = new[] { records[0], RoundTrip(records[1]) };
            LandingReplayTrack.TryCreate(restored[0], out var track);
            Require(LandingReplayContacts.ContactTimes(restored[0], track!, restored).Count == 1,
                "nonfinite or sentinel contact clocks cannot become scene anchors");
            var invalidFactory = LandingRecordFactory.Create(new TouchdownResult { EstimatedContactTimeSeconds = invalid },
                Array.Empty<TelemetrySample>(), "Fixture", "Airplane");
            if (invalid != LandingRecord.NonFiniteStorageSentinel)
                Require(invalidFactory.EstimatedContactTimeSeconds == null, "factory does not persist nonfinite contact clocks");
        }
    }

    private static LandingRecord RoundTrip(LandingRecord record)
    {
        var serializer = new DataContractJsonSerializer(typeof(LandingRecordFile));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, LandingRecordFile.FromRecord(record));
        stream.Position = 0;
        var restored = ((LandingRecordFile)serializer.ReadObject(stream)!).ToRecord();
        Require(restored.FormatVersion == 7, "contact clock remains an optional compatible v7 field");
        return restored;
    }

    private static LandingRecord[] FactoryContacts()
    {
        var samples = new List<TelemetrySample>();
        for (var i = 0; i <= 1200; i++)
        {
            var time = 80 + i * .02;
            var height = time < 100 ? 5.2 + (100 - time) * .2 : time < 101 ? 5.2 :
                time < 102 ? 6.2 : time < 103.20 ? 5.2 + (103.20 - time) * .4 : 5.2;
            samples.Add(new TelemetrySample
            {
                SimulationTimeSeconds = time, LatitudeDegrees = 42 + (time - 80) * .0005, LongitudeDegrees = 23,
                MotionSimulation = true,
                GroundAltitudeFeet = 1000, PlaneAltitudeFeet = 1000 + height / .3048, AboveGroundLevelFeet = height / .3048,
                HeadingTrueDegrees = 0, PitchDegrees = -5, BankDegrees = -1, GForce = 1,
                OnGround = time >= 100 && time <= 101 || time >= 103.20,
            });
        }
        var timestamp = new DateTime(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc);
        return new[] { 100d, 103.20 }.Select((time, index) => LandingRecordFactory.Create(
            new TouchdownResult { ContactNumber = index + 1, EstimatedContactTimeSeconds = time, SimulationTimeSeconds = time },
            samples, "Fixture", "Airplane", contactCount: 2, timestampUtc: timestamp, episodeEndSimulationTimeSeconds: 104)).ToArray();
    }
    private static void Near(double actual, double expected, double tolerance, string message) => Require(Math.Abs(actual - expected) <= tolerance, message + ": " + actual);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
