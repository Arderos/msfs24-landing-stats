using System;
using System.Collections.Generic;
using System.IO;
using LandingStats.App.Runtime;

namespace LandingStats.App.Telemetry;

internal sealed partial class LandingEpisodeCoordinator
{
    // Checkpoints predating the parent-owned counter still carry sequence values
    // in their retained samples. A rotated journal cursor is not their maximum.
    internal long MaximumRetainedSequence
    {
        get
        {
            var maximum = Math.Max(_lastKinematicFrame?.Sequence ?? 0, _lastActiveSdkFrame?.Sequence ?? 0);
            foreach (var sample in _preRoll) maximum = Math.Max(maximum, sample.Sequence);
            if (_episodeSamples != null) foreach (var sample in _episodeSamples) maximum = Math.Max(maximum, sample.Sequence);
            return Math.Max(maximum, _rejectedContactBatcher.MaximumRetainedSequence);
        }
    }
    // This is state only. Ports, clocks, observers and outstanding native tasks
    // are attached by the runtime after it has retired the previous owner.
    internal byte[] CaptureCheckpoint()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0x3243454c); writer.Write(_captureSessionId.ToByteArray()); writer.Write(_stopped);
        writer.Write(_nextEpisodeId); writer.Write(_activeEpisodeId); writer.Write(_activePersistentId.ToByteArray());
        writer.Write(_episodeSamples != null); if (_episodeSamples != null) RuntimeCheckpointCodec.WriteSamples(writer, _episodeSamples);
        RuntimeCheckpointCodec.WriteJournal(writer, _episodeDecisionJournal);
        writer.Write(_episodeCoalescedFrameCount); writer.Write(_episodeNonMotionFrameCount); writer.Write(_rawDebugEnabled);
        writer.Write(_lastSimulationTime); writer.Write(_episodeStartTime); writer.Write(_lastContactTime.HasValue); if (_lastContactTime.HasValue) writer.Write(_lastContactTime.Value);
        writer.Write(_contactContinuity.HasAirborneWitness); writer.Write(_previousOnGround); writer.Write(_armed); writer.Write(_replayActive);
        RuntimeCheckpointCodec.WriteNullableBool(writer, _aircraftOnGround);
        foreach (var value in new[] { _simulator, _aircraftTitle, _aircraftType, _aircraftModel, _episodeSimulator, _episodeAircraftTitle, _episodeAircraftType, _episodeAircraftModel })
            RuntimeCheckpointCodec.WriteText(writer, value);
        writer.Write(_episodeControlSources.Count); foreach (var value in _episodeControlSources) RuntimeCheckpointCodec.WriteText(writer, value);
        RuntimeCheckpointCodec.WriteSample(writer, _lastKinematicFrame); _preRoll.WriteCheckpoint(writer); _rejectedContactBatcher.WriteCheckpoint(writer);
        writer.Write(_sdkStateKnown); RuntimeCheckpointCodec.WriteNullableBool(writer, _sdkSimRunning); writer.Write(_sdkPauseFlags);
        writer.Write((int)_flightBlock); writer.Write((int)_cancelledFlightBlocks); writer.Write(_sdkFlightSeen); writer.Write(_sdkSuspended);
        writer.Write(_pauseFramesObserved); writer.Write(_pauseClockAdvanced); writer.Write(_firstPausedRawTime); writer.Write(_lastPausedRawTime);
        writer.Write(_multiplePausedFrames); writer.Write(_pausedClockOffset); RuntimeCheckpointCodec.WriteSample(writer, _lastActiveSdkFrame);
        writer.Write(_flightSegment); RuntimeCheckpointCodec.WriteJournal(writer, _pendingLifecycleJournal);
        writer.Write(_axisElevatorResetHostSeconds);
        return stream.ToArray();
    }
    internal static LandingEpisodeCoordinator RestoreCheckpoint(byte[] bytes, IEpisodeFacilityPort facilities, Func<double> hostSeconds,
        Func<IReadOnlyList<string>> controlSources, Func<DateTime>? utcNow = null)
    {
        try { return RestoreCheckpointCore(bytes, facilities, hostSeconds, controlSources, utcNow); }
        catch (Exception error) when (error is EndOfStreamException || error is ArgumentException || error is System.Runtime.Serialization.SerializationException)
        { throw new InvalidDataException("Malformed recording coordinator checkpoint.", error); }
    }
    private static LandingEpisodeCoordinator RestoreCheckpointCore(byte[] bytes, IEpisodeFacilityPort facilities, Func<double> hostSeconds,
        Func<IReadOnlyList<string>> controlSources, Func<DateTime>? utcNow)
    {
        if (bytes == null || bytes.Length > 192 * 1024 * 1024) throw new InvalidDataException("Coordinator checkpoint exceeds its bound.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        var version = reader.ReadInt32();
        if (version != 0x3143454c && version != 0x3243454c) throw new InvalidDataException("Unsupported coordinator checkpoint.");
        var capture = new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16));
        if (capture == Guid.Empty) throw new InvalidDataException("Coordinator checkpoint has no capture session.");
        var value = new LandingEpisodeCoordinator(facilities, hostSeconds, controlSources, utcNow, capture)
        {
            _stopped = RuntimeCheckpointCodec.ReadBoolean(reader), _nextEpisodeId = reader.ReadInt64(), _activeEpisodeId = reader.ReadInt64(),
            _activePersistentId = new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16))
        };
        if (RuntimeCheckpointCodec.ReadBoolean(reader)) value._episodeSamples = new List<Core.TelemetrySample>(RuntimeCheckpointCodec.ReadSamples(reader));
        value._episodeDecisionJournal = RuntimeCheckpointCodec.ReadJournal(reader);
        value._episodeCoalescedFrameCount = RuntimeCheckpointCodec.ReadCount(reader, int.MaxValue); value._episodeNonMotionFrameCount = RuntimeCheckpointCodec.ReadCount(reader, int.MaxValue);
        value._rawDebugEnabled = RuntimeCheckpointCodec.ReadBoolean(reader); value._lastSimulationTime = reader.ReadDouble(); value._episodeStartTime = reader.ReadDouble();
        value._lastContactTime = RuntimeCheckpointCodec.ReadBoolean(reader) ? reader.ReadDouble() : (double?)null;
        value._contactContinuity.RestoreAirborneWitness(RuntimeCheckpointCodec.ReadBoolean(reader)); value._previousOnGround = RuntimeCheckpointCodec.ReadBoolean(reader);
        value._armed = RuntimeCheckpointCodec.ReadBoolean(reader); value._replayActive = RuntimeCheckpointCodec.ReadBoolean(reader);
        value._aircraftOnGround = RuntimeCheckpointCodec.ReadNullableBool(reader);
        value._simulator = RuntimeCheckpointCodec.ReadText(reader); value._aircraftTitle = RuntimeCheckpointCodec.ReadText(reader);
        value._aircraftType = RuntimeCheckpointCodec.ReadText(reader); value._aircraftModel = RuntimeCheckpointCodec.ReadText(reader);
        value._episodeSimulator = RuntimeCheckpointCodec.ReadText(reader); value._episodeAircraftTitle = RuntimeCheckpointCodec.ReadText(reader);
        value._episodeAircraftType = RuntimeCheckpointCodec.ReadText(reader); value._episodeAircraftModel = RuntimeCheckpointCodec.ReadText(reader);
        var sources = new string[RuntimeCheckpointCodec.ReadCount(reader, 64)]; for (var i = 0; i < sources.Length; i++) sources[i] = RuntimeCheckpointCodec.ReadText(reader);
        value._episodeControlSources = sources;
        value._lastKinematicFrame = RuntimeCheckpointCodec.ReadSample(reader); value._preRoll.RestoreCheckpoint(reader); value._rejectedContactBatcher.RestoreCheckpoint(reader);
        value._sdkStateKnown = RuntimeCheckpointCodec.ReadBoolean(reader); value._sdkSimRunning = RuntimeCheckpointCodec.ReadNullableBool(reader); value._sdkPauseFlags = reader.ReadInt32();
        value._flightBlock = (FlightBlock)RuntimeCheckpointCodec.ReadCount(reader, 127); value._cancelledFlightBlocks = (FlightBlock)RuntimeCheckpointCodec.ReadCount(reader, 127);
        value._sdkFlightSeen = RuntimeCheckpointCodec.ReadBoolean(reader); value._sdkSuspended = RuntimeCheckpointCodec.ReadBoolean(reader);
        value._pauseFramesObserved = RuntimeCheckpointCodec.ReadBoolean(reader); value._pauseClockAdvanced = RuntimeCheckpointCodec.ReadBoolean(reader);
        value._firstPausedRawTime = reader.ReadDouble(); value._lastPausedRawTime = reader.ReadDouble(); value._multiplePausedFrames = RuntimeCheckpointCodec.ReadBoolean(reader);
        value._pausedClockOffset = reader.ReadDouble(); value._lastActiveSdkFrame = RuntimeCheckpointCodec.ReadSample(reader); value._flightSegment = reader.ReadInt64();
        value._pendingLifecycleJournal = RuntimeCheckpointCodec.ReadJournal(reader) ?? throw new InvalidDataException("Missing lifecycle journal.");
        if (version >= 0x3243454c) value._axisElevatorResetHostSeconds = reader.ReadDouble();
        if (double.IsInfinity(value._axisElevatorResetHostSeconds) || value._axisElevatorResetHostSeconds < 0)
            throw new InvalidDataException("Invalid simulator control reset boundary.");
        if (stream.Position != stream.Length || value._nextEpisodeId < 0 || value._activeEpisodeId < 0 || value._activeEpisodeId > value._nextEpisodeId || value._flightSegment < 0 ||
            (value._activeEpisodeId == 0) != (value._episodeSamples == null) || (value._activeEpisodeId == 0) != (value._activePersistentId == Guid.Empty) ||
            (value._activeEpisodeId == 0) != (value._episodeDecisionJournal == null) || (value._lastContactTime.HasValue && value._episodeSamples == null))
            throw new InvalidDataException("Invalid coordinator checkpoint state.");
        if (facilities.EpisodeId == value._activeEpisodeId && value._activeEpisodeId != 0)
            facilities.SetDecisionContext(value._episodeDecisionJournal, facilities.DecisionTime);
        return value;
    }
}
