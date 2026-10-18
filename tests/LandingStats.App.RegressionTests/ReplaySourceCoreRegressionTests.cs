using System;
using System.IO;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ReplaySourceCoreRegressionTests
{
    internal static readonly Guid Worker = Guid.Parse("d9a58e43-71ec-483f-b2c7-6aa09388951e");
    internal static ReplaySourceEvidence Evidence(long observed = 1000, long frame = 1100, int mask = 7, double latitude = 42,
        long generation = 1, long connection = 1, int slew = 0, SdkReplayState sdk = SdkReplayState.Unknown,
        SourceObservationAvailability availability = SourceObservationAvailability.Available) =>
        new ReplaySourceEvidence(Worker, generation, connection, observed, frame, 1000, sdk,
            availability == SourceObservationAvailability.Available
                ? new ReplaySourceObservation(availability, SourceObservationReason.None, mask, slew, (uint)observed,
                    simulationTime: 12, latitude: latitude, longitude: 23, altitudeFeet: 1000, pitch: 3, bank: -0.0, heading: 180)
                : new ReplaySourceObservation(availability, availability == SourceObservationAvailability.Gap ? SourceObservationReason.ConnectionGap : SourceObservationReason.RequestTimeout));
    internal static void RunAll()
    {
        var sample = new TelemetrySample(); var original = TelemetryBinaryCodec.Encode(sample);
        sample.SourceEvidence = Evidence();
        Check(original.SequenceEqual(TelemetryBinaryCodec.Encode(sample)), "Optional observations changed numeric telemetry.");
        using var single = new MemoryStream(); using var writer = new BinaryWriter(single);
        ReplaySourceEvidenceCodec.Write(writer, sample.SourceEvidence); Check(single.Length == 128, "Evidence layout changed.");
        single.Position = 0; using var reader = new BinaryReader(single); var copy = ReplaySourceEvidenceCodec.Read(reader);
        Check(copy.WorkerInstance == Worker && copy.Observation.FreezeMask == 7 && BitConverter.DoubleToInt64Bits(copy.Observation.Bank) == long.MinValue,
            "Source observation lost identity or IEEE-754 bits.");
        var timeline = ReplaySourceTimelineCodec.Encode(new[] { sample, new TelemetrySample() })!;
        var recovered = ReplaySourceTimelineCodec.Decode(timeline, 2);
        Check(recovered[0]!.FrameQpc == 1100 && recovered[1] == null, "Timeline fabricated legacy evidence.");
        Check(ReplaySourceTimelineCodec.Encode(new[] { new TelemetrySample() }) == null, "Legacy capture acquired a new format member.");
        Reject(() => ReplaySourceTimelineCodec.Decode(timeline, 1));
        for (var length = 0; length < timeline.Length; length++) Reject(() => ReplaySourceTimelineCodec.Decode(timeline.Take(length).ToArray(), 2));
        Reject(() => ReplaySourceTimelineCodec.Decode(timeline.Concat(new byte[] { 0 }).ToArray(), 2));
        foreach (var offset in new[] { 8, 9, 10, 11, 69, 70, 71, 72 })
        { var invalid = (byte[])timeline.Clone(); invalid[offset] = 128; Reject(() => ReplaySourceTimelineCodec.Decode(invalid, 2)); }

        var assessor = new ReplaySourceAssessor();
        Check(assessor.Observe(null, true).Kind == ReplaySourceKind.Unknown, "Legacy numeric suspicion became new source proof.");
        for (var mask = 0; mask < 7; mask++)
        {
            assessor.Observe(Evidence(mask: mask));
            Check(assessor.Observe(Evidence(1250, 1300, mask, 42.01)).Kind == ReplaySourceKind.Unknown, "Partial freeze proved external control.");
        }
        Check(assessor.Observe(Evidence()).Kind == ReplaySourceKind.Unknown, "One frozen observation proved movement.");
        Check(assessor.Observe(Evidence(1250, 1300, latitude: 42.01)).Kind == ReplaySourceKind.ExternallyDrivenPose, "Frozen pose movement was missed.");
        Check(!assessor.Observe(Evidence(1500, 1500, latitude: 42.01)).ConfirmsReplay, "External control was called confirmed replay.");
        Check(assessor.Observe(Evidence(1750, 1800, latitude: 42.01)).Kind == ReplaySourceKind.ExternallyDrivenPose, "Pause erased an observed external-control interval.");
        Check(assessor.Observe(Evidence(1750, 4000)).Kind == ReplaySourceKind.Unknown, "Stale flags stayed usable.");
        Check(assessor.Observe(Evidence(2000, 1900)).Reason == ReplaySourceAssessmentReason.ClockDiscontinuity, "Future observation was accepted.");
        assessor.Observe(Evidence()); assessor.Observe(Evidence(1250, 1300, latitude: 42.01));
        Check(assessor.Observe(Evidence(1500, 1500, generation: 2)).Kind == ReplaySourceKind.Unknown, "Worker replacement inherited external state.");
        Check(assessor.Observe(Evidence(1750, 1800, connection: 2)).Kind == ReplaySourceKind.Unknown, "Connection replacement inherited external state.");
        Check(assessor.Observe(Evidence(2000, 2100, slew: 1)).Reason == ReplaySourceAssessmentReason.Slew, "User slew was classified as replay.");
        Check(assessor.Observe(Evidence(latitude: double.NaN)).Reason == ReplaySourceAssessmentReason.NonFinitePose, "Non-finite pose proved movement.");
        Check(assessor.Observe(Evidence(sdk: SdkReplayState.Active, availability: SourceObservationAvailability.Unavailable)).ConfirmsReplay,
            "Missing optional variables erased authoritative SDK replay.");
        Check(!assessor.Observe(Evidence(sdk: SdkReplayState.Active, availability: SourceObservationAvailability.Gap)).ConfirmsReplay,
            "Connection gap retained SDK state.");
        Check(assessor.Observe(Evidence(mask: 0), true).Kind == ReplaySourceKind.SuspiciousKinematics, "Compound kinematic evidence was lost.");
    }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Malformed source evidence accepted."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
