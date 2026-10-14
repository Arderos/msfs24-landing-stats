using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class BacklogGeometryAndHistoryRegressionTests
{
    public static void ReferenceDatumIsAppliedExactlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-datum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var common = Path.Combine(root, "common.cfg");
            var attachment = Path.Combine(root, "attachment.cfg");
            // Prescribed model-frame mains -10/-12 and nose +20 are unchanged
            // by moving the arbitrary configuration reference origin.
            foreach (var shift in new[] { -120.0, 0.0, 70.0 })
            {
                File.WriteAllText(common, "[WEIGHT_AND_BALANCE]\nreference_datum_position = " + N(shift) + ", 4, -2\n" +
                    "[CONTACT_POINTS]\npoint.0 = 1, " + N(20 - shift) + ", 0, -5\n" +
                    "point.1 = 1, " + N(-10 - shift) + ", -7, -5\n");
                File.WriteAllText(attachment, "[CONTACT_POINTS]\npoint.0 = 1, " + N(-12 - shift) + ", 7, -5\n");
                Assert(FlightModelConfigParser.TryReadGearGeometry(new[] { common, attachment }, out var g), "shifted CFG rejected");
                Assert(g.MainGearLongitudinalFeet == -11 && g.ReferenceDatumLongitudinalFeet == shift, "datum or median applied incorrectly");
                Assert(g.MainGearPoints.Select(p => p.LongitudinalFeet).SequenceEqual(new[] { -10.0, -12.0 }), "individual coordinates changed");
                Assert(g.MainGearPoints.Select(p => p.ContactPointIndex).SequenceEqual(new[] { 1, 2 }), "merge indices changed");
            }
            foreach (var bad in new[] { "0,0", "NaN,0,0", "70,Infinity,0", "[dynamic],0,0", "71,4,-2", "70,4,-3" })
            {
                File.AppendAllText(attachment, "\n[WEIGHT_AND_BALANCE]\nreference_datum_position=" + bad + "\n");
                Assert(!FlightModelConfigParser.TryReadGearGeometry(new[] { common, attachment }, out _), "ambiguous/invalid datum accepted: " + bad);
                File.WriteAllText(attachment, "[CONTACT_POINTS]\npoint.0=1,-82,7,-5\n");
            }
            File.AppendAllText(attachment, "[WEIGHT_AND_BALANCE]\nreference_datum_position=70,4,-2\n");
            Assert(FlightModelConfigParser.TryReadGearGeometry(new[] { common, attachment }, out var same) && same.MainGearLongitudinalFeet == -11,
                "consistent duplicate datum must not apply twice");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void HistoryPreservesShortDiscretePhases()
    {
        foreach (var offset in Enumerable.Range(0, 5))
        foreach (var duration in new[] { 1, 2 })
        foreach (var inverted in new[] { false, true })
        {
            var samples = new List<TelemetrySample>();
            var start = 21 + offset;
            for (var i = 0; i < 100; i++)
            {
                var ground = (i >= start && i < start + duration) != inverted;
                var s = new TelemetrySample
                {
                    Sequence = i, SimulationTimeSeconds = 100 + i * .01, HostElapsedSeconds = i * .01,
                    MotionSimulation = true, OnGround = ground, AboveGroundLevelFeet = 5,
                    PlaneAltitudeFeet = 105, GroundAltitudeFeet = 100, GForce = 1,
                    LatitudeDegrees = 40, LongitudeDegrees = 20, HeadingTrueDegrees = 90,
                };
                s.ContactPointOnGround[0] = ground;
                s.ContactPointCompression[0] = ground ? .5 : 0;
                s.ContactPointPosition[0] = 1;
                samples.Add(s);
            }
            var result = new TouchdownResult
            {
                Sequence = start, SimulationTimeSeconds = samples[start].SimulationTimeSeconds,
                EstimatedContactTimeSeconds = samples[start].SimulationTimeSeconds - .005,
                EpisodeNumber = 1, ContactNumber = 1, InertialVerticalFpm = 123,
                LatchedNormalFpm = 145, ReconstructedClosureFpm = 167,
            };
            var record = LandingRecordFactory.Create(result, samples, "test", "test");
            var restored = LandingRecordFile.FromRecord(record).ToRecord();
            foreach (var index in new[] { start - 1, start, start + duration - 1, start + duration })
            {
                var expected = samples[index];
                var t = Math.Round(expected.SimulationTimeSeconds - result.EstimatedContactTimeSeconds, 4);
                Assert(restored.Series.Any(p => p.TimeSeconds == t && p.OnGround == expected.OnGround), "discrete phase lost");
            }
            Assert(restored.Series.Zip(restored.Series.Skip(1), (a,b) => a.TimeSeconds < b.TimeSeconds).All(x => x), "history time is not ordered");
            Assert(restored.ContactPoints.All(c => c.Points.Select(p => p.TimeSeconds).SequenceEqual(restored.Series.Select(p => p.TimeSeconds))), "contact columns misaligned");
            Assert(restored.InertialFpm == 123 && restored.SurfaceFpm == 145 && restored.ReconstructedClosureFpm == 167, "sampling changed metrics");
            Assert(restored.Series.Count < 30, "edge retention disabled ordinary downsampling");
        }
    }

    public static void GeometryInputsRoundTripWithoutInstalledAircraft()
    {
        var samples = ContactPointGeometryRegressionTests.Recorded("13dc14ff");
        foreach (var configured in new[] { false, true })
        {
            var options = configured ? new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.5, LongitudinalMainGearArmQuality = .7,
                LongitudinalMainGearArmSource = TouchdownGeometrySource.FlightModelConfig,
                MainGearContactPoints = new[] { new TouchdownMainGearContactPoint(1, -8), new TouchdownMainGearContactPoint(2, -9) },
                NoseGearContactPointIndices = new[] { 0 },
            } : new TouchdownAnalysisOptions();
            var before = TouchdownAnalysis.Analyze(samples, options);
            var context = AnalysisGeometrySnapshot.Capture(samples, options, configured ? "cfg_options_ready" : "no_readable_matching_geometry");
            var record = LandingRecordFactory.Create(before[0], samples, "test", "test");
            record.AnalysisGeometry = context;
            var serializer = new DataContractJsonSerializer(typeof(LandingRecordFile));
            using var stream = new MemoryStream();
            serializer.WriteObject(stream, LandingRecordFile.FromRecord(record));
            var text = Encoding.UTF8.GetString(stream.ToArray());
            Assert(!text.Contains("NaN") && !text.Contains("Infinity") && !text.Contains("ConfigPath"), "unsafe snapshot serialization");
            stream.Position = 0;
            var restored = ((LandingRecordFile)serializer.ReadObject(stream)!).ToRecord().AnalysisGeometry!;
            var after = TouchdownAnalysis.Analyze(samples, restored.Restore(samples));
            Assert(before.Count == after.Count, "snapshot changed contacts");
            for (var i = 0; i < before.Count; i++)
                Assert(before[i].InertialVerticalFpm.Equals(after[i].InertialVerticalFpm) &&
                    before[i].ReconstructedClosureFpm.Equals(after[i].ReconstructedClosureFpm) &&
                    before[i].ClosureReconstructionUncertaintyFpm.Equals(after[i].ClosureReconstructionUncertaintyFpm) &&
                    before[i].ClosureReconstructionGeometrySource == after[i].ClosureReconstructionGeometrySource,
                    "archived options did not reproduce the original branches/results");
            Assert(restored.MainArmsFeet.SequenceEqual(context.MainArmsFeet) && restored.NoseIndices.SequenceEqual(context.NoseIndices), "lost full per-wheel options");
            Reject(() => restored.Restore(samples.Skip(1).ToArray()), "snapshot accepted another segment");
            var originalVelocity = samples[samples.Count / 2].VelocityWorldYFps;
            samples[samples.Count / 2].VelocityWorldYFps = originalVelocity + 1;
            Reject(() => restored.Restore(samples), "snapshot accepted modified interior telemetry with identical segment boundaries");
            samples[samples.Count / 2].VelocityWorldYFps = originalVelocity;
            restored.CoordinateFrame = "unknown";
            Reject(() => restored.Restore(samples), "snapshot accepted unknown coordinate frame");
        }
        Assert(LandingRecordFile.FromRecord(new LandingRecord()).ToRecord().AnalysisGeometry == null, "legacy record gained fabricated geometry");
    }

    public static void ConfigResolutionDistinguishesReadAndCalibrationFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-cfg-diagnostics-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "test-package", "SimObjects", "Airplanes", "test");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "aircraft.cfg"), "[FLTSIM.0]\ntitle=Fixture\n");
            var cfg = Path.Combine(dir, "flight_model.cfg");
            var cases = new[] { "flight_model_unavailable", "unresolved_contact_point", "invalid_reference_datum", "telemetry_datum_unavailable" };
            foreach (var expected in cases)
            {
                if (expected == "unresolved_contact_point") File.WriteAllText(cfg, "[CONTACT_POINTS]\npoint.0=[dynamic]\n");
                if (expected == "invalid_reference_datum") File.WriteAllText(cfg, "[WEIGHT_AND_BALANCE]\nreference_datum_position=NaN,0,0\n");
                if (expected == "telemetry_datum_unavailable") File.WriteAllText(cfg, "[CONTACT_POINTS]\npoint.0=1,20,0,-5\npoint.1=1,-10,-5,-5\npoint.2=1,-10,5,-5\n");
                var resolver = new FlightModelGeometryResolver(new[] { root }, false);
                var journal = new TelemetryDecisionJournal();
                var result = resolver.ResolveAnalysisAsync("Fixture", "", "", Array.Empty<TelemetrySample>(), journal).GetAwaiter().GetResult();
                Assert(result.Options == null && result.Outcome == expected, "wrong cfg failure: " + result.Outcome);
                Assert(journal.Snapshot().Entries.Single().Code == expected, "cfg outcome absent from archive journal");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
