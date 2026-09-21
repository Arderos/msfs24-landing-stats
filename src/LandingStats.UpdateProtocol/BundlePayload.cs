using System;
using System.IO;
using System.Linq;
using System.Text;

namespace LandingStats.Packaging
{
    // Structural validation only. ReleaseUpdateProtocol authenticates the entire
    // download; Windows/SignTool separately verifies the Authenticode signature.
    public sealed class BundlePayload
    {
        public long Offset { get; private set; }
        public int Length { get; private set; }

        public static BundlePayload Locate(Stream input)
        {
            using (var reader = new BinaryReader(input, Encoding.UTF8, true))
            {
                if (input.Length < 64) throw Invalid();
                input.Position = 0;
                if (reader.ReadUInt16() != 0x5a4d) throw Invalid();
                input.Position = 0x3c;
                long pe = reader.ReadUInt32();
                if (pe < 64 || pe > input.Length - 24) throw Invalid();
                input.Position = pe;
                if (reader.ReadUInt32() != 0x00004550) throw Invalid();
                input.Position = pe + 6;
                var sections = reader.ReadUInt16();
                input.Position = pe + 20;
                var optionalSize = reader.ReadUInt16();
                long optional = pe + 24;
                long sectionTable = optional + optionalSize;
                if (optionalSize < 2 || sectionTable + sections * 40L > input.Length) throw Invalid();
                input.Position = optional;
                var kind = reader.ReadUInt16();
                var directories = kind == 0x10b ? 96 : kind == 0x20b ? 112 : 0;
                if (directories == 0 || optionalSize < directories + 40) throw Invalid();
                input.Position = optional + directories - 4;
                if (reader.ReadUInt32() < 5) throw Invalid();

                long imageEnd = sectionTable + sections * 40L;
                for (var i = 0; i < sections; i++)
                {
                    input.Position = sectionTable + i * 40L + 16;
                    long size = reader.ReadUInt32();
                    long offset = reader.ReadUInt32();
                    if (offset + size > input.Length) throw Invalid();
                    imageEnd = Math.Max(imageEnd, offset + size);
                }

                // The security directory contains a FILE OFFSET, not an RVA.
                input.Position = optional + directories + 32;
                long certificateOffset = reader.ReadUInt32();
                long certificateSize = reader.ReadUInt32();
                long end = input.Length;
                var signed = certificateOffset != 0 || certificateSize != 0;
                if (signed)
                {
                    if (certificateOffset < imageEnd || certificateOffset % 8 != 0 ||
                        certificateSize < 8 || certificateOffset + certificateSize != input.Length) throw Invalid();
                    for (long position = certificateOffset; position < input.Length;)
                    {
                        if (input.Length - position < 8) throw Invalid();
                        input.Position = position;
                        long length = reader.ReadUInt32();
                        if (length < 8 || reader.ReadUInt16() != 0x200 || reader.ReadUInt16() != 2) throw Invalid();
                        var alignedLength = (length + 7) & ~7L;
                        if (alignedLength > input.Length - position) throw Invalid();
                        position += alignedLength;
                    }
                    end = certificateOffset;
                }

                var magic = Encoding.ASCII.GetBytes("MSFSLSABUNDLE1");
                // SignTool inserts at most seven zero bytes before WIN_CERTIFICATE.
                // Never search inside the certificate itself for a bundle marker.
                for (var padding = 0; padding <= (signed ? 7 : 0); padding++)
                {
                    long trailer = end - padding - magic.Length - sizeof(long);
                    if (trailer <= imageEnd) break;
                    input.Position = trailer + sizeof(long);
                    if (reader.ReadBytes(magic.Length).SequenceEqual(magic))
                    {
                        input.Position = trailer;
                        long length = reader.ReadInt64();
                        if (length <= 0 || length > int.MaxValue || length > trailer - imageEnd) throw Invalid();
                        return new BundlePayload { Offset = trailer - length, Length = (int)length };
                    }
                    input.Position = end - padding - 1;
                    if (input.ReadByte() != 0) break;
                }
                throw Invalid();
            }
        }

        private static InvalidDataException Invalid()
        {
            return new InvalidDataException("The application bundle is missing, damaged, or has invalid PE bounds.");
        }
    }
}
