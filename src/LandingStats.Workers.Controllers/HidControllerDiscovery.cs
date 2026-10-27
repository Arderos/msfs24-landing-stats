using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace LandingStats.App.Telemetry;

internal sealed class HidControllerEndpoint
{
    internal HidControllerEndpoint(string path, ushort usagePage, ushort usage)
    { Path = path; UsagePage = usagePage; Usage = usage; }
    internal string Path { get; }
    internal ushort UsagePage { get; }
    internal ushort Usage { get; }
    // HID Usage Tables: Generic Desktop joystick/gamepad/multi-axis controller;
    // Simulation Controls vehicle/flight collections and Game Controls TLCs.
    internal bool IsGameController => UsagePage == 1 && (Usage == 4 || Usage == 5 || Usage == 8) ||
        UsagePage == 2 && (Usage >= 1 && Usage <= 12 || Usage >= 0x20 && Usage <= 0x25) ||
        UsagePage == 5 && Usage >= 1 && Usage <= 3;
}

internal interface IDirectInputDiscoveryDevice : IDisposable
{
    string GetInterfacePath();
    DirectInputIdentity GetIdentity();
}

internal static class HidControllerDiscovery
{
    // A cached GUID is only a candidate. The live DirectInput interface path
    // must match an attached game-controller TLC before requesting its name.
    // In particular, matching VID/PID is insufficient for composite devices.
    internal static IReadOnlyList<DirectInputIdentity> Discover(IEnumerable<HidControllerEndpoint> endpoints,
        IEnumerable<Guid> candidates, Func<Guid, IDirectInputDiscoveryDevice> open, Action<string> report)
    {
        var remaining = new HashSet<string>(endpoints.Where(item => item.IsGameController).Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        var result = new List<DirectInputIdentity>();
        foreach (var id in candidates.Where(id => id != Guid.Empty).Distinct())
        {
            if (remaining.Count == 0) break;
            try
            {
                using var device = open(id);
                var path = device.GetInterfacePath();
                if (!remaining.Contains(path)) continue;
                var identity = device.GetIdentity();
                if (identity.InstanceId != id) throw new InvalidOperationException("DirectInput returned a different instance GUID.");
                result.Add(identity);
                remaining.Remove(path);
            }
            catch (COMException error)
            {
                // Stale cached GUIDs and a device unplugged during discovery are
                // expected. Never fall back to global EnumDevices on failure.
                report("candidate_unavailable instance=" + id + " hresult=0x" + error.ErrorCode.ToString("X8") + " message=" + error.Message);
            }
        }
        foreach (var path in remaining)
            report("controller_unresolved path=" + path + " reason=no_live_DirectInput_instance_match");
        return result;
    }
}

internal sealed partial class NativeDirectInputApi
{
    private string[]? _discoveryPaths;
    private IReadOnlyList<DirectInputIdentity>? _discoveredControllers;

