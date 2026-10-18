using System;

namespace LandingStats.Core;

// Assemble geometry provenance once, after the numerical estimator has decided
// whether its result is usable. Missing reconstruction never implies a zero arm.
internal static class ClosureReconstructionProvenance
{
    internal static void Apply(TouchdownResult result, bool available, TouchdownClosureEstimate estimate,
        double arm, double quality, bool recovered, bool spatial, TouchdownGeometrySource source,
        TelemetryGearTopology? topology, bool interpolatedContact, int contactNumber, double exclusiveEnd)
    {
        result.ClosureReconstructionFitPointCount = available ? estimate.FitPointCount : 0;
        result.ClosureReconstructionLongitudinalArmFeet = available ? arm : double.NaN;
        result.ClosureReconstructionGeometryQuality = available ? quality : double.NaN;
        result.ClosureReconstructionArmRecoveredFromTelemetry = available && recovered;
        result.ClosureReconstructionGeometrySource = available ? source : TouchdownGeometrySource.Unavailable;
        if (!available) { result.ClosureReconstructionUncertaintyFpm = double.NaN; return; }
        var uncertainty = TouchdownClosureReconstruction.FallbackUncertaintyFpm;
        if (!spatial)
        {
            if (topology!.UsesClusteredWheelInference || topology.MainContactPointCount > 4)
            {
                if (source == TouchdownGeometrySource.Telemetry) uncertainty = TouchdownClosureReconstruction.MultiBogieTelemetryUncertaintyFpm;
            }
            else if (interpolatedContact && contactNumber == 1 && double.IsPositiveInfinity(exclusiveEnd) && topology.MainContactPointCount == 2)
                uncertainty = TouchdownClosureReconstruction.PrimaryUncertaintyFpm;
        }
        result.ClosureReconstructionUncertaintyFpm = uncertainty;
    }
}
