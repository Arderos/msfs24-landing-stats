using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LandingStats.App.Telemetry;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.RegressionTests;

internal static class RecorderConnectionRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        SimulatorWorkerRegressionTests.RunOwner();


        CloseWaitsForOutermostNativeDispatch();
        ReceiveFailureClosesAfterUnwinding();
        RetiredCallbacksCannotCloseReplacement();
    }

    private static void Attempt(SimConnectLandingRecorder recorder) =>
        RecorderTestAccess.Call(recorder, "TryConnect", null);

    private static SimConnect ConnectionIdentity()
    {
        // Only an identity token: injected receive/dispose delegates replace all
        // native operations. Never construct a real connection in these tests.
        var connection = (SimConnect)FormatterServices.GetUninitializedObject(typeof(SimConnect));
        GC.SuppressFinalize(connection);
        return connection;
    }

    private static void CloseWaitsForOutermostNativeDispatch()
    {
        foreach (var action in new[] { "quit", "disconnect", "dispose" })
        {
            var native = ConnectionIdentity();
            var depth = 0;
            var closes = 0;
            var exits = 0;
            var opens = 0;
            SimConnectLandingRecorder? recorder = null;
            recorder = new SimConnectLandingRecorder(IntPtr.Zero,
                () => { opens++; throw new InvalidOperationException("Unexpected open."); },
                connection =>
                {
                    depth++;
                    try
                    {
                        if (depth == 1)
                        {
                            var nestedHandled = false;
                            recorder!.HandleWindowMessage(SimConnectLandingRecorder.WindowMessage, ref nestedHandled);
                            Require(nestedHandled && closes == 0, "Nested dispatch must not release its parent's native handle.");
                            Attempt(recorder);
                            Require(opens == 0, "A replacement cannot start while old native dispatch is active.");
                        }
                        else
                        {
                            if (action == "quit")
                            {
                                Invoke(recorder!, "OnRecvQuit", connection, null!);
                                Invoke(recorder!, "OnRecvQuit", connection, null!);
                            }
                            else if (action == "dispose") recorder!.Dispose();
                            else Invoke(recorder!, "Disconnect");
                            Require(closes == 0, "Closing inside a callback must defer native disposal.");
                            Require(Current(recorder!) == null, "A retired connection must immediately reject later callbacks.");
                        }
                    }
                    finally { depth--; }
                },
                connection =>
                {
                    Require(depth == 0 && ReferenceEquals(connection, native), "Only the unwound native owner may close.");
                    Attempt(recorder!);
                    Require(opens == 0, "Replacement construction cannot overlap native Close either.");
                    closes++;
                });
            using (recorder)
            {
                recorder.SimulatorExited += (_, __) => exits++;
                RecorderTestAccess.Set(recorder, "_simConnect", native);
                var handled = false;
                recorder.HandleWindowMessage(SimConnectLandingRecorder.WindowMessage, ref handled);
                Require(handled && closes == 1, "Native handle must be closed once after the outer dispatch returns.");
                Require(exits == (action == "quit" ? 1 : 0), "Only a real current-generation QUIT notifies simulator exit once.");
                recorder.Dispose();
                Require(closes == 1, "Repeated shutdown must not close the handle twice.");
            }
        }
    }

    private static void ReceiveFailureClosesAfterUnwinding()
    {
        var inNative = false;
        var closes = 0;
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero, null,
            _ =>
            {
                inNative = true;
                try { throw new InvalidOperationException("Injected dispatch failure."); }
                finally { inNative = false; }
            },
            _ => { Require(!inNative, "Receive error must unwind before disposal."); closes++; });
        RecorderTestAccess.Set(recorder, "_simConnect", ConnectionIdentity());
        var handled = false;
        recorder.HandleWindowMessage(SimConnectLandingRecorder.WindowMessage, ref handled);
        Require(handled && closes == 1 && Current(recorder) == null, "Receive error must close and allow recovery.");
    }

    private static void RetiredCallbacksCannotCloseReplacement()
    {
        var old = ConnectionIdentity();
        var current = ConnectionIdentity();
        var exits = 0;
        var closes = 0;
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero, null, _ => { }, _ => closes++);
        recorder.SimulatorExited += (_, __) => exits++;
        RecorderTestAccess.Set(recorder, "_simConnect", current);
        Invoke(recorder, "OnRecvQuit", old, null!);
        Invoke(recorder, "OnRecvSimobjectData", old, null!);
        Require(ReferenceEquals(Current(recorder), current) && closes == 0 && exits == 0,
            "Late QUIT/data cannot close or change a replacement connection.");
    }

    private static object? Current(SimConnectLandingRecorder recorder) =>
        RecorderTestAccess.Get(recorder, "_simConnect");

    private static void Invoke(SimConnectLandingRecorder recorder, string method, params object[] args) =>
        RecorderTestAccess.Call(recorder, method, args);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
