using System;

namespace LandingStats.Core;

public enum ReplaySourceKind { Unknown, ConfirmedSdkReplay, ExternallyDrivenPose, SuspiciousKinematics }
public enum ReplaySourceAssessmentReason
{
    MissingObservation, SdkReplay, ObservationUnavailable, ObservationStale, ClockDiscontinuity,
    NonFinitePose, Slew, PartialFreeze, AwaitingMovement, PoseChangedWhileFrozen, KinematicEvidence, NoFreeze,
}
public sealed class ReplaySourceAssessment
{
    internal ReplaySourceAssessment(ReplaySourceKind kind, ReplaySourceAssessmentReason reason) { Kind = kind; Reason = reason; }
    public ReplaySourceKind Kind { get; }
    public ReplaySourceAssessmentReason Reason { get; }
    // External position control also occurs outside replay. Only the SDK signal
    // is authoritative here; controlled live evidence is required to change this.
    public bool ConfirmsReplay => Kind == ReplaySourceKind.ConfirmedSdkReplay;
}

/// <summary>Diagnostic state machine, independent of landing acceptance. A paused
/// externally driven pose remains observed until flags, freshness or source change.</summary>
public sealed class ReplaySourceAssessor
{
    public const double MaximumObservationAgeSeconds = 1.5;
    private ReplaySourceEvidence? _previous;
    private bool _external;
    public ReplaySourceAssessment Observe(ReplaySourceEvidence? evidence, bool suspiciousKinematics = false)
    {
        ReplaySourceAssessment Unknown(ReplaySourceAssessmentReason reason)
        {
            _previous = null; _external = false;
            return new ReplaySourceAssessment(suspiciousKinematics && evidence != null ? ReplaySourceKind.SuspiciousKinematics : ReplaySourceKind.Unknown,
                reason);
        }
        if (evidence == null) return Unknown(ReplaySourceAssessmentReason.MissingObservation);
        if (evidence.Observation.Availability == SourceObservationAvailability.Gap) return Unknown(ReplaySourceAssessmentReason.ObservationUnavailable);
        if (evidence.SdkReplay == SdkReplayState.Active)
        { _previous = null; _external = false; return new ReplaySourceAssessment(ReplaySourceKind.ConfirmedSdkReplay, ReplaySourceAssessmentReason.SdkReplay); }
        if (evidence.AgeSeconds < 0) return Unknown(ReplaySourceAssessmentReason.ClockDiscontinuity);
        if (evidence.Observation.Availability != SourceObservationAvailability.Available) return Unknown(ReplaySourceAssessmentReason.ObservationUnavailable);
        if (evidence.AgeSeconds > MaximumObservationAgeSeconds) return Unknown(ReplaySourceAssessmentReason.ObservationStale);
        var current = evidence.Observation;
        if (!Finite(current.Latitude) || !Finite(current.Longitude) || !Finite(current.AltitudeFeet) || !Finite(current.Pitch) || !Finite(current.Bank) || !Finite(current.Heading))
            return Unknown(ReplaySourceAssessmentReason.NonFinitePose);
        if (current.Slew != 0) return Unknown(ReplaySourceAssessmentReason.Slew);
        if (current.FreezeMask != 7) return Unknown(current.FreezeMask == 0 ? ReplaySourceAssessmentReason.NoFreeze : ReplaySourceAssessmentReason.PartialFreeze);
        if (_previous == null || !evidence.SameSource(_previous) || evidence.ObservedQpc < _previous.ObservedQpc ||
            evidence.FrameQpc < _previous.FrameQpc || (evidence.ObservedQpc - (double)_previous.ObservedQpc) / evidence.QpcFrequency > MaximumObservationAgeSeconds)
            _external = false;
        else if (evidence.ObservedQpc > _previous.ObservedQpc && Moved(_previous.Observation, current)) _external = true;
        _previous = evidence;
        return _external
            ? new ReplaySourceAssessment(ReplaySourceKind.ExternallyDrivenPose, ReplaySourceAssessmentReason.PoseChangedWhileFrozen)
            : new ReplaySourceAssessment(suspiciousKinematics ? ReplaySourceKind.SuspiciousKinematics : ReplaySourceKind.Unknown, ReplaySourceAssessmentReason.AwaitingMovement);
    }
    private static bool Finite(double value) => AnalysisNumericPolicy.IsFinite(value);
    private static bool Moved(ReplaySourceObservation left, ReplaySourceObservation right) =>
        Math.Abs(left.Latitude - right.Latitude) > 1e-8 || Math.Abs(left.Longitude - right.Longitude) > 1e-8 ||
        Math.Abs(left.AltitudeFeet - right.AltitudeFeet) > 1e-3 || Math.Abs(left.Pitch - right.Pitch) > 1e-4 ||
        Math.Abs(left.Bank - right.Bank) > 1e-4 || Math.Abs(left.Heading - right.Heading) > 1e-4;
}
