using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Telemetry;

internal interface IControllerReader : IDisposable
{
    int Count { get; }
    int AxisCount { get; }
    void BeginRefresh();
    bool RefreshNext(out bool changed);
    void ReadInto(TelemetrySample sample, long[]? capturedAt = null);
    ControllerCatalogMessage CreateCatalog(long revision);
}

/// <summary>One owner enumerates and polls DirectInput devices. Slots are scoped
/// to a catalog; the instance GUID identifies a device across enumerations.</summary>
internal sealed class DirectInputJoystickReader : IControllerReader
{
    internal const int StandardAxisCount = 8;
    private readonly IDirectInputApi _api;
    private Slot[] _slots = Array.Empty<Slot>();
    private DirectInputIdentity[]? _refreshDevices;
    private List<Slot>? _refreshSlots;
    private int _refreshIndex;
    private bool _disposed;

    internal DirectInputJoystickReader() : this(new NativeDirectInputApi()) { }
    internal DirectInputJoystickReader(IDirectInputApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        try { BeginRefresh(); while (!RefreshNext(out _)) { } }
        catch { Dispose(); throw; }
    }
    public int Count => _slots.Length;
    public int AxisCount => StandardAxisCount;
    public void BeginRefresh()
    {
        ThrowIfDisposed();
        if (_refreshSlots != null) throw new InvalidOperationException("Controller enumeration is already in progress.");
        _refreshDevices = _api.Enumerate().OrderBy(device => device.InstanceId).ToArray();
        if (_refreshDevices.Select(value => value.InstanceId).Distinct().Count() != _refreshDevices.Length)
            throw new InvalidOperationException("DirectInput returned duplicate device identities.");
        _refreshSlots = new List<Slot>(_refreshDevices.Length); _refreshIndex = 0;
    }
    // Open at most one device between polls. Publish the complete new catalog
    // atomically, retaining the old handles throughout enumeration.
    public bool RefreshNext(out bool changed)
    {
        ThrowIfDisposed(); changed = false;
        if (_refreshSlots == null || _refreshDevices == null) return true;
        if (_refreshIndex < _refreshDevices.Length)
        {
            var identity = _refreshDevices[_refreshIndex++];
            IDirectInputDevice? device = null;
            try { device = _api.Open(identity); }
            catch (COMException error)
            { Trace.TraceWarning("DirectInput cannot open {0} ({1}): 0x{2:X8} {3}", identity.Name, identity.InstanceId, error.HResult, error.Message); }
            _refreshSlots.Add(new Slot(identity, device));
        }
        if (_refreshIndex < _refreshDevices.Length) return false;
        var next = _refreshSlots.ToArray(); _refreshSlots = null; _refreshDevices = null;
        changed = next.Length != _slots.Length || next.Where((slot, index) => !slot.SameDescriptor(_slots[index])).Any();
        var previous = _slots; _slots = next;
        foreach (var slot in previous) slot.Device?.Dispose();
        return true;
    }
    public void ReadInto(TelemetrySample sample, long[]? capturedAt = null)
    {
        ThrowIfDisposed();
        if (capturedAt != null && capturedAt.Length != Count) throw new ArgumentException("Invalid controller clock array.");
        _api.PumpMessages();
        sample.ResizeControllers(Count, AxisCount);
        Array.Clear(sample.RawControllerAxesPercent, 0, sample.RawControllerAxesPercent.Length);
        Array.Clear(sample.RawControllerAxesValid, 0, sample.RawControllerAxesValid.Length);
        Array.Clear(sample.RawControllerAxesAgeSeconds, 0, sample.RawControllerAxesAgeSeconds.Length);
        for (var index = 0; index < _slots.Length; index++)
        {
            sample.RawControllerDeviceId[index] = -1;
            if (capturedAt != null) capturedAt[index] = 0;
            var slot = _slots[index];
            if (slot.Device != null && slot.Device.TryRead(slot.Values))
            {
                if (capturedAt != null) capturedAt[index] = Stopwatch.GetTimestamp();
                sample.RawControllerDeviceId[index] = index;
                for (var axis = 0; axis < AxisCount; axis++)
                {
                    if (!slot.Device.Supports(axis)) continue;
                    var offset = index * AxisCount + axis; var value = slot.Values[axis];
                    if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 100)
                        throw new InvalidOperationException("DirectInput returned an invalid normalized axis.");
                    sample.RawControllerAxesPercent[offset] = value;
                    sample.RawControllerAxesValid[offset] = true;
                }
            }
            var y = index * AxisCount + 1;
            sample.RawControllerYAxisPercent[index] = sample.RawControllerAxesPercent[y];
            sample.RawControllerYAxisValid[index] = sample.RawControllerAxesValid[y];
            sample.RawControllerYAxisAgeSeconds[index] = 0;
        }
    }
    public ControllerCatalogMessage CreateCatalog(long revision)
    { ThrowIfDisposed(); return new ControllerCatalogMessage(revision, _slots.Select((slot, index) => slot.Descriptor(index))); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots) slot.Device?.Dispose();
        if (_refreshSlots != null) foreach (var slot in _refreshSlots) slot.Device?.Dispose();
        _slots = Array.Empty<Slot>(); _refreshSlots = null; _refreshDevices = null; _api.Dispose();
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(DirectInputJoystickReader)); }
    private sealed class Slot
    {
        internal readonly DirectInputIdentity Identity;
        internal readonly IDirectInputDevice? Device;
        internal readonly double[] Values = new double[StandardAxisCount];
        internal Slot(DirectInputIdentity identity, IDirectInputDevice? device) { Identity = identity; Device = device; }
        internal bool SameDescriptor(Slot other)
        {
            if (!Identity.Equals(other.Identity)) return false;
            for (var axis = 0; axis < StandardAxisCount; axis++)
                if ((Device?.Supports(axis) ?? false) != (other.Device?.Supports(axis) ?? false) ||
                    (Device?.Range(axis) ?? 0) != (other.Device?.Range(axis) ?? 0)) return false;
            return true;
        }
        internal ControllerDescriptor Descriptor(int slot)
        {
            byte supported = 0; var maximum = new uint[StandardAxisCount];
            for (var axis = 0; axis < maximum.Length; axis++)
                if (Device?.Supports(axis) == true) { supported |= (byte)(1 << axis); maximum[axis] = Device.Range(axis); }
            // The stored interval is translated to zero; native normalization
            // retains its signed minimum, so negative native ranges also work.
            return new ControllerDescriptor(slot, slot, OptionalMetadataText.Sanitize(Identity.Name, 512, "DirectInput controller"),
                Identity.ProductId.ToString("D"), supported, new uint[StandardAxisCount], maximum, Identity.InstanceId, Identity.ProductId);
        }
    }
}

