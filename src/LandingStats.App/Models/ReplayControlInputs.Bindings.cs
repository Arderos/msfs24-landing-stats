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
        return new ReplayControlAxis(source, column, devices[0].Name + " · " + AxisLabel(binding.AxisIndex, _axisCount) + " · " + binding.Source,
            0, 0, false, binding.Inverted ? -1 : 1, true);
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
            var candidates = Rank(record, sources, false, engine + 3, Enumerable.Range(0, _points.Length).ToArray(), journal, 3);
            if (candidates.Count == 0) continue;
            var best = Math.Abs(candidates[0].Correlation);
            if (candidates.Count(candidate => Math.Abs(candidate.Correlation) == best) == 1) _throttles[engine] = candidates[0];
        }
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
