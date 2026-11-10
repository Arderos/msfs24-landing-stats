using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class CumulativeGroundDepartureRegressionTests
{
    private enum Motion { Horizontal, Vertical, Rotation }
    private const double FeetToMeters = .3048;
    private const double KnotsToMetersPerSecond = 1852.0 / 3600.0;

    public static void RunAll()
    {
        ProgressiveUnsupportedMotionCannotArmAContact();
        SparseAndIrregularCadenceRetainsEvaluationSupport();
        CoherentMotionAndRotatingDatumRemainSupported();
        ObservedPoseNoiseAndPublicationTimingRemainSupported();
        PoseComparisonResolutionBoundsGroundEvidence();
        GroundRecoveryEndsProvisionalRejection();
        GroundedAnomalyDoesNotTruncateAnAcceptedContact();
        NonFiniteVelocityObservationsRoundTripWithoutChangingRawValues();
        SameTimeVelocityRevisionsAgreeWithStoredAnalysis();
        NumericRevisionsRetainPendingGroundEvidence();
        MissingChannelsStayIndependent();
        ShortBouncesAndAirborneCaptureStartsRemainSupported();
        CheckpointsRetainAccumulatedHistory();
        LiveRecordingAndRecoveryRetainTheirEvidence();
        PauseDoesNotAccumulateStaleMovement();
        InactiveIntervalsDoNotSupplyMotionHistory();
        PendingGroundEvidenceCannotCrossANewTimeline();
        RepeatedDeliveriesCannotRetainEvictedProof();
        LongGroundHistoryDoesNotOutliveRecordedEvidence();
    }

    private static void ProgressiveUnsupportedMotionCannotArmAContact()
    {
        foreach (Motion motion in Enum.GetValues(typeof(Motion)))
        foreach (var period in new[] { .02, .05, .1 })
        foreach (var origin in new[] { 0.0, 1000000.0 })
        {
            var samples = Departure(motion, false, period, origin);
            var before = samples.Select(TelemetryCsv.Format).ToArray();
            Check(Count(samples) == 0, "Progressive unsupported " + motion + " motion armed a contact.");
            var journal = new TelemetryDecisionJournal();
            Check(TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal }).Count == 0,
                "Public analysis revived progressive unsupported " + motion + " motion.");
            Check(journal.Snapshot().Entries.Any(entry => entry.Code == "ground_reposition_contact_rejected"),
                "The accumulated-motion rejection has no recorded reason.");
            Check(before.SequenceEqual(samples.Select(TelemetryCsv.Format)), "Accumulated-motion analysis changed raw telemetry.");
        }
    }

    private static void CoherentMotionAndRotatingDatumRemainSupported()
    {
        foreach (Motion motion in Enum.GetValues(typeof(Motion)))
        foreach (var period in new[] { .02, .05, .1 })
        {
            var samples = Departure(motion, true, period);
            Check(Count(samples) == 1 && TouchdownAnalysis.Analyze(samples).Count == 1,
                "Supported " + motion + " motion lost its contact.");
        }
        var rotating = Departure(Motion.Rotation, true);
        foreach (var sample in rotating)
        {
            // Position and WORLD Y can describe different aircraft reference
            // points. A rotating offset must not be labelled a vertical shift.
            var pitch = sample.PitchDegrees * Math.PI / 180;
            sample.PlaneAltitudeFeet += 20 * Math.Sin(pitch) / FeetToMeters;
            sample.LatitudeDegrees += NorthDegrees(20 * (Math.Cos(pitch) - 1));
        }
        Check(Count(rotating) == 1 && TouchdownAnalysis.Analyze(rotating).Count == 1,
            "A changing attitude with an unknown datum offset was treated as unsupported translation.");

        var wrapped = Departure(Motion.Rotation, true);
        foreach (var sample in wrapped)
        {
            sample.HeadingTrueDegrees = (359 + sample.PitchDegrees) % 360;
            sample.PitchDegrees = 0;
            sample.RotationVelocityBodyYRadiansPerSecond = sample.RotationVelocityBodyXRadiansPerSecond;
            sample.RotationVelocityBodyXRadiansPerSecond = 0;
        }
        Check(Count(wrapped) == 1, "Heading wrap manufactured unsupported angular movement.");
    }

    private static void SparseAndIrregularCadenceRetainsEvaluationSupport()
    {
        foreach (Motion motion in Enum.GetValues(typeof(Motion)))
        foreach (var supported in new[] { false, true })
        foreach (var origin in new[] { 0.0, 1000000.0 })
        foreach (var cadence in new[] { new[] { .125 }, new[] { .15 }, new[] { .2 }, new[] { .24 },
            new[] { .25 }, new[] { .07, .19, .23, .11 } })
        {
            // A stable prefix supplies the complete preceding grounded window
            // even at the slowest accepted cadence. Clock origin must not alter it.
            var times = new List<double>();
            for (var time = -3.0; time <= 2.9; time += cadence[(times.Count - 1) % cadence.Length]) times.Add(time);
            var samples = Departure(motion, supported, times, origin);
            var expected = supported ? 1 : 0;
            Check(Count(samples) == expected && TouchdownAnalysis.Analyze(samples).Count == expected,
                "Sparse or irregular cadence changed " + motion + " departure eligibility.");
            var continuity = new TelemetryContactContinuity();
            var contacts = 0;
            foreach (var sample in samples)
            {
                if (continuity.Observe(sample)) contacts++;
                var checkpoint = Checkpoint(continuity);
                continuity = Restore(checkpoint);
                Check(checkpoint.SequenceEqual(Checkpoint(continuity)), "Sparse cumulative support changed across recovery.");
            }
            Check(contacts == expected, "Restoring sparse cumulative support changed its departure decision.");
        }
    }

    private static void SameTimeVelocityRevisionsAgreeWithStoredAnalysis()
    {
        foreach (Motion motion in Enum.GetValues(typeof(Motion)))
        {
            var original = Departure(motion, true);
            var revised = new List<TelemetrySample>();
            foreach (var sample in original)
            {
                if (sample.OnGround)
                {
                    var earlier = sample.CopyForAnalysis();
                    earlier.GroundSpeedKnots = earlier.VelocityWorldYFps = earlier.RotationVelocityBodyXRadiansPerSecond = 0;
                    revised.Add(earlier);
                }
                revised.Add(sample.CopyForAnalysis());
            }
            var retained = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(revised));
            Check(Count(revised) == 1 && Count(retained) == 1 && TouchdownAnalysis.Analyze(retained).Count == 1,
                "A later same-time velocity observation changed accumulated-motion eligibility.");
        }
    }

    private static void NumericRevisionsRetainPendingGroundEvidence()
    {
        foreach (var offset in new[] { -1e-10, 0, 1e-10 })
        {
            var prefix = Departure(Motion.Rotation, false).TakeWhile(sample => sample.SimulationTimeSeconds <= 2.2 + 1e-9).ToList();
            var revision = prefix.Last().CopyForAnalysis();
            revision.SimulationTimeSeconds += offset; revision.HostElapsedSeconds += offset;
            prefix.Add(revision);
            var samples = prefix.Concat(new[] { Frame(2.25, false), Frame(2.3, true) }).ToArray();
            var canonical = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(samples));
            Check(Count(samples) == 0 && Count(canonical) == 0,
                "A numeric timestamp revision erased pending cumulative departure evidence.");
            var continuity = new TelemetryContactContinuity();
            foreach (var sample in prefix) continuity.Observe(sample);
            continuity = Restore(Checkpoint(continuity));
            Check(!continuity.Observe(samples[samples.Length - 2]) && !continuity.Observe(samples[samples.Length - 1]),
                "A revised cumulative endpoint changed eligibility after checkpoint recovery.");
        }
    }

    private static void GroundedAnomalyDoesNotTruncateAnAcceptedContact()
    {
        var samples = Enumerable.Range(0, 47).Select(index =>
        {
            var time = index * .05;
            var sample = Frame(time, time >= .1 - 1e-9);
            sample.GForce = Math.Abs(time - .1) < 1e-9 ? 1.2 : Math.Abs(time - 1.95) < 1e-9 ? 2 : 1;
            sample.PitchDegrees = 8 * Math.Max(0, Math.Min(1, (time - 1.25) / .6));
            return sample;
        }).ToArray();
        var before = samples.Select(TelemetryCsv.Format).ToArray();
        var journal = new TelemetryDecisionJournal();
        var contact = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions { DecisionJournal = journal }).Single();
        Check(Count(samples) == 1 && contact.PeakG == 2 && contact.GAtFirstGroundSample == 1.2,
            "Unsupported grounded orientation without departure changed an accepted contact's eligibility or G measurements.");
        Check(!journal.Snapshot().Entries.Any(entry => entry.Code == "ground_reposition_measurement_boundary"),
            "Pending grounded cumulative evidence prematurely ended contact measurements.");
        Check(before.SequenceEqual(samples.Select(TelemetryCsv.Format)), "Grounded cumulative evidence changed raw measurements.");

        // The same motion must still reject an actual subsequent departure.
        var departed = samples.Select(sample => sample.CopyForAnalysis()).ToArray();
        foreach (var sample in departed)
            if (sample.SimulationTimeSeconds >= 1.8 - 1e-9 && sample.SimulationTimeSeconds < 2.05 - 1e-9) sample.OnGround = false;
        var departedJournal = new TelemetryDecisionJournal();
        var retained = TouchdownAnalysis.Analyze(departed, new TouchdownAnalysisOptions { DecisionJournal = departedJournal }).Single();
        Check(departedJournal.Snapshot().Entries.Any(entry => entry.Code == "ground_reposition_contact_rejected") && retained.PeakG == 1.2,
            "The actual rejected departure did not end support before its later G spike.");
    }

    private static void NonFiniteVelocityObservationsRoundTripWithoutChangingRawValues()
    {
        foreach (var missing in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var sample = Frame(.5, true);
            sample.GroundSpeedKnots = missing;
            sample.RotationVelocityBodyXRadiansPerSecond = missing;
            var before = TelemetryCsv.Format(sample);
            var continuity = new TelemetryContactContinuity();
            continuity.Observe(sample);
            var bytes = Checkpoint(continuity);
            var restored = Restore(bytes);
            Check(bytes.SequenceEqual(Checkpoint(restored)), "A non-finite velocity endpoint could not round-trip through recovery.");

            var unknown = sample.CopyForAnalysis();
            unknown.GroundSpeedKnots = unknown.RotationVelocityBodyXRadiansPerSecond = double.NaN;
            var expected = new TelemetryContactContinuity();
            expected.Observe(unknown);
            Check(CumulativeBytes(continuity).SequenceEqual(CumulativeBytes(expected)),
                "Derived cumulative speed must normalize unavailable or infinite values to unknown.");
            Check(TelemetryCsv.Format(sample) == before, "Normalizing derived cumulative speeds modified raw telemetry.");
            Check(!restored.Observe(Frame(.55, false)) && restored.Observe(Frame(.56, true)),
                "An unavailable stored velocity invented a ground-motion rejection.");
        }
    }

    private static byte[] CumulativeBytes(TelemetryContactContinuity continuity)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        continuity.WriteCumulativeCheckpoint(writer);
        return stream.ToArray();
    }

    private static void ObservedPoseNoiseAndPublicationTimingRemainSupported()
    {
        var jitter = new List<TelemetrySample>();
        var time = 0.0;
        for (var index = 0; time <= 3.1; index++)
        {
            var sample = Frame(time, time < 2.4 || time >= 2.7);
            // Recurrent grounded sensor jitter can return to its starting pose.
            // Its endpoint difference alone does not describe the observed noise.
            sample.PitchDegrees = time < 1.8 ? .03 * Math.Sin(index * 2.4) : .03 + (time - 1.8) * .01;
            jitter.Add(sample);
            time += new[] { .035, .065, .045, .055 }[index % 4];
        }
        Check(Count(jitter) == 1 && TouchdownAnalysis.Analyze(jitter).Count == 1,
            "Grounded orientation jitter that cancels at its endpoints invalidated a later contact.");

        var verticalJitter = jitter.Select(sample =>
        {
            var copy = sample.CopyForAnalysis();
            copy.PlaneAltitudeFeet += sample.PitchDegrees / FeetToMeters;
            copy.PitchDegrees = 0;
            return copy;
        }).ToArray();
        Check(Count(verticalJitter) == 1 && TouchdownAnalysis.Analyze(verticalJitter).Count == 1,
            "Grounded altitude jitter that cancels at its endpoints invalidated a later contact.");

        var asynchronous = new List<TelemetrySample>();
        const double degreesPerSecond = 5;
        for (var index = 0; index <= 62; index++)
        {
            time = index * .05;
            var sample = Frame(time, time < 2.4 || time >= 2.7);
            // Pose publication can lead or trail the common frame time by less
            // than one observation while the angular-rate channel is constant.
            var publicationOffset = index % 17 == 0 ? .045 : index % 23 == 0 ? -.045 : 0;
            sample.PitchDegrees = degreesPerSecond * (time + publicationOffset);
            sample.RotationVelocityBodyXRadiansPerSecond = degreesPerSecond * Math.PI / 180;
            asynchronous.Add(sample);
        }
        Check(Count(asynchronous) == 1 && TouchdownAnalysis.Analyze(asynchronous).Count == 1,
            "One-frame pose publication timing invalidated coherent constant-rate rotation.");
    }

    private static void PoseComparisonResolutionBoundsGroundEvidence()
    {
        foreach (var vertical in new[] { false, true })
        foreach (var sign in new[] { -1.0, 1.0 })
        foreach (var resolved in new[] { false, true })
        {
            // Exercise the existing comparison contract independently of its
            // implementation constants: 0.001 ft and 0.00000001 degrees.
            var scale = sign * (resolved ? 8 : .8);
            var samples = Enumerable.Range(0, 55).Select(index =>
            {
                var time = index * .05;
                var sample = Frame(time, time < 2.3 - 1e-9 || time >= 2.6 - 1e-9);
                var progress = Math.Max(0, Math.Min(1, time - 1.2));
                if (vertical) sample.PlaneAltitudeFeet += progress * scale * .001;
                else
                {
                    sample.LatitudeDegrees += progress * scale * .00000001;
                    sample.LongitudeDegrees += progress * scale * .00000001;
                }
                return sample;
            }).ToArray();
            var expected = resolved ? 0 : 1;
            Check(Count(samples) == expected && TouchdownAnalysis.Analyze(samples).Count == expected,
                "Ground evidence confused unresolved pose variation with resolved unsupported " +
                (vertical ? "altitude" : "position") + " motion.");
            var continuity = new TelemetryContactContinuity();
            var contacts = 0;
            foreach (var sample in samples)
            {
                if (continuity.Observe(sample)) contacts++;
                continuity = Restore(Checkpoint(continuity));
            }
            Check(contacts == expected, "Recovery changed the pose-resolution decision.");
        }
    }

    private static void GroundRecoveryEndsProvisionalRejection()
    {
        foreach (Motion motion in Enum.GetValues(typeof(Motion)))
        {
            var prefix = Departure(motion, false).TakeWhile(sample => sample.OnGround).ToList();
            var last = prefix.Last();
            TelemetrySample Next(double time, bool ground)
            {
                var sample = last.CopyForAnalysis();
                sample.Sequence = (long)Math.Round(time * 10000);
                sample.SimulationTimeSeconds = sample.HostElapsedSeconds = time;
                sample.OnGround = ground;
                return sample;
            }
            var immediate = prefix.Concat(new[] { Next(2.3, false), Next(2.35, true) }).ToArray();
            Check(Count(immediate) == 0 && TouchdownAnalysis.Analyze(immediate).Count == 0,
                "Confirmed unsupported motion was forgotten before its immediate settling contact.");

            // New stable ground observations replace the pending motion proof.
            // That old proof must not condemn a later independent departure.
            var recovered = prefix.ToList();
            for (var index = 1; index <= 60; index++) recovered.Add(Next(last.SimulationTimeSeconds + index * .05, true));
            var departureTime = recovered.Last().SimulationTimeSeconds;
            for (var index = 1; index <= 40; index++)
            {
                var t = index * .05;
                var sample = Next(departureTime + t, index == 40);
                sample.PlaneAltitudeFeet += .5 * (1 - Math.Cos(Math.PI * t)) / FeetToMeters;
                sample.VelocityWorldYFps = .5 * Math.PI * Math.Sin(Math.PI * t) / FeetToMeters;
                recovered.Add(sample);
            }
            Check(Count(recovered) == 1 && TouchdownAnalysis.Analyze(recovered).Count == 1,
                "An earlier ground anomaly rejected a new supported departure after ground recovery.");
            var continuity = new TelemetryContactContinuity();
            var contacts = 0;
            foreach (var sample in recovered)
            {
                if (continuity.Observe(sample)) contacts++;
                continuity = Restore(Checkpoint(continuity));
            }
            Check(contacts == 1, "Recovery prolonged a provisional ground rejection.");
        }
    }

    private static void MissingChannelsStayIndependent()
    {
        var horizontal = Departure(Motion.Horizontal, false);
        foreach (var sample in horizontal) sample.VelocityWorldYFps = double.NaN;
        Check(Count(horizontal) == 0, "Missing WORLD Y disabled available horizontal evidence.");
        foreach (var sample in horizontal) sample.GroundSpeedKnots = double.NaN;
        Check(Count(horizontal) == 1, "Missing ground speed invented horizontal inconsistency.");

        var vertical = Departure(Motion.Vertical, false);
        foreach (var sample in vertical) sample.GroundSpeedKnots = double.NaN;
        Check(Count(vertical) == 0, "Missing ground speed disabled available vertical evidence.");
        foreach (var sample in vertical) sample.VelocityWorldYFps = double.NaN;
        Check(Count(vertical) == 1, "Missing WORLD Y invented vertical inconsistency.");

        var rotation = Departure(Motion.Rotation, false);
        foreach (var sample in rotation) sample.RotationVelocityBodyXRadiansPerSecond = double.NaN;
        Check(Count(rotation) == 1, "Missing angular velocity invented rotational inconsistency.");
    }

    private static void ShortBouncesAndAirborneCaptureStartsRemainSupported()
    {
        var bounce = new[] { Frame(0, false), Frame(.01, true), Frame(.02, false), Frame(.021, true) };
        Check(Count(bounce) == 2 && TouchdownAnalysis.Analyze(bounce).Count == 2,
            "Cumulative motion added a minimum duration to a vertical bounce.");
        var midair = Departure(Motion.Vertical, false).SkipWhile(sample => sample.OnGround).ToArray();
        Check(Count(midair) == 1 && TouchdownAnalysis.Analyze(midair).Count == 1,
            "Starting capture airborne now requires a recorded ground departure.");
        var fresh = Departure(Motion.Vertical, false).ToList();
        var air = Frame(3, false); air.FlightSegment = 2;
        var ground = Frame(3.001, true); ground.FlightSegment = 2;
        fresh.Add(air); fresh.Add(ground);
        Check(Count(fresh) == 1, "The rejected departure contaminated a new flight segment.");
    }

    private static void CheckpointsRetainAccumulatedHistory()
    {
        foreach (var supported in new[] { false, true })
        {
            var samples = Departure(Motion.Vertical, supported);
            for (var restoreAfter = 0; restoreAfter < samples.Length; restoreAfter++)
            {
                var continuity = new TelemetryContactContinuity();
                var contacts = 0;
                for (var index = 0; index < samples.Length; index++)
                {
                    if (continuity.Observe(samples[index])) contacts++;
                    if (index != restoreAfter) continue;
                    var bytes = Checkpoint(continuity);
                    continuity = Restore(bytes);
                    Check(bytes.SequenceEqual(Checkpoint(continuity)), "Accumulated-motion checkpoint changed on restoration.");
                }
                Check(contacts == (supported ? 1 : 0), "Recovery changed a partly accumulated departure decision.");
            }
        }
    }

    private static void LiveRecordingAndRecoveryRetainTheirEvidence()
    {
        foreach (var supported in new[] { false, true })
        foreach (var restoreAfter in new[] { -1, 20, 30, 40, 48 })
        {
            var samples = Departure(Motion.Vertical, supported);
            var harness = new ReplayRecordingRegressionTests.Harness();
            for (var index = 0; index < samples.Length; index++)
            {
                harness.Feed(samples[index].CopyForAnalysis());
                if (index == restoreAfter) harness.Restore(harness.Coordinator.CaptureCheckpoint());
            }
            harness.Coordinator.Stop();
            Check(harness.Captures.Count > 0, "Accumulated departure lost its capture or diagnostics.");
            Check(harness.Captures.Sum(capture => TouchdownAnalysis.Analyze(capture.Samples).Count) == (supported ? 1 : 0),
                "Stored capture lost the history supporting its live decision.");
            Check(harness.Captures.All(capture => capture.IsDiagnosticOnly == !supported),
                "Accumulated-motion rejection changed across recorder recovery.");
        }
    }

    private static void LongGroundHistoryDoesNotOutliveRecordedEvidence()
    {
        foreach (var period in new[] { .01, .0001 })
        {
            // One run exercises time eviction, the other the independent row
            // bound. Both then record the same recent unsupported departure.
            var harness = new ReplayRecordingRegressionTests.Harness();
            for (var index = 0; index < 5000; index++) harness.Feed(Frame(index * period, true));
            var bounded = harness.Coordinator.CaptureCheckpoint();
            harness.Restore(bounded);
            Check(bounded.SequenceEqual(harness.Coordinator.CaptureCheckpoint()), "Bounded history changed across recovery.");
            foreach (var sample in Departure(Motion.Horizontal, false, .05, 5000 * period))
                harness.Feed(sample.CopyForAnalysis());
            harness.Coordinator.Stop();
            Check(harness.Captures.Count > 0 && harness.Captures.All(capture => capture.IsDiagnosticOnly &&
                TouchdownAnalysis.Analyze(capture.Samples).Count == 0),
                "Pre-roll eviction left the archive unable to reproduce the live accumulated-motion rejection.");
        }
    }

    private static void PauseDoesNotAccumulateStaleMovement()
    {
        foreach (var supported in new[] { false, true })
        foreach (var advancing in new[] { false, true })
        {
            var samples = Departure(Motion.Vertical, supported);
            var harness = new ReplayRecordingRegressionTests.Harness();
            var offset = 0.0;
            for (var index = 0; index < samples.Length; index++)
            {
                if (index == 30)
                {
                    harness.Coordinator.SetSdkPauseFlags(1);
                    for (var pausedIndex = 1; pausedIndex <= 3; pausedIndex++)
                    {
                        var paused = samples[index - 1].CopyForAnalysis();
                        paused.SimulationTimeSeconds += advancing ? pausedIndex * 10 : 0;
                        paused.HostElapsedSeconds += pausedIndex * 10;
                        harness.Feed(paused);
                    }
                    harness.Restore(harness.Coordinator.CaptureCheckpoint());
                    harness.Coordinator.SetSdkPauseFlags(0);
                    offset = advancing ? 30 : 0;
                }
                var sample = samples[index].CopyForAnalysis();
                sample.SimulationTimeSeconds += offset;
                if (index >= 30) sample.HostElapsedSeconds += 30;
                harness.Feed(sample);
            }
            harness.Coordinator.Stop();
            Check(harness.Captures.Count > 0 && harness.Captures.All(capture => capture.IsDiagnosticOnly == !supported) &&
                harness.Captures.Sum(capture => TouchdownAnalysis.Analyze(capture.Samples).Count) == (supported ? 1 : 0),
                "SDK pause or paused recovery changed the accumulated-motion decision.");
        }
    }

    private static void InactiveIntervalsDoNotSupplyMotionHistory()
    {
        var samples = Departure(Motion.Vertical, false);
        var interruption = samples.Single(sample => Math.Abs(sample.SimulationTimeSeconds - 1.3) < 1e-9);
        interruption.MotionSimulation = false;
        interruption.SimulatorState = TelemetrySimulatorState.Inactive;
        Check(Count(samples) == 1 && TouchdownAnalysis.Analyze(samples).Count == 1,
            "Accumulated evidence crossed an inactive interval without continuous observations.");
    }

    private static void PendingGroundEvidenceCannotCrossANewTimeline()
    {
        var prefix = Departure(Motion.Rotation, false).TakeWhile(sample => sample.SimulationTimeSeconds <= 2.2 + 1e-9).ToArray();
        Check(Count(prefix.Concat(new[] { Frame(2.25, false), Frame(2.3, true) })) == 0,
            "The boundary fixture did not establish pending unsupported ground motion.");
        foreach (var boundary in new[] { "flight segment", "reversed clock", "observation gap" })
        {
            var time = boundary == "reversed clock" ? .1 : boundary == "observation gap" ? 12.25 : 2.25;
            var air = Frame(time, false);
            var ground = Frame(time + .05, true);
            if (boundary == "flight segment") air.FlightSegment = ground.FlightSegment = 2;
            var samples = prefix.Concat(new[] { air, ground }).ToArray();
            Check(Count(samples) == 1 && TouchdownAnalysis.Analyze(samples).Count == 1,
                "Pending grounded evidence crossed a " + boundary + " and rejected a new contact.");
        }

        var rejectedAir = prefix.Concat(new[] { Frame(2.25, false), Frame(12.25, false), Frame(12.3, true) }).ToArray();
        Check(Count(rejectedAir) == 0 && TouchdownAnalysis.Analyze(rejectedAir).Count == 0,
            "An observation gap retroactively validated an already rejected airborne run.");
    }

    private static void RepeatedDeliveriesCannotRetainEvictedProof()
    {
        var prefix = Departure(Motion.Rotation, false).TakeWhile(sample => sample.SimulationTimeSeconds <= 2.2 + 1e-9).ToArray();
        var samples = prefix.Select(sample => sample.CopyForAnalysis()).ToList();
        for (var index = 0; index < 5000; index++) samples.Add(prefix.Last().CopyForAnalysis());
        var air = prefix.Last().CopyForAnalysis();
        air.SimulationTimeSeconds = air.HostElapsedSeconds = 2.25;
        air.OnGround = false;
        var ground = air.CopyForAnalysis();
        ground.SimulationTimeSeconds = ground.HostElapsedSeconds = 2.3;
        ground.OnGround = true;
        samples.Add(air); samples.Add(ground);
        var deduplicated = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(samples));
        Check(Count(samples) == 0 && Count(deduplicated) == 0 && TouchdownAnalysis.Analyze(samples).Count == 0 &&
            TouchdownAnalysis.Analyze(deduplicated).Count == 0,
            "Same-time delivery count changed eligibility despite identical retained physical observations.");

        var harness = new ReplayRecordingRegressionTests.Harness();
        for (var index = 0; index < samples.Count; index++)
        {
            harness.Feed(samples[index].CopyForAnalysis());
            if (index == samples.Count - 3) harness.Restore(harness.Coordinator.CaptureCheckpoint());
        }
        harness.Coordinator.Stop();
        Check(harness.Captures.Count > 0 && harness.Captures.All(capture => !capture.IsDiagnosticOnly) &&
            harness.Captures.Sum(capture => TouchdownAnalysis.Analyze(capture.Samples).Count) == 1,
            "Same-time pre-roll eviction changed contact eligibility between live recovery and the saved capture.");

        // An active capture retains the original canonical evidence even when
        // subsequent same-time deliveries are coalesced. Its first accepted
        // contact must agree with the offline reconstruction after recovery.
        var approach = Frame(-.1, false);
        approach.VelocityWorldYFps = -3;
        approach.AboveGroundLevelFeet = 100;
        var active = new[] { approach }.Concat(samples).ToArray();
        Check(Count(active) == 1 && TouchdownAnalysis.Analyze(active).Count == 1,
            "The active-capture fixture did not retain exactly its initial contact.");
        foreach (var restore in new[] { false, true })
        {
            var recording = new ReplayRecordingRegressionTests.Harness();
            for (var index = 0; index < active.Length; index++)
            {
                recording.Feed(active[index].CopyForAnalysis());
                if (restore && (index == prefix.Length || index == active.Length - 3))
                    recording.Restore(recording.Coordinator.CaptureCheckpoint());
            }
            recording.Coordinator.Stop();
            var capture = recording.Captures.Single();
            Check(!capture.IsDiagnosticOnly && TouchdownAnalysis.Analyze(capture.Samples).Count == 1 &&
                TouchdownAnalysis.Analyze(TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(capture.Samples))).Count == 1 &&
                capture.DecisionJournal!.Snapshot().Entries.Count(entry => entry.Code == "contact_flag_transition") == 1,
                "Active capture coalescing or recovery changed the retained cumulative proof between recording and analysis.");
        }
    }

    // Legacy fixture conversion uses the production writer to determine the new
    // tail length. It does not duplicate the cumulative frame's binary layout.
    internal static byte[] RemoveCumulativeTail(byte[] checkpoint)
    {
        checkpoint = GroundRepositionRegressionTests.RemoveRevisionTail(checkpoint);
        var expected = GroundRepositionRegressionTests.RestoreContactCheckpoint(checkpoint);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) expected.WriteCumulativeCheckpoint(writer);
        var length = checked((int)stream.Length);
        Check(BitConverter.ToInt32(checkpoint, 0) == 0x3743454c && checkpoint.Length > length + sizeof(int),
            "Legacy fixture is not a complete version-7 coordinator checkpoint.");
        Check(checkpoint.Skip(checkpoint.Length - length).SequenceEqual(stream.ToArray()),
            "The serialized recorder cumulative state does not match the checkpoint tail.");
        return checkpoint.Take(checkpoint.Length - length).ToArray();
    }

    private static byte[] Checkpoint(TelemetryContactContinuity continuity)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(continuity.HasAirborneWitness);
        continuity.WriteSourceCheckpoint(writer); continuity.WriteGroundCheckpoint(writer); continuity.WriteCumulativeCheckpoint(writer);
        continuity.WriteGroundRevisionCheckpoint(writer);
        return stream.ToArray();
    }

    private static TelemetryContactContinuity Restore(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var continuity = new TelemetryContactContinuity();
        continuity.RestoreAirborneWitness(reader.ReadBoolean());
        continuity.RestoreSourceCheckpoint(reader); continuity.RestoreGroundCheckpoint(reader); continuity.RestoreCumulativeCheckpoint(reader);
        continuity.RestoreGroundRevisionCheckpoint(reader);
        Check(reader.BaseStream.Position == reader.BaseStream.Length, "Accumulated-motion checkpoint has unread data.");
        return continuity;
    }

    private static TelemetrySample[] Departure(Motion motion, bool supported, double period = .05, double origin = 0)
        => Departure(motion, supported, Enumerable.Range(0, (int)Math.Round(2.7 / period) + 1).Select(index => index * period), origin);

    private static TelemetrySample[] Departure(Motion motion, bool supported, IEnumerable<double> times, double origin)
    {
        var samples = new List<TelemetrySample>();
        foreach (var time in times)
        {
            var progress = Math.Max(0, Math.Min(1, time - 1.2));
            var moving = time >= 1.2 && time <= 2.2;
            var sample = Frame(origin + time, time < 2.3 - 1e-9 || time >= 2.6 - 1e-9);
            if (motion == Motion.Horizontal)
            {
                sample.LatitudeDegrees += NorthDegrees(progress * 3);
                sample.GroundSpeedKnots = supported && moving ? 3 / KnotsToMetersPerSecond : 0;
            }
            else if (motion == Motion.Vertical)
            {
                sample.PlaneAltitudeFeet += progress * 3 / FeetToMeters;
                sample.VelocityWorldYFps = supported && moving ? 3 / FeetToMeters : 0;
            }
            else
            {
                sample.PitchDegrees = progress * 8;
                sample.RotationVelocityBodyXRadiansPerSecond = supported && moving ? 8 * Math.PI / 180 : 0;
            }
            samples.Add(sample);
        }
        return samples.ToArray();
    }

    private static TelemetrySample Frame(double time, bool ground) => new TelemetrySample
    {
        Sequence = (long)Math.Round(time * 10000), SimulationTimeSeconds = time, HostElapsedSeconds = time,
        SimulationDeltaSeconds = .05, SimulatorState = TelemetrySimulatorState.Running, FlightSegment = 1,
        MotionSimulation = true, OnGround = ground, LatitudeDegrees = 42, LongitudeDegrees = 23,
        PlaneAltitudeFeet = 604, GroundAltitudeFeet = 600, AboveGroundLevelFeet = 4,
        GroundSpeedKnots = 0, VelocityWorldYFps = 0, GForce = 1,
        PitchDegrees = 0, BankDegrees = 0, HeadingTrueDegrees = 0,
        RotationVelocityBodyXRadiansPerSecond = 0, RotationVelocityBodyYRadiansPerSecond = 0,
        RotationVelocityBodyZRadiansPerSecond = 0,
    };

    private static double NorthDegrees(double meters) => meters / (3440.065 * 1852) * 180 / Math.PI;
    private static int Count(IEnumerable<TelemetrySample> samples)
    { var continuity = new TelemetryContactContinuity(); return samples.Count(sample => continuity.Observe(sample)); }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