internal sealed class DirectInputIdentity : IEquatable<DirectInputIdentity>
{
    internal DirectInputIdentity(Guid instanceId, Guid productId, string name)
    { if (instanceId == Guid.Empty) throw new ArgumentException("Missing DirectInput instance GUID."); InstanceId = instanceId; ProductId = productId; Name = name; }
    internal Guid InstanceId { get; }
    internal Guid ProductId { get; }
    internal string Name { get; }
    public bool Equals(DirectInputIdentity? other) => other != null && InstanceId == other.InstanceId && ProductId == other.ProductId && Name == other.Name;
    public override bool Equals(object? other) => Equals(other as DirectInputIdentity);
    public override int GetHashCode() => InstanceId.GetHashCode();
}
internal interface IDirectInputApi : IDisposable
{
    void PumpMessages();
    IReadOnlyList<DirectInputIdentity> Enumerate();
    IDirectInputDevice Open(DirectInputIdentity identity);
}
internal interface IDirectInputDevice : IDisposable
{
    bool Supports(int axis);
    uint Range(int axis);
    bool TryRead(double[] values);
}

/// <summary>DirectInput8W ABI from Windows SDK dinput.h. This reader never writes
/// calibration, action mappings, force feedback, or exclusive device settings.</summary>
internal sealed partial class NativeDirectInputApi : IDirectInputApi
{
    private IntPtr _input, _window;
    internal NativeDirectInputApi()
    {
        try
        {
            var instance = GetModuleHandle(null);
            var iid = new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");
            Check(DirectInput8Create(instance, 0x0800, ref iid, out _input, IntPtr.Zero), "DirectInput8Create");
            // SetCooperativeLevel requires a top-level HWND owned by this
            // process. This window is never shown or activated.
            _window = CreateWindowEx(0, "STATIC", "Landing Stats controller capture", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }
    public void PumpMessages()
    {
        if (_window == IntPtr.Zero) return;
        while (PeekMessage(out var message, _window, 0, 0, 1))
        { TranslateMessage(ref message); DispatchMessage(ref message); }
    }
    public IReadOnlyList<DirectInputIdentity> Enumerate()
    {
        if (_input == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeDirectInputApi));
        return DiscoverGameControllers();
    }
    public IDirectInputDevice Open(DirectInputIdentity identity)
    {
        if (_input == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeDirectInputApi));
        var id = identity.InstanceId;
        Check(Method<CreateDevice>(_input, 3)(_input, ref id, out var pointer, IntPtr.Zero), "CreateDevice");
        return new NativeDevice(pointer, _window, identity);
    }
    public void Dispose()
    {
        // The reader disposes all device handles before their shared HWND.
        if (_input != IntPtr.Zero) { Marshal.Release(_input); _input = IntPtr.Zero; }
        if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
    }
    internal static double NormalizeAxis(int position, int minimum, int maximum)
    {
        if (maximum <= minimum) throw new ArgumentException("Invalid native axis range.");
        return Math.Max(-100, Math.Min(100, ((double)position - minimum) / ((double)maximum - minimum) * 200 - 100));
    }
    private sealed class NativeDevice : IDirectInputDevice
    {
        private IntPtr _device;
        private readonly DirectInputIdentity _identity;
        private readonly AxisObject?[] _axes = new AxisObject?[DirectInputJoystickReader.StandardAxisCount];
        private readonly int[] _state = new int[DirectInputJoystickReader.StandardAxisCount];
        private DeviceCall? _poll, _acquire;
        private ReadState? _read;
        private int _lastError;
        private bool _hasAxes;
        internal NativeDevice(IntPtr device, IntPtr window, DirectInputIdentity identity)
        {
            _device = device; _identity = identity;
            try
            {
                var objects = new List<DeviceObject>(); Exception? callbackError = null;
                EnumCallback callback = (pointer, _) =>
                {
                    try { objects.Add(Marshal.PtrToStructure<DeviceObject>(pointer)); return 1; }
                    catch (Exception error) { callbackError = error; return 0; }
                };
                Check(Method<EnumerateObjects>(device, 4)(device, callback, IntPtr.Zero, 3), "EnumObjects");
                GC.KeepAlive(callback);
                if (callbackError != null) throw new InvalidOperationException("Invalid DirectInput axis enumeration.", callbackError);
                var slider = 0;
                foreach (var item in objects.OrderBy(value => value.Type & 0x00ffff00))
                {
                    // Relative controls do not have a persistent lever position.
                    if ((item.Type & 0xff) != 2) continue;
                    var axis = AxisIndex(item.TypeId, ref slider);
                    if (axis < 0) { Trace.TraceWarning("DirectInput axis {0} ({1}) has no standard positional mapping on {2}.", item.Name, item.TypeId, identity.Name); continue; }
                    if (_axes[axis] != null) throw new InvalidOperationException("DirectInput returned duplicate standard axes.");
                    var range = new PropertyRange { Size = (uint)Marshal.SizeOf<PropertyRange>(), HeaderSize = 16, Object = item.Type, How = 2 };
                    // DIPROP_RANGE is MAKEDIPROP(4), a pseudo-GUID pointer.
                    var error = Method<GetRange>(device, 5)(device, new IntPtr(4), ref range);
                    if (error < 0 || range.Minimum >= range.Maximum)
                    { Trace.TraceWarning("DirectInput range unavailable for {0}/{1}: 0x{2:X8} [{3},{4}].", identity.Name, item.Name, error, range.Minimum, range.Maximum); continue; }
                    _axes[axis] = new AxisObject(item.Type, range.Minimum, range.Maximum);
                }
                _hasAxes = _axes.Any(axis => axis != null);
                if (!_hasAxes) return;
                SetFormat();
                // DISCL_NONEXCLUSIVE | DISCL_BACKGROUND.
                Check(Method<CooperativeLevel>(device, 13)(device, window, 0x00000002 | 0x00000008), "SetCooperativeLevel");
                _poll = Method<DeviceCall>(device, 25); _acquire = Method<DeviceCall>(device, 7); _read = Method<ReadState>(device, 9);
                // Temporary acquisition loss is handled by the next poll.
                Report(_acquire(device));
            }
            catch { Dispose(); throw; }
        }
        public bool Supports(int axis) => _axes[axis] != null;
        public uint Range(int axis) => _axes[axis] is AxisObject value ? checked((uint)((long)value.Maximum - value.Minimum)) : 0;
        public bool TryRead(double[] values)
        {
            if (values.Length != _axes.Length) throw new ArgumentException("Invalid DirectInput value buffer.");
            if (_device == IntPtr.Zero || !_hasAxes) return false;
            var error = _poll!(_device);
            if (NeedsAcquire(error))
            {
                error = _acquire!(_device);
                if (error >= 0) error = _poll(_device);
            }
            if (error < 0) { Report(error); return false; }
            error = _read!(_device, (uint)(_state.Length * sizeof(int)), _state);
            if (NeedsAcquire(error))
            {
                error = _acquire!(_device);
                if (error >= 0) error = _poll(_device);
                if (error >= 0) error = _read(_device, (uint)(_state.Length * sizeof(int)), _state);
            }
            Report(error);
            if (error < 0) return false;
            for (var axis = 0; axis < _axes.Length; axis++)
                values[axis] = _axes[axis] is AxisObject item ? NormalizeAxis(_state[axis], item.Minimum, item.Maximum) : 0;
            return true;
        }
        private void Report(int error)
        {
            if (error < 0 && error != _lastError)
                Trace.TraceWarning("DirectInput input unavailable for {0} ({1}): 0x{2:X8}.", _identity.Name, _identity.InstanceId, error);
            _lastError = error;
        }
        private static bool NeedsAcquire(int error) => error == unchecked((int)0x8007001e) || error == unchecked((int)0x8007000c);
        private void SetFormat()
        {
            var size = Marshal.SizeOf<ObjectDataFormat>();
            var objects = Marshal.AllocHGlobal(size * _axes.Count(axis => axis != null));
            try
            {
                var count = 0;
                for (var axis = 0; axis < _axes.Length; axis++)
                {
                    if (_axes[axis] is not AxisObject item) continue;
                    // Select the enumerated object ID, writing its value at our
                    // canonical DIJOYSTATE axis offset.
                    var value = new ObjectDataFormat { TypeId = IntPtr.Zero, Offset = (uint)(axis * sizeof(int)), Type = item.Type, Flags = 0 };
                    Marshal.StructureToPtr(value, IntPtr.Add(objects, count++ * size), false);
                }
                var format = new DataFormat { Size = (uint)Marshal.SizeOf<DataFormat>(), ObjectSize = (uint)size,
                    Flags = 1, DataSize = (uint)(_axes.Length * sizeof(int)), ObjectCount = (uint)count, Objects = objects };
                Check(Method<SetDataFormat>(_device, 11)(_device, ref format), "SetDataFormat");
            }
            finally { Marshal.FreeHGlobal(objects); }
        }
        public void Dispose()
        {
            if (_device == IntPtr.Zero) return;
            Method<DeviceCall>(_device, 8)(_device);
            Marshal.Release(_device); _device = IntPtr.Zero;
        }
        private sealed class AxisObject
        {
            internal AxisObject(uint type, int minimum, int maximum) { Type = type; Minimum = minimum; Maximum = maximum; }
            internal uint Type { get; }
            internal int Minimum { get; }
            internal int Maximum { get; }
        }
    }
    private static readonly Guid[] AxisTypes =
    {
        new Guid("A36D02E0-C9F3-11CF-BFC7-444553540000"), new Guid("A36D02E1-C9F3-11CF-BFC7-444553540000"),
        new Guid("A36D02E2-C9F3-11CF-BFC7-444553540000"), new Guid("A36D02F4-C9F3-11CF-BFC7-444553540000"),
        new Guid("A36D02F5-C9F3-11CF-BFC7-444553540000"), new Guid("A36D02E3-C9F3-11CF-BFC7-444553540000"),
        new Guid("A36D02E4-C9F3-11CF-BFC7-444553540000")
    };
    private static int AxisIndex(Guid type, ref int slider)
    { var index = Array.IndexOf(AxisTypes, type); return index == 6 ? slider < 2 ? 6 + slider++ : -1 : index; }
    private static T Method<T>(IntPtr instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));
    private static void Check(int error, string operation)
    { if (error < 0) throw new COMException("DirectInput " + operation + " failed (0x" + error.ToString("X8") + ").", error); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInstance
    {
        internal uint Size; internal Guid InstanceId, ProductId; internal uint Type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string InstanceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ProductName;
        internal Guid ForceFeedbackDriver; internal ushort UsagePage, Usage;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceObject
    {
        internal uint Size; internal Guid TypeId; internal uint Offset, Type, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
        internal uint ForceFeedbackMaximum, ForceFeedbackResolution;
        internal ushort Collection, Designator, UsagePage, Usage; internal uint Dimension; internal ushort Exponent, ReportId;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyRange
    { internal uint Size, HeaderSize, Object, How; internal int Minimum, Maximum; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectDataFormat
    { internal IntPtr TypeId; internal uint Offset, Type, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DataFormat
    { internal uint Size, ObjectSize, Flags, DataSize, ObjectCount; internal IntPtr Objects; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumCallback(IntPtr value, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDevice(IntPtr self, ref Guid id, out IntPtr device, IntPtr outer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumerateObjects(IntPtr self, EnumCallback callback, IntPtr context, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetRange(IntPtr self, IntPtr property, ref PropertyRange value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetDataFormat(IntPtr self, ref DataFormat value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CooperativeLevel(IntPtr self, IntPtr window, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DeviceCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadState(IntPtr self, uint length, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 8)] int[] values);
    [StructLayout(LayoutKind.Sequential)] private struct WindowMessage
    { internal IntPtr Window; internal uint Message; internal UIntPtr WParam; internal IntPtr LParam; internal uint Time; internal int X, Y; internal uint Private; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out WindowMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref WindowMessage message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref WindowMessage message);
    [DllImport("dinput8.dll", ExactSpelling = true)] private static extern int DirectInput8Create(IntPtr instance, uint version, ref Guid iid, out IntPtr directInput, IntPtr outer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
}
