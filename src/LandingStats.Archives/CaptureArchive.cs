using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace LandingStats.Archives;

public static class CaptureArchiveFormat
{
    public const int LegacyZip = 1, ZstdZip = 2;
    public const string ManifestName = "archive-format.bin";
    public const long MaximumDecodedBytes = 256L * 1024 * 1024;
    // A 1 MiB controller catalog plus its text keys, identity labels and sparse
    // slot placeholders fits in two payload budgets; the old eight-device limit did not.
    public const int MaximumSessionMetadataBytes = 2 * 1024 * 1024;
    internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("LSAR0002");
    internal static readonly string[] Names = { "telemetry.csv", "session.txt", "landing-results.json", "decisions.json", "controller-catalogs.json", "source-state.bin" };
    internal static long Limit(string name) => name == "session.txt" ? MaximumSessionMetadataBytes : name == "decisions.json" ? 4L * 1024 * 1024 :
        name == "controller-catalogs.json" ? 8L * 1024 * 1024 : name == "source-state.bin" ? 16L * 1024 * 1024 : MaximumDecodedBytes;
}

public sealed class CaptureArchiveEntry
{
    internal CaptureArchiveEntry(string name, long length, ZipArchiveEntry physical, byte[]? hash = null, bool zstd = false)
    { Name = name; Length = length; Physical = physical; Hash = hash; IsZstd = zstd; }
    public string Name { get; }
    public long Length { get; }
    public long CompressedLength => Physical.CompressedLength;
    public bool IsZstd { get; }
    internal ZipArchiveEntry Physical { get; }
    internal byte[]? Hash { get; }
}

