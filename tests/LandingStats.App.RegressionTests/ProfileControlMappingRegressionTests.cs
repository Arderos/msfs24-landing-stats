using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ProfileControlMappingRegressionTests
{
    internal static void RunAll()
    {
        ConstantHighAxesAndSharedThrottle();
        MissingProfilesUseCorrelation();
        AmbiguousRoleUsesRemainingAxes();
        ExplicitStickAxesCannotBecomeThrottles();
        ConflictingFlightRolesPreserveOtherRoles();
        ProfileTransitionsAndMissingObservations();
        AssignedDisconnectedInputsNeverFallBack();
        MissingAssignedAxisNeverFallsBack();
        DisconnectedProfilesDoNotConflictWithAttachedDevice();
        ConflictingPresentThrottlesUseRemainingAxes();
        StrideEightContactRemapping();
        CatalogSamplesUseLatestDuplicateTime();
        CatalogSamplesRejectInvalidTime();
    }

    private static void CatalogSamplesUseLatestDuplicateTime()
    {
        var devices = Devices(); var last = devices[8];
        var record = Record(new[] { 70, 71 }, _ => new[] { 20.0, 40.0 });
        record.Series[21].TimeSeconds = record.Series[20].TimeSeconds;
        record.Series[21].RawControllerAxesPercent = new[] { -60.0, -80.0 };
        var first = Catalog(devices, Binding(last, ControllerControlKind.Pitch, 6), Binding(last, ControllerControlKind.Throttle, 7, false, 0));
        Attach(record, (0, first));
        var inputs = ReplayControlInputs.Resolve(record);
        Equal(inputs.Sample(1).PitchPercent, -60, "Duplicate simulator timestamp retained an obsolete physical stick position");
        Equal(inputs.SampleThrottle(1, 0), 10, "Duplicate simulator timestamp retained an obsolete physical lever position");
        Equal(inputs.Sample(.975).PitchPercent, -20, "Interpolation used an obsolete endpoint at a duplicate timestamp");
        record.Series[21].RawControllerAxesValid[0] = false;
        inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Sample(1).PitchPercent == null, "A stale valid observation replaced the latest unavailable physical input");
        record.Series[21].RawControllerAxesValid[0] = true;
        var second = Catalog(devices, Binding(last, ControllerControlKind.Pitch, 7), Binding(last, ControllerControlKind.Throttle, 6, false, 0));
        Attach(record, (0, first), (21, second));
        inputs = ReplayControlInputs.Resolve(record);
        Equal(inputs.Sample(1).PitchPercent, -80, "A duplicate timestamp lost the latest catalog transition");
        Equal(inputs.SampleThrottle(1, 0), 20, "A duplicate timestamp used the previous throttle assignment");
        Equal(inputs.Sample(.975).PitchPercent, 20, "A later catalog changed samples before its transition");
    }

    private static void CatalogSamplesRejectInvalidTime()
    {
        foreach (var invalid in new[] { double.NaN, .9 })
        {
            var devices = Devices(); var last = devices[8];
            var record = Record(new[] { 70, 71 }, _ => new[] { 20.0, 40.0 });
            record.Series[21].TimeSeconds = invalid;
            var catalog = Catalog(devices, Binding(last, ControllerControlKind.Pitch, 6), Binding(last, ControllerControlKind.Throttle, 7, false, 0));
            Attach(record, (0, catalog), (21, catalog));
            var inputs = ReplayControlInputs.Resolve(record);
            Check(inputs.Sample(1).PitchPercent == null && inputs.SampleThrottle(1, 0) == null,
                "Catalog replay accepted an invalid or reversed clock across a transition.");
        }
    }
    private static void ConstantHighAxesAndSharedThrottle()
    {
        var devices = Devices(); var last = devices[8];
        var record = Record(new[] { 70, 71, 67 }, _ => new[] { 20.0, 30.0, -40.0 });
        var catalog = Catalog(devices, Binding(last, ControllerControlKind.Pitch, 6, true), Binding(last, ControllerControlKind.Roll, 7),
            Binding(last, ControllerControlKind.Throttle, 3, true, 0), Binding(last, ControllerControlKind.Throttle, 3, true, 1));
        Attach(record, (0, catalog));
        var inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch?.IsExplicitBinding == true && inputs.Pitch.SourceIndex == 70 && inputs.Roll?.SourceIndex == 71, "Constant stick bindings beyond the eighth device were lost.");
        var value = inputs.Sample(1);
        Equal(value.PitchPercent, -20, "Explicit pitch inversion"); Equal(value.RollPercent, 30, "Eighth physical axis");
        Check(inputs.Throttle(0)?.SourceIndex == 67 && inputs.Throttle(1)?.SourceIndex == 67, "Shared lever was not assigned to both engines.");
        Equal(inputs.SampleThrottle(1, 0), 70, "Throttle inversion"); Equal(inputs.SampleThrottle(1, 1), 70, "Shared throttle sample");
        record.Series[20].RawControllerAxesValid[2] = false;
        inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.SampleThrottle(1, 0) == null, "Invalid throttle sample was filled in.");
    }
    private static void MissingProfilesUseCorrelation()
    {
        var devices = Devices();
        var record = Record(new[] { 70, 71, 67 }, i => new[] { Pitch(i), -Roll(i), Throttle(i) });
        Attach(record, (0, Catalog(devices)));
        var inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch?.SourceIndex == 70 && inputs.Roll?.SourceIndex == 71 && inputs.Roll.Polarity == -1, "Catalog without assignments stopped the existing control mapper.");
        Check(inputs.Throttle(0)?.SourceIndex == 67 && inputs.Throttle(1)?.SourceIndex == 67, "Correlation fallback lost a shared throttle.");
        Check(inputs.Pitch?.IsExplicitBinding == false, "Inferred source was marked explicit.");
    }
    private static void AmbiguousRoleUsesRemainingAxes()
    {
        var devices = Devices(); var last = devices[8];
        var record = Record(new[] { 64, 65, 66, 67 }, i => new[] { 8.0, -9.0, Pitch(i), 12.0 });
        Attach(record, (0, Catalog(devices, Binding(last, ControllerControlKind.Pitch, 0), Binding(last, ControllerControlKind.Pitch, 1), Binding(last, ControllerControlKind.Roll, 3))));
        var inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch?.SourceIndex == 66 && inputs.Pitch.IsExplicitBinding == false, "Conflicting pitch assignments did not use the remaining correlation evidence.");
        Check(inputs.Roll?.SourceIndex == 67 && inputs.Roll.IsExplicitBinding, "Conflict in one role discarded the explicit other role.");
    }
    private static void ExplicitStickAxesCannotBecomeThrottles()
    {
        var devices = Devices(); var last = devices[8];
        var record = Record(new[] { 64, 65, 66 }, i => new[] { Throttle(i), 12.0, Throttle(i) });
        Attach(record, (0, Catalog(devices, Binding(last, ControllerControlKind.Pitch, 0), Binding(last, ControllerControlKind.Roll, 1),
            Binding(last, ControllerControlKind.Throttle, 0, false, 0))));
        var inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch?.SourceIndex == 64 && inputs.Roll?.SourceIndex == 65, "Explicit flight control priority changed.");
        Check(inputs.Throttle(0)?.SourceIndex == 66, "A known stick axis was reused as throttle.");
    }
    private static void ConflictingFlightRolesPreserveOtherRoles()
    {
        var devices = Devices(); var last = devices[8];
        var record = Record(new[] { 64, 65, 66, 67 }, i => new[] { 5.0, Pitch(i), Roll(i), 13.0 });
        Attach(record, (0, Catalog(devices, Binding(last, ControllerControlKind.Pitch, 0), Binding(last, ControllerControlKind.Roll, 0), Binding(last, ControllerControlKind.Yaw, 3))));
        var inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch?.SourceIndex == 65 && inputs.Roll?.SourceIndex == 66, "Cross-role conflict prevented independent fallback.");
        Check(inputs.Yaw?.SourceIndex == 67 && inputs.Yaw.IsExplicitBinding, "Cross-role conflict discarded unaffected yaw.");
    }
    private static void ProfileTransitionsAndMissingObservations()
    {
        var devices = Devices(); var last = devices[8];
        var record = Record(new[] { 70, 71 }, _ => new[] { 20.0, 40.0 });
        var first = Catalog(devices, Binding(last, ControllerControlKind.Throttle, 6, false, 0));
        var second = Catalog(devices, Binding(last, ControllerControlKind.Throttle, 7, true, 0));
        Attach(record, (0, first), (40, second), (80, Array.Empty<byte>()));
        var inputs = ReplayControlInputs.Resolve(record);
        Equal(inputs.SampleThrottle(1, 0), 60, "First profile");
        Equal(inputs.SampleThrottle(2, 0), 30, "Changed profile");
        Check(inputs.SampleThrottle(1.975, 0) == null, "Interpolation crossed a profile boundary.");
        Check(inputs.SampleThrottle(4, 0) == null, "Missing catalog reused a stale profile.");
        Equal(inputs.SampleThrottle(1, 0), 60, "Backward replay did not restore its recorded profile");
        record.Series[20].RawControllerAxesAgeSeconds[0] = .5;
        inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.SampleThrottle(1, 0) == null, "Stale physical samples were displayed.");
    }
    private static void AssignedDisconnectedInputsNeverFallBack()
    {
        var devices = Devices(); var last = devices[8];
        var record = Record(new[] { 0, 1, 70, 71 }, i => new[] { Pitch(i), 12.0, 20.0, 40.0 });
        var bindings = new[] { Binding(last, ControllerControlKind.Pitch, 6), Binding(last, ControllerControlKind.Throttle, 7, true, 0), Binding(devices[0], ControllerControlKind.Roll, 1) };
        for (var i = 0; i < 40; i++) { record.Series[i].RawControllerAxesValid[2] = false; record.Series[i].RawControllerAxesValid[3] = false; }
        Attach(record, (0, Catalog(devices.Take(8).ToArray(), bindings)), (40, Catalog(devices, bindings)));
        var inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.HasPhysicalPitch && inputs.HasPhysicalThrottle(0), "A known disconnected assignment lost its physical channel identity.");
        Check(inputs.Pitch == null && inputs.Throttle(0) == null && inputs.Sample(1).PitchPercent == null && inputs.SampleThrottle(1, 0) == null,
            "Missing assigned hardware was replaced with another correlated controller.");
        Equal(inputs.Sample(2).PitchPercent, 20, "Reconnected stick did not use its saved GUID assignment");
        Equal(inputs.SampleThrottle(2, 0), 30, "Reconnected throttle did not use its saved GUID assignment");
        Check(inputs.SampleThrottle(1, 0) == null && inputs.Sample(1).PitchPercent == null, "Backward replay borrowed future device availability.");
    }
    private static void MissingAssignedAxisNeverFallsBack()
    {
        var devices = Devices(); var last = devices[8];
        var record = Record(new[] { 64, 65 }, i => new[] { Pitch(i), Throttle(i) });
        Attach(record, (0, Catalog(devices, Binding(last, ControllerControlKind.Pitch, 6), Binding(last, ControllerControlKind.Throttle, 7, false, 0))));
        var inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch == null && inputs.Throttle(0) == null && inputs.HasPhysicalPitch && inputs.HasPhysicalThrottle(0),
            "Assigned axes absent from recorded columns were replaced by correlation.");
        Attach(record, (0, Catalog(devices, Binding(last, ControllerControlKind.Pitch, 6), Binding(last, ControllerControlKind.Pitch, 7),
            Binding(last, ControllerControlKind.Throttle, 6, false, 0), Binding(last, ControllerControlKind.Throttle, 7, false, 0))));
        inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch == null && inputs.Throttle(0) == null, "Multiple assigned axes with no observations triggered replacement bindings.");
        devices[8] = new ControllerDescriptor(last.Slot, last.DeviceId, last.Name, last.ProductKey, 63, new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), last.InstanceId, last.ProductId);
        Attach(record, (0, Catalog(devices, Binding(last, ControllerControlKind.Pitch, 6), Binding(last, ControllerControlKind.Throttle, 7, false, 0))));
        inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch == null && inputs.Throttle(0) == null, "A device missing its assigned axes triggered replacement bindings.");
        var empty = Record(Array.Empty<int>(), _ => Array.Empty<double>());
        empty.RawPitchInputSourceIndex = 0; empty.RawPitchInputCorrelation = 1;
        foreach (var point in empty.Series) { point.RawControllerYAxisPercent[0] = 25; point.RawControllerYAxisValid[0] = true; }
        Attach(empty, (0, Catalog(Array.Empty<ControllerDescriptor>(), Binding(last, ControllerControlKind.Pitch, 6), Binding(last, ControllerControlKind.Throttle, 7, false, 0))));
        inputs = ReplayControlInputs.Resolve(empty);
        Check(inputs.Pitch == null && inputs.HasPhysicalPitch && inputs.HasPhysicalThrottle(0) && inputs.SampleThrottle(1, 0) == null,
            "Legacy Y fallback or simulator command replaced a known missing assignment.");
    }
    private static void DisconnectedProfilesDoNotConflictWithAttachedDevice()
    {
        var devices = Devices(); var last = devices[8]; var old = Devices()[8];
        var record = Record(new[] { 70, 71 }, _ => new[] { 20.0, 40.0 });
        Attach(record, (0, Catalog(devices, Binding(last, ControllerControlKind.Pitch, 6), Binding(old, ControllerControlKind.Pitch, 2),
            Binding(last, ControllerControlKind.Throttle, 7, true, 0), Binding(old, ControllerControlKind.Throttle, 0, false, 0))));
        var inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch?.IsExplicitBinding == true && inputs.Throttle(0)?.IsExplicitBinding == true, "Historic disconnected profile created a conflict with the attached hardware.");
        Equal(inputs.Sample(1).PitchPercent, 20, "Current stick assignment"); Equal(inputs.SampleThrottle(1, 0), 30, "Current throttle assignment");
    }
    private static void ConflictingPresentThrottlesUseRemainingAxes()
    {
        var devices = Devices(); var last = devices[8];
        var record = Record(new[] { 64, 65, 66, 67 }, i => new[] { 10.0, 20.0, Throttle(i), 30.0 });
        // No roll observation: a previously matched flight-control axis must remain unavailable to throttle fallback.
        foreach (var point in record.Series) point.PilotRollPercent = 0;
        Attach(record, (0, Catalog(devices, Binding(last, ControllerControlKind.Throttle, 0, false, 0), Binding(last, ControllerControlKind.Throttle, 1, false, 0),
            Binding(last, ControllerControlKind.Pitch, 3))));
        var inputs = ReplayControlInputs.Resolve(record);
        Check(inputs.Pitch?.SourceIndex == 67 && inputs.Throttle(0)?.SourceIndex == 66 && inputs.Throttle(0)?.IsExplicitBinding == false,
            "Conflicting attached throttle assignments consumed reserved axes or prevented fallback.");
    }
    private static void StrideEightContactRemapping()
    {
        var first = Record(new[] { 70, 71, 67 }, i => new[] { Pitch(i), -Roll(i), Throttle(i) });
        var second = Record(new[] { 70, 71, 67 }, _ => new[] { 20.0, 30.0, 40.0 });
        foreach (var record in new[] { first, second })
        { record.ContactCount = 2; record.TimestampUtc = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc); record.RecordingEpisodeId = "one-episode";
            record.ControlInputSources = Enumerable.Range(0, 12).Select(i => "Source " + i).ToList(); }
        first.ContactNumber = 1; second.ContactNumber = 2; second.ControlInputSources[11] = "Different unrelated source";
        var inputs = ReplayControlInputs.Resolve(second, new[] { first, second });
        Check(inputs.Pitch?.SourceIndex == 70 && inputs.Roll?.SourceIndex == 71 && inputs.Throttle(0)?.SourceIndex == 67,
            "Eight-axis contact remapping compared a different device using the historical stride.");
        Equal(inputs.Sample(1).PitchPercent, 20, "Remapped pitch amplitude"); Equal(inputs.Sample(1).RollPercent, -30, "Remapped roll polarity");
    }

    private static double Pitch(int i) => Math.Sin(i * .17) * 30;
    private static double Roll(int i) => Math.Cos(i * .31) * 20;
    private static double Throttle(int i) => Math.Sin(i * .47) * 40;
    private static LandingRecord Record(int[] sources, Func<int, double[]> values)
    {
        var record = new LandingRecord { ControllerAxisCount = 8, RawControllerAxesSourceIndices = sources.ToList() };
        for (var engine = 0; engine < 2; engine++) record.Engines.Add(new LandingEngineSeries { EngineNumber = engine + 1 });
        for (var i = 0; i < 120; i++)
        {
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = i * .05, PilotPitchPercent = Pitch(i), PilotRollPercent = Roll(i),
                RawControllerAxesPercent = values(i), RawControllerAxesValid = Enumerable.Repeat(true, sources.Length).ToArray(), RawControllerAxesAgeSeconds = new double[sources.Length] });
            foreach (var engine in record.Engines) engine.Points.Add(new LandingEnginePoint { TimeSeconds = i * .05, ThrottlePercent = 50 + Throttle(i) / 2 });
        }
        return record;
    }
    private static ControllerDescriptor[] Devices() => Enumerable.Range(0, 9).Select(slot => new ControllerDescriptor(slot, slot, "Device " + slot, "product", 255,
        new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), Guid.NewGuid(), Guid.NewGuid())).ToArray();
    private static ControllerBinding Binding(ControllerDescriptor device, ControllerControlKind kind, int axis, bool inverted = false, int engine = -1)
        => new ControllerBinding(kind, engine, device.InstanceId, axis, inverted, "MSFS profile");
    private static byte[] Catalog(ControllerDescriptor[] devices, params ControllerBinding[] bindings) => ControllerProvenanceCodec.Encode(new RuntimeEnvelope(
        new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers), 0, 1, 100, new ControllerCatalogMessage(1, devices, bindings)));
    private static void Attach(LandingRecord record, params (int Start, byte[] Catalog)[] segments)
    {
        record.ControllerTimeline = new ControllerCatalogTimeline { SampleCount = record.Series.Count,
            Catalogs = segments.Select(value => Convert.ToBase64String(value.Catalog)).ToArray(),
            Transitions = segments.Select((value, index) => new[] { value.Start, index }).ToArray() };
    }
    private static void Equal(double? actual, double expected, string message) => Check(actual.HasValue && Math.Abs(actual.Value - expected) < 1e-8, message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
