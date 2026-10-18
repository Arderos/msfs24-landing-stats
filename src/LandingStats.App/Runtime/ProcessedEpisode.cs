using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.Core;

namespace LandingStats.App.Runtime;

internal sealed class ProcessedEpisode
{
    private ProcessedEpisode(
        bool replayLike,
        IReadOnlyList<AirportFacility> airportFacilities,
        IReadOnlyList<LandingRecord> records,
        TelemetryDecisionSnapshot? decisions = null)
    {
        ReplayLike = replayLike;
        AirportFacilities = airportFacilities;
        Records = records;
        ReportRecords = records.Select(record => LandingRecordFile.FromRecord(record).ToRecord()).ToArray();
        Decisions = decisions;
    }

    public bool ReplayLike { get; }

    public IReadOnlyList<AirportFacility> AirportFacilities { get; }

    public IReadOnlyList<LandingRecord> Records { get; }
    public TelemetryDecisionSnapshot? Decisions { get; }
    public IReadOnlyList<LandingRecord> ReportRecords { get; }

    public static ProcessedEpisode Replay(TelemetryDecisionSnapshot decisions) => new ProcessedEpisode(
        true,
        Array.Empty<AirportFacility>(),
        Array.Empty<LandingRecord>(), decisions);

    public static ProcessedEpisode Completed(
        IReadOnlyList<AirportFacility> airportFacilities,
        IReadOnlyList<LandingRecord> records,
        TelemetryDecisionSnapshot decisions) =>
        new ProcessedEpisode(false, airportFacilities, records, decisions);
}
