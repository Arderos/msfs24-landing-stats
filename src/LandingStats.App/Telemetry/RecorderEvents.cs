using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

internal enum RecorderState
{
    Waiting,
    Connected,
    Recording,
    Error,
}

internal sealed class RecorderStatusEventArgs : EventArgs
{
    public RecorderStatusEventArgs(RecorderState state, string messageKey, params object[] messageArguments)
    {
        State = state;
        MessageKey = messageKey;
        MessageArguments = messageArguments ?? Array.Empty<object>();
    }

    public RecorderState State { get; }

    public string MessageKey { get; }

    public object[] MessageArguments { get; }

    public string Message => ModelText.Format(MessageKey, MessageArguments);
}

internal sealed class LandingEpisodeEventArgs : EventArgs
{
    public LandingEpisodeEventArgs(
        long episodeId,
        IReadOnlyList<TelemetrySample> samples,
        string simulator,
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<AirportFacility> airportFacilities,
        IReadOnlyList<string> controlInputSources,
        TelemetryDecisionJournal? decisionJournal = null,
        Task<IReadOnlyList<RunwayFacility>>? runwayFacilitiesTask = null,
        bool isDiagnosticOnly = false,
        Task<EpisodeFacilitySnapshot>? facilitiesTask = null,
        EpisodeFacilitySnapshot? initialFacilities = null,
        Guid persistentId = default)
    {
        EpisodeId = episodeId;
        if (persistentId == Guid.Empty) throw new ArgumentException("The input producer must assign a persistent episode identity.", nameof(persistentId));
        PersistentId = persistentId;
        Samples = samples;
        Simulator = simulator;
        AircraftTitle = aircraftTitle;
        AircraftType = aircraftType;
        AircraftModel = aircraftModel;
        AirportFacilities = airportFacilities;
        ControlInputSources = controlInputSources;
        DecisionJournal = decisionJournal;
        IsDiagnosticOnly = isDiagnosticOnly;
        RunwayFacilitiesTask = runwayFacilitiesTask ?? Task.FromResult<IReadOnlyList<RunwayFacility>>(Array.Empty<RunwayFacility>());
        FacilitiesTask = facilitiesTask ?? CreateLegacyFacilitySnapshotAsync(episodeId, airportFacilities, RunwayFacilitiesTask);
        InitialFacilities = initialFacilities ?? new EpisodeFacilitySnapshot(episodeId, Array.Empty<RunwayFacility>(),
            Array.Empty<AirportFacility>(), Array.Empty<TelemetrySample>(), Array.Empty<RunwayFacility>(), airportFacilities, false);
    }

    public long EpisodeId { get; }

    // Assigned once by the runtime (or by a legacy in-memory producer) and
    // persisted with inputs before any repeatable side effects.
    public Guid PersistentId { get; }

    public IReadOnlyList<TelemetrySample> Samples { get; }

    public string Simulator { get; }

    public string AircraftTitle { get; }

    public string AircraftType { get; }

    public string AircraftModel { get; }

    public IReadOnlyList<AirportFacility> AirportFacilities { get; }

    public IReadOnlyList<string> ControlInputSources { get; }

    public TelemetryDecisionJournal? DecisionJournal { get; }

    public bool IsDiagnosticOnly { get; }

    public Task<IReadOnlyList<RunwayFacility>> RunwayFacilitiesTask { get; }

    public Task<EpisodeFacilitySnapshot> FacilitiesTask { get; }
    public EpisodeFacilitySnapshot InitialFacilities { get; }

    private static async Task<EpisodeFacilitySnapshot> CreateLegacyFacilitySnapshotAsync(long episodeId,
        IReadOnlyList<AirportFacility> airports, Task<IReadOnlyList<RunwayFacility>> runways)
    {
        return new EpisodeFacilitySnapshot(episodeId, Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(),
            Array.Empty<TelemetrySample>(), await runways.ConfigureAwait(false), airports, true);
    }
}

internal sealed class LandingEpisodeStartedEventArgs : EventArgs
{
    public LandingEpisodeStartedEventArgs(long episodeId)
    {
        EpisodeId = episodeId;
    }

    public long EpisodeId { get; }
}

internal sealed class AirportFacilitiesEventArgs : EventArgs
{
    public AirportFacilitiesEventArgs(IReadOnlyList<AirportFacility> facilities)
    {
        Facilities = facilities;
    }

    public IReadOnlyList<AirportFacility> Facilities { get; }
}

internal sealed class RawDebugCaptureStartedEventArgs : EventArgs
{
    public RawDebugCaptureStartedEventArgs(
        IReadOnlyList<TelemetrySample> initialSamples,
        DateTime startedUtc,
        string simulator,
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<string> controlInputSources)
    {
        InitialSamples = initialSamples;
        StartedUtc = startedUtc;
        Simulator = simulator;
        AircraftTitle = aircraftTitle;
        AircraftType = aircraftType;
        AircraftModel = aircraftModel;
        ControlInputSources = controlInputSources;
    }

    public IReadOnlyList<TelemetrySample> InitialSamples { get; }

    public DateTime StartedUtc { get; }

    public string Simulator { get; }

    public string AircraftTitle { get; }

    public string AircraftType { get; }

    public string AircraftModel { get; }

    public IReadOnlyList<string> ControlInputSources { get; }
}

internal sealed class RawDebugSampleEventArgs : EventArgs
{
    public RawDebugSampleEventArgs(TelemetrySample sample)
    {
        Sample = sample;
    }

    public TelemetrySample Sample { get; }
}

internal sealed class AircraftGroundStateEventArgs : EventArgs
{
    public AircraftGroundStateEventArgs(bool onGround)
    {
        OnGround = onGround;
    }

    public bool OnGround { get; }
}
