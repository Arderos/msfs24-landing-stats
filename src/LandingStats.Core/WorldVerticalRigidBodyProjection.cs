using System;

namespace LandingStats.Core;

internal static class WorldVerticalRigidBodyProjection
{
    // Frozen revision 1 for archived geometry snapshots. Its known bank/yaw
    // defect must not silently change a requested historical reconstruction.
    internal static double LegacyAtLongitudinalOffsetFps(double omegaX, double omegaY, double pitch, double bank, double offset)
    {
        var pitchContribution = omegaX * offset * Math.Cos(pitch) * Math.Cos(bank);
        var yawBankContribution = omegaY * offset * Math.Sin(bank);
        return -pitchContribution - yawBankContribution;
    }

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
            Math.Cos(pitchRadians) *
            Math.Sin(bankRadians);
        // theta-dot = q*cos(phi) - r*sin(phi); forward-point height is -L*sin(theta).
        return -pitchContribution + yawBankContribution;
    }
}