/// <summary>Logical members of a legacy ZIP or a versioned Zstd-in-ZIP capture.
/// Zstd members use ZIP NoCompression (stored DEFLATE blocks on net48).
/// Names/lengths/hashes are
/// checked before use; a member is verified only after reading it to EOF.</summary>
public sealed class CaptureArchiveReader : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly CaptureArchiveEntry[] _entries;
    public CaptureArchiveReader(Stream input, bool leaveOpen = false, long maximumDecodedBytes = CaptureArchiveFormat.MaximumDecodedBytes)
    {
        if (maximumDecodedBytes < 1 || maximumDecodedBytes > CaptureArchiveFormat.MaximumDecodedBytes) throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
        Dictionary<string, DirectoryEntry> directory;
        try { directory = CheckDirectoryBound(input); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid capture member encoding.", error); }
        _zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen);
        try
        {
            if (_zip.Entries.Count != directory.Count || _zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != _zip.Entries.Count)
                throw new InvalidDataException("Duplicate or excessive capture members.");
            foreach (var entry in _zip.Entries)
                if (!directory.TryGetValue(entry.FullName, out var checkedEntry) || entry.Length != checkedEntry.Decoded || entry.CompressedLength != checkedEntry.Compressed)
                    throw new InvalidDataException("Capture ZIP parsers disagree on a directory entry.");
            var manifest = _zip.GetEntry(CaptureArchiveFormat.ManifestName);
            Format = manifest == null ? CaptureArchiveFormat.LegacyZip : CaptureArchiveFormat.ZstdZip;
            try { _entries = manifest == null ? _zip.Entries.Select(entry => new CaptureArchiveEntry(entry.FullName, entry.Length, entry)).ToArray() : ReadManifest(manifest); }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated capture descriptor.", error); }
            long total = 0;
            foreach (var entry in _entries)
            {
                if (entry.Length < 0 || entry.Length > CaptureArchiveFormat.Limit(entry.Name) || entry.Length > maximumDecodedBytes - total)
                    throw new InvalidDataException("Capture expands beyond its byte limit.");
                total += entry.Length;
                if (entry.Name.IndexOfAny(new[] { '/', '\\' }) >= 0 || entry.Name.StartsWith(".", StringComparison.Ordinal))
                    throw new InvalidDataException("Capture member path is unsafe.");
            }
            DecodedLength = total;
        }
        catch { _zip.Dispose(); throw; }
    }
    public int Format { get; }
    public long DecodedLength { get; }
    public IReadOnlyList<CaptureArchiveEntry> Entries => _entries;
    public CaptureArchiveEntry? GetEntry(string name) => _entries.FirstOrDefault(entry => entry.Name == name);
    public Stream Open(string name)
    {
        var entry = GetEntry(name) ?? throw new InvalidDataException("Capture member is missing: " + name);
        if (entry.IsZstd) { using var frame = OpenPhysical(entry.Physical); ZstdFrameBoundary.Validate(frame, entry.Length); }
        Stream input = OpenPhysical(entry.Physical);
        try
        {
            if (entry.IsZstd)
            {
                var decoder = new DecompressionStream(input, leaveOpen: false);
                input = decoder;
                decoder.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 27);
            }
            return new VerifiedReadStream(input, entry.Length, entry.Hash);
        }
        catch { input.Dispose(); throw; }
    }
    private static Stream OpenPhysical(ZipArchiveEntry entry) => new VerifiedReadStream(entry.Open(), entry.Length, null);
    private CaptureArchiveEntry[] ReadManifest(ZipArchiveEntry manifest)
    {
        if (manifest.Length < 9 || manifest.Length > 4096) throw new InvalidDataException("Invalid capture format descriptor size.");
        using var input = OpenPhysical(manifest); using var reader = new BinaryReader(input, Encoding.ASCII, true);
        if (!reader.ReadBytes(8).SequenceEqual(CaptureArchiveFormat.Magic)) throw new InvalidDataException("Unsupported capture archive format.");
        var count = reader.ReadByte();
        if (count < 2 || count > 6 || count + 1 != _zip.Entries.Count) throw new InvalidDataException("Capture descriptor member count differs.");
        var entries = new List<CaptureArchiveEntry>(); var physicalNames = new HashSet<string>(StringComparer.Ordinal) { CaptureArchiveFormat.ManifestName };
        for (var i = 0; i < count; i++)
        {
            var nameBytes = reader.ReadByte(); if (nameBytes < 1 || nameBytes > 32) throw new InvalidDataException("Invalid capture member name.");
            var name = Encoding.ASCII.GetString(Exact(reader, nameBytes)); var codec = reader.ReadByte(); var length = reader.ReadInt64(); var hash = Exact(reader, 32);
            if (!CaptureArchiveFormat.Names.Contains(name) || entries.Any(entry => entry.Name == name) || codec != (name == "session.txt" ? 0 : 1))
                throw new InvalidDataException("Invalid capture member descriptor.");
            var physicalName = codec == 0 ? name : name + ".zst";
            var physical = _zip.GetEntry(physicalName) ?? throw new InvalidDataException("Encoded capture member is missing.");
            if (codec == 0 && physical.Length != length || codec == 1 && (length < 0 || physical.Length > length + length / 100 + 1024))
                throw new InvalidDataException("Encoded capture member size or storage method differs.");
            physicalNames.Add(physicalName); entries.Add(new CaptureArchiveEntry(name, length, physical, hash, codec == 1));
        }
        if (input.ReadByte() != -1 || !entries.Any(entry => entry.Name == "session.txt") || !entries.Any(entry => entry.Name == "telemetry.csv") ||
            _zip.Entries.Any(entry => !physicalNames.Contains(entry.FullName))) throw new InvalidDataException("Invalid capture descriptor tail or members.");
        return entries.ToArray();
    }
    private static byte[] Exact(BinaryReader reader, int count)
    { var bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new InvalidDataException("Truncated capture descriptor."); return bytes; }
    private sealed class DirectoryEntry
    {
        public DirectoryEntry(uint compressed, uint decoded) { Compressed = compressed; Decoded = decoded; }
        public readonly uint Compressed, Decoded;
    }
    private static void CheckExtra(byte[] extra)
    {
        for (var offset = 0; offset < extra.Length;)
        {
            if (extra.Length - offset < 4) throw new InvalidDataException("Truncated ZIP extra field.");
            if (BitConverter.ToUInt16(extra, offset) == 1) throw new InvalidDataException("ZIP64 captures are unsupported.");
            offset += 4 + BitConverter.ToUInt16(extra, offset + 2);
            if (offset > extra.Length) throw new InvalidDataException("Truncated ZIP extra field.");
        }
    }
    private static Dictionary<string, DirectoryEntry> CheckDirectoryBound(Stream input)
    {
        // Bound central-directory allocation before ZipArchive materializes it.
        // Captures are small, single-disk archives; ZIP64 is never required.
        if (!input.CanRead || !input.CanSeek || input.Length < 22 || input.Length > 256L * 1024 * 1024)
            throw new InvalidDataException("Capture input is not a bounded seekable ZIP.");
        var length = (int)Math.Min(input.Length, 65557); var tail = new byte[length]; input.Position = input.Length - length;
        var got = 0; while (got < length) { var read = input.Read(tail, got, length - got); if (read == 0) throw new InvalidDataException("Truncated ZIP directory."); got += read; }
        for (var position = length - 22; position >= 0; position--)
        {
            if (BitConverter.ToUInt32(tail, position) != 0x06054b50 || position + 22 + BitConverter.ToUInt16(tail, position + 20) != length) continue;
            for (var commentOffset = position + 1; commentOffset <= length - 4; commentOffset++)
                if (BitConverter.ToUInt32(tail, commentOffset) == 0x06054b50) throw new InvalidDataException("Ambiguous capture ZIP directory.");
            if (position >= 20 && BitConverter.ToUInt32(tail, position - 20) == 0x07064b50)
                throw new InvalidDataException("ZIP64 captures are unsupported.");
            var count = BitConverter.ToUInt16(tail, position + 10);
            var end = input.Length - length + position;
            var size = BitConverter.ToUInt32(tail, position + 12); var start = BitConverter.ToUInt32(tail, position + 16);
            if (BitConverter.ToUInt32(tail, position + 4) != 0 || BitConverter.ToUInt16(tail, position + 8) != count || count > 7 || size > 32768 ||
                (long)start + size != end)
                throw new InvalidDataException("Capture ZIP directory exceeds its bound.");
            input.Position = start;
            var entries = new Dictionary<string, DirectoryEntry>(StringComparer.Ordinal);
            using var reader = new BinaryReader(input, Encoding.UTF8, true);
            for (var i = 0; i < count; i++)
            {
                var header = Exact(reader, 46);
                var nameLength = BitConverter.ToUInt16(header, 28); var extraLength = BitConverter.ToUInt16(header, 30); var commentLength = BitConverter.ToUInt16(header, 32);
                var method = BitConverter.ToUInt16(header, 10); var flags = BitConverter.ToUInt16(header, 8);
                if (BitConverter.ToUInt32(header, 0) != 0x02014b50 || nameLength > 256 || extraLength > 4096 || commentLength > 1024 ||
                    (flags & 0x41) != 0 || (method != 0 && method != 8) || BitConverter.ToUInt16(header, 34) != 0 ||
                    BitConverter.ToUInt32(header, 20) > 256L * 1024 * 1024 || BitConverter.ToUInt32(header, 24) > 256L * 1024 * 1024)
                    throw new InvalidDataException("Unsupported capture ZIP directory entry.");
                var name = new UTF8Encoding(false, true).GetString(Exact(reader, nameLength));
                if (entries.ContainsKey(name)) throw new InvalidDataException("Duplicate capture member.");
                var compressed = BitConverter.ToUInt32(header, 20); var decoded = BitConverter.ToUInt32(header, 24);
                entries.Add(name, new DirectoryEntry(compressed, decoded)); CheckExtra(Exact(reader, extraLength)); Exact(reader, commentLength);
                if (input.Position > end) throw new InvalidDataException("Capture ZIP directory is truncated.");
                var next = input.Position; var localOffset = BitConverter.ToUInt32(header, 42);
                if ((long)localOffset + 30 > start) throw new InvalidDataException("Capture local header is outside the data region.");
                input.Position = localOffset;
                var local = Exact(reader, 30);
                var localNameLength = BitConverter.ToUInt16(local, 26); var localExtraLength = BitConverter.ToUInt16(local, 28);
                if (BitConverter.ToUInt32(local, 0) != 0x04034b50 || BitConverter.ToUInt16(local, 8) != method ||
                    BitConverter.ToUInt16(local, 6) != flags || localNameLength > 256 || localExtraLength > 4096 ||
                    (long)localOffset + 30 + localNameLength + localExtraLength + compressed > start)
                    throw new InvalidDataException("Capture ZIP local header differs.");
                if (new UTF8Encoding(false, true).GetString(Exact(reader, localNameLength)) != name) throw new InvalidDataException("Capture ZIP local member name differs.");
                CheckExtra(Exact(reader, localExtraLength)); input.Position = next;
            }
            if (input.Position != end) throw new InvalidDataException("Capture ZIP directory count differs.");
            input.Position = 0; return entries;
        }
        throw new InvalidDataException("Capture ZIP directory is missing.");
    }
    public void Dispose() => _zip.Dispose();

    private sealed class VerifiedReadStream : Stream
    {
        private readonly Stream _inner; private readonly long _length; private readonly byte[]? _expected;
        private readonly SHA256 _hash = SHA256.Create(); private long _position; private bool _finished; private long _readTicks;
        public VerifiedReadStream(Stream inner, long length, byte[]? expected) { _inner = inner; _length = length; _expected = expected; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0 || _finished) return 0;
            var start = Stopwatch.GetTimestamp(); int read;
            try { read = _inner.Read(buffer, offset, Math.Min(count, 65536)); }
            catch (ZstdException error) { throw new InvalidDataException("Invalid Zstd capture member.", error); }
            _readTicks += Stopwatch.GetTimestamp() - start;
            if (_readTicks > 10L * Stopwatch.Frequency) throw new IOException("Capture decoding exceeded its time budget; retry the unchanged archive.");
            if (read > _length - _position) throw new InvalidDataException("Capture member exceeds its declared length.");
            if (read != 0) { _position += read; if (_expected != null) _hash.TransformBlock(buffer, offset, read, buffer, offset); return read; }
            if (_position != _length) throw new InvalidDataException("Capture member is truncated.");
            _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (_expected != null && !_hash.Hash!.SequenceEqual(_expected)) throw new InvalidDataException("Capture member checksum differs.");
            _finished = true; return 0;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => _length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _inner.Dispose(); _hash.Dispose(); } base.Dispose(disposing); }
    }
}

