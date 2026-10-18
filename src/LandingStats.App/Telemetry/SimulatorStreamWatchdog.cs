using System;

namespace LandingStats.App.Telemetry;

/// <summary>Connection evidence independent of the SIM_FRAME subscriptions.
/// A running flag alone does not prove advancing physics. Probe samples are
/// used only here and never inserted into the recording stream.</summary>
internal sealed class SimulatorStreamWatchdog
{
    private const double ProbeInterval = 2, ProbeReplyDeadline = 5, ReplyDeadline = 30, SuspendedReplyDeadline = 120, PhysicsEvidenceAge = 5;
    [Flags] private enum Block { None = 0, Loading = 1, Teleport = 2, BackOnTrack = 4, Skip = 8, Rtc = 16, Replay = 32, Ended = 64 }
    private Block _blocks;
    private bool? _running, _motion;
    private int _pause = -1;
    private double _lastReply, _lastTime = double.NaN, _advancedAt = double.NaN, _nextProbe, _probeSent;
    private double? _expectedSince;
    // Distinct from all acquisition/scenery request ranges. Never reused in
    // this source object, including after reconnect, so late replies are inert.
    private uint _nextRequest = 0x80000000;
    internal uint SystemRequest { get; private set; }
    internal uint FrameRequest { get; private set; }

    internal void Reset(double now)
    {
        _blocks = Block.None; _running = _motion = null; _pause = -1;
        _lastReply = now; _lastTime = _advancedAt = double.NaN; _nextProbe = now;
        _expectedSince = null; SystemRequest = FrameRequest = 0;
    }
    internal bool BeginProbe(double now)
    {
        if (now < _nextProbe || (SystemRequest != 0 || FrameRequest != 0) && now - _probeSent < ProbeReplyDeadline) return false;
        if (_nextRequest > uint.MaxValue - 2) throw new InvalidOperationException("Simulator probe identities exhausted.");
        SystemRequest = _nextRequest++; FrameRequest = _nextRequest++;
        _nextProbe = now + ProbeInterval; _probeSent = now; return true;
    }
    internal bool ObserveSystem(uint request, bool running, double now)
    {
        if (request == 0 || request != SystemRequest) return false;
        SystemRequest = 0; Reply(now); SetRunning(running); return true;
    }
    internal bool ObserveProbe(uint request, double time, bool motion, double now)
    {
        if (request == 0 || request != FrameRequest) return false;
        FrameRequest = 0; ObserveData(time, motion, now); return true;
    }
    internal void Reply(double now) => _lastReply = now;
    internal void ObserveData(double time, bool motion, double now)
    {
        Reply(now);
        if (double.IsNaN(time) || double.IsInfinity(time)) return;
        // Guard, Full and ONCE replies may interleave. A slightly older reply
        // must not keep resetting the timeout of a different missing stream.
        if (!double.IsNaN(_lastTime) && time < _lastTime - 0.000001)
        {
            if (_lastTime - time < 1) return;
            _advancedAt = double.NaN; _expectedSince = null;
        }
        else if (motion && !double.IsNaN(_lastTime) && time > _lastTime + 0.000001) _advancedAt = now;
        _lastTime = time; _motion = motion;
        if (!motion) _expectedSince = null;
    }
    internal void SetRunning(bool running)
    { if (_running != running) _expectedSince = null; _running = running; }
    internal void SetPause(int flags)
    { if (_pause != flags) _expectedSince = null; _pause = flags; }
    internal void Flow(SimulatorFlowEvent flow)
    {
        switch (flow)
        {
            case SimulatorFlowEvent.FLT_LOAD: _blocks = (_blocks & ~Block.Ended) | Block.Loading; break;
            case SimulatorFlowEvent.FLT_LOADED: _blocks &= ~Block.Loading; break;
            case SimulatorFlowEvent.TELEPORT_START: _blocks |= Block.Teleport; break;
            case SimulatorFlowEvent.TELEPORT_DONE: _blocks &= ~Block.Teleport; break;
            case SimulatorFlowEvent.BACK_ON_TRACK_START: _blocks |= Block.BackOnTrack; break;
            case SimulatorFlowEvent.BACK_ON_TRACK_DONE: _blocks &= ~Block.BackOnTrack; break;
            case SimulatorFlowEvent.SKIP_START: _blocks |= Block.Skip; break;
            case SimulatorFlowEvent.SKIP_DONE: _blocks &= ~Block.Skip; break;
            case SimulatorFlowEvent.RTC_START: _blocks |= Block.Rtc; break;
            case SimulatorFlowEvent.RTC_END: _blocks &= ~Block.Rtc; break;
            case SimulatorFlowEvent.REPLAY_START: _blocks |= Block.Replay; break;
            case SimulatorFlowEvent.REPLAY_END: _blocks &= ~Block.Replay; break;
            case SimulatorFlowEvent.BACK_TO_MAIN_MENU:
            case SimulatorFlowEvent.FLIGHT_END:
            case SimulatorFlowEvent.PLANE_CRASH: _blocks = Block.Ended; break;
            case SimulatorFlowEvent.FLIGHT_START: _blocks &= ~Block.Ended; break;
        }
        _expectedSince = null; _lastTime = _advancedAt = double.NaN; _running = null;
    }
    private bool Suspended => _pause > 0 || _running == false || _blocks != Block.None || _motion == false;
    internal double? ExpectedSince(double now)
    {
        if (Suspended || double.IsNaN(_advancedAt) || now - _advancedAt >= PhysicsEvidenceAge) _expectedSince = null;
        else if (!_expectedSince.HasValue) _expectedSince = now;
        return _expectedSince;
    }
    // Suspension suppresses missing physics-frame recovery, not connection
    // liveness. Loading gets a longer grace period, but a stale pause/loading
    // flag cannot exempt an indefinitely unanswered connection from recovery.
    internal bool ConnectionUnresponsive(double now) =>
        now - _lastReply >= (Suspended ? SuspendedReplyDeadline : ReplyDeadline);
}
