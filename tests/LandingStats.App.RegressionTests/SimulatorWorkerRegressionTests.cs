using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class SimulatorWorkerRegressionTests
{
    public static void RunScenery()
    {
        var worker = Identity(); var commands = new List<SceneryCommandMessage>(); var results = new List<IReadOnlyList<RunwayFacility>>();
        var context = new SceneryLookupContext(Guid.NewGuid(), 1, Guid.NewGuid(), LookupStage.Preliminary);
        RemoteSceneryQueryPort Make() => new RemoteSceneryQueryPort(commands.Add, () => context, results.Add);
        var port = Make(); port.Connect(worker, 1); port.Request(42, 23);
        Check(commands.Count == 1 && commands[0].Lookup == context.Lookup, "Lookup did not retain its caller's durable identity.");
        var request = commands[0];
        var snapshot = new SceneryQuerySnapshot(new[] { new RunwayFacility { AirportIdent = "PRE", WidthMeters = 30 } },
            Enumerable.Range(0, 1500).Select(i => new AirportFacility { Ident = "A" + i, LatitudeDegrees = 42, LongitudeDegrees = 23 }).ToArray(), true, false, 3, 1);
        var bytes = Encode(snapshot); var transfer = Guid.NewGuid(); var chunks = Chunks(bytes, false, transfer).ToArray();
        Check(chunks.Length > 1, "Fixture must cross transfer boundaries.");
        RuntimeEnvelope Wrap(SceneryChunkMessage chunk, WorkerIdentity? identity = null, long connection = 1, Guid? lookup = null, LookupStage stage = LookupStage.Preliminary) =>
            new RuntimeEnvelope(identity ?? worker, connection, 1, 1, chunk, episodeId: context.Episode, lookupId: lookup ?? context.Lookup, lookupStage: stage);
        Check(!port.Accept(Wrap(chunks[0], Identity())) && !port.Accept(Wrap(chunks[0], connection: 2)) &&
            !port.Accept(Wrap(chunks[0], lookup: Guid.NewGuid())) && !port.Accept(Wrap(chunks[0], stage: LookupStage.Final)), "Stale scenery crossed a context boundary.");
        port.Accept(Wrap(chunks[0]));
        Check(port.Snapshot.Airports.Count == 0 && results.Count == 0, "Partial transfer was published as complete geometry.");
        var checkpoint = port.CaptureCheckpoint(); port = Make(); port.RestoreCheckpoint(checkpoint);
        Check(port.CaptureCheckpoint().SequenceEqual(checkpoint), "Partial scenery transfer changed on recovery.");
        Check(commands.Count == 1 && port.CaptureCheckpoint().SequenceEqual(checkpoint),
            "Restoring durable source progress must not reissue an already dispatched lookup.");
        foreach (var chunk in chunks.Skip(1)) port.Accept(Wrap(chunk));
        Check(port.Snapshot.Runways.Single().WidthMeters == 30 && port.Snapshot.Airports.Count == 1500 && results.Count == 0,
            "Accepted preliminary progress was lost or prematurely finalized.");
        port.Retire(worker); Check(!port.CanQuery && port.Snapshot.Runways.Count == 1, "Retirement discarded previously accepted evidence.");
        foreach (var chunk in Chunks(bytes, true, Guid.NewGuid())) Check(!port.Accept(Wrap(chunk)), "Retired source completed a lookup.");
        Check(results.Count == 0, "Retirement fabricated a completed response.");
        port.Reset(); worker = new WorkerIdentity(worker.AppSessionId, Guid.NewGuid(), 2, ComponentRole.Simulator); port.Connect(worker, 1);
        context = new SceneryLookupContext(context.Episode, 1, Guid.NewGuid(), LookupStage.Final); port.Request(42, 23);
        foreach (var chunk in Chunks(bytes, true, Guid.NewGuid())) port.Accept(Wrap(chunk, stage: LookupStage.Final));
        Check(results.Count == 1 && port.Snapshot.FreshAirportsReceived && !port.Snapshot.CandidatesComplete, "Completion fabricated candidate completeness.");
        var finished = port.CaptureCheckpoint(); port = Make(); port.RestoreCheckpoint(finished);
        Check(port.CaptureCheckpoint().SequenceEqual(finished), "Completed scenery state changed on recovery.");
        foreach (var chunk in Chunks(bytes, true, Guid.NewGuid())) Check(!port.Accept(Wrap(chunk, stage: LookupStage.Final)), "Completed lookup was overwritten.");
        context = new SceneryLookupContext(Guid.NewGuid(), 2, Guid.NewGuid(), LookupStage.Preliminary); port.Request(42, 23);
        var corrupted = (byte[])bytes.Clone(); corrupted[corrupted.Length - 1] ^= 1;
        var invalid = Chunks(corrupted, true, Guid.NewGuid(), SHA256.Create().ComputeHash(bytes)).ToArray();
        Reject(() => { foreach (var chunk in invalid) port.Accept(Wrap(chunk)); });
        Check(port.Snapshot.Runways.Count == 0 && results.Count == 1, "A corrupt transfer replaced accepted scenery.");
        port.Reset(); port.Request(42, 23);
        Reject(() => port.Accept(Wrap(chunks[1])));
        port.Reset(); port.Request(42, 23); port.Accept(Wrap(chunks[0]));
        Reject(() => port.Accept(Wrap(Chunks(bytes, false, Guid.NewGuid()).First())));
        port.Reset();
        Check(commands.Last().Cancel, "Cancelling the current context did not send a correlated cancellation.");
        for (var cut = 0; cut < checkpoint.Length; cut += 997) Reject(() => Make().RestoreCheckpoint(checkpoint.Take(cut).ToArray()));
        Reject(() => Make().RestoreCheckpoint(checkpoint.Concat(new byte[] { 0 }).ToArray()));
        var commandRoundTrip = RuntimeWire.Decode(RuntimeWire.Encode(new RuntimeEnvelope(worker, 0, 1, 1, request, Guid.NewGuid(), 1)));
        Check(((SceneryCommandMessage)commandRoundTrip.Payload).EpisodeOrdinal == 1, "Scenery command changed on the wire.");
        var failedPort = new RemoteSceneryQueryPort(_ => { }, () => context, _ => { }); failedPort.Connect(worker, 1);
        var facilities = new EpisodeFacilityCoordinator(failedPort); facilities.BeginEpisode(1);
        var approach = new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true, OnGround = false, LatitudeDegrees = 42, LongitudeDegrees = 23 };
        facilities.RequestPreliminary(approach, new[] { approach }); facilities.FailCurrentLookupStage("Preliminary request rejected.");
        Check(!facilities.SnapshotTask!.IsCompleted, "Preliminary failure prematurely settled the final episode facilities.");
        var contact = approach.CopyForAnalysis(); contact.Sequence = 2; contact.SimulationTimeSeconds = 1.1; contact.OnGround = true;
        context = new SceneryLookupContext(context.Episode, 1, Guid.NewGuid(), LookupStage.Final);
        facilities.RequestFinal(new[] { approach, contact }); facilities.FailCurrentLookupStage("Final request rejected.");
        Check(facilities.SnapshotTask.IsCompleted && !facilities.SnapshotTask.Result.FinalLookupCompleted,
            "Final failure did not resolve the episode or invented a successful lookup.");
    }
    public static void RunOwner() => Task.Run(Owner).GetAwaiter().GetResult();
    public static void RunNativeProcess() => Task.Run(async () =>
    {
        var identity = Identity(); var clock = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        var received = new ConcurrentQueue<RuntimeEnvelope>(); ProcessWorkerHost? host = null;
        using (host = new ProcessWorkerHost(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), identity, clock, async (message, _) =>
        { received.Enqueue(message); await host!.AcknowledgeAsync(message.Sequence).ConfigureAwait(false); }))
        {
            await host.StartAsync(TimeSpan.FromSeconds(10));
            await Until(() => host.LastHealth != null && host.LastHealth.OwnerProgress >= 3, "Native source owner did not continue after its connection attempt.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var probe = await host.CommandAsync(new WorkerCommandMessage(WorkerCommand.Probe), deadline.Token);
            Check(probe.Outcome == CommandOutcome.Completed, "Native source owner did not answer its probe.");
            Check(received.Any(item => item.Payload is SourceStatusMessage) && received.All(item => !(item.Payload is ControllerSnapshotMessage)),
                "Native simulator role did not report status or mixed another source into its output.");
            await host.StopAsync(TimeSpan.FromSeconds(3)); await host.SourceCompletion;
            Check(host.Exit.IsCompleted, "Native simulator generation survived its owner stop.");
        }
    }).GetAwaiter().GetResult();
    private static async Task Owner()
    {
        var identity = Identity(); var epoch = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        var transport = new Transport(identity); FakeSource? source = null; using var cancel = new CancellationTokenSource();
        var role = new SimulatorWorkerRole((window, capture, scenery) => source = new FakeSource(capture, scenery));
        var run = role.RunAsync(new WorkerExecutionContext(identity, epoch, transport, cancel.Token));
        await Until(() => transport.Messages.Any(item => item.Payload is TelemetryFrameMessage), "Source owner did not publish telemetry.");
        var lookup = new SceneryCommandMessage(1, Guid.NewGuid(), 1, Guid.NewGuid(), LookupStage.Preliminary, 42, 23,
            witnessQpc: transport.Messages.Last(item => item.Payload is TelemetryFrameMessage).CaptureQpc);
        transport.Command(new SceneryCommandMessage(2, lookup.Episode, 1, Guid.NewGuid(), lookup.Stage, 42, 23));
        transport.Command(new WorkerCommandMessage(WorkerCommand.FullTelemetry, true));
        transport.Command(new WorkerCommandMessage(WorkerCommand.RawCapture, true));
        transport.Command(lookup);
        transport.Command(lookup); // Lost command ACK must not repeat a native query.
        transport.Command(new SceneryCommandMessage(1, lookup.Episode, 1, lookup.Lookup, lookup.Stage, 43, 23, witnessQpc: lookup.WitnessQpc));
        await Until(() => transport.Messages.Any(item => item.Payload is SceneryChunkMessage), "Source did not answer the lookup.");
        var received = new List<IReadOnlyList<RunwayFacility>>();
        var port = new RemoteSceneryQueryPort(_ => { }, () => new SceneryLookupContext(lookup.Episode, 1, lookup.Lookup, lookup.Stage), received.Add);
        port.Connect(identity, 1); port.Request(42, 23);
        foreach (var message in transport.Messages.Where(item => item.Payload is SceneryChunkMessage)) port.Accept(message);
        Check(received.Count == 1 && received[0].Single().WidthMeters == 45, "Worker scenery did not survive the transport contract.");
        source!.PendingFlow = (int)SimulatorFlowEvent.FLT_LOAD;
        transport.Command(new WorkerCommandMessage(WorkerCommand.Metadata));
        await Until(() => transport.Messages.Any(item => item.Payload is LifecycleMessage life && life.State == SourceLifecycle.Flow), "Fixture did not replace the flight context.");
        transport.Command(lookup);
        transport.Command(new SceneryCommandMessage(1, lookup.Episode, 1, Guid.NewGuid(), lookup.Stage, 42, 23,
            witnessQpc: Stopwatch.GetTimestamp(), resumeOnly: true));
        transport.Command(new WorkerCommandMessage(WorkerCommand.Probe));
        transport.Command(new WorkerCommandMessage(WorkerCommand.Stop));
        await run;
        Check(source != null && source.Disposed && source.ThreadIds.Distinct().Count() == 1 && source.QueryCalls == 1 && source.CapturePinned && source.RawEnabled,
            "SDK operations crossed owners, stale connection was queried, or desired capture mode was not applied.");
        Check(transport.Messages.Any(item => item.Payload is CommandResultMessage result && result.Reason == "stale_connection"), "Stale command was not explicitly rejected.");
        Check(transport.Messages.Any(item => item.Payload is CommandResultMessage result && result.Reason == "lookup_exists") &&
            transport.Messages.Any(item => item.Payload is CommandResultMessage changed && changed.Reason == "lookup_id_reused"), "Lookup retry duplicated work or changed its coordinates.");
        Check(transport.Messages.Any(item => item.Payload is CommandResultMessage result && result.Reason == "stale_flight_context") &&
            transport.Messages.Any(item => item.Payload is CommandResultMessage missing && missing.Reason == "lookup_missing"),
            "A delayed or recovered lookup queried a different flight.");
        Check(transport.Messages.Any(item => item.Payload is CommandResultMessage result && result.Reason == "owner_observed"), "Probe did not reach the owner.");
        Check(!transport.Messages.Any(item => item.Payload is GuardFrameMessage) && transport.Messages.Any(item => item.Payload is AircraftMessage) &&
            transport.Messages.Any(item => item.Payload is LifecycleMessage life && life.State == SourceLifecycle.PauseFlags), "Acquisition omitted typed source events.");
        Check(transport.Messages.Count(item => item.Payload is LifecycleMessage life && life.State == SourceLifecycle.PhysicsReady) == 1,
            "Guard/full physics must announce readiness once without streaming guard frames.");
        Check(transport.Messages.Where(item => item.Payload is TelemetryFrameMessage).All(item => ((TelemetryFrameMessage)item.Payload).CopySample().RawControllerAxesValid.All(value => !value)),
            "Simulator worker fabricated physical controller input.");
        using var overflowCancellation = new CancellationTokenSource();
        var failed = new Transport(Identity()) { Overflow = true }; var created = false;
        try
        {
            await new SimulatorWorkerRole((window, capture, scenery) => { created = true; return new FakeSource(capture, scenery); })
                .RunAsync(new WorkerExecutionContext(failed.Identity, epoch, failed, overflowCancellation.Token));
            throw new InvalidOperationException("Simulator owner ignored failed output.");
        }
        catch (IOException) { Check(created, "Owner factory was not exercised."); }
    }
    private static byte[] Encode(SceneryQuerySnapshot snapshot)
    { using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory); RuntimeCheckpointCodec.WriteJson(writer, SceneryCheckpointData.Capture(snapshot)); return memory.ToArray(); }
    private static IEnumerable<SceneryChunkMessage> Chunks(byte[] bytes, bool completed, Guid transfer, byte[]? expectedHash = null)
    {
        using var sha = SHA256.Create(); var hash = expectedHash ?? sha.ComputeHash(bytes);
        for (var offset = 0; offset < bytes.Length; offset += SceneryChunkMessage.ChunkBytes)
            yield return new SceneryChunkMessage(transfer, offset / SceneryChunkMessage.ChunkBytes, bytes.Length, completed, hash,
                bytes.Skip(offset).Take(Math.Min(SceneryChunkMessage.ChunkBytes, bytes.Length - offset)).ToArray());
    }
    private static WorkerIdentity Identity() => new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Simulator);
    private static async Task Until(Func<bool> condition, string message)
    { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(message); await Task.Delay(10); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Malformed scenery was accepted."); }
    private sealed class Transport : IWorkerTransport
    {
        public Transport(WorkerIdentity identity) { Identity = identity; }
        public readonly WorkerIdentity Identity;
        public readonly ConcurrentQueue<RuntimeEnvelope> Messages = new ConcurrentQueue<RuntimeEnvelope>();
        private readonly ConcurrentQueue<RuntimeEnvelope> _commands = new ConcurrentQueue<RuntimeEnvelope>();
        private long _sequence, _command;
        public bool Overflow;
        public int PendingCount => 0;
        public void Command(RuntimeCommand command) { var revision = Interlocked.Increment(ref _command); _commands.Enqueue(new RuntimeEnvelope(Identity, 0, revision, Stopwatch.GetTimestamp(), command, Guid.NewGuid(), revision)); }
        public bool TryTakeCommand(out RuntimeEnvelope? command) => _commands.TryDequeue(out command);
        public bool TryPublish(RuntimePayload payload, long connectionGeneration, long captureQpc, Guid correlationId = default, long revision = 0, Guid episodeId = default, Guid lookupId = default, LookupStage stage = LookupStage.None)
        {
            if (Overflow) return false;
            Messages.Enqueue(RuntimeWire.Decode(RuntimeWire.Encode(new RuntimeEnvelope(Identity, connectionGeneration, Interlocked.Increment(ref _sequence), captureQpc,
                payload, correlationId, revision, episodeId, lookupId, stage)))); return true;
        }
    }
    private sealed class FakeSource : ISimulatorSource, ISceneryQueryPort
    {
        private readonly ISimulatorCaptureContext _capture;
        private readonly ISceneryQueryObserver _scenery;
        public FakeSource(ISimulatorCaptureContext capture, ISceneryQueryObserver scenery) { _capture = capture; _scenery = scenery; Observe(); }
        public readonly List<int> ThreadIds = new List<int>();
        public bool Disposed, CapturePinned, RawEnabled;
        public int QueryCalls;
        private void Observe() => ThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
        public bool IsConnected { get; private set; }
        public long SceneryRevision { get; private set; }
        public ISceneryQueryPort SceneryQuery => this;
        public event Action<string>? Connected;
        public event Action<SourceDisconnectionReason>? Disconnected;
        public event Action<TelemetrySample>? SampleReceived;
        public event Action<GuardFrameMessage>? GuardReceived;
        public event Action<ReplaySourceObservation>? SourceObservationReceived;
        public event Action<string, string, string>? MetadataReceived;
        public event Action<SourceLifecycle, int>? LifecycleReceived;
        public event EventHandler? SimulatorExited;
        public event EventHandler<RecorderStatusEventArgs>? StatusChanged;
        public void Start()
        {
            Observe(); IsConnected = true; Connected?.Invoke("fixture"); MetadataReceived?.Invoke("Aircraft", "type", "model");
            StatusChanged?.Invoke(this, new RecorderStatusEventArgs(RecorderState.Connected, "Recorder.ConnectedGate"));
            LifecycleReceived?.Invoke(SourceLifecycle.PauseFlags, 0);
            SourceObservationReceived?.Invoke(new ReplaySourceObservation(SourceObservationAvailability.Pending, SourceObservationReason.AwaitingReply));
            GuardReceived?.Invoke(new GuardFrameMessage(1, 100, false, -3, true));
            SampleReceived?.Invoke(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1, MotionSimulation = true });
        }
        public IntPtr HandleWindowMessage(int message, ref bool handled) { Observe(); return IntPtr.Zero; }
        public void RefreshTelemetryMode() { Observe(); CapturePinned = _capture.IsRecording; RawEnabled = _capture.RawDebugEnabled; }
        public int PendingFlow;
        public void RefreshMetadata()
        { Observe(); var flow = Interlocked.Exchange(ref PendingFlow, 0); if (flow != 0) LifecycleReceived?.Invoke(SourceLifecycle.Flow, flow); }
        public void Dispose() { Observe(); Disposed = true; IsConnected = false; Disconnected?.Invoke(SourceDisconnectionReason.ConnectionClosed); SimulatorExited?.Invoke(this, EventArgs.Empty); }
        public bool CanQuery => IsConnected;
        public IReadOnlyList<AirportFacility> CachedAirports => Array.Empty<AirportFacility>();
        public SceneryQuerySnapshot Snapshot => new SceneryQuerySnapshot(new[] { new RunwayFacility { AirportIdent = "TEST", WidthMeters = 45 } }, Array.Empty<AirportFacility>(), true, true, 1, 1);
        public void Request(double latitude, double longitude) { Observe(); QueryCalls++; SceneryRevision++; _scenery.FinishRunwayLookupStage(Snapshot.Runways); }
        public void Reset() { Observe(); SceneryRevision++; }
        public void Cancel(string reason) { Observe(); }
    }
}
