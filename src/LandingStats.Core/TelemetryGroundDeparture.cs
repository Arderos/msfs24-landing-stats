using System;
using System.IO;

namespace LandingStats.Core;

/// <summary>Grounded position history must support the departure that arms a new contact.</summary>
internal sealed class TelemetryGroundDeparture
{
    private const double FeetToMeters = .3048;
    private const double KnotsToMetersPerSecond = 1852.0 / 3600.0;
    private GroundFrame? _previous, _last;
    private GroundFrame? _revisionHistory, _revisionPrevious;
    private bool _replacedBeforeRevision;
    private RevisionBoundary? _groundBoundary, _departureBoundary;
    private GroundFrame? _departureHistory, _departurePrevious, _departureFrame;
    private bool _departureFixedRejection;
    private readonly TelemetryCumulativeMotion _cumulative = new TelemetryCumulativeMotion();
    private bool _replacedOnGround, _unverifiedAirborne;
    private bool _pendingCumulativeDeparture;
    private long _unverifiedSegment;
    private double _unverifiedStartTime;

    public bool HasUnverifiedAirborneRun => _unverifiedAirborne;
    public bool HasPendingGroundReplacement => _replacedOnGround;

    // An unexplained ground-to-air step invalidates this airborne run. Later
    // airborne flags from settling cannot re-arm it. Its ground endpoint ends
    // the rejected run; a subsequent physical departure can arm a new contact.
    public bool Observe(TelemetrySample sample)
    {
        var active = sample.MotionSimulation && sample.SimulatorState != TelemetrySimulatorState.Inactive &&
            sample.SimulatorState != TelemetrySimulatorState.Paused;
        var time = TelemetryAnalysisFrames.TimeOf(sample);
        if (_departureFrame != null)
        {
            if (active && !sample.OnGround && SameTime(time, _departureFrame.Time) &&
                sample.FlightSegment == _departureFrame.Segment && _departureBoundary!.Matches(sample))
            {
                // A first AIR delivery can revise the same departure in either
                // direction. Keep its grounded interval until that instant ends;
                // source/discrete changes retain the already observed boundary.
                var revised = new GroundFrame(sample);
                if (Finite(revised.Acceleration))
                {
                    _cumulative.Observe(sample);
                    _departureBoundary = new RevisionBoundary(sample);
                    return EvaluateDeparture(revised);
                }
            }
            ClearDepartureRevision();
        }
        if (_unverifiedAirborne)
        {
            if (sample.FlightSegment != _unverifiedSegment ||
                (Finite(time) && time < _unverifiedStartTime && !SameTime(time, _unverifiedStartTime))) Reset();
            else
            {
                // Missing/inactive observations do not retroactively validate the
                // departure. A new segment or an actual ground endpoint ends it.
                if (active && sample.OnGround && Finite(time)) { Reset(); _last = new GroundFrame(sample); _cumulative.Observe(sample); }
                return true;
            }
        }
        if (!active || !Finite(time)) { ResetHistory(); return false; }
        if (_last != null && (sample.FlightSegment != _last.Segment ||
            (time < _last.Time && !SameTime(time, _last.Time)))) ResetHistory();
        var pendingCumulative = _pendingCumulativeDeparture;
        var cumulativeReplacement = _cumulative.Observe(sample);
        pendingCumulative &= _cumulative.HasRetainedEvidence;
        if (!_cumulative.HasRetainedEvidence) _pendingCumulativeDeparture = false;
        if (!sample.OnGround) _pendingCumulativeDeparture = false;
        else if (_last == null || !SameTime(time, _last.Time)) _pendingCumulativeDeparture = cumulativeReplacement;
        if (_last == null && cumulativeReplacement)
        {
            if (sample.OnGround) { Reset(); _last = new GroundFrame(sample); _cumulative.Observe(sample); }
            else { _unverifiedAirborne = true; _unverifiedSegment = sample.FlightSegment; _unverifiedStartTime = time; }
            return true;
        }
        if (!sample.OnGround && _last == null) return false;

        var frame = new GroundFrame(sample);
        if (!Finite(frame.Acceleration)) { ResetHistory(); return false; }

        if (!sample.OnGround)
        {
            _departureHistory = _previous;
            _departurePrevious = _last;
            _departureBoundary = new RevisionBoundary(sample);
            _departureFixedRejection = _replacedOnGround || pendingCumulative || cumulativeReplacement;
            _previous = _last = null;
            _revisionHistory = _revisionPrevious = null;
            _groundBoundary = null;
            _replacedBeforeRevision = false;
            _replacedOnGround = false;
            return EvaluateDeparture(frame);
        }

        var sameTime = _last != null && SameTime(frame.Time, _last.Time);
        if (!sameTime || !SamePosition(_last!, frame) || (_groundBoundary != null && !_groundBoundary.Matches(sample)))
        {
            // Numeric revisions of one grounded pose reuse its original motion
            // interval. Distinct same-time positions are separate evidence and
            // retain the result of the preceding pose before starting a new one.
            _revisionHistory = _previous;
            _revisionPrevious = _last;
            _replacedBeforeRevision = sameTime && _replacedOnGround;
        }
        _replacedOnGround = _replacedBeforeRevision || (_revisionHistory != null && _revisionPrevious != null &&
            Replaced(_revisionHistory, _revisionPrevious, frame));
        if (_last != null && !sameTime) _previous = _last;
        _last = frame;
        _groundBoundary = new RevisionBoundary(sample);
        return false;
    }

