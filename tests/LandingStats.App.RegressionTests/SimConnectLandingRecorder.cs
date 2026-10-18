using System;
using System.Collections.Generic;
using System.Diagnostics;
using LandingStats.App.RegressionTests;
using LandingStats.App.Models;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

/// <summary>Test-only composition for existing native/coordinator regression fixtures.
/// The application starts these components exclusively through LandingRuntime.</summary>
internal sealed class SimConnectLandingRecorder : IDisposable
{
    public const int WindowMessage = SimConnectSource.WindowMessage;
    private readonly SimConnectSource _source;
    private readonly EpisodeFacilityCoordinator _facilityCoordinator;
    private readonly LandingEpisodeCoordinator _coordinator;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _disposed;
    internal SimConnectSource NativeSource => _source;
    internal EpisodeFacilityCoordinator FacilityCoordinator => _facilityCoordinator;
    internal LandingEpisodeCoordinator Coordinator => _coordinator;
    public SimConnectLandingRecorder(IntPtr windowHandle) : this(windowHandle, null) { }
    internal SimConnectLandingRecorder(IntPtr windowHandle, Func<SimConnect>? openConnection,
        Action<SimConnect>? receiveConnection = null, Action<SimConnect>? disposeConnection = null,
        Func<SimConnect, uint, uint, bool, uint?>? requestTelemetry = null, Func<double>? subscriptionClock = null)
    {
        _source = new SimConnectSource(windowHandle, new CaptureContext(this), new SceneryObserver(this),
            openConnection, receiveConnection, disposeConnection, requestTelemetry, subscriptionClock);
        _facilityCoordinator = new EpisodeFacilityCoordinator(_source.SceneryQuery);
        _coordinator = new LandingEpisodeCoordinator(_facilityCoordinator, () => _clock.Elapsed.TotalSeconds, ControlInputSources);
        _source.Connected += simulator => _coordinator.Simulator = simulator;
        _source.MetadataReceived += (title, type, model) => _coordinator.SetMetadata(title, type, model);
        _source.SampleReceived += sample => _coordinator.ProcessSample(sample);
        _source.Disconnected += reason => _coordinator.SourceInterrupted(reason.ToString());
        _source.LifecycleReceived += OnLifecycle;
        _source.StatusChanged += (_, args) => StatusChanged?.Invoke(this, args);
        _source.SimulatorExited += (_, args) => SimulatorExited?.Invoke(this, args);
        _coordinator.StatusChanged += (_, args) => StatusChanged?.Invoke(this, args);
        _coordinator.EpisodeCompleted += (_, args) => EpisodeCompleted?.Invoke(this, args);
        _coordinator.EpisodeStarted += (_, args) => EpisodeStarted?.Invoke(this, args);
        _coordinator.RawDebugCaptureStarted += (_, args) => RawDebugCaptureStarted?.Invoke(this, args);
        _coordinator.RawDebugSampleReceived += (_, args) => RawDebugSampleReceived?.Invoke(this, args);
        _coordinator.RawDebugCaptureStopped += (_, args) => RawDebugCaptureStopped?.Invoke(this, args);
        _coordinator.AircraftGroundStateChanged += (_, args) => AircraftGroundStateChanged?.Invoke(this, args);
        _coordinator.MetadataRequested += (_, _) => _source.RefreshMetadata();
    }
    private void OnLifecycle(SourceLifecycle kind, int value)
    {
        switch (kind)
        {
            case SourceLifecycle.PauseFlags: _coordinator.SetSdkPauseFlags(value); break;
            case SourceLifecycle.SimRunning: _coordinator.SetSdkSimRunning(value != 0); break;
            case SourceLifecycle.Flow: _coordinator.HandleFlowEvent((SimulatorFlowEvent)value); break;
        }
    }
    public event EventHandler<RecorderStatusEventArgs>? StatusChanged;
    public event EventHandler? SimulatorExited;
    public event EventHandler<LandingEpisodeEventArgs>? EpisodeCompleted;
    public event EventHandler<LandingEpisodeStartedEventArgs>? EpisodeStarted;
    public event EventHandler<RunwayFacilitiesEventArgs>? RunwayFacilitiesUpdated;
    public event EventHandler<RawDebugCaptureStartedEventArgs>? RawDebugCaptureStarted;
    public event EventHandler<RawDebugSampleEventArgs>? RawDebugSampleReceived;
    public event EventHandler? RawDebugCaptureStopped;
    public event EventHandler<AircraftGroundStateEventArgs>? AircraftGroundStateChanged;
    public bool RawDebugEnabled => _coordinator.RawDebugEnabled;
    public bool IsAircraftAirborne => _coordinator.IsAircraftAirborne;
    public IReadOnlyList<RunwayFacility> RunwayFacilities => _source.SceneryQuery.Snapshot.CandidatesComplete ? _source.SceneryQuery.Snapshot.Runways : Array.Empty<RunwayFacility>();
    public IReadOnlyList<RunwayFacility> GetRunwayFacilities(long episodeId) => episodeId == _facilityCoordinator.EpisodeId ? RunwayFacilities : Array.Empty<RunwayFacility>();
    public void SeedAirportFacilities(IEnumerable<AirportFacility> facilities)
    {
        var cache = (Dictionary<string, AirportFacility>)RecorderTestAccess.Get(_source, "_airportFacilities")!;
        foreach (var facility in facilities) cache[facility.Key] = facility;
    }
    public void Start() => _source.Start();
    public IntPtr HandleWindowMessage(int message, ref bool handled) => _source.HandleWindowMessage(message, ref handled);
    public void SetRawDebugEnabled(bool enabled)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimConnectLandingRecorder));
        if (enabled == RawDebugEnabled) return;
        _coordinator.SetRawDebugEnabled(enabled); _source.RefreshTelemetryMode();
    }
    private void ProcessSample(TelemetrySample sample) => _coordinator.ProcessSample(sample);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _coordinator.Stop(); } finally { _source.Dispose(); }
    }
    private IReadOnlyList<string> ControlInputSources() => Array.Empty<string>();
    private sealed class CaptureContext : ISimulatorCaptureContext
    {
        private readonly SimConnectLandingRecorder _owner;
        public CaptureContext(SimConnectLandingRecorder owner) { _owner = owner; }
        public bool RawDebugEnabled => _owner._coordinator.RawDebugEnabled;
        public bool IsRecording => _owner._coordinator.IsRecording;
        public bool HasContact => false; // Matches the isolated simulator role; contact state belongs to the parent.
        public TelemetryDecisionJournal DecisionJournal => _owner._coordinator.DecisionJournal;
        public double LastSimulationTime => _owner._coordinator.LastSimulationTime;
    }
    private sealed class SceneryObserver : ISceneryQueryObserver
    {
        private readonly SimConnectLandingRecorder _owner;
        public SceneryObserver(SimConnectLandingRecorder owner) { _owner = owner; }
        public long EpisodeId => _owner._facilityCoordinator.EpisodeId;
        public bool IsPreliminary => _owner._facilityCoordinator.IsPreliminary;
        public TelemetryDecisionJournal? DecisionJournal => _owner._facilityCoordinator.DecisionJournal;
        public double DecisionTime => _owner._facilityCoordinator.DecisionTime;
        public void FinishRunwayLookupStage(IReadOnlyList<RunwayFacility> facilities)
        {
            _owner._facilityCoordinator.FinishRunwayLookupStage(facilities);
            if (_owner._source.SceneryQuery.Snapshot.CandidatesComplete)
                _owner.RunwayFacilitiesUpdated?.Invoke(_owner, new RunwayFacilitiesEventArgs(facilities, EpisodeId));
        }
        public void ResetConnection() => _owner._facilityCoordinator.ResetConnection();
    }
}
