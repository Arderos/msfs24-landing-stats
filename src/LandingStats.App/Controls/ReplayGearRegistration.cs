using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Controls;

/// <summary>
/// Fits only the symbolic landing gear to the recorded contact. Aircraft position,
/// orientation, trail and contact time are never changed to make the tyres touch.
/// This is visual suspension, not a measurement of the installed aircraft's struts.
/// </summary>
internal sealed class ReplayGearRegistration
{
    private const double MinimumExtension = -1.2;
    private const double MaximumExtension = 6;
    private readonly ReplayLandingGear _gear;
    private readonly LandingReplayTrack _track;
    private readonly LandingReplaySurface _surface;
    private readonly double _visualOffset;
    private readonly double _contactGroundTime;
    private readonly List<KeyValuePair<double, double>> _contactSupports = new List<KeyValuePair<double, double>>();
    private readonly List<AirSpan> _airSpans = new List<AirSpan>();
    private readonly TelemetryDecisionJournal? _journal;
    private readonly Dictionary<string, double>? _frameDecisions;

    internal double UnloadedExtension { get; }
    internal bool IsCalibrated { get; }

    internal static double VisualOffset(LandingReplayTrack track, TelemetryDecisionJournal? journal = null)
    {
        var grounded = track.Frames.Where(f => f.Sample.OnGround && Math.Abs(f.Sample.PitchDegrees) < 2)
            .Select(f => f.Position.Y - f.Ground.Y).OrderBy(h => h).ToArray();
        var offset = grounded.Length > 0 ? ReplayAircraftModel.RestHeight - grounded[grounded.Length / 2] : 0;
        journal?.Record(0, "replay.gear", grounded.Length > 0 ? "visual_datum_registered" : "visual_datum_unavailable",
            "Generic-model rest-height registration is constant for the whole replay; it never clamps recorded motion or erases bounces.",
            new Dictionary<string, double> { ["grounded_samples"] = grounded.Length, ["visual_offset_m"] = offset });
        return offset;
    }

    internal ReplayGearRegistration(ReplayLandingGear gear, LandingReplayTrack track, LandingReplaySurface surface, double visualOffset,
        TelemetryDecisionJournal? journal = null, IEnumerable<double>? contactTimesSeconds = null)
    {
        _gear = gear; _track = track; _surface = surface; _visualOffset = visualOffset; _journal = journal;
        if (journal != null) _frameDecisions = new Dictionary<string, double>();
        // The analysed contact may precede the first stored OnGround frame by a
        // fraction of the sampling interval. That known interval belongs to this
        // contact; do not shift the contact to the delayed boolean transition.
        _contactGroundTime = track.Frames.Where(f => f.TimeSeconds >= 0 && f.TimeSeconds <= LandingReplayTrack.MaximumInterpolationGapSeconds && f.Sample.OnGround)
            .Select(f => f.TimeSeconds).DefaultIfEmpty(double.NaN).First();
        if (!LandingReplayTrack.Finite(_contactGroundTime) || track.StartSeconds > 0 || track.EndSeconds < 0 || !track.TrySample(0, out var contact))
        { journal?.Record(0, "replay.gear", "contact_registration_unavailable", "Exact analysed contact or nearby recorded ground support is unavailable; generic gear remains uncalibrated."); return; }
        var matrix = Matrix(contact);
        double Clearance(double extension) => gear.Legs.Min(leg => ClearanceAt(leg, matrix, contact, extension));
        if (!TrySolve(Clearance, MinimumExtension, MaximumExtension, out var unloaded))
        { journal?.Record(0, "replay.gear", "extension_outside_bounds", "No supported symbolic strut extension can place the lowest tyre on the displayed surface."); return; }
        UnloadedExtension = unloaded; IsCalibrated = true;
        journal?.Record(0, "replay.gear", "contact_registered", "Only generic symbolic suspension is fitted; aircraft position, attitude and analysed contact time stay unchanged.",
            new Dictionary<string, double> { ["unloaded_extension_m"] = unloaded, ["ground_flag_delay_seconds"] = _contactGroundTime });
        foreach (var time in (contactTimesSeconds ?? Array.Empty<double>()).Append(0).Where(LandingReplayTrack.Finite).Distinct().OrderBy(t => t))
        {
            if (time < track.StartSeconds || time > track.EndSeconds || !track.TrySample(time, out _)) continue;
            var previous = time;
            foreach (var frame in track.Frames.Where(f => f.TimeSeconds >= time))
            {
                if (frame.TimeSeconds - time > LandingReplayTrack.MaximumInterpolationGapSeconds ||
                    frame.TimeSeconds - previous > LandingReplayTrack.MaximumInterpolationGapSeconds) break;
                if (frame.Sample.OnGround)
                {
                    _contactSupports.Add(new KeyValuePair<double, double>(time, frame.TimeSeconds));
                    journal?.Record(time, "replay.gear", "contact_support_registered", "Analysed contact starts visual ground support before its delayed stored ground flag.",
                        new Dictionary<string, double> { ["ground_flag_delay_seconds"] = frame.TimeSeconds - time });
                    break;
                }
                previous = frame.TimeSeconds;
            }
        }
        BuildAirSpans();
    }

