using System;
using System.Collections.Generic;
using System.IO;

namespace LandingStats.Core;

/// <summary>Bounds accumulated ground-origin motion by the observed velocity envelope.</summary>
internal sealed class TelemetryCumulativeMotion
{
    private const double Alignment = TelemetryKinematicDiscontinuity.ChannelAlignmentSeconds;
    private const double Window = 2 * Alignment;
    private const double MaximumFrameGap = Alignment;
    // An aligned endpoint can need both a rounded-down observation and its
    // preceding publication bracket. Each can add one accepted frame gap.
    private const double MaximumAlignedBracket = Alignment + 2 * MaximumFrameGap;
    private const double MaximumWindowDuration = Window + MaximumFrameGap;
    // Latest -> completed -> end -> start -> baseline -> supportStart.
    // Both windows round down; the baseline adds one further lookup rounding.
    // Retain their worst-case support even when irregular cadence makes the
    // next evaluation's oldest required endpoint move backwards.
    private const double History = MaximumFrameGap + MaximumAlignedBracket +
        2 * MaximumWindowDuration + MaximumFrameGap + MaximumAlignedBracket;
    // This is below the recorder's time bound and no larger than its row bound.
    private const int MaximumFrames = 4096;
    private const double AnglePrecision = TelemetryPoseResolution.AngleDegrees * Math.PI / 180;
    private const double PositionRoundoff = 64 * 2.2204460492503131e-16 * 6371000;
    private readonly List<Frame> _frames = new List<Frame>();
    private double _proofStart = double.NaN;
    internal bool HasRetainedEvidence => Finite(_proofStart) && _frames.Count > 0 && _frames[0].Time <= _proofStart;

    internal bool Observe(TelemetrySample sample)
    {
        var frame = new Frame(sample);
        if (!Finite(frame.Time) || !sample.MotionSimulation || sample.SimulatorState == TelemetrySimulatorState.Inactive ||
            sample.SimulatorState == TelemetrySimulatorState.Paused) { Reset(); return false; }
        if (_frames.Count > 0 && (frame.Segment != _frames[_frames.Count - 1].Segment ||
            frame.Time < _frames[_frames.Count - 1].Time - TelemetryDeduplicator.TimeToleranceSeconds ||
            frame.Time - _frames[_frames.Count - 1].Time > MaximumFrameGap)) Reset();
        if (_frames.Count == 0)
        {
            if (frame.Ground) _frames.Add(frame);
            return false;
        }
        var last = _frames[_frames.Count - 1];
        if (Math.Abs(frame.Time - last.Time) <= TelemetryDeduplicator.TimeToleranceSeconds)
        {
            // Finalize an observation only when time advances. A same-instant
            // velocity correction must have the same effect live and offline.
            _frames[_frames.Count - 1] = frame;
            Trim(frame.Time);
            return false;
        }
        _frames.Add(frame);
        Trim(frame.Time);
        var rejected = Evaluate(last.Time);
        if (frame.Ground && !last.Ground) { Reset(); _frames.Add(frame); }
        return rejected;
    }

    private void Trim(double time)
    {
        var remove = 0;
        while (remove + 1 < _frames.Count && _frames[remove + 1].Time < time - History) remove++;
        remove = Math.Max(remove, _frames.Count - MaximumFrames);
        if (remove > 0) _frames.RemoveRange(0, remove);
    }

    // The recorder owns capture retention. Coalescing numeric revisions must
    // not change eligibility, but proof absent from the future capture cannot
    // reject its departure. Offline analysis already starts at this boundary.
    internal void RestrictHistory(double firstRetainedTime)
    {
        if (!Finite(firstRetainedTime)) { Reset(); return; }
        var remove = 0;
        while (remove < _frames.Count && _frames[remove].Time < firstRetainedTime) remove++;
        if (remove > 0) _frames.RemoveRange(0, remove);
        if (!HasRetainedEvidence) _proofStart = double.NaN;
    }

    internal void Reset() { _frames.Clear(); _proofStart = double.NaN; }

