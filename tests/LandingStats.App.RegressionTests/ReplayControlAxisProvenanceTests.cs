using System;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ReplayControlAxisProvenanceTests
{
    public static void FullAxisBindingsDoNotDependOnAxisNames()
    {
        // Three separate one-axis devices may expose any physical axis name.
        // Identical evidence must identify the same roles for X, Y, Z, R, U or V.
        for (var axis = 0; axis < TelemetrySample.CapturedControllerAxisCount; axis++)
        {
            var record = new LandingRecord();
            record.RawControllerAxesSourceIndices.AddRange(new[] { axis, 6 + axis, 12 + axis });
            for (var i = 0; i < 600; i++)
            {
                var pitch = 30 * Math.Sin(i * .17);
                var roll = 20 * Math.Sin(i * .31);
                var yaw = 25 * Math.Cos(i * .23);
                record.Series.Add(new LandingSeriesPoint
                {
                    TimeSeconds = i * .05,
                    PilotPitchPercent = pitch, PilotRollPercent = roll, PilotYawPercent = yaw,
                    RawControllerAxesPercent = new[] { pitch, -roll, yaw },
                    RawControllerAxesValid = new[] { true, true, true },
                    RawControllerAxesAgeSeconds = new double[3],
                });
            }
            var inputs = ReplayControlInputs.Resolve(record);
            Require(inputs.Pitch?.SourceIndex == axis && inputs.Roll?.SourceIndex == 6 + axis &&
                    inputs.Yaw?.SourceIndex == 12 + axis, "physical axis names suppressed a valid binding: " + axis);
            Require(inputs.Roll!.Correlation < 0, "axis polarity lost: " + axis);
        }
    }

    public static void ImportedLegacyCsvRetainsItsLayout()
    {
        var samples = Enumerable.Range(0, 60).Select(i =>
        {
            var sample = new TelemetrySample
            {
                Sequence = i, SimulationTimeSeconds = 100 + i * .05, MotionSimulation = true,
                OnGround = i >= 40, GForce = 1.1, VelocityWorldYFps = -2,
                AboveGroundLevelFeet = i < 40 ? 20 : 0,
            };
            sample.RawControllerYAxisPercent[0] = 30 * Math.Sin(i * .2);
            sample.RawControllerYAxisValid[0] = true;
            sample.PilotPitchInputPercent = -sample.RawControllerYAxisPercent[0];
            var row = string.Join(",", TelemetryCsv.Format(sample).Split(',').Take(TelemetryCsv.V5Header.Split(',').Length));
            Require(TelemetryCsv.TryParse(row, out var parsed), "legacy CSV parsing");
            Require(!parsed.RawControllerAxesValid.Any(valid => valid), "legacy CSV gained full-axis provenance");
            return parsed;
        }).ToArray();
        var result = TouchdownAnalysis.Analyze(samples).Single();
        var record = LandingRecordFactory.Create(result, samples, "Fixture", "TEST", "TEST", "00", 1);
        Require(record.RawControllerAxesSourceIndices.Count == 0 && record.RawControllerSourceIndices.SequenceEqual(new[] { 0 }),
            "legacy layout changed while creating a landing record");
        var inputs = ReplayControlInputs.Resolve(record);
        Require(inputs.Pitch != null && inputs.Pitch.Legacy && inputs.Roll == null && inputs.Yaw == null,
            "legacy import invented a full stick or discarded physical pitch");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
