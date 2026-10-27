using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.Runtime.Contracts;

public enum ControllerControlKind : byte { Pitch, Roll, Yaw, Throttle }

public sealed class ControllerBinding
{
    public ControllerBinding(ControllerControlKind control, int engineIndex, Guid deviceInstanceId, int axisIndex, bool inverted, string source)
    {
        if (control < ControllerControlKind.Pitch || control > ControllerControlKind.Throttle || engineIndex < -1 || engineIndex > 3 ||
            deviceInstanceId == Guid.Empty || axisIndex < 0 || axisIndex > 7 || (control != ControllerControlKind.Throttle && engineIndex != -1))
            throw new ArgumentException("Invalid controller binding.");
        RuntimeWire.ValidateText(source, 4096);
        Control = control; EngineIndex = engineIndex; DeviceInstanceId = deviceInstanceId; AxisIndex = axisIndex; Inverted = inverted; Source = source;
    }
    public ControllerControlKind Control { get; }
    public int EngineIndex { get; }
    public Guid DeviceInstanceId { get; }
    public int AxisIndex { get; }
    public bool Inverted { get; }
    public string Source { get; }
    internal void Write(BinaryWriter writer)
    { writer.Write((byte)Control); writer.Write(EngineIndex); writer.Write(DeviceInstanceId.ToByteArray()); writer.Write(AxisIndex); writer.Write(Inverted); RuntimeWire.WriteText(writer, Source, 4096); }
    internal static ControllerBinding Read(BinaryReader reader) => new ControllerBinding((ControllerControlKind)reader.ReadByte(), reader.ReadInt32(),
        new Guid(reader.ReadBytes(16)), reader.ReadInt32(), RuntimeWire.ReadBoolean(reader), RuntimeWire.ReadText(reader, 4096));
}

/// <summary>A slot belongs to a catalog; DirectInput additionally supplies a durable instance GUID.</summary>
public sealed class ControllerDescriptor
{
    private readonly uint[] _minimum, _maximum;
    public ControllerDescriptor(int slot, int deviceId, string name, string productKey, byte axes, uint[] minimum, uint[] maximum,
        Guid instanceId = default, Guid productId = default)
    {
        if (slot < 0 || deviceId < 0 || minimum == null || (minimum.Length != 6 && minimum.Length != 8) ||
            maximum == null || maximum.Length != minimum.Length || (minimum.Length == 6 && axes > 63))
            throw new ArgumentException("Invalid controller descriptor.");
        RuntimeWire.ValidateText(name, 512); RuntimeWire.ValidateText(productKey, 512);
        for (var axis = 0; axis < minimum.Length; axis++)
            if ((axes & (1 << axis)) != 0 && minimum[axis] >= maximum[axis]) throw new ArgumentException("Invalid axis range.");
        Slot = slot; DeviceId = deviceId; Name = name; ProductKey = productKey; Axes = axes;
        InstanceId = instanceId; ProductId = productId;
        _minimum = (uint[])minimum.Clone(); _maximum = (uint[])maximum.Clone();
    }
    public int Slot { get; }
    public int DeviceId { get; }
    public string Name { get; }
    public string ProductKey { get; }
    public Guid InstanceId { get; }
    public Guid ProductId { get; }
    public int AxisCount => _minimum.Length;
    public byte Axes { get; }
    public uint Minimum(int axis) => _minimum[axis];
    public uint Maximum(int axis) => _maximum[axis];
    public bool Supports(int axis) => axis >= 0 && axis < AxisCount && (Axes & (1 << axis)) != 0;
    internal void Write(BinaryWriter writer, bool extended)
    {
        writer.Write(Slot); writer.Write(DeviceId); RuntimeWire.WriteText(writer, Name, 512); RuntimeWire.WriteText(writer, ProductKey, 512); writer.Write(Axes);
        if (extended) { writer.Write(AxisCount); writer.Write(InstanceId.ToByteArray()); writer.Write(ProductId.ToByteArray()); }
        for (var axis = 0; axis < AxisCount; axis++) { writer.Write(_minimum[axis]); writer.Write(_maximum[axis]); }
    }
    internal static ControllerDescriptor Read(BinaryReader reader, bool extended)
    {
        var slot = reader.ReadInt32(); var device = reader.ReadInt32(); var name = RuntimeWire.ReadText(reader, 512);
        var product = RuntimeWire.ReadText(reader, 512); var axes = reader.ReadByte();
        var count = extended ? reader.ReadInt32() : 6;
        if (count != 6 && count != 8) throw new InvalidDataException("Unsupported controller axis layout.");
        var instance = extended ? new Guid(ReadExact(reader, 16)) : Guid.Empty;
        var productId = extended ? new Guid(ReadExact(reader, 16)) : Guid.Empty;
        var minimum = new uint[count]; var maximum = new uint[count];
        for (var axis = 0; axis < count; axis++) { minimum[axis] = reader.ReadUInt32(); maximum[axis] = reader.ReadUInt32(); }
        return new ControllerDescriptor(slot, device, name, product, axes, minimum, maximum, instance, productId);
    }
    private static byte[] ReadExact(BinaryReader reader, int count)
    { var value = reader.ReadBytes(count); if (value.Length != count) throw new EndOfStreamException(); return value; }
}

