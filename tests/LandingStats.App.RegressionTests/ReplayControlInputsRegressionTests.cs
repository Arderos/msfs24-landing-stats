using System;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class ReplayControlInputsRegressionTests
{
    public static void RunAll()
    {
        var record = Fixture();
        var inputs = ReplayControlInputs.Resolve(record);
        Require(inputs.Pitch?.SourceIndex == 11 && inputs.Roll?.SourceIndex == 4 && inputs.Yaw?.SourceIndex == 15,
            "bound controls follow recorded evidence across arbitrary Windows axes and devices");
        Require(inputs.Pitch!.SourceLabel == "Stick · V" && inputs.Yaw!.SourceLabel == "Yaw controller · R", "source labels retain real axis provenance");
        var sample = inputs.Sample(3.025);
        Near((record.Series[60].PilotPitchPercent + record.Series[61].PilotPitchPercent) / 2, sample.PitchPercent, "inverted pitch gives positive pull");
        Near((record.Series[60].PilotRollPercent + record.Series[61].PilotRollPercent) / 2, sample.RollPercent, "roll gives positive right");
        Near((record.Series[60].PilotYawPercent + record.Series[61].PilotYawPercent) / 2, sample.YawPercent, "inverted pedal axis gives positive right");
        Require(sample.HasStick, "two independently resolved axes give full stick position");

        record.Series[60].RawControllerAxesValid[0] = false;
        sample = inputs.Sample(3.025);
        Require(!sample.PitchPercent.HasValue && sample.RollPercent.HasValue && sample.YawPercent.HasValue, "invalid endpoint only hides its own dimension");
        record.Series[60].RawControllerAxesValid[0] = true;
        record.Series[60].RawControllerAxesAgeSeconds[1] = 0.251;
        Require(!inputs.Sample(3.025).RollPercent.HasValue, "stale raw values are not rendered as fresh physical input");
        record.Series[60].RawControllerAxesAgeSeconds[1] = double.NaN;
        Require(!inputs.Sample(3).RollPercent.HasValue, "unknown raw age is not valid");
        Require(!inputs.Sample(-1).HasStick && !inputs.Sample(99).HasStick, "no endpoint clamping invents input outside recorded time");

        var gapRecord = Fixture();
        gapRecord.Series.RemoveAll(point => point.TimeSeconds > 3 && point.TimeSeconds < 4);
        var gapInputs = ReplayControlInputs.Resolve(gapRecord);
        Require(gapInputs.Pitch != null && !gapInputs.Sample(3.5).PitchPercent.HasValue, "identified axes never bridge telemetry gaps");

        var legacy = Fixture();
        legacy.RawControllerAxesSourceIndices.Clear();
        legacy.RawControllerSourceIndices.Add(5);
        foreach (var point in legacy.Series)
        {
            point.RawControllerYAxisPercent = new[] { -point.PilotPitchPercent };
            point.RawControllerYAxisValid = new[] { true };
            point.RawControllerYAxisAgeSeconds = new[] { 0.0 };
        }
        var legacyInputs = ReplayControlInputs.Resolve(legacy);
        Require(legacyInputs.Pitch?.SourceIndex == 31 && legacyInputs.Pitch.Legacy && legacyInputs.Roll == null && legacyInputs.Yaw == null,
            "legacy Y-only history does not gain fictional roll or pedals");
        Near(legacy.Series[50].PilotPitchPercent, legacyInputs.Sample(2.5).PitchPercent, "legacy compact column keeps polarity");
        legacy.RawPitchInputSourceIndex = 5;
        legacy.RawPitchInputCorrelation = -.76;
        legacy.RawPitchInputLagSeconds = .3;
        Require(ReplayControlInputs.Resolve(legacy).Pitch?.Correlation == -.76, "existing accepted legacy pitch is not silently tightened by a new auto-mapper");
        Require(ReplayControlInputs.Resolve(legacy).Pitch?.Legacy == true,
            "historical Y-only layout remains explicit rather than inferred from physical axis names");

        // Stored old selection alone is not proof: current valid samples must support it.
        var missing = new LandingRecord { FormatVersion = 7, RawPitchInputSourceIndex = 0, RawPitchInputCorrelation = 1 };
        for (var index = 0; index < 100; index++) missing.Series.Add(new LandingSeriesPoint { TimeSeconds = index * .05, PilotPitchPercent = 20 * Math.Sin(index * .2) });
        var noInputs = ReplayControlInputs.Resolve(missing);
        Require(noInputs.Pitch == null && noInputs.Roll == null && noInputs.Yaw == null && !noInputs.Sample(1).PitchPercent.HasValue,
            "missing physical channels remain absent instead of using simulator commands or neutral zero");

        var ambiguous = Fixture();
        ambiguous.RawControllerAxesSourceIndices.Add(0);
        foreach (var point in ambiguous.Series)
        {
            point.RawControllerAxesPercent = new[] { point.RawControllerAxesPercent[0], point.RawControllerAxesPercent[1], point.RawControllerAxesPercent[2], point.RawControllerAxesPercent[0] };
            point.RawControllerAxesValid = new[] { true, true, true, true };
            point.RawControllerAxesAgeSeconds = new[] { 0.0, 0.0, 0.0, 0.0 };
        }
        Require(ReplayControlInputs.Resolve(ambiguous).Pitch?.SourceIndex == 0, "same-device evidence resolves equal pitch candidates in favour of the recorded roll device");

        var closeRunner = new LandingRecord();
        closeRunner.RawControllerAxesSourceIndices.AddRange(new[] { 0, 1 });
        for (var i = 0; i < 400; i++)
        {
            var signal = Math.Sin(2 * Math.PI * i / 100);
            // Each candidate wins different independent stretches. A slightly
            // higher whole-record correlation is not a consistent binding.
            var quality = i / 40 % 2 == 0 ? .93 : .76;
            var otherQuality = i / 40 % 2 == 0 ? .76 : .93;
            var first = quality * signal + Math.Sqrt(1 - quality * quality) * Math.Sin(2 * Math.PI * i / 4);
            var second = otherQuality * signal + Math.Sqrt(1 - otherQuality * otherQuality) * Math.Cos(2 * Math.PI * i / 4);
            closeRunner.Series.Add(new LandingSeriesPoint { TimeSeconds = i * .05, PilotPitchPercent = signal * 30,
                RawControllerAxesPercent = new[] { first * 30, second * 30 }, RawControllerAxesValid = new[] { true, true }, RawControllerAxesAgeSeconds = new[] { 0.0, 0.0 } });
        }
        Require(ReplayControlInputs.Resolve(closeRunner).Pitch != null, "relative ranking does not reject close competitors because local windows disagree");

        var coordinated = Fixture();
        foreach (var point in coordinated.Series) point.PilotYawPercent = point.PilotRollPercent;
        var coordinatedInputs = ReplayControlInputs.Resolve(coordinated);
        Require(coordinatedInputs.Roll?.SourceIndex != coordinatedInputs.Yaw?.SourceIndex, "one physical channel is not assigned to both roll and pedals");

        var threeRoles = Fixture();
        foreach (var point in threeRoles.Series) point.PilotYawPercent = point.PilotRollPercent = point.PilotPitchPercent;
        var triple = ReplayControlInputs.Resolve(threeRoles);
        Require(triple.Pitch == null && triple.Roll == null, "identical role scores leave the pitch/roll assignment tied");

        var lagAcrossGaps = new LandingRecord();
        lagAcrossGaps.RawControllerAxesSourceIndices.Add(0);
        for (var i = 0; i < 40; i++)
        {
            var value = 35 * Math.Sin(i * .7);
            lagAcrossGaps.Series.Add(new LandingSeriesPoint { TimeSeconds = i * 2, PilotPitchPercent = double.NaN,
                RawControllerAxesPercent = new[] { value }, RawControllerAxesValid = new[] { true }, RawControllerAxesAgeSeconds = new[] { 0.0 } });
            lagAcrossGaps.Series.Add(new LandingSeriesPoint { TimeSeconds = i * 2 + .75, PilotPitchPercent = value });
        }
        Require(ReplayControlInputs.Resolve(lagAcrossGaps).Pitch == null, "lag matching cannot leap across disconnected telemetry segments");

        var waggle = new LandingRecord();
        waggle.RawControllerAxesSourceIndices.Add(0);
        for (var i = 0; i < 400; i++)
        {
            var raw = i % 12 < 6 ? 40.0 : -40.0;
            waggle.Series.Add(new LandingSeriesPoint { TimeSeconds = i * .05, PilotPitchPercent = -raw,
                RawControllerAxesPercent = new[] { raw }, RawControllerAxesValid = new[] { true }, RawControllerAxesAgeSeconds = new[] { 0.0 } });
        }
        Require(ReplayControlInputs.Resolve(waggle).Pitch?.Correlation < 0, "equal periodic fits prefer the earliest response, without a polarity confidence cutoff");

        var stationary = Fixture();
        foreach (var point in stationary.Series) { point.PilotYawPercent = 0; point.RawControllerAxesPercent[2] = 0; }
        Require(ReplayControlInputs.Resolve(stationary).Yaw == null, "constant channel does not establish a pedal binding");

        var delayed = Fixture();
        for (var i = delayed.Series.Count - 1; i >= 0; i--)
            delayed.Series[i].PilotPitchPercent = i >= 4 ? -delayed.Series[i - 4].RawControllerAxesPercent[0] : double.NaN;
        var delayedInputs = ReplayControlInputs.Resolve(delayed);
        Near(.2, delayedInputs.Pitch?.LagSeconds, "control response lag can identify the source");
        Near(-delayed.Series[50].RawControllerAxesPercent[0], delayedInputs.Sample(2.5).PitchPercent, "identified lag does not shift physical input playback in time");

        var duplicate = Fixture();
        duplicate.RawControllerAxesSourceIndices[1] = duplicate.RawControllerAxesSourceIndices[0];
        Require(ReplayControlInputs.Resolve(duplicate).Pitch == null && ReplayControlInputs.Resolve(duplicate).Roll == null,
            "malformed duplicate source mapping is not assigned");

        // Fenix report c9b08468: clear stick motion in the air, attenuated roll
        // command, then unrelated ground-mode pitch/roll values after touchdown.
        var phased = Fixture();
        for (var i = 0; i < phased.Series.Count; i++)
        {
            var p = phased.Series[i]; p.OnGround = i >= 280;
            if (!p.OnGround) { p.PilotPitchPercent *= .15; p.PilotRollPercent *= .02; }
            else
            {
                p.RawControllerAxesPercent[0] = p.RawControllerAxesPercent[1] = 0;
                p.PilotPitchPercent = 25 + 5 * Math.Sin(i * .7);
                p.PilotRollPercent = 4 * Math.Cos(i * .4);
            }
        }
        var phasedInputs = ReplayControlInputs.Resolve(phased);
        Require(phasedInputs.Pitch?.SourceIndex == 11 && phasedInputs.Roll?.SourceIndex == 4 && phasedInputs.Yaw?.SourceIndex == 15,
            "ground-mode commands do not erase airborne pitch and attenuated roll matches");
        Near(-phased.Series[50].RawControllerAxesPercent[0], phasedInputs.Sample(2.5).PitchPercent, "phase matching preserves original physical amplitude");
        Near(phased.Series[50].RawControllerAxesPercent[1], phasedInputs.Sample(2.5).RollPercent, "attenuated simulator roll is not substituted for physical roll");
        Require(phasedInputs.Sample((phased.Series[279].TimeSeconds + phased.Series[280].TimeSeconds) / 2).HasStick,
            "physical input remains visible between samples straddling touchdown; only matching separates flight phases");
        var phasedConflict = Fixture();
        for (var i = 0; i < phasedConflict.Series.Count; i++)
        {
            var p = phasedConflict.Series[i]; p.OnGround = i >= 200;
            if (p.OnGround) p.PilotPitchPercent = p.RawControllerAxesPercent[1];
        }
        Require(ReplayControlInputs.Resolve(phasedConflict).Pitch?.SourceIndex == 11, "ground-mode processing does not veto the approach source");
        foreach (var p in phasedConflict.Series)
            if (p.OnGround) p.PilotPitchPercent = p.RawControllerAxesPercent[0];
        Require(ReplayControlInputs.Resolve(phasedConflict).Pitch?.Correlation < 0, "ground-mode processing does not reverse the approach polarity");
        var quantization = Fixture();
        foreach (var p in quantization.Series) p.PilotRollPercent = Math.Round(p.PilotRollPercent * .001, 2);
        Require(ReplayControlInputs.Resolve(quantization).Roll?.SourceIndex == 4, "small stored command amplitudes are still ranked instead of rejected by an amplitude cutoff");
    }

    private static LandingRecord Fixture()
    {
        var record = new LandingRecord { FormatVersion = 7 };
        record.RawControllerAxesSourceIndices.AddRange(new[] { 11, 4, 15 });
        record.ControlInputSources.AddRange(new[] { "Other controller", "Stick", "Yaw controller" });
        for (var index = 0; index < 400; index++)
        {
            var time = index * .05;
            var pitch = 32 * Math.Sin(time * 1.13) + 9 * Math.Sin(time * 4.2);
            var roll = 27 * Math.Sin(time * 2.31) + 7 * Math.Cos(time * 5.7);
            var yaw = 25 * Math.Cos(time * .73) + 6 * Math.Sin(time * 6.1);
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = time, PilotPitchPercent = pitch, PilotRollPercent = roll, PilotYawPercent = yaw,
                RawControllerAxesPercent = new[] { -pitch, roll, -yaw }, RawControllerAxesValid = new[] { true, true, true },
                RawControllerAxesAgeSeconds = new[] { 0.0, 0.0, 0.0 } });
        }
        return record;
    }

    private static void Near(double expected, double? actual, string message)
    {
        Require(actual.HasValue && Math.Abs(expected - actual.Value) < 1e-7, message + ": expected " + expected + ", actual " + actual);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
