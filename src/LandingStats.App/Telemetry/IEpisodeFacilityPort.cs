using System.Collections.Generic;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

/// <summary>Episode-facing scenery state; the coordinator never receives a native connection.</summary>
internal interface IEpisodeFacilityPort
{
    long EpisodeId { get; }
    bool CanQuery { get; }
    void SetDecisionContext(TelemetryDecisionJournal? journal, double time);
    double DecisionTime { get; }
    Task<IReadOnlyList<RunwayFacility>>? RunwayTask { get; }
    Task<EpisodeFacilitySnapshot>? SnapshotTask { get; }
    IReadOnlyList<AirportFacility> CachedAirports { get; }
    void BeginEpisode(long episodeId);
    void RequestPreliminary(TelemetrySample trigger, IReadOnlyList<TelemetrySample> samples);
    void RequestFinal(IReadOnlyList<TelemetrySample> samples);
    void CompleteWithoutRefresh(string reason);
    void DiscardEpisode(long episodeId);
    EpisodeFacilitySnapshot CurrentSnapshot();
}
