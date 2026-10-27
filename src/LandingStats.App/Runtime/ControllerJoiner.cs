using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal enum ControllerJoinAvailability : byte { NoSource, NoSnapshot, Stale, Available }

/// <summary>Consumes one ordered controller stream. It never waits for a future poll or a missing device.</summary>
internal sealed class ControllerJoiner
{
    internal const int MaximumSnapshots = 512;
    internal static readonly TimeSpan DefaultFreshness = TimeSpan.FromMilliseconds(250);
    private readonly RuntimeClockEpoch _clock;
    private readonly TimeSpan _freshness;
    private readonly List<RuntimeEnvelope> _snapshots = new List<RuntimeEnvelope>();
    private readonly List<RuntimeEnvelope> _catalogs = new List<RuntimeEnvelope>();
    private WorkerIdentity? _active;
    private RuntimeEnvelope? _aircraftControls;
    private long _lastSequence, _lastCapture;
    public ControllerJoiner(RuntimeClockEpoch clock, TimeSpan? freshness = null)
    {
        _clock = clock; _freshness = freshness ?? DefaultFreshness;
        if (_freshness <= TimeSpan.Zero || _freshness > TimeSpan.FromSeconds(5)) throw new ArgumentException("Invalid controller freshness bound.");
    }
    public int SnapshotCount => _snapshots.Count;
    public int CatalogCount => _catalogs.Count;
    public WorkerIdentity? ActiveWorker => _active;
    public byte[] CaptureCheckpoint()
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(0x31434a4c); writer.Write(_clock.Id.ToByteArray()); writer.Write(_clock.OriginQpc); writer.Write(_clock.Frequency); writer.Write(_freshness.Ticks);
        writer.Write(_active != null);
        if (_active != null) { writer.Write(_active.AppSessionId.ToByteArray()); writer.Write(_active.InstanceId.ToByteArray()); writer.Write(_active.Generation); }
        writer.Write(_lastSequence); writer.Write(_lastCapture);
        foreach (var list in new[] { _catalogs, _snapshots })
        {
            writer.Write(list.Count);
            foreach (var item in list) { var bytes = RuntimeWire.Encode(item); writer.Write(bytes.Length); writer.Write(bytes); }
        }
        writer.Write(_aircraftControls != null);
        if (_aircraftControls != null) { var bytes = RuntimeWire.Encode(_aircraftControls); writer.Write(bytes.Length); writer.Write(bytes); }
        return memory.ToArray();
    }
    public static ControllerJoiner RestoreCheckpoint(byte[] bytes, RuntimeClockEpoch clock)
    {
        try { return RestoreCheckpointCore(bytes, clock); }
        catch (Exception error) when (error is EndOfStreamException || error is ArgumentException)
        { throw new InvalidDataException("Malformed controller checkpoint.", error); }
    }
    private static ControllerJoiner RestoreCheckpointCore(byte[] bytes, RuntimeClockEpoch clock)
    {
        if (bytes == null || bytes.Length > 17 * 1024 * 1024) throw new InvalidDataException("Controller checkpoint exceeds its bound.");
        using var memory = new MemoryStream(bytes, false); using var reader = new BinaryReader(memory);
        if (reader.ReadInt32() != 0x31434a4c || new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)) != clock.Id ||
            reader.ReadInt64() != clock.OriginQpc || reader.ReadInt64() != clock.Frequency) throw new InvalidDataException("Controller checkpoint belongs to another clock epoch.");
        var value = new ControllerJoiner(clock, TimeSpan.FromTicks(reader.ReadInt64()));
        if (RuntimeCheckpointCodec.ReadBoolean(reader)) value._active = new WorkerIdentity(new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)),
            new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)), reader.ReadInt64(), ComponentRole.Controllers);
        value._lastSequence = reader.ReadInt64(); value._lastCapture = reader.ReadInt64();
        foreach (var list in new[] { value._catalogs, value._snapshots })
        {
            var count = RuntimeCheckpointCodec.ReadCount(reader, MaximumSnapshots); long sequence = 0, capture = clock.OriginQpc, revision = 0;
            for (var i = 0; i < count; i++)
            {
                var message = RuntimeWire.Decode(RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, RuntimeWire.MaximumPayloadLength + RuntimeWire.HeaderLength)));
                if (value._active == null || !value._active.Equals(message.Worker) || message.Sequence <= sequence || message.CaptureQpc < capture ||
                    message.Sequence > value._lastSequence || message.CaptureQpc > value._lastCapture) throw new InvalidDataException("Invalid controller checkpoint ordering.");
                if (ReferenceEquals(list, value._catalogs))
                {
                    if (!(message.Payload is ControllerCatalogMessage catalog) || catalog.Revision <= revision) throw new InvalidDataException("Invalid checkpoint catalog revision.");
                    revision = catalog.Revision;
                }
                else
                {
                    if (!(message.Payload is ControllerSnapshotMessage snapshot)) throw new InvalidDataException("Invalid checkpoint measurement.");
                    var catalog = value._catalogs.SingleOrDefault(item => ((ControllerCatalogMessage)item.Payload).Revision == snapshot.CatalogRevision);
                    if (catalog == null) throw new InvalidDataException("Checkpoint measurement lost its catalog.");
                    ControllerJoinDecision.ValidateSnapshot(clock, catalog, message);
                }
                list.Add(message); sequence = message.Sequence; capture = message.CaptureQpc;
            }
        }
        if (memory.Position < memory.Length && RuntimeCheckpointCodec.ReadBoolean(reader))
        {
            var controls = RuntimeWire.Decode(RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, RuntimeWire.MaximumPayloadLength + RuntimeWire.HeaderLength)));
            if (controls.Worker.Role != ComponentRole.Simulator || !(controls.Payload is AircraftControlsMessage)) throw new InvalidDataException("Invalid aircraft binding checkpoint.");
            value._aircraftControls = controls;
        }
        if (memory.Position != memory.Length || value._lastSequence < 0 || value._lastCapture < 0) throw new InvalidDataException("Invalid controller checkpoint suffix.");
        return value;
    }
    public void Activate(WorkerIdentity worker)
    {
        if (worker.Role != ComponentRole.Controllers || _active != null) throw new InvalidOperationException("Retire the previous controller owner before activation.");
        _active = worker; _lastSequence = 0; _lastCapture = _clock.OriginQpc;
    }
    public void Retire(WorkerIdentity worker)
    {
        if (_active == null || !_active.Equals(worker)) return;
        _active = null; _snapshots.Clear(); _catalogs.Clear();
    }
    public bool Accept(RuntimeEnvelope envelope)
    {
        if (_active == null || !_active.Equals(envelope.Worker) || envelope.Sequence <= _lastSequence) return false;
        if (!RuntimeSequence.IsNext(_lastSequence, envelope) || envelope.CaptureQpc < _lastCapture) throw new InvalidDataException("Controller source has an unordered boundary.");
        switch (envelope.Payload)
        {
            case WorkerHello _: break;
            case ControllerCatalogMessage catalog:
                var revision = _catalogs.Count == 0 ? 0 : ((ControllerCatalogMessage)_catalogs[_catalogs.Count - 1].Payload).Revision;
                if (revision != 0 && catalog.Revision != revision + 1) throw new InvalidDataException("Controller catalog revision is not contiguous.");
                _catalogs.Add(envelope);
                break;
            case ControllerSnapshotMessage snapshot:
                if (_catalogs.Count == 0 || snapshot.CatalogRevision != ((ControllerCatalogMessage)_catalogs[_catalogs.Count - 1].Payload).Revision)
                    throw new InvalidDataException("Controller snapshot has no preceding catalog.");
                ControllerJoinDecision.ValidateSnapshot(_clock, _catalogs[_catalogs.Count - 1], envelope);
                _snapshots.Add(envelope);
                if (_snapshots.Count > MaximumSnapshots) _snapshots.RemoveAt(0);
                break;
            case SourceGapMessage _: _snapshots.Clear(); _catalogs.Clear(); break;
            default: throw new InvalidDataException("Unexpected controller source payload.");
        }
        _lastSequence = envelope.Sequence; _lastCapture = envelope.CaptureQpc;
        // Keep the catalog of the oldest retained sample and all subsequent
        // revisions. Even catalog-only malfunction cannot grow memory forever.
        var oldest = _snapshots.Count == 0 ? long.MaxValue : ((ControllerSnapshotMessage)_snapshots[0].Payload).CatalogRevision;
        while (_catalogs.Count > 1 && ((ControllerCatalogMessage)_catalogs[1].Payload).Revision <= oldest) _catalogs.RemoveAt(0);
        while (_catalogs.Count > MaximumSnapshots)
        {
            var removed = ((ControllerCatalogMessage)_catalogs[0].Payload).Revision; _catalogs.RemoveAt(0);
            _snapshots.RemoveAll(value => ((ControllerSnapshotMessage)value.Payload).CatalogRevision <= removed);
        }
        return true;
    }
    public void ObserveAircraftControls(RuntimeEnvelope message)
    {
        if (message.Worker.Role != ComponentRole.Simulator || !(message.Payload is AircraftControlsMessage))
            throw new ArgumentException("Expected simulator binding observation.");
        _aircraftControls = message;
    }
    public void ClearAircraftControls() => _aircraftControls = null;

    public ControllerJoinDecision Join(long frameCaptureQpc)
    {
        if (frameCaptureQpc < _clock.OriginQpc) throw new ArgumentException("Frame belongs to an earlier clock epoch.");
        RuntimeEnvelope? catalog = null, snapshot = null;
        for (var i = _catalogs.Count - 1; i >= 0; i--)
            if (_catalogs[i].CaptureQpc <= frameCaptureQpc) { catalog = _catalogs[i]; break; }
        if (catalog != null)
            for (var i = _snapshots.Count - 1; i >= 0; i--)
            {
                var candidate = _snapshots[i];
                if (candidate.CaptureQpc <= frameCaptureQpc && ((ControllerSnapshotMessage)candidate.Payload).CatalogRevision == ((ControllerCatalogMessage)catalog.Payload).Revision)
                { snapshot = candidate; break; }
            }
        if (catalog?.Payload is ControllerCatalogMessage devices && _aircraftControls?.Payload is AircraftControlsMessage profiles &&
            _aircraftControls.CaptureQpc <= frameCaptureQpc)
        {
            var bindings = profiles.Bindings;
            catalog = new RuntimeEnvelope(catalog.Worker, catalog.ConnectionGeneration, catalog.Sequence, catalog.CaptureQpc,
                new ControllerCatalogMessage(devices.Revision, devices.Devices, bindings, profiles.Context));
        }
        return new ControllerJoinDecision(_clock, frameCaptureQpc, _freshness, _active != null, catalog, snapshot);
    }
}

