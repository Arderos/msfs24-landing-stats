using System;
using LandingStats.App.Storage;

namespace LandingStats.App.Storage;

internal sealed class LastLandingBugReportBuffer
{
    private readonly object _gate = new object();
    private long _activeEpisodeId;
    private long _submittedEpisodeId;
    private BugReportCandidate? _candidate;

    public void BeginEpisode(long episodeId)
    {
        lock (_gate)
        {
            _activeEpisodeId = episodeId;
            _submittedEpisodeId = 0;
            _candidate = null;
        }
    }

    public bool TryRetain(BugReportCandidate candidate)
    {
        if (candidate == null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }

        lock (_gate)
        {
            if (candidate.EpisodeId != _activeEpisodeId || candidate.Samples.Count == 0 || candidate.Records.Count == 0)
            {
                return false;
            }

            _candidate = candidate;
            _submittedEpisodeId = 0;
            return true;
        }
    }

    public BugReportCandidate? Available()
    {
        lock (_gate)
        {
            return _candidate != null && _candidate.EpisodeId != _submittedEpisodeId
                ? _candidate
                : null;
        }
    }

    public bool IsActiveEpisode(long episodeId)
    {
        lock (_gate)
        {
            return _activeEpisodeId == episodeId;
        }
    }

    public void MarkSubmitted(long episodeId)
    {
        lock (_gate)
        {
            if (_candidate?.EpisodeId == episodeId)
            {
                _submittedEpisodeId = episodeId;
            }
        }
    }
}

// Compatibility factories for synthetic historical archives only.
internal static class LegacyBugReportFixture
{
    internal static string Create(this BugReportRepository repository, BugReportCandidate candidate, DateTime timestampUtc, bool automatic = false) =>
        automatic ? repository.TryCreateAutomatic(candidate, timestampUtc, long.MaxValue)! :
            repository.CreateManual(candidate, timestampUtc, Guid.NewGuid());
}
