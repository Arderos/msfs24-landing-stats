using System;
using System.IO;

namespace LandingStats.Core;

/// <summary>A changed grounded pose during a pause starts a new motion history.</summary>
internal sealed class TelemetryPauseContinuity
{
    private TelemetrySample? _previous;
    private bool _paused, _changed;
    private double _releasedAirTime = double.NaN;
    private long _releasedAirSegment;
    public bool StartsNewMotion { get; private set; }
    public bool RequiresAirborneInterval { get; private set; }

    public void Observe(TelemetrySample sample)
    {
        StartsNewMotion = false;
        if (sample.SimulatorState == TelemetrySimulatorState.Paused)
        {
            _paused = true;
            _changed = _previous != null && _previous.OnGround && SameSegment(_previous, sample) && PoseChanged(_previous, sample);
            _releasedAirTime = double.NaN;
            return;
        }
        var active = sample.MotionSimulation && sample.SimulatorState != TelemetrySimulatorState.Inactive;
        if (!active)
        {
            _previous = null; _paused = _changed = false; _releasedAirTime = double.NaN;
            return;
        }

        var inferredPause = _previous != null && SameSegment(_previous, sample) &&
            sample.SimulatorState == TelemetrySimulatorState.Running &&
            _previous.SimulatorState == TelemetrySimulatorState.Running &&
            Finite(sample.AnalysisTimeSeconds) && Finite(_previous.AnalysisTimeSeconds) &&
            RawTime(sample) - RawTime(_previous) - (Time(sample) - Time(_previous)) > TelemetryDeduplicator.TimeToleranceSeconds;
        StartsNewMotion = sample.AnalysisPauseReposition || (!sample.AnalysisPauseEvaluated && _previous != null && SameSegment(_previous, sample) &&
            _previous.OnGround && (_changed || (!_paused && inferredPause && OmittedPauseChanged(_previous, sample))));
        if (StartsNewMotion)
        {
            RequiresAirborneInterval = true;
            _releasedAirTime = double.NaN;
        }
        _paused = _changed = false;

        if (RequiresAirborneInterval)
        {
            var time = Time(sample);
            var freshInterval = !StartsNewMotion && _previous != null && Finite(time) &&
                time - Time(_previous) > TelemetryDeduplicator.TimeToleranceSeconds &&
                SameSegment(_previous, sample) && !TelemetryContactPhysics.IsBlocked(sample) &&
                !TelemetryKinematicDiscontinuity.TryDetect(_previous, sample, out _);
            if (sample.OnGround && _previous != null && _previous.OnGround && freshInterval)
            {
                // A resumed ground interval supplies a new departure origin. Do
                // not carry a pause rejection into a later real short bounce.
                RequiresAirborneInterval = false; _releasedAirTime = double.NaN;
            }
            else if (!sample.OnGround && Finite(time) && !TelemetryContactPhysics.IsBlocked(sample))
            {
                // Two distinct active observations establish motion entirely after
                // the relocation. A new approach need not touch ground to re-arm.
                var interval = freshInterval && Finite(_releasedAirTime) &&
                    time - _releasedAirTime > TelemetryDeduplicator.TimeToleranceSeconds && sample.FlightSegment == _releasedAirSegment;
                _releasedAirTime = time; _releasedAirSegment = sample.FlightSegment;
                if (interval) { RequiresAirborneInterval = false; _releasedAirTime = double.NaN; }
            }
            else _releasedAirTime = double.NaN;
        }
        _previous = sample.CopyForAnalysis();
    }

    public void ResetContext()
    {
        _previous = null; _paused = _changed = false; _releasedAirTime = double.NaN;
        StartsNewMotion = false;
    }

    private static bool SameSegment(TelemetrySample a, TelemetrySample b) => a.FlightSegment == b.FlightSegment;
    private static double RawTime(TelemetrySample sample) => sample.RecordedSimulationTimeSeconds ?? sample.SimulationTimeSeconds;
    private static double Time(TelemetrySample sample) => TelemetryAnalysisFrames.TimeOf(sample);
    private static bool Finite(double value) => AnalysisNumericPolicy.IsFinite(value);
    private static bool Changed(double a, double b, double tolerance) => Finite(a) && Finite(b) && Math.Abs(a - b) > tolerance;
    private static bool PoseChanged(TelemetrySample a, TelemetrySample b) =>
        Changed(a.PlaneAltitudeFeet, b.PlaneAltitudeFeet, TelemetryPoseResolution.AltitudeFeet) ||
        Changed(a.LatitudeDegrees, b.LatitudeDegrees, TelemetryPoseResolution.CoordinateDegrees) ||
        Changed(a.LongitudeDegrees, b.LongitudeDegrees, TelemetryPoseResolution.CoordinateDegrees) ||
        Changed(a.PitchDegrees, b.PitchDegrees, TelemetryPoseResolution.AngleDegrees) ||
        Changed(a.BankDegrees, b.BankDegrees, TelemetryPoseResolution.AngleDegrees);

