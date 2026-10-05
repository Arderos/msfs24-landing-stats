using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

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

    internal static IReadOnlyList<Point3D> GroundPositions(LandingRecord selected, LandingReplayTrack track, IEnumerable<LandingRecord> records)
    {
        var result = new List<Point3D>();
        foreach (var record in ForEpisode(selected, records))
        {
            var contactTrack = track;
            if (record.Id != selected.Id && !LandingReplayTrack.TryCreate(record, out contactTrack)) continue;
            if (contactTrack!.StartSeconds > 0 || contactTrack.EndSeconds < 0 || !contactTrack.TrySample(0, out var pose)) continue;
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
}
