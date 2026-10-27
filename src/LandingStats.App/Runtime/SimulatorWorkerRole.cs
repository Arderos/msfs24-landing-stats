using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class SimulatorWorkerRole : IWorkerRole
{
    private readonly Func<IntPtr, ISimulatorCaptureContext, ISceneryQueryObserver, ISimulatorSource> _create;
    public SimulatorWorkerRole() : this((window, capture, scenery) => new SimConnectSource(window, capture, scenery)) { }
    internal SimulatorWorkerRole(Func<IntPtr, ISimulatorCaptureContext, ISceneryQueryObserver, ISimulatorSource> create) { _create = create; }
    public Task RunAsync(WorkerExecutionContext context)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Run(context); completion.TrySetResult(true); }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { completion.TrySetCanceled(); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "SimConnect source owner" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private void Run(WorkerExecutionContext context)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var owner = new WorkerOwner(context); var capture = new CaptureContext(); var scenery = new SceneryObserver(capture, context);
        Exception? failure = null; ISimulatorSource? source = null; HwndSource? window = null; long connection = 0, useful = 0, previousUseful = 0;
        var simulator = "MSFS";
        using var profiles = new MsfsControlProfileMonitor();
        var physicsAnnounced = false;
        void Physics(double time, bool motion)
        {
            if (physicsAnnounced || !motion || double.IsNaN(time) || double.IsInfinity(time)) return;
            physicsAnnounced = true; Publish(new LifecycleMessage(SourceLifecycle.PhysicsReady));
        }
        void Publish(RuntimePayload payload)
        { owner.Publish(payload, Stopwatch.GetTimestamp(), connection); if (!(payload is SourceStatusMessage) && !(payload is SourceDiagnosticMessage)) useful++; }
        void FlushDiagnostics()
        {
            var diagnostics = capture.TakeDiagnostics();
            foreach (var entry in diagnostics.Entries)
                Publish(new SourceDiagnosticMessage(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, entry.Details));
            if (diagnostics.DroppedEntryCount != 0)
                Publish(new SourceDiagnosticMessage(capture.LastSimulationTime, "source", "diagnostics_limited", "Source diagnostics reached their bounded buffer.",
                    new Dictionary<string, double> { ["dropped"] = diagnostics.DroppedEntryCount }));
        }
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        DispatcherUnhandledExceptionEventHandler failed = (_, args) =>
        { failure = args.Exception; args.Handled = true; dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
        dispatcher.UnhandledException += failed;
        try
        {
            // A message-only HWND belongs to this owner. No application window,
            // coordinator, controller API or presentation dispatcher is created.
            window = new HwndSource(new HwndSourceParameters("Landing Stats simulator source")
                { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
            source = _create(window.Handle, capture, scenery); scenery.Source = source;
            window.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) => source.HandleWindowMessage(message, ref handled));
            source.Connected += name =>
            {
                connection++; profiles.Reset(); physicsAnnounced = false; simulator = OptionalMetadataText.Sanitize(name, 128, "MSFS"); scenery.Connection = connection;
                scenery.FlightBoundary();
                Publish(new LifecycleMessage(SourceLifecycle.Connected));
                Publish(new AircraftMessage(simulator, "Unknown aircraft", "unknown", "unknown"));
            };
            source.Disconnected += reason => { profiles.Reset(); FlushDiagnostics(); scenery.FlushDiagnostics(); Publish(new LifecycleMessage(SourceLifecycle.Disconnected, (int)reason)); scenery.Retire(); };
            source.SimulatorExited += (_, _) => Publish(new LifecycleMessage(SourceLifecycle.SimulatorQuit));
            source.MetadataReceived += (title, type, model) => Publish(new AircraftMessage(simulator,
                OptionalMetadataText.Sanitize(title, 512, "Unknown aircraft"), OptionalMetadataText.Sanitize(type, 256, "unknown"), OptionalMetadataText.Sanitize(model, 256, "unknown")));
            if (source is IAircraftConfigurationSource aircraftSource)
                aircraftSource.AircraftConfigurationReceived += (path, category) =>
                {
                    Publish(new AircraftControlsMessage(path, category, Array.Empty<ControllerBinding>()));
                    profiles.Update(simulator, path, category);
                };
            source.SampleReceived += sample => { FlushDiagnostics(); Physics(sample.SimulationTimeSeconds, sample.MotionSimulation); capture.LastSimulationTime = sample.SimulationTimeSeconds; Publish(new TelemetryFrameMessage(sample)); };
            // Guard is acquisition-local: the recording reducer never consumes it.
            source.GuardReceived += sample => { Physics(sample.SimulationTime, sample.MotionSimulation); capture.LastSimulationTime = sample.SimulationTime; useful++; };
            source.SourceObservationReceived += observation => Publish(new SourceObservationMessage(observation));
            source.LifecycleReceived += (kind, value) =>
            {
                FlushDiagnostics();
                if (kind == SourceLifecycle.Flow && IsReplacementStart((SimulatorFlowEvent)value)) { scenery.FlightBoundary(); profiles.Reset(); }
                Publish(new LifecycleMessage(kind, value));
                if (kind == SourceLifecycle.Flow && IsReplacementStart((SimulatorFlowEvent)value))
                    Publish(new AircraftMessage(simulator, "Unknown aircraft", "unknown", "unknown"));
            };
            source.StatusChanged += (_, args) => Publish(new SourceStatusMessage((byte)args.State, args.MessageKey,
                args.MessageArguments.Select(value => Bound(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)).ToArray()));
            CommandResultMessage Command(RuntimeEnvelope message)
            {
                if (message.Payload is SceneryCommandMessage lookup) return scenery.Command(lookup);
                var command = (WorkerCommandMessage)message.Payload;
                switch (command.Command)
                {
                    case WorkerCommand.RawCapture: capture.RawDebugEnabled = command.Enabled; source.RefreshTelemetryMode(); break;
                    case WorkerCommand.FullTelemetry: capture.IsRecording = command.Enabled; source.RefreshTelemetryMode(); break;
                    case WorkerCommand.Metadata: source.RefreshMetadata(); break;
                    default: return new CommandResultMessage(CommandOutcome.Rejected, "unsupported_simulator_command");
                }
                return new CommandResultMessage(CommandOutcome.Completed, "applied");
            }
            timer.Tick += (_, _) =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                FlushDiagnostics();
                scenery.Pump();
                if (profiles.TryTake(out var profile))
                {
                    Publish(profile!);
                    foreach (var diagnostic in profiles.Diagnostics)
                        Publish(new SourceDiagnosticMessage(capture.LastSimulationTime, "source.controls", "profile_resolution",
                            OptionalMetadataText.Sanitize(diagnostic, 1024, "Profile resolution unavailable"), new Dictionary<string, double>()));
                }
                owner.Progress(useful != previousUseful, source.IsConnected ? DependencyWait.None : DependencyWait.Simulator, Command);
                previousUseful = useful;
                if (owner.Stopping) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            owner.Progress(false, DependencyWait.Simulator, Command);
            if (!owner.Stopping)
            {
                timer.Start(); source.Start();
                using (context.CancellationToken.Register(() => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send))) Dispatcher.Run();
            }
        }
        finally
        {
            timer.Stop();
            // The task completes only after SDK close on the same owner. If close
            // hangs, the parent retains ownership until it kills the process job.
            try { source?.Dispose(); }
            finally { window?.Dispose(); dispatcher.UnhandledException -= failed; if (!dispatcher.HasShutdownFinished) dispatcher.InvokeShutdown(); }
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        context.CancellationToken.ThrowIfCancellationRequested();
    }
    private static string Bound(string value) => OptionalMetadataText.Sanitize(value, 512, "unknown");
    internal static bool IsReplacementStart(SimulatorFlowEvent flow) => flow == SimulatorFlowEvent.FLT_LOAD ||
        flow == SimulatorFlowEvent.TELEPORT_START || flow == SimulatorFlowEvent.BACK_ON_TRACK_START || flow == SimulatorFlowEvent.SKIP_START ||
        flow == SimulatorFlowEvent.RTC_START || flow == SimulatorFlowEvent.REPLAY_START || flow == SimulatorFlowEvent.BACK_TO_MAIN_MENU ||
        flow == SimulatorFlowEvent.FLIGHT_END || flow == SimulatorFlowEvent.PLANE_CRASH;
    private sealed class CaptureContext : ISimulatorCaptureContext
    {
        public bool RawDebugEnabled { get; set; }
        public bool IsRecording { get; set; }
        public bool HasContact => false;
        public TelemetryDecisionJournal DecisionJournal { get; private set; } = new TelemetryDecisionJournal();
        public double LastSimulationTime { get; set; }
        public TelemetryDecisionSnapshot TakeDiagnostics()
        { var result = DecisionJournal.Snapshot(); DecisionJournal = new TelemetryDecisionJournal(); return result; }
    }
    private sealed class SceneryObserver : ISceneryQueryObserver
    {
        private readonly CaptureContext _capture;
        private readonly WorkerExecutionContext _context;
        private SceneryCommandMessage? _request;
        private byte[]? _transfer, _hash;
        private Guid _transferId;
        private int _offset;
        private long _publishedRevision = -1;
        private long _flightBoundaryQpc;
        private long _lastSnapshotQpc;
        private TelemetryDecisionJournal _diagnostics = new TelemetryDecisionJournal();
        private bool _complete, _publishedComplete;
        public SceneryObserver(CaptureContext capture, WorkerExecutionContext context) { _capture = capture; _context = context; }
        public ISimulatorSource Source { get; set; } = null!;
        public long Connection { get; set; }
        public long EpisodeId => _request?.EpisodeOrdinal ?? 0;
        public bool IsPreliminary => _request?.Stage == LookupStage.Preliminary;
        public TelemetryDecisionJournal DecisionJournal => _diagnostics;
        public double DecisionTime => _capture.LastSimulationTime;
        public void FinishRunwayLookupStage(IReadOnlyList<RunwayFacility> facilities) { _complete = true; }
        public void ResetConnection() => Retire();
        public void Retire() { FlushDiagnostics(); _request = null; _transfer = null; _hash = null; _complete = _publishedComplete = false; _publishedRevision = -1; }
        public void FlushDiagnostics()
        {
            var snapshot = _diagnostics.Snapshot(); _diagnostics = new TelemetryDecisionJournal();
            if (_request == null) return;
            foreach (var entry in snapshot.Entries)
                if (!_context.Transport.TryPublish(new SourceDiagnosticMessage(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, entry.Details),
                    Connection, Stopwatch.GetTimestamp(), episodeId: _request.Episode, lookupId: _request.Lookup, stage: _request.Stage))
                    throw new IOException("Scenery diagnostic output capacity was exhausted.");
        }
        public void FlightBoundary()
        {
            _flightBoundaryQpc = Stopwatch.GetTimestamp();
            if (_request != null) Source.SceneryQuery.Cancel("Simulator flight context changed.");
            Retire();
        }
        public CommandResultMessage Command(SceneryCommandMessage request)
        {
            if (!Source.IsConnected || request.Connection != Connection) return new CommandResultMessage(CommandOutcome.Rejected, "stale_connection");
            if (request.Cancel)
            {
                if (_request?.Lookup != request.Lookup || _request.Episode != request.Episode || _request.Stage != request.Stage)
                    return new CommandResultMessage(CommandOutcome.Rejected, "stale_lookup");
                Source.SceneryQuery.Cancel("Runtime cancelled this lookup."); Retire();
                return new CommandResultMessage(CommandOutcome.Cancelled, "lookup_cancelled");
            }
            if (request.WitnessQpc < _flightBoundaryQpc || request.WitnessQpc > Stopwatch.GetTimestamp())
                return new CommandResultMessage(CommandOutcome.Rejected, "stale_flight_context");
            if (_request?.Lookup == request.Lookup)
            {
                if (_request.Episode != request.Episode || _request.EpisodeOrdinal != request.EpisodeOrdinal || _request.Stage != request.Stage ||
                    _request.Latitude != request.Latitude || _request.Longitude != request.Longitude || _request.WitnessQpc != request.WitnessQpc)
                    return new CommandResultMessage(CommandOutcome.Rejected, "lookup_id_reused");
                return new CommandResultMessage(CommandOutcome.Completed, "lookup_exists");
            }
            if (request.ResumeOnly) return new CommandResultMessage(CommandOutcome.Rejected, "lookup_missing");
            Source.SceneryQuery.Cancel("A new runtime lookup superseded the old context."); Retire();
            _request = request; Source.SceneryQuery.Reset(); Source.SceneryQuery.Request(request.Latitude, request.Longitude);
            return new CommandResultMessage(CommandOutcome.Completed, "lookup_started");
        }
        public void Pump()
        {
            if (_request == null) return;
            FlushDiagnostics();
            var now = Stopwatch.GetTimestamp();
            if (_transfer == null && (_publishedRevision != Source.SceneryRevision || _publishedComplete != _complete) &&
                (_publishedRevision < 0 || _complete != _publishedComplete || now - _lastSnapshotQpc >= Stopwatch.Frequency / 2))
            {
                _lastSnapshotQpc = now;
                using var memory = new MemoryStream(); using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
                    RuntimeCheckpointCodec.WriteJson(writer, SceneryCheckpointData.Capture(Source.SceneryQuery.Snapshot));
                _transfer = memory.ToArray(); using var sha = SHA256.Create(); _hash = sha.ComputeHash(_transfer);
                _transferId = Guid.NewGuid(); _offset = 0; _publishedRevision = Source.SceneryRevision; _publishedComplete = _complete;
            }
            // Reserve room for flight frames and control results. A large airport
            // list cannot fill the unacknowledged buffer in one owner iteration.
            while (_transfer != null && _context.Transport.PendingCount < 24)
            {
                var part = new byte[Math.Min(SceneryChunkMessage.ChunkBytes, _transfer.Length - _offset)]; Array.Copy(_transfer, _offset, part, 0, part.Length);
                if (!_context.Transport.TryPublish(new SceneryChunkMessage(_transferId, _offset / SceneryChunkMessage.ChunkBytes, _transfer.Length, _publishedComplete, _hash!, part),
                    Connection, Stopwatch.GetTimestamp(), episodeId: _request.Episode, lookupId: _request.Lookup, stage: _request.Stage))
                    throw new IOException("Scenery output capacity was exhausted.");
                _offset += part.Length;
                if (_offset == _transfer.Length) { _transfer = null; _hash = null; }
            }
        }
    }
}
