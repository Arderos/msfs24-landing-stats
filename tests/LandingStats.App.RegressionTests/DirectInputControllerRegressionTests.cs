using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class DirectInputControllerRegressionTests
{
    internal static void RunAll()
    {
        var api = new FakeApi();
        for (var i = 0; i < 12; i++) api.Devices.Add(new FakeSource(i + 1, i == 2 ? (byte)0 : (byte)255));
        using (var reader = new DirectInputJoystickReader(api))
        {
            Check(reader.Count == 12, "DirectInput discarded controllers after the eighth device.");
            var sample = new TelemetrySample(); var clocks = new long[reader.Count];
            reader.ReadInto(sample, clocks);
            Check(sample.ControllerCount == 12 && sample.ControllerAxisCount == 8, "Captured controller shape was truncated.");
            Check(sample.RawControllerAxesValid[11 * 8 + 7] && sample.RawControllerAxesPercent[11 * 8 + 7] == 75,
                "The eighth axis of the twelfth device was lost.");
            Check(sample.RawControllerAxesValid[11 * 8 + 6] && sample.RawControllerAxesPercent[11 * 8 + 6] == 50,
                "The seventh physical axis was lost.");
            Check(!sample.RawControllerAxesValid.Skip(2 * 8).Take(8).Any(value => value), "Button-only panel invented axes.");
            var catalog = reader.CreateCatalog(1);
            Check(catalog.Devices[11].InstanceId == api.Devices[11].Identity.InstanceId && catalog.Devices[11].ProductId == api.Devices[11].Identity.ProductId,
                "Device identity was reduced to an enumeration index.");
            Check(catalog.Devices[11].AxisCount == 8 && catalog.Devices[11].Supports(7), "Catalog dropped the last axis.");
            Check(catalog.Devices[2].Axes == 0, "Button panel was mistaken for an axis device.");
            Check(clocks[11] > 0 && clocks[2] == 0, "Capture times do not correspond to actual reads.");

            api.ReverseEnumeration = true;
            reader.BeginRefresh();
            Check(!reader.RefreshNext(out var changed) && !changed && reader.Count == 12, "Partial refresh was published.");
            reader.ReadInto(sample, clocks);
            Check(sample.RawControllerAxesValid[11 * 8 + 7], "Enumeration interrupted existing measurements.");
            while (!reader.RefreshNext(out changed)) { }
            Check(!changed && reader.CreateCatalog(2).Devices.Select(value => value.InstanceId).SequenceEqual(catalog.Devices.Select(value => value.InstanceId)),
                "Enumeration order changed device identity or slots.");
            Check(api.Handles.Take(12).All(value => value.DisposeCount == 1), "Replaced native handles were not disposed exactly once.");

            var disconnected = api.Devices[11];
            disconnected.Readable = false;
            reader.ReadInto(sample, clocks);
            Check(!sample.RawControllerAxesValid.Skip(11 * 8).Any(value => value) && sample.RawControllerAxesPercent.Skip(11 * 8).All(value => value == 0),
                "Read failure reused stale axis values.");
            Check(clocks[11] == 0 && sample.RawControllerDeviceId[11] == -1, "Failed read fabricated a capture time.");
            api.Devices.RemoveAt(11);
            reader.BeginRefresh(); while (!reader.RefreshNext(out changed)) { }
            Check(changed && reader.Count == 11, "Hot-unplug failed to change catalog.");
            reader.ReadInto(sample, new long[reader.Count]);
            Check(sample.ControllerCount == 11 && sample.RawControllerAxesPercent.Length == 88, "Unplug retained the previous controller shape.");

            disconnected.Readable = true;
            api.Devices.Add(disconnected);
            reader.BeginRefresh(); while (!reader.RefreshNext(out changed)) { }
            Check(changed && reader.Count == 12 && reader.CreateCatalog(3).Devices[11].InstanceId == disconnected.Identity.InstanceId,
                "Reconnected hardware did not recover its instance identity.");
            reader.ReadInto(sample, new long[reader.Count]);
            Check(sample.RawControllerAxesValid[11 * 8 + 7] && sample.RawControllerAxesPercent[11 * 8 + 7] == 75,
                "Measurements did not resume after reconnecting the same controller.");
            api.Devices.Add(new FakeSource(21, 255));
            reader.BeginRefresh(); reader.RefreshNext(out _);
            // Disposal must close both live and partially enumerated handles.
        }
        Check(api.Disposed && api.Handles.All(value => value.DisposeCount == 1), "Controller owner leaked or double-released a device.");

        var failed = new FakeApi(); failed.Devices.Add(new FakeSource(1, 255) { OpenFailure = true }); failed.Devices.Add(new FakeSource(2, 255));
        using (var reader = new DirectInputJoystickReader(failed))
        {
            var sample = new TelemetrySample(); reader.ReadInto(sample);
            Check(reader.Count == 2 && !sample.RawControllerAxesValid.Take(8).Any(value => value) && sample.RawControllerAxesValid.Skip(8).All(value => value),
                "One unavailable device stopped or contaminated another controller.");
        }
        var duplicate = new FakeApi(); var same = new FakeSource(1, 255); duplicate.Devices.Add(same); duplicate.Devices.Add(same);
        try { using var unused = new DirectInputJoystickReader(duplicate); throw new Exception("Duplicate device identity was accepted."); }
        catch (InvalidOperationException) { Check(duplicate.Disposed, "Failed initialization did not dispose DirectInput."); }
        Near(-100, NativeDirectInputApi.NormalizeAxis(int.MinValue, int.MinValue, int.MaxValue));
        Near(100, NativeDirectInputApi.NormalizeAxis(int.MaxValue, int.MinValue, int.MaxValue));
        Near(0, NativeDirectInputApi.NormalizeAxis(0, -100, 100));
        Near(-100, NativeDirectInputApi.NormalizeAxis(-200, -100, 100));
        Near(100, NativeDirectInputApi.NormalizeAxis(200, -100, 100));
    }

    // Explicit opt-in: background/nonexclusive polling of attached hardware.
    // It does not open MSFS or change the simulator/controller configuration.
    internal static void RunNativeSmoke(int polls = 30)
    {
        using var reader = new DirectInputJoystickReader();
        if (reader.Count == 0) throw new InvalidOperationException("DirectInput hardware smoke found no attached controllers.");
        var sample = new TelemetrySample(); var clocks = new long[reader.Count];
        var observed = new bool[reader.Count * reader.AxisCount]; var watch = Stopwatch.StartNew();
        var initialCatalog = reader.CreateCatalog(1);
        var refreshes = 0;
        for (var i = 0; i < polls; i++)
        {
            if (i > 0 && i % 500 == 0)
            {
                reader.BeginRefresh();
                while (!reader.RefreshNext(out _)) { }
                Check(reader.CreateCatalog(1).Devices.Select(device => device.InstanceId).SequenceEqual(initialCatalog.Devices.Select(device => device.InstanceId)),
                    "Native refresh changed the attached controller identities.");
                refreshes++;
            }
            reader.ReadInto(sample, clocks);
            for (var axis = 0; axis < observed.Length; axis++) observed[axis] |= sample.RawControllerAxesValid[axis];
            Thread.Sleep(10);
        }
        var catalog = reader.CreateCatalog(1);
        foreach (var device in catalog.Devices)
        {
            Check(Enumerable.Range(0, device.AxisCount).Where(device.Supports).All(axis => observed[device.Slot * reader.AxisCount + axis]),
                "No valid measurement from an advertised axis of " + device.Name);
            var values = Enumerable.Range(0, device.AxisCount).Where(device.Supports).Select(axis =>
                axis + ":" + (sample.RawControllerAxesValid[device.Slot * reader.AxisCount + axis]
                    ? sample.RawControllerAxesPercent[device.Slot * reader.AxisCount + axis].ToString("F4", CultureInfo.InvariantCulture) : "unavailable"));
            Console.WriteLine("directinput: slot=" + device.Slot + " guid=" + device.InstanceId + " product=" + device.ProductId + " name=" + device.Name +
                " axes=" + Enumerable.Range(0, device.AxisCount).Count(device.Supports) + " values=" + string.Join(",", values));
        }
        Check(observed.Any(value => value), "DirectInput enumerated devices but obtained no valid measurements.");
        Console.WriteLine("directinput: devices=" + reader.Count + " valid_axes=" + observed.Count(value => value) + " polls=" + polls + " refreshes=" + refreshes + " elapsed_ms=" + watch.ElapsedMilliseconds);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < 0.0000001, "Native axis normalization failed.");
    private sealed class FakeSource
    {
        internal FakeSource(int id, byte axes)
        { Identity = new DirectInputIdentity(new Guid(id, 0, 0, new byte[8]), new Guid("00000001-0000-0000-0000-000000000000"), "Controller " + id); Axes = axes; }
        internal DirectInputIdentity Identity { get; }
        internal byte Axes { get; }
        internal bool Readable = true, OpenFailure;
    }
    private sealed class FakeApi : IDirectInputApi
    {
        internal readonly List<FakeSource> Devices = new List<FakeSource>();
        internal readonly List<FakeDevice> Handles = new List<FakeDevice>();
        internal bool ReverseEnumeration, Disposed;
        public void PumpMessages() { }
        public IReadOnlyList<DirectInputIdentity> Enumerate() => (ReverseEnumeration ? Devices.AsEnumerable().Reverse() : Devices).Select(value => value.Identity).ToArray();
        public IDirectInputDevice Open(DirectInputIdentity identity)
        {
            var source = Devices.Single(value => value.Identity.InstanceId == identity.InstanceId);
            if (source.OpenFailure) throw new COMException("Fixture device disappeared.", unchecked((int)0x80040209));
            var value = new FakeDevice(source); Handles.Add(value); return value;
        }
        public void Dispose() { Disposed = true; }
    }
    private sealed class FakeDevice : IDirectInputDevice
    {
        private readonly FakeSource _source;
        internal FakeDevice(FakeSource source) { _source = source; }
        internal int DisposeCount;
        public bool Supports(int axis) => (_source.Axes & (1 << axis)) != 0;
        public uint Range(int axis) => Supports(axis) ? 65535u : 0;
        public bool TryRead(double[] values)
        {
            Check(DisposeCount == 0, "Disposed handle was polled.");
            if (!_source.Readable || _source.Axes == 0) return false;
            for (var axis = 0; axis < values.Length; axis++) values[axis] = -100 + axis * 25;
            return true;
        }
        public void Dispose() { DisposeCount++; }
    }
}
