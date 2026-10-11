using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.Models;

// A single episode owns both stages. No shared scenery cache can fill a lookup
// after the user has changed flight or the recorder has begun another landing.
internal sealed class EpisodeFacilitySnapshot
{
    public EpisodeFacilitySnapshot(long episodeId,
        IReadOnlyList<RunwayFacility> preliminaryRunways,
        IReadOnlyList<AirportFacility> preliminaryAirports,
        IReadOnlyList<TelemetrySample> approachSamples,
        IReadOnlyList<RunwayFacility> finalRunways,
        IReadOnlyList<AirportFacility> finalAirports,
        bool finalLookupCompleted,
        bool preliminaryRunwayCandidatesComplete = true,
        bool finalRunwayCandidatesComplete = true)
    {
        EpisodeId = episodeId;
        PreliminaryRunways = preliminaryRunways.Select(value => value.Snapshot()).ToArray();
        PreliminaryAirports = CopyAirports(preliminaryAirports);
        ApproachSamples = approachSamples.Select(value => value.CopyForAnalysis()).ToArray();
        FinalRunways = finalRunways.Select(value => value.Snapshot()).ToArray();
        FinalAirports = CopyAirports(finalAirports);
        FinalLookupCompleted = finalLookupCompleted;
        PreliminaryRunwayCandidatesComplete = preliminaryRunwayCandidatesComplete;
        FinalRunwayCandidatesComplete = finalRunwayCandidatesComplete;
    }

    public long EpisodeId { get; }
    public IReadOnlyList<RunwayFacility> PreliminaryRunways { get; }
    public IReadOnlyList<AirportFacility> PreliminaryAirports { get; }
    public IReadOnlyList<TelemetrySample> ApproachSamples { get; }
    public IReadOnlyList<RunwayFacility> FinalRunways { get; }
    public IReadOnlyList<AirportFacility> FinalAirports { get; }
    public bool FinalLookupCompleted { get; }
    // A complete airport list is not proof that every requested airport's base
    // runway reply arrived. Keep successful replies without claiming uniqueness
    // against missing candidates. Optional lighting/threshold replies do not
    // affect base candidate completeness.
    public bool PreliminaryRunwayCandidatesComplete { get; }
    public bool FinalRunwayCandidatesComplete { get; }

    private static AirportFacility[] CopyAirports(IEnumerable<AirportFacility> airports) => airports.Select(value =>
        new AirportFacility { Ident = value.Ident, Region = value.Region, LatitudeDegrees = value.LatitudeDegrees,
            LongitudeDegrees = value.LongitudeDegrees, AltitudeMeters = value.AltitudeMeters }).ToArray();
}