/// <summary>Build a new immutable capture. Format selection precedes publication;
/// retries of a published capture never invoke this writer.</summary>
public sealed class CaptureArchiveWriter : IDisposable
{
    private readonly ZipArchive _zip; private readonly bool _zstd; private readonly DateTimeOffset? _timestamp;
    private readonly List<WrittenEntry> _entries = new List<WrittenEntry>(); private bool _writing, _disposed; private long _total;
    private MemberWriter? _active;
    public CaptureArchiveWriter(Stream output, bool zstd = false, bool leaveOpen = false, DateTimeOffset? timestamp = null)
    { _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen); _zstd = zstd; _timestamp = timestamp; }
    public Stream CreateEntry(string name)
    {
        if (_disposed || _writing || !CaptureArchiveFormat.Names.Contains(name) || _entries.Any(entry => entry.Name == name))
            throw new InvalidOperationException("Invalid capture member or concurrent writer.");
        var encoded = _zstd && name != "session.txt";
        var entry = _zip.CreateEntry(encoded ? name + ".zst" : name, encoded ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        if (_timestamp.HasValue) entry.LastWriteTime = _timestamp.Value;
        Stream output = entry.Open();
        if (encoded) output = new CompressionStream(output, 9, leaveOpen: false);
        _writing = true;
        return _active = new MemberWriter(output, Math.Min(CaptureArchiveFormat.Limit(name), CaptureArchiveFormat.MaximumDecodedBytes - _total), (length, hash) =>
        { _total += length; _entries.Add(new WrittenEntry(name, length, hash, encoded)); _writing = false; _active = null; });
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        // A failed Write/Flush already delivered its exception to the caller.
        // Cleanup must not replace it with "unfinished member" or another I/O failure.
        if (_active?.Failed == true)
        {
            try { _active.Abort(); } catch { }
            try { _zip.Dispose(); } catch { }
            return;
        }
        Exception? failure = null;
        try
        {
            if (_writing) { _active?.Abort(); throw new InvalidOperationException("Capture member did not finish; archive cannot be published."); }
            if (!_zstd) return;
            if (!_entries.Any(entry => entry.Name == "telemetry.csv") || !_entries.Any(entry => entry.Name == "session.txt"))
                throw new InvalidDataException("Capture requires telemetry and session metadata.");
            var entry = _zip.CreateEntry(CaptureArchiveFormat.ManifestName, CompressionLevel.NoCompression);
            if (_timestamp.HasValue) entry.LastWriteTime = _timestamp.Value;
            using var output = entry.Open(); using var writer = new BinaryWriter(output, Encoding.ASCII, true);
            writer.Write(CaptureArchiveFormat.Magic); writer.Write((byte)_entries.Count);
            foreach (var value in _entries)
            {
                var name = Encoding.ASCII.GetBytes(value.Name); writer.Write((byte)name.Length); writer.Write(name);
                writer.Write((byte)(value.Zstd ? 1 : 0)); writer.Write(value.Length); writer.Write(value.Hash);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (failure == null) _zip.Dispose();
            else { try { _zip.Dispose(); } catch { } }
        }
    }
    private sealed class WrittenEntry
    {
        public WrittenEntry(string name, long length, byte[] hash, bool zstd) { Name = name; Length = length; Hash = hash; Zstd = zstd; }
        public readonly string Name; public readonly long Length; public readonly byte[] Hash; public readonly bool Zstd;
    }
    private sealed class MemberWriter : Stream
    {
        private readonly Stream _inner; private readonly long _limit; private readonly Action<long, byte[]> _complete;
        private readonly SHA256 _hash = SHA256.Create(); private long _length; private bool _failed, _disposed;
        public MemberWriter(Stream inner, long limit, Action<long, byte[]> complete) { _inner = inner; _limit = limit; _complete = complete; }
        public bool Failed => _failed;
        public void Abort() { _failed = true; Dispose(); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_disposed || _failed) throw new InvalidOperationException("Capture member writer has retired.");
            try
            {
                if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
                if (count > _limit - _length) throw new InvalidDataException("Capture member exceeds its byte limit.");
                _inner.Write(buffer, offset, count); _hash.TransformBlock(buffer, offset, count, buffer, offset); _length += count;
            }
            catch { _failed = true; throw; }
        }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => !_disposed;
        public override long Length => _length; public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { try { _inner.Flush(); } catch { _failed = true; throw; } }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try
                {
                    if (_failed) { try { _inner.Dispose(); } catch { } }
                    else _inner.Dispose();
                    if (!_failed) { _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); _complete(_length, _hash.Hash!); }
                }
                catch { _failed = true; throw; }
                finally { _hash.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
