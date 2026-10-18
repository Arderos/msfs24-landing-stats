using System.Collections.Generic;
using LandingStats.App.Models;

namespace LandingStats.App.Telemetry;

internal interface ISceneryQueryPort
{
    bool CanQuery { get; }
    IReadOnlyList<AirportFacility> CachedAirports { get; }
    SceneryQuerySnapshot Snapshot { get; }
    void Request(double latitude, double longitude);
    void Reset();
    void Cancel(string reason);
}

internal sealed class SceneryQuerySnapshot
{
    public SceneryQuerySnapshot(IReadOnlyList<RunwayFacility> runways, IReadOnlyList<AirportFacility> airports,
        bool freshAirportsReceived, bool candidatesComplete, int requestedAirports, int completedAirports)
    {
        Runways = runways; Airports = airports; FreshAirportsReceived = freshAirportsReceived;
        CandidatesComplete = candidatesComplete; RequestedAirports = requestedAirports; CompletedAirports = completedAirports;
    }
    public IReadOnlyList<RunwayFacility> Runways { get; }
    public IReadOnlyList<AirportFacility> Airports { get; }
    public bool FreshAirportsReceived { get; }
    public bool CandidatesComplete { get; }
    public int RequestedAirports { get; }
    public int CompletedAirports { get; }
}
