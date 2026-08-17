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
        double episodeGapSeconds)
    {
        var candidates = new List<TelemetryContactCandidate>();
        var hasBeenAirborne = false;
        var previousOnGround = samples.Count > 0 && samples[0].OnGround;
        var previousAirborneIndex = -1;
        var previousContactTime = double.NegativeInfinity;
        var episodeNumber = 0;
        var contactNumber = 0;

        for (var index = 0; index < samples.Count; index++)
        {
            var current = samples[index];
            if (!current.OnGround)
            {
                hasBeenAirborne = true;
                previousAirborneIndex = index;
            }

            if (hasBeenAirborne && current.OnGround && !previousOnGround && previousAirborneIndex >= 0)
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
                previousContactTime = contactTime;
            }

            previousOnGround = current.OnGround;
        }

        return candidates;
    }
}
