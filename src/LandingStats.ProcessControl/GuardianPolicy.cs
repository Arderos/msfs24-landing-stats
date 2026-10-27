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
        long recoveryRequested = 0, bool recoveryReady = false) =>
        Decide(now, started, health, stop, exited, channelFailed, out _, recoveryRequested, recoveryReady);

    public GuardianDecision Decide(long now, long started, GuardianStatus? health, GuardianStopReason stop, bool exited, bool channelFailed,
        out string reason, long recoveryRequested = 0, bool recoveryReady = false)
    {
        if (stop != GuardianStopReason.None) { reason = "intentional_" + stop; return GuardianDecision.Stop; }
        if (exited) { reason = "process_exited"; return GuardianDecision.Restart; }
        if (channelFailed) { reason = "channel_closed"; return GuardianDecision.Restart; }
        if (recoveryReady) { reason = "recovery_ready"; return GuardianDecision.Restart; }
        if (recoveryRequested != 0)
        {
            var expired = Age(now, recoveryRequested) >= GracefulRecovery;
            reason = expired ? "recovery_deadline" : "recovery_draining";
            return expired ? GuardianDecision.Restart : GuardianDecision.Continue;
        }
        if (health == null || !health.Ready)
        {
            var expired = Age(now, started) >= Startup;
            reason = expired ? "startup_deadline" : "starting";
            return expired ? GuardianDecision.Restart : GuardianDecision.Continue;
        }
        if (health.ControlQpc > now + Stopwatch.Frequency * 2 || health.UiQpc > now + Stopwatch.Frequency * 2)
        { reason = "health_clock_invalid"; return GuardianDecision.Restart; }
        if (health.Recovery == GuardianRecovery.Runtime) { reason = "runtime_recovery"; return GuardianDecision.Restart; }
        if (health.ControlQpc == 0) { reason = "control_missing"; return GuardianDecision.Restart; }
        if (Age(now, health.ControlQpc) >= ControlStall) { reason = "control_stalled"; return GuardianDecision.Restart; }
        if (health.Recovery == GuardianRecovery.Presentation || health.UiQpc == 0 || Age(now, health.UiQpc) >= UiStall)
        {
            reason = health.Recovery == GuardianRecovery.Presentation ? "presentation_recovery" : health.UiQpc == 0 ? "ui_missing" : "ui_stalled";
            return health.SafeToRestart ? GuardianDecision.PrepareRecovery : GuardianDecision.WaitForSafePoint;
        }
        reason = "healthy"; return GuardianDecision.Continue;
    }
    public TimeSpan Backoff(int failures) => TimeSpan.FromMilliseconds(Math.Min(MaximumBackoff.TotalMilliseconds,
        InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(20, Math.Max(0, failures - 1)))));
    private static TimeSpan Age(long now, long since) => TimeSpan.FromSeconds(Math.Max(0, (now - since) / (double)Stopwatch.Frequency));
}