    private static bool OmittedPauseChanged(TelemetrySample previous, TelemetrySample sample)
    {
        // Older pre-rolls omitted paused rows. The clock offset proves a pause;
        // the optional USER-object observation can still describe a pose inside it.
        var evidence = sample.SourceEvidence;
        var observation = evidence?.Observation;
        var lastPausedTime = RawTime(sample) - (Time(sample) - Time(previous));
        if (observation != null && observation.Availability == SourceObservationAvailability.Available &&
            observation.SimulationTime > RawTime(previous) && observation.SimulationTime <= lastPausedTime &&
            (previous.SourceEvidence == null || evidence!.SameSource(previous.SourceEvidence)))
            return Changed(previous.PlaneAltitudeFeet, observation.AltitudeFeet, TelemetryPoseResolution.AltitudeFeet) ||
                Changed(previous.LatitudeDegrees, observation.Latitude, TelemetryPoseResolution.CoordinateDegrees) ||
                Changed(previous.LongitudeDegrees, observation.Longitude, TelemetryPoseResolution.CoordinateDegrees) ||
                Changed(previous.PitchDegrees, observation.Pitch, TelemetryPoseResolution.AngleDegrees) ||
                Changed(previous.BankDegrees, observation.Bank, TelemetryPoseResolution.AngleDegrees);
        // A ground-to-air transition across an unobserved pause needs fresh
        // airborne evidence. Resuming on the ground supplies a new ground origin.
        return !sample.OnGround && PoseChanged(previous, sample);
    }

    public void WriteCheckpoint(BinaryWriter writer)
    {
        writer.Write(_paused); writer.Write(_changed); writer.Write(RequiresAirborneInterval);
        writer.Write(_releasedAirTime); writer.Write(_releasedAirSegment);
        writer.Write(_previous != null);
        if (_previous == null) return;
        // The sample codec keeps the original clocks, not the normalized view.
        var raw = _previous.CopyForAnalysis(); raw.SimulationTimeSeconds = RawTime(_previous);
        writer.Write(TelemetryBinaryCodec.Encode(raw));
        writer.Write(_previous.SourceEvidence != null);
        if (_previous.SourceEvidence != null) ReplaySourceEvidenceCodec.Write(writer, _previous.SourceEvidence);
    }

    public void RestoreCheckpoint(BinaryReader reader)
    {
        _paused = ReadBoolean(reader); _changed = ReadBoolean(reader); RequiresAirborneInterval = ReadBoolean(reader);
        _releasedAirTime = reader.ReadDouble(); _releasedAirSegment = reader.ReadInt64();
        _previous = ReadBoolean(reader) ? TelemetryBinaryCodec.Read(reader) : null;
        if (_previous != null && ReadBoolean(reader)) _previous.SourceEvidence = ReplaySourceEvidenceCodec.Read(reader);
        if (double.IsInfinity(_releasedAirTime) || _releasedAirSegment < 0 ||
            (_previous != null && (!_previous.MotionSimulation || _previous.SimulatorState == TelemetrySimulatorState.Paused ||
                _previous.SimulatorState == TelemetrySimulatorState.Inactive)) ||
            (_changed && (!_paused || _previous == null || !_previous.OnGround)) ||
            (Finite(_releasedAirTime) && (!RequiresAirborneInterval || _previous == null || _previous.OnGround ||
                _releasedAirTime != Time(_previous) || _releasedAirSegment != _previous.FlightSegment)))
            throw new InvalidDataException("Invalid pause contact-continuity checkpoint.");
    }

    private static bool ReadBoolean(BinaryReader reader)
    {
        var value = reader.ReadByte();
        if (value > 1) throw new InvalidDataException("Invalid pause contact-continuity flag.");
        return value != 0;
    }
}
