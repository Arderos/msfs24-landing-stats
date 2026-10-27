using System;
using System.Collections.Generic;
using LandingStats.App.Models;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Telemetry;

internal interface IAircraftConfigurationSource
{
    event Action<string, string>? AircraftConfigurationReceived;
}

internal interface ISimulatorSource : IDisposable
{
    bool IsConnected { get; }
    long SceneryRevision { get; }
    ISceneryQueryPort SceneryQuery { get; }
    event Action<string>? Connected;
    event Action<SourceDisconnectionReason>? Disconnected;
    event Action<TelemetrySample>? SampleReceived;
    event Action<GuardFrameMessage>? GuardReceived;
    event Action<ReplaySourceObservation>? SourceObservationReceived;
    event Action<string, string, string>? MetadataReceived;
    event Action<SourceLifecycle, int>? LifecycleReceived;
    event EventHandler? SimulatorExited;
    event EventHandler<RecorderStatusEventArgs>? StatusChanged;
    void Start();
    IntPtr HandleWindowMessage(int message, ref bool handled);
    void RefreshTelemetryMode();
    void RefreshMetadata();
}

// Owner-local ports: the native adapter owns no episode, storage or UI.
internal interface ISimulatorCaptureContext
{
    bool RawDebugEnabled { get; }
    bool IsRecording { get; }
    bool HasContact { get; }
    TelemetryDecisionJournal DecisionJournal { get; }
    double LastSimulationTime { get; }
}
internal interface ISceneryQueryObserver
{
    long EpisodeId { get; }
    bool IsPreliminary { get; }
    TelemetryDecisionJournal? DecisionJournal { get; }
    double DecisionTime { get; }
    void FinishRunwayLookupStage(IReadOnlyList<RunwayFacility> facilities);
    void ResetConnection();
}
