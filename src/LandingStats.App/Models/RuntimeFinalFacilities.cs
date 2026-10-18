using System;
using LandingStats.App.Models;
using LandingStats.Core;
namespace LandingStats.App.Runtime;
internal sealed class RuntimeFinalFacilities
{
    public RuntimeFinalFacilities(Guid episode, EpisodeFacilitySnapshot snapshot, TelemetryDecisionSnapshot? decisions)
    { Episode = episode; Snapshot = snapshot; Decisions = decisions; }
    public Guid Episode { get; }
    public EpisodeFacilitySnapshot Snapshot { get; }
    public TelemetryDecisionSnapshot? Decisions { get; }
}
