using System;
using System.Globalization;
using System.IO;
using System.Linq;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

internal static class ReplayControlEvidenceRegressionTests
{
    public static void RunAll()
    {
        // Only relative time, command channels and physical axes from real
        // reports. No aircraft/device identity or geographic data is needed.
        foreach (var fixture in new[] { "physical-input-1.csv", "physical-input-2.csv", "physical-input-3.csv", "physical-input-4.csv", "physical-input-5.csv", "physical-input-6.csv" })
        foreach (var shifted in new[] { false, true })
        {
            var record = Read(fixture);
            if (shifted)
            {
                // Rename devices and axes while preserving which channels belong
                // to the same device: that relationship is now useful evidence.
                record.RawControllerAxesSourceIndices = new[] { 44, 42, 47, 17, 16, 0 }.ToList();
                foreach (var p in record.Series)
                {
                    p.TimeSeconds -= 1.523;
                    p.RawControllerAxesPercent[1] *= -1;
                    p.RawControllerAxesPercent[2] *= -1;
                }
            }
            var inputs = ReplayControlInputs.Resolve(record);
            Require(inputs.Pitch?.SourceIndex == (shifted ? 42 : 1) && inputs.Roll?.SourceIndex == (shifted ? 44 : 0) &&
                inputs.Yaw?.SourceIndex == (shifted ? 47 : 2), "real input evidence survives renamed/permuted devices, inverted axes and another contact's time origin: " + fixture);
            var point = record.Series.OrderByDescending(p => Math.Abs(p.RawControllerAxesPercent[1])).First();
            Require(Math.Abs(inputs.Sample(point.TimeSeconds).PitchPercent!.Value - (shifted ? -1 : 1) * point.RawControllerAxesPercent[1]) < 1e-8,
                "matching preserves original physical amplitude and sample time");
        }

        var flare = new LandingRecord();
        flare.RawControllerAxesSourceIndices.AddRange(new[] { 28, 5 });
        for (var i = 0; i < 1200; i++)
        {
            var t = i * .05;
            var pitch = 6 * Math.Sin(t * 1.13) + 2 * Math.Cos(t * 3.7) + (t >= 50 ? 30 : 0);
            flare.Series.Add(new LandingSeriesPoint { TimeSeconds = t, PilotPitchPercent = pitch * .12,
                RawControllerAxesPercent = new[] { pitch, t >= 50 ? -38.0 : 38.0 },
                RawControllerAxesValid = new[] { true, true }, RawControllerAxesAgeSeconds = new double[2] });
        }
        Require(ReplayControlInputs.Resolve(flare).Pitch?.SourceIndex == 28, "repeated pitch movements beat a coincident throttle step at flare");
        foreach (var p in flare.Series) p.RawControllerAxesValid[0] = false;
        Require(ReplayControlInputs.Resolve(flare).Pitch?.SourceIndex == 5, "a sole varying candidate is ranked rather than vetoed by a confidence rule");

        var small = Read("physical-input-1.csv");
        foreach (var p in small.Series) p.RawControllerAxesPercent[0] *= .03;
        Require(ReplayControlInputs.Resolve(small).Roll?.SourceIndex == 0, "small physical deflections are not rejected by the old four-percent range gate");

        for (var seed = 0; seed < 32; seed++)
        {
            var random = new Random(seed);
            var noise = new LandingRecord(); noise.RawControllerAxesSourceIndices.AddRange(Enumerable.Range(0, 8).Select(i => i * 6 + 1));
            var axes = new double[8]; var commands = new double[3];
            for (var i = 0; i < 800; i++)
            {
                for (var j = 0; j < axes.Length; j++) axes[j] = axes[j] * .8 + (random.NextDouble() - .5) * 15;
                for (var j = 0; j < commands.Length; j++) commands[j] = commands[j] * .8 + (random.NextDouble() - .5) * 15;
                noise.Series.Add(new LandingSeriesPoint { TimeSeconds = i * .05, PilotPitchPercent = commands[0], PilotRollPercent = double.NaN, PilotYawPercent = double.NaN,
                    RawControllerAxesPercent = (double[])axes.Clone(), RawControllerAxesValid = Enumerable.Repeat(true, 8).ToArray(), RawControllerAxesAgeSeconds = new double[8] });
            }
            var mapped = ReplayControlInputs.Resolve(noise);
            var reference = (from column in Enumerable.Range(0, 8) from lag in Enumerable.Range(0, 17)
                             let x = noise.Series.Take(800 - lag).Select(p => p.RawControllerAxesPercent[column]).ToArray()
                             let y = noise.Series.Skip(lag).Select(p => p.PilotPitchPercent).ToArray()
                             let dx = x.Skip(1).Zip(x, (next, previous) => next - previous).ToArray()
                             let dy = y.Skip(1).Zip(y, (next, previous) => next - previous).ToArray()
                             let score = Math.Abs(dx.Zip(dy, (a, b) => a * b).Sum() /
                                 Math.Sqrt(dx.Sum(a => a * a) * dy.Sum(b => b * b)))
                             orderby score descending select new { Source = column * 6 + 1, Score = score }).First();
            Require(mapped.Pitch?.SourceIndex == reference.Source && Math.Abs(mapped.Pitch.Correlation) < .75,
                "even weak input ranks follow the independent whole-record movement reference, without a confidence veto; seed=" + seed);
        }

        PairEvidence();
        JointRoles();
        LinearRamps();
    }

