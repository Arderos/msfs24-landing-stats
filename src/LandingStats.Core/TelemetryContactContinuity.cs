using System;
using System.IO;

namespace LandingStats.Core;

/// <summary>A contact requires airborne evidence in the same uninterrupted physics segment.</summary>
public sealed class TelemetryContactContinuity
{
    private bool _airborne;
    private bool _requiresReleasedInterval;
    private ReplaySourceEvidence? _blockedSource;
    private ReplaySourceEvidence? _releasedAirSource;
    private double _releasedAirTime = double.NaN;
    private long _releasedAirSegment;

    public string RejectionCode { get; private set; } = "physics_discontinuity_rejected";
    public string RejectionReason { get; private set; } = "Ground transition has no airborne evidence in the current active-physics segment.";

    public bool Observe(TelemetrySample sample)
    {
        RejectionCode = "physics_discontinuity_rejected";
        RejectionReason = "Ground transition has no airborne evidence in the current active-physics segment.";
        if (TelemetryContactPhysics.IsBlocked(sample))
        {
            _requiresReleasedInterval = true;
            _blockedSource = sample.SourceEvidence;
            Reset();
            RejectionCode = "source_controlled_contact_rejected";
            RejectionReason = "Frozen, slewing or SDK replay physics cannot establish an airborne witness or touchdown.";
            return false;
        }
        if (_requiresReleasedInterval)
        {
            RejectionCode = "source_release_airborne_interval_required";
            RejectionReason = "After external physics control, require an airborne interval with both endpoints in freshly released physics; retain the unverified transition as diagnostics.";
            var evidence = sample.SourceEvidence;
            var time = TelemetryAnalysisFrames.TimeOf(sample);
            var fresh = TelemetryContactPhysics.IsFreshReleased(sample) &&
                (_blockedSource == null || !evidence!.SameSource(_blockedSource) || evidence.ObservedQpc > _blockedSource.ObservedQpc);
            if (!fresh || !sample.MotionSimulation || sample.OnGround ||
                sample.SimulatorState == TelemetrySimulatorState.Paused || sample.SimulatorState == TelemetrySimulatorState.Inactive ||
                !AnalysisNumericPolicy.IsFinite(time))
            {
                Reset();
                return false;
            }
            // The first released airborne point starts an interval. Its preceding
            // endpoint can still belong to frozen/buffered physics. Only a later
            // distinct point proves an interval wholly after the interruption.
            // This adds no speed, height or minimum elapsed-time requirement.
            var interval = _releasedAirSource != null && evidence!.SameSource(_releasedAirSource) &&
                evidence.FrameQpc > _releasedAirSource.FrameQpc && time > _releasedAirTime &&
                sample.FlightSegment == _releasedAirSegment;
            _releasedAirSource = evidence;
            _releasedAirTime = time;
            _releasedAirSegment = sample.FlightSegment;
            _airborne = interval;
            if (interval)
            {
                _requiresReleasedInterval = false;
                _blockedSource = _releasedAirSource = null;
                _releasedAirTime = double.NaN;
                _releasedAirSegment = 0;
            }
            return false;
        }
        var contact = _airborne && sample.MotionSimulation && sample.OnGround;
        _airborne = sample.MotionSimulation && !sample.OnGround;
        return contact;
    }

    public void Reset()
    {
        _airborne = false;
        _releasedAirSource = null;
        _releasedAirTime = double.NaN;
        _releasedAirSegment = 0;
    }
    public bool HasAirborneWitness => _airborne;
    public void RestoreAirborneWitness(bool airborne) => _airborne = airborne;

    public void WriteSourceCheckpoint(BinaryWriter writer)
    {
        writer.Write(_requiresReleasedInterval);
        writer.Write(_blockedSource != null);
        if (_blockedSource != null) ReplaySourceEvidenceCodec.Write(writer, _blockedSource);
        writer.Write(_releasedAirSource != null);
        if (_releasedAirSource != null)
        {
            ReplaySourceEvidenceCodec.Write(writer, _releasedAirSource);
            writer.Write(_releasedAirTime);
            writer.Write(_releasedAirSegment);
        }
    }

    public void RestoreSourceCheckpoint(BinaryReader reader)
    {
        _requiresReleasedInterval = ReadBoolean(reader);
        _blockedSource = ReadBoolean(reader) ? ReplaySourceEvidenceCodec.Read(reader) : null;
        _releasedAirSource = ReadBoolean(reader) ? ReplaySourceEvidenceCodec.Read(reader) : null;
        _releasedAirTime = _releasedAirSource != null ? reader.ReadDouble() : double.NaN;
        _releasedAirSegment = _releasedAirSource != null ? reader.ReadInt64() : 0;
        if (_requiresReleasedInterval != (_blockedSource != null) || (_requiresReleasedInterval && _airborne) ||
            (_releasedAirSource != null && (!_requiresReleasedInterval || !TelemetryContactPhysics.IsFreshReleased(_releasedAirSource) ||
                !AnalysisNumericPolicy.IsFinite(_releasedAirTime) || _releasedAirSegment < 0)))
            throw new InvalidDataException("Invalid contact source-continuity checkpoint.");
    }

    private static bool ReadBoolean(BinaryReader reader)
    {
        var value = reader.ReadByte();
        if (value > 1) throw new InvalidDataException("Invalid contact source-continuity flag.");
        return value != 0;
    }
}