    private bool EvaluateDeparture(GroundFrame frame)
    {
        _departureFrame = frame;
        _unverifiedAirborne = _departureFixedRejection || (_departureHistory != null && _departurePrevious != null &&
            Replaced(_departureHistory, _departurePrevious, frame));
        _unverifiedSegment = _unverifiedAirborne ? frame.Segment : 0;
        _unverifiedStartTime = _unverifiedAirborne ? frame.Time : 0;
        return _unverifiedAirborne;
    }

    private void ClearDepartureRevision()
    {
        _departureHistory = _departurePrevious = _departureFrame = null;
        _departureBoundary = null;
        _departureFixedRejection = false;
    }

    public void Reset()
    { ResetHistory(); _unverifiedAirborne = false; _unverifiedSegment = 0; _unverifiedStartTime = 0; }

    public void ResetHistory()
    {
        _previous = _last = _revisionHistory = _revisionPrevious = null;
        _groundBoundary = null;
        ClearDepartureRevision();
        _replacedOnGround = _replacedBeforeRevision = _pendingCumulativeDeparture = false;
        _cumulative.Reset();
    }

    public void RestrictCumulativeHistory(double firstRetainedTime)
    {
        _cumulative.RestrictHistory(firstRetainedTime);
        _pendingCumulativeDeparture &= _cumulative.HasRetainedEvidence;
    }

    public void WriteCumulativeCheckpoint(BinaryWriter writer)
    { writer.Write(_pendingCumulativeDeparture); _cumulative.WriteCheckpoint(writer); }
    public void RestoreCumulativeCheckpoint(BinaryReader reader)
    {
        _pendingCumulativeDeparture = ReadBoolean(reader);
        _cumulative.RestoreCheckpoint(reader);
        if (_pendingCumulativeDeparture && (_last == null || _unverifiedAirborne || !_cumulative.HasRetainedEvidence))
            throw new InvalidDataException("Invalid pending cumulative departure checkpoint.");
    }

