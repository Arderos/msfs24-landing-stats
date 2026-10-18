using System;

namespace LandingStats.App.Telemetry;

internal enum SubscriptionFreshness { Current, Retry, Reconnect }

/// <summary>Desired mode, submitted request and received evidence are distinct.</summary>
internal sealed class RequiredTelemetrySubscription
{
    internal bool Desired { get; private set; }
    internal bool? Requested { get; private set; }
    internal bool Observed { get; private set; }
    internal bool RecoveryPending { get; private set; }
    internal uint? Packet { get; private set; }
    internal long Revision { get; private set; }
    private double _retryAt;
    private int _failures;
    private bool _everRequested;
    private bool _everObserved;
    private double _submittedAt = double.NaN, _observedAt = double.NaN;
    private int _silentRetries;

    internal void Reset(bool desired)
    {
        _everRequested = false;
        _everObserved = false;
        ChangeDesired(desired);
    }

    private void ChangeDesired(bool desired)
    {
        Desired = desired;
        Requested = null;
        Observed = RecoveryPending = false;
        Packet = null;
        _retryAt = 0;
        _failures = 0;
        _silentRetries = 0;
        _submittedAt = _observedAt = double.NaN;
        Revision++;
    }

    internal void SetDesired(bool desired)
    {
        if (Desired == desired) return;
        ChangeDesired(desired);
    }

    // SDK 1.8.1 rejects NEVER for an id that has never had an active request.
    // Above the AGL gate an unused stream is already disabled. Keep that local
    // state until its first enable; later disable/retry still sends real NEVER.
    internal bool ShouldRequest(double now) => (Desired || _everRequested) && !Requested.HasValue && now >= _retryAt;

    internal void Submitted(uint? packet, double now = 0)
    {
        Requested = Desired;
        _everRequested = true;
        Packet = packet;
        Observed = false;
        _submittedAt = now;
        // NEVER intentionally has no frame acknowledgement. An enabled stream
        // remains recovering until it produces its own data.
        if (!Desired) RecoveryPending = false;
    }

    internal bool Reject(uint packet, double now)
    {
        if (!Packet.HasValue || Packet.Value != packet || !Requested.HasValue) return false;
        if (Requested == true && !_everObserved) _everRequested = false;
        Requested = null;
        Packet = null;
        Observed = false;
        RecoveryPending = true;
        _retryAt = now + Math.Min(30, Math.Pow(2, Math.Min(_failures++, 5)));
        return true;
    }

    internal void Observe(double now = 0)
    {
        if (!Desired || Requested != true) return;
        Observed = true;
        _everObserved = true;
        RecoveryPending = false;
        _failures = 0;
        _observedAt = now; _silentRetries = 0;
    }

    internal SubscriptionFreshness CheckFreshness(double now, double expectedSince)
    {
        if (!Desired || Requested != true) return SubscriptionFreshness.Current;
        var evidence = Math.Max(expectedSince, _submittedAt);
        if (!double.IsNaN(_observedAt)) evidence = Math.Max(evidence, _observedAt);
        if (now - evidence < 5) return SubscriptionFreshness.Current;
        if (_silentRetries >= 2) return SubscriptionFreshness.Reconnect;
        _silentRetries++; Requested = null; Packet = null; Observed = false;
        RecoveryPending = true; _retryAt = now;
        return SubscriptionFreshness.Retry;
    }
}
