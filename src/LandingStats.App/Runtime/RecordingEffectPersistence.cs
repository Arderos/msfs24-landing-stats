using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LandingStats.App.Models;

namespace LandingStats.App.Runtime;

/// <summary>Minimal durable recording effects. Analysis, archive construction,
/// network delivery and UI updates run in their own supervised owners.</summary>
internal sealed class RecordingEffectPersistence
{
    private readonly EpisodeTransactionStore _episodes;
    private readonly Func<DurableRuntimeInput, IReadOnlyList<object>, Task> _raw;
    private readonly Action _wakeEpisodes;
    private readonly Func<IReadOnlyList<AirportFacility>> _known;
    public RecordingEffectPersistence(EpisodeTransactionStore episodes, Action wakeEpisodes,
        Func<DurableRuntimeInput, IReadOnlyList<object>, Task> raw, Func<IReadOnlyList<AirportFacility>>? known = null)
    { _episodes = episodes; _wakeEpisodes = wakeEpisodes; _raw = raw; _known = known ?? (() => Array.Empty<AirportFacility>()); }
    public async Task CommitAsync(DurableRuntimeInput input, RecordingRuntimeEffects effects)
    {
        if (effects.QuarantineReason != null) PersistQuarantine(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_episodes.Root)!, "input-quarantine"), input, effects.QuarantineReason);
        foreach (var capture in effects.Captures)
        {
            _episodes.CommitInputs(capture.Episode, capture.TimestampUtc, _known(), capture.AutomaticAllowed, capture.PolicyRevision);
            EpisodeTimeline.Mark(System.IO.Path.GetDirectoryName(_episodes.Root)!, capture.Episode.PersistentId,
                EpisodeTimelinePhase.CaptureCommitted, capture.Episode.Samples.Count);
            if (capture.Episode.IsDiagnosticOnly)
                _episodes.CommitFacilities(new RuntimeFinalFacilities(capture.Episode.PersistentId, capture.Episode.InitialFacilities, capture.Episode.DecisionJournal?.Snapshot()));
        }
        foreach (var final in effects.Facilities) _episodes.CommitFacilities(final);
        if (effects.RawCapture.Count != 0) await _raw(input, effects.RawCapture).ConfigureAwait(false);
        if (effects.Captures.Count != 0 || effects.Facilities.Count != 0) _wakeEpisodes();
    }
    internal static void PersistQuarantine(string root, DurableRuntimeInput input, string reason)
    {
        var bytes = input.Input.Encode();
        DurableFile.Write(System.IO.Path.Combine(root, input.Input.Id.ToString("N") + ".bin"), output =>
        {
            using var writer = new System.IO.BinaryWriter(output, System.Text.Encoding.UTF8, true);
            writer.Write((ushort)input.Input.Kind); writer.Write(input.Cursor.Segment); writer.Write(input.Cursor.Sequence);
            writer.Write(input.Input.AcceptedQpc); writer.Write(reason.Length > 4096 ? reason.Substring(0, 4096) : reason);
            writer.Write(bytes.Length); writer.Write(bytes);
        });
    }
}