    private static bool Replaced(GroundFrame history, GroundFrame previous, GroundFrame current)
    {
        var priorDuration = previous.Time - history.Time;
        var duration = current.Time - previous.Time;
        if (priorDuration <= TelemetryDeduplicator.TimeToleranceSeconds ||
            duration < -TelemetryDeduplicator.TimeToleranceSeconds || history.Segment != previous.Segment ||
            previous.Segment != current.Segment) return false;
        duration = Math.Max(0, duration);

        var interval = duration + TelemetryKinematicDiscontinuity.ChannelAlignmentSeconds;
        var acceleration = Math.Max(TelemetryKinematicDiscontinuity.MinimumAccelerationAllowanceMetersPerSecondSquared,
            Math.Max(previous.Acceleration, current.Acceleration));
        var accelerationDistance = .5 * acceleration * interval * interval;
        var horizontalSpeed = MaximumSpeed(previous.GroundSpeed, current.GroundSpeed);
        var priorHorizontalSpeed = MaximumSpeed(history.GroundSpeed, previous.GroundSpeed);

        if (PositionAvailable(history) && PositionAvailable(previous) && PositionAvailable(current) &&
            Finite(horizontalSpeed) && Finite(priorHorizontalSpeed))
        {
            // The preceding grounded interval supplies the local channel/position
            // mismatch. Persistently inconsistent channels cannot turn every row
            // into a teleport; an isolated new displacement must exceed that
            // mismatch as well as speed, channel alignment and acceleration.
            var uncertainty = Math.Max(0, HorizontalDistance(history, previous) - priorHorizontalSpeed * priorDuration);
            var allowance = uncertainty + horizontalSpeed * interval + accelerationDistance;
            if (HorizontalDistance(previous, current) > allowance) return true;
        }

        if (Finite(history.Altitude) && Finite(previous.Altitude) && Finite(current.Altitude) &&
            Finite(history.VerticalSpeed) && Finite(previous.VerticalSpeed) && Finite(current.VerticalSpeed) &&
            Finite(horizontalSpeed) && Finite(priorHorizontalSpeed))
        {
            var priorVerticalSpeed = Math.Max(Math.Abs(history.VerticalSpeed), Math.Abs(previous.VerticalSpeed));
            var verticalSpeed = Math.Max(Math.Abs(previous.VerticalSpeed), Math.Abs(current.VerticalSpeed));
            var uncertainty = Math.Max(0, Math.Abs(previous.Altitude - history.Altitude) -
                (priorVerticalSpeed + priorHorizontalSpeed) * priorDuration);
            // Horizontal speed retains the existing allowance for reference-point
            // rotation, runway slope and asynchronously published channels.
            var allowance = uncertainty + (verticalSpeed + horizontalSpeed) * interval + accelerationDistance;
            if (Math.Abs(current.Altitude - previous.Altitude) > allowance) return true;
        }
        return false;
    }

    public void WriteCheckpoint(BinaryWriter writer)
    {
        writer.Write(_replacedOnGround); writer.Write(_unverifiedAirborne);
        if (_unverifiedAirborne) { writer.Write(_unverifiedSegment); writer.Write(_unverifiedStartTime); }
        WriteFrame(writer, _previous); WriteFrame(writer, _last);
    }

    public void RestoreCheckpoint(BinaryReader reader)
    {
        _replacedOnGround = ReadBoolean(reader); _unverifiedAirborne = ReadBoolean(reader);
        _unverifiedSegment = _unverifiedAirborne ? reader.ReadInt64() : 0;
        _unverifiedStartTime = _unverifiedAirborne ? reader.ReadDouble() : 0;
        _previous = ReadFrame(reader); _last = ReadFrame(reader);
        if ((_previous != null && (_last == null || _previous.Time >= _last.Time || _previous.Segment != _last.Segment)) ||
            (_replacedOnGround && _last == null) || (_unverifiedAirborne && (_last != null || _replacedOnGround ||
                _unverifiedSegment < 0 || !Finite(_unverifiedStartTime))))
            throw new InvalidDataException("Invalid ground-departure checkpoint.");
        // Older checkpoints have no original interval for revising their last
        // pose. Preserve its existing decision until a new interval is observed.
        _revisionHistory = _revisionPrevious = null;
        _replacedBeforeRevision = _replacedOnGround;
        _groundBoundary = null;
        ClearDepartureRevision();
    }

