using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
using Microsoft.Win32;

namespace LandingStats.App.Telemetry;

internal sealed class WindowsJoystickReader : IControllerReader
{
    private const uint JoyReturnAxes = 0x0000003f;
    private const uint JoyNoError = 0;

    private Slot[] _slots = Array.Empty<Slot>();
    private readonly IJoystickApi _api;
    private List<Slot>? _refreshSlots;
    private uint _refreshId, _refreshCount;

    public WindowsJoystickReader() : this(new NativeJoystickApi())
    {
    }

    internal WindowsJoystickReader(IJoystickApi api)
    {
        _api = api;
        Refresh();
    }

    public bool Refresh()
    {
        BeginRefresh();
        while (!RefreshNext(out var changed)) { }
        return _refreshChanged;
    }

    private bool _refreshChanged;
    public void BeginRefresh()
    {
        _refreshSlots = new List<Slot>(); _refreshId = 0;
        _refreshCount = _api.DeviceCount;
    }

    // The same native owner scans at most one ID between regular samples.
    // Commit the catalog atomically after a full scan; active slots remain usable
    // throughout enumeration. A single blocked driver call is still supervised.
    public bool RefreshNext(out bool changed)
    {
        changed = false;
        if (_refreshSlots == null) return true;
        if (_refreshId < _refreshCount)
        {
            var id = _refreshId++;
            if (_api.TryGetState(id, out _) && _api.TryGetCapabilities(id, out var caps))
                _refreshSlots.Add(new Slot(id, caps, ResolveProductName(id, caps, ReadRegistryString)));
        }
        if (_refreshId < _refreshCount) return false;
        var refreshed = _refreshSlots.ToArray(); _refreshSlots = null;
        changed = refreshed.Length != _slots.Length;
        if (!changed)
        {
            for (var index = 0; index < refreshed.Length; index++)
            {
                if (refreshed[index].JoystickId != _slots[index].JoystickId ||
                    !refreshed[index].HasSameAxes(_slots[index]) ||
                    refreshed[index].AxisCount != _slots[index].AxisCount ||
                    !string.Equals(refreshed[index].Name, _slots[index].Name, StringComparison.Ordinal))
                {
                    changed = true;
                    break;
                }
            }
        }

        _slots = refreshed;
        _refreshChanged = changed;
        return true;
    }

    public void ReadInto(TelemetrySample sample, long[]? capturedAt = null)
    {
        sample.ResizeControllers(Count, AxisCount);
        if (capturedAt != null && capturedAt.Length != Count) throw new ArgumentException("Invalid controller clock array.");
        for (var slotIndex = 0; slotIndex < Count; slotIndex++)
        {
            var offset = slotIndex * TelemetrySample.CapturedControllerAxisCount;
            Array.Clear(sample.RawControllerAxesPercent, offset, TelemetrySample.CapturedControllerAxisCount);
            Array.Clear(sample.RawControllerAxesValid, offset, TelemetrySample.CapturedControllerAxisCount);
            Array.Clear(sample.RawControllerAxesAgeSeconds, offset, TelemetrySample.CapturedControllerAxisCount);
            sample.RawControllerDeviceId[slotIndex] = -1;
            if (capturedAt != null) capturedAt[slotIndex] = 0;
            if (slotIndex < _slots.Length)
            {
                var slot = _slots[slotIndex];
                // One native poll supplies every axis at the same receipt time. A disconnected
                // controller never reuses its previous values or masquerades as a centred stick.
                if (_api.TryGetState(slot.JoystickId, out var info))
                {
                    if (capturedAt != null) capturedAt[slotIndex] = Stopwatch.GetTimestamp();
                    sample.RawControllerDeviceId[slotIndex] = unchecked((int)slot.JoystickId);
                    for (var axis = 0; axis < TelemetrySample.CapturedControllerAxisCount; axis++)
                    {
                        if (!slot.Supported[axis]) continue;
                        sample.RawControllerAxesPercent[offset + axis] = NormalizeAxis(
                            info.Axis(axis), slot.Minimum[axis], slot.Maximum[axis]);
                        sample.RawControllerAxesValid[offset + axis] = true;
                    }
                }
            }

            sample.RawControllerYAxisPercent[slotIndex] = sample.RawControllerAxesPercent[offset + 1];
            sample.RawControllerYAxisValid[slotIndex] = sample.RawControllerAxesValid[offset + 1];
            sample.RawControllerYAxisAgeSeconds[slotIndex] = sample.RawControllerAxesAgeSeconds[offset + 1];
        }
    }