    private IReadOnlyList<DirectInputIdentity> DiscoverGameControllers()
    {
        var endpoints = RawInputEndpoints.Read();
        var paths = endpoints.Where(item => item.IsGameController).Select(item => item.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (_discoveryPaths != null && _discoveredControllers != null &&
            _discoveredControllers.Count == paths.Length && _discoveryPaths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
            return _discoveredControllers;

        // GetConfig reads the Windows joystick configuration and lets DirectInput
        // refresh its HID instance cache without EnumDevices/GetDeviceInfo on
        // unrelated HID collections. Read all configured IDs, then supplement
        // the WinMM-sized configuration table with persisted instance GUIDs.
        var candidates = paths.Length == 0 ? Array.Empty<Guid>() : ReadInstanceCandidates();
        var result = HidControllerDiscovery.Discover(endpoints, candidates, id => new DiscoveryDevice(_input, id), message => Trace.TraceWarning("{0}", message));
        _discoveryPaths = paths;
        _discoveredControllers = result;
        return result;
    }

    private Guid[] ReadInstanceCandidates()
    {
        var result = new HashSet<Guid>();
        var iid = new Guid("EB0D7DFA-1990-4F27-B4D6-EDF2EEC4A44C");
        var config = IntPtr.Zero;
        Check(Marshal.QueryInterface(_input, ref iid, out config), "QueryInterface(IDirectInputJoyConfig8)");
        try
        {
            var read = Method<GetJoyConfig>(config, 11);
            for (uint index = 0; ; index++)
            {
                var value = new JoyConfig { Size = (uint)Marshal.SizeOf<JoyConfig>() };
                var hr = read(config, index, ref value, 1);
                if (hr == unchecked((int)0x80070103)) break; // DIERR_NOMOREITEMS
                if (hr == unchecked((int)0x80070002)) continue; // Unassigned joystick ID.
                Check(hr, "GetConfig(DIJC_GUIDINSTANCE)");
                if (value.InstanceId != Guid.Empty) result.Add(value.InstanceId);
            }
        }
        finally { Marshal.Release(config); }
        foreach (var id in DirectInputInstanceCache.ReadCandidates()) result.Add(id);
        return result.ToArray();
    }

    private sealed class DiscoveryDevice : IDirectInputDiscoveryDevice
    {
        private IntPtr _device;
        internal DiscoveryDevice(IntPtr input, Guid id)
        {
            Check(Method<CreateDevice>(input, 3)(input, ref id, out _device, IntPtr.Zero), "CreateDevice(discovery)");
        }
        public string GetInterfacePath()
        {
            var value = new GuidAndPath { Size = (uint)Marshal.SizeOf<GuidAndPath>(), HeaderSize = 16 };
            Check(Method<GetGuidAndPath>(_device, 5)(_device, new IntPtr(12), ref value), "GetProperty(DIPROP_GUIDANDPATH)");
            return value.Path ?? throw new InvalidOperationException("DirectInput returned no interface path.");
        }
        public DirectInputIdentity GetIdentity()
        {
            var value = new DeviceInstance { Size = (uint)Marshal.SizeOf<DeviceInstance>() };
            Check(Method<GetDeviceInfo>(_device, 15)(_device, ref value), "GetDeviceInfo(game-controller)");
            var result = new DirectInputIdentity(value.InstanceId, value.ProductId,
                (string.IsNullOrWhiteSpace(value.ProductName) ? value.InstanceName : value.ProductName) ?? string.Empty);
            return result;
        }
        public void Dispose()
        {
            if (_device == IntPtr.Zero) return;
            Marshal.Release(_device); _device = IntPtr.Zero;
        }
    }

    // DIJOYCONFIG from dinputd.h. Only dwSize and guidInstance are requested;
    // the remaining 1156 bytes retain the exact native structure size/alignment.
    [StructLayout(LayoutKind.Explicit, Size = 1176)]
    private struct JoyConfig
    {
        [FieldOffset(0)] internal uint Size;
        [FieldOffset(4)] internal Guid InstanceId;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct GuidAndPath
    {
        internal uint Size, HeaderSize, Object, How;
        internal Guid ClassId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Path;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetJoyConfig(IntPtr self, uint index, ref JoyConfig value, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGuidAndPath(IntPtr self, IntPtr property, ref GuidAndPath value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDeviceInfo(IntPtr self, ref DeviceInstance value);
}

// Windows exposes no supported conversion from a Raw Input interface path to a
// DirectInput instance GUID. GetConfig is limited by the legacy joystick table.
// Keep the Windows persistence compatibility lookup here, read-only. Its data
// never establishes identity: each GUID is validated through DIPROP_GUIDANDPATH
// against the live Raw Input snapshot. Missing/malformed entries are diagnosed;
// never synthesize a GUID, guess from VID/PID, or invoke global EnumDevices.
internal static class DirectInputInstanceCache
{
    internal static IEnumerable<Guid> ReadCandidates()
    {
        var result = new HashSet<Guid>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput", false);
            if (root == null) { Trace.TraceWarning("DirectInput instance cache is missing."); return result; }
            foreach (var product in root.GetSubKeyNames())
            {
                using var calibration = root.OpenSubKey(product + @"\Calibration", false);
                if (calibration == null) continue;
                foreach (var instance in calibration.GetSubKeyNames())
                {
                    using var key = calibration.OpenSubKey(instance, false);
                    var bytes = key?.GetValue("GUID") as byte[];
                    if (bytes == null || bytes.Length != 16)
                    { Trace.TraceWarning("Invalid DirectInput instance cache entry: {0}\\Calibration\\{1}", product, instance); continue; }
                    var id = new Guid(bytes);
                    if (id != Guid.Empty) result.Add(id);
                }
            }
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is SecurityException)
        {
            Trace.TraceWarning("DirectInput instance cache unavailable: {0}", error);
        }
        return result;
    }
}

internal static class RawInputEndpoints
{
    internal static IReadOnlyList<HidControllerEndpoint> Read()
    {
        var result = new List<HidControllerEndpoint>();
        uint count = 0;
        var itemSize = (uint)Marshal.SizeOf<RawDevice>();
        Check(GetRawInputDeviceList(IntPtr.Zero, ref count, itemSize), "GetRawInputDeviceList(size)");
        while (count != 0)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)(count * itemSize)));
            try
            {
                var read = GetRawInputDeviceList(buffer, ref count, itemSize);
                if (read == uint.MaxValue && Marshal.GetLastWin32Error() == 122) continue; // List grew: use the returned required size.
                Check(read, "GetRawInputDeviceList");
                for (var i = 0; i < read; i++)
                {
                    var item = Marshal.PtrToStructure<RawDevice>(IntPtr.Add(buffer, checked((int)(i * itemSize))));
                    if (item.Type != 2) continue; // RIM_TYPEHID
                    try
                    {
                        var info = new RawInfo { Size = (uint)Marshal.SizeOf<RawInfo>() };
                        var infoSize = info.Size;
                        Check(GetRawInputDeviceInfo(item.Handle, 0x2000000b, ref info, ref infoSize), "GetRawInputDeviceInfo(RIDI_DEVICEINFO)");
                        var path = ReadPath(item.Handle);
                        var endpoint = new HidControllerEndpoint(path, info.UsagePage, info.Usage);
                        result.Add(endpoint);
                    }
                    catch (Win32Exception error)
                    { Trace.TraceWarning("Raw Input device {0} unavailable: {1}", item.Handle, error); }
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }
    private static string ReadPath(IntPtr handle)
    {
        uint length = 0;
        Check(GetRawInputDeviceInfo(handle, 0x20000007, IntPtr.Zero, ref length), "GetRawInputDeviceInfo(path size)");
        var buffer = Marshal.AllocHGlobal(checked(((int)length + 1) * 2));
        try
        {
            Check(GetRawInputDeviceInfo(handle, 0x20000007, buffer, ref length), "GetRawInputDeviceInfo(RIDI_DEVICENAME)");
            return Marshal.PtrToStringUni(buffer, checked((int)length))!.TrimEnd('\0');
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void Check(uint value, string operation)
    { if (value == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { internal IntPtr Handle; internal uint Type; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct RawInfo
    {
        [FieldOffset(0)] internal uint Size;
        [FieldOffset(4)] internal uint Type;
        [FieldOffset(8)] internal uint VendorId;
        [FieldOffset(12)] internal uint ProductId;
        [FieldOffset(20)] internal ushort UsagePage;
        [FieldOffset(22)] internal ushort Usage;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList(IntPtr devices, ref uint count, uint itemSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)] private static extern uint GetRawInputDeviceInfo(IntPtr handle, uint command, ref RawInfo info, ref uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)] private static extern uint GetRawInputDeviceInfo(IntPtr handle, uint command, IntPtr data, ref uint size);
}
