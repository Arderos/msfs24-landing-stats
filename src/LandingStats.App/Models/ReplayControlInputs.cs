using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.Models;

/// <summary>
/// Ranks physical axes against the recorded pilot commands. Correlation selects
/// the source and polarity, but never changes physical amplitude or sample time.
/// </summary>
public sealed class ReplayControlInputs
{
    public const double MaximumAgeSeconds = 0.25;
    // A strong, bounded preference for a two-axis stick/yoke, not a binding rule
    // or acceptance cutoff. The weight is a heuristic, not a probability.
    private const double SameDevicePreference = 0.5;
    private readonly LandingSeriesPoint[] _points;
    private readonly int[] _segments;

    private ReplayControlInputs(LandingSeriesPoint[] points)
    {
        _points = points;
        _segments = new int[points.Length];
        for (var i = 1; i < points.Length; i++)
            _segments[i] = _segments[i - 1] + (points[i].TimeSeconds - points[i - 1].TimeSeconds > LandingReplayTrack.MaximumInterpolationGapSeconds ||
                points[i].OnGround != points[i - 1].OnGround ? 1 : 0);
    }
    public ReplayControlAxis? Pitch { get; private set; }
    public ReplayControlAxis? Roll { get; private set; }
    public ReplayControlAxis? Yaw { get; private set; }

    public static ReplayControlInputs Resolve(LandingRecord? record, IEnumerable<LandingRecord> contacts, TelemetryDecisionJournal? journal = null)
    {
        if (record == null) return Resolve(record, journal);
        // Contact-local windows can have different lengths and downsampling
        // phases. Select once from the first available contact in the episode;
        // other contacts reuse its bindings, never rank their own competitors.
        var canonical = LandingReplayContacts.ForEpisode(record, contacts)
            .FirstOrDefault(contact => !contact.IsSummaryOnly && contact.Series.Count >= 2) ?? record;
        journal?.Record(0, "replay.inputs", "episode_binding_source", "All contacts reuse the first available contact's physical-axis selection.",
            new Dictionary<string, double> { ["binding_contact_number"] = canonical.ContactNumber, ["display_contact_number"] = record.ContactNumber });
        var bindings = Resolve(canonical, journal);
        if (ReferenceEquals(canonical, record)) return bindings;

        var points = new List<LandingSeriesPoint>();
        foreach (var point in record.Series)
        {
            if (!Finite(point.TimeSeconds) || (points.Count > 0 && point.TimeSeconds < points[points.Count - 1].TimeSeconds))
            { journal?.Record(point.TimeSeconds, "replay.inputs", "invalid_clock", "Input sampling is unavailable for invalid or reversed recorded time."); return new ReplayControlInputs(Array.Empty<LandingSeriesPoint>()); }
            if (points.Count > 0 && point.TimeSeconds == points[points.Count - 1].TimeSeconds) points[points.Count - 1] = point;
            else points.Add(point);
        }
        var result = new ReplayControlInputs(points.ToArray());
        result.Pitch = Remap(bindings.Pitch, 0);
        result.Roll = Remap(bindings.Roll, 1);
        result.Yaw = Remap(bindings.Yaw, 2);
        return result;

        ReplayControlAxis? Remap(ReplayControlAxis? axis, int role)
        {
            var legacy = record.RawControllerAxesSourceIndices.Count == 0;
            var sources = legacy ? record.RawControllerSourceIndices.Count > 0 ? record.RawControllerSourceIndices.ToArray() :
                Enumerable.Range(0, TelemetrySample.CapturedControllerCount).ToArray() : record.RawControllerAxesSourceIndices.ToArray();
            var source = axis == null ? -1 : legacy ? axis.SourceIndex / TelemetrySample.CapturedControllerAxisCount : axis.SourceIndex;
            var column = Array.IndexOf(sources, source);
            var mapped = axis != null && sources.Distinct().Count() == sources.Length && column >= 0 &&
                (!legacy || axis.SourceIndex % TelemetrySample.CapturedControllerAxisCount == 1) &&
                SameRecordedDevice(canonical, record, axis.SourceIndex / TelemetrySample.CapturedControllerAxisCount)
                ? new ReplayControlAxis(axis.SourceIndex, column, axis.SourceLabel, axis.Correlation, axis.LagSeconds, legacy) : null;
            journal?.Record(0, "replay.inputs", mapped == null ? "episode_axis_unavailable" : "episode_axis_mapped",
                mapped == null ? "The episode binding is absent or has conflicting provenance; no replacement axis is selected." :
                    "The episode binding maps to this contact's recorded column; physical amplitude, polarity and sample time are retained.",
                new Dictionary<string, double> { ["role"] = role, ["source_index"] = axis?.SourceIndex ?? -1, ["column"] = mapped?.Column ?? -1 });
            return mapped;
        }
    }

