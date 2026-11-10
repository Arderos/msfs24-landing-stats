using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;

namespace LandingStats.App.Storage;

// A capture timestamp is shared by every contact, including distinct
// touch-and-go episodes. Keep those episodes together, using their recorded
// contact time for chronology rather than treating an identity as a clock.
internal static class LandingHistoryOrder
{
    internal static LandingRecord[] Sort(IEnumerable<LandingRecord> records) => records
        .GroupBy(record => record.TimestampUtc)
        .OrderByDescending(timestamp => timestamp.Key)
        .SelectMany(timestamp => timestamp
            .GroupBy(record => new
            {
                ExplicitEpisode = !string.IsNullOrWhiteSpace(record.RecordingEpisodeId),
                Identity = string.IsNullOrWhiteSpace(record.RecordingEpisodeId) ? record.Id : record.RecordingEpisodeId,
                record.IsImported,
            })
            .OrderByDescending(episode => episode.Max(ContactTime))
            .ThenByDescending(episode => episode.Max(record => record.ContactNumber))
            .ThenBy(episode => episode.OrderBy(record => record.Id, StringComparer.Ordinal).First().Id, StringComparer.Ordinal)
            .SelectMany(episode => episode.OrderByDescending(record => record.ContactNumber)
                .ThenByDescending(ContactTime).ThenBy(record => record.Id, StringComparer.Ordinal)))
        .ToArray();

    private static double ContactTime(LandingRecord record) =>
        record.EstimatedContactTimeSeconds is double time && !double.IsNaN(time) && !double.IsInfinity(time)
            ? time : double.NegativeInfinity;
}
