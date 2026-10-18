using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using LandingStats.App.Telemetry;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.RegressionTests;

internal static class RequiredSubscriptionRegressionTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void RunAll()
    {
        var state = new RequiredTelemetrySubscription();
        state.Reset(false);
        Require(!state.ShouldRequest(0) && !state.ShouldRequest(1000), "A never-started stream must not send initial NEVER or timer retries.");
        state.SetDesired(true);
        state.SetDesired(false);
        Require(!state.ShouldRequest(1000), "An enable that was never submitted does not create a native request to stop.");
        state.Reset(true);
        double now = 0;
        for (uint packet = 1; packet <= 20; packet++)
        {
            Require(state.ShouldRequest(now), "Retry becomes eligible at its deadline.");
            state.Submitted(packet);
            Require(!state.Observed && !state.ShouldRequest(now), "Submitted is not observed and cannot be submitted twice.");
            Require(state.Reject(packet, now), "Rejection invalidates the current request.");
            Require(!state.ShouldRequest(now) && !state.Reject(packet, now), "Rejection is idempotent and enforces backoff.");
            state.Observe();
            Require(state.RecoveryPending && !state.Observed, "Frames before a replacement request cannot confirm it.");
            now += 30;
        }
        state.Submitted(21);
        state.Observe();
        Require(state.Observed && !state.RecoveryPending, "Recovery resumes after arbitrarily many transient failures.");
        Require(!state.Reject(20, now), "An obsolete packet cannot invalidate the newer request.");
        state.SetDesired(false);
        Require(state.ShouldRequest(now), "Disabling issues a real NEVER request.");
        state.Submitted(22);
        Require(!state.Observed && !state.RecoveryPending, "NEVER does not wait for an impossible full-frame acknowledgement.");
        Require(state.Reject(22, now), "A rejected disable request also requires retry.");
        Require(state.ShouldRequest(now + 1), "Successful evidence reset the failure backoff.");
        state.Reset(true);
        Require(!state.Reject(22, now), "Reset drops old connection packet identities.");
        state.Reset(false);
        Require(!state.ShouldRequest(now), "Reconnect drops the old subscription; initial NEVER must not recur.");

        GuardDoesNotConcealFailedFullSubscription();
    }

    private static void GuardDoesNotConcealFailedFullSubscription()
    {
        var native = (SimConnect)FormatterServices.GetUninitializedObject(typeof(SimConnect));
        GC.SuppressFinalize(native);
        uint packet = 100;
        double now = 0;
        var requests = new List<uint>();
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero, null, _ => { }, _ => { },
            (_, request, definition, enabled) => { requests.Add(request); return ++packet; }, () => now);
        Set(recorder, "_simConnect", native);
        Call(recorder, "RetryRequiredSubscriptions");
        Require(requests.Count == 0, "An adopted handle cannot issue requests before OPEN definitions are registered.");
        Set(recorder, "_connectionSetupComplete", true);
        Call(recorder, "RetryRequiredSubscriptions");
        Require(requests.Count == 1 && requests[0] == 0, "Normal connection requests only Guard until the AGL gate enables Full.");
        Call(recorder, "SetFullTelemetryEnabled", true);
        var full = (RequiredTelemetrySubscription)Get(recorder, "_fullSubscription")!;
        var guard = (RequiredTelemetrySubscription)Get(recorder, "_guardSubscription")!;
        var rejected = full.Packet!.Value;
        Call(recorder, "OnRecvException", native, new SIMCONNECT_RECV_EXCEPTION { dwException = 3, dwSendID = rejected, dwIndex = uint.MaxValue });
        var before = requests.Count;
        Call(recorder, "OnRecvSimobjectData", native, new SIMCONNECT_RECV_SIMOBJECT_DATA
        {
            dwRequestID = 0,
            dwData = new object[] { new SimGuardData { AboveGroundLevelFeet = 100 } },
        });
        Require(guard.Observed && full.RecoveryPending && (bool)Get(recorder, "_recoverStatusAfterFrame")!,
            "A valid guard frame cannot erase the rejected Full subscription.");
        Require(requests.Count == before, "Guard updates cannot bypass retry backoff.");
        now = 1;
        Call(recorder, "RetryRequiredSubscriptions");
        Require(requests.Count == before + 1 && requests[requests.Count-1] == 1 && full.Requested == true,
            "Only the rejected Full subscription is resubmitted.");
        Call(recorder, "RecoverStatusAfterFrame");
        Require((bool)Get(recorder, "_recoverStatusAfterFrame")!, "Submitting a request is not proof of resumed frames.");
        Call(recorder, "OnRecvSimobjectData", native, new SIMCONNECT_RECV_SIMOBJECT_DATA
        {
            dwRequestID = 1,
            dwData = new object[] { new SimFrameData() },
        });
        Require(full.Observed && !(bool)Get(recorder, "_recoverStatusAfterFrame")!, "Full data restores its own stream health.");
        Call(recorder, "Disconnect");
        Require(!full.Reject(rejected, now), "Reconnect cannot inherit an old request failure.");
    }

    private static object? Get(object instance, string name) => RecorderTestAccess.Get(instance, name);
    private static void Set(object instance, string name, object value) => RecorderTestAccess.Set(instance, name, value);
    private static void Call(object instance, string name, params object[] args) => RecorderTestAccess.Call(instance, name, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
