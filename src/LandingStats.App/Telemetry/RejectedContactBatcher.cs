using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

/// <summary>Combines only short rejected transitions; never coalesces real landing captures.</summary>
internal sealed class RejectedContactBatcher
{
    internal const int MaximumCaptures = 16;
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);
    private readonly List<LandingEpisodeEventArgs> _pending = new List<LandingEpisodeEventArgs>();
    private DateTime _startedUtc;

    internal IReadOnlyList<LandingEpisodeEventArgs> Accept(LandingEpisodeEventArgs episode, DateTime nowUtc)
    {
        var output = new List<LandingEpisodeEventArgs>();
        var snapshot = episode.DecisionJournal?.Snapshot();
        var eligible = episode.IsDiagnosticOnly && episode.Samples.Count > 0 && episode.Samples.Count <= 3 &&
            snapshot?.Truncated == false && snapshot.Entries.All(e => e.Details.Count <= 22 && e.Code != "rejected_contact_batch") &&
            snapshot.Entries.Any(e => e.Code == "rejected_contact_diagnostics") && !BuildJournal(new[] { episode }).Snapshot().Truncated;
        if (_pending.Count > 0 && (!eligible || nowUtc - _startedUtc >= MaximumDelay || !SameContext(_pending[_pending.Count - 1], episode) ||
            BuildJournal(_pending.Concat(new[] { episode }).ToArray()).Snapshot().Truncated))
            output.Add(Flush()!);
        if (!eligible) { output.Add(episode); return output; }
        if (_pending.Count == 0) _startedUtc = nowUtc;
        _pending.Add(episode);
        if (_pending.Count >= MaximumCaptures) output.Add(Flush()!);
        return output;
    }

    internal LandingEpisodeEventArgs? FlushExpired(DateTime nowUtc) =>
        _pending.Count > 0 && nowUtc - _startedUtc >= MaximumDelay ? Flush() : null;

    internal LandingEpisodeEventArgs? Flush()
    {
        if (_pending.Count == 0) return null;
        var first = _pending[0];
        var samples = _pending.SelectMany(e => e.Samples).ToArray();
        var journal = BuildJournal(_pending);
        _pending.Clear();
        return new LandingEpisodeEventArgs(first.EpisodeId, samples, first.Simulator, first.AircraftTitle,
            first.AircraftType, first.AircraftModel, first.AirportFacilities, first.ControlInputSources,
            journal, isDiagnosticOnly: true);
    }

    private static TelemetryDecisionJournal BuildJournal(IReadOnlyList<LandingEpisodeEventArgs> episodes)
    {
        var journal = new TelemetryDecisionJournal();
        for (var index = 0; index < episodes.Count; index++)
        foreach (var entry in episodes[index].DecisionJournal!.Snapshot().Entries)
        {
            var details = entry.Details.ToDictionary(p => p.Key, p => p.Value);
            details["diagnostic_index"] = index;
            details["source_episode_id"] = episodes[index].EpisodeId;
            journal.Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, details);
        }
        journal.Record(TelemetryAnalysisFrames.TimeOf(episodes[episodes.Count - 1].Samples.Last()), "recorder", "rejected_contact_batch",
            "Combined short rejected transitions from one aircraft and segment; every original raw sample and rejection context is retained.",
            new Dictionary<string, double>
            {
                ["capture_count"] = episodes.Count, ["raw_sample_count"] = episodes.Sum(e => e.Samples.Count),
                ["first_episode_id"] = episodes[0].EpisodeId, ["last_episode_id"] = episodes[episodes.Count - 1].EpisodeId,
            });
        return journal;
    }

    private static bool SameContext(LandingEpisodeEventArgs a, LandingEpisodeEventArgs b)
    {
        if (a.Simulator != b.Simulator || a.AircraftTitle != b.AircraftTitle || a.AircraftType != b.AircraftType ||
            a.AircraftModel != b.AircraftModel || !a.ControlInputSources.SequenceEqual(b.ControlInputSources)) return false;
        var reasonsA = a.DecisionJournal!.Snapshot().Entries.Where(e => e.Code.Contains("rejected")).Select(e => e.Component + "/" + e.Code + "/" + e.Reason);
        var reasonsB = b.DecisionJournal!.Snapshot().Entries.Where(e => e.Code.Contains("rejected")).Select(e => e.Component + "/" + e.Code + "/" + e.Reason);
        if (!reasonsA.SequenceEqual(reasonsB)) return false;
        var last = a.Samples[a.Samples.Count - 1]; var next = b.Samples[0];
        return last.FlightSegment == next.FlightSegment && last.SimulatorState == next.SimulatorState &&
            b.Samples.All(s => s.FlightSegment == next.FlightSegment) &&
            next.SimulationTimeSeconds >= last.SimulationTimeSeconds && next.HostElapsedSeconds >= last.HostElapsedSeconds;
    }
}
