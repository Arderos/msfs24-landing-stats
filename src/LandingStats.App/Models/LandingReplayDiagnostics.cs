using System.Collections.Generic;
using LandingStats.App.Controls;
using LandingStats.Core;

namespace LandingStats.App.Models;

/// <summary>Runs the same replay decisions off the UI thread, even if no replay tab is opened.</summary>
internal static class LandingReplayDiagnostics
{
    internal static void Record(LandingRecord record, IEnumerable<LandingRecord> contacts, TelemetryDecisionJournal journal)
    {
        var replay = new TelemetryDecisionJournal();
        try
        {
            ReplayControlInputs.Resolve(record, replay);
            if (!LandingReplayTrack.TryCreate(record, out var track, replay)) return;
            LandingReplayRunwayOffset.TryCreate(record, track!, out _, replay);
            var offset = ReplayGearRegistration.VisualOffset(track!, replay);
            var surface = LandingReplaySurface.Build(track!, record.RunwayGeometry, replay);
            var gear = new ReplayGearRegistration(new ReplayLandingGear(), track!, surface, offset, replay,
                LandingReplayContacts.ContactTimes(record, track!, contacts, replay));
            foreach (var frame in track!.Frames)
                if (track.TrySample(frame.TimeSeconds, out var pose)) gear.Update(pose);
            gear.RecordFrameDecisions();
            foreach (var marker in LandingReplayContacts.GroundPositions(record, track!, contacts, replay))
            {
                var height = surface.HeightAt(marker.X, marker.Z);
                replay.Record(0, "replay.contacts", height.HasValue ? "marker_surface_height" : "marker_ground_height",
                    height.HasValue ? "Marker lies on rendered runway/ground surface." : "No rendered surface at marker; retain recorded ground altitude.",
                    new Dictionary<string, double> { ["east_m"] = marker.X, ["south_m"] = marker.Z, ["height_m"] = height ?? marker.Y });
            }
        }
        finally
        {
            var snapshot = replay.Snapshot();
            foreach (var entry in snapshot.Entries)
            {
                var details = new Dictionary<string, double>
                {
                    ["contact_number"] = record.ContactNumber,
                    ["relative_contact_time"] = 1,
                };
                foreach (var detail in entry.Details) details[detail.Key] = detail.Value;
                journal.Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, details);
            }
            if (snapshot.Truncated)
                journal.Record(0, "replay", "journal_truncated", "Replay decision journal reached its bounded diagnostic capacity.",
                    new Dictionary<string, double> { ["contact_number"] = record.ContactNumber, ["relative_contact_time"] = 1 });
        }
    }
}