    private static bool SameRecordedDevice(LandingRecord canonical, LandingRecord selected, int device)
    {
        var first = device < canonical.ControlInputSources.Count ? canonical.ControlInputSources[device] : null;
        var second = device < selected.ControlInputSources.Count ? selected.ControlInputSources[device] : null;
        // Native source indices remain the provenance within a verified episode
        // for old files without names. Never infer a relocated device from a name.
        if (string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second)) return true;
        return !string.IsNullOrWhiteSpace(first) && string.Equals(first, second, StringComparison.Ordinal) &&
            canonical.ControlInputSources.Count(name => string.Equals(name, first, StringComparison.Ordinal)) == 1 &&
            selected.ControlInputSources.Count(name => string.Equals(name, first, StringComparison.Ordinal)) == 1;
    }

    public static ReplayControlInputs Resolve(LandingRecord? record, TelemetryDecisionJournal? journal = null)
    {
        var points = new List<LandingSeriesPoint>();
        if (record != null)
            foreach (var point in record.Series)
            {
                if (!Finite(point.TimeSeconds) || (points.Count > 0 && point.TimeSeconds < points[points.Count - 1].TimeSeconds))
                { journal?.Record(point.TimeSeconds, "replay.inputs", "invalid_clock", "Input matching is unavailable for invalid or reversed recorded time."); return new ReplayControlInputs(Array.Empty<LandingSeriesPoint>()); }
                if (points.Count > 0 && point.TimeSeconds == points[points.Count - 1].TimeSeconds) points[points.Count - 1] = point;
                else points.Add(point);
            }
        var result = new ReplayControlInputs(points.ToArray());
        if (record == null || points.Count < 2)
        { journal?.Record(0, "replay.inputs", "insufficient_samples", "Input matching requires at least two recorded samples."); return result; }
        var legacy = record.RawControllerAxesSourceIndices.Count == 0;
        var sources = legacy
            ? record.RawControllerSourceIndices.Count > 0 ? record.RawControllerSourceIndices.ToArray() : Enumerable.Range(0, TelemetrySample.CapturedControllerCount).ToArray()
            : record.RawControllerAxesSourceIndices.ToArray();
        if (sources.Distinct().Count() != sources.Length)
        { journal?.Record(0, "replay.inputs", "duplicate_source_indices", "Malformed column provenance prevents physical axis matching."); return result; }
        // Historical recordings have their own Y-only layout. A full-axis
        // recording can legitimately contain only Y axes from separate devices;
        // physical axis names do not determine their simulator bindings.
        var pitchOnly = legacy;

        // All contacts in a bounce sequence use the approach before the FIRST
        // contact. Ground-mode FBW cannot veto an available approach match.
        var contact = points.Count;
        for (var i = 1; i < points.Count; i++)
            if (points[i].OnGround && !points[i - 1].OnGround) { contact = i; break; }
        var approach = Enumerable.Range(0, contact).Where(i => !points[i].OnGround).ToArray();
        var ground = Enumerable.Range(0, points.Count).Where(i => points[i].OnGround).ToArray();
        journal?.Record(0, "replay.inputs", "matching_windows", legacy
                ? "Historical Y-only recordings retain level correlation over the airborne approach before first contact, with ground fallback when unavailable."
                : "Rank matching changes, not coincident signal levels: pitch and roll use the complete airborne approach before first contact; yaw uses the complete recorded sequence.",
            new Dictionary<string, double> { ["legacy_y_only"] = legacy ? 1 : 0, ["approach_samples"] = approach.Length, ["ground_samples"] = ground.Length, ["sources"] = sources.Length });
        var ranked = new List<ReplayControlAxis>[3];
        for (var role = 0; role < 3; role++)
        {
            // Unlike flare controls, directional steering often first moves on
            // the runway; rank yaw over the complete recorded sequence.
            var indices = role == 2 ? Enumerable.Range(0, points.Count).ToArray() : approach;
            ranked[role] = pitchOnly && role != 0 ? new List<ReplayControlAxis>() : result.Rank(record, sources, legacy, role, indices, journal);
            // Rudder may first move during rollout. Fallback requires absence of
            // a computable approach correlation, never a low score.
            if (ranked[role].Count == 0 && (!pitchOnly || role == 0))
            {
                journal?.Record(0, "replay.inputs", "ground_fallback", "No computable primary-window correlation; trying ground samples without an acceptance cutoff.", new Dictionary<string, double> { ["role"] = role });
                ranked[role] = result.Rank(record, sources, legacy, role, ground, journal);
            }
        }
        // Assign all roles together: a weaker pitch/roll match must not consume
        // an axis with stronger yaw evidence. Null permits genuinely missing roles.
        var choices = ranked.Select(axes => axes.Cast<ReplayControlAxis?>().Concat(new ReplayControlAxis?[] { null }).ToArray()).ToArray();
        var selectedAxes = new ReplayControlAxis?[3];
        var ambiguous = new bool[3];
        var bestScore = double.NegativeInfinity; var assignments = 0; var winners = 0;
        foreach (var pitch in choices[0])
        foreach (var roll in choices[1])
        foreach (var yaw in choices[2])
        {
            if ((pitch != null && pitch.SourceIndex == roll?.SourceIndex) ||
                (yaw != null && (yaw.SourceIndex == pitch?.SourceIndex || yaw.SourceIndex == roll?.SourceIndex))) continue;
            var p = Math.Abs(pitch?.Correlation ?? 0); var r = Math.Abs(roll?.Correlation ?? 0);
            var sameDevice = pitch != null && roll != null && pitch.SourceIndex / TelemetrySample.CapturedControllerAxisCount == roll.SourceIndex / TelemetrySample.CapturedControllerAxisCount;
            var score = p + r + Math.Abs(yaw?.Correlation ?? 0) + (sameDevice ? SameDevicePreference * Math.Min(p, r) : 0);
            assignments++;
            if (score < bestScore) continue;
            var axes = new[] { pitch, roll, yaw };
            if (score > bestScore)
            {
                bestScore = score; winners = 0;
                Array.Copy(axes, selectedAxes, axes.Length);
                Array.Clear(ambiguous, 0, ambiguous.Length);
            }
            else
                for (var role = 0; role < axes.Length; role++)
                    ambiguous[role] |= selectedAxes[role]?.SourceIndex != axes[role]?.SourceIndex ||
                        Math.Sign(selectedAxes[role]?.Correlation ?? 0) != Math.Sign(axes[role]?.Correlation ?? 0);
            winners++;
        }
        result.Pitch = ambiguous[0] ? null : selectedAxes[0];
        result.Roll = ambiguous[1] ? null : selectedAxes[1];
        result.Yaw = ambiguous[2] ? null : selectedAxes[2];
        journal?.Record(0, "replay.inputs", "joint_assignment", "Highest joint pitch/roll/yaw evidence wins with distinct physical axes; only pitch/roll receive a soft same-device preference. Exact ties leave only disputed roles unavailable.",
            new Dictionary<string, double> { ["assignments"] = assignments, ["best_score"] = bestScore, ["winning_assignments"] = winners, ["same_device_weight"] = SameDevicePreference });
        // Preserve already recorded Y-only selections. These are historical
        // metadata, not a confidence cutoff for newly matched full-axis input.
        if (pitchOnly && record.RawPitchInputSourceIndex >= 0 && record.RawPitchInputSourceIndex < TelemetrySample.CapturedControllerCount &&
            Finite(record.RawPitchInputCorrelation) && record.RawPitchInputCorrelation != 0 && Math.Abs(record.RawPitchInputCorrelation) <= 1 &&
            Finite(record.RawPitchInputLagSeconds) && record.RawPitchInputLagSeconds >= 0 && record.RawPitchInputLagSeconds <= .8)
        {
            var source = record.RawPitchInputSourceIndex * TelemetrySample.CapturedControllerAxisCount + 1;
            var stored = legacy ? record.RawPitchInputSourceIndex : source;
            var column = Array.IndexOf(sources, stored);
            if (column >= 0 && sources.Count(value => value == stored) == 1 && points.Count(point => Raw(point, column, legacy, out _)) >= 2)
            {
                result.Pitch = new ReplayControlAxis(source, column,
                    (record.RawPitchInputSourceIndex < record.ControlInputSources.Count ? record.ControlInputSources[record.RawPitchInputSourceIndex] : "Controller " + (record.RawPitchInputSourceIndex + 1)) + " · Y",
                    record.RawPitchInputCorrelation, record.RawPitchInputLagSeconds, legacy);
                journal?.Record(0, "replay.inputs", "legacy_selection_retained", "Historical Y-axis source metadata retained.", new Dictionary<string, double> { ["source_index"] = source });
            }
        }
        var selected = new[] { result.Pitch, result.Roll, result.Yaw };
        for (var role = 0; role < selected.Length; role++)
        {
            var axis = selected[role];
            journal?.Record(0, "replay.inputs", axis == null ? "role_unavailable" : "axis_selected",
                axis == null ? "No unique eligible physical axis remains for this role." : "Selected physical axis; only polarity is aligned, amplitude and sample time are unchanged.",
                new Dictionary<string, double> { ["role"] = role, ["source_index"] = axis?.SourceIndex ?? -1,
                    ["correlation"] = axis?.Correlation ?? 0, ["lag_seconds"] = axis?.LagSeconds ?? 0,
                    ["candidate_count"] = ranked[role].Count });
        }
        return result;
    }

    private List<ReplayControlAxis> Rank(LandingRecord record, int[] sources, bool legacy, int role, int[] indices, TelemetryDecisionJournal? journal)
    {
        var candidates = new List<ReplayControlAxis>();
        if (indices.Length < 2) return candidates;
        for (var column = 0; column < sources.Length; column++)
        {
            var source = legacy ? sources[column] * TelemetrySample.CapturedControllerAxisCount + 1 : sources[column];
            if (source < 0 || source >= TelemetrySample.CapturedControllerCount * TelemetrySample.CapturedControllerAxisCount ||
                sources.Count(value => value == sources[column]) != 1) continue;
            // Constant input has undefined correlation; skip it before lag search.
            var observed = false; var first = 0.0; var varies = false;
            foreach (var index in indices)
                if (Raw(_points[index], column, legacy, out var value))
                {
                    if (observed && value != first) { varies = true; break; }
                    first = value; observed = true;
                }
            if (!varies)
            { journal?.Record(0, "replay.inputs", observed ? "constant_axis" : "missing_axis", "No usable variation exists in this candidate window.", new Dictionary<string, double> { ["role"] = role, ["source_index"] = source, ["samples"] = indices.Length }); continue; }
            double? best = null; var delay = 0.0;
            for (var step = 0; step <= 16; step++)
            {
                var lag = step * 0.05;
                var correlation = Correlation(column, legacy, role, lag, indices);
                if (correlation.HasValue && (!best.HasValue || Math.Abs(correlation.Value) > Math.Abs(best.Value)))
                { best = correlation; delay = lag; }
            }
            // Zero covariance supplies no computable direction/polarity;
            // this is not an acceptance threshold on correlation strength.
            if (!best.HasValue || best.Value == 0)
            { journal?.Record(0, "replay.inputs", "undefined_correlation", "No computable nonzero covariance supplies a physical-axis polarity.", new Dictionary<string, double> { ["role"] = role, ["source_index"] = source }); continue; }
            var device = source / TelemetrySample.CapturedControllerAxisCount;
            var label = (device < record.ControlInputSources.Count ? record.ControlInputSources[device] : "Controller " + (device + 1)) +
                        " · " + new[] { "X", "Y", "Z", "R", "U", "V" }[source % TelemetrySample.CapturedControllerAxisCount];
            candidates.Add(new ReplayControlAxis(source, column, label, best.Value, delay, legacy));
            journal?.Record(0, "replay.inputs", "axis_candidate", label,
                new Dictionary<string, double> { ["role"] = role, ["source_index"] = source, ["correlation"] = best.Value, ["score"] = Math.Abs(best.Value), ["lag_seconds"] = delay, ["samples"] = indices.Length });
        }
        return candidates.OrderByDescending(axis => Math.Abs(axis.Correlation)).ToList();
    }

    public ReplayControlSample Sample(double timeSeconds)
    {
        if (!Find(timeSeconds, out var a, out var b, out var fraction)) return default;
        return new ReplayControlSample(Value(Pitch, a, b, fraction), Value(Roll, a, b, fraction), Value(Yaw, a, b, fraction));
    }

    private double? Value(ReplayControlAxis? axis, LandingSeriesPoint a, LandingSeriesPoint b, double fraction)
    {
        if (axis == null || !Raw(a, axis.Column, axis.Legacy, out var first) || !Raw(b, axis.Column, axis.Legacy, out var last)) return null;
        return Math.Sign(axis.Correlation) * (first + (last - first) * fraction);
    }

    private double? Correlation(int column, bool legacy, int role, double lag, int[] indices)
    {
        var previousIndex = -2; var previousRaw = 0.0; var previousCommand = 0.0;
        var n = 0; var mx = 0.0; var my = 0.0;
        var vx = 0.0; var vy = 0.0; var covariance = 0.0;
        var end = _points[indices[indices.Length - 1]].TimeSeconds;
        foreach (var index in indices)
        {
            var point = _points[index]; var time = point.TimeSeconds + lag;
            if (time > end || !Raw(point, column, legacy, out var raw) ||
                !Find(time, out var a, out var b, out var fraction, out var segment) ||
                segment != _segments[index] || a.OnGround != point.OnGround || b.OnGround != point.OnGround) continue;
            var before = Command(a, role); var after = Command(b, role);
            if (!Finite(before) || !Finite(after) || Math.Abs(before) > 100.001 || Math.Abs(after) > 100.001) continue;
            var command = before + (after - before) * fraction;
            // Preserve the published matching semantics for historical Y-only
            // files. The format, never an axis or device name, selects this path.
            if (legacy)
            {
                n++; var dx = raw - mx; var dy = command - my;
                mx += dx / n; my += dy / n;
                vx += dx * (raw - mx); vy += dy * (command - my); covariance += dx * (command - my);
                continue;
            }
            // Compare movements over the same adjacent recorded interval. A
            // throttle step must not gain evidence from its subsequent plateau.
            // Do not divide by dt: burst delivery cannot amplify an increment.
            if (previousIndex == index - 1 && _segments[previousIndex] == _segments[index])
            {
                var dx = raw - previousRaw; var dy = command - previousCommand;
                vx += dx * dx; vy += dy * dy; covariance += dx * dy;
            }
            previousIndex = index; previousRaw = raw; previousCommand = command;
        }
        // Uncentered increments retain steady ramps and remain independent of
        // axis amplitude/offset. Undefined motion is not a confidence cutoff.
        if ((legacy && n < 2) || vx <= 0 || vy <= 0) return null;
        var correlation = covariance / Math.Sqrt(vx * vy);
        return Finite(correlation) ? Math.Max(-1, Math.Min(1, correlation)) : (double?)null;
    }

    private bool Find(double time, out LandingSeriesPoint a, out LandingSeriesPoint b, out double fraction)
        => Find(time, out a, out b, out fraction, out _);

    private bool Find(double time, out LandingSeriesPoint a, out LandingSeriesPoint b, out double fraction, out int segment)
    {
        a = b = null!; fraction = 0; segment = -1;
        if (!Finite(time) || _points.Length == 0 || time < _points[0].TimeSeconds || time > _points[_points.Length - 1].TimeSeconds) return false;
        var low = 0; var high = _points.Length - 1;
        while (low < high) { var middle = low + (high - low) / 2; if (_points[middle].TimeSeconds < time) low = middle + 1; else high = middle; }
        b = _points[low];
        segment = _segments[low];
        if (low == 0 || b.TimeSeconds == time) { a = b; return true; }
        a = _points[low - 1];
        if (b.TimeSeconds - a.TimeSeconds > LandingReplayTrack.MaximumInterpolationGapSeconds) return false;
        fraction = (time - a.TimeSeconds) / (b.TimeSeconds - a.TimeSeconds);
        return true;
    }

    private static bool Raw(LandingSeriesPoint point, int column, bool legacy, out double value)
    {
        value = 0;
        var values = legacy ? point.RawControllerYAxisPercent : point.RawControllerAxesPercent;
        var valid = legacy ? point.RawControllerYAxisValid : point.RawControllerAxesValid;
        var ages = legacy ? point.RawControllerYAxisAgeSeconds : point.RawControllerAxesAgeSeconds;
        if (values == null || valid == null || ages == null || column >= values.Length || column >= valid.Length || column >= ages.Length ||
            !valid[column] || !Finite(values[column]) || Math.Abs(values[column]) > 100.001 || !Finite(ages[column]) || ages[column] < 0 || ages[column] > MaximumAgeSeconds) return false;
        value = values[column]; return true;
    }

    private static double Command(LandingSeriesPoint point, int role) => role == 0 ? point.PilotPitchPercent : role == 1 ? point.PilotRollPercent : point.PilotYawPercent;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed class ReplayControlAxis
{
    internal ReplayControlAxis(int sourceIndex, int column, string label, double correlation, double lag, bool legacy)
    { SourceIndex = sourceIndex; Column = column; SourceLabel = label; Correlation = correlation; LagSeconds = lag; Legacy = legacy; }
    public int SourceIndex { get; }
    internal int Column { get; }
    public string SourceLabel { get; }
    public double Correlation { get; }
    public double LagSeconds { get; }
    public bool Legacy { get; }
}

public readonly struct ReplayControlSample
{
    internal ReplayControlSample(double? pitch, double? roll, double? yaw) { PitchPercent = pitch; RollPercent = roll; YawPercent = yaw; }
    /// <summary>Positive pull, matching the recorded YOKE Y command; rendered downward.</summary>
    public double? PitchPercent { get; }
    /// <summary>Positive right, matching the recorded YOKE X command.</summary>
    public double? RollPercent { get; }
    /// <summary>Positive right rudder command; the device can be pedals, twist or another bound axis.</summary>
    public double? YawPercent { get; }
    public bool HasStick => PitchPercent.HasValue && RollPercent.HasValue;
}
