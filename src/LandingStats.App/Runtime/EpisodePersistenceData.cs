using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class EpisodeGeometryData
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Id { get; set; }
    [DataMember(Order = 3)] public string Engine { get; set; } = "";
    [DataMember(Order = 4)] public string InputHash { get; set; } = "";
    [DataMember(Order = 5)] public AnalysisGeometrySnapshot?[] Segments { get; set; } = Array.Empty<AnalysisGeometrySnapshot?>();
    [DataMember(Order = 6)] public DecisionPersistenceData Decisions { get; set; } = new DecisionPersistenceData();
    internal void Validate(EpisodeWorkManifest manifest)
    {
        if (Version != 1 || Id != manifest.Id || Engine != manifest.Engine || InputHash != manifest.InputHash || Segments == null || Segments.Length > 65536 || Decisions == null)
            throw new System.IO.InvalidDataException("Geometry inputs do not belong to this episode and engine.");
        foreach (var segment in Segments) segment?.Validate();
        _ = Decisions.Restore();
    }
}

[DataContract]
internal sealed class EpisodeInputMetadata
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Id { get; set; }
    [DataMember(Order = 3)] public long LocalEpisodeId { get; set; }
    [DataMember(Order = 4)] public long TimestampUtcTicks { get; set; }
    public DateTime TimestampUtc { get => new DateTime(TimestampUtcTicks, DateTimeKind.Utc); set => TimestampUtcTicks = value.ToUniversalTime().Ticks; }
    [DataMember(Order = 5)] public string Simulator { get; set; } = "";
    [DataMember(Order = 6)] public string AircraftTitle { get; set; } = "";
    [DataMember(Order = 7)] public string AircraftType { get; set; } = "";
    [DataMember(Order = 8)] public string AircraftModel { get; set; } = "";
    [DataMember(Order = 9)] public string[] ControlSources { get; set; } = Array.Empty<string>();
    [DataMember(Order = 10)] public bool DiagnosticOnly { get; set; }
    [DataMember(Order = 11)] public AirportFacility[] KnownAirports { get; set; } = Array.Empty<AirportFacility>();
    [DataMember(Order = 12)] public AirportFacility[] EpisodeAirports { get; set; } = Array.Empty<AirportFacility>();
    [DataMember(Order = 13)] public FacilityPersistenceData Facilities { get; set; } = new FacilityPersistenceData();
    [DataMember(Order = 14)] public DecisionPersistenceData Decisions { get; set; } = new DecisionPersistenceData();

    public static EpisodeInputMetadata Capture(LandingEpisodeEventArgs episode, DateTime timestamp,
        IReadOnlyList<AirportFacility> known) => new EpisodeInputMetadata
    {
        Id = episode.PersistentId, LocalEpisodeId = episode.EpisodeId, TimestampUtc = timestamp.ToUniversalTime(),
        Simulator = episode.Simulator, AircraftTitle = episode.AircraftTitle, AircraftType = episode.AircraftType,
        AircraftModel = episode.AircraftModel, ControlSources = episode.ControlInputSources.ToArray(),
        DiagnosticOnly = episode.IsDiagnosticOnly, KnownAirports = known.ToArray(), EpisodeAirports = episode.AirportFacilities.ToArray(),
        Facilities = FacilityPersistenceData.Capture(episode.InitialFacilities),
        Decisions = DecisionPersistenceData.Capture(episode.DecisionJournal?.Snapshot())
    };
    public LandingEpisodeEventArgs Restore(IReadOnlyList<TelemetrySample> samples, IReadOnlyList<TelemetrySample> approach)
    {
        if (Version != 1 || Id == Guid.Empty || ControlSources == null || ControlSources.Length > 64 ||
            KnownAirports == null || EpisodeAirports == null || KnownAirports.Length > 65536 || EpisodeAirports.Length > 65536 ||
            Simulator == null || AircraftTitle == null || AircraftType == null || AircraftModel == null || Facilities == null || Decisions == null)
            throw new System.IO.InvalidDataException("Invalid episode metadata.");
        var site = Facilities.Restore(LocalEpisodeId, approach);
        return new LandingEpisodeEventArgs(LocalEpisodeId, samples, Simulator, AircraftTitle, AircraftType, AircraftModel,
            EpisodeAirports, ControlSources, Decisions.Restore(), isDiagnosticOnly: DiagnosticOnly,
            facilitiesTask: Task.FromResult(site), initialFacilities: site, persistentId: Id);
    }
}