    public void WriteRevisionCheckpoint(BinaryWriter writer)
    {
        writer.Write(_replacedBeforeRevision);
        WriteFrame(writer, _revisionHistory); WriteFrame(writer, _revisionPrevious);
        WriteBoundary(writer, _groundBoundary);
        WriteFrame(writer, _departureHistory); WriteFrame(writer, _departurePrevious); WriteFrame(writer, _departureFrame);
        writer.Write(_departureFixedRejection); WriteBoundary(writer, _departureBoundary);
    }

    public void RestoreRevisionCheckpoint(BinaryReader reader)
    {
        _replacedBeforeRevision = ReadBoolean(reader);
        _revisionHistory = ReadFrame(reader); _revisionPrevious = ReadFrame(reader);
        _groundBoundary = ReadBoundary(reader);
        _departureHistory = ReadFrame(reader); _departurePrevious = ReadFrame(reader); _departureFrame = ReadFrame(reader);
        _departureFixedRejection = ReadBoolean(reader); _departureBoundary = ReadBoundary(reader);
        if ((_revisionPrevious != null && (_last == null || (_revisionPrevious.Time > _last.Time &&
                !SameTime(_revisionPrevious.Time, _last.Time)) ||
                _revisionPrevious.Segment != _last.Segment)) ||
            (_revisionHistory != null && (_revisionPrevious == null || _revisionHistory.Time >= _revisionPrevious.Time ||
                _revisionHistory.Segment != _revisionPrevious.Segment)) ||
            (_replacedBeforeRevision && (_last == null || !_replacedOnGround)) ||
            (_unverifiedAirborne && (_revisionHistory != null || _revisionPrevious != null || _replacedBeforeRevision)) ||
            (_groundBoundary != null && _last == null))
            throw new InvalidDataException("Invalid ground-departure revision checkpoint.");
        if (_departureFrame == null)
        {
            if (_departureHistory != null || _departurePrevious != null || _departureBoundary != null || _departureFixedRejection)
                throw new InvalidDataException("Incomplete departure revision checkpoint.");
        }
        else if (_last != null || _previous != null || _departurePrevious == null || _departureBoundary == null ||
            _departurePrevious.Segment != _departureFrame.Segment ||
            (_departurePrevious.Time > _departureFrame.Time && !SameTime(_departurePrevious.Time, _departureFrame.Time)) ||
            (_departureHistory != null && (_departureHistory.Time >= _departurePrevious.Time ||
                _departureHistory.Segment != _departurePrevious.Segment)) ||
            _unverifiedAirborne != (_departureFixedRejection || (_departureHistory != null &&
                Replaced(_departureHistory, _departurePrevious, _departureFrame))))
            throw new InvalidDataException("Invalid departure revision checkpoint.");
    }

    private static void WriteBoundary(BinaryWriter writer, RevisionBoundary? boundary)
    {
        writer.Write(boundary != null);
        if (boundary == null) return;
        writer.Write((int)boundary.SimulatorState); writer.Write(boundary.Source != null);
        if (boundary.Source != null) ReplaySourceEvidenceCodec.Write(writer, boundary.Source);
    }

    private static RevisionBoundary? ReadBoundary(BinaryReader reader)
    {
        if (!ReadBoolean(reader)) return null;
        var state = (TelemetrySimulatorState)reader.ReadInt32();
        if (state != TelemetrySimulatorState.Legacy && state != TelemetrySimulatorState.Running)
            throw new InvalidDataException("Invalid departure revision simulator state.");
        return new RevisionBoundary(state, ReadBoolean(reader) ? ReplaySourceEvidenceCodec.Read(reader) : null);
    }

