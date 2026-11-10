using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class GroundNoiseRegressionTests
{
    private const double FeetToMeters = .3048;
    private const double PeriodSeconds = .05;
    private const string FixtureHeader = "analysis_time_s,host_elapsed_s,simulation_delta_s,plane_altitude_ft,ground_altitude_ft,agl_ft,velocity_world_y_fps,ground_speed_kt,pitch_deg,bank_deg,heading_true_deg,rotation_velocity_body_x_rad_s,rotation_velocity_body_y_rad_s,rotation_velocity_body_z_rad_s,g_force,acceleration_body_x_fps2,acceleration_body_z_fps2";

    public static void RunAll()
    {
        RecordedGroundNoisePreservesCoherentDepartures();
        NumericRevisionsPreserveCoherentDepartures();
    }

    private static void RecordedGroundNoisePreservesCoherentDepartures()
    {
        var prefix = ReadGroundPrefix();
        var maxAltitudeChange = prefix.Max(sample => Math.Abs(sample.PlaneAltitudeFeet - prefix.Last().PlaneAltitudeFeet)) * FeetToMeters;
        Check(maxAltitudeChange > 6e-6 && maxAltitudeChange < 7e-6,
            "The recorded ground-noise fixture no longer contains the micrometre altitude variation.");

        foreach (var duration in new[] { 2.0, 20.0 })
        {
            // The control changes only ground-prefix altitude. All other
            // observations and the complete subsequent trajectory are identical.
            var control = ContinueVertically(prefix, duration, true);
            var recorded = ContinueVertically(prefix, duration, false);
            var expected = VerifyContact(control, "altitude-only control, " + duration + " s");
            var actual = VerifyContact(recorded, "recorded ground noise, " + duration + " s");
            Check(actual.Sequence == expected.Sequence && actual.SimulationTimeSeconds == expected.SimulationTimeSeconds,
                "Ground-prefix altitude noise changed the accepted contact relative to its altitude-only control.");
        }
    }

    private static void NumericRevisionsPreserveCoherentDepartures()
    {
        var prefix = ReadGroundPrefix();
        foreach (var duration in new[] { 2.0, 20.0 })
        foreach (var offset in new[] { -1e-10, 0.0, 1e-10 })
        {
            var original = ContinueVertically(prefix, duration, false);
            var revised = new List<TelemetrySample>();
            for (var index = 0; index < original.Count; index++)
            {
                revised.Add(original[index]);
                if (index != prefix.Length - 1 && index != prefix.Length) continue;
                // Both sides of the departure can receive a numeric revision
                // within the existing same-observation timestamp tolerance.
                var revision = original[index].CopyForAnalysis();
                revision.AnalysisTimeSeconds += offset;
                revision.SimulationTimeSeconds += offset;
                revision.HostElapsedSeconds += offset;
                revised.Add(revision);
            }
            var label = "recorded noise with timestamp revision " + offset.ToString("R", CultureInfo.InvariantCulture) + ", " + duration + " s";
            var live = VerifyContact(revised, label);
            var retained = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(revised));
            var canonical = VerifyContact(retained, label + ", prepared/deduplicated");
            Check(live.Sequence == canonical.Sequence && live.SimulationTimeSeconds == canonical.SimulationTimeSeconds,
                "Coalescing a departure timestamp revision changed the accepted contact.");
        }
    }

    private static TouchdownResult VerifyContact(IReadOnlyList<TelemetrySample> samples, string label)
    {
        var before = samples.Select(TelemetryCsv.Format).ToArray();
        var live = AcceptedSequences(samples, false);
        var recovered = AcceptedSequences(samples, true);
        var journal = new TelemetryDecisionJournal();
        var analyzed = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal });
        var expected = new[] { samples[samples.Count - 1].Sequence };
        Check(live.SequenceEqual(expected), "Live continuity lost the coherent vertical contact after " + label + ".");
        Check(recovered.SequenceEqual(live), "Checkpoint recovery changed contact eligibility after " + label + ".");
        Check(analyzed.Select(contact => contact.Sequence).SequenceEqual(live),
            "Public analysis disagreed with live contact eligibility after " + label + ".");
        Check(!journal.Snapshot().Entries.Any(entry => entry.Code == "ground_reposition_contact_rejected"),
            "Ground noise created a position-replacement rejection after " + label + ".");
        Check(before.SequenceEqual(samples.Select(TelemetryCsv.Format)), "Ground-noise analysis changed raw telemetry after " + label + ".");
        return analyzed.Single();
    }

    private static long[] AcceptedSequences(IEnumerable<TelemetrySample> samples, bool recoverEveryFrame)
    {
        var continuity = new TelemetryContactContinuity();
        var accepted = new List<long>();
        foreach (var sample in samples)
        {
            if (continuity.Observe(sample)) accepted.Add(sample.Sequence);
            if (!recoverEveryFrame) continue;
            var checkpoint = Checkpoint(continuity);
            continuity = Restore(checkpoint);
            Check(checkpoint.SequenceEqual(Checkpoint(continuity)), "Ground-noise checkpoint changed during round-trip recovery.");
        }
        return accepted.ToArray();
    }

    internal static List<TelemetrySample> ContinueVertically(TelemetrySample[] prefix, double duration, bool constantGroundAltitude)
    {
        var last = prefix[prefix.Length - 1];
        var samples = prefix.Select(sample => sample.CopyForAnalysis()).ToList();
        if (constantGroundAltitude)
            foreach (var sample in samples) sample.PlaneAltitudeFeet = last.PlaneAltitudeFeet;

        var amplitudeMeters = duration / 2;
        var steps = (int)Math.Round(duration / PeriodSeconds);
        for (var index = 1; index <= steps; index++)
        {
            var elapsed = index * PeriodSeconds;
            var phase = 2 * Math.PI * elapsed / duration;
            var heightFeet = amplitudeMeters * .5 * (1 - Math.Cos(phase)) / FeetToMeters;
            var sample = last.CopyForAnalysis();
            sample.Sequence = last.Sequence + index;
            sample.AnalysisTimeSeconds = last.AnalysisTimeSeconds + elapsed;
            sample.SimulationTimeSeconds = sample.AnalysisTimeSeconds;
            sample.HostElapsedSeconds = last.HostElapsedSeconds + elapsed;
            sample.SimulationDeltaSeconds = PeriodSeconds;
            sample.OnGround = index == steps;
            sample.PlaneAltitudeFeet = last.PlaneAltitudeFeet + heightFeet;
            sample.AboveGroundLevelFeet = last.AboveGroundLevelFeet + heightFeet;
            sample.GroundAltitudeFeet = last.GroundAltitudeFeet;
            // This is the exact derivative of the cosine height trajectory.
            sample.VelocityWorldYFps = amplitudeMeters * Math.PI / duration * Math.Sin(phase) / FeetToMeters;
            sample.VerticalSpeedFps = sample.VelocityWorldYFps;
            sample.GroundSpeedKnots = 0;
            sample.RotationVelocityBodyXRadiansPerSecond = 0;
            sample.RotationVelocityBodyYRadiansPerSecond = 0;
            sample.RotationVelocityBodyZRadiansPerSecond = 0;
            samples.Add(sample);
        }
        return samples;
    }

    internal static TelemetrySample[] ReadGroundPrefix()
    {
        var lines = File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "ground-noise-prefix.csv"));
        Check(lines.Length == 41 && lines[0] == FixtureHeader, "Unexpected recorded ground-noise fixture shape.");
        return lines.Skip(1).Select((line, index) =>
        {
            var fields = line.Split(',');
            Check(fields.Length == 17, "Invalid recorded ground-noise fixture row.");
            var values = fields.Select(value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            return new TelemetrySample
            {
                Sequence = index + 1, AnalysisTimeSeconds = values[0], SimulationTimeSeconds = values[0],
                HostElapsedSeconds = values[1], SimulationDeltaSeconds = values[2],
                PlaneAltitudeFeet = values[3], GroundAltitudeFeet = values[4], AboveGroundLevelFeet = values[5],
                VelocityWorldYFps = values[6], GroundSpeedKnots = values[7], PitchDegrees = values[8],
                BankDegrees = values[9], HeadingTrueDegrees = values[10],
                RotationVelocityBodyXRadiansPerSecond = values[11], RotationVelocityBodyYRadiansPerSecond = values[12],
                RotationVelocityBodyZRadiansPerSecond = values[13], GForce = values[14],
                AccelerationBodyXFps2 = values[15], AccelerationBodyZFps2 = values[16],
                LatitudeDegrees = 0, LongitudeDegrees = 0,
                OnGround = true, MotionSimulation = true, SimulatorState = TelemetrySimulatorState.Running, FlightSegment = 1,
            };
        }).ToArray();
    }

    private static byte[] Checkpoint(TelemetryContactContinuity continuity)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(continuity.HasAirborneWitness);
        continuity.WriteSourceCheckpoint(writer);
        continuity.WriteGroundCheckpoint(writer);
        continuity.WriteCumulativeCheckpoint(writer);
        continuity.WriteGroundRevisionCheckpoint(writer);
        return stream.ToArray();
    }

    private static TelemetryContactContinuity Restore(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var continuity = new TelemetryContactContinuity();
        continuity.RestoreAirborneWitness(reader.ReadBoolean());
        continuity.RestoreSourceCheckpoint(reader);
        continuity.RestoreGroundCheckpoint(reader);
        continuity.RestoreCumulativeCheckpoint(reader);
        continuity.RestoreGroundRevisionCheckpoint(reader);
        Check(reader.BaseStream.Position == reader.BaseStream.Length, "Ground-noise checkpoint has unread data.");
        return continuity;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
