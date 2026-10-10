using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using LandingStats.Core;

namespace LandingStats.App.Models;

internal static class LandingReplayContacts
{
    internal static IReadOnlyList<LandingRecord> ForEpisode(LandingRecord selected, IEnumerable<LandingRecord> records)
    {
        // Native contacts share the episode's UTC timestamp. Do not guess from
        // proximity in time, airport names or extra OnGround transitions.
        return new[] { selected }.Concat(records.Where(r => selected.ContactCount > 1 &&
                r.Id != selected.Id && r.TimestampUtc == selected.TimestampUtc &&
                r.Simulator == selected.Simulator && r.AircraftTitle == selected.AircraftTitle &&
                r.AircraftType == selected.AircraftType && r.ContactCount == selected.ContactCount &&
                r.ContactNumber >= 1 && r.ContactNumber <= selected.ContactCount && r.ContactNumber != selected.ContactNumber))
            .GroupBy(r => r.Id).Select(g => g.First()).GroupBy(r => r.ContactNumber)
            .Where(g => g.Count() == 1).Select(g => g.Single()).OrderBy(r => r.ContactNumber).ToArray();
    }

    internal static IReadOnlyList<Point3D> GroundPositions(LandingRecord selected, LandingReplayTrack track, IEnumerable<LandingRecord> records, TelemetryDecisionJournal? journal = null)
    {
        var result = new List<Point3D>();
        var contacts = ForEpisode(selected, records);
        journal?.Record(0, "replay.contacts", "episode_grouped", "Contact markers use unique contact numbers sharing the recorded episode identity, never incidental on-ground transitions.",
            new Dictionary<string, double> { ["expected_contacts"] = selected.ContactCount, ["matched_contacts"] = contacts.Count });
        foreach (var record in contacts)
        {
            var contactTrack = track;
            if (record.Id != selected.Id && !LandingReplayTrack.TryCreate(record, out contactTrack))
            { journal?.Record(0, "replay.contacts", "marker_missing_pose", "Contact has no usable recorded position track.", new Dictionary<string, double> { ["marker_contact_number"] = record.ContactNumber }); continue; }
            if (contactTrack!.StartSeconds > 0 || contactTrack.EndSeconds < 0 || !contactTrack.TrySample(0, out var pose))
            { journal?.Record(0, "replay.contacts", "marker_missing_contact", "Analysed contact falls outside the recorded track or inside a motion gap.", new Dictionary<string, double> { ["marker_contact_number"] = record.ContactNumber }); continue; }
            journal?.Record(0, "replay.contacts", "marker_selected", "Contact marker uses the exact analysed zero, interpolated from recorded position and projected into this scene.",
                new Dictionary<string, double> { ["marker_contact_number"] = record.ContactNumber });
            if (record.Id == selected.Id) { result.Add(pose.Ground); continue; }
            // Each record has its own t=0 and local origin. Interpolate that
            // exact analysed contact, then project it into the selected scene.
            var originLongitude = contactTrack.Frames[0].Sample.LongitudeDegrees!.Value;
            var longitude = originLongitude + pose.Value(p => (p.LongitudeDegrees!.Value - originLongitude + 540) % 360 - 180);
            result.Add(track.ProjectGeo(pose.Value(p => p.LatitudeDegrees!.Value), (longitude + 540) % 360 - 180,
                pose.Value(p => p.GroundAltitudeFeet!.Value)));
        }
        return result;
    }

    internal static IReadOnlyList<double> ContactTimes(LandingRecord selected, LandingReplayTrack track,
        IEnumerable<LandingRecord> records, TelemetryDecisionJournal? journal = null)
    {
        var times = new List<double> { 0 };
        foreach (var record in ForEpisode(selected, records).Where(r => r.Id != selected.Id))
        {
            if (!LandingReplayTrack.TryCreate(record, out var other) || other!.StartSeconds > 0 ||
                other.EndSeconds < 0 || !other.TrySample(0, out _)) continue;
            if (selected.EstimatedContactTimeSeconds is double selectedClock &&
                record.EstimatedContactTimeSeconds is double contactClock &&
                LandingReplayTrack.Finite(selectedClock) && LandingReplayTrack.Finite(contactClock) &&
                selectedClock != LandingRecord.NonFiniteStorageSentinel && contactClock != LandingRecord.NonFiniteStorageSentinel)
            {
                // Per-contact windows can retain different downsampling phases.
                // Their original contact clocks map zero without matching poses.
                var contactTime = contactClock - selectedClock;
                if (contactTime >= track.StartSeconds && contactTime <= track.EndSeconds &&
                    track.TrySample(contactTime, out _)) times.Add(contactTime);
                continue;
            }
            var before = other.Frames.Last(f => f.TimeSeconds <= 0);
            var after = other.Frames.First(f => f.TimeSeconds >= 0);
            // Legacy records have no original clock. Use shared exact samples
            // only when present, never nearest positions or the 500-ft gate
            // (which can use a per-contact fallback).
            var left = SharedFrame(before.Sample);
            var right = SharedFrame(after.Sample);
            if (left == null || right == null || right.TimeSeconds < left.TimeSeconds ||
                right.TimeSeconds - left.TimeSeconds > LandingReplayTrack.MaximumInterpolationGapSeconds)
            {
                journal?.Record(0, "replay.contacts", "contact_clock_unavailable", "Contact has no unambiguous shared pose samples around its analysed zero.",
                    new Dictionary<string, double> { ["marker_contact_number"] = record.ContactNumber });
                continue;
            }
            var fraction = before.TimeSeconds == after.TimeSeconds ? 0 :
                -before.TimeSeconds / (after.TimeSeconds - before.TimeSeconds);
            var time = left.TimeSeconds + (right.TimeSeconds - left.TimeSeconds) * fraction;
            if (track.TrySample(time, out _)) times.Add(time);
        }
        return times.Distinct().OrderBy(t => t).ToArray();

        LandingReplayFrame? SharedFrame(LandingSeriesPoint point)
        {
            var matches = track.Frames.Where(f => f.Sample.LatitudeDegrees == point.LatitudeDegrees &&
                f.Sample.LongitudeDegrees == point.LongitudeDegrees && f.Sample.PlaneAltitudeFeet == point.PlaneAltitudeFeet &&
                f.Sample.GroundAltitudeFeet == point.GroundAltitudeFeet && f.Sample.HeadingTrueDegrees == point.HeadingTrueDegrees &&
                f.Sample.PitchDegrees == point.PitchDegrees && f.Sample.BankDegrees == point.BankDegrees &&
                f.Sample.OnGround == point.OnGround).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }
}
