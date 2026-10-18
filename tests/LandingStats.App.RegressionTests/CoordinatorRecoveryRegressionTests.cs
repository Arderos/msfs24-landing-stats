using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class CoordinatorRecoveryRegressionTests
{
    public static void RunAll()
    {
        StableMetadataAndGap();
        FacilitySnapshots();
        var capture = Guid.NewGuid();
        Action<LandingEpisodeCoordinator>[] script =
        {
            value => value.SetMetadata("First aircraft", "A", "one"),
            value => value.SetSdkSimRunning(true),
            value => value.ProcessSample(Frame(1, 10, false)),
            value => value.ProcessSample(Frame(2, 10.1, false)),
            value => value.SetMetadata("Late second aircraft", "B", "two"),
            value => value.ProcessSample(Frame(3, 10.2, true)),
            value => value.SetSdkPauseFlags(4),
            value => value.ProcessSample(Frame(4, 10.3, true)),
            value => value.ProcessSample(Frame(5, 100.3, true)),
            value => value.SetSdkPauseFlags(0),
            value => value.ProcessSample(Frame(6, 100.4, true)),
            value => value.ProcessSample(Frame(7, 100.5, false)),
            value => value.ProcessSample(Frame(8, 100.6, true)),
            value => value.SourceInterrupted("worker_crashed"),
            value => value.ProcessSample(Frame(9, 100.7, true)),
            value => value.ProcessSample(Frame(10, 100.8, false)),
            value => value.ProcessSample(Frame(11, 100.9, false)),
            value => value.SourceInterrupted("source_stalled"),
            value => value.HandleFlowEvent(SimulatorFlowEvent.REPLAY_START),
            value => value.ProcessSample(Frame(12, 101, false)),
            value => value.HandleFlowEvent(SimulatorFlowEvent.REPLAY_END),
            value => value.ProcessSample(Frame(13, 101.1, false)),
            value => value.ProcessSample(Frame(14, 101.2, true)),
            value => value.Stop()
        };
        var expected = Run(script, capture, -1);
        Check(expected.Count > 1, "Checkpoint scenario produced no useful outputs.");
        for (var split = 0; split <= script.Length; split++)
        {
            var actual = Run(script, capture, split);
            Check(actual.SequenceEqual(expected), "Managed recovery changed captures at boundary " + split);
        }
    }
    private static IReadOnlyList<string> Run(Action<LandingEpisodeCoordinator>[] script, Guid capture, int split)
    {
        var facilities = new EpisodeFacilityCoordinator(new Query()); var now = 0.0;
        Func<DateTime> utc = () => new DateTime(2026, 10, 17, 1, 2, 3, DateTimeKind.Utc).AddSeconds(now);
        var value = new LandingEpisodeCoordinator(facilities, () => now, () => new[] { "stick A" }, utc, capture);
        var outputs = new List<string>();
        void Observe(LandingEpisodeCoordinator current)
        {
            current.EpisodeCompleted += (_, episode) => outputs.Add("episode:" + Signature(episode));
            current.ApproachInterrupted += (_, episode) => outputs.Add("interrupted:" + Signature(episode));
        }
        Observe(value);
        for (var i = 0; i <= script.Length; i++)
        {
            if (i == split)
            {
                var checkpoint = value.CaptureCheckpoint();
                value = LandingEpisodeCoordinator.RestoreCheckpoint(checkpoint, facilities, () => now, () => new[] { "stick A" }, utc);
                Check(value.CaptureCheckpoint().SequenceEqual(checkpoint), "Checkpoint changed exact state on a round trip.");
                Observe(value);
                for (var cut = 0; cut < checkpoint.Length; cut += Math.Max(1, checkpoint.Length / 11))
                    Reject(() => LandingEpisodeCoordinator.RestoreCheckpoint(checkpoint.Take(cut).ToArray(), new EpisodeFacilityCoordinator(new Query()), () => now, () => Array.Empty<string>()));
                Reject(() => LandingEpisodeCoordinator.RestoreCheckpoint(checkpoint.Concat(new byte[] { 0 }).ToArray(), new EpisodeFacilityCoordinator(new Query()), () => now, () => Array.Empty<string>()));
            }
            if (i < script.Length) { now += .1; script[i](value); }
        }
        return outputs;
    }
    private static void StableMetadataAndGap()
    {
        var capture = Guid.NewGuid(); var facilities = new EpisodeFacilityCoordinator(new Query());
        var coordinator = new LandingEpisodeCoordinator(facilities, () => 0, () => Array.Empty<string>(), captureSessionId: capture);
        var complete = new List<LandingEpisodeEventArgs>(); var interrupted = new List<LandingEpisodeEventArgs>();
        coordinator.EpisodeCompleted += (_, episode) => complete.Add(episode);
        coordinator.ApproachInterrupted += (_, episode) => interrupted.Add(episode);
        coordinator.SetMetadata("Original", "type", "model"); coordinator.ProcessSample(Frame(1, 1, false));
        coordinator.SetMetadata("Changed", "another", "other"); coordinator.ProcessSample(Frame(2, 1.1, true));
        coordinator.SourceInterrupted("process_exit");
        Check(complete.Count == 1 && complete[0].AircraftTitle == "Original" && complete[0].AircraftModel == "model" && !complete[0].IsDiagnosticOnly,
            "Late metadata rewrote the captured aircraft or gap discarded a real contact.");
        Check(complete[0].PersistentId == LandingStats.Runtime.Contracts.StableRuntimeIdentity.Episode(capture, 1), "Episode identity was generated at completion.");
        coordinator.ProcessSample(Frame(3, 1.2, true));
        Check(!coordinator.HasContact && !coordinator.IsRecording, "The first ground after recovery invented a landing.");
        coordinator.ProcessSample(Frame(4, 1.3, false));
        coordinator.SourceInterrupted("owner_stalled");
        Check(complete.Count == 1 && interrupted.Count == 1 && interrupted[0].IsDiagnosticOnly && interrupted[0].AircraftTitle == "Unknown aircraft",
            "Interrupted approach was sent as a landing, discarded, or borrowed metadata from the former source.");
        coordinator.Stop();
        Check(complete.Count == 1 && interrupted.Count == 1 && facilities.SnapshotTask!.IsCompleted, "Shutdown duplicated a gap outcome or leaked a facility request.");
    }
    private static void FacilitySnapshots()
    {
        var query = new Query(); var facilities = new EpisodeFacilityCoordinator(query);
        var empty = facilities.CaptureCheckpoint();
        Check(EpisodeFacilityCoordinator.RestoreCheckpoint(empty, new Query()).CaptureCheckpoint().SequenceEqual(empty), "Empty facility state changed on recovery.");
        facilities.BeginEpisode(1); facilities.SetDecisionContext(new TelemetryDecisionJournal(), 1);
        var air = Frame(1, 1, false); var ground = Frame(2, 1.1, true);
        facilities.RequestPreliminary(air, new[] { air });
        var runway = new RunwayFacility { AirportIdent = "PRE", LengthMeters = 2000, WidthMeters = 30 };
        query.Progress = new SceneryQuerySnapshot(new[] { runway }, new[] { new AirportFacility { Ident = "PRE" } }, true, false, 2, 1);
        var beforeReply = facilities.CaptureCheckpoint();
        var resumedQuery = new Query { Progress = query.Progress };
        var resumed = EpisodeFacilityCoordinator.RestoreCheckpoint(beforeReply, resumedQuery, queryRestored: true);
        resumedQuery.Progress = new SceneryQuerySnapshot(new[] { new RunwayFacility { AirportIdent = "NEW", WidthMeters = 45 } },
            new[] { new AirportFacility { Ident = "NEW" } }, true, false, 3, 2);
        resumed.CompleteWithoutRefresh("source_lost_after_new_progress");
        Check(resumed.SnapshotTask!.Result.PreliminaryRunways.Single().AirportIdent == "NEW", "Checkpoint shadowed live partial scenery progress.");
        var restored = EpisodeFacilityCoordinator.RestoreCheckpoint(beforeReply, new Query());
        Check(restored.CaptureCheckpoint().SequenceEqual(beforeReply), "Pending facility state changed on recovery.");
        runway.WidthMeters = 99;
        restored.CompleteWithoutRefresh("source_lost");
        var fallback = restored.SnapshotTask!.GetAwaiter().GetResult();
        Check(fallback.PreliminaryRunways.Single().WidthMeters == 30 && !fallback.PreliminaryRunwayCandidatesComplete && !fallback.FinalLookupCompleted,
            "Recovery lost partial preliminary evidence or fabricated completeness.");
        facilities.FinishRunwayLookupStage(query.Progress.Runways);
        var preliminary = facilities.CaptureCheckpoint();
        var finalQuery = new Query(); restored = EpisodeFacilityCoordinator.RestoreCheckpoint(preliminary, finalQuery);
        restored.RequestFinal(new[] { air, ground });
        finalQuery.Progress = new SceneryQuerySnapshot(new[] { new RunwayFacility { AirportIdent = "FIN", WidthMeters = 45, LengthMeters = 2500 } },
            new[] { new AirportFacility { Ident = "FIN" } }, true, false, 3, 1);
        var pendingFinal = restored.CaptureCheckpoint();
        restored = EpisodeFacilityCoordinator.RestoreCheckpoint(pendingFinal, new Query());
        restored.CompleteWithoutRefresh("generation_retired");
        var completed = restored.SnapshotTask!.GetAwaiter().GetResult();
        Check(completed.FinalRunways.Single().WidthMeters == 45 && !completed.FinalRunwayCandidatesComplete && completed.PreliminaryRunways.Count == 1,
            "Recovered final lookup lost its own evidence or borrowed the preliminary completion flag.");
        var completedBytes = restored.CaptureCheckpoint();
        restored = EpisodeFacilityCoordinator.RestoreCheckpoint(completedBytes, new Query());
        Check(restored.CaptureCheckpoint().SequenceEqual(completedBytes) && restored.SnapshotTask!.IsCompleted, "Completed facility promise became pending after recovery.");
        var frozen = restored.SnapshotTask!.Result; restored.ResetConnection();
        var disconnected = restored.CaptureCheckpoint();
        restored = EpisodeFacilityCoordinator.RestoreCheckpoint(disconnected, new Query());
        Check(restored.EpisodeId == 0 && restored.SnapshotTask!.Result.EpisodeId == 1 && restored.CaptureCheckpoint().SequenceEqual(disconnected),
            "Connection reset rewrote the identity of an already completed facility snapshot.");
        restored.BeginEpisode(2); restored.CompleteWithoutRefresh("new_context_unavailable");
        Check(restored.SnapshotTask!.Result.FinalRunways.Count == 0 && frozen.FinalRunways.Count == 1, "New context inherited or modified completed scenery.");
    }
    private static string Signature(LandingEpisodeEventArgs episode)
    {
        using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(episode.PersistentId.ToByteArray()); writer.Write(episode.EpisodeId); writer.Write(episode.IsDiagnosticOnly);
            writer.Write(episode.Simulator); writer.Write(episode.AircraftTitle); writer.Write(episode.AircraftType); writer.Write(episode.AircraftModel);
            RuntimeCheckpointCodec.WriteSamples(writer, episode.Samples); RuntimeCheckpointCodec.WriteJournal(writer, episode.DecisionJournal);
        }
        using var sha = System.Security.Cryptography.SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream.ToArray()));
    }
    private static TelemetrySample Frame(long sequence, double time, bool ground) => new TelemetrySample
    { Sequence = sequence, HostElapsedSeconds = time, SimulationTimeSeconds = time, SimulationDeltaSeconds = .1, MotionSimulation = true,
        OnGround = ground, AboveGroundLevelFeet = ground ? 0 : 100, VelocityWorldYFps = ground ? 0 : -3, GForce = 1, LatitudeDegrees = 42, LongitudeDegrees = 23 };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Malformed checkpoint was accepted."); }
    private sealed class Query : ISceneryQueryPort
    {
        public SceneryQuerySnapshot Progress = new SceneryQuerySnapshot(Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false, false, 0, 0);
        public bool CanQuery => true;
        public IReadOnlyList<AirportFacility> CachedAirports => Array.Empty<AirportFacility>();
        public SceneryQuerySnapshot Snapshot => Progress;
        public void Request(double latitude, double longitude) { }
        public void Reset() { Progress = new SceneryQuerySnapshot(Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false, false, 0, 0); }
        public void Cancel(string reason) { Reset(); }
    }
}