    private bool Evaluate(double completedTime)
    {
        // Retain the next observed frame as the upper endpoint's publication
        // bracket, in addition to the channel-alignment support.
        var end = AtOrBefore(completedTime - Alignment) - 1;
        if (end < 0) return false;
        var start = AtOrBefore(_frames[end].Time - Window);
        if (start < 0) return false;
        var duration = _frames[end].Time - _frames[start].Time;
        var baseline = AtOrBefore(_frames[start].Time - duration);
        if (baseline < 0 || AtOrBefore(_frames[baseline].Time - Alignment) < 0) return false;
        for (var i = baseline; i <= start; i++) if (!_frames[i].Ground) return false;
        if (!TryEnvelope(baseline, start, out var before) || !TryEnvelope(start, end, out var current)) return false;
        var evidenceStart = AtOrBefore(_frames[baseline - 1].Time - Alignment);
        var scale = duration / (_frames[start].Time - _frames[baseline].Time);
        var priorAngle = 0.0;
        var priorHorizontal = 0.0;
        var priorVerticalPath = 0.0;
        for (var i = baseline; i < start; i++)
        {
            // A noisy grounded pose can move back and forth while its endpoint
            // chord stays small. Its observed path supplies the uncertainty;
            // a cancelled jitter must not become evidence of a new departure.
            priorAngle += Rotation(_frames[i], _frames[i + 1]);
            priorHorizontal += Distance(_frames[i], _frames[i + 1]);
            priorVerticalPath += Math.Abs(_frames[i + 1].Altitude - _frames[i].Altitude);
        }
        if (Finite(priorAngle) && Finite(before.Angular) && Finite(current.Angular))
        {
            var allowance = current.Angular;
            var uncertainty = Math.Max(0, priorAngle - before.Angular) * Math.Max(1, scale) + AnglePrecision;
            for (var first = start; first < end; first++)
            {
                // Motion before a pose change cannot pay for that later change.
                // Check every observed suffix of the completed interval, using
                // one precomputed integral and the full prior noise allowance.
                var angle = Rotation(_frames[first], _frames[end]);
                if (Finite(angle) && angle > allowance + uncertainty) return Reject(evidenceStart);
                allowance -= current.AngularSteps[first - start];
            }
        }

        // An unknown offset between altitude/position and velocity reference
        // points creates real displacement during rotation. Without geometry,
        // use linear residuals only when the observed pose is unchanged. The
        // independent angular bound above remains valid for any datum or bank.
        if (!before.StablePose || !current.StablePose) return false;
        var horizontal = Distance(_frames[start], _frames[end]);
        // Observed mismatch augments the channel's comparison resolution. A
        // quiet baseline cannot make micrometre noise conclusive evidence and
        // thereby reject the entire next airborne run. Roundoff is additional
        // arithmetic uncertainty, not the resolution of a simulator observation.
        if (Finite(horizontal) && Finite(priorHorizontal) && Finite(before.Horizontal) && Finite(current.Horizontal) &&
            horizontal > current.Horizontal + Math.Max(0, priorHorizontal - before.Horizontal) * scale +
                TelemetryPoseResolution.HorizontalMeters + PositionRoundoff) return Reject(evidenceStart);
        var vertical = _frames[end].Altitude - _frames[start].Altitude;
        var priorVertical = _frames[start].Altitude - _frames[baseline].Altitude;
        if (Finite(vertical) && Finite(priorVertical) && Finite(priorVerticalPath) && Finite(before.AbsoluteVertical) &&
            Finite(before.LowerVertical) && Finite(before.UpperVertical) &&
            Finite(current.LowerVertical) && Finite(current.UpperVertical))
        {
            var mismatch = Math.Max(Math.Max(0, priorVerticalPath - before.AbsoluteVertical),
                Math.Max(before.LowerVertical - priorVertical, priorVertical - before.UpperVertical)) * scale;
            var uncertainty = mismatch + TelemetryPoseResolution.AltitudeMeters + PositionRoundoff;
            if (vertical < current.LowerVertical - uncertainty || vertical > current.UpperVertical + uncertainty)
                return Reject(evidenceStart);
        }
        return false;
    }

    private bool Reject(int start) { _proofStart = _frames[start].Time; return true; }

