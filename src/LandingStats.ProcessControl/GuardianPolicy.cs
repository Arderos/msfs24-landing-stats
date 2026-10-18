using System;
using System.Diagnostics;

namespace LandingStats.App.Runtime;

internal enum GuardianDecision { Continue, WaitForSafePoint, PrepareRecovery, Restart, Stop }
internal sealed class GuardianPolicy
{
    public TimeSpan Startup { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ControlStall { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan UiStall { get; set; } = TimeSpan.FromSeconds(GuardianStatus.DefaultUiStallSeconds);
    public TimeSpan GracefulRecovery { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan ShutdownIdle { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan MaximumShutdown { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ResumeGrace { get; set; } = TimeSpan.FromSeconds(15);
    public int MaximumStartupFailures { get; set; } = 3;
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromSeconds(30);
    public void Validate()
    {
        if (Startup <= TimeSpan.Zero || ControlStall <= TimeSpan.Zero || UiStall <= TimeSpan.Zero || GracefulRecovery <= TimeSpan.Zero ||
            CheckInterval <= TimeSpan.Zero || InitialBackoff < TimeSpan.Zero || MaximumBackoff < InitialBackoff || ShutdownIdle <= TimeSpan.Zero ||
            MaximumShutdown < ShutdownIdle || ResumeGrace <= TimeSpan.Zero || MaximumStartupFailures < 1) throw new ArgumentException("Invalid guardian deadlines.");
    }
    public GuardianDecision Decide(long now, long started, GuardianStatus? health, GuardianStopReason stop, bool exited, bool channelFailed,
        long recoveryRequested = 0, bool recoveryReady = false)
    {
        if (stop != GuardianStopReason.None) return GuardianDecision.Stop;
        if (exited || channelFailed) return GuardianDecision.Restart;
        if (recoveryReady) return GuardianDecision.Restart;
        if (recoveryRequested != 0) return Age(now, recoveryRequested) >= GracefulRecovery ? GuardianDecision.Restart : GuardianDecision.Continue;
        if (health == null || !health.Ready) return Age(now, started) >= Startup ? GuardianDecision.Restart : GuardianDecision.Continue;
        if (health.ControlQpc > now + Stopwatch.Frequency * 2 || health.UiQpc > now + Stopwatch.Frequency * 2 ||
            health.Recovery == GuardianRecovery.Runtime || health.ControlQpc == 0 || Age(now, health.ControlQpc) >= ControlStall) return GuardianDecision.Restart;
        if (health.Recovery == GuardianRecovery.Presentation || health.UiQpc == 0 || Age(now, health.UiQpc) >= UiStall)
            return health.SafeToRestart ? GuardianDecision.PrepareRecovery : GuardianDecision.WaitForSafePoint;
        return GuardianDecision.Continue;
    }
    public TimeSpan Backoff(int failures) => TimeSpan.FromMilliseconds(Math.Min(MaximumBackoff.TotalMilliseconds,
        InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(20, Math.Max(0, failures - 1)))));
    private static TimeSpan Age(long now, long since) => TimeSpan.FromSeconds(Math.Max(0, (now - since) / (double)Stopwatch.Frequency));
}
