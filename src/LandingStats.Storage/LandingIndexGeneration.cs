using System;
using System.IO;
using System.Security.Cryptography;

namespace LandingStats.App.Storage;

// A generation is part of the atomic index file, in a standard gzip FEXTRA
// subfield. The decompressed JSON array and ordinary gzip readers are unchanged.
internal static class LandingIndexGeneration
{
    internal const string Missing = "missing";
    internal static string Name(Guid generation) => "gzip:" + generation.ToString("N");
    internal static Stream Wrap(Stream destination, Guid generation) => new GenerationStream(destination, generation);

    internal static string Read(Stream file)
    {
        try
        {
            var header = new byte[10];
            if (ReadFully(file, header) && header[0] == 31 && header[1] == 139 && header[2] == 8 && (header[3] & 4) != 0)
            {
                var low = file.ReadByte(); var high = file.ReadByte();
                if (low >= 0 && high >= 0)
                {
                    var extra = new byte[low | (high << 8)]; // Gzip XLEN bounds this allocation to 65535.
                    if (ReadFully(file, extra))
                    {
                        for (var offset = 0; offset + 4 <= extra.Length;)
                        {
                            var length = extra[offset + 2] | (extra[offset + 3] << 8);
                            if (length > extra.Length - offset - 4) break;
                            if (extra[offset] == (byte)'L' && extra[offset + 1] == (byte)'S' && length == 16)
                            {
                                var bytes = new byte[16]; Array.Copy(extra, offset + 4, bytes, 0, bytes.Length);
                                return Name(new Guid(bytes));
                            }
                            offset += 4 + length;
                        }
                    }
                }
            }
            // Existing indexes have no generation. Their exact file is the
            // baseline until the first successful generation-bearing write.
            file.Position = 0;
            using var sha = SHA256.Create();
            return "legacy:" + Convert.ToBase64String(sha.ComputeHash(file));
        }
        finally { file.Position = 0; }
    }

    private static bool ReadFully(Stream source, byte[] bytes)
    {
        var read = 0;
        while (read < bytes.Length)
        {
            var count = source.Read(bytes, read, bytes.Length - read);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }

    private sealed class GenerationStream : Stream
    {
        private readonly Stream _destination;
        private readonly byte[] _header = new byte[10];
        private readonly byte[] _extra;
        private int _headerBytes;
        internal GenerationStream(Stream destination, Guid generation)
        {
            _destination = destination;
            _extra = new byte[22];
            _extra[0] = 20; // XLEN: four-byte subfield header and sixteen-byte Guid.
            _extra[2] = (byte)'L'; _extra[3] = (byte)'S'; _extra[4] = 16;
            Array.Copy(generation.ToByteArray(), 0, _extra, 6, 16);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
            if (_headerBytes < _header.Length)
            {
                var copied = Math.Min(count, _header.Length - _headerBytes);
                Array.Copy(buffer, offset, _header, _headerBytes, copied);
                _headerBytes += copied; offset += copied; count -= copied;
                if (_headerBytes == _header.Length)
                {
                    if (_header[0] != 31 || _header[1] != 139 || _header[2] != 8 || _header[3] != 0)
                        throw new InvalidDataException("Unexpected generated gzip header.");
                    _header[3] = 4;
                    _destination.Write(_header, 0, _header.Length);
                    _destination.Write(_extra, 0, _extra.Length);
                }
            }
            if (count != 0) _destination.Write(buffer, offset, count);
        }
        public override void Flush() => _destination.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