    internal static double NormalizeAxis(uint position, uint minimum, uint maximum)
    {
        if (maximum <= minimum) return 0;
        var midpoint = ((double)minimum + maximum) / 2.0;
        var halfRange = ((double)maximum - minimum) / 2.0;
        return Math.Max(-100, Math.Min(100, (position - midpoint) / halfRange * 100.0));
    }

    public int Count => _slots.Length;
    public int AxisCount => TelemetrySample.CapturedControllerAxisCount;
    public void Dispose() { }

    public ControllerCatalogMessage CreateCatalog(long revision)
    {
        var devices = new List<ControllerDescriptor>();
        for (var index = 0; index < _slots.Length; index++)
        {
            var slot = _slots[index]; byte axes = 0;
            for (var axis = 0; axis < slot.Supported.Length; axis++) if (slot.Supported[axis]) axes |= (byte)(1 << axis);
            devices.Add(new ControllerDescriptor(index, (int)slot.JoystickId,
                OptionalMetadataText.Sanitize(slot.Name, 512, "Windows joystick"),
                OptionalMetadataText.Sanitize(slot.ProductKey, 512, "unknown"), axes, slot.Minimum, slot.Maximum));
        }
        return new ControllerCatalogMessage(revision, devices);
    }

    public string DescribeSource(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _slots.Length)
        {
            return "winmm:unknown";
        }