public sealed class ControllerCatalogMessage : RuntimePayload
{
    public ControllerCatalogMessage(long revision, IEnumerable<ControllerDescriptor> devices, IEnumerable<ControllerBinding>? bindings = null, string bindingContext = "", string profileEvidence = "")
    {
        if (revision <= 0) throw new ArgumentException("Invalid catalog revision.");
        var copy = devices.ToArray();
        if (copy.Any(value => value == null) || copy.Select(value => value.Slot).Distinct().Count() != copy.Length ||
            copy.Select(value => value.DeviceId).Distinct().Count() != copy.Length || copy.Any(value => value.Slot >= RuntimeWire.MaximumPayloadLength / (8 + value.AxisCount * 9)))
            throw new ArgumentException("Invalid controller catalog.");
        var mapped = (bindings ?? Array.Empty<ControllerBinding>()).ToArray();
        if (mapped.Any(value => value == null)) throw new ArgumentException("Invalid controller binding collection.");
        RuntimeWire.ValidateText(bindingContext, 4096);
        RuntimeWire.ValidateText(profileEvidence, RuntimeWire.MaximumPayloadLength);
        Revision = revision; Devices = new ReadOnlyCollection<ControllerDescriptor>(copy);
        Bindings = new ReadOnlyCollection<ControllerBinding>(mapped); BindingContext = bindingContext; ProfileEvidence = profileEvidence;
    }
    public long Revision { get; }
    public IReadOnlyList<ControllerDescriptor> Devices { get; }
    public IReadOnlyList<ControllerBinding> Bindings { get; }
    public string BindingContext { get; }
    public string ProfileEvidence { get; }
    public int DeviceCount => Devices.Count == 0 ? 0 : Devices.Max(value => value.Slot) + 1;
    public int AxisCount => Devices.Count == 0 ? 6 : Devices.Max(value => value.AxisCount);
    public bool HasExtendedMetadata => Devices.Count > 8 || Devices.Any(value => value.Slot >= 8 || value.DeviceId >= 16 || value.AxisCount != 6 || value.InstanceId != Guid.Empty || value.ProductId != Guid.Empty) || Bindings.Count != 0 || BindingContext.Length != 0 || ProfileEvidence.Length != 0;
    public bool IsAmbiguous(ControllerDescriptor device) => device.InstanceId == Guid.Empty
        ? Devices.Count(value => value.ProductKey == device.ProductKey) > 1 : Devices.Count(value => value.InstanceId == device.InstanceId) > 1;
    public override RuntimeMessageKind Kind => RuntimeMessageKind.ControllerCatalog;
    internal override void Write(BinaryWriter writer) => WriteMetadata(writer);
    // Shared immutable catalog representation for wire and the versioned archive envelope.
    public void WriteMetadata(BinaryWriter writer)
    {
        var extended = HasExtendedMetadata;
        writer.Write(Revision); writer.Write(extended ? -Devices.Count - 1 : Devices.Count);
        foreach (var device in Devices) device.Write(writer, extended);
        if (extended)
        {
            RuntimeWire.WriteText(writer, BindingContext, 4096); writer.Write(Bindings.Count); foreach (var binding in Bindings) binding.Write(writer);
            ProfileEvidenceCodec.WriteOptional(writer, ProfileEvidence);
        }
    }
    public static ControllerCatalogMessage ReadMetadata(BinaryReader reader)
    {
        var revision = reader.ReadInt64(); var encoded = reader.ReadInt32(); var extended = encoded < 0;
        var count = extended ? -(long)encoded - 1 : encoded;
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 65)
            throw new InvalidDataException("Controller count exceeds its payload.");
        var devices = new ControllerDescriptor[(int)count];
        for (var i = 0; i < devices.Length; i++) devices[i] = ControllerDescriptor.Read(reader, extended);
        var context = extended ? RuntimeWire.ReadText(reader, 4096) : "";
        var bindingCount = extended ? reader.ReadInt32() : 0;
        if (bindingCount < 0 || bindingCount > (reader.BaseStream.Length - reader.BaseStream.Position) / 30)
            throw new InvalidDataException("Controller bindings exceed their payload.");
        var bindings = new ControllerBinding[bindingCount];
        for (var i = 0; i < bindings.Length; i++) bindings[i] = ControllerBinding.Read(reader);
        return new ControllerCatalogMessage(revision, devices, bindings, context, extended ? ProfileEvidenceCodec.ReadOptional(reader) : "");
    }
    internal static ControllerCatalogMessage Read(BinaryReader reader) => ReadMetadata(reader);
}

