using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LandingStats.App.Telemetry;
using LandingStats.App.TelemetryUpload;
using LandingStats.Core;
using Microsoft.Win32;

namespace LandingStats.App.RegressionTests;

internal static class PhysicalControllerAxesRegressionTests
{
    public static void RunAll()
    {
        Near(-100, WindowsJoystickReader.NormalizeAxis(100, 100, 1100), "minimum");
        Near(0, WindowsJoystickReader.NormalizeAxis(600, 100, 1100), "midpoint");
        Near(100, WindowsJoystickReader.NormalizeAxis(1100, 100, 1100), "maximum");
        Near(100, WindowsJoystickReader.NormalizeAxis(1200, 100, 1100), "clamp");
        Near(0, WindowsJoystickReader.NormalizeAxis(uint.MaxValue - 50, uint.MaxValue - 100, uint.MaxValue), "uint range does not overflow");

        var api = new FakeApi();
        api.Devices[2] = (Caps(6, 15), State(0, 1000, 250, 750, 1000, 500));
        var reader = new WindowsJoystickReader(api);
        var reads = api.Reads;
        var sample = new TelemetrySample();
        reader.ReadInto(sample);
        Require(api.Reads == reads + 1, "only one poll per device for six axes");
        Require(sample.RawControllerDeviceId[0] == 2, "sparse physical device id preserved");
        Require(sample.RawControllerAxesValid.Take(6).All(value => value), "all supported axes valid");
        var expected = new[] { -100.0, 100, -50, 50, 100, 0 };
        for (var axis = 0; axis < 6; axis++) Near(expected[axis], sample.RawControllerAxesPercent[axis], "axis order " + axis);
        Near(100, sample.RawControllerYAxisPercent[0], "legacy Y is identical");
        Require(sample.RawControllerYAxisValid[0], "legacy Y valid");
        Require(sample.RawControllerAxesAgeSeconds.All(age => age == 0), "fresh polling ages");

        api.Devices[7] = (Caps(3, 2), State(250, 750, 999, 1000, 999, 999));
        Require(reader.Refresh(), "hotplug adds separate pedal device");
        reader.ReadInto(sample);
        Require(sample.RawControllerDeviceId[1] == 7, "pedals retain separate device id");
        Require(sample.RawControllerAxesValid[6] && sample.RawControllerAxesValid[7] && sample.RawControllerAxesValid[9], "pedal X/Y/R captured");
        Require(!sample.RawControllerAxesValid[8] && !sample.RawControllerAxesValid[10] && !sample.RawControllerAxesValid[11], "capability flags exclude nonexistent Z/U/V");
        Near(100, sample.RawControllerAxesPercent[9], "pedal R endpoint");

        api.Devices.Remove(2);
        reader.ReadInto(sample);
        Require(!sample.RawControllerAxesValid.Take(6).Any(value => value), "disconnect invalidates all axes");
        Require(sample.RawControllerAxesPercent.Take(6).All(value => value == 0), "disconnect clears stale axis values");
        Require(!sample.RawControllerYAxisValid[0] && sample.RawControllerDeviceId[0] == -1, "disconnect invalidates legacy Y");
        Require(sample.RawControllerDeviceId[1] == 7, "disconnect does not renumber during capture");
        Require(reader.Refresh() && reader.Count == 1, "refresh removes disconnected device");
        Require(!reader.Refresh(), "unchanged refresh is stable");

        var invalidCaps = Caps(6, 15);
        invalidCaps.VMaximum = invalidCaps.VMinimum;
        api.Devices[7] = (invalidCaps, State(0, 0, 0, 0, 0, 0));
        Require(reader.Refresh(), "range change detected");
        reader.ReadInto(sample);
        Require(!sample.RawControllerAxesValid[5], "zero width axis range is invalid");

        for (uint device = 0; device < 16; device++) api.Devices[device] = (Caps(2, 0), State(0, 0, 0, 0, 0, 0));
        reader.Refresh();
        Require(reader.Count == TelemetrySample.CapturedControllerCount, "device slots are bounded");

        VerifyCsv();
        VerifyQueuedSchema();
        VerifyFriendlyNames();
    }

    private static void VerifyFriendlyNames()
    {
        var caps = Caps(6, 15);
        caps.RegistryKey = "DINPUT.DLL";
        const string settings = @"System\CurrentControlSet\Control\MediaResources\Joystick\DINPUT.DLL\CurrentJoystickSettings";
        const string product = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_FAKE&PID_1234";
        var found = WindowsJoystickReader.ResolveProductName(3, caps, (hive, path, name) =>
        {
            if (hive == RegistryHive.CurrentUser && path == settings && name == "Joystick4OEMName") return "VID_FAKE&PID_1234";
            if (hive == RegistryHive.LocalMachine && path == product && name == "OEMName") return "  Separate throttle  ";
            return null;
        });
        Require(found == "Separate throttle", "zero-based WinMM id resolves one-based slot and cross-hive OEM name");
        Require(WindowsJoystickReader.ResolveProductName(2, caps, (_, __, ___) => null) == "Fixture", "missing mapping uses driver product fallback");
        var direct = WindowsJoystickReader.ResolveProductName(3, caps, (_, path, name) =>
            path.EndsWith(@"OEM\DINPUT.DLL", StringComparison.Ordinal) && name == "OEMName" ? "Legacy product" : null);
        Require(direct == "Legacy product", "legacy direct OEM registry layout remains supported");
    }

