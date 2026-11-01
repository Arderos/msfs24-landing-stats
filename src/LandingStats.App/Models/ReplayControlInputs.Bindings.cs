using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Models;

public sealed partial class ReplayControlInputs
{
    public bool HasPhysicalPitch => _catalogSegments == null ? _assignedFlightControls[0] || Pitch != null :
        _catalogSegments.Any(segment => segment.Inputs.HasPhysicalPitch);

    public ReplayControlAxis? Throttle(int engineIndex) => engineIndex >= 0 && engineIndex < _throttles.Length ? _throttles[engineIndex] : null;
    // A recorded assignment owns this channel even while its device is absent.
    // Consumers must retain the resulting gap instead of substituting simulator commands.
    public bool HasPhysicalThrottle(int engineIndex) => engineIndex >= 0 && engineIndex < _throttles.Length &&
        (_catalogSegments == null ? _assignedThrottles[engineIndex] || Throttle(engineIndex) != null :
            _catalogSegments.Any(segment => segment.Inputs.HasPhysicalThrottle(engineIndex)));

    /// <summary>Physical lever travel, 0..100 percent. Simulator throttle commands are separate recorded signals.</summary>
    public double? SampleThrottle(double timeSeconds, int engineIndex)
    {
        if (_catalogSegments != null)
        {
            SampleCatalog(timeSeconds);
            return _catalogSegments[_catalogSelection].Inputs.SampleThrottle(timeSeconds, engineIndex);
        }
        if (!Find(timeSeconds, out var a, out var b, out var fraction)) return null;
        var raw = Value(Throttle(engineIndex), a, b, fraction);
        return raw.HasValue ? (raw.Value + 100) / 2 : (double?)null;
    }

    private ReplayControlAxis? DirectBinding(LandingRecord record, ControllerCatalogMessage? catalog, ControllerControlKind kind, int engine)
    {
        if (catalog == null || record.RawControllerAxesSourceIndices.Count == 0) return null;
        var candidates = PresentBindings(catalog, kind, engine);
        if (candidates.Length != 1) return null;
        var binding = candidates[0];
        var devices = catalog.Devices.Where(device => device.InstanceId == binding.DeviceInstanceId && device.Supports(binding.AxisIndex)).ToArray();
        if (devices.Length != 1 || binding.AxisIndex >= _axisCount) return null;
        var source = devices[0].Slot * _axisCount + binding.AxisIndex;
        var column = record.RawControllerAxesSourceIndices.IndexOf(source);
        if (column < 0 || record.RawControllerAxesSourceIndices.Count(value => value == source) != 1) return null;
        // MSFS unipolar throttle profiles start with the opposite direction to
        // increasing native DirectInput values; FL_MOD_INV toggles that baseline.
        // The saved flag describes the profile transform, not display polarity.
        // Keep the original flag/raw samples and orient physical travel here.
        var polarity = binding.Inverted ? -1 : 1;
        if (kind == ControllerControlKind.Throttle) polarity = -polarity;
        return new ReplayControlAxis(source, column, devices[0].Name + " · " + AxisLabel(binding.AxisIndex, _axisCount) + " · " + binding.Source,
            0, 0, false, polarity, true);
    }

    private static IEnumerable<ControllerBinding> ProfileBindings(ControllerCatalogMessage catalog, ControllerControlKind kind, int engine)
        => catalog.Bindings.Where(binding => binding.Control == kind &&
            (kind != ControllerControlKind.Throttle || binding.EngineIndex == -1 || binding.EngineIndex == engine));

    private static ControllerBinding[] PresentBindings(ControllerCatalogMessage catalog, ControllerControlKind kind, int engine)
        => ProfileBindings(catalog, kind, engine)
            .Where(binding => catalog.Devices.Any(device => device.InstanceId == binding.DeviceInstanceId && device.Supports(binding.AxisIndex)))
            .GroupBy(binding => new { binding.DeviceInstanceId, binding.AxisIndex, binding.Inverted }).Select(group => group.First()).ToArray();

    // A known assignment remains authoritative while hardware or its measurements
    // are absent. Several assignments to attached devices are an unresolved conflict.
    private bool MissingAssignedInput(LandingRecord record, ControllerCatalogMessage? catalog, ControllerControlKind kind, int engine)
    {
        if (catalog == null || !ProfileBindings(catalog, kind, engine).Any()) return false;
        var candidates = PresentBindings(catalog, kind, engine);
        if (candidates.Length == 0) return true;
        return !candidates.Any(binding => binding.AxisIndex < _axisCount && catalog.Devices.Any(device =>
            device.InstanceId == binding.DeviceInstanceId && device.Supports(binding.AxisIndex) &&
            record.RawControllerAxesSourceIndices.Contains(device.Slot * _axisCount + binding.AxisIndex)));
    }

