using System;
using System.Collections.Generic;

namespace LandingStats.Core;

internal readonly struct TelemetryContactCandidate
{
    public TelemetryContactCandidate(
        int contactIndex,
        int previousAirborneIndex,
        int episodeNumber,
        int contactNumber)
    {
        ContactIndex = contactIndex;
        PreviousAirborneIndex = previousAirborneIndex;
        EpisodeNumber = episodeNumber;
        ContactNumber = contactNumber;
    }

    public int ContactIndex { get; }
    public int PreviousAirborneIndex { get; }
    public int EpisodeNumber { get; }
    public int ContactNumber { get; }
}

internal static class TelemetryContactDetector
{
    public static IReadOnlyList<TelemetryContactCandidate> Find(
        IReadOnlyList<TelemetrySample> samples,
        double episodeGapSeconds,
        TelemetryDecisionJournal? journal = null)
    {
        var candidates = new List<TelemetryContactCandidate>();
        var continuity = new TelemetryContactContinuity();
        var previousOnGround = samples.Count > 0 && samples[0].OnGround;
        var previousAirborneIndex = -1;
        var previousContactTime = double.NegativeInfinity;
        var episodeNumber = 0;
        var contactNumber = 0;

        for (var index = 0; index < samples.Count; index++)
        {
            var current = samples[index];
            var isContact = continuity.Observe(current);
            if (!current.MotionSimulation) previousAirborneIndex = -1;
            else if (!current.OnGround)
            {
                previousAirborneIndex = index;
            }

            if (current.OnGround && !previousOnGround && !isContact)
                journal?.Record(TouchdownAnalysis.TimeOf(current), "contact", "physics_discontinuity_rejected",
                    "Ground transition has no airborne evidence in the current active-physics segment.",
                    new Dictionary<string, double> { ["sample_index"] = index });

            if (isContact && previousAirborneIndex >= 0)
            {
                var contactTime = TouchdownAnalysis.TimeOf(current);
                if (contactTime - previousContactTime > episodeGapSeconds)
                {
                    episodeNumber++;
                    contactNumber = 1;
                }
                else
                {
                    contactNumber++;
                }

                candidates.Add(new TelemetryContactCandidate(
                    index,
                    previousAirborneIndex,
                    episodeNumber,
                    contactNumber));
                journal?.Record(contactTime, "contact", "air_to_ground",
                    "Accepted an airborne-to-ground transition; contacts are grouped by time since the previous contact.",
                    new Dictionary<string, double>
                    {
                        ["episode_number"] = episodeNumber,
                        ["contact_number"] = contactNumber,
                        ["sample_index"] = index,
                        ["previous_airborne_index"] = previousAirborneIndex,
                        ["seconds_since_previous_contact"] = contactTime - previousContactTime,
                        ["episode_gap_seconds"] = episodeGapSeconds
                    });
                previousContactTime = contactTime;
            }

            previousOnGround = current.OnGround;
        }

        journal?.Record(samples.Count > 0 ? TouchdownAnalysis.TimeOf(samples[samples.Count - 1]) : 0,
            "contact", candidates.Count > 0 ? "detection_complete" : "no_air_to_ground_transition",
            candidates.Count > 0 ? "Finished contact detection." : "No eligible airborne-to-ground transition in this sample sequence.",
            new Dictionary<string, double> { ["sample_count"] = samples.Count, ["contact_count"] = candidates.Count });
        return candidates;
    }
}
