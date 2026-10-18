using System;
using System.IO;

namespace LandingStats.Archives;

// Only framing is inspected here. The library decoder verifies block contents.
// Capture v2 contains exactly one ordinary dictionary-free frame per member.
// https://github.com/facebook/zstd/blob/v1.5.7/doc/zstd_compression_format.md
internal static class ZstdFrameBoundary
{
    public static void Validate(Stream input, long decodedLength)
    {
        ulong Number(int bytes)
        {
            ulong value = 0;
            for (var i = 0; i < bytes; i++) { var b = input.ReadByte(); if (b < 0) throw new InvalidDataException("Truncated Zstd frame."); value |= (ulong)b << (8 * i); }
            return value;
        }
        if (Number(4) != 0xfd2fb528) throw new InvalidDataException("Capture requires a standard Zstd frame.");
        var descriptor = (int)Number(1); var single = (descriptor & 32) != 0;
        if ((descriptor & 24) != 0) throw new InvalidDataException("Unsupported Zstd frame flags.");
        ulong window = 0;
        if (!single) { var wd = (int)Number(1); var basis = 1UL << (10 + (wd >> 3)); window = basis + (basis >> 3) * (uint)(wd & 7); }
        var dictionaryBytes = new[] { 0, 1, 2, 4 }[descriptor & 3];
        if (Number(dictionaryBytes) != 0) throw new InvalidDataException("Capture dictionaries are unsupported.");
        var sizeBytes = new[] { single ? 1 : 0, 2, 4, 8 }[descriptor >> 6]; var size = Number(sizeBytes);
        if (sizeBytes == 2) size += 256;
        if (sizeBytes != 0 && size != (ulong)decodedLength) throw new InvalidDataException("Zstd frame length differs from capture descriptor.");
        if (single) window = size;
        if (window > 128UL * 1024 * 1024) throw new InvalidDataException("Zstd frame window exceeds its bound.");
        var buffer = new byte[65536]; var blocks = 0;
        while (true)
        {
            var block = (uint)Number(3); var type = (block >> 1) & 3; var sizeOnDisk = (int)(block >> 3);
            if (++blocks > 65536 || type == 3 || sizeOnDisk > 131072) throw new InvalidDataException("Invalid Zstd block.");
            if (type == 1) sizeOnDisk = 1;
            while (sizeOnDisk > 0) { var read = input.Read(buffer, 0, Math.Min(buffer.Length, sizeOnDisk)); if (read == 0) throw new InvalidDataException("Truncated Zstd block."); sizeOnDisk -= read; }
            if ((block & 1) != 0) break;
        }
        if ((descriptor & 4) != 0) Number(4);
        if (input.ReadByte() != -1) throw new InvalidDataException("Trailing data after Zstd capture frame.");
    }
}