    private static void VerifyCsv()
    {
        var sample = new TelemetrySample { Sequence = 123, SimulationTimeSeconds = 456.125 };
        for (var device = 0; device < 8; device++)
        {
            sample.RawControllerDeviceId[device] = 10 + device;
            for (var axis = 0; axis < 6; axis++)
            {
                var index = device * 6 + axis;
                sample.RawControllerAxesPercent[index] = index - 24.12345678901234;
                sample.RawControllerAxesValid[index] = index % 3 != 0;
                sample.RawControllerAxesAgeSeconds[index] = index * 0.01;
            }
            sample.RawControllerYAxisPercent[device] = sample.RawControllerAxesPercent[device * 6 + 1];
            sample.RawControllerYAxisValid[device] = sample.RawControllerAxesValid[device * 6 + 1];
            sample.RawControllerYAxisAgeSeconds[device] = sample.RawControllerAxesAgeSeconds[device * 6 + 1];
        }

        var row = TelemetryCsv.Format(sample);
        Require(TelemetryCsv.TryParse(row, out var current), "v6 parses");
        Require(TelemetryCsv.Header.Split(',').Length == TelemetryCsv.V5Header.Split(',').Length + 120, "compact schema adds only five missing axes");
        Require(TelemetryCsv.Header.Split(',').Distinct().Count() == TelemetryCsv.Header.Split(',').Length, "unique column names");
        for (var index = 0; index < 48; index++)
        {
            Require(current.RawControllerAxesPercent[index] == sample.RawControllerAxesPercent[index], "exact axis double round trip");
            Require(current.RawControllerAxesValid[index] == sample.RawControllerAxesValid[index], "axis valid round trip");
            Require(current.RawControllerAxesAgeSeconds[index] == sample.RawControllerAxesAgeSeconds[index], "axis age round trip");
        }

        var oldRow = string.Join(",", row.Split(',').Take(TelemetryCsv.V5Header.Split(',').Length));
        Require(TelemetryCsv.TryParse(oldRow, out var legacy), "v5 parses");
        for (var index = 0; index < 48; index++)
        {
            Require(!legacy.RawControllerAxesValid[index], "v5 does not invent full-axis layout provenance");
            if (index % 6 == 1)
            {
                Require(legacy.RawControllerYAxisValid[index / 6] == sample.RawControllerYAxisValid[index / 6], "v5 Y validity preserved");
                Require(legacy.RawControllerYAxisPercent[index / 6] == sample.RawControllerYAxisPercent[index / 6], "v5 Y preserved");
            }
        }
        Require(!TelemetryCsv.TryParse(row + ",0", out _), "wrong width rejected");
        var invalid = row.Split(',');
        invalid[TelemetryCsv.V5Header.Split(',').Length + 1] = "2";
        Require(!TelemetryCsv.TryParse(string.Join(",", invalid), out _), "new axis validity must be boolean");

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, TelemetryCsv.V5Header + "\n" + oldRow + "\n");
            Require(TelemetryCsv.ReadFile(path).Count == 1, "v5 full file accepted");
            File.WriteAllText(path, TelemetryCsv.Header + "\n" + oldRow + "\n");
            try { TelemetryCsv.ReadFile(path); throw new Exception("header row mismatch accepted"); }
            catch (InvalidDataException) { }
        }
        finally { File.Delete(path); }
    }

    private static void VerifyQueuedSchema()
    {
        var path = Path.GetTempFileName();
        try
        {
            foreach (var version in new[] { 5, 6 })
            {
                using (var file = new FileStream(path, FileMode.Create))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("session.txt").Open()))
                    writer.WriteLine("telemetry_schema=" + version);
                Require(TelemetryUploadClient.ReadCaptureSchema(path) == version, "queued archive retains own schema " + version);
            }

            foreach (var metadata in new[] { "", "telemetry_schema=999", "telemetry_schema=5\ntelemetry_schema=6", new string('x', 8193) })
            {
                using (var file = new FileStream(path, FileMode.Create))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("session.txt").Open())) writer.Write(metadata);
                try { TelemetryUploadClient.ReadCaptureSchema(path); throw new Exception("malformed schema accepted"); }
                catch (InvalidDataException) { }
            }
        }
        finally { File.Delete(path); }
    }

    private static WindowsJoystickReader.JoyCaps Caps(uint count, uint flags) => new WindowsJoystickReader.JoyCaps
    {
        ProductName = "Fixture", RegistryKey = "", OemDriver = "", AxisCount = count, Capabilities = flags,
        XMaximum = 1000, YMaximum = 1000, ZMaximum = 1000, RMaximum = 1000, UMaximum = 1000, VMaximum = 1000,
    };

    private static WindowsJoystickReader.JoyInfoEx State(uint x, uint y, uint z, uint r, uint u, uint v) =>
        new WindowsJoystickReader.JoyInfoEx { XPosition = x, YPosition = y, ZPosition = z, RPosition = r, UPosition = u, VPosition = v };

    private sealed class FakeApi : WindowsJoystickReader.IJoystickApi
    {
        public readonly Dictionary<uint, (WindowsJoystickReader.JoyCaps Caps, WindowsJoystickReader.JoyInfoEx State)> Devices =
            new Dictionary<uint, (WindowsJoystickReader.JoyCaps, WindowsJoystickReader.JoyInfoEx)>();
        public int Reads;
        public uint DeviceCount => 16;
        public bool TryGetState(uint id, out WindowsJoystickReader.JoyInfoEx state)
        {
            Reads++;
            var found = Devices.TryGetValue(id, out var device);
            state = device.State;
            return found;
        }
        public bool TryGetCapabilities(uint id, out WindowsJoystickReader.JoyCaps capabilities)
        {
            var found = Devices.TryGetValue(id, out var device);
            capabilities = device.Caps;
            return found;
        }
    }

    private static void Near(double expected, double actual, string message) => Require(Math.Abs(expected - actual) < 1e-10, message);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