public sealed class ControllerSnapshotMessage : RuntimePayload
{
    private readonly long[] _capturedAt;
    private readonly double[] _values;
    private readonly bool[] _valid;
    public ControllerSnapshotMessage(long catalogRevision, long[] capturedAt, double[] values, bool[] valid, int axisCount = 6)
    {
        if (catalogRevision <= 0 || (axisCount != 6 && axisCount != 8) || values.Length != checked(capturedAt.Length * axisCount) || valid.Length != values.Length ||
            capturedAt.Any(value => value < 0)) throw new ArgumentException("Invalid controller snapshot.");
        for (var axis = 0; axis < values.Length; axis++)
            if (valid[axis] && (capturedAt[axis / axisCount] == 0 || double.IsNaN(values[axis]) || double.IsInfinity(values[axis]) || Math.Abs(values[axis]) > 100))
                throw new ArgumentException("Invalid measured controller axis.");
        CatalogRevision = catalogRevision; AxisCount = axisCount; _capturedAt = (long[])capturedAt.Clone(); _values = (double[])values.Clone(); _valid = (bool[])valid.Clone();
    }
    public long CatalogRevision { get; }
    public int DeviceCount => _capturedAt.Length;
    public int AxisCount { get; }
    public long CapturedAt(int slot) => _capturedAt[slot];
    public double Value(int slot, int axis) => _values[slot * AxisCount + axis];
    public bool Valid(int slot, int axis) => _valid[slot * AxisCount + axis];
    public override RuntimeMessageKind Kind => RuntimeMessageKind.ControllerSnapshot;
    internal override void Write(BinaryWriter writer)
    {
        var extended = DeviceCount != 8 || AxisCount != 6;
        writer.Write(extended ? -CatalogRevision : CatalogRevision);
        if (extended) { writer.Write(DeviceCount); writer.Write(AxisCount); }
        foreach (var value in _capturedAt) writer.Write(value);
        for (var axis = 0; axis < _values.Length; axis++) { writer.Write(_values[axis]); writer.Write(_valid[axis]); }
    }
    internal static ControllerSnapshotMessage Read(BinaryReader reader)
    {
        var revision = reader.ReadInt64(); var extended = revision < 0;
        if (revision == long.MinValue) throw new InvalidDataException("Invalid controller revision.");
        var count = extended ? reader.ReadInt32() : 8; var axes = extended ? reader.ReadInt32() : 6;
        if (count < 0 || (axes != 6 && axes != 8) || count > (reader.BaseStream.Length - reader.BaseStream.Position) / (8 + axes * 9))
            throw new InvalidDataException("Controller measurements exceed their payload.");
        var captured = new long[count]; var values = new double[checked(count * axes)]; var valid = new bool[values.Length];
        for (var slot = 0; slot < count; slot++) captured[slot] = reader.ReadInt64();
        for (var axis = 0; axis < values.Length; axis++) { values[axis] = reader.ReadDouble(); valid[axis] = RuntimeWire.ReadBoolean(reader); }
        return new ControllerSnapshotMessage(extended ? -revision : revision, captured, values, valid, axes);
    }
}
