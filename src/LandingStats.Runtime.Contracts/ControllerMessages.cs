using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.Runtime.Contracts;

/// <summary>A descriptor is scoped to a worker/catalog/slot, never a hardware serial number.</summary>
public sealed class ControllerDescriptor
{
    private readonly uint[] _minimum, _maximum;
    public ControllerDescriptor(int slot, int deviceId, string name, string productKey, byte axes, uint[] minimum, uint[] maximum)
    {
        if (slot < 0 || slot >= TelemetrySample.CapturedControllerCount || deviceId < 0 || deviceId >= 16 || axes > 63 ||
            minimum == null || minimum.Length != 6 || maximum == null || maximum.Length != 6)
            throw new ArgumentException("Invalid controller descriptor.");
        RuntimeWire.ValidateText(name, 512); RuntimeWire.ValidateText(productKey, 512);
        for (var axis = 0; axis < 6; axis++)
            if ((axes & (1 << axis)) != 0 && minimum[axis] >= maximum[axis]) throw new ArgumentException("Invalid axis range.");
        Slot = slot; DeviceId = deviceId; Name = name; ProductKey = productKey; Axes = axes;
        _minimum = (uint[])minimum.Clone(); _maximum = (uint[])maximum.Clone();
    }
    public int Slot { get; }
    public int DeviceId { get; }
    public string Name { get; }
    // WinMM exposes model/driver metadata, not a durable per-unit identity.
    public string ProductKey { get; }
    public byte Axes { get; }
    public uint Minimum(int axis) => _minimum[axis];
    public uint Maximum(int axis) => _maximum[axis];
    public bool Supports(int axis) => (Axes & (1 << axis)) != 0;
    internal void Write(BinaryWriter writer)
    {
        writer.Write(Slot); writer.Write(DeviceId); RuntimeWire.WriteText(writer, Name, 512); RuntimeWire.WriteText(writer, ProductKey, 512); writer.Write(Axes);
        for (var axis = 0; axis < 6; axis++) { writer.Write(_minimum[axis]); writer.Write(_maximum[axis]); }
    }
    internal static ControllerDescriptor Read(BinaryReader reader)
    {
        var slot = reader.ReadInt32(); var device = reader.ReadInt32(); var name = RuntimeWire.ReadText(reader, 512);
        var product = RuntimeWire.ReadText(reader, 512); var axes = reader.ReadByte();
        var minimum = new uint[6]; var maximum = new uint[6];
        for (var axis = 0; axis < 6; axis++) { minimum[axis] = reader.ReadUInt32(); maximum[axis] = reader.ReadUInt32(); }
        return new ControllerDescriptor(slot, device, name, product, axes, minimum, maximum);
    }
}

public sealed class ControllerCatalogMessage : RuntimePayload
{
    public ControllerCatalogMessage(long revision, IEnumerable<ControllerDescriptor> devices)
    {
        if (revision <= 0) throw new ArgumentException("Invalid catalog revision.");
        var copy = devices.Take(TelemetrySample.CapturedControllerCount + 1).ToArray();
        if (copy.Length > TelemetrySample.CapturedControllerCount || copy.Any(value => value == null) ||
            copy.Select(value => value.Slot).Distinct().Count() != copy.Length || copy.Select(value => value.DeviceId).Distinct().Count() != copy.Length)
            throw new ArgumentException("Invalid controller catalog.");
        Revision = revision; Devices = new ReadOnlyCollection<ControllerDescriptor>(copy);
    }
    public long Revision { get; }
    public IReadOnlyList<ControllerDescriptor> Devices { get; }
    public bool IsAmbiguous(ControllerDescriptor device) => Devices.Count(value => value.ProductKey == device.ProductKey) > 1;
    public override RuntimeMessageKind Kind => RuntimeMessageKind.ControllerCatalog;
    internal override void Write(BinaryWriter writer)
    { writer.Write(Revision); writer.Write(Devices.Count); foreach (var device in Devices) device.Write(writer); }
    internal static ControllerCatalogMessage Read(BinaryReader reader)
    {
        var revision = reader.ReadInt64(); var count = reader.ReadInt32();
        if (count < 0 || count > TelemetrySample.CapturedControllerCount) throw new InvalidDataException("Controller count exceeds the protocol bound.");
        var devices = new ControllerDescriptor[count];
        for (var i = 0; i < count; i++) devices[i] = ControllerDescriptor.Read(reader);
        return new ControllerCatalogMessage(revision, devices);
    }
}

public sealed class ControllerSnapshotMessage : RuntimePayload
{
    private readonly long[] _capturedAt;
    private readonly double[] _values;
    private readonly bool[] _valid;
    public ControllerSnapshotMessage(long catalogRevision, long[] capturedAt, double[] values, bool[] valid)
    {
        if (catalogRevision <= 0 || capturedAt.Length != TelemetrySample.CapturedControllerCount || values.Length != 48 || valid.Length != 48 ||
            capturedAt.Any(value => value < 0)) throw new ArgumentException("Invalid controller snapshot.");
        for (var axis = 0; axis < values.Length; axis++)
            if (valid[axis] && (capturedAt[axis / 6] == 0 || double.IsNaN(values[axis]) || double.IsInfinity(values[axis]) || Math.Abs(values[axis]) > 100))
                throw new ArgumentException("Invalid measured controller axis.");
        CatalogRevision = catalogRevision; _capturedAt = (long[])capturedAt.Clone(); _values = (double[])values.Clone(); _valid = (bool[])valid.Clone();
    }
    public long CatalogRevision { get; }
    public long CapturedAt(int slot) => _capturedAt[slot];
    public double Value(int slot, int axis) => _values[slot * 6 + axis];
    public bool Valid(int slot, int axis) => _valid[slot * 6 + axis];
    public override RuntimeMessageKind Kind => RuntimeMessageKind.ControllerSnapshot;
    internal override void Write(BinaryWriter writer)
    {
        writer.Write(CatalogRevision); foreach (var value in _capturedAt) writer.Write(value);
        for (var axis = 0; axis < _values.Length; axis++) { writer.Write(_values[axis]); writer.Write(_valid[axis]); }
    }
    internal static ControllerSnapshotMessage Read(BinaryReader reader)
    {
        var revision = reader.ReadInt64(); var captured = new long[8]; var values = new double[48]; var valid = new bool[48];
        for (var slot = 0; slot < captured.Length; slot++) captured[slot] = reader.ReadInt64();
        for (var axis = 0; axis < values.Length; axis++) { values[axis] = reader.ReadDouble(); valid[axis] = RuntimeWire.ReadBoolean(reader); }
        return new ControllerSnapshotMessage(revision, captured, values, valid);
    }
}
