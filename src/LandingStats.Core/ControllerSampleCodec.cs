using System;
using System.IO;

namespace LandingStats.Core;

/// <summary>CSV v8 controller field: version, dimensions and complete physical observations.</summary>
public static class ControllerSampleCodec
{
    public static byte[] Encode(TelemetrySample sample)
    {
        var size = 10L + sample.ControllerCount * (21L + 17L * sample.ControllerAxisCount);
        if (size > TelemetryBinaryCodec.MaximumEncodedSize) throw new InvalidDataException("Controller field exceeds its byte budget.");
        using var stream = new MemoryStream((int)size); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)2); writer.Write(sample.ControllerCount); writer.Write(sample.ControllerAxisCount);
        for (var slot = 0; slot < sample.ControllerCount; slot++)
        {
            writer.Write(sample.RawControllerDeviceId[slot]); writer.Write(sample.RawControllerYAxisPercent[slot]);
            writer.Write(sample.RawControllerYAxisValid[slot]); writer.Write(sample.RawControllerYAxisAgeSeconds[slot]);
            for (var axis = 0; axis < sample.ControllerAxisCount; axis++)
            {
                var offset = slot * sample.ControllerAxisCount + axis;
                writer.Write(sample.RawControllerAxesPercent[offset]); writer.Write(sample.RawControllerAxesValid[offset]); writer.Write(sample.RawControllerAxesAgeSeconds[offset]);
            }
        }
        return stream.ToArray();
    }
    public static void Apply(TelemetrySample sample, byte[] bytes)
    {
        if (bytes.Length < 10 || bytes.Length > TelemetryBinaryCodec.MaximumEncodedSize) throw new InvalidDataException("Invalid controller field size.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 2) throw new InvalidDataException("Unsupported controller field version.");
        var count = reader.ReadInt32(); var axes = reader.ReadInt32();
        if (count < 0 || (axes != 6 && axes != 8) || 10L + count * (21L + 17L * axes) != bytes.Length)
            throw new InvalidDataException("Controller field dimensions do not match its payload.");
        sample.ResizeControllers(count, axes);
        for (var slot = 0; slot < count; slot++)
        {
            sample.RawControllerDeviceId[slot] = reader.ReadInt32(); sample.RawControllerYAxisPercent[slot] = reader.ReadDouble();
            sample.RawControllerYAxisValid[slot] = Boolean(reader); sample.RawControllerYAxisAgeSeconds[slot] = reader.ReadDouble();
            for (var axis = 0; axis < axes; axis++)
            {
                var offset = slot * axes + axis;
                sample.RawControllerAxesPercent[offset] = reader.ReadDouble(); sample.RawControllerAxesValid[offset] = Boolean(reader);
                sample.RawControllerAxesAgeSeconds[offset] = reader.ReadDouble();
            }
        }
    }
    private static bool Boolean(BinaryReader reader)
    { var value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid controller field boolean."); return value != 0; }
}
