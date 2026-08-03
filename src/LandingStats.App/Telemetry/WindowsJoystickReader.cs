using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LandingStats.Core;
using Microsoft.Win32;

namespace LandingStats.App.Telemetry;

internal sealed class WindowsJoystickReader
{
    private const uint JoyReturnY = 0x00000002;
    private const uint JoyNoError = 0;
    private const int MaximumWinmmJoysticks = 16;

    private readonly Slot[] _slots;

    public WindowsJoystickReader()
    {
        var slots = new List<Slot>();
        var supported = Math.Min(MaximumWinmmJoysticks, unchecked((int)joyGetNumDevs()));
        for (uint joystickId = 0; joystickId < supported && slots.Count < TelemetrySample.CapturedControllerCount; joystickId++)
        {
            var info = NewInfo();
            if (joyGetPosEx(joystickId, ref info) != JoyNoError)
            {
                continue;
            }

            var minimum = 0u;
            var maximum = 65535u;
            var name = "Windows joystick";
            var axisCount = 0u;
            if (joyGetDevCaps(joystickId, out var caps, unchecked((uint)Marshal.SizeOf(typeof(JoyCaps)))) == JoyNoError)
            {
                if (caps.YMaximum > caps.YMinimum)
                {
                    minimum = caps.YMinimum;
                    maximum = caps.YMaximum;
                }

                name = ResolveProductName(caps);
                axisCount = caps.AxisCount;
            }

            slots.Add(new Slot(joystickId, minimum, maximum, name, axisCount));
        }

        _slots = slots.ToArray();
    }

    public bool TryReadYAxis(int slotIndex, out int joystickId, out double percent)
    {
        joystickId = -1;
        percent = 0;
        if (slotIndex < 0 || slotIndex >= _slots.Length)
        {
            return false;
        }

        var slot = _slots[slotIndex];
        var info = NewInfo();
        if (joyGetPosEx(slot.JoystickId, ref info) != JoyNoError)
        {
            return false;
        }

        joystickId = unchecked((int)slot.JoystickId);
        var midpoint = (slot.Minimum + slot.Maximum) / 2.0;
        var halfRange = Math.Max(1.0, (slot.Maximum - slot.Minimum) / 2.0);
        percent = Math.Max(-100, Math.Min(100, (info.YPosition - midpoint) / halfRange * 100.0));
        return true;
    }

    public int Count => _slots.Length;

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

    private static string ResolveProductName(JoyCaps caps)
    {
        if (!string.IsNullOrWhiteSpace(caps.RegistryKey))
        {
            const string oemPath = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\";
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = hive.OpenSubKey(oemPath + caps.RegistryKey);
                    if (key?.GetValue("OEMName") is string oemName && !string.IsNullOrWhiteSpace(oemName))
                    {
                        return oemName.Trim();
                    }
                }
                catch
                {
                    // Device provenance is diagnostic metadata; polling must still work if the registry is unavailable.
                }
            }
        }

        return string.IsNullOrWhiteSpace(caps.ProductName) ? "Windows joystick" : caps.ProductName.Trim();
    }

    private static JoyInfoEx NewInfo()
    {
        return new JoyInfoEx
        {
            Size = unchecked((uint)Marshal.SizeOf(typeof(JoyInfoEx))),
            Flags = JoyReturnY,
        };
    }

    [DllImport("winmm.dll")]
    private static extern uint joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern uint joyGetPosEx(uint joystickId, ref JoyInfoEx info);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint joyGetDevCaps(uint joystickId, out JoyCaps capabilities, uint capabilitiesSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
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
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
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
        public Slot(uint joystickId, uint minimum, uint maximum, string name, uint axisCount)
        {
            JoystickId = joystickId;
            Minimum = minimum;
            Maximum = maximum;
            Name = name;
            AxisCount = axisCount;
        }

        public uint JoystickId { get; }

        public uint Minimum { get; }

        public uint Maximum { get; }

        public string Name { get; }

        public uint AxisCount { get; }
    }
}