        var slot = _slots[slotIndex];
        var axes = slot.AxisCount == 1 ? "1 axis" : $"{slot.AxisCount} axes";
        return $"winmm:{slot.JoystickId}: {slot.Name} ({axes})";
    }

    internal static string ResolveProductName(uint joystickId, JoyCaps caps, Func<RegistryHive, string, string, string?> readString)
    {
        if (!string.IsNullOrWhiteSpace(caps.RegistryKey))
        {
            const string oemPath = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\";
            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            var settingsPath = @"System\CurrentControlSet\Control\MediaResources\Joystick\" + caps.RegistryKey + @"\CurrentJoystickSettings";
            foreach (var hive in hives)
            {
                // szRegKey identifies the driver, not the product. WinMM IDs are zero-based;
                // CurrentJoystickSettings maps one-based slots to their OEM product keys.
                var productKey = readString(hive, settingsPath, "Joystick" + (joystickId + 1) + "OEMName");
                if (string.IsNullOrWhiteSpace(productKey)) continue;
                foreach (var oemHive in hives)
                {
                    var oemName = readString(oemHive, oemPath + productKey, "OEMName");
                    if (!string.IsNullOrWhiteSpace(oemName)) return oemName!.Trim();
                }
            }
            foreach (var hive in hives)
            {
                var directName = readString(hive, oemPath + caps.RegistryKey, "OEMName");
                if (!string.IsNullOrWhiteSpace(directName)) return directName!.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(caps.ProductName) ? "Windows joystick" : caps.ProductName.Trim();
    }

    private static string? ReadRegistryString(RegistryHive hive, string path, string name)
    {
        try
        {
            using var key = (hive == RegistryHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch
        {
            // Provenance is diagnostic metadata; a registry ACL must never stop input capture.
            return null;
        }
    }

    private static JoyInfoEx NewInfo()
    {
        return new JoyInfoEx
        {
            Size = unchecked((uint)Marshal.SizeOf(typeof(JoyInfoEx))),
            Flags = JoyReturnAxes,
        };
    }

    [DllImport("winmm.dll")]
    private static extern uint joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern uint joyGetPosEx(uint joystickId, ref JoyInfoEx info);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint joyGetDevCaps(UIntPtr joystickId, out JoyCaps capabilities, uint capabilitiesSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct JoyInfoEx
    {
        public uint Size;
        public uint Flags;
        public uint XPosition;
        public uint YPosition;
        public uint ZPosition;
        public uint RPosition;
        public uint UPosition;
        public uint VPosition;
        public uint Buttons;
        public uint ButtonNumber;
        public uint PointOfView;
        public uint Reserved1;
        public uint Reserved2;

        public uint Axis(int index) => index switch
        {
            0 => XPosition, 1 => YPosition, 2 => ZPosition,
            3 => RPosition, 4 => UPosition, 5 => VPosition,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct JoyCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;

        public uint XMinimum;
        public uint XMaximum;
        public uint YMinimum;
        public uint YMaximum;
        public uint ZMinimum;
        public uint ZMaximum;
        public uint ButtonCount;
        public uint PeriodMinimum;
        public uint PeriodMaximum;
        public uint RMinimum;
        public uint RMaximum;
        public uint UMinimum;
        public uint UMaximum;
        public uint VMinimum;
        public uint VMaximum;
        public uint Capabilities;
        public uint MaximumAxes;
        public uint AxisCount;
        public uint MaximumButtons;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string RegistryKey;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string OemDriver;
    }

    private sealed class Slot
    {
        public Slot(uint joystickId, JoyCaps caps, string name)
        {
            JoystickId = joystickId;
            Minimum = new[] { caps.XMinimum, caps.YMinimum, caps.ZMinimum, caps.RMinimum, caps.UMinimum, caps.VMinimum };
            Maximum = new[] { caps.XMaximum, caps.YMaximum, caps.ZMaximum, caps.RMaximum, caps.UMaximum, caps.VMaximum };
            // Z/R/U/V presence comes from JOYCAPS_HAS*, not a guess based on axis count.
            Supported = new[] { caps.AxisCount >= 1, caps.AxisCount >= 2,
                (caps.Capabilities & 1) != 0, (caps.Capabilities & 2) != 0,
                (caps.Capabilities & 4) != 0, (caps.Capabilities & 8) != 0 };
            for (var axis = 0; axis < Supported.Length; axis++)
                Supported[axis] &= Maximum[axis] > Minimum[axis];
            Name = name;
            ProductKey = caps.ManufacturerId.ToString("x4", CultureInfo.InvariantCulture) + ":" + caps.ProductId.ToString("x4", CultureInfo.InvariantCulture) +
                ":" + (caps.RegistryKey ?? "") + ":" + (caps.ProductName ?? "");
            AxisCount = caps.AxisCount;
        }

        public uint JoystickId { get; }

        public uint[] Minimum { get; }

        public uint[] Maximum { get; }

        public bool[] Supported { get; }

        public string Name { get; }
        public string ProductKey { get; }

        public uint AxisCount { get; }

        public bool HasSameAxes(Slot other)
        {
            if (ProductKey != other.ProductKey) return false;
            for (var axis = 0; axis < Supported.Length; axis++)
                if (Minimum[axis] != other.Minimum[axis] || Maximum[axis] != other.Maximum[axis] ||
                    Supported[axis] != other.Supported[axis]) return false;
            return true;
        }
    }

    internal interface IJoystickApi
    {
        uint DeviceCount { get; }
        bool TryGetState(uint joystickId, out JoyInfoEx state);
        bool TryGetCapabilities(uint joystickId, out JoyCaps capabilities);
    }

    private sealed class NativeJoystickApi : IJoystickApi
    {
        public uint DeviceCount => joyGetNumDevs();

        public bool TryGetState(uint joystickId, out JoyInfoEx state)
        {
            state = NewInfo();
            return joyGetPosEx(joystickId, ref state) == JoyNoError;
        }

        public bool TryGetCapabilities(uint joystickId, out JoyCaps capabilities) =>
            joyGetDevCaps(new UIntPtr(joystickId), out capabilities, unchecked((uint)Marshal.SizeOf(typeof(JoyCaps)))) == JoyNoError;
    }
}
