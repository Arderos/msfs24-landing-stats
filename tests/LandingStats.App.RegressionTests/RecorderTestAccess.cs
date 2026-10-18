using System;
using System.Reflection;
using System.Linq;
using LandingStats.App.Telemetry;

namespace LandingStats.App.RegressionTests;

// Existing integration fixtures inspect both native request state and landing state.
// Resolve the owner explicitly during the mechanical extraction; assertions remain unchanged.
internal static class RecorderTestAccess
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static object Owner(object target, string name)
    {
        if (!(target is SimConnectLandingRecorder recorder) || target.GetType().GetMember(name, Flags).Length != 0) return target;
        var owners = new object[] { recorder.NativeSource, recorder.Coordinator, recorder.FacilityCoordinator }
            .Where(owner => owner.GetType().GetMember(name, Flags).Length != 0).ToArray();
        if (owners.Length > 1) throw new AmbiguousMatchException("Select the production owner explicitly for " + name);
        return owners.Length == 1 ? owners[0] : throw new MissingMemberException("No production owner declares " + name);
    }
    public static object? Get(object target, string name)
    {
        target = Owner(target, name);
        return (target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target);
    }
    public static void Set(object target, string name, object? value)
    {
        target = Owner(target, name);
        (target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().FullName, name)).SetValue(target, value);
    }
    public static object? Call(object target, string name, object?[]? arguments = null)
    {
        target = Owner(target, name);
        return (target.GetType().GetMethod(name, Flags) ?? throw new MissingMethodException(target.GetType().FullName, name)).Invoke(target, arguments);
    }
}
