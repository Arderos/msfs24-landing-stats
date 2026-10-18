using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.ReplayMath;
using LandingStats.Core;

namespace LandingStats.App.Models;

/// <summary>
/// Horizontal aircraft-reference distance from the facility's extended runway
/// centreline. Positive is right in the selected landing direction, not camera
/// right or the instantaneous nose direction. No runway means no measurement.
/// </summary>
internal sealed class LandingReplayRunwayOffset
{
    private readonly Point3D _center;
    private readonly Vector3D _right;

    private LandingReplayRunwayOffset(Point3D center, Vector3D right)
    { _center = center; _right = right; }

    internal double HeadingTrueDegrees => (Math.Atan2(_right.Z, _right.X) * 180 / Math.PI + 360) % 360;

    public static bool TryCreate(LandingRecord record, LandingReplayTrack track, out LandingReplayRunwayOffset? offset, TelemetryDecisionJournal? journal = null)
    {
        offset = null;
        var runway = record.RunwayGeometry;
        if (runway == null || !runway.HasValidGeometry || runway.IsWater)
        { journal?.Record(0, "replay.runway_offset", "no_runway_reference", "No valid non-water runway; lateral offset and runway-relative yaw are unavailable."); return false; }
        var angle = runway.HeadingTrueDegrees * Math.PI / 180;
        var forward = new Vector3D(Math.Sin(angle), 0, -Math.Cos(angle));
        var right = new Vector3D(Math.Cos(angle), 0, Math.Sin(angle));
        var primary = Matches(record.Runway, runway.PrimaryName);
        var secondary = Matches(record.Runway, runway.SecondaryName);
        if (primary && secondary)
        { journal?.Record(0, "replay.runway_offset", "ambiguous_end", "Recorded designation matches both runway ends."); return false; }
        if (secondary) right = -right;
        else if (!primary)
        {
            // Old/imported records may retain geometry without an end name.
            // Choose the end once from touchdown ground motion, never a crabbed
            // heading or a turn made later during rollout.
            var frames = track.Frames.Where(f => f.TimeSeconds >= -2 && f.TimeSeconds <= .5).ToArray();
            if (frames.Length < 2)
            { journal?.Record(0, "replay.runway_offset", "insufficient_motion", "Unnamed runway end cannot be inferred from fewer than two nearby contact frames."); return false; }
            for (var i = 1; i < frames.Length; i++)
                if (frames[i].TimeSeconds - frames[i - 1].TimeSeconds > LandingReplayTrack.MaximumInterpolationGapSeconds)
                { journal?.Record(0, "replay.runway_offset", "motion_gap", "A contact-motion gap prevents inference of an unnamed runway end."); return false; }
            var elapsed = frames[frames.Length - 1].TimeSeconds - frames[0].TimeSeconds;
            var motion = frames[frames.Length - 1].Position - frames[0].Position;
            motion.Y = 0;
            var distance = motion.Length;
            if (elapsed < .2 || distance < 5 || distance / elapsed > 200)
            { journal?.Record(0, "replay.runway_offset", "unsupported_motion", "Recorded contact motion is too short, stationary or implausible to infer the runway end.", new Dictionary<string, double> { ["elapsed_seconds"] = elapsed, ["distance_m"] = distance }); return false; }
            var along = Vector3D.DotProduct(motion, forward);
            if (Math.Abs(along) / distance < Math.Cos(20 * Math.PI / 180))
            { journal?.Record(0, "replay.runway_offset", "motion_not_aligned", "Recorded motion is not aligned enough to infer an unnamed runway end."); return false; }
            if (along < 0) right = -right;
        }
        var center = track.ProjectGeo(runway.LatitudeDegrees, runway.LongitudeDegrees,
            runway.AltitudeMeters / LandingReplayTrack.FeetToMeters);
        offset = new LandingReplayRunwayOffset(center, right);
        journal?.Record(0, "replay.runway_offset", primary || secondary ? "named_end_selected" : "motion_end_selected",
            "Positive lateral distance means right in the selected landing direction; runway-relative yaw uses this same fixed heading.",
            new Dictionary<string, double> { ["heading_true_deg"] = offset.HeadingTrueDegrees });
        return true;
    }

    public bool TryGetMeters(LandingReplayPose pose, out double meters)
    {
        meters = Vector3D.DotProduct(pose.Position - _center, _right);
        return LandingReplayTrack.Finite(meters);
    }

    private static bool Matches(string? selected, string? name) =>
        !string.IsNullOrWhiteSpace(selected) && !string.IsNullOrWhiteSpace(name) &&
        string.Equals(selected!.Trim(), name!.Trim(), StringComparison.OrdinalIgnoreCase);
}
