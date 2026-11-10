using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace LandingStats.App.Models;

// Original exchange bytes belong to the detail, independently of its current
// UI/storage projection. Chunked Base64 is streamed by the history serializer;
// runtime storage holds binary bytes, without whole-payload UTF-16 copies.
[DataContract]
public sealed class LandingShareData
{
    public const int MaximumPayloadBytes = 256 * 1024 * 1024;
    public string Core { get => CorePayload.ToBase64(); set => CorePayload = LandingSharePayload.FromBase64(value); }
    [DataMember(Name = "core", Order = 1, EmitDefaultValue = false)]
    private string? LegacyCore { get => null; set { if (value != null) Core = value; } }
    [DataMember(Name = "blocks", Order = 2, IsRequired = true)] public List<LandingShareBlock> Blocks { get; set; } = new List<LandingShareBlock>();
    [DataMember(Name = "coreChunks", Order = 3, EmitDefaultValue = false)]
    internal LandingSharePayload CorePayload { get; set; } = new LandingSharePayload();
    public LandingShareData Copy() => new LandingShareData
    { CorePayload = CorePayload?.Copy()!, Blocks = Blocks?.Select(block => block?.Copy()!).ToList()! };
}

[DataContract]
public sealed class LandingShareBlock
{
    [DataMember(Name = "type", Order = 1, IsRequired = true)] public string Type { get; set; } = "";
    [DataMember(Name = "version", Order = 2, IsRequired = true)] public int Version { get; set; }
    [DataMember(Name = "required", Order = 3, IsRequired = true)] public bool Required { get; set; }
    public string Data { get => Payload.ToBase64(); set => Payload = LandingSharePayload.FromBase64(value); }
    [DataMember(Name = "data", Order = 4, EmitDefaultValue = false)]
    private string? LegacyData { get => null; set { if (value != null) Data = value; } }
    [DataMember(Name = "dataChunks", Order = 5, EmitDefaultValue = false)]
    internal LandingSharePayload Payload { get; set; } = new LandingSharePayload();
    public LandingShareBlock Copy() => new LandingShareBlock { Type = Type, Version = Version, Required = Required, Payload = Payload?.Copy()! };
}

// A collection contract lets DataContractJsonSerializer decode each chunk as it
// arrives and release its text, instead of building a second full Base64 string.
[CollectionDataContract]
internal sealed class LandingSharePayload : ICollection<string>
{
    // Multiple of three; both binary and encoded UTF-16 chunks stay below LOH size.
    private const int ChunkBytes = 24 * 1024;
    private readonly List<byte[]> _chunks = new List<byte[]>();
    internal int ByteCount { get; private set; }
    public LandingSharePayload() { }
    public int Count => _chunks.Count;
    public bool IsReadOnly => false;
    public void Add(string text)
    {
        if (text == null || text.Length > (ChunkBytes / 3) * 4)
            throw new SerializationException("A retained share chunk exceeds its byte limit.");
        AddBytes(Convert.FromBase64String(text));
    }
    private void AddBytes(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > ChunkBytes || bytes.Length > LandingShareData.MaximumPayloadBytes - ByteCount)
            throw new SerializationException("The retained share payload exceeds its byte limit.");
        if (_chunks.Count > 0 && _chunks[_chunks.Count - 1].Length != ChunkBytes)
            throw new SerializationException("Only the final retained share chunk may be partial.");
        _chunks.Add(bytes); ByteCount += bytes.Length;
    }
    public void Clear() { _chunks.Clear(); ByteCount = 0; }
    public bool Contains(string item) => this.Any(value => value == item);
    public void CopyTo(string[] array, int arrayIndex) { foreach (var item in this) array[arrayIndex++] = item; }
    public bool Remove(string item) => throw new NotSupportedException();
    public IEnumerator<string> GetEnumerator()
    { foreach (var chunk in _chunks) yield return Convert.ToBase64String(chunk); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal LandingSharePayload Copy()
    { var copy = new LandingSharePayload { ByteCount = ByteCount }; copy._chunks.AddRange(_chunks); return copy; }
    internal void WriteTo(Stream output)
    { foreach (var chunk in _chunks) output.Write(chunk, 0, chunk.Length); }
    internal Stream OpenRead() => new PayloadStream(_chunks, ByteCount);
    internal static LandingSharePayload Read(Stream input, int length)
    {
        if (length < 0 || length > LandingShareData.MaximumPayloadBytes)
            throw new InvalidDataException("The share payload exceeds its byte limit.");
        var result = new LandingSharePayload();
        while (length > 0)
        {
            var chunk = new byte[Math.Min(length, ChunkBytes)]; var offset = 0;
            while (offset < chunk.Length)
            {
                var read = input.Read(chunk, offset, chunk.Length - offset);
                if (read == 0) throw new InvalidDataException("The share payload is truncated.");
                offset += read;
            }
            result.AddBytes(chunk); length -= chunk.Length;
        }
        return result;
    }
    internal static LandingSharePayload FromBytes(byte[] bytes)
    { using var input = new MemoryStream(bytes, false); return Read(input, bytes.Length); }
    internal static LandingSharePayload FromBase64(string text)
    {
        if (text == null || text.Length > ((long)LandingShareData.MaximumPayloadBytes + 2) / 3 * 4)
            throw new InvalidDataException("The retained share payload exceeds its byte limit.");
        // Compatibility with earlier local detail files; new writes use chunks.
        return FromBytes(Convert.FromBase64String(text));
    }
    internal string ToBase64()
    { using var output = new MemoryStream(ByteCount); WriteTo(output); return Convert.ToBase64String(output.GetBuffer(), 0, ByteCount); }

    private sealed class PayloadStream : Stream
    {
        private readonly List<byte[]> _chunks;
        private readonly long _length;
        private long _position;
        private int _index, _offset;
        internal PayloadStream(List<byte[]> chunks, long length) { _chunks = chunks; _length = length; }
        public override int ReadByte()
        {
            if (_index == _chunks.Count) return -1;
            var chunk = _chunks[_index]; var value = chunk[_offset++]; _position++;
            if (_offset == chunk.Length) { _index++; _offset = 0; }
            return value;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (count > 0 && _index < _chunks.Count)
            {
                var chunk = _chunks[_index]; var read = Math.Min(count, chunk.Length - _offset);
                Array.Copy(chunk, _offset, buffer, offset, read);
                count -= read; offset += read; total += read; _position += read; _offset += read;
                if (_offset == chunk.Length) { _index++; _offset = 0; }
            }
            return total;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? _position : _length) + offset;
            if (position < 0 || position > _length) throw new IOException("Invalid share payload position.");
            _position = position; _index = 0;
            while (_index < _chunks.Count && position >= _chunks[_index].Length) position -= _chunks[_index++].Length;
            _offset = (int)position; return _position;
        }
        public override long Length => _length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