    private bool TryEnvelope(int start, int end, out Envelope envelope)
    {
        envelope = new Envelope { StablePose = true, AngularSteps = new double[end - start + 2] };
        if (start < 1 || end + 1 >= _frames.Count) return false;
        var supportStart = AtOrBefore(_frames[start - 1].Time - Alignment);
        var supportEnd = AtOrAfter(_frames[end + 1].Time + Alignment);
        if (supportStart < 0 || supportEnd < 0) return false;
        for (var i = supportStart; i <= supportEnd; i++)
        {
            if (i > supportStart && _frames[i].Time - _frames[i - 1].Time > MaximumFrameGap) return false;
            var angle = Rotation(_frames[start], _frames[i]);
            envelope.StablePose &= Finite(angle) && angle <= AnglePrecision;
        }
        for (var i = start - 1; i <= end; i++)
        {
            var left = AtOrBefore(_frames[i].Time - Alignment);
            var right = AtOrAfter(_frames[i + 1].Time + Alignment);
            if (left < 0 || right < 0) return false;
            var minimumVertical = double.PositiveInfinity;
            var maximumVertical = double.NegativeInfinity;
            var maximumHorizontal = 0.0;
            var maximumAngular = 0.0;
            for (var j = left; j <= right; j++)
            {
                var row = _frames[j];
                maximumHorizontal = Math.Max(maximumHorizontal, row.GroundSpeed);
                minimumVertical = Math.Min(minimumVertical, row.VerticalSpeed);
                maximumVertical = Math.Max(maximumVertical, row.VerticalSpeed);
                maximumAngular = Math.Max(maximumAngular, row.AngularSpeed);
            }
            var dt = _frames[i + 1].Time - _frames[i].Time;
            envelope.Horizontal += maximumHorizontal * dt;
            envelope.AbsoluteVertical += Math.Max(Math.Abs(minimumVertical), Math.Abs(maximumVertical)) * dt;
            // Retain uncertainty from unresolved changes between rate samples.
            // A fast collision impulse must not be judged as a pose replacement
            // merely because its angular-rate peak lies between deliveries.
            var angular = maximumAngular * dt + .5 * Math.Abs(_frames[i + 1].AngularSpeed - _frames[i].AngularSpeed) * dt;
            envelope.Angular += angular;
            envelope.AngularSteps[i - start + 1] = angular;
            if (i < start || i == end)
            {
                // Position/pose and rates are delivered once per frame. Their
                // endpoints can fall anywhere in adjacent observed frame
                // brackets; do not mistake that resolution for a pose jump.
                // Signed vertical motion needs both possible endpoint shifts.
                var boundary = Math.Max(Math.Abs(minimumVertical), Math.Abs(maximumVertical)) * dt;
                envelope.LowerVertical -= boundary;
                envelope.UpperVertical += boundary;
            }
            else
            {
                envelope.LowerVertical += minimumVertical * dt;
                envelope.UpperVertical += maximumVertical * dt;
            }
        }
        return true;
    }

    private int AtOrBefore(double time)
    {
        var lo = 0; var hi = _frames.Count;
        while (lo < hi) { var mid = lo + (hi - lo) / 2; if (_frames[mid].Time <= time) lo = mid + 1; else hi = mid; }
        return lo - 1;
    }
    private int AtOrAfter(double time)
    {
        var lo = 0; var hi = _frames.Count;
        while (lo < hi) { var mid = lo + (hi - lo) / 2; if (_frames[mid].Time < time) lo = mid + 1; else hi = mid; }
        return lo < _frames.Count ? lo : -1;
    }

