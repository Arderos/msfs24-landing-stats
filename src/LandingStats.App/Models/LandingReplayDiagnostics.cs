using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.Models;

/// <summary>Runs the same replay decisions off the UI thread, even if no replay tab is opened.</summary>
internal static class LandingReplayDiagnostics
{
    internal static void Record(LandingRecord record, IEnumerable<LandingRecord> contacts, TelemetryDecisionJournal journal)
        => Append(new[] { record }, contacts.ToArray(), journal);

    internal static void RecordAll(IReadOnlyList<LandingRecord> contacts, TelemetryDecisionJournal journal)
        => Append(contacts, contacts, journal);

    private static void Append(IReadOnlyList<LandingRecord> records, IReadOnlyList<LandingRecord> contacts, TelemetryDecisionJournal journal)
    {
        var snapshots = records.Select(record => Capture(record, contacts)).ToArray();
        var bindingOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        var owners = new int[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            var canonical = LandingReplayContacts.ForEpisode(records[i], contacts)
                .FirstOrDefault(c => !c.IsSummaryOnly && c.Series.Count >= 2) ?? records[i];
            // Different catalogue windows may contain source generations absent
            // from the first contact. Retain their explicitly scoped decisions.
            var ownerKey = records[i].ControllerTimeline == null ? canonical.Id : records[i].Id;
            if (!bindingOwners.TryGetValue(ownerKey, out var owner)) bindingOwners[ownerKey] = owner = i;
            owners[i] = owner;
        }
        // Cover every contact before spending the remaining shared budget on
        // verbose candidate details from the first contacts.
        for (var i = 0; i < records.Count; i++)
        {
            var snapshot = snapshots[i];
            journal.Record(0, "replay", "contact_summary", "Replay diagnostics completed for this contact; counts include any local truncation.",
                new Dictionary<string, double>
                {
                    ["contact_number"] = records[i].ContactNumber, ["relative_contact_time"] = 1,
                    ["contact_time_seconds"] = records[i].EstimatedContactTimeSeconds ?? 0,
                    ["entries"] = snapshot.Entries.Count, ["dropped_entries"] = snapshot.DroppedEntryCount,
                    ["track_available"] = snapshot.Entries.Any(e => e.Code == "track_created") ? 1 : 0,
                    ["gear_registered"] = snapshot.Entries.Any(e => e.Code == "contact_registered") ? 1 : 0,
                    ["failed"] = snapshot.Entries.Any(e => e.Code == "diagnostics_failed") ? 1 : 0,
                });
            if (owners[i] != i)
                journal.Record(0, "replay.inputs", "binding_diagnostics_reference", "The same episode binding was evaluated from the same canonical record; its candidate details are stored once.",
                    new Dictionary<string, double>
                    {
                        ["contact_number"] = records[i].ContactNumber, ["relative_contact_time"] = 1,
                        ["contact_time_seconds"] = records[i].EstimatedContactTimeSeconds ?? 0,
                        ["binding_diagnostics_contact_number"] = records[owners[i]].ContactNumber,
                        ["binding_diagnostics_contact_time_seconds"] = records[owners[i]].EstimatedContactTimeSeconds ?? 0,
                    });
        }
        foreach (var essential in new[] { true, false })
        for (var i = 0; i < records.Count; i++)
        foreach (var entry in snapshots[i].Entries.Where(e => Essential(e) == essential))
        {
            if (owners[i] != i && entry.Component == "replay.inputs" && entry.Code != "invalid_clock" && !entry.Code.StartsWith("episode_", StringComparison.Ordinal)) continue;
            var details = new Dictionary<string, double>
            {
                ["contact_number"] = records[i].ContactNumber, ["relative_contact_time"] = 1,
                ["contact_time_seconds"] = records[i].EstimatedContactTimeSeconds ?? 0,
            };
            foreach (var detail in entry.Details) details[detail.Key] = detail.Value;
            journal.Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, details);
        }
    }

    private static bool Essential(TelemetryDecisionEntry entry) =>
        entry.Component != "replay.inputs" || entry.Code == "axis_selected" || entry.Code == "role_unavailable" || entry.Code == "joint_assignment";

    private static TelemetryDecisionSnapshot Capture(LandingRecord record, IEnumerable<LandingRecord> contacts)
    {
        var replay = new TelemetryDecisionJournal();
        try
        {
            ReplayControlInputs.Resolve(record, contacts, replay);
            if (!LandingReplayTrack.TryCreate(record, out var track, replay)) return replay.Snapshot();
            LandingReplayRunwayOffset.TryCreate(record, track!, out _, replay);
            var offset = ReplayGearRegistrationModel.VisualOffset(track!, replay);
            var surface = ReplaySurfaceGeometry.Build(track!, record.RunwayGeometry, replay);
            if (record.RunwayGeometry != null) RunwayMarkingModel.Build(record.RunwayGeometry, replay);
            var gear = new ReplayGearRegistrationModel(new ReplayGearState(), track!, surface, offset, replay,
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
        catch (Exception exception)
        {
            replay.Record(0, "replay", "diagnostics_failed", exception.GetType().Name);
        }
        return replay.Snapshot();
    }
}
