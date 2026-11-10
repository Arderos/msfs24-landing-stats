using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class PauseRepositionRegressionTests
{
    public static void RunAll()
    {
        RecordedSettlingIsRejected();
        NewApproachAfterRelocationIsAccepted();
        UnchangedGroundPausePreservesAShortBounce();
        ResumedGroundMotionEstablishesANewDepartureOrigin();
        Verify(ReadRecorded("ground-pause-then-bounce.csv"), 1);
        LiveRecordingRetainsPauseAndRecovers();
        PreviousCoordinatorRevisionRemainsReadable();
    }

    private static void PreviousCoordinatorRevisionRemainsReadable()
    {
        var harness = new ReplayRecordingRegressionTests.Harness();
        harness.Feed(Frame(0, 0, false, 100.1));
        harness.Restore(RemovePauseTail(harness.Coordinator.CaptureCheckpoint()));
        harness.Feed(Frame(.1, .1, true, 100));
        Check(harness.Coordinator.HasContact, "Version-8 airborne history did not recover into the pause-aware recorder.");
        harness.Coordinator.Stop();
    }

    internal static byte[] RemovePauseTail(byte[] checkpoint)
    {
        var expected = GroundRepositionRegressionTests.RestoreContactCheckpoint(checkpoint);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) expected.WritePauseCheckpoint(writer);
        var length = checked((int)stream.Length);
        Check(BitConverter.ToInt32(checkpoint, 0) == 0x3943454c && checkpoint.Length > length + sizeof(int),
            "Legacy fixture is not a complete version-9 coordinator checkpoint.");
        Check(checkpoint.Skip(checkpoint.Length - length).SequenceEqual(stream.ToArray()),
            "The serialized recorder pause state does not match the checkpoint tail.");
        var legacy = checkpoint.Take(checkpoint.Length - length).ToArray();
        Array.Copy(BitConverter.GetBytes(0x3843454c), legacy, sizeof(int));
        return legacy;
    }

    private static void RecordedSettlingIsRejected()
    {
        var rows = ReadRecorded();
        Check(rows.Count == 106 && rows.Count(s => !s.OnGround) == 1, "Recorded pause fixture changed.");
        Verify(rows, 0);
        // Repeated preparation and copying must not erase the original clock offset.
        Verify(TelemetryAnalysisFrames.Prepare(rows).Select(s => s.CopyForAnalysis()).ToArray(), 0);
        var journal = new TelemetryDecisionJournal();
        TouchdownAnalysis.Analyze(rows, new TouchdownAnalysisOptions { DecisionJournal = journal });
        Check(journal.Snapshot().Entries.Any(e => e.Code == "pause_reposition_airborne_interval_required"),
            "Recorded settling lacks the pause-specific rejection reason.");
    }

    private static void NewApproachAfterRelocationIsAccepted()
    {
        foreach (var explicitPause in new[] { false, true })
        foreach (var sourceAvailable in new[] { false, true })
        foreach (var altitude in new[] { 100.4, 1100.0 })
        {
            var before = Frame(0, 0, true, 100);
            var pause = Frame(10, 0, false, altitude, 42.2);
            pause.SimulatorState = TelemetrySimulatorState.Paused;
            pause.PauseFlags = 4;
            var air = Frame(10.1, .1, false, altitude, 42.2);
            if (sourceAvailable) air.SourceEvidence = Evidence(10, altitude, 42.2);
            var rows = new List<TelemetrySample> { before };
            if (explicitPause) rows.Add(pause);
            rows.Add(air);
            var duplicate = Frame(10.1, .1, false, altitude, 42.2); duplicate.SourceEvidence = air.SourceEvidence;
            Verify(rows.Concat(new[] { duplicate, Frame(10.2, .2, true, altitude - .1, 42.2) }).ToArray(), 0);
            duplicate = Frame(10.1 + 1e-10, .1 + 1e-10, false, altitude, 42.2); duplicate.SourceEvidence = air.SourceEvidence;
            Verify(rows.Concat(new[] { duplicate, Frame(10.2, .2, true, altitude - .1, 42.2) }).ToArray(), 0);
            // Fresh distinct air observations re-arm without an intervening ground
            // event, regardless of displacement, altitude, GS or approach duration.
            rows.Add(Frame(10.15, .15, false, altitude - .05, 42.2));
            rows.Add(Frame(10.2, .2, true, altitude - .1, 42.2));
            Verify(rows, 1);
            rows.Add(Frame(10.3, .3, false, altitude, 42.2));
            rows.Add(Frame(10.4, .4, true, altitude - .1, 42.2));
            Verify(rows, 2);
        }
    }

    private static void UnchangedGroundPausePreservesAShortBounce()
    {
        var pause = Frame(10, 0, true, 100); pause.SimulatorState = TelemetrySimulatorState.Paused; pause.PauseFlags = 4;
        var rows = new[] { Frame(-.1, -.1, false, 100.1), Frame(0, 0, true, 100), pause,
            Frame(10.1, .1, false, 100.01), Frame(10.2, .2, true, 100) };
        Verify(rows, 2);
        // A temporary paused pose followed by a return to the original pose must
        // behave identically before and after first/last pause coalescing.
        var moved = Frame(5, 0, true, 101); moved.SimulatorState = TelemetrySimulatorState.Paused; moved.PauseFlags = 4;
        Verify(new[] { rows[0], rows[1], moved, pause, rows[3], rows[4] }, 2);
    }

    private static void LiveRecordingRetainsPauseAndRecovers()
    {
        foreach (var approach in new[] { false, true })
        foreach (var recoverAt in new[] { -1, 0, 1, 2 })
        {
            var h = new ReplayRecordingRegressionTests.Harness();
            h.Feed(Frame(100, 100, true, 100));
            h.Coordinator.SetSdkPauseFlags(4);
            for (var i = 1; i <= 5000; i++)
                h.Feed(Frame(100 + i * .002, 100, false, approach ? 1100 : 100.4, approach ? 42.2 : 42));
            if (recoverAt == 0) h.Restore(h.Coordinator.CaptureCheckpoint());
            h.Coordinator.SetSdkPauseFlags(0);
            h.Feed(Frame(110.1, 100.1, false, approach ? 1099.9 : 100.39, approach ? 42.2 : 42));
            if (recoverAt == 1) h.Restore(h.Coordinator.CaptureCheckpoint());
            if (approach)
            {
                h.Feed(Frame(110.2, 100.2, false, 1099.8, 42.2));
                if (recoverAt == 2) h.Restore(h.Coordinator.CaptureCheckpoint());
                // Continue to the recorder's existing approach gate.
                for (var i = 1; i <= 100; i++) h.Feed(Frame(110.2 + i, 100.2 + i, false, 1099.8 - i * 9.998, 42.2));
            }
            else if (recoverAt == 2) h.Restore(h.Coordinator.CaptureCheckpoint());
            h.Feed(Frame(approach ? 210.3 : 110.2, approach ? 200.3 : 100.2, true, 100, approach ? 42.2 : 42));
            h.Coordinator.Stop();
            var contacts = h.Captures.SelectMany(c => TouchdownAnalysis.Analyze(c.Samples)).ToArray();
            Check(contacts.Length == (approach ? 1 : 0), "Live pause/relocation differs from stored analysis after recovery.");
            Check(h.Captures.Sum(c => c.DecisionJournal!.Snapshot().Entries.Count(e => e.Code == "contact_flag_transition")) == contacts.Length,
                "Live pause eligibility disagrees with public analysis.");
            if (!approach)
            {
                var saved = h.Captures.Single().Samples;
                Check(saved.Count(s => s.SimulatorState == TelemetrySimulatorState.Paused) == 2 && saved.Any(s => s.OnGround && s.SimulationTimeSeconds == 100),
                    "A long ground pause lost its pre-pause origin or grew retained evidence.");
            }
        }
    }

    private static void ResumedGroundMotionEstablishesANewDepartureOrigin()
    {
        var pause = Frame(10, 0, true, 100.4); pause.SimulatorState = TelemetrySimulatorState.Paused; pause.PauseFlags = 4;
        Verify(new[] { Frame(0, 0, true, 100), pause, Frame(10.1, .1, true, 100.4),
            Frame(10.2, .2, true, 100.4), Frame(10.3, .3, false, 100.41), Frame(10.4, .4, true, 100.4) }, 1);
    }

    private static void Verify(IReadOnlyList<TelemetrySample> rows, int expected)
    {
        var original = rows.Select(TelemetryCsv.Format).ToArray();
        foreach (var input in new[] { rows, TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(rows)) })
        foreach (var recover in new[] { false, true })
        {
            var continuity = new TelemetryContactContinuity(); var count = 0;
            foreach (var row in input)
            {
                if (continuity.Observe(row)) count++;
                if (recover) continuity = Restore(continuity);
            }
            Check(count == expected, "Pause continuity count: " + count + " expected " + expected);
            var actual = TouchdownAnalysis.Analyze(input).Count;
            Check(actual == expected, "Public pause analysis count " + actual + " expected " + expected + "; rows=" + input.Count + "; last raw time=" + input.Last().SimulationTimeSeconds);
        }
        Check(original.SequenceEqual(rows.Select(TelemetryCsv.Format)), "Pause analysis mutated raw observations.");
    }

    private static TelemetryContactContinuity Restore(TelemetryContactContinuity value)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(value.HasAirborneWitness); value.WriteSourceCheckpoint(writer); value.WriteGroundCheckpoint(writer);
        value.WriteCumulativeCheckpoint(writer); value.WriteGroundRevisionCheckpoint(writer); value.WritePauseCheckpoint(writer);
        memory.Position = 0; using var reader = new BinaryReader(memory); var restored = new TelemetryContactContinuity();
        restored.RestoreAirborneWitness(reader.ReadBoolean()); restored.RestoreSourceCheckpoint(reader); restored.RestoreGroundCheckpoint(reader);
        restored.RestoreCumulativeCheckpoint(reader); restored.RestoreGroundRevisionCheckpoint(reader); restored.RestorePauseCheckpoint(reader);
        Check(memory.Position == memory.Length, "Pause checkpoint was not fully consumed."); return restored;
    }

    private static List<TelemetrySample> ReadRecorded(string fileName = "ground-pause-settling.csv")
    {
        var rows = new List<TelemetrySample>();
        foreach (var line in File.ReadLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", fileName)).Skip(1))
        {
            var v = line.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var sample = Frame(v[1], v[2], v[3] == 1, v[4]);
            sample.Sequence = (long)v[0]; sample.AboveGroundLevelFeet = v[5]; sample.GroundAltitudeFeet = v[4] - v[5];
            sample.VelocityWorldYFps = v[6]; sample.GroundSpeedKnots = v[7]; sample.GForce = v[8]; sample.PitchDegrees = v[9]; sample.BankDegrees = v[10];
            sample.SourceEvidence = Evidence(v[11], v[12], 42, v[13], v[14]); rows.Add(sample);
            sample.RotationVelocityBodyXRadiansPerSecond = v[15]; sample.RotationVelocityBodyYRadiansPerSecond = v[16];
            sample.RotationVelocityBodyZRadiansPerSecond = v[17]; sample.AccelerationBodyXFps2 = v[18]; sample.AccelerationBodyZFps2 = v[19];
        }
        return rows;
    }

    private static TelemetrySample Frame(double raw, double active, bool ground, double altitude, double latitude = 42) =>
        new TelemetrySample { SimulationTimeSeconds = raw, AnalysisTimeSeconds = active, HostElapsedSeconds = raw,
            SimulationDeltaSeconds = .1, SimulatorState = TelemetrySimulatorState.Running, PauseFlags = 0, FlightSegment = 1,
            MotionSimulation = true, OnGround = ground, PlaneAltitudeFeet = altitude, GroundAltitudeFeet = 100,
            AboveGroundLevelFeet = altitude - 100, LatitudeDegrees = latitude, LongitudeDegrees = 23,
            VelocityWorldYFps = ground ? 0 : -1, VerticalSpeedFps = ground ? 0 : -1, GForce = 1 };

    private static ReplaySourceEvidence Evidence(double time, double altitude, double latitude, double pitch = 0, double bank = 0) =>
        new ReplaySourceEvidence(new Guid("6ebd6e42-d71b-4ed4-9c3d-1dc1d27dbd63"), 1, 1, 1000, 1100, 1000, SdkReplayState.Unknown,
            new ReplaySourceObservation(SourceObservationAvailability.Available, SourceObservationReason.None, 0, 0, 1,
                simulationTime: time, latitude: latitude, longitude: 23, altitudeFeet: altitude, pitch: pitch, bank: bank, heading: 0));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
