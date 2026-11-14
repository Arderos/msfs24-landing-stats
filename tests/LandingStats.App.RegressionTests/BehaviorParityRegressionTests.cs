using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Archives;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class BehaviorParityRegressionTests
{
    public static void Recording()
    {
        foreach (var mode in new[] { "replay", "loading", "pause", "live" })
        foreach (var restore in new[] { false, true })
        foreach (var transitionLost in new[] { false, true })
        {
            var fixture = new RecordingFixture();
            fixture.Flow(new LifecycleMessage(SourceLifecycle.SimRunning, 1));
            if (!transitionLost) Block(fixture, mode);
            fixture.Gap();
            if (transitionLost) Block(fixture, mode, lost: true);
            if (restore) fixture.Restore();
            Check(fixture.Frame(1, false).SourceSample == null, "Frames were accepted before lifecycle resynchronization.");
            fixture.Feed(fixture.Lifecycle);
            fixture.Land();
            Check(fixture.Captures.Count(c => !c.LocalOnly) == (mode == "live" ? 1 : 0), "Gap changed " + mode + " capture policy.");
            Check(fixture.Captures.Count(c => c.AutomaticAllowed) == (mode == "live" ? 1 : 0), "Gap changed automatic capture eligibility.");
        }
        foreach (var mode in new[] { "replay", "loading", "pause" })
        {
            var fixture = new RecordingFixture(); Block(fixture, mode); fixture.Gap();
            fixture.Flow(mode == "pause" ? new LifecycleMessage(SourceLifecycle.PauseFlags, 0) :
                new LifecycleMessage(SourceLifecycle.Flow, mode == "replay" ? (int)SimulatorFlowEvent.REPLAY_END : (int)SimulatorFlowEvent.FLT_LOADED), lost: true);
            fixture.Feed(fixture.Lifecycle); fixture.Land();
            Check(fixture.Captures.Count(c => !c.LocalOnly) == 1, "A lost end transition left " + mode + " permanently blocked.");
        }
        foreach (var restore in new[] { false, true })
        foreach (var positionJump in new[] { false, true })
        {
            AxisBoundary(restore, positionJump);
            AxisBoundaryWithoutFreshEvent(restore, positionJump);
        }
    }
    private static void Block(RecordingFixture fixture, string mode, bool lost = false)
    {
        if (mode != "live") fixture.Flow(mode == "pause" ? new LifecycleMessage(SourceLifecycle.PauseFlags, 1) :
            new LifecycleMessage(SourceLifecycle.Flow, mode == "replay" ? (int)SimulatorFlowEvent.REPLAY_START : (int)SimulatorFlowEvent.FLT_LOAD), lost);
    }
    private static void AxisBoundary(bool restore, bool positionJump)
    {
        var fixture = new RecordingFixture(); fixture.Flow(new LifecycleMessage(SourceLifecycle.SimRunning, 1));
        fixture.Frame(100, false, axisAge: 0); fixture.Frame(100.1, false, axisAge: .1);
        var time = positionJump ? 100.2 : 1;
        var latitude = positionJump ? 43 : 42;
        fixture.Frame(time, false, axisAge: .2, latitude: latitude);
        if (restore) fixture.Restore();
        var stale = fixture.Frame(time + .1, false, axisAge: .3, latitude: latitude).SourceSample!;
        Check(!stale.AxisElevatorSetValid && stale.AxisElevatorSetPercent == 0 && stale.AxisElevatorSetAgeSeconds == 0,
            "Parent boundary must clear the preceding event with finite source-compatible placeholders.");
        var fresh = fixture.Frame(time + .2, false, axisAge: .01, latitude: latitude).SourceSample!;
        Check(fresh.AxisElevatorSetValid && fresh.AxisElevatorSetPercent == 50, "Boundary discarded a newly observed simulator event.");
        foreach (var sample in new[] { stale, fresh })
            Check(TelemetryCsv.TryParse(TelemetryCsv.Format(sample), out var exported) && exported.AxisElevatorSetValid == sample.AxisElevatorSetValid &&
                exported.AxisElevatorSetPercent == sample.AxisElevatorSetPercent && exported.AxisElevatorSetAgeSeconds == sample.AxisElevatorSetAgeSeconds,
                "CSV export changed the simulator event validity or age.");
        for (var i = 0; i <= 190; i++) fixture.Frame(time + .3 + i * .1, i >= 30, axisAge: .11 + i * .1, latitude: latitude);
        var episode = fixture.Captures.Single(c => !c.LocalOnly).Episode;
        var contact = TouchdownAnalysis.Analyze(episode.Samples).Single();
        var record = LandingRecordFactory.Create(contact, episode.Samples, "fixture", "fixture");
        var root = Path.Combine(Path.GetTempPath(), "axis-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(root); repository.Save(record);
            var loaded = repository.LoadDetail(repository.LoadAll().Single()) ?? throw new InvalidOperationException("Boundary history detail was not saved.");
            foreach (var sample in new[] { stale, fresh })
            {
                var relative = TelemetryAnalysisFrames.TimeOf(sample) - contact.EstimatedContactTimeSeconds;
                var point = loaded.Series.Single(p => Math.Abs(p.TimeSeconds - relative) < .0001);
                Check(point.AxisElevatorSetValid == sample.AxisElevatorSetValid && point.AxisElevatorSetPercent == sample.AxisElevatorSetPercent,
                    "History roundtrip changed the simulator event at the physical boundary.");
            }
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void AxisBoundaryWithoutFreshEvent(bool restore, bool positionJump)
    {
        var fixture = new RecordingFixture(); fixture.Flow(new LifecycleMessage(SourceLifecycle.SimRunning, 1));
        fixture.EnableRawCapture();
        fixture.Frame(100, false, axisAge: 0, agl: 1200);
        fixture.Frame(100.1, false, axisAge: .1, agl: 1200);
        var time = positionJump ? 100.2 : 1;
        var latitude = positionJump ? 43 : 42;
        fixture.Frame(time, false, axisAge: .2, latitude: latitude, agl: 1200);
        if (restore) fixture.Restore();
        // Hold the same source event beyond the pre-roll window, then land
        // without another axis event (for example, an autopilot approach).
        // The stale source cache must remain absent in every consumer.
        for (var i = 1; i <= 410; i++)
        {
            var ground = i >= 250;
            var effects = fixture.Frame(time + i * .1, ground, axisAge: .2 + i * .1,
                latitude: latitude, agl: i < 220 ? 1200 : 100);
            var sample = effects.SourceSample!;
            AssertAbsentAxis(sample);
            AssertAbsentAxis(TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(sample)));
            Check(effects.RawCapture.OfType<RawDebugSampleEventArgs>().Any(), "The stale-axis case did not emit RAW telemetry.");
            foreach (var raw in effects.RawCapture.OfType<RawDebugSampleEventArgs>()) AssertAbsentAxis(raw.Sample);
        }
        var episode = fixture.Captures.Single(c => !c.LocalOnly).Episode;
        Check(episode.Samples.Count > 150, "The stale-axis fixture did not retain an approach and rollout.");
        foreach (var sample in episode.Samples) AssertAbsentAxis(sample);
        var contact = TouchdownAnalysis.Analyze(episode.Samples).Single();
        var record = LandingRecordFactory.Create(contact, episode.Samples, "fixture", "fixture");
        var candidate = new BugReportCandidate(episode.EpisodeId, episode.Samples, "MSFS", "Axis boundary fixture", "fixture", "fixture",
            Array.Empty<string>(), new[] { record });
        var root = Path.Combine(Path.GetTempPath(), "axis-boundary-archives-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var zstd in new[] { false, true })
            {
                var reports = new BugReportRepository(Path.Combine(root, zstd ? "v2" : "v1"), () => zstd);
                var automatic = reports.TryCreateAutomatic(candidate, DateTime.UtcNow, long.MaxValue)!;
                var manual = reports.CreateManual(candidate, DateTime.UtcNow, Guid.NewGuid());
                foreach (var path in new[] { automatic, manual })
                {
                    using var input = File.OpenRead(path); using var archive = new CaptureArchiveReader(input);
                    Check(archive.Format == (zstd ? 2 : 1), "Boundary archive used the wrong negotiated format.");
                    using var csv = new StreamReader(archive.Open("telemetry.csv"));
                    Check(csv.ReadLine() == TelemetryCsv.Header, "Boundary archive changed the telemetry schema.");
                    var rows = 0;
                    for (var line = csv.ReadLine(); line != null; line = csv.ReadLine())
                    {
                        Check(TelemetryCsv.TryParse(line, out var sample), "Boundary archive contains an unreadable row.");
                        AssertAbsentAxis(sample); rows++;
                    }
                    Check(rows == episode.Samples.Count, "Boundary archive lost recorded samples.");
                }
                var target = Environment.GetEnvironmentVariable("MSFSLS_AXIS_BOUNDARY_FIXTURE");
                if (!restore && !positionJump && !zstd && !string.IsNullOrWhiteSpace(target)) File.Copy(automatic, target, true);
            }
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void AssertAbsentAxis(TelemetrySample sample) => Check(!sample.AxisElevatorSetValid &&
        sample.AxisElevatorSetPercent == 0 && sample.AxisElevatorSetAgeSeconds == 0,
        "An invalidated source event must remain absent with finite placeholders throughout recording and export.");
    private sealed class RecordingFixture
    {
        private readonly RuntimeClockEpoch _clock = new RuntimeClockEpoch(Guid.NewGuid(), 1000, 1000);
        private readonly RecoveryJournalIdentity _identity;
        private readonly WorkerIdentity _worker;
        private readonly ControllerInputPreparation _prepare;
        private RecordingRuntimeState _state;
        private long _sequence, _journal;
        public SimulatorLifecycleSnapshotMessage Lifecycle = new SimulatorLifecycleSnapshotMessage();
        public readonly List<RuntimeCapturedEpisode> Captures = new List<RuntimeCapturedEpisode>();
        public RecordingFixture()
        {
            _identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), _clock);
            _worker = new WorkerIdentity(_identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator);
            _state = new RecordingRuntimeState(_identity); _prepare = new ControllerInputPreparation(new ControllerJoiner(_clock));
            var policy = new RuntimeInput(Guid.NewGuid(), JournalRecordKind.RuntimeCommand, 1000, 1000, DateTime.UtcNow,
                new RecordingCommand(RecordingCommandKind.DeliveryPolicy, true, 1).Encode());
            _state.Apply(new DurableRuntimeInput(policy, new JournalCursor(1, ++_journal)));
            Feed(new WorkerHello("fixture", new string('a', 64), 1, _clock.Id, _clock.Frequency));
            Flow(new LifecycleMessage(SourceLifecycle.Connected));
        }
        public void Flow(LifecycleMessage message, bool lost = false)
        { Lifecycle = Lifecycle.Observe(message); if (!lost) Feed(message); }
        public void Restore() => _state = RecordingRuntimeState.RestoreCheckpoint(_state.CaptureCheckpoint(), _identity);
        public void EnableRawCapture()
        {
            var input = new RuntimeInput(Guid.NewGuid(), JournalRecordKind.RuntimeCommand, 1000, 1000, DateTime.UtcNow,
                new RecordingCommand(RecordingCommandKind.RawCapture, true).Encode());
            _state.Apply(new DurableRuntimeInput(input, new JournalCursor(1, ++_journal)));
        }
        public void Gap() => Feed(new SourceGapMessage(SourceGapReason.BufferOverflow, _sequence + 1, _sequence + 2, true));
        public RecordingRuntimeEffects Feed(RuntimePayload payload)
        {
            if (payload is SourceGapMessage gap) _sequence = gap.LastMissing;
            var message = new RuntimeEnvelope(_worker, payload is WorkerHello ? 0 : 1, ++_sequence, 1000 + _sequence * 100, payload);
            var input = new RuntimeInput(Guid.NewGuid(), JournalRecordKind.SourceMessage, message.CaptureQpc, message.CaptureQpc,
                new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc), RuntimeWire.Encode(message));
            var effects = _state.Apply(new DurableRuntimeInput(_prepare.Prepare(input), new JournalCursor(1, ++_journal)));
            Check(effects.QuarantineReason == null, effects.QuarantineReason ?? ""); Captures.AddRange(effects.Captures); return effects;
        }
        public RecordingRuntimeEffects Frame(double time, bool ground, double axisAge = double.NaN, double latitude = 42, double agl = 100) => Feed(new TelemetryFrameMessage(new TelemetrySample
        {
            SimulationTimeSeconds = time, SimulationDeltaSeconds = .1, MotionSimulation = true, OnGround = ground,
            AboveGroundLevelFeet = ground ? 0 : agl, VelocityWorldYFps = ground ? 0 : -3, VerticalSpeedFps = ground ? 0 : -3,
            TouchdownNormalVelocityFps = ground ? 3 : 0, GForce = 1, GroundSpeedKnots = 100, IndicatedAirspeedKnots = 100,
            PlaneAltitudeFeet = 1000, GroundAltitudeFeet = 900, LatitudeDegrees = latitude, LongitudeDegrees = 23,
            AxisElevatorSetValid = !double.IsNaN(axisAge), AxisElevatorSetPercent = 50, AxisElevatorSetAgeSeconds = axisAge
        }));
        public void Land() { for (var i = 0; i <= 210; i++) Frame(2 + i * .1, i >= 50); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
