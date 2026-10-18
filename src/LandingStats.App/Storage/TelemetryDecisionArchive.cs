using System.Collections.Generic;
using System;
using System.IO;
using LandingStats.Archives;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using LandingStats.Core;

namespace LandingStats.App.Storage;

internal static class TelemetryDecisionArchive
{
    internal static void Write(CaptureArchiveWriter archive, long episodeId, TelemetryDecisionSnapshot snapshot)
        => Write(name => archive.CreateEntry(name), episodeId, snapshot);
    private static void Write(Func<string, Stream> create, long episodeId, TelemetryDecisionSnapshot snapshot)
    {
        var payload = new Journal
        {
            EpisodeId = episodeId,
            Truncated = snapshot.Truncated,
            Entries = snapshot.Entries.Select(entry => new Entry
            {
                TimeSeconds = entry.TimeSeconds,
                Component = entry.Component,
                Code = entry.Code,
                Reason = entry.Reason,
                Details = entry.Details.ToDictionary(pair => pair.Key, pair => pair.Value),
            }).ToArray(),
        };
        using var stream = create("decisions.json");
        new DataContractJsonSerializer(typeof(Journal), new DataContractJsonSerializerSettings
        {
            UseSimpleDictionaryFormat = true,
        }).WriteObject(stream, payload);
    }

    [DataContract]
    private sealed class Journal
    {
        [DataMember(Name = "format_version", Order = 1)] public int Version = 1;
        [DataMember(Name = "episode_id", Order = 2)] public long EpisodeId;
        [DataMember(Name = "truncated", Order = 3)] public bool Truncated;
        [DataMember(Name = "entries", Order = 4)] public Entry[] Entries = System.Array.Empty<Entry>();
    }

    [DataContract]
    private sealed class Entry
    {
        [DataMember(Name = "time_seconds", Order = 1)] public double TimeSeconds;
        [DataMember(Name = "component", Order = 2)] public string Component = string.Empty;
        [DataMember(Name = "code", Order = 3)] public string Code = string.Empty;
        [DataMember(Name = "reason", Order = 4)] public string Reason = string.Empty;
        [DataMember(Name = "details", Order = 5)] public Dictionary<string, double> Details = new Dictionary<string, double>();
    }
}