    internal void Update(LandingReplayPose pose)
    {
        var matrix = Matrix(pose);
        var grounded = IsGrounded(pose.TimeSeconds);
        var baseline = AirExtension(pose.TimeSeconds);
        CountDecision(IsCalibrated ? grounded ? "grounded_frames" : "airborne_frames" : "uncalibrated_frames");
        if (IsCalibrated && !grounded)
        {
            // A generic wheelbase is not the recorded aircraft (especially a
            // flying boat). Never let its illustrative tyres invent an earlier
            // contact. Adjust only the strut datum, with sub-centimetre clearance
            // tending to zero at the phase boundary; the body/bounce arc is intact.
            var margin = AirMargin(pose.TimeSeconds);
            if (_gear.Legs.Min(leg => ClearanceAt(leg, matrix, pose, baseline)) < margin)
            {
                if (TrySolve(e => _gear.Legs.Min(leg => ClearanceAt(leg, matrix, pose, e)) - margin,
                    MinimumExtension, baseline, out var raised)) { baseline = raised; CountDecision("air_clearance_adjusted"); }
                else CountDecision("air_clearance_unresolved");
            }
        }
        if (IsCalibrated && grounded && _gear.Legs.Min(leg => ClearanceAt(leg, matrix, pose, baseline)) > .00001)
        {
            if (TrySolve(e => _gear.Legs.Min(leg => ClearanceAt(leg, matrix, pose, e)), baseline, MaximumExtension, out var extended))
            { baseline = extended; CountDecision("ground_extension_adjusted"); }
            else CountDecision("ground_extension_unresolved");
        }
        for (var index = 0; index < _gear.Legs.Count; index++)
        {
            var leg = _gear.Legs[index];
            var extension = baseline;
            // A banked touchdown does not force the high main or the nose down.
            // Only compress a leg whose extended tyre would penetrate the surface.
            if (IsCalibrated && grounded && ClearanceAt(leg, matrix, pose, extension) < 0)
            {
                if (TrySolve(e => ClearanceAt(leg, matrix, pose, e), MinimumExtension, extension, out var compressed))
                { extension = compressed; CountDecision(index == 0 ? "left_main_compressed" : index == 1 ? "right_main_compressed" : "nose_compressed"); }
                else CountDecision("compression_unresolved");
            }
            leg.SetExtension(extension);
        }
    }

    private void CountDecision(string code)
    {
        if (_frameDecisions == null) return;
        _frameDecisions.TryGetValue(code, out var count);
        _frameDecisions[code] = count + 1;
    }

    internal void RecordFrameDecisions()
    {
        _journal?.Record(0, "replay.gear", "frame_decision_counts",
            "Actual suspension update decisions aggregated over recorded frames: airborne clearance, ground extension and independent leg compression; recorded pose is never changed.",
            _frameDecisions);
    }