    private static void WriteFrame(BinaryWriter writer, GroundFrame? frame)
    {
        writer.Write(frame != null);
        if (frame == null) return;
        writer.Write(frame.Time); writer.Write(frame.Segment); writer.Write(frame.Latitude); writer.Write(frame.Longitude);
        writer.Write(frame.Altitude); writer.Write(frame.GroundSpeed); writer.Write(frame.VerticalSpeed); writer.Write(frame.Acceleration);
    }

    private static GroundFrame? ReadFrame(BinaryReader reader)
    {
        if (!ReadBoolean(reader)) return null;
        var frame = new GroundFrame(reader);
        if (!Finite(frame.Time) || frame.Segment < 0 || !Finite(frame.Acceleration) || frame.Acceleration < 0)
            throw new InvalidDataException("Invalid grounded observation checkpoint.");
        return frame;
    }

    private static bool ReadBoolean(BinaryReader reader)
    {
        var value = reader.ReadByte();
        if (value > 1) throw new InvalidDataException("Invalid ground-departure checkpoint flag.");
        return value != 0;
    }

    private static double MaximumSpeed(double a, double b) =>
        Finite(a) && a >= 0 && Finite(b) && b >= 0 ? Math.Max(a, b) : double.NaN;
    private static bool PositionAvailable(GroundFrame frame) => Finite(frame.Latitude) && Finite(frame.Longitude) &&
        Math.Abs(frame.Latitude) <= 90 && Math.Abs(frame.Longitude) <= 180 && (frame.Latitude != 0 || frame.Longitude != 0);
    private static bool SamePosition(GroundFrame first, GroundFrame second) =>
        first.Latitude.Equals(second.Latitude) && first.Longitude.Equals(second.Longitude) && first.Altitude.Equals(second.Altitude);
    private static bool SameTime(double first, double second) => Math.Abs(first - second) <= TelemetryDeduplicator.TimeToleranceSeconds;
    private static double HorizontalDistance(GroundFrame a, GroundFrame b) =>
        GeoDistance.GreatCircleNauticalMiles(a.Latitude, a.Longitude, b.Latitude, b.Longitude) * 1852;
    private static bool Finite(double value) => AnalysisNumericPolicy.IsFinite(value);

    private sealed class RevisionBoundary
    {
        internal readonly TelemetrySimulatorState SimulatorState;
        internal readonly ReplaySourceEvidence? Source;
        internal RevisionBoundary(TelemetrySample sample) : this(sample.SimulatorState, sample.SourceEvidence) { }
        internal RevisionBoundary(TelemetrySimulatorState state, ReplaySourceEvidence? source)
        { SimulatorState = state; Source = source; }
        internal bool Matches(TelemetrySample sample) => SimulatorState == sample.SimulatorState &&
            TelemetryDeduplicator.SameSourceControlState(Source, sample.SourceEvidence);
    }

    private sealed class GroundFrame
    {
        internal readonly double Time, Latitude, Longitude, Altitude, GroundSpeed, VerticalSpeed, Acceleration;
        internal readonly long Segment;
        internal GroundFrame(TelemetrySample sample)
        {
            Time = TelemetryAnalysisFrames.TimeOf(sample); Segment = sample.FlightSegment;
            Latitude = sample.LatitudeDegrees; Longitude = sample.LongitudeDegrees;
            Altitude = sample.PlaneAltitudeFeet * FeetToMeters; GroundSpeed = sample.GroundSpeedKnots * KnotsToMetersPerSecond;
            VerticalSpeed = sample.VelocityWorldYFps * FeetToMeters;
            Acceleration = TelemetryKinematicDiscontinuity.AccelerationAllowance(sample);
        }
        internal GroundFrame(BinaryReader reader)
        {
            Time = reader.ReadDouble(); Segment = reader.ReadInt64(); Latitude = reader.ReadDouble(); Longitude = reader.ReadDouble();
            Altitude = reader.ReadDouble(); GroundSpeed = reader.ReadDouble(); VerticalSpeed = reader.ReadDouble(); Acceleration = reader.ReadDouble();
        }
    }
}
