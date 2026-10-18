using System;
using System.Collections.Generic;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectSource
{
    private readonly Queue<Action> _nativeEffects = new Queue<Action>();
    private long _nativeQueryEpoch;

    private bool DeferNativeEffect(Action effect, bool scenery = false)
    {
        if (_dispatchDepth == 0) return false;
        var connection = _simConnect; var epoch = _nativeQueryEpoch;
        _nativeEffects.Enqueue(() =>
        {
            if (!_disposed && ReferenceEquals(connection, _simConnect) && connection != null && (!scenery || epoch == _nativeQueryEpoch)) effect();
        });
        return true;
    }

    private void DrainNativeEffects()
    {
        if (_dispatchDepth != 0) return;
        try
        {
            while (_nativeEffects.Count > 0) _nativeEffects.Dequeue()();
            TryPublishRunwayBatch();
        }
        catch (Exception error)
        {
            _nativeEffects.Clear();
            _capture.DecisionJournal.Record(_capture.LastSimulationTime, "source", "deferred_effect_failed", error.Message);
            DisconnectWithReason(SourceDisconnectionReason.ReceiveFailed);
        }
    }
}