    private static void JointRoles()
    {
        var random = new Random(73);
        var record = new LandingRecord();
        record.RawControllerAxesSourceIndices.AddRange(new[] { 0, 1, 2 });
        for (var i = 0; i < 4000; i++)
        {
            var pitch = random.NextDouble() - .5;
            var roll = random.NextDouble() - .5;
            var yaw = random.NextDouble() - .5;
            var noise = random.NextDouble() - .5;
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = i * .05,
                PilotPitchPercent = pitch * 20, PilotRollPercent = (roll * .2 + yaw * .3 + noise) * 20,
                PilotYawPercent = yaw * 20, RawControllerAxesPercent = new[] { pitch * 20, roll * 20, yaw * 20 },
                RawControllerAxesValid = new[] { true, true, true }, RawControllerAxesAgeSeconds = new double[3] });
        }
        var inputs = ReplayControlInputs.Resolve(record);
        Require(inputs.Pitch?.SourceIndex == 0 && inputs.Roll?.SourceIndex == 1 && inputs.Yaw?.SourceIndex == 2,
            "a weak roll association cannot consume the axis with decisive yaw evidence before yaw is ranked");

        // Belonging to the stick must not break a tie between pedal candidates.
        record.RawControllerAxesSourceIndices.Add(6);
        foreach (var point in record.Series)
        {
            point.PilotRollPercent = point.RawControllerAxesPercent[1];
            point.RawControllerAxesPercent = point.RawControllerAxesPercent.Concat(new[] { point.PilotYawPercent }).ToArray();
            point.RawControllerAxesValid = new[] { true, true, true, true };
            point.RawControllerAxesAgeSeconds = new double[4];
        }
        inputs = ReplayControlInputs.Resolve(record);
        Require(inputs.Pitch?.SourceIndex == 0 && inputs.Roll?.SourceIndex == 1 && inputs.Yaw == null,
            "same-device evidence favours pitch/roll only, not a stick twist over equally correlated separate pedals");
        foreach (var point in record.Series)
            point.RawControllerAxesPercent[2] += (random.NextDouble() - .5) * 10;
        Require(ReplayControlInputs.Resolve(record).Yaw?.SourceIndex == 6,
            "stronger separate pedal evidence wins without a device preference");

        record.RawControllerAxesSourceIndices.RemoveRange(2, 2);
        foreach (var point in record.Series)
        {
            point.PilotRollPercent = .1 * point.RawControllerAxesPercent[1] + (random.NextDouble() - .5) * 20;
            point.PilotYawPercent = point.RawControllerAxesPercent[1];
            point.RawControllerAxesPercent = point.RawControllerAxesPercent.Take(2).ToArray();
            point.RawControllerAxesValid = new[] { true, true };
            point.RawControllerAxesAgeSeconds = new double[2];
        }
        inputs = ReplayControlInputs.Resolve(record);
        Require(inputs.Pitch?.SourceIndex == 0 && inputs.Roll == null && inputs.Yaw?.SourceIndex == 1,
            "with two available axes the strongest two roles win, not the roles visited first");
    }

    private static void LinearRamps()
    {
        var record = new LandingRecord(); record.RawControllerAxesSourceIndices.Add(19);
        for (var i = 0; i < 80; i++)
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = i * .05, PilotPitchPercent = i * -.0003,
                RawControllerAxesPercent = new[] { 43 + i * .0001 },
                RawControllerAxesValid = new[] { true }, RawControllerAxesAgeSeconds = new[] { 0d } });
        var selected = ReplayControlInputs.Resolve(record).Pitch;
        Require(selected?.SourceIndex == 19 && selected.Correlation < -.999999,
            "steady, tiny, offset and inverted movement remains identifiable without variance or amplitude cutoffs");
        for (var i = 0; i < record.Series.Count; i++) record.Series[i].TimeSeconds = i * 1e-12;
        Require(ReplayControlInputs.Resolve(record).Pitch?.Correlation < -.999999,
            "near-zero sample intervals cannot amplify motion because correlation never divides by dt");
        for (var i = 0; i < record.Series.Count; i++) record.Series[i].RawControllerAxesValid[0] = i % 2 == 0;
        Require(ReplayControlInputs.Resolve(record).Pitch == null,
            "missing intermediate samples cannot be bridged into invented matching movements");
    }

    private static void PairEvidence()
    {
        var random = new Random(42);
        var vectors = new double[5][];
        for (var j = 0; j < vectors.Length; j++)
        {
            var v = Enumerable.Range(0, 1000).Select(_ => random.NextDouble() - .5).ToArray();
            var mean = v.Average(); for (var i = 0; i < v.Length; i++) v[i] -= mean;
            for (var k = 0; k < j; k++)
            {
                var projection = v.Zip(vectors[k], (a, b) => a * b).Average();
                for (var i = 0; i < v.Length; i++) v[i] -= projection * vectors[k][i];
            }
            var rms = Math.Sqrt(v.Average(a => a * a)); for (var i = 0; i < v.Length; i++) v[i] /= rms;
            vectors[j] = v;
        }
        foreach (var otherRoll in new[] { .72, .99 })
        foreach (var sources in new[] { new[] { 12, 14, 3 }, new[] { 44, 42, 19 } })
        {
            var record = new LandingRecord(); record.RawControllerAxesSourceIndices.AddRange(sources);
            for (var i = 0; i < 1000; i++)
                record.Series.Add(new LandingSeriesPoint { TimeSeconds = i * .05,
                    PilotPitchPercent = vectors[0][i] * 10, PilotRollPercent = vectors[1][i] * 10,
                    RawControllerAxesPercent = new[] {
                        10 * (.7 * vectors[0][i] + Math.Sqrt(1 - .7 * .7) * vectors[2][i]),
                        -10 * (.65 * vectors[1][i] + Math.Sqrt(1 - .65 * .65) * vectors[3][i]),
                        10 * (otherRoll * vectors[1][i] + Math.Sqrt(1 - otherRoll * otherRoll) * vectors[4][i]) },
                    RawControllerAxesValid = new[] { true, true, true }, RawControllerAxesAgeSeconds = new double[3] });
            var mapped = ReplayControlInputs.Resolve(record);
            Require(mapped.Pitch?.SourceIndex == sources[0] && mapped.Roll?.SourceIndex == (otherRoll < .9 ? sources[1] : sources[2]),
                "same-device evidence strongly favours .65 over .72, but .99 on another device can outweigh it");
            Require(otherRoll > .9 || mapped.Roll!.Correlation < 0, "device preference does not discard inverted axes");
        }
    }

    private static LandingRecord Read(string name)
    {
        var record = new LandingRecord();
        record.RawControllerAxesSourceIndices.AddRange(new[] { 0, 1, 2, 6, 7, 23 });
        foreach (var line in File.ReadLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", name)).Skip(1))
        {
            var v = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = v[0], OnGround = v[1] != 0, PilotPitchPercent = v[2], PilotRollPercent = v[3], PilotYawPercent = v[4],
                RawControllerAxesPercent = v.Skip(5).ToArray(), RawControllerAxesValid = Enumerable.Repeat(true, 6).ToArray(), RawControllerAxesAgeSeconds = new double[6] });
        }
        return record;
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
