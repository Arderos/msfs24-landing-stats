using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class GroundRepositionRegressionTests
{
    public static void RunAll()
    {
        SmallReplacementCannotCreateAContact();
        ReleasedGroundObservationsRetainDepartureEvidence();
        ReplacementOnTheLastGroundRowCannotCreateAContact();
        SameTimeGroundReplacementRetainsItsEvidence();
        ReplacementCannotPolluteThePrecedingContact();
        PhysicalDeparturesAndBouncesRemainContacts();
        LocalMismatchAndMotionBudgetAreRespected();
        MissingEvidenceDoesNotInventAReplacement();
        InterruptedSettlingCannotRearmTheContact();
        LaterPhysicalDeparturesAndNewFlightsRecover();
        CheckpointsRetainGroundHistoryAndRejectedRuns();
        LiveOfflineAndRecoveryAgree();
        NumericRevisionsReuseTheirGroundedInterval();
        NumericRevisionTimeMatchesDeduplication();
        DifferentSameTimePositionsRetainEarlierEvidence();
        RevisionCheckpointsAndRecordedMeasurementsAgree();
        PreviousCoordinatorRevisionRemainsReadable();
        FirstAirborneNumericRevisionsReuseGroundedEvidence();
        FirstAirborneRevisionBoundariesRemainDiscrete();
    }

    private static void SmallReplacementCannotCreateAContact()
    {
        foreach (var vertical in new[] { false, true })
        foreach (var origin in new[] { 0.0, 1000000.0 })
        {
            var samples = Replacement(vertical);
            foreach (var sample in samples) { sample.SimulationTimeSeconds += origin; sample.HostElapsedSeconds += origin; }
            Check(Count(samples) == 0, "Settling after an unexplained ground departure became a contact.");
            var before = samples.Select(TelemetryCsv.Format).ToArray();
            var journal = new TelemetryDecisionJournal();
            Check(TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal }).Count == 0,
                "Public analysis revived a rejected ground departure.");
            Check(journal.Snapshot().Entries.Any(entry => entry.Code == "ground_reposition_contact_rejected"),
                "The rejected ground transition has no diagnostic reason.");
            Check(before.SequenceEqual(samples.Select(TelemetryCsv.Format)), "Contact eligibility mutated raw telemetry.");
        }
    }

    private static void ReplacementOnTheLastGroundRowCannotCreateAContact()
    {
        var samples = Replacement();
        samples[3].OnGround = true;
        Check(Count(samples) == 0, "One replacement row marked GROUND authorized its following airborne settling.");
        // A complete grounded interval after replacement supports a new departure.
        samples[4].OnGround = true;
        Check(Count(samples) == 1, "A new grounded interval could not establish a subsequent physical departure.");
    }

    private static void ReleasedGroundObservationsRetainDepartureEvidence()
    {
        foreach (var vertical in new[] { false, true })
        foreach (var supported in new[] { false, true })
        foreach (var slew in new[] { false, true })
        {
            var samples = Enumerable.Range(0, 20).Select(i => Frame(-1 + i * .05, true))
                .Concat(Replacement(vertical)).ToArray();
            for (var i = 0; i < samples.Length; i++)
            {
                AddSource(samples[i], i == 0 && !slew ? 7 : 0, i == 0 && slew ? 1 : 0);
                if (supported) samples[i].AccelerationBodyXFps2 = 1000;
            }
            var expected = supported ? 1 : 0;
            Check(Count(samples) == expected && TouchdownAnalysis.Analyze(samples).Count == expected,
                "Released ground observations lost the evidence distinguishing replacement from a physical departure.");
            AssertEveryCheckpoint(samples, expected);
        }
    }

    private static void PhysicalDeparturesAndBouncesRemainContacts()
    {
        foreach (var speed in new[] { 0.0, 40.0, 140.0 })
        foreach (var airborneDuration in new[] { .001, .02, .1 })
        {
            var samples = new[] { Frame(0, true), Frame(.02, true), Frame(.04, false), Frame(.04 + airborneDuration, true) };
            foreach (var sample in samples)
            {
                sample.GroundSpeedKnots = speed;
                sample.LatitudeDegrees += sample.SimulationTimeSeconds * speed * 1852 / 3600 / 111195;
            }
            samples[2].VelocityWorldYFps = 1;
            samples[2].PlaneAltitudeFeet += .02;
            Check(Count(samples) == 1 && TouchdownAnalysis.Analyze(samples).Count == 1,
                "Physical departure acquired a minimum speed, height or duration requirement.");
        }
        var bounce = new[] { Frame(0, false), Frame(.01, true), Frame(.02, false), Frame(.03, true) };
        Check(Count(bounce) == 2 && TouchdownAnalysis.Analyze(bounce).Count == 2, "Short vertical bounces changed.");
        Check(TouchdownAnalysis.Analyze(new[] { Frame(0, false), Frame(.01, true) }).Count == 1,
            "Starting capture during an approach now requires an earlier takeoff.");
    }

    private static void ReplacementCannotPolluteThePrecedingContact()
    {
        var frames = new[] { Frame(0, false), Frame(.02, true), Frame(.04, true), Frame(.06, true),
            Frame(.08, false, 7), Frame(.1, false, 7), Frame(.12, true, 7), Frame(.14, true, 7) };
        foreach (var frame in frames) frame.TouchdownNormalVelocityFps = 0;
        frames[1].GForce = 1.2;
        foreach (var frame in frames.Skip(4))
        {
            frame.GForce = 999; frame.TouchdownNormalVelocityFps = 999;
            frame.ContactPointCompression[0] = 1;
        }
        // Keep the replacement's measured acceleration ordinary so its lack of
        // motion support remains established; corruption starts after that row.
        frames[4].GForce = 1;
        var before = frames.Select(TelemetryCsv.Format).ToArray();
        var journal = new TelemetryDecisionJournal();
        var contact = TouchdownAnalysis.Analyze(frames, new TouchdownAnalysisOptions { DecisionJournal = journal }).Single();
        Check(contact.PeakG == 1.2 && contact.Impact.PeakG == 1.2 && !contact.LatchUpdateDetected &&
            !contact.ContactTimeEstimatedFromCompression, "Later reposition/settling polluted an earlier valid contact's measurements.");
        Check(journal.Snapshot().Entries.Any(entry => entry.Code == "ground_reposition_measurement_boundary"),
            "The measurement boundary was not explained.");
        Check(before.SequenceEqual(frames.Select(TelemetryCsv.Format)), "Measurement barriers mutated recorded telemetry.");
    }

    private static void SameTimeGroundReplacementRetainsItsEvidence()
    {
        var samples = new[] { Frame(0, true), Frame(.05, true), Frame(.05, true, 7),
            Frame(.07, false, 7), Frame(.09, false, 7), Frame(.12, true, 7) };
        Check(Count(samples) == 0, "Live ground-replacement evidence was lost.");
        var unique = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(samples));
        Check(unique.Count == samples.Length && TouchdownAnalysis.Analyze(unique).Count == 0,
            "Coalescing erased the grounded endpoint of a same-time position replacement.");
        var harness = new ReplayRecordingRegressionTests.Harness();
        foreach (var sample in samples) harness.Feed(sample.CopyForAnalysis());
        for (var i = 1; i <= 20; i++) harness.Feed(Frame(.12 + i, true, 7));
        harness.Coordinator.Stop();
        Check(harness.Captures.Count > 0 && harness.Captures.All(capture => capture.IsDiagnosticOnly &&
            TouchdownAnalysis.Analyze(capture.Samples).Count == 0), "Live capture coalescing revived a same-time replacement.");
    }

    private static void LocalMismatchAndMotionBudgetAreRespected()
    {
        var mismatch = new[] { Frame(0, true), Frame(.02, true, 10), Frame(.04, false, 20), Frame(.06, true, 20) };
        Check(Count(mismatch) == 1, "An already inconsistent position channel was mistaken for a new isolated replacement.");
        foreach (var useAcceleration in new[] { false, true })
        {
            var samples = Replacement();
            foreach (var sample in samples)
            {
                if (useAcceleration) sample.AccelerationBodyXFps2 = 1000;
                else sample.GroundSpeedKnots = 150;
            }
            Check(Count(samples) == 1, "The departure ignored available speed or acceleration support.");
        }
        var longInterval = Replacement();
        foreach (var sample in longInterval.Skip(3)) { sample.SimulationTimeSeconds += 1; sample.HostElapsedSeconds += 1; }
        Check(Count(longInterval) == 1, "A long measurement gap was evaluated as an instantaneous position step.");
    }

    private static void MissingEvidenceDoesNotInventAReplacement()
    {
        foreach (var missing in new Action<TelemetrySample>[]
        {
            sample => { sample.LatitudeDegrees = sample.LongitudeDegrees = 0; },
            sample => sample.GroundSpeedKnots = double.NaN,
            sample => sample.LatitudeDegrees = double.NaN,
        })
        {
            var samples = Replacement();
            foreach (var sample in samples) missing(sample);
            Check(Count(samples) == 1, "Unavailable position/speed invented positive evidence of replacement.");
        }
        Check(Count(Replacement().Skip(2)) == 1, "A single grounded observation invented a local error estimate.");
        var sameClock = Replacement();
        sameClock[1].SimulationTimeSeconds = sameClock[2].SimulationTimeSeconds = 0;
        Check(Count(sameClock) == 1, "Repeated same-time ground rows invented independent motion evidence.");
    }

    private static void InterruptedSettlingCannotRearmTheContact()
    {
        foreach (var missing in new Action<TelemetrySample>[]
        {
            sample => sample.MotionSimulation = false,
            sample => { sample.SimulationTimeSeconds = sample.HostElapsedSeconds = double.NaN; },
            sample => sample.SimulatorState = TelemetrySimulatorState.Inactive,
        })
        {
            var samples = Replacement();
            missing(samples[4]);
            Check(Count(samples) == 0, "An unavailable row erased a previously invalid departure.");
            Check(TouchdownAnalysis.Analyze(samples).Count == 0, "Analysis segmentation forgot an invalid departure.");
        }
        var source = Replacement();
        for (var i = 0; i < source.Length; i++) AddSource(source[i], i == 4 ? 7 : 0);
        Check(Count(source) == 0, "Fresh source observations retroactively validated a ground replacement.");
    }

    private static void LaterPhysicalDeparturesAndNewFlightsRecover()
    {
        var samples = Replacement().ToList();
        samples.Add(Frame(.4, true, 7));
        samples.Add(Frame(.42, false, 7));
        samples.Add(Frame(.43, true, 7));
        Check(Count(samples) == 1 && TouchdownAnalysis.Analyze(samples).Count == 1,
            "Rejecting settling disabled a later short vertical departure and landing.");
        var nextFlight = Replacement().Take(5).ToList();
        var air = Frame(.5, false); air.FlightSegment = 1;
        var ground = Frame(.52, true); ground.FlightSegment = 1;
        nextFlight.Add(air); nextFlight.Add(ground);
        Check(Count(nextFlight) == 1, "The previous flight's unverified departure survived a new flight segment.");
    }

    private static void CheckpointsRetainGroundHistoryAndRejectedRuns()
    {
        var samples = Replacement();
        for (var restoreAfter = 0; restoreAfter < samples.Length; restoreAfter++)
        {
            var continuity = new TelemetryContactContinuity();
            var contacts = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                if (continuity.Observe(samples[i])) contacts++;
                if (i != restoreAfter) continue;
                var before = Checkpoint(continuity);
                using var reader = new BinaryReader(new MemoryStream(before));
                continuity = new TelemetryContactContinuity();
                continuity.RestoreAirborneWitness(reader.ReadBoolean());
                continuity.RestoreSourceCheckpoint(reader); continuity.RestoreGroundCheckpoint(reader);
                continuity.RestoreGroundRevisionCheckpoint(reader);
                Check(before.SequenceEqual(Checkpoint(continuity)), "Ground-departure checkpoint did not round-trip exactly.");
            }
            Check(contacts == 0, "Recovery lost the grounded baseline or revived an unverified airborne run.");
        }
    }

    private static byte[] Checkpoint(TelemetryContactContinuity value)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(value.HasAirborneWitness); value.WriteSourceCheckpoint(writer); value.WriteGroundCheckpoint(writer);
        value.WriteGroundRevisionCheckpoint(writer);
        return stream.ToArray();
    }

    private static void NumericRevisionsReuseTheirGroundedInterval()
    {
        foreach (var vertical in new[] { false, true })
        foreach (var supported in new[] { false, true })
        {
            var samples = RevisedDeparture(vertical, supported);
            var before = samples.Select(TelemetryCsv.Format).ToArray();
            var canonical = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(samples));
            var expected = supported ? 1 : 0;
            Check(Count(samples) == expected && Count(canonical) == expected &&
                TouchdownAnalysis.Analyze(samples).Count == expected && TouchdownAnalysis.Analyze(canonical).Count == expected,
                "A numeric revision changed grounded motion eligibility relative to its final observation.");
            Check(before.SequenceEqual(samples.Select(TelemetryCsv.Format)), "Grounded revisions changed raw telemetry.");
            AssertEveryCheckpoint(samples, expected);
        }
    }

    private static void DifferentSameTimePositionsRetainEarlierEvidence()
    {
        var samples = new[] { Frame(0, true), Frame(.05, true), Frame(.1, true, 2),
            Frame(.1, true, 2.1), Frame(.1, true, 2.1), Frame(.12, false, 2.2), Frame(.14, true, 2.4) };
        foreach (var sample in samples.Skip(4)) sample.GroundSpeedKnots = 20;
        var canonical = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(samples));
        Check(canonical.Count == samples.Length - 1 && Count(samples) == 0 && Count(canonical) == 0,
            "Correcting a later same-time pose erased an earlier, separately retained replacement.");
        AssertEveryCheckpoint(samples, 0);
    }

    private static void NumericRevisionTimeMatchesDeduplication()
    {
        foreach (var offset in new[] { -1e-10, 0, 1e-10, 2e-9 })
        {
            var samples = new[] { Frame(0, true), Frame(.05, true), Frame(.1, true, 2),
                Frame(.1 + offset, true, 2), Frame(.12, false, 2.2), Frame(.14, true, 2.4) };
            samples[2].GroundSpeedKnots = 20;
            samples[4].GroundSpeedKnots = samples[5].GroundSpeedKnots = 20;
            var expected = offset > 1e-9 ? 1 : 0;
            var canonical = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(samples));
            Check(Count(samples) == expected && Count(canonical) == expected &&
                TouchdownAnalysis.Analyze(samples).Count == expected && TouchdownAnalysis.Analyze(canonical).Count == expected,
                "The ground revision and deduplicator disagree about one physics instant.");
            AssertEveryCheckpoint(samples, expected);
            var harness = new ReplayRecordingRegressionTests.Harness();
            foreach (var sample in samples) harness.Feed(sample.CopyForAnalysis());
            Check(harness.Coordinator.HasContact == (expected == 1), "The recorder used a different same-instant revision boundary.");
            harness.Coordinator.Stop();
        }
    }

    private static TelemetrySample[] RevisedAirborneDeparture(bool supported, double offset = 0, bool changedPosition = false)
    {
        var samples = new[] { Frame(0, true), Frame(.05, true), Frame(.1, false, 2),
            Frame(.1 + offset, false, changedPosition ? 2.1 : 2), Frame(.12, false, 2.2), Frame(.14, true, 2.4) };
        samples[2].GroundSpeedKnots = supported ? 0 : 20;
        samples[3].GroundSpeedKnots = supported ? 20 : 0;
        samples[4].GroundSpeedKnots = samples[5].GroundSpeedKnots = 20;
        return samples;
    }

    private static void FirstAirborneNumericRevisionsReuseGroundedEvidence()
    {
        foreach (var supported in new[] { false, true })
        foreach (var offset in new[] { -1e-10, 0, 1e-10, 2e-9 })
        foreach (var changedPosition in new[] { false, true })
        {
            var samples = RevisedAirborneDeparture(supported, offset, changedPosition);
            var expected = offset > 1e-9 ? (supported ? 0 : 1) : (supported ? 1 : 0);
            AssertAirborneRevisions(samples, expected);
        }
    }

    private static void FirstAirborneRevisionBoundariesRemainDiscrete()
    {
        foreach (var supported in new[] { false, true })
        {
            var changedSource = RevisedAirborneDeparture(supported);
            AddSource(changedSource[2], 0);
            // Fresh -> unknown is a separately retained source observation, so
            // it cannot revise the preceding departure's numeric decision.
            AssertAirborneRevisions(changedSource, supported ? 0 : 1);
        }

        var frozen = RevisedAirborneDeparture(true).ToList();
        foreach (var sample in frozen) AddSource(sample, 0);
        var blocked = frozen[2].CopyForAnalysis(); AddSource(blocked, 7);
        frozen.Insert(3, blocked);
        AssertAirborneRevisions(frozen.ToArray(), 0);

        var interrupted = RevisedAirborneDeparture(true).ToList();
        interrupted.Insert(3, Frame(.1, true, 2));
        AssertAirborneRevisions(interrupted.ToArray(), 1);
    }

    private static void AssertAirborneRevisions(TelemetrySample[] samples, int expected)
    {
        var before = samples.Select(TelemetryCsv.Format).ToArray();
        var canonical = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(samples));
        Check(Count(samples) == expected && Count(canonical) == expected &&
            TouchdownAnalysis.Analyze(samples).Count == expected && TouchdownAnalysis.Analyze(canonical).Count == expected,
            "First-airborne revisions changed contact eligibility relative to canonical deliveries.");
        AssertEveryCheckpoint(samples, expected);
        for (var split = 0; split < samples.Length; split++)
        {
            var harness = new ReplayRecordingRegressionTests.Harness();
            for (var index = 0; index < samples.Length; index++)
            {
                harness.Feed(samples[index].CopyForAnalysis());
                if (index == split)
                {
                    var checkpoint = harness.Coordinator.CaptureCheckpoint();
                    harness.Restore(checkpoint);
                    Check(checkpoint.SequenceEqual(harness.Coordinator.CaptureCheckpoint()),
                        "First-airborne revision context changed across recorder recovery.");
                }
            }
            Check(harness.Coordinator.HasContact == (expected != 0), "Recorder and canonical analysis disagree about a revised departure.");
            harness.Coordinator.Stop();
            Check(harness.Captures.Count > 0 && harness.Captures.All(capture =>
                    capture.IsDiagnosticOnly == (TouchdownAnalysis.Analyze(capture.Samples).Count == 0)) &&
                harness.Captures.Sum(capture => TouchdownAnalysis.Analyze(capture.Samples).Count) == expected,
                "Saved capture classification disagrees with revised airborne evidence: expected=" + expected +
                ", split=" + split + ", captures=" + string.Join(",", harness.Captures.Select(capture =>
                    capture.IsDiagnosticOnly + ":" + TouchdownAnalysis.Analyze(capture.Samples).Count)) +
                ", source=" + string.Join(",", samples.Select(sample => sample.SourceEvidence?.Observation.FreezeMask.ToString() ?? "unknown")));
        }
        Check(before.SequenceEqual(samples.Select(TelemetryCsv.Format)), "Revising an airborne observation changed raw telemetry.");
    }

    private static TelemetrySample[] RevisedDeparture(bool vertical, bool supported)
    {
        var samples = new[] { Frame(0, true), Frame(.05, true), Frame(.1, true, vertical ? 0 : 2),
            Frame(.1, true, vertical ? 0 : 2), Frame(.1, true, vertical ? 0 : 2),
            Frame(.12, false, vertical ? 0 : 2.2), Frame(.14, true, vertical ? 0 : 2.4) };
        if (vertical) foreach (var sample in samples.Skip(2)) sample.PlaneAltitudeFeet += 2 / .3048;
        foreach (var index in new[] { 2, 3, 4 })
        {
            var moving = index == 4 ? supported : index == 2 ? !supported : supported;
            if (vertical) samples[index].VelocityWorldYFps = moving ? 20 : 0;
            else samples[index].GroundSpeedKnots = moving ? 20 : 0;
        }
        foreach (var sample in samples.Skip(5)) sample.GroundSpeedKnots = 20;
        return samples;
    }

    private static void AssertEveryCheckpoint(TelemetrySample[] samples, int expected)
    {
        for (var split = 0; split < samples.Length; split++)
        {
            var continuity = new TelemetryContactContinuity();
            var contacts = 0;
            for (var index = 0; index < samples.Length; index++)
            {
                if (continuity.Observe(samples[index])) contacts++;
                if (index != split) continue;
                var checkpoint = Checkpoint(continuity);
                using var reader = new BinaryReader(new MemoryStream(checkpoint));
                continuity = new TelemetryContactContinuity();
                continuity.RestoreAirborneWitness(reader.ReadBoolean());
                continuity.RestoreSourceCheckpoint(reader); continuity.RestoreGroundCheckpoint(reader);
                continuity.RestoreGroundRevisionCheckpoint(reader);
                Check(checkpoint.SequenceEqual(Checkpoint(continuity)), "Grounded revision checkpoint did not round-trip.");
            }
            Check(contacts == expected, "Checkpoint recovery changed a revised grounded pose's eligibility.");
        }
    }

    private static void RevisionCheckpointsAndRecordedMeasurementsAgree()
    {
        foreach (var earlierContact in new[] { false, true })
        for (var split = -1; split < 8; split++)
        {
            var samples = RevisedDeparture(false, true).ToList();
            if (earlierContact) samples.Insert(0, Frame(-.02, false));
            foreach (var sample in samples) sample.GForce = sample.SimulationTimeSeconds >= .1 ? 1.8 : 1.2;
            var canonical = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(samples));
            var expected = TouchdownAnalysis.Analyze(canonical);
            var raw = TouchdownAnalysis.Analyze(samples);
            Check(raw.Count == expected.Count && raw.Select(contact => contact.PeakG).SequenceEqual(expected.Select(contact => contact.PeakG)),
                "A preliminary numeric delivery truncated raw-analysis contact measurements.");
            var harness = new ReplayRecordingRegressionTests.Harness();
            for (var index = 0; index < samples.Count; index++)
            {
                harness.Feed(samples[index].CopyForAnalysis());
                if (index == split) harness.Restore(harness.Coordinator.CaptureCheckpoint());
            }
            harness.Coordinator.Stop();
            Check(harness.Captures.Count > 0 && harness.Captures.All(capture => !capture.IsDiagnosticOnly),
                "A supported numeric revision made the live capture diagnostic-only.");
            var actual = harness.Captures.SelectMany(capture => TouchdownAnalysis.Analyze(
                TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(capture.Samples)))).ToArray();
            Check(actual.Length == expected.Count && actual.Select(contact => contact.PeakG).SequenceEqual(expected.Select(contact => contact.PeakG)),
                "Saved-capture contacts or G measurements differ from the final numeric observations.");
            var stored = harness.Captures.SelectMany(capture => TouchdownAnalysis.Analyze(capture.Samples)).ToArray();
            Check(stored.Length == actual.Length && stored.Select(contact => contact.PeakG).SequenceEqual(actual.Select(contact => contact.PeakG)),
                "Stored active-capture measurements differ before and after analysis deduplication.");
        }
    }

    private static void PreviousCoordinatorRevisionRemainsReadable()
    {
        var samples = RevisedDeparture(false, true);
        var harness = new ReplayRecordingRegressionTests.Harness();
        foreach (var sample in samples.Take(2)) harness.Feed(sample.CopyForAnalysis());
        harness.Restore(RemoveRevisionTail(harness.Coordinator.CaptureCheckpoint()));
        foreach (var sample in samples.Skip(2)) harness.Feed(sample.CopyForAnalysis());
        Check(harness.Coordinator.HasContact, "Version-7 grounded history did not recover into the revision-aware recorder.");
        harness.Coordinator.Stop();
    }

    internal static TelemetryContactContinuity RestoreContactCheckpoint(byte[] checkpoint)
    {
        var harness = new ReplayRecordingRegressionTests.Harness();
        harness.Restore(checkpoint);
        // Legacy fixture conversion needs the actual recorder-owned state,
        // including its normalized clocks and optional source observations.
        var field = harness.Coordinator.GetType().GetField("_contactContinuity",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Check(field != null, "The coordinator contact state is unavailable for the legacy fixture.");
        return (TelemetryContactContinuity)field!.GetValue(harness.Coordinator)!;
    }

    internal static byte[] RemoveRevisionTail(byte[] checkpoint)
    {
        checkpoint = PauseRepositionRegressionTests.RemovePauseTail(checkpoint);
        var expected = RestoreContactCheckpoint(checkpoint);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) expected.WriteGroundRevisionCheckpoint(writer);
        var length = checked((int)stream.Length);
        Check(BitConverter.ToInt32(checkpoint, 0) == 0x3843454c && checkpoint.Length > length + sizeof(int),
            "Legacy fixture is not a complete version-8 coordinator checkpoint.");
        Check(checkpoint.Skip(checkpoint.Length - length).SequenceEqual(stream.ToArray()),
            "The serialized recorder revision state does not match the checkpoint tail.");
        var legacy = checkpoint.Take(checkpoint.Length - length).ToArray();
        Array.Copy(BitConverter.GetBytes(0x3743454c), legacy, sizeof(int));
        return legacy;
    }

    private static void LiveOfflineAndRecoveryAgree()
    {
        foreach (var withSource in new[] { false, true })
        for (var restoreAfter = -1; restoreAfter < 9; restoreAfter++)
        {
            var samples = Replacement().ToList();
            for (var i = 1; i <= 20; i++) samples.Add(Frame(.3 + i, true, 7));
            if (withSource) foreach (var sample in samples) AddSource(sample, 0);
            var harness = new ReplayRecordingRegressionTests.Harness();
            for (var i = 0; i < samples.Count; i++)
            {
                harness.Feed(samples[i].CopyForAnalysis());
                if (i == restoreAfter)
                {
                    var before = harness.Coordinator.CaptureCheckpoint(); harness.Restore(before);
                    Check(before.SequenceEqual(harness.Coordinator.CaptureCheckpoint()), "Coordinator lost ground-departure state.");
                }
            }
            harness.Coordinator.Stop();
            Check(harness.Captures.Count > 0 && harness.Captures.All(capture => capture.IsDiagnosticOnly),
                "Live recording published a normal landing for ground repositioning or lost its diagnostics.");
            Check(harness.Captures.Any(capture => capture.DecisionJournal!.Snapshot().Entries.Any(entry =>
                entry.Code == "ground_reposition_contact_rejected")), "Live diagnostics lost the rejection reason.");
            Check(TouchdownAnalysis.Analyze(samples).Count == 0, "Offline analysis disagrees with live rejection.");
            foreach (var capture in harness.Captures)
                Check(TouchdownAnalysis.Analyze(capture.Samples).Count == 0, "A saved diagnostic no longer contains its grounded evidence.");
        }
    }

    private static TelemetrySample[] Replacement(bool vertical = false)
    {
        var frames = new[] { Frame(0, true), Frame(.05, true), Frame(.1, true), Frame(.12, false, vertical ? 0 : 7),
            Frame(.15, false, vertical ? 0 : 7), Frame(.19, false, vertical ? 0 : 7),
            Frame(.23, false, vertical ? 0 : 7), Frame(.27, false, vertical ? 0 : 7), Frame(.3, true, vertical ? 0 : 7) };
        foreach (var frame in frames.Skip(3)) frame.PlaneAltitudeFeet += vertical ? 10 : 1.5;
        return frames;
    }

    private static TelemetrySample Frame(double time, bool ground, double northMeters = 0) => new TelemetrySample
    {
        Sequence = (long)Math.Round(time * 10000), SimulationTimeSeconds = time, HostElapsedSeconds = time,
        SimulationDeltaSeconds = .02, MotionSimulation = true, OnGround = ground,
        LatitudeDegrees = 42 + northMeters / 111195, LongitudeDegrees = 23,
        PlaneAltitudeFeet = 604, GroundAltitudeFeet = 600, AboveGroundLevelFeet = 4,
        GroundSpeedKnots = 0, VelocityWorldYFps = ground ? 0 : -1, GForce = 1,
        TouchdownNormalVelocityFps = ground ? 1 : 0,
    };

    private static void AddSource(TelemetrySample sample, int mask, int slew = 0)
    {
        var qpc = 1000 + (long)Math.Round(sample.SimulationTimeSeconds * 1000);
        sample.SourceEvidence = new ReplaySourceEvidence(ReplaySourceCoreRegressionTests.Worker, 1, 1, qpc, qpc, 1000,
            SdkReplayState.Inactive, new ReplaySourceObservation(SourceObservationAvailability.Available,
                SourceObservationReason.None, mask, slew, 1, simulationTime: sample.SimulationTimeSeconds,
                latitude: sample.LatitudeDegrees, longitude: sample.LongitudeDegrees, altitudeFeet: sample.PlaneAltitudeFeet,
                pitch: 0, bank: 0, heading: 0));
    }

    private static int Count(IEnumerable<TelemetrySample> samples)
    { var continuity = new TelemetryContactContinuity(); return samples.Count(sample => continuity.Observe(sample)); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
