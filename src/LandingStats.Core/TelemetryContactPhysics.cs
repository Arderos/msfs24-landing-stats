namespace LandingStats.Core;

/// <summary>Source observations constrain contact evidence without classifying an external control as replay.</summary>
public static class TelemetryContactPhysics
{
    public static bool IsBlocked(TelemetrySample sample) => IsBlocked(sample.SourceEvidence);

    internal static bool IsBlocked(ReplaySourceEvidence? evidence)
    {
        if (evidence == null) return false;
        if (evidence.SdkReplay == SdkReplayState.Active) return true;
        return Fresh(evidence) && (evidence.Observation.FreezeMask != 0 || evidence.Observation.Slew != 0);
    }

    public static bool IsFreshReleased(TelemetrySample sample) => IsFreshReleased(sample.SourceEvidence);

    public static bool IsFreshReleased(ReplaySourceEvidence? evidence) => evidence != null &&
        evidence.SdkReplay != SdkReplayState.Active && Fresh(evidence) &&
        evidence.Observation.FreezeMask == 0 && evidence.Observation.Slew == 0 &&
        AnalysisNumericPolicy.IsFinite(evidence.Observation.SimulationTime) &&
        AnalysisNumericPolicy.IsFinite(evidence.Observation.Latitude) &&
        AnalysisNumericPolicy.IsFinite(evidence.Observation.Longitude) &&
        AnalysisNumericPolicy.IsFinite(evidence.Observation.AltitudeFeet) &&
        AnalysisNumericPolicy.IsFinite(evidence.Observation.Pitch) &&
        AnalysisNumericPolicy.IsFinite(evidence.Observation.Bank) &&
        AnalysisNumericPolicy.IsFinite(evidence.Observation.Heading);

    private static bool Fresh(ReplaySourceEvidence evidence) =>
        evidence.Observation.Availability == SourceObservationAvailability.Available &&
        evidence.AgeSeconds >= 0 && evidence.AgeSeconds <= ReplaySourceAssessor.MaximumObservationAgeSeconds;
}
