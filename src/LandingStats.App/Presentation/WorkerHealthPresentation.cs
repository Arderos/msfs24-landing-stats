using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LandingStats.App.Runtime;

namespace LandingStats.App.Presentation;

internal enum WorkerIndicator { Healthy, Recovering, Failed }

internal sealed class WorkerHealthItem
{
    public WorkerHealthItem(string key, WorkerIndicator indicator, string detail, string reason, bool warning)
    { Key = key; Indicator = indicator; Detail = detail; Reason = reason; Warning = warning; }
    public string Key { get; }
    public WorkerIndicator Indicator { get; }
    public string Detail { get; }
    public string Reason { get; }
    public bool Warning { get; }
}

// A failure episode spans generation changes and only ends after every member
// has settled. UI acknowledgement never changes supervision or recording.
internal sealed class WorkerHealthPresentation
{
    internal static readonly TimeSpan FailureThreshold = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, Episode> _episodes = new Dictionary<string, Episode>();
    private sealed class Episode
    {
        public long Since;
        public bool Failed, Acknowledged;
    }
    private static readonly KeyValuePair<string, string[]>[] Groups =
    {
        Group("Simulator", "source.simulator"), Group("Controllers", "source.controllers"),
        Group("Analysis", "analysis.episodes", "analysis.catalog", "recovery.sessions"),
        Group("Recording", "recording", "journal", "commands", "recording.clock", "storage.raw", "storage.reports", "storage.retention"),
        Group("Upload", "delivery.telemetry"), Group("Cloud", "cloud.sync"), Group("Api", "integration.api", "integration.data")
    };
    private static KeyValuePair<string, string[]> Group(string key, params string[] components) => new KeyValuePair<string, string[]>(key, components);

    public IReadOnlyList<WorkerHealthItem> Update(IReadOnlyList<SupervisionSnapshot> health, long now, bool cloud, bool api)
    {
        var result = new List<WorkerHealthItem>();
        foreach (var group in Groups)
        {
            if (group.Key == "Cloud" && !cloud || group.Key == "Api" && !api)
            { _episodes.Remove(group.Key); continue; }
            var members = group.Value.Select(name => health.FirstOrDefault(item => item.Component == name)).ToArray();
            var unavailable = members.Where(item => item == null || !item.Settled ||
                item.State != SupervisionState.Running && item.State != SupervisionState.Waiting).ToArray();
            if (unavailable.Length == 0)
            {
                _episodes.Remove(group.Key);
                result.Add(new WorkerHealthItem(group.Key, WorkerIndicator.Healthy, "", "", false));
                continue;
            }
            if (!_episodes.TryGetValue(group.Key, out var episode))
                _episodes.Add(group.Key, episode = new Episode { Since = now });
            foreach (var item in unavailable)
                if (item != null && item.UnavailableSinceQpc > 0) episode.Since = Math.Min(episode.Since, item.UnavailableSinceQpc);
            episode.Failed |= (now - episode.Since) / (double)Stopwatch.Frequency >= FailureThreshold.TotalSeconds;
            var fault = unavailable.FirstOrDefault(item => item != null && item.Failure.Length != 0) ?? unavailable.FirstOrDefault(item => item != null);
            result.Add(new WorkerHealthItem(group.Key, episode.Failed ? WorkerIndicator.Failed : WorkerIndicator.Recovering,
                fault?.Failure ?? "", fault?.Reason ?? "not_started", episode.Failed && !episode.Acknowledged));
        }
        return result;
    }
    public void Acknowledge(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            if (_episodes.TryGetValue(key, out var episode) && episode.Failed) episode.Acknowledged = true;
    }
}