    private void BuildAirSpans()
    {
        var frames = _track.Frames;
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].Sample.OnGround) continue;
            var first = i;
            while (i + 1 < frames.Count && !frames[i + 1].Sample.OnGround) i++;
            if (i + 1 == frames.Count)
            { _journal?.Record(frames[first].TimeSeconds, "replay.gear", "air_span_open", "No later contact; keep unloaded flight gear."); continue; }
            var continuous = true;
            for (var j = Math.Max(1, first); j <= i + 1; j++)
                if (frames[j].TimeSeconds - frames[j - 1].TimeSeconds > LandingReplayTrack.MaximumInterpolationGapSeconds) { continuous = false; break; }
            if (!continuous)
            { _journal?.Record(frames[first].TimeSeconds, "replay.gear", "air_span_gap", "Do not infer visual suspension across missing motion."); continue; }
            var from = first > 0 ? frames[first - 1].TimeSeconds : frames[first].TimeSeconds;
            var to = i + 1 < frames.Count ? frames[i + 1].TimeSeconds : frames[i].TimeSeconds;
            foreach (var support in _contactSupports)
                if (to == support.Value && from <= support.Key) { to = support.Key; break; }
            if (to <= from) continue;
            double ExtensionAt(double time)
            {
                if (!_track.TrySample(time, out var pose)) return UnloadedExtension;
                var matrix = Matrix(pose);
                return TrySolve(e => _gear.Legs.Min(leg => ClearanceAt(leg, matrix, pose, e)),
                    MinimumExtension, MaximumExtension, out var extension) ? extension : UnloadedExtension;
            }
            var end = ExtensionAt(to);
            var start = first > 0 ? ExtensionAt(from) : end;
            _airSpans.Add(new AirSpan(from, to, start, end));
            _journal?.Record(from, "replay.gear", "air_span_registered", "Interpolate symbolic strut datum between support phases while preserving recorded airborne motion.",
                new Dictionary<string, double> { ["from_seconds"] = from, ["to_seconds"] = to, ["start_extension_m"] = start, ["end_extension_m"] = end });
        }
    }

    private double AirExtension(double time)
    {
        // Between support phases only interpolate the symbolic strut datum.
        // The aircraft's actual airborne arc is neither clamped nor flattened.
        foreach (var span in _airSpans)
            if (time >= span.From && time <= span.To)
                return span.Start + (span.End - span.Start) * (time - span.From) / (span.To - span.From);
        return UnloadedExtension;
    }

    private double AirMargin(double time)
    {
        foreach (var span in _airSpans)
            if (time >= span.From && time <= span.To)
                return Math.Min(.01, Math.Max(0, Math.Min(time - span.From, span.To - time)) * .2);
        return .01;
    }

    private readonly struct AirSpan
    {
        internal AirSpan(double from, double to, double start, double end) { From = from; To = to; Start = start; End = end; }
        internal double From { get; } internal double To { get; }
        internal double Start { get; } internal double End { get; }
    }

    internal double ClearanceAt(ReplayLandingGear.Leg leg, LandingReplayPose pose) => ClearanceAt(leg, Matrix(pose), pose, leg.Extension);

    private double ClearanceAt(ReplayLandingGear.Leg leg, Matrix3D matrix, LandingReplayPose pose, double extension)
    {
        var min = double.PositiveInfinity;
        foreach (var vertex in leg.TyrePoints)
        {
            var local = vertex; local.Y -= extension;
            var world = matrix.Transform(local);
            // Outside the displayed surface use the sampled ground datum only
            // for this symbolic gear, never to create extra scenery geometry.
            var height = _surface.HeightAt(world.X, world.Z) ?? pose.Ground.Y;
            min = Math.Min(min, world.Y - height);
        }
        return min;
    }

    private Matrix3D Matrix(LandingReplayPose pose)
    {
        var matrix = Matrix3D.Identity; matrix.Rotate(pose.Orientation);
        matrix.Translate(new Vector3D(pose.Position.X, pose.Position.Y + _visualOffset, pose.Position.Z)); return matrix;
    }

    private bool IsGrounded(double time)
    {
        foreach (var support in _contactSupports)
            if (time >= support.Key && time <= support.Value) return true;
        var frames = _track.Frames; var lo = 0; var hi = frames.Count - 1;
        while (lo < hi) { var middle = lo + (hi - lo + 1) / 2; if (frames[middle].TimeSeconds <= time) lo = middle; else hi = middle - 1; }
        return frames[lo].Sample.OnGround;
    }

    private static bool TrySolve(Func<double, double> clearance, double low, double high, out double result)
    {
        result = 0; var atLow = clearance(low); var atHigh = clearance(high);
        if (!LandingReplayTrack.Finite(atLow) || !LandingReplayTrack.Finite(atHigh) || atLow < 0 || atHigh > 0) return false;
        if (Math.Abs(atLow) < 1e-7) { result = low; return true; }
        if (Math.Abs(atHigh) < 1e-7) { result = high; return true; }
        // On a planar patch clearance is linear in strut extension. A bracketed
        // secant reaches it in one step instead of querying every tyre 24 times;
        // the bracket still protects profile bends and changes of supporting tyre.
        for (var i = 0; i < 20; i++)
        {
            var fraction = atLow / (atLow - atHigh);
            var middle = low + (high - low) * Math.Max(.001, Math.Min(.999, fraction));
            var value = clearance(middle);
            if (!LandingReplayTrack.Finite(value)) return false;
            if (Math.Abs(value) < 1e-7) { result = middle; return true; }
            if (value > 0) { low = middle; atLow = value; } else { high = middle; atHigh = value; }
        }
        result = Math.Abs(atLow) <= Math.Abs(atHigh) ? low : high;
        return Math.Min(Math.Abs(atLow), Math.Abs(atHigh)) < .00001;
    }
}