/// <summary>Immutable decision recorded with the joined frame; replay never queries the live joiner.</summary>
internal sealed class ControllerJoinDecision
{
    private const int MaximumMessage = RuntimeWire.MaximumPayloadLength + RuntimeWire.HeaderLength;
    private readonly RuntimeClockEpoch _clock;
    private readonly TimeSpan _freshness;
    private readonly bool _sourceActive;
    public ControllerJoinDecision(RuntimeClockEpoch clock, long frameQpc, TimeSpan freshness, bool sourceActive, RuntimeEnvelope? catalog, RuntimeEnvelope? snapshot)
    {
        if (frameQpc < clock.OriginQpc || freshness <= TimeSpan.Zero || freshness > TimeSpan.FromSeconds(5) ||
            (!sourceActive && (catalog != null || snapshot != null)) || (snapshot != null && catalog == null) ||
            (catalog != null && (!(catalog.Payload is ControllerCatalogMessage) || catalog.Worker.Role != ComponentRole.Controllers || catalog.CaptureQpc > frameQpc)))
            throw new InvalidDataException("Invalid controller join decision.");
        if (snapshot != null)
        {
            ValidateSnapshot(clock, catalog!, snapshot);
            if (snapshot.CaptureQpc > frameQpc) throw new InvalidDataException("Controller decision selected a future snapshot.");
        }
        _clock = clock; FrameCaptureQpc = frameQpc; _freshness = freshness; _sourceActive = sourceActive; Catalog = catalog; Snapshot = snapshot;
    }
    public long FrameCaptureQpc { get; }
    public RuntimeEnvelope? Catalog { get; }
    public RuntimeEnvelope? Snapshot { get; }
    public ControllerJoinAvailability Availability
    {
        get
        {
            if (!_sourceActive) return ControllerJoinAvailability.NoSource;
            if (Snapshot == null) return ControllerJoinAvailability.NoSnapshot;
            return (FrameCaptureQpc - Snapshot.CaptureQpc) / (double)_clock.Frequency <= _freshness.TotalSeconds ?
                ControllerJoinAvailability.Available : ControllerJoinAvailability.Stale;
        }
    }
    internal static void ValidateSnapshot(RuntimeClockEpoch clock, RuntimeEnvelope catalogEnvelope, RuntimeEnvelope snapshotEnvelope)
    {
        if (!(catalogEnvelope.Payload is ControllerCatalogMessage catalog) || !(snapshotEnvelope.Payload is ControllerSnapshotMessage snapshot) ||
            !catalogEnvelope.Worker.Equals(snapshotEnvelope.Worker) || snapshot.CatalogRevision != catalog.Revision ||
            snapshotEnvelope.Sequence <= catalogEnvelope.Sequence || catalogEnvelope.CaptureQpc < clock.OriginQpc || snapshotEnvelope.CaptureQpc < catalogEnvelope.CaptureQpc)
            throw new InvalidDataException("Controller snapshot context does not match its catalog.");
        if (catalog.Devices.Count > 0 && snapshot.AxisCount != catalog.AxisCount || snapshot.DeviceCount < catalog.DeviceCount)
            throw new InvalidDataException("Controller snapshot dimensions do not match its catalog.");
        for (var slot = 0; slot < snapshot.DeviceCount; slot++)
        {
            var descriptor = catalog.Devices.FirstOrDefault(value => value.Slot == slot);
            if (snapshot.CapturedAt(slot) != 0 && (snapshot.CapturedAt(slot) < catalogEnvelope.CaptureQpc || snapshot.CapturedAt(slot) > snapshotEnvelope.CaptureQpc))
                throw new InvalidDataException("Controller measurement time is outside its catalog and publication boundary.");
            for (var axis = 0; axis < snapshot.AxisCount; axis++)
                if (snapshot.Valid(slot, axis) && (descriptor == null || !descriptor.Supports(axis))) throw new InvalidDataException("Measured axis is missing from its catalog.");
        }
    }
    public void Apply(TelemetrySample sample)
    {
        sample.ControllerCatalog = Catalog == null ? Array.Empty<byte>() : ControllerProvenanceCodec.Encode(Catalog);
        var catalog = Catalog?.Payload as ControllerCatalogMessage;
        sample.ResizeControllers(catalog?.DeviceCount ?? 0, catalog?.AxisCount ?? 8);
        Array.Clear(sample.RawControllerAxesPercent, 0, sample.RawControllerAxesPercent.Length);
        Array.Clear(sample.RawControllerAxesValid, 0, sample.RawControllerAxesValid.Length);
        Array.Clear(sample.RawControllerAxesAgeSeconds, 0, sample.RawControllerAxesAgeSeconds.Length);
        for (var slot = 0; slot < sample.ControllerCount; slot++) sample.RawControllerDeviceId[slot] = -1;
        if (catalog != null && Snapshot?.Payload is ControllerSnapshotMessage snapshot)
            foreach (var device in catalog.Devices)
            {
                var captured = snapshot.CapturedAt(device.Slot);
                var age = captured == 0 ? 0 : (FrameCaptureQpc - captured) / (double)_clock.Frequency;
                if (captured != 0) sample.RawControllerDeviceId[device.Slot] = device.DeviceId;
                for (var axis = 0; axis < sample.ControllerAxisCount; axis++)
                {
                    var index = device.Slot * sample.ControllerAxisCount + axis;
                    sample.RawControllerAxesPercent[index] = snapshot.Value(device.Slot, axis);
                    sample.RawControllerAxesAgeSeconds[index] = age;
                    sample.RawControllerAxesValid[index] = captured != 0 && snapshot.Valid(device.Slot, axis) && age >= 0 && age <= _freshness.TotalSeconds;
                }
            }
        for (var slot = 0; slot < sample.ControllerCount; slot++)
        {
            sample.RawControllerYAxisPercent[slot] = sample.RawControllerAxesPercent[slot * sample.ControllerAxisCount + 1];
            sample.RawControllerYAxisValid[slot] = sample.RawControllerAxesValid[slot * sample.ControllerAxisCount + 1];
            sample.RawControllerYAxisAgeSeconds[slot] = sample.RawControllerAxesAgeSeconds[slot * sample.ControllerAxisCount + 1];
        }
    }

