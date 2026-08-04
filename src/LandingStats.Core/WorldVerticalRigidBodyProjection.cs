using System;

namespace LandingStats.Core;

internal static class WorldVerticalRigidBodyProjection
{
    /// <summary>
    /// Projects the velocity induced at a signed longitudinal body-axis offset
    /// onto world vertical. Positive return values point upward.
    /// </summary>
    internal static double AtLongitudinalOffsetFps(
        double omegaXRadiansPerSecond,
        double omegaYRadiansPerSecond,
        double pitchRadians,
        double bankRadians,
        double longitudinalOffsetFeet)
    {
        var pitchContribution =
            omegaXRadiansPerSecond *
            longitudinalOffsetFeet *
            Math.Cos(pitchRadians) *
            Math.Cos(bankRadians);
        var yawBankContribution =
            omegaYRadiansPerSecond *
            longitudinalOffsetFeet *
            Math.Sin(bankRadians);
        return -pitchContribution - yawBankContribution;
    }
}
