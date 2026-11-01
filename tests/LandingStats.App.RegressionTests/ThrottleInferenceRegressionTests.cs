using System;
using System.Globalization;
using System.IO;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ThrottleInferenceRegressionTests
{
    internal static void RunAll()
    {
        RepeatedMovementIdentifiesEitherPolarity();
        UnrelatedMotionRemainsUnknown();
        SynchronousCandidatesRemainUnknown();
        IndependentWindowsMustAgree();
        OneActionAndConstantCommandsRemainUnknown();
        MissingSamplesRemainMissing();
        RecordedControllerFixturesDoNotIdentifyUnrelatedCommands();
        SavedProfilesOrientPhysicalTravel();
    }

    private static void SavedProfilesOrientPhysicalTravel()
    {
        var actions = new[] { "KEY_THROTTLE_AXIS_SET_EX1", "KEY_AXIS_THROTTLE_SET", "KEY_THROTTLE_SET" }.Concat(
            Enumerable.Range(1, 4).SelectMany(engine => new[] { "KEY_THROTTLE" + engine + "_AXIS_SET_EX1", "KEY_AXIS_THROTTLE" + engine + "_SET", "KEY_THROTTLE" + engine + "_SET" }));
        foreach (var action in actions)
        foreach (var inverted in new[] { false, true })
        {
            var resolved = MsfsControlProfileRegressionTests.ThrottleDirectionFixture(action, inverted).Bindings;
            Require(resolved.Count > 0 && resolved.All(binding => binding.Inverted == inverted), "Profile parsing changed the original inversion flag.");
            var id = resolved[0].DeviceInstanceId;
            var device = new ControllerDescriptor(0, 0, "Physical throttle", "test", 255, new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), id);
            var bindings = resolved.Select(binding => new ControllerBinding(ControllerControlKind.Throttle, binding.EngineIndex,
                id, binding.AxisIndex, binding.Inverted, binding.ActionName)).ToArray();
            var polarity = inverted ? 1 : -1;
            // CL -> IDLE native travel reproduced from the local WinWing record;
            // the opposite hardware direction uses the non-inverted MSFS profile.
            var travel = new[] { 38.46, 38.46, -38.46, -38.46 };
            var record = Record(travel.Length, i => new[] { polarity * travel[i] }, i => 20 + 10 * i);
            var original = record.Series.Select(point => point.RawControllerAxesPercent[0]).ToArray();
            var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
            var catalog = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(worker, 0, 1, 100, new ControllerCatalogMessage(1, new[] { device }, bindings)));
            record.ControllerTimeline = new ControllerCatalogTimeline { SampleCount = record.Series.Count,
                Catalogs = new[] { Convert.ToBase64String(catalog) }, Transitions = new[] { new[] { 0, 0 } } };
            var inputs = ReplayControlInputs.Resolve(record);
            foreach (var engine in resolved.Select(binding => binding.EngineIndex))
            {
                Require(inputs.Throttle(engine)?.IsExplicitBinding == true && inputs.Throttle(engine)!.SourceIndex == 3 && inputs.Throttle(engine)!.Polarity == polarity,
                    "An explicit throttle must retain identity and derive display direction from the throttle profile baseline.");
                Require(Math.Abs(inputs.SampleThrottle(.05, engine)!.Value - 69.23) < 1e-8 && Math.Abs(inputs.SampleThrottle(.1, engine)!.Value - 30.77) < 1e-8,
                    "Pulling a physical throttle toward idle must lower its display even while the independent engine command rises.");
            }
            Require(record.Series.Select(point => point.RawControllerAxesPercent[0]).SequenceEqual(original) && bindings.All(binding => binding.Inverted == inverted),
                "Display normalization modified saved raw data or profile inversion.");
            record.Series[2].RawControllerAxesValid[0] = false;
            inputs = ReplayControlInputs.Resolve(record);
            Require(resolved.All(binding => inputs.HasPhysicalThrottle(binding.EngineIndex) && inputs.SampleThrottle(.1, binding.EngineIndex) == null),
                "An explicitly assigned missing physical sample must retain a gap.");
        }
    }

    private static void RepeatedMovementIdentifiesEitherPolarity()
    {
        foreach (var polarity in new[] { -1, 1 })
        foreach (var scale in new[] { .0001, 30.0 })
        foreach (var lag in new[] { 0, 3, 8 })
        {
            // Known physical source, nonstationary movement, lag, measurement
            // noise and another unrelated axis. No perfect sine-wave shortcut.
            var random = new Random(91); var motion = Motion(640, 53);
            var record = Record(640, i => new[] { polarity * scale * motion[i], 30 * (random.NextDouble() - .5) },
                i => 50 + 20 * motion[Math.Max(0, i - lag)] + .25 * Math.Sin(i * 1.7));
            var inputs = ReplayControlInputs.Resolve(record);
            Require(inputs.Throttle(0)?.SourceIndex == 3 && inputs.Throttle(0)!.Polarity == polarity,
                "Known repeated lever movements must identify their source and direction despite noise, lag and amplitude.");
            var point = record.Series[120];
            Require(Math.Abs(inputs.SampleThrottle(point.TimeSeconds, 0)!.Value - (100 + scale * motion[120]) / 2) < 1e-8,
                "Inference must align direction without applying its fitted gain or lag to physical display values.");
        }
    }

    private static void UnrelatedMotionRemainsUnknown()
    {
        for (var seed = 0; seed < 24; seed++)
        {
            var random = new Random(seed); var raw = new double[8]; var command = 0.0;
            var record = Record(600, _ =>
            {
                for (var axis = 0; axis < raw.Length; axis++) raw[axis] = raw[axis] * .8 + 10 * (random.NextDouble() - .5);
                return (double[])raw.Clone();
            }, _ => { command = command * .8 + 10 * (random.NextDouble() - .5); return 50 + command; });
            Require(ReplayControlInputs.Resolve(record).Throttle(0) == null,
                "A numerical winner among unrelated moving axes must not become a throttle; seed=" + seed);
        }
        var weak = Record(640, i => new[] { 20 * Math.Sin(i * .37) },
            i => 50 + .01 * Math.Sin(i * .37) + 20 * Math.Sin(i * 1.13));
        Require(ReplayControlInputs.Resolve(weak).Throttle(0) == null,
            "A tiny reproducible correlation cannot pass solely because it beats a zero prediction by epsilon.");
    }

    private static void SynchronousCandidatesRemainUnknown()
    {
        var motion = Motion(640, 73);
        var record = Record(640, i => new[] { 30 * motion[i], -15 * motion[i] + .05 * Math.Sin(i * 1.3) }, i => 50 + 20 * motion[i]);
        Require(ReplayControlInputs.Resolve(record).Throttle(0) == null,
            "Two synchronous levers remain unidentifiable even when their floating-point scores differ.");
    }

    private static void IndependentWindowsMustAgree()
    {
        var motion = Motion(640, 12);
        var record = Record(640, i => new[] { 30 * motion[i] }, i => 50 + (i < 320 ? 20 : -20) * motion[i]);
        Require(ReplayControlInputs.Resolve(record).Throttle(0) == null, "Opposite polarity in independent windows must remain unknown.");
        record = Record(640, i => new[] { 30 * motion[i] }, i => 50 + 20 * motion[Math.Max(0, i - (i < 320 ? 0 : 12))]);
        Require(ReplayControlInputs.Resolve(record).Throttle(0) == null,
            "Each held-out window must use the other window's lag; selecting a new lag during validation leaks evidence.");
    }

    private static void OneActionAndConstantCommandsRemainUnknown()
    {
        foreach (var values in new Func<int, double>[] { i => i < 320 ? 20 : -20, i => i * .05 - 20, i => Math.Floor(i / 16.0) - 20 })
        {
            var record = Record(640, i => new[] { values(i) }, i => 50 + values(i));
            Require(ReplayControlInputs.Resolve(record).Throttle(0) == null, "One action or uninterrupted ramp, including quantized plateaus, cannot validate itself twice.");
        }
        var constant = Record(640, i => new[] { 30 * Math.Sin(i * .23) }, _ => 50);
        Require(ReplayControlInputs.Resolve(constant).Throttle(0) == null, "A constant engine command cannot identify a moving lever.");
        constant = Record(640, _ => new[] { 30.0 }, i => 50 + 20 * Math.Sin(i * .23));
        Require(ReplayControlInputs.Resolve(constant).Throttle(0) == null, "Autothrottle movement cannot identify a stationary physical lever.");
    }

    private static void MissingSamplesRemainMissing()
    {
        var motion = Motion(640, 98);
        var record = Record(640, i => new[] { 30 * motion[i] }, i => 50 + 20 * motion[i]);
        record.Series[100].RawControllerAxesValid[0] = false;
        record.Series[200].RawControllerAxesAgeSeconds[0] = ReplayControlInputs.MaximumAgeSeconds + .01;
        var inputs = ReplayControlInputs.Resolve(record);
        Require(inputs.Throttle(0) != null && inputs.SampleThrottle(5, 0) == null && inputs.SampleThrottle(10, 0) == null,
            "Reliable source selection must preserve missing and stale display samples.");
        for (var index = 0; index < record.Series.Count; index++) record.Series[index].RawControllerAxesValid[0] = index % 2 == 0;
        Require(ReplayControlInputs.Resolve(record).Throttle(0) == null, "Inference cannot bridge missing samples into fictitious movements.");
    }

    private static void RecordedControllerFixturesDoNotIdentifyUnrelatedCommands()
    {
        for (var fixture = 1; fixture <= 6; fixture++)
        {
            var random = new Random(fixture); var command = 0.0;
            var record = new LandingRecord(); record.RawControllerAxesSourceIndices.AddRange(new[] { 0, 1, 2, 6, 7, 23 });
            var engine = new LandingEngineSeries { EngineNumber = 1 }; record.Engines.Add(engine);
            foreach (var line in File.ReadLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "physical-input-" + fixture + ".csv")).Skip(1))
            {
                var v = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                record.Series.Add(new LandingSeriesPoint { TimeSeconds = v[0], OnGround = v[1] != 0, PilotPitchPercent = v[2], PilotRollPercent = v[3], PilotYawPercent = v[4],
                    RawControllerAxesPercent = v.Skip(5).ToArray(), RawControllerAxesValid = Enumerable.Repeat(true, 6).ToArray(), RawControllerAxesAgeSeconds = new double[6] });
                command = command * .8 + 10 * (random.NextDouble() - .5);
                engine.Points.Add(new LandingEnginePoint { TimeSeconds = v[0], ThrottlePercent = 50 + command });
            }
            Require(ReplayControlInputs.Resolve(record).Throttle(0) == null,
                "Real recorded controller motion must not identify an unrelated synthetic engine command; fixture=" + fixture);
        }
    }

    private static double[] Motion(int count, int seed)
    {
        var random = new Random(seed); var result = new double[count]; var target = 0.0;
        for (var index = 1; index < count; index++)
        {
            if (index % 17 == 1) target = random.NextDouble() * 1.6 - .8;
            result[index] = result[index - 1] + Math.Max(-.12, Math.Min(.12, target - result[index - 1]));
        }
        return result;
    }

    private static LandingRecord Record(int count, Func<int, double[]> values, Func<int, double> command)
    {
        var record = new LandingRecord { ControllerAxisCount = 8 };
        var engine = new LandingEngineSeries { EngineNumber = 1 }; record.Engines.Add(engine);
        for (var index = 0; index < count; index++)
        {
            var raw = values(index); if (index == 0) record.RawControllerAxesSourceIndices.AddRange(Enumerable.Range(3, raw.Length));
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = index * .05,
                RawControllerAxesPercent = raw, RawControllerAxesValid = Enumerable.Repeat(true, raw.Length).ToArray(), RawControllerAxesAgeSeconds = new double[raw.Length] });
            engine.Points.Add(new LandingEnginePoint { TimeSeconds = index * .05, ThrottlePercent = command(index) });
        }
        return record;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
