using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Runtime;

namespace LandingStats.App.Storage;

public static partial class LandingShareArchive
{
    private const string ContentsName = "contents.bin";
    private static readonly byte[] ContentsMagic = Encoding.ASCII.GetBytes("LSLAND01");
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static void ExportExtensible(LandingRecord[] contacts, string destination)
    {
        var budget = new LandingShareReadBudget();
        budget.ReserveContacts(contacts.Length);
        LandingRecord[] originals;
        if (contacts.Any(record => record.IsImported || record.ShareData != null))
        {
            if (contacts.Any(record => !record.IsImported || record.ShareData == null))
                throw new InvalidDataException("The shared episode mixes incompatible origins.");
            originals = contacts.Select(record =>
            {
                ReserveFeatureMetadata(record.ShareData!, budget);
                var original = LandingShareContent.Read(record.ShareData!, budget);
                if (original.Id != record.ImportedSourceId || original.RecordingEpisodeId != record.ImportedSourceEpisodeId)
                    throw new InvalidDataException("The retained share identity differs from its imported contact.");
                return original;
            }).ToArray();
        }
        else
        {
            originals = PrepareSourceContacts(contacts);
            foreach (var record in originals)
            {
                record.ShareData = LandingShareContent.Create(record);
                ReserveFeatureMetadata(record.ShareData, budget);
                _ = LandingShareContent.Read(record.ShareData, budget);
            }
        }
        ValidateEpisode(originals);
        var manifest = Serialize(new Manifest { Format = FormatName, Version = Version, Contacts = originals.Length, Sha256 = HashContents(originals) }, MaximumManifestBytes);
        DurableFile.Write(destination, output =>
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                WriteEntry(zip, ManifestName, manifest);
                using var contents = zip.CreateEntry(ContentsName, CompressionLevel.Optimal).Open();
                EncodeContents(originals, contents);
            }
            ValidateEncodedLength(output.Length);
        });
    }

    private static IncomingPackage ReadExtensible(Stream source, long length, int contactCount)
    {
        using var input = new ContentsReadStream(source, length);
        using var reader = new BinaryReader(input, StrictUtf8, true);
        if (!reader.ReadBytes(ContentsMagic.Length).SequenceEqual(ContentsMagic))
            throw new InvalidDataException("The landing share contents header is unsupported.");
        var count = ReadCount(reader, 8);
        if (count < 1 || count != contactCount) throw new InvalidDataException("The landing share contact count differs from its descriptor.");
        var budget = new LandingShareReadBudget();
        budget.ReserveContacts(count);
        var records = new List<LandingRecord>();
        for (var contact = 0; contact < count; contact++)
        {
            var data = new LandingShareData { CorePayload = ReadPayload(reader) };
            var blocks = ReadCount(reader, 14);
            budget.ReserveBlocks(blocks);
            var types = new HashSet<string>(StringComparer.Ordinal);
            for (var block = 0; block < blocks; block++)
            {
                var typeLength = ReadLength(reader);
                if (typeLength > 512 * 3) throw new InvalidDataException("The landing share extension type exceeds its length limit.");
                budget.ReserveTypeBytes(typeLength);
                var type = StrictUtf8.GetString(reader.ReadBytes(typeLength));
                var version = reader.ReadInt32();
                var flags = reader.ReadByte();
                if (!ValidIdentity(type) || version < 1 || flags > 1 || !types.Add(type))
                    throw new InvalidDataException("A landing share extension descriptor is invalid or duplicated.");
                data.Blocks.Add(new LandingShareBlock { Type = type, Version = version, Required = flags == 1, Payload = ReadPayload(reader) });
            }
            var record = LandingShareContent.Read(data, budget);
            record.ShareData = data;
            records.Add(record);
        }
        if (input.Position != input.Length || input.ReadByte() != -1) throw new InvalidDataException("The landing share contains trailing block data.");
        var result = records.ToArray();
        ValidateEpisode(result);
        // Framing and ordering are a fixed wire contract. No current DTO is
        // serialized here: future readers hash these exact original bytes too.
        return new IncomingPackage(result, HashContents(result));
    }

    private static void ReserveFeatureMetadata(LandingShareData data, LandingShareReadBudget budget)
    {
        if (data.Blocks == null) throw new InvalidDataException("The landing share extension list is missing.");
        budget.ReserveBlocks(data.Blocks.Count);
        foreach (var block in data.Blocks)
        {
            if (block == null || !ValidIdentity(block.Type)) throw new InvalidDataException("A landing share extension descriptor is invalid.");
            budget.ReserveTypeBytes(StrictUtf8.GetByteCount(block.Type));
        }
    }

    private static string HashContents(IEnumerable<LandingRecord> records)
    {
        using var hash = SHA256.Create();
        using var output = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        EncodeContents(records, output);
        output.FlushFinalBlock();
        return Hex(hash.Hash!);
    }

    private static void EncodeContents(IEnumerable<LandingRecord> records, Stream destination)
    {
        var ordered = records.OrderBy(record => record.Id, StringComparer.Ordinal).ToArray();
        using var output = new ContentsWriteStream(destination);
        using var writer = new BinaryWriter(output, StrictUtf8, true);
        writer.Write(ContentsMagic); writer.Write(ordered.Length);
        foreach (var record in ordered)
        {
            var data = record.ShareData ?? throw new InvalidDataException("The original share contents are missing.");
            WritePayload(writer, data.CorePayload);
            if (data.Blocks == null) throw new InvalidDataException("The landing share extension list is missing.");
            writer.Write(data.Blocks.Count);
            var types = new HashSet<string>(StringComparer.Ordinal);
            if (data.Blocks.Any(block => block == null)) throw new InvalidDataException("A landing share extension is missing.");
            foreach (var block in data.Blocks.OrderBy(block => block.Type, StringComparer.Ordinal))
            {
                if (!ValidIdentity(block.Type) || block.Version < 1 || !types.Add(block.Type))
                    throw new InvalidDataException("A landing share extension descriptor is invalid or duplicated.");
                WriteBytes(writer, StrictUtf8.GetBytes(block.Type));
                writer.Write(block.Version); writer.Write((byte)(block.Required ? 1 : 0));
                WritePayload(writer, block.Payload);
            }
        }
        writer.Flush();
    }

    private static void WritePayload(BinaryWriter writer, LandingSharePayload payload)
    {
        if (payload == null) throw new InvalidDataException("A retained landing share payload is missing.");
        writer.Write(payload.ByteCount); payload.WriteTo(writer.BaseStream);
    }
    private static void WriteBytes(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    private static LandingSharePayload ReadPayload(BinaryReader reader) => LandingSharePayload.Read(reader.BaseStream, ReadLength(reader));
    private static int ReadLength(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("A landing share block is truncated or exceeds its byte limit.");
        return length;
    }
    private static int ReadCount(BinaryReader reader, int minimumItemBytes)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumItemBytes)
            throw new InvalidDataException("The landing share block count exceeds its available bytes.");
        return count;
    }
    private static void ValidateEpisode(IReadOnlyList<LandingRecord> records)
    {
        if (records.Count == 0) throw new InvalidDataException("The landing share contains no contacts.");
        var episode = records[0].RecordingEpisodeId;
        if (!ValidIdentity(episode) || records.Any(record => !ValidIdentity(record.Id) || record.RecordingEpisodeId != episode ||
            record.TimestampUtc == default || record.ContactNumber < 1 || record.ContactCount < record.ContactNumber) ||
            records.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != records.Count ||
            LandingReplayContacts.ForEpisode(records[0], records).Count != records.Count)
            throw new InvalidDataException("The shared contacts do not form one unambiguous landing episode.");
    }

    private static void ValidateRetainedDetail(LandingRecord record)
    {
        if (record.ShareData == null) return;
        // Base64 plus a decoded UI projection must fit the same bound used by
        // history and cloud readers. Validate every contact before publication.
        using var counter = new DetailSizeCounter();
        if (record.FormatVersion >= 7) new DataContractJsonSerializer(typeof(LandingRecordFile)).WriteObject(counter, LandingRecordFile.FromRecord(record));
        else new DataContractJsonSerializer(typeof(LandingRecord)).WriteObject(counter, record);
    }
    private sealed class DetailSizeCounter : Stream
    {
        private long _length;
        public override void Write(byte[] buffer, int offset, int count)
        { if (count > BoundedHistoryStream.MaximumDetailBytes - _length) throw new InvalidDataException("The retained share exceeds the history detail limit."); _length += count; }
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ContentsWriteStream : Stream
    {
        private readonly Stream _output;
        private long _written;
        internal ContentsWriteStream(Stream output) { _output = output; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > MaximumDecodedBytes - MaximumManifestBytes - _written)
                throw new InvalidDataException("The landing share exceeds its decoded size limit.");
            _output.Write(buffer, offset, count); _written += count;
        }
        public override void Flush() => _output.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class ContentsReadStream : Stream
    {
        private readonly Stream _input;
        private readonly long _length;
        private long _position;
        internal ContentsReadStream(Stream input, long length) { _input = input; _length = length; }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = _input.Read(buffer, offset, count); _position += read; return read; }
        public override int ReadByte() { var value = _input.ReadByte(); if (value >= 0) _position++; return value; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