    internal void WriteCheckpoint(BinaryWriter writer)
    {
        writer.Write(_proofStart);
        writer.Write(_frames.Count);
        foreach (var frame in _frames) frame.Write(writer);
    }
    internal void RestoreCheckpoint(BinaryReader reader)
    {
        Reset();
        _proofStart = reader.ReadDouble();
        if (double.IsInfinity(_proofStart)) throw new InvalidDataException("Invalid cumulative-motion evidence time.");
        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumFrames) throw new InvalidDataException("Invalid cumulative-motion history length.");
        for (var i = 0; i < count; i++)
        {
            var frame = new Frame(reader);
            if (!Finite(frame.Time) || frame.Segment < 0 || (i > 0 &&
                (frame.Time <= _frames[i - 1].Time || frame.Segment != _frames[i - 1].Segment)))
                throw new InvalidDataException("Invalid cumulative-motion timeline.");
            _frames.Add(frame);
        }
        if (Finite(_proofStart) && (count == 0 || _proofStart > _frames[count - 1].Time))
            throw new InvalidDataException("Invalid cumulative-motion evidence history.");
        if (count > 1 && _frames[count - 1].Time - _frames[1].Time > History)
            throw new InvalidDataException("Cumulative-motion history exceeds its time bound.");
    }

    private static double Rotation(Frame a, Frame b)
    {
        // Quaternion chord gives a stable geodesic angle near zero and across
        // heading wrap; q and -q represent the same orientation.
        var minus = Square(a.W - b.W) + Square(a.X - b.X) + Square(a.Y - b.Y) + Square(a.Z - b.Z);
        var plus = Square(a.W + b.W) + Square(a.X + b.X) + Square(a.Y + b.Y) + Square(a.Z + b.Z);
        return 4 * Math.Asin(Math.Min(1, Math.Sqrt(Math.Min(minus, plus)) / 2));
    }
    private static double Distance(Frame a, Frame b) => Position(a) && Position(b)
        ? GeoDistance.GreatCircleNauticalMiles(a.Latitude, a.Longitude, b.Latitude, b.Longitude) * 1852 : double.NaN;
    private static bool Position(Frame a) => Finite(a.Latitude) && Finite(a.Longitude) && Math.Abs(a.Latitude) <= 90 &&
        Math.Abs(a.Longitude) <= 180 && (a.Latitude != 0 || a.Longitude != 0);
    private static bool Finite(double value) => AnalysisNumericPolicy.IsFinite(value);
    private static double Square(double value) => value * value;

    private struct Envelope
    {
        internal double Horizontal, LowerVertical, UpperVertical, AbsoluteVertical, Angular;
        internal double[] AngularSteps;
        internal bool StablePose;
    }
    private sealed class Frame
    {
        internal readonly double Time, Latitude, Longitude, Altitude, GroundSpeed, VerticalSpeed, AngularSpeed, W, X, Y, Z;
        internal readonly long Segment;
        internal readonly bool Ground;
        internal Frame(TelemetrySample sample)
        {
            Time = TelemetryAnalysisFrames.TimeOf(sample); Segment = sample.FlightSegment; Ground = sample.OnGround;
            Latitude = sample.LatitudeDegrees; Longitude = sample.LongitudeDegrees; Altitude = sample.PlaneAltitudeFeet * .3048;
            var groundSpeed = sample.GroundSpeedKnots * 1852 / 3600;
            GroundSpeed = Finite(groundSpeed) && groundSpeed >= 0 ? groundSpeed : double.NaN;
            VerticalSpeed = Finite(sample.VelocityWorldYFps) ? sample.VelocityWorldYFps * .3048 : double.NaN;
            var angularSpeed = Math.Sqrt(Square(sample.RotationVelocityBodyXRadiansPerSecond) +
                Square(sample.RotationVelocityBodyYRadiansPerSecond) + Square(sample.RotationVelocityBodyZRadiansPerSecond));
            AngularSpeed = Finite(angularSpeed) ? angularSpeed : double.NaN;
            var pitch = sample.PitchDegrees * Math.PI / 360; var bank = sample.BankDegrees * Math.PI / 360;
            var heading = sample.HeadingTrueDegrees * Math.PI / 360;
            var cp = Math.Cos(pitch); var sp = Math.Sin(pitch); var cb = Math.Cos(bank); var sb = Math.Sin(bank);
            var ch = Math.Cos(heading); var sh = Math.Sin(heading);
            W = ch * cp * cb + sh * sp * sb; X = ch * sp * cb + sh * cp * sb;
            Y = sh * cp * cb - ch * sp * sb; Z = ch * cp * sb - sh * sp * cb;
        }
        internal Frame(BinaryReader reader)
        {
            Time = reader.ReadDouble(); Segment = reader.ReadInt64(); var ground = reader.ReadByte();
            if (ground > 1) throw new InvalidDataException("Invalid cumulative-motion ground flag.");
            Ground = ground != 0;
            Latitude = reader.ReadDouble(); Longitude = reader.ReadDouble(); Altitude = reader.ReadDouble();
            GroundSpeed = reader.ReadDouble(); VerticalSpeed = reader.ReadDouble(); AngularSpeed = reader.ReadDouble();
            W = reader.ReadDouble(); X = reader.ReadDouble(); Y = reader.ReadDouble(); Z = reader.ReadDouble();
            var norm = W * W + X * X + Y * Y + Z * Z;
            if ((Finite(norm) ? Math.Abs(norm - 1) > 1e-12 : !(double.IsNaN(W) && double.IsNaN(X) && double.IsNaN(Y) && double.IsNaN(Z))) ||
                GroundSpeed < 0 || AngularSpeed < 0 || double.IsInfinity(GroundSpeed) || double.IsInfinity(AngularSpeed))
                throw new InvalidDataException("Invalid cumulative-motion observation.");
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write(Time); writer.Write(Segment); writer.Write(Ground); writer.Write(Latitude); writer.Write(Longitude);
            writer.Write(Altitude); writer.Write(GroundSpeed); writer.Write(VerticalSpeed); writer.Write(AngularSpeed);
            writer.Write(W); writer.Write(X); writer.Write(Y); writer.Write(Z);
        }
    }
}