    private void ResolveThrottles(LandingRecord record, ControllerCatalogMessage? catalog, int[] sources, bool legacy, TelemetryDecisionJournal? journal)
    {
        for (var engine = 0; engine < _throttles.Length; engine++)
            _assignedThrottles[engine] = catalog != null && ProfileBindings(catalog, ControllerControlKind.Throttle, engine).Any();
        if (legacy) return;
        var flightAxes = new[] { Pitch, Roll, Yaw }.Where(axis => axis != null).Select(axis => axis!.SourceIndex).ToArray();
        foreach (var axis in flightAxes) _reservedAxes.Add(axis);
        for (var engine = 0; engine < _throttles.Length; engine++)
        {
            _throttles[engine] = DirectBinding(record, catalog, ControllerControlKind.Throttle, engine);
            if (_throttles[engine] != null && flightAxes.Contains(_throttles[engine]!.SourceIndex)) _throttles[engine] = null;
            if (_throttles[engine] != null || MissingAssignedInput(record, catalog, ControllerControlKind.Throttle, engine)) continue;
            _throttles[engine] = InferThrottle(record, sources, engine, journal);
        }
    }

    // Engine command can be driven by automation. It identifies a physical lever
    // only when repeated movements support the same mapping in independent time
    // windows. A single retard, its plateau, or a numerical winner among unrelated
    // axes supplies insufficient evidence. Explicit bindings bypass this contract.
    private ReplayControlAxis? InferThrottle(LandingRecord record, int[] sources, int engine, TelemetryDecisionJournal? journal)
    {
        if (!ThrottleWindows(engine, out var split)) return null;
        var supported = new List<ReplayControlAxis>();
        for (var column = 0; column < sources.Length; column++)
        {
            var source = sources[column];
            if (source < 0 || _reservedAxes.Contains(source) || sources.Count(value => value == source) != 1) continue;
            var first = FitThrottle(column, engine, 0, split);
            var second = FitThrottle(column, engine, split, _points.Length);
            if (first == null || second == null || Math.Sign(first.Gain) != Math.Sign(second.Gain)) continue;
            // Each lag/gain is selected using its training window only. The other
            // window cannot choose a favourable lag for its validation score.
            if (!PredictsThrottle(column, engine, split, _points.Length, first) ||
                !PredictsThrottle(column, engine, 0, split, second)) continue;
            var device = source / _axisCount;
            var label = (device < record.ControlInputSources.Count ? record.ControlInputSources[device] : "Controller " + (device + 1)) +
                " · " + AxisLabel(source % _axisCount, _axisCount);
            supported.Add(new ReplayControlAxis(source, column, label, first.Correlation, first.Lag, false));
        }
        // Even a slightly better score cannot distinguish synchronous levers.
        // Require one supported physical source, not one floating-point maximum.
        journal?.Record(0, "replay.inputs", supported.Count == 1 ? "throttle_evidence_supported" : "throttle_evidence_unresolved",
            "Throttle inference requires repeatable movement prediction in both independent windows and one supported source.",
            new Dictionary<string, double> { ["engine"] = engine + 1, ["supported_sources"] = supported.Count,
                ["source_index"] = supported.Count == 1 ? supported[0].SourceIndex : -1, ["split_time"] = _points[split].TimeSeconds });
        return supported.Count == 1 ? supported[0] : null;
    }

    private bool ThrottleWindows(int engine, out int split)
    {
        split = 0;
        var starts = new List<int>(); var direction = 0; var previous = double.NaN;
        for (var index = 0; index < _points.Length; index++)
        {
            var command = EngineCommand(_points[index].TimeSeconds, engine);
            if (!Finite(command) || !Finite(previous) || index == 0 || _segments[index] != _segments[index - 1]) direction = 0;
            else
            {
                var next = Math.Sign(command - previous);
                if (next != 0)
                {
                    if (next != direction) starts.Add(index - 1);
                    direction = next;
                }
            }
            previous = command;
        }
        // At least two separate movements per window. Splitting a lone ramp in
        // half would validate the same action twice. Only reversals delimit runs;
        // equal samples can be quantization within one uninterrupted movement.
        if (starts.Count < 4) return false;
        split = starts[starts.Count / 2];
        return split > 0 && split < _points.Length - 1;
    }