    public byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(_clock.Id.ToByteArray()); writer.Write(_clock.OriginQpc); writer.Write(_clock.Frequency);
        writer.Write(FrameCaptureQpc); writer.Write(_freshness.Ticks); writer.Write(_sourceActive);
        foreach (var message in new[] { Catalog, Snapshot })
        { var bytes = message == null ? Array.Empty<byte>() : RuntimeWire.Encode(message); writer.Write(bytes.Length); writer.Write(bytes); }
        return stream.ToArray();
    }
    public static ControllerJoinDecision Decode(byte[] bytes)
    {
        try { return DecodeCore(bytes); }
        catch (Exception error) when (error is EndOfStreamException || error is ArgumentException)
        { throw new InvalidDataException("Malformed controller join decision.", error); }
    }
    private static ControllerJoinDecision DecodeCore(byte[] bytes)
    {
        if (bytes.Length > MaximumMessage * 2 + 64) throw new InvalidDataException("Controller decision exceeds its bound.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported controller decision version.");
        var clock = new RuntimeClockEpoch(new Guid(reader.ReadBytes(16)), reader.ReadInt64(), reader.ReadInt64());
        var frame = reader.ReadInt64(); var freshness = TimeSpan.FromTicks(reader.ReadInt64()); var active = reader.ReadByte();
        if (active > 1) throw new InvalidDataException("Invalid controller source presence.");
        var messages = new RuntimeEnvelope?[2];
        for (var i = 0; i < messages.Length; i++)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > MaximumMessage || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid controller decision payload length.");
            if (length > 0) messages[i] = RuntimeWire.Decode(reader.ReadBytes(length));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected controller decision suffix.");
        return new ControllerJoinDecision(clock, frame, freshness, active != 0, messages[0], messages[1]);
    }
}
