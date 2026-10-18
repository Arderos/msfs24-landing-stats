using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LandingStats.App.Telemetry;

namespace LandingStats.App.Runtime;

internal sealed class RawEffectBatch
{
    public RawEffectBatch(Guid id, JournalCursor cursor, IReadOnlyList<object> effects) { Id = id; Cursor = cursor; Effects = effects; }
    public Guid Id { get; }
    public JournalCursor Cursor { get; }
    public IReadOnlyList<object> Effects { get; }
}

/// <summary>The recording checkpoint may advance only after these exact RAW
/// effects are durable. Export is a separate owner and can restart from them.</summary>
internal sealed class RawEffectStore
{
    private const int Magic = 0x31455752; // RWE1
    private const long MaximumBytes = 256L * 1024 * 1024;
    private readonly object _gate = new object();
    private readonly long _quota;
    private SortedDictionary<JournalCursor, Guid>? _index;
    private JournalCursor? _collected;
    private long _bytes;
    public RawEffectStore(string root, long quota = 512L * 1024 * 1024) { Root = Path.GetFullPath(root); _quota = quota; }
    public string Root { get; }
    internal string PathFor(Guid id) => Path.Combine(Root, id.ToString("N") + ".bin");
    public void Commit(DurableRuntimeInput input, IReadOnlyList<object> effects)
    {
        if (effects.Count == 0) return;
        var bytes = Encode(input, effects);
        lock (_gate)
        {
            LoadIndex();
            if (_collected != null && input.Cursor.CompareTo(_collected) < 0) return;
            if (_locations.ContainsKey(input.Input.Id))
            {
                if (!ReadBytes(input.Input.Id).SequenceEqual(bytes)) throw new InvalidDataException("RAW input identity was reused with different effects.");
                return;
            }
            Reserve(input.Cursor, input.Input.Id, bytes.Length);
            var path = PathFor(input.Input.Id);
            DurableFile.Write(path, stream => stream.Write(bytes, 0, bytes.Length));
            Add(input.Cursor, input.Input.Id, path, 0, bytes.Length); _bytes += bytes.Length;
        }
    }
    internal static byte[] Encode(DurableRuntimeInput input, IReadOnlyList<object> effects)
    {
        if (effects.Count > 8 || input.Input.Id == Guid.Empty) throw new InvalidDataException("Invalid RAW effect batch.");
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
        {
            writer.Write(Magic); writer.Write(input.Input.Id.ToByteArray()); writer.Write(input.Cursor.Segment); writer.Write(input.Cursor.Sequence);
            writer.Write(effects.Count);
            foreach (var effect in effects)
            {
                if (effect is RawDebugCaptureStartedEventArgs start)
                {
                    writer.Write((byte)1); writer.Write(start.StartedUtc.ToUniversalTime().Ticks);
                    RuntimeCheckpointCodec.WriteText(writer, start.Simulator); RuntimeCheckpointCodec.WriteText(writer, start.AircraftTitle);
                    RuntimeCheckpointCodec.WriteText(writer, start.AircraftType); RuntimeCheckpointCodec.WriteText(writer, start.AircraftModel);
                    if (start.ControlInputSources.Count > 64) throw new InvalidDataException("RAW source count exceeds its bound.");
                    writer.Write(start.ControlInputSources.Count); foreach (var source in start.ControlInputSources) RuntimeCheckpointCodec.WriteText(writer, source);
                    RuntimeCheckpointCodec.WriteSamples(writer, start.InitialSamples);
                }
                else if (effect is RawDebugSampleEventArgs sample) { writer.Write((byte)2); RuntimeCheckpointCodec.WriteSample(writer, sample.Sample); }
                else if (ReferenceEquals(effect, EventArgs.Empty)) writer.Write((byte)3);
                else if (effect is RawCaptureGap gap)
                { writer.Write((byte)4); writer.Write(gap.DroppedInputs); RuntimeCheckpointCodec.WriteText(writer, gap.Reason); }
                else throw new InvalidDataException("Unsupported RAW effect.");
            }
        }
        if (memory.Length > MaximumBytes) throw new InvalidDataException("RAW effect batch exceeds its bound.");
        return memory.ToArray();
    }
    private readonly Dictionary<Guid, Location> _locations = new Dictionary<Guid, Location>();
    private sealed class Location
    {
        public Location(string path, long offset, int length, JournalCursor cursor) { Path = path; Offset = offset; Length = length; Cursor = cursor; }
        public string Path { get; } public long Offset { get; } public int Length { get; } public JournalCursor Cursor { get; }
    }
    private void Add(JournalCursor cursor, Guid id, string path, long offset, int length)
    { _index!.Add(cursor, id); _locations.Add(id, new Location(path, offset, length, cursor)); }
    private byte[] ReadBytes(Guid id)
    {
        var location = _locations[id]; using var file = File.OpenRead(location.Path); file.Position = location.Offset;
        using var reader = new BinaryReader(file); return RuntimeCheckpointCodec.ReadBytes(reader, location.Length);
    }
    // One immutable file and flush per batch, never per simulator frame.
    internal void CommitBatch(IReadOnlyList<RawPendingEffect> pending)
    {
        if (pending.Count == 0) return;
        lock (_gate)
        {
            LoadIndex();
            var fresh = pending.Where(p => (_collected == null || p.Cursor.CompareTo(_collected) >= 0) && !_locations.ContainsKey(p.Id)).ToArray();
            foreach (var p in pending.Where(p => _locations.ContainsKey(p.Id)))
                if (!ReadBytes(p.Id).SequenceEqual(p.Bytes)) throw new InvalidDataException("RAW input identity was reused with different effects.");
            if (fresh.Length == 0) return;
            var total = checked(8L + fresh.Sum(p => 4L + p.Bytes.Length));
            if (fresh.Length > 4096 || total > 16L * 1024 * 1024) throw new InvalidDataException("RAW batch exceeds its bound.");
            if (_index!.Count + fresh.Length > 65536 || total > _quota - _bytes) throw new IOException("RAW recovery storage is full.");
            foreach (var p in fresh) Reserve(p.Cursor, p.Id, 0);
            var path = Path.Combine(Root, fresh[0].Id.ToString("N") + ".batch");
            DurableFile.Write(path, stream =>
            {
                using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
                writer.Write(0x32455752); writer.Write(fresh.Length);
                foreach (var p in fresh) { writer.Write(p.Bytes.Length); writer.Write(p.Bytes); }
            });
            long offset = 8;
            foreach (var p in fresh) { offset += 4; Add(p.Cursor, p.Id, path, offset, p.Bytes.Length); offset += p.Bytes.Length; }
            _bytes += total;
        }
    }
    public RawEffectBatch Read(Guid id)
    {
        byte[] bytes; lock (_gate) { LoadIndex(); bytes = ReadBytes(id); }
        using var stream = new MemoryStream(bytes, false);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("RAW effect batch exceeds its bound.");
        using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != Magic || new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)) != id) throw new InvalidDataException("Invalid RAW effect identity.");
        var cursor = new JournalCursor(reader.ReadInt64(), reader.ReadInt64());
        if (cursor.Segment < 1 || cursor.Sequence < 1) throw new InvalidDataException("Invalid RAW effect cursor.");
        var count = RuntimeCheckpointCodec.ReadCount(reader, 8); var effects = new List<object>();
        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadByte())
            {
                case 1:
                    var timestamp = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                    var sim = RuntimeCheckpointCodec.ReadText(reader); var title = RuntimeCheckpointCodec.ReadText(reader);
                    var type = RuntimeCheckpointCodec.ReadText(reader); var model = RuntimeCheckpointCodec.ReadText(reader);
                    var sources = new string[RuntimeCheckpointCodec.ReadCount(reader, 64)];
                    for (var n = 0; n < sources.Length; n++) sources[n] = RuntimeCheckpointCodec.ReadText(reader);
                    effects.Add(new RawDebugCaptureStartedEventArgs(RuntimeCheckpointCodec.ReadSamples(reader), timestamp, sim, title, type, model, sources)); break;
                case 2: effects.Add(new RawDebugSampleEventArgs(RuntimeCheckpointCodec.ReadSample(reader) ?? throw new InvalidDataException("Missing RAW sample."))); break;
                case 3: effects.Add(EventArgs.Empty); break;
                case 4: effects.Add(new RawCaptureGap(reader.ReadInt64(), RuntimeCheckpointCodec.ReadText(reader))); break;
                default: throw new InvalidDataException("Unknown RAW effect.");
            }
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing RAW effect bytes.");
        return new RawEffectBatch(id, cursor, effects);
    }
    public IEnumerable<Guid> Enumerate()
    { lock (_gate) { LoadIndex(); return _index!.Values.ToArray(); } }
    public void Import(RawEffectStore source, Guid id, string hash)
    {
        lock (_gate)
        {
            EpisodeTransactionStore.Verify(source.PathFor(id), hash); var incoming = source.Read(id); LoadIndex();
            if (_collected != null && incoming.Cursor.CompareTo(_collected) < 0) return;
            if (File.Exists(PathFor(id))) { EpisodeTransactionStore.Verify(PathFor(id), hash); return; }
            var length = new FileInfo(source.PathFor(id)).Length; Reserve(incoming.Cursor, id, length);
            EpisodeTransactionStore.CopyImmutable(source.PathFor(id), PathFor(id), hash);
            Add(incoming.Cursor, id, PathFor(id), 0, checked((int)length)); _bytes += length;
        }
    }
    public IReadOnlyList<Guid> After(JournalCursor? cursor, bool inclusive = false)
    {
        lock (_gate)
        {
            LoadIndex();
            return _index!.Where(item => cursor == null || item.Key.CompareTo(cursor) > 0 || inclusive && item.Key.CompareTo(cursor) == 0).Select(item => item.Value).ToArray();
        }
    }
    // Caller has already flushed its export checkpoint. Keep the cursor's own
    // batch because a chunk boundary can occur inside its initial sample array.
    public void CollectBefore(JournalCursor cursor)
    {
        lock (_gate)
        {
            LoadIndex();
            if (_collected == null || cursor.CompareTo(_collected) > 0)
            { DurableFile.WriteJson(Path.Combine(Root, "collected.json"), cursor); _collected = cursor; }
            foreach (var group in _locations.GroupBy(pair => pair.Value.Path).ToArray())
            {
                if (group.Any(item => item.Value.Cursor.CompareTo(_collected) >= 0)) continue;
                var length = new FileInfo(group.Key).Length; File.Delete(group.Key);
                foreach (var item in group.ToArray())
                { _index!.Remove(item.Value.Cursor); _locations.Remove(item.Key); }
                _bytes -= length;
            }
        }
    }
    private void LoadIndex()
    {
        if (_index != null) return;
        var index = new SortedDictionary<JournalCursor, Guid>(); long bytes = 0;
        var marker = Path.Combine(Root, "collected.json");
        var collected = File.Exists(marker) ? DurableFile.ReadJson<JournalCursor>(marker, 4096) : null;
        if (collected != null && (collected.Segment < 1 || collected.Sequence < 1)) throw new InvalidDataException("Invalid RAW collection marker.");
        var locations = new Dictionary<Guid, Location>();
        if (Directory.Exists(Root))
        foreach (var path in Directory.EnumerateFiles(Root).Where(p => p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".batch", StringComparison.OrdinalIgnoreCase)))
        {
            using var file = File.OpenRead(path); using var reader = new BinaryReader(file);
            if (file.Length > MaximumBytes) throw new InvalidDataException("RAW effect exceeds its bound.");
            var batched = path.EndsWith(".batch", StringComparison.OrdinalIgnoreCase);
            var count = 1;
            if (batched) { if (reader.ReadInt32() != 0x32455752) throw new InvalidDataException("Invalid RAW batch header."); count = RuntimeCheckpointCodec.ReadCount(reader, 4096); }
            for (var i = 0; i < count; i++)
            {
                var length = batched ? RuntimeCheckpointCodec.ReadCount(reader, (int)MaximumBytes) : checked((int)file.Length);
                var offset = file.Position;
                if (length < 40 || offset + length > file.Length || reader.ReadInt32() != Magic) throw new InvalidDataException("Invalid RAW effect identity.");
                var id = new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16));
                var cursor = new JournalCursor(reader.ReadInt64(), reader.ReadInt64());
                if (id == Guid.Empty || cursor.Segment < 1 || cursor.Sequence < 1 || index.ContainsKey(cursor) || locations.ContainsKey(id)) throw new InvalidDataException("RAW effect cursors are ambiguous.");
                if (!batched && Path.GetFileNameWithoutExtension(path) != id.ToString("N")) throw new InvalidDataException("Invalid RAW effect filename.");
                index.Add(cursor, id); locations.Add(id, new Location(path, offset, length, cursor)); file.Position = offset + length;
                if (index.Count > 65536) throw new InvalidDataException("RAW effect count exceeds its bound.");
            }
            if (file.Position != file.Length) throw new InvalidDataException("Trailing RAW batch bytes.");
            bytes += file.Length;
        }
        _locations.Clear(); foreach (var item in locations) _locations.Add(item.Key, item.Value);
        _collected = collected; _bytes = bytes; _index = index;
    }
    private void Reserve(JournalCursor cursor, Guid id, long length)
    {
        if (_index!.TryGetValue(cursor, out var previous) && previous != id) throw new InvalidDataException("RAW cursor refers to another input.");
        if (_index.Count >= 65536 || length > _quota - _bytes) throw new IOException("RAW recovery storage is full; accepted inputs retained for retry.");
    }
}