    private ThrottleFit? FitThrottle(int column, int engine, int start, int end)
    {
        ThrottleFit? best = null;
        for (var step = 0; step <= 16; step++)
        {
            var lag = step * .05;
            var motion = ThrottleMotion(column, engine, start, end, lag);
            if (motion.Count < 3 || motion.RawRuns < 2 || motion.RawEnergy <= 0 || motion.CommandEnergy <= 0 || motion.Product == 0) continue;
            var correlation = motion.Product / Math.Sqrt(motion.RawEnergy * motion.CommandEnergy);
            if (!Finite(correlation) || best != null && Math.Abs(correlation) <= Math.Abs(best.Correlation)) continue;
            best = new ThrottleFit(lag, motion.Product / motion.RawEnergy, correlation);
        }
        return best;
    }

    private bool PredictsThrottle(int column, int engine, int start, int end, ThrottleFit fit)
    {
        var motion = ThrottleMotion(column, engine, start, end, fit.Lag);
        if (motion.Count < 3 || motion.RawRuns < 2 || motion.RawEnergy <= 0 || motion.CommandEnergy <= 0) return false;
        var predicted = fit.Gain * fit.Gain * motion.RawEnergy;
        var residual = Math.Max(0, motion.CommandEnergy - 2 * fit.Gain * motion.Product + predicted);
        // Explicit engineering admission policy: predicted movement must exceed
        // unexplained movement (signal-to-residual energy > 1), and must improve
        // on predicting no command movement. This is not statistical confidence.
        return Finite(predicted) && Finite(residual) && predicted > residual && residual < motion.CommandEnergy;
    }

    private ThrottleMotionEvidence ThrottleMotion(int column, int engine, int start, int end, double lag)
    {
        var motion = new ThrottleMotionEvidence();
        var previousIndex = -2; var previousRaw = 0.0; var previousCommand = 0.0; var direction = 0;
        for (var index = start; index < end; index++)
        {
            var point = _points[index]; var time = point.TimeSeconds + lag;
            if (time > _points[end - 1].TimeSeconds || !Raw(point, column, false, out var raw) ||
                !Find(time, out _, out _, out _, out var segment) || segment != _segments[index]) continue;
            var command = EngineCommand(time, engine);
            if (!Finite(command) || Math.Abs(command) > 100.001) continue;
            if (previousIndex == index - 1 && _segments[index] == _segments[previousIndex])
            {
                var x = raw - previousRaw; var y = command - previousCommand;
                motion.Count++; motion.RawEnergy += x * x; motion.CommandEnergy += y * y; motion.Product += x * y;
                if (x != 0)
                {
                    var next = Math.Sign(x);
                    if (next != direction) motion.RawRuns++;
                    direction = next;
                }
            }
            previousIndex = index; previousRaw = raw; previousCommand = command;
        }
        return motion;
    }

    private sealed class ThrottleFit
    {
        internal ThrottleFit(double lag, double gain, double correlation) { Lag = lag; Gain = gain; Correlation = correlation; }
        internal double Lag { get; }
        internal double Gain { get; }
        internal double Correlation { get; }
    }
    private struct ThrottleMotionEvidence
    {
        internal int Count, RawRuns;
        internal double RawEnergy, CommandEnergy, Product;
    }

    private double EngineCommand(double time, int engineIndex)
    {
        var series = _record?.Engines.FirstOrDefault(engine => engine.EngineNumber == engineIndex + 1)?.Points;
        if (series == null || series.Count == 0 || time < series[0].TimeSeconds || time > series[series.Count - 1].TimeSeconds) return double.NaN;
        var index = RecordedTimeSearch.LowerBound(series.Count, i => series[i].TimeSeconds, time);
        var b = series[index];
        if (index == 0 || b.TimeSeconds == time) return b.ThrottlePercent;
        var a = series[index - 1]; var gap = b.TimeSeconds - a.TimeSeconds;
        if (gap <= 0 || gap > LandingReplayTrack.MaximumInterpolationGapSeconds) return double.NaN;
        return a.ThrottlePercent + (b.ThrottlePercent - a.ThrottlePercent) * (time - a.TimeSeconds) / gap;
    }

    private static string AxisLabel(int axis, int count) => (count == 8
        ? new[] { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider 1", "Slider 2" }
        : new[] { "X", "Y", "Z", "R", "U", "V" })[axis];
}
