using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Runtime;

namespace LandingStats.App.Telemetry;

internal sealed partial class EpisodeFacilityCoordinator
{
    internal long MaximumRetainedSequence => _approachSamples
        .Concat(_episodeFacilityCompletion?.Task.Status == TaskStatus.RanToCompletion
            ? _episodeFacilityCompletion.Task.Result.ApproachSamples : Array.Empty<Core.TelemetrySample>())
        .Select(sample => sample.Sequence).DefaultIfEmpty(0).Max();
    internal byte[] CaptureCheckpoint()
    {
        var progress = QuerySnapshot;
        var preserving = _runwayLookupIsPreliminary && !_runwayStageCompleted;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0x3146454c); writer.Write(_runwayEpisodeId); writer.Write(_runwayDecisionTime);
        RuntimeCheckpointCodec.WriteJournal(writer, _runwayDecisionJournal);
        writer.Write(_runwayLookupIsPreliminary); writer.Write(_runwayStageCompleted); writer.Write(preserving ? progress.CandidatesComplete : _preliminaryRunwayCandidatesComplete);
        RuntimeCheckpointCodec.WriteJson(writer, new FacilityPersistenceData
        { PreliminaryRunways = (preserving ? progress.Runways : _preliminaryRunways).ToArray(), PreliminaryAirports = (preserving ? progress.Airports : _preliminaryAirports).ToArray() });
        RuntimeCheckpointCodec.WriteSamples(writer, _approachSamples);
        RuntimeCheckpointCodec.WriteJson(writer, SceneryCheckpointData.Capture(QuerySnapshot));
        writer.Write(_episodeFacilityCompletion != null);
        var completed = _episodeFacilityCompletion?.Task.Status == TaskStatus.RanToCompletion;
        writer.Write(completed);
        if (completed)
        {
            var result = _episodeFacilityCompletion!.Task.Result;
            writer.Write(result.EpisodeId); RuntimeCheckpointCodec.WriteJson(writer, FacilityPersistenceData.Capture(result));
            RuntimeCheckpointCodec.WriteSamples(writer, result.ApproachSamples);
        }
        return stream.ToArray();
    }
    internal static EpisodeFacilityCoordinator RestoreCheckpoint(byte[] bytes, ISceneryQueryPort query, bool queryRestored = false)
    {
        try { return RestoreCheckpointCore(bytes, query, queryRestored); }
        catch (Exception error) when (error is EndOfStreamException || error is ArgumentException || error is System.Runtime.Serialization.SerializationException)
        { throw new InvalidDataException("Malformed facility coordinator checkpoint.", error); }
    }
    private static EpisodeFacilityCoordinator RestoreCheckpointCore(byte[] bytes, ISceneryQueryPort query, bool queryRestored)
    {
        if (bytes == null || bytes.Length > 192 * 1024 * 1024) throw new InvalidDataException("Facility checkpoint exceeds its bound.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 0x3146454c) throw new InvalidDataException("Unsupported facility checkpoint.");
        var value = new EpisodeFacilityCoordinator(query) { _runwayEpisodeId = reader.ReadInt64(), _runwayDecisionTime = reader.ReadDouble() };
        value._runwayDecisionJournal = RuntimeCheckpointCodec.ReadJournal(reader);
        value._runwayLookupIsPreliminary = RuntimeCheckpointCodec.ReadBoolean(reader); value._runwayStageCompleted = RuntimeCheckpointCodec.ReadBoolean(reader);
        value._preliminaryRunwayCandidatesComplete = RuntimeCheckpointCodec.ReadBoolean(reader);
        var retained = RuntimeCheckpointCodec.ReadJson<FacilityPersistenceData>(reader).Restore(value._runwayEpisodeId, Array.Empty<Core.TelemetrySample>());
        value._preliminaryRunways = retained.PreliminaryRunways; value._preliminaryAirports = retained.PreliminaryAirports;
        value._approachSamples = RuntimeCheckpointCodec.ReadSamples(reader);
        value._restoredQuerySnapshot = RuntimeCheckpointCodec.ReadJson<SceneryCheckpointData>(reader).Restore();
        if (queryRestored) value._restoredQuerySnapshot = null;
        var hasPromise = RuntimeCheckpointCodec.ReadBoolean(reader); var completed = RuntimeCheckpointCodec.ReadBoolean(reader);
        if (completed && !hasPromise) throw new InvalidDataException("Facility checkpoint completed a nonexistent promise.");
        if (hasPromise)
        {
            value._episodeFacilityCompletion = new TaskCompletionSource<EpisodeFacilitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            value._runwaySnapshotCompletion = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (completed)
            {
                var episode = reader.ReadInt64(); var metadata = RuntimeCheckpointCodec.ReadJson<FacilityPersistenceData>(reader);
                var result = metadata.Restore(episode, RuntimeCheckpointCodec.ReadSamples(reader));
                value._episodeFacilityCompletion.TrySetResult(result);
                value._runwaySnapshotCompletion.TrySetResult(result.FinalRunwayCandidatesComplete && result.FinalRunways.Count > 0 ? result.FinalRunways :
                    result.PreliminaryRunwayCandidatesComplete ? result.PreliminaryRunways : Array.Empty<RunwayFacility>());
            }
        }
        if (stream.Position != stream.Length || value._runwayEpisodeId < 0) throw new InvalidDataException("Invalid facility checkpoint state.");
        // Query handles are never revived from disk. The runtime either submits
        // a fresh lookup in a proven matching context or settles this snapshot
        // through CompleteWithoutRefresh when its source generation is gone.
        return value;
    }
}

[DataContract]
internal sealed class SceneryCheckpointData
{
    [DataMember(Order = 1)] public RunwayFacility[] Runways { get; set; } = Array.Empty<RunwayFacility>();
    [DataMember(Order = 2)] public AirportFacility[] Airports { get; set; } = Array.Empty<AirportFacility>();
    [DataMember(Order = 3)] public bool Fresh { get; set; }
    [DataMember(Order = 4)] public bool Complete { get; set; }
    [DataMember(Order = 5)] public int Requested { get; set; }
    [DataMember(Order = 6)] public int Completed { get; set; }
    public static SceneryCheckpointData Capture(SceneryQuerySnapshot source) => new SceneryCheckpointData
    { Runways = source.Runways.ToArray(), Airports = source.Airports.ToArray(), Fresh = source.FreshAirportsReceived,
        Complete = source.CandidatesComplete, Requested = source.RequestedAirports, Completed = source.CompletedAirports };
    public SceneryQuerySnapshot Restore()
    {
        if (Runways == null || Airports == null || Runways.Length > 65536 || Airports.Length > 65536 || Requested < 0 || Completed < 0 || Completed > Requested)
            throw new InvalidDataException("Invalid scenery progress snapshot.");
        var frozen = new EpisodeFacilitySnapshot(0, Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), Array.Empty<Core.TelemetrySample>(), Runways, Airports, Fresh);
        return new SceneryQuerySnapshot(frozen.FinalRunways, frozen.FinalAirports, Fresh, Complete, Requested, Completed);
    }
}