[DataContract]
internal sealed class FacilityPersistenceData
{
    [DataMember(Order = 1)] public RunwayFacility[] PreliminaryRunways { get; set; } = Array.Empty<RunwayFacility>();
    [DataMember(Order = 2)] public AirportFacility[] PreliminaryAirports { get; set; } = Array.Empty<AirportFacility>();
    [DataMember(Order = 3)] public RunwayFacility[] FinalRunways { get; set; } = Array.Empty<RunwayFacility>();
    [DataMember(Order = 4)] public AirportFacility[] FinalAirports { get; set; } = Array.Empty<AirportFacility>();
    [DataMember(Order = 5)] public bool FinalCompleted { get; set; }
    [DataMember(Order = 6)] public bool PreliminaryCandidatesComplete { get; set; }
    [DataMember(Order = 7)] public bool FinalCandidatesComplete { get; set; }
    public static FacilityPersistenceData Capture(EpisodeFacilitySnapshot source) => new FacilityPersistenceData
    {
        PreliminaryRunways = source.PreliminaryRunways.ToArray(), PreliminaryAirports = source.PreliminaryAirports.ToArray(),
        FinalRunways = source.FinalRunways.ToArray(), FinalAirports = source.FinalAirports.ToArray(), FinalCompleted = source.FinalLookupCompleted,
        PreliminaryCandidatesComplete = source.PreliminaryRunwayCandidatesComplete, FinalCandidatesComplete = source.FinalRunwayCandidatesComplete
    };
    public EpisodeFacilitySnapshot Restore(long episode, IReadOnlyList<TelemetrySample> approach)
    {
        if (PreliminaryRunways == null || PreliminaryAirports == null || FinalRunways == null || FinalAirports == null ||
            PreliminaryRunways.Length > 65536 || PreliminaryAirports.Length > 65536 || FinalRunways.Length > 65536 || FinalAirports.Length > 65536)
            throw new System.IO.InvalidDataException("Invalid facility snapshot.");
        return new EpisodeFacilitySnapshot(episode, PreliminaryRunways, PreliminaryAirports, approach, FinalRunways, FinalAirports,
            FinalCompleted, PreliminaryCandidatesComplete, FinalCandidatesComplete);
    }
}

[DataContract]
internal sealed class DecisionPersistenceData
{
    [DataMember(Order = 1)] public DecisionEntryData[] Entries { get; set; } = Array.Empty<DecisionEntryData>();
    [DataMember(Order = 2)] public int Dropped { get; set; }
    public static DecisionPersistenceData Capture(TelemetryDecisionSnapshot? source) => new DecisionPersistenceData
    {
        Dropped = source?.DroppedEntryCount ?? 0,
        Entries = source?.Entries.Select(value => new DecisionEntryData
        { Time = value.TimeSeconds, Component = value.Component, Code = value.Code, Reason = value.Reason,
            Details = value.Details.ToDictionary(pair => pair.Key, pair => pair.Value) }).ToArray() ?? Array.Empty<DecisionEntryData>()
    };
    public TelemetryDecisionJournal Restore()
    {
        if (Entries == null || Entries.Length > TelemetryDecisionJournal.MaximumEntries || Dropped < 0)
            throw new System.IO.InvalidDataException("Invalid decision snapshot.");
        var journal = new TelemetryDecisionJournal();
        foreach (var entry in Entries)
            journal.Record(entry.Time, entry.Component, entry.Code, entry.Reason, entry.Details);
        journal.RestoreDroppedEntryCount(Dropped);
        return journal;
    }
}

[DataContract]
internal sealed class DecisionEntryData
{
    [DataMember(Order = 1)] public double Time { get; set; }
    [DataMember(Order = 2)] public string Component { get; set; } = "";
    [DataMember(Order = 3)] public string Code { get; set; } = "";
    [DataMember(Order = 4)] public string Reason { get; set; } = "";
    [DataMember(Order = 5)] public Dictionary<string, double> Details { get; set; } = new Dictionary<string, double>();
}
