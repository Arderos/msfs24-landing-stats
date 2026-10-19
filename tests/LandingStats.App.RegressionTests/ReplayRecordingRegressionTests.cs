using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ReplayRecordingRegressionTests
{
    public static void RunAll()
    {
        ReplayDiscardsApproachAndPendingContact();
        RepeatedReplayCancelsEachUnfinishedLiveApproach();
        RolloutStopsBeforeTheFirstFrozenFrame();
        HandoffStartsFreshBelowGateAndNormallyAboveIt();
        GroundHandoffCannotFabricateContact();
        UncertainObservationsCannotReleaseReplay();
        StationaryFreezeDoesNotProveReplay();
        SdkReplayAndExternalControlRemainIndependent();
        CheckpointRetainsPendingAndActiveReplay();
        InsufficientLiveHistoryIsNotScored();
        AnalysisRejectsReplayAndShortLiveCapture();
        ControlledInputs();
    }

    private static void ReplayDiscardsApproachAndPendingContact()
    {
        var h = new Harness(); h.Live(0, .8);
        Check(h.Coordinator.IsRecording, "Live approach did not start.");
        h.Feed(Frame(1, ground: true, mask: 7)); // Must not become a landing before the next USER reply.
        Check(h.Captures.Count == 0 && !h.Coordinator.HasContact, "Unconfirmed frozen frame became a contact.");
        h.Feed(Frame(1.25, ground: true, mask: 7));
        Check(!h.Coordinator.IsRecording && h.Captures.Count == 0, "Replay preserved the unfinished approach.");
        Check(h.Status == "Recorder.ReplayActive", "Replay warning is absent.");
        Check(h.Raw.All(x => x.SourceEvidence!.Observation.FreezeMask == 0), "Replay leaked into RAW.");
        h.Live(2, 2.8); h.Feed(Frame(2.82, true)); h.Live(2.84, 18, true);
        var saved = h.Captures.Single();
        Check(saved.Samples.All(x => x.SimulationTimeSeconds >= 2), "Handoff borrowed the earlier approach or replay.");
        Check(TouchdownAnalysis.Analyze(saved.Samples).Count == 1, "Handoff lost the real new contact.");
    }

    private static void RepeatedReplayCancelsEachUnfinishedLiveApproach()
    {
        var h = new Harness();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            h.Live(cycle * 3, cycle * 3 + .8);
            h.Replay(cycle * 3 + 1);
            Check(!h.Coordinator.IsRecording && h.Captures.Count == 0, "Repeated replay kept a previous live approach.");
        }
        h.Live(9, 9.8); h.Feed(Frame(9.82, true)); h.Live(9.84, 25, true);
        var saved = h.Captures.Single();
        Check(saved.Samples.All(sample => sample.SimulationTimeSeconds >= 9), "Final landing contains frames from a discarded control interval.");
    }

    private static void RolloutStopsBeforeTheFirstFrozenFrame()
    {
        foreach (var nearDeadline in new[] { false, true })
        {
            var h = new Harness(); h.Live(0, .8); h.Feed(Frame(.82, true));
            h.Live(.84, nearDeadline ? 15.8 : 1.2, true);
            var prefix = h.Raw.ToArray();
            var start = nearDeadline ? 15.84 : 1.24;
            h.Feed(Frame(start, mask: 7));
            Check(h.Captures.Count == 0, "Pending replay completed a contaminated rollout at its deadline.");
            h.Feed(Frame(start + .25, mask: 7));
            var saved = h.Captures.Single();
            Check(saved.Samples.Select(x => x.Sequence).SequenceEqual(prefix.Select(x => x.Sequence)), "Rollout did not stop at the last live frame.");
            Check(!saved.IsDiagnosticOnly && !h.Coordinator.IsRecording, "Real contact was discarded or recording continued.");
            h.Live(start + .3, start + 17, true, mask: 7);
            Check(h.Captures.Count == 1, "Replay produced extra contacts after the truncated rollout.");
            var result = TouchdownAnalysis.Analyze(saved.Samples).Single();
            if (!nearDeadline) Check(!result.Impact.IsComplete, "Shortened impact window was reported complete.");
        }
    }

    private static void HandoffStartsFreshBelowGateAndNormallyAboveIt()
    {
        foreach (var above in new[] { false, true })
        {
            var h = new Harness(); h.Replay(0);
            var released = Frame(.5, agl: above ? 700 : 200); released.VelocityWorldYFps = 2;
            h.Feed(released);
            Check(h.Coordinator.IsRecording == !above, "Handoff did not respect the 500 ft gate.");
            if (above) { h.Feed(Frame(.7, agl: 600)); Check(!h.Coordinator.IsRecording, "Capture started above the gate."); h.Feed(Frame(.9, agl: 400)); }
            Check(h.Coordinator.IsRecording && h.Status == "Recorder.BelowGate", "Live capture did not resume.");
            h.Coordinator.CompleteEpisode(false);
            Check(h.Captures.Single().Samples.All(x => x.SourceEvidence!.Observation.FreezeMask == 0), "Replay pre-roll survived handoff.");
        }
    }

    private static void GroundHandoffCannotFabricateContact()
    {
        var h = new Harness(); h.Replay(0); h.Live(.5, 17, true);
        Check(h.Captures.Count == 0 && !h.Coordinator.IsRecording, "Replay-to-ground fabricated a landing.");
        h.Live(18, 18.8); h.Feed(Frame(18.82, true)); h.Live(18.84, 34, true);
        Check(h.Captures.Count == 1 && TouchdownAnalysis.Analyze(h.Captures[0].Samples).Count == 1, "A fresh live flight could not land after ground handoff.");
    }

    private static void UncertainObservationsCannotReleaseReplay()
    {
        var h = new Harness(); h.Replay(0);
        var unavailable = Frame(.5); unavailable.SourceEvidence = ReplaySourceCoreRegressionTests.Evidence(100, 2100, availability: SourceObservationAvailability.Unavailable);
        h.Feed(unavailable);
        var missing = Frame(.6); missing.SourceEvidence = null; h.Feed(missing);
        var stale = Frame(.7); stale.SourceEvidence = ReplaySourceCoreRegressionTests.Evidence(100, 2100, mask: 0); h.Feed(stale);
        h.Feed(Frame(.8, mask: 3)); h.Feed(Frame(.9, mask: 0, slew: 1));
        Check(h.Status == "Recorder.ReplayActive" && !h.Coordinator.IsRecording, "Unknown, stale, partial or slew state released replay.");
        h.Coordinator.SourceInterrupted("fixture_gap"); h.Coordinator.RestoreTransportLifecycle(new LandingStats.Runtime.Contracts.SimulatorLifecycleSnapshotMessage(connected: true, known: true, running: 1, pause: 0));
        h.Feed(missing);
        Check(h.Status == "Recorder.ReplayActive" && !h.Coordinator.IsRecording, "A source gap erased external replay.");
        h.Feed(Frame(1)); Check(h.Coordinator.IsRecording, "Fresh released source did not restore capture.");
    }

    private static void StationaryFreezeDoesNotProveReplay()
    {
        var h = new Harness();
        for (var i = 0; i < 5000; i++) h.Feed(Frame(i * .02, mask: 7, pose: 42));
        Check(h.Status == "Recorder.ReplayChecking" && !h.Coordinator.IsRecording, "Stationary freeze became confirmed replay.");
        Check(h.Coordinator.CaptureCheckpoint().Length < 9 * 1024 * 1024, "Stationary pending buffer is unbounded.");
        h.Feed(Frame(100, pose: 42));
        Check(h.Status != "Recorder.ReplayActive", "Static freeze was classified as replay on release.");
        Check(!ReplayTelemetryDetector.HasReplaySourceEvidence(h.Raw), "Offline policy classified static freeze as moving replay.");
    }

    private static void SdkReplayAndExternalControlRemainIndependent()
    {
        var h = new Harness(); h.Live(0, .8);
        h.Coordinator.HandleFlowEvent(SimulatorFlowEvent.REPLAY_START);
        h.Feed(Frame(1, sdk: SdkReplayState.Active));
        Check(!h.Coordinator.IsRecording && h.Captures.Count == 0, "SDK replay kept a live approach.");
        h.Coordinator.HandleFlowEvent(SimulatorFlowEvent.REPLAY_END);
        h.Feed(Frame(1.2, sdk: SdkReplayState.Inactive, mask: 7));
        h.Feed(Frame(1.45, sdk: SdkReplayState.Inactive, mask: 7));
        Check(h.Status == "Recorder.ReplayActive", "SDK end bypassed ongoing external replay.");
        h.Coordinator.HandleFlowEvent(SimulatorFlowEvent.REPLAY_END);
        h.Feed(Frame(1.5)); Check(h.Coordinator.IsRecording, "External release after SDK end did not resume.");
        Check(!new ReplaySourceAssessor().Observe(Frame(2, mask: 7).SourceEvidence).ConfirmsReplay, "External control was renamed authoritative SDK replay.");
    }

    private static void CheckpointRetainsPendingAndActiveReplay()
    {
        foreach (var active in new[] { false, true })
        {
            var h = new Harness(); h.Live(0, .8); h.Feed(Frame(1, mask: 7));
            if (active) h.Feed(Frame(1.25, mask: 7));
            var checkpoint = h.Coordinator.CaptureCheckpoint(); h.Restore(checkpoint);
            if (!active) Check(h.Coordinator.MaximumRetainedSequence >= h.LastSequence, "Checkpoint lost a buffered parent sequence.");
            if (!active) h.Feed(Frame(1.25, mask: 7));
            Check(h.Status == "Recorder.ReplayActive" && h.Captures.Count == 0 && !h.Coordinator.IsRecording, "Recovery forgot replay or its pending movement evidence.");
            h.Feed(Frame(2)); Check(h.Coordinator.IsRecording, "Recovered replay cannot hand control back.");
            foreach (var cut in new[] { 0, 4, checkpoint.Length - 1 })
            {
                try { LandingEpisodeCoordinator.RestoreCheckpoint(checkpoint.Take(cut).ToArray(), h.Port, () => 0, () => Array.Empty<string>()); }
                catch (InvalidDataException) { continue; }
                throw new InvalidOperationException("Truncated replay checkpoint accepted.");
            }
        }
    }

    private static void InsufficientLiveHistoryIsNotScored()
    {
        var options = new TouchdownAnalysisOptions { RequirePreContactHistory = true, DecisionJournal = new TelemetryDecisionJournal() };
        var shortCapture = new[] { Frame(0), Frame(.02, true), Frame(.04, true) };
        Check(TouchdownAnalysis.Analyze(shortCapture, options).Count == 0, "Two-frame live handoff was scored.");
        Check(options.DecisionJournal.Snapshot().Entries.Any(x => x.Code == "insufficient_live_history"), "Short capture rejection has no reason.");
        var enough = Enumerable.Range(0, 41).Select(i => Frame(i * .02)).Concat(new[] { Frame(.82, true), Frame(.84, true) }).ToArray();
        Check(TouchdownAnalysis.Analyze(enough, options).Count == 1, "Sufficient airborne history required a full 15-second rollout.");
        Check(TouchdownAnalysis.Analyze(shortCapture).Count == 1, "Historical best-effort behavior changed.");
        Check(ReplayTelemetryDetector.HasReplaySourceEvidence(new[] { Frame(0, mask: 7), Frame(.25, mask: 7) }), "Offline replay source guard missed moving freeze.");
    }

    private static void ControlledInputs()
    {
        var root = Environment.GetEnvironmentVariable("MSFSLS_CONTROLLED_REPLAY_INPUTS");
        if (string.IsNullOrEmpty(root)) return;
        var identities = new[] { "5730f786aaa9a4bcf2db51127dd7fe0a", "ffcf327771aa227ea64d1e4cce4f3ede", "cabf6fa0256bc1bb96c4b9b62c0e6982" };
        for (var i = 0; i < identities.Length; i++)
        {
            var store = new EpisodeTransactionStore(root!, "7d34aec2efe4dc28f4c1e035599d3904b6009b040e742fe0ddd446d94a9575ce");
            var input = store.ReadInputs(store.Read(Guid.ParseExact(identities[i], "N"))).Episode;
            var h = new Harness(); h.Coordinator.SetMetadata(input.AircraftTitle, input.AircraftType, input.AircraftModel);
            Check(ReplayTelemetryDetector.HasReplaySourceEvidence(input.Samples) == (i != 0), "Controlled source classification mismatch.");
            foreach (var frame in input.Samples)
            {
                var copy = TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(frame));
                copy.SourceEvidence = frame.SourceEvidence; copy.ControllerCatalog = frame.ControllerCatalog; h.Feed(copy);
            }
            h.Coordinator.Stop();
            var contacts = h.Captures.Where(x => !x.IsDiagnosticOnly).SelectMany(x => TouchdownAnalysis.Analyze(x.Samples,
                new TouchdownAnalysisOptions { RequirePreContactHistory = true })).ToArray();
            Check(contacts.Length == (i == 0 ? 2 : 0), "Controlled recording saved replay or lost the manual bounce.");
            Console.WriteLine("CONTROLLED " + identities[i] + " frames=" + input.Samples.Count + " contacts=" + contacts.Length);
        }
    }

    private static void AnalysisRejectsReplayAndShortLiveCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-replay-analysis-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(root);
            var processor = new EpisodeProcessor(new FlightModelGeometryResolver(Array.Empty<string>(), false), repository, new object());
            foreach (var sdk in new[] { false, true })
            {
                var samples = new[] { Frame(0, mask: sdk ? 0 : 7, sdk: sdk ? SdkReplayState.Active : SdkReplayState.Unknown),
                    Frame(.25, mask: sdk ? 0 : 7), Frame(.5, true, sdk ? 0 : 7), Frame(.75, true, sdk ? 0 : 7) };
                Check(!ReplayTelemetryDetector.IsReplayLike(samples), "Replay fixture unexpectedly satisfies the old near-zero-speed heuristic.");
                var episode = new LandingEpisodeEventArgs(1, samples, "MSFS", "Fixture", "jet", "model", Array.Empty<AirportFacility>(),
                    Array.Empty<string>(), persistentId: Guid.NewGuid());
                var result = processor.ProcessAsync(episode, Array.Empty<AirportFacility>(), DateTime.UtcNow).GetAwaiter().GetResult();
                Check(result.ReplayLike && result.Records.Count == 0 && repository.LoadAll().Count == 0, "Analysis saved a source-proven replay.");
            }
            var shortCapture = new LandingEpisodeEventArgs(2, new[] { Frame(0), Frame(.02, true), Frame(.04, true) },
                "MSFS", "Fixture", "jet", "model", Array.Empty<AirportFacility>(), Array.Empty<string>(), persistentId: Guid.NewGuid());
            var shortResult = processor.ProcessAsync(shortCapture, Array.Empty<AirportFacility>(), DateTime.UtcNow).GetAwaiter().GetResult();
            Check(!shortResult.ReplayLike && shortResult.Records.Count == 0 && repository.LoadAll().Count == 0,
                "Analysis persisted a modern capture without the minimum live fit history.");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static TelemetrySample Frame(double time, bool ground = false, int mask = 0, double agl = 100,
        int slew = 0, double? pose = null, SdkReplayState sdk = SdkReplayState.Unknown)
    {
        var qpc = 100000 + (long)Math.Round(time * 1000);
        var sample = new TelemetrySample { SimulationTimeSeconds = time, SimulationDeltaSeconds = .02, HostElapsedSeconds = time,
            MotionSimulation = true, OnGround = ground, LatitudeDegrees = 42, LongitudeDegrees = 23,
            AboveGroundLevelFeet = ground ? 0 : agl, PlaneAltitudeFeet = 604, GroundAltitudeFeet = 600,
            GroundSpeedKnots = 120, IndicatedAirspeedKnots = 120, VelocityWorldYFps = ground ? 0 : -4,
            TouchdownNormalVelocityFps = ground ? 4 : 0, GForce = ground ? 1.2 : 1 };
        sample.SourceEvidence = new ReplaySourceEvidence(ReplaySourceCoreRegressionTests.Worker, 1, 1, qpc, qpc, 1000, sdk,
            new ReplaySourceObservation(SourceObservationAvailability.Available, SourceObservationReason.None, mask, slew, (uint)qpc,
                simulationTime: time, latitude: pose ?? 42 + time * .00001, longitude: 23, altitudeFeet: 700, pitch: 3, bank: 0, heading: 180));
        return sample;
    }

    private sealed class Harness
    {
        internal LandingEpisodeCoordinator Coordinator;
        internal readonly Port Port = new Port();
        internal readonly List<LandingEpisodeEventArgs> Captures = new List<LandingEpisodeEventArgs>();
        internal readonly List<TelemetrySample> Raw = new List<TelemetrySample>();
        internal string Status = "";
        internal long LastSequence;
        private double _time;
        internal Harness()
        {
            Coordinator = new LandingEpisodeCoordinator(Port, () => _time, () => Array.Empty<string>());
            Attach(); Coordinator.SetSdkSimRunning(true); Coordinator.SetSdkPauseFlags(0); Coordinator.SetRawDebugEnabled(true);
        }
        private void Attach()
        {
            Coordinator.EpisodeCompleted += (_, capture) => Captures.Add(capture);
            Coordinator.RawDebugSampleReceived += (_, frame) => Raw.Add(frame.Sample);
            Coordinator.StatusChanged += (_, status) => Status = status.MessageKey;
        }
        internal void Feed(TelemetrySample frame) { _time = frame.HostElapsedSeconds; frame.Sequence = ++LastSequence; Coordinator.ProcessSample(frame); }
        internal void Live(double start, double end, bool ground = false, int mask = 0)
        { for (var i = 0; start + i * .02 <= end + 1e-9; i++) Feed(Frame(start + i * .02, ground, mask)); }
        internal void Replay(double time) { Feed(Frame(time, mask: 7)); Feed(Frame(time + .25, mask: 7)); }
        internal void Restore(byte[] bytes)
        { Coordinator = LandingEpisodeCoordinator.RestoreCheckpoint(bytes, Port, () => _time, () => Array.Empty<string>()); Attach(); Coordinator.PublishCaptureStatus(); }
    }

    private sealed class Port : IEpisodeFacilityPort
    {
        public long EpisodeId { get; private set; }
        public bool CanQuery => false;
        public double DecisionTime => 0;
        public Task<IReadOnlyList<RunwayFacility>>? RunwayTask => null;
        public Task<EpisodeFacilitySnapshot>? SnapshotTask => null;
        public IReadOnlyList<AirportFacility> CachedAirports => Array.Empty<AirportFacility>();
        public void SetDecisionContext(TelemetryDecisionJournal? journal, double time) { }
        public void BeginEpisode(long episodeId) => EpisodeId = episodeId;
        public void RequestPreliminary(TelemetrySample trigger, IReadOnlyList<TelemetrySample> samples) { }
        public void RequestFinal(IReadOnlyList<TelemetrySample> samples) { }
        public void CompleteWithoutRefresh(string reason) { }
        public void DiscardEpisode(long episodeId) { if (episodeId == EpisodeId) EpisodeId = 0; }
        public EpisodeFacilitySnapshot CurrentSnapshot() => new EpisodeFacilitySnapshot(EpisodeId, Array.Empty<RunwayFacility>(),
            Array.Empty<AirportFacility>(), Array.Empty<TelemetrySample>(), Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false);
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
