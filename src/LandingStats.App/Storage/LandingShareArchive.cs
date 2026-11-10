using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using LandingStats.App.Models;
using LandingStats.Archives;

namespace LandingStats.App.Storage;

/// <summary>Portable, self-contained episode files. Callers serialize repository access.</summary>
public static partial class LandingShareArchive
{
    public const string Extension = ".lslanding";
    private const string FormatName = "MSFS Landing Stats landing";
    private const string ManifestName = "manifest.json";
    private const int Version = 1, MaximumManifestBytes = 4096;
    // Sharing uses the established bounded ZIP reader, including central/local
    // directory agreement and streamed expansion limits. Nothing is extracted.
    public const long MaximumDecodedBytes = LandingShareData.MaximumPayloadBytes;

    public static void Export(LandingRepository repository, string selectedId, string destination, object? repositoryGate = null)
    {
        if (repository == null) throw new ArgumentNullException(nameof(repository));
        RequireExtension(destination);
        LandingRecord[] contacts;
        lock (repositoryGate ?? repository)
        {
            var summaries = repository.LoadAll();
            var selected = summaries.FirstOrDefault(record => record.Id == selectedId) ??
                throw new FileNotFoundException("The selected landing is no longer available.");
            contacts = LandingReplayContacts.ForEpisode(selected, summaries)
                .Select(summary => repository.LoadDetail(summary) ?? throw new InvalidDataException("A landing detail could not be read."))
                .ToArray();
        }
        ExportExtensible(contacts, destination);
    }

    public static LandingShareImportResult Import(LandingRepository repository, IEnumerable<string> paths, object? repositoryGate = null)
    {
        if (repository == null) throw new ArgumentNullException(nameof(repository));
        if (paths == null) throw new ArgumentNullException(nameof(paths));
        var imported = new List<LandingRecord>(); var failures = new List<LandingShareImportFailure>();
        var duplicates = 0; string? preferred = null;
        foreach (var path in paths)
        {
            try
            {
                RequireExtension(path);
                var package = ReadPackage(path);
                var records = package.Records;
                var packageHash = package.Hash;
                lock (repositoryGate ?? repository)
                {
                    var existing = repository.LoadAll();
                    var pending = new List<LandingRecord>();
                    // Validate the whole package and resolve all conflicts before
                    // publishing any contact. Storage failures can leave committed
                    // contacts; their persistent provenance makes retry idempotent.
                    foreach (var record in records)
                    {
                        var sourceId = record.Id;
                        var matches = existing.Where(candidate => candidate.IsImported && candidate.ImportedPackageHash == packageHash &&
                            candidate.ImportedSourceId == sourceId).ToArray();
                        if (matches.Length > 1) throw new InvalidDataException("Several imported records claim the same shared contact.");
                        if (matches.Length == 1) { duplicates++; preferred ??= matches[0].Id; continue; }
                        var id = Hash(Encoding.UTF8.GetBytes("landing-share-v" + Version + "\n" + packageHash + "\n" + sourceId)).Substring(0, 32);
                        // Import is an explicit user action. Reimporting a deleted
                        // share creates a new identity, without clearing its old
                        // tombstone or changing cloud/recovery deletion semantics.
                        if (repository.IsDeleted(id)) id = Guid.NewGuid().ToString("N");
                        if (repository.Contains(id)) throw new InvalidDataException("A different local landing already uses the shared contact identity.");
                        record.ImportedSourceId = sourceId;
                        record.ImportedSourceEpisodeId = record.RecordingEpisodeId;
                        record.ImportedPackageHash = packageHash;
                        record.IsImported = true;
                        record.Id = id; record.RecordingEpisodeId = "share:" + packageHash;
                        ValidateRetainedDetail(record);
                        pending.Add(record);
                    }
                    using (repository.BeginBatch())
                    {
                        foreach (var record in pending)
                        {
                            // Reuse durable import publication and pending-index
                            // recovery. Shared imports never replace existing files.
                            var saved = repository.Import(repository.SerializeForBackup(record), record.Id);
                            imported.Add(saved); preferred ??= saved.Id;
                        }
                    }
                }
            }
            catch (Exception error) when (IsImportFailure(error))
            { failures.Add(new LandingShareImportFailure(path, error.Message)); }
        }
        return new LandingShareImportResult(imported, duplicates, preferred, failures);
    }

    private static IncomingPackage ReadPackage(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new CaptureArchiveReader(input, leaveOpen: true);
        if (archive.Format != CaptureArchiveFormat.LegacyZip || archive.Entries.Count != 2 ||
            archive.GetEntry(ManifestName) == null || archive.GetEntry(ContentsName) == null)
            throw new InvalidDataException("The file is not a supported landing share.");
        if (archive.GetEntry(ManifestName)!.Length > MaximumManifestBytes)
            throw new InvalidDataException("The landing share descriptor exceeds its size limit.");
        var manifest = Deserialize<Manifest>(ReadEntry(archive, ManifestName));
        if (manifest.Format != FormatName || manifest.Version != Version)
            throw new InvalidDataException("The landing share format is unsupported. A newer application version may be required.");
        if (manifest.Contacts < 1 || !IsHash(manifest.Sha256))
            throw new InvalidDataException("The landing share descriptor is invalid.");
        using (var contents = archive.Open(ContentsName))
        using (var hash = SHA256.Create())
            if (!string.Equals(Hex(hash.ComputeHash(contents)), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The landing share checksum does not match its contents.");
        using var payload = archive.Open(ContentsName);
        return ReadExtensible(payload, archive.GetEntry(ContentsName)!.Length, manifest.Contacts);
    }

    private static LandingRecord[] PrepareSourceContacts(IReadOnlyList<LandingRecord> records)
    {
        if (records.Count == 0) throw new InvalidDataException("The landing share contains no contacts.");
        var first = records[0];
        var originalEpisode = first.RecordingEpisodeId;
        var episode = originalEpisode;
        if (string.IsNullOrWhiteSpace(episode))
        {
            // The established replay owner groups legacy contacts by this exact
            // recording identity. Include all source IDs to distinguish subsets.
            episode = "legacy:" + Hash(Encoding.UTF8.GetBytes(string.Join("\n", records.Select(record => record.Id).OrderBy(id => id, StringComparer.Ordinal))));
        }
        if (!ValidIdentity(episode)) throw new InvalidDataException("The shared episode identity is invalid.");
        var normalized = new List<LandingRecord>();
        foreach (var original in records)
        {
            var sourceId = original.Id;
            var sourceEpisode = original.RecordingEpisodeId;
            if (!string.Equals(originalEpisode, sourceEpisode, StringComparison.Ordinal))
                throw new InvalidDataException("The shared contacts belong to different landing episodes.");
            if (!ValidIdentity(sourceId) || original.ContactNumber < 1 || original.ContactCount < original.ContactNumber ||
                original.TimestampUtc == default || original.FormatVersion < 1 || original.FormatVersion > LandingRecord.CurrentFormatVersion ||
                original.IsSummaryOnly)
                throw new InvalidDataException("A shared landing contact is invalid or incomplete.");
            var copy = LandingRecordFile.FromRecord(original).ToRecord();
            // The v7 container can carry older measurements. Their source
            // version determines quality semantics and must survive sharing.
            copy.Id = sourceId!; copy.RecordingEpisodeId = episode;
            copy.IsImported = false; copy.ImportedSourceId = null; copy.ImportedSourceEpisodeId = null; copy.ImportedPackageHash = null;
            copy.ShareData = null;
            normalized.Add(copy);
        }
        if (normalized.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Count ||
            LandingReplayContacts.ForEpisode(normalized[0], normalized).Count != normalized.Count)
            throw new InvalidDataException("The shared contacts do not form one unambiguous landing episode.");
        return normalized.ToArray();
    }

    private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value!.Length <= 512 && !value.Any(char.IsControl);
    private static bool IsHash(string? value) => value != null && value.Length == 64 && value.All(character =>
        character >= '0' && character <= '9' || character >= 'a' && character <= 'f' || character >= 'A' && character <= 'F');
    private static string Hash(byte[] bytes)
    { using var hash = SHA256.Create(); return Hex(hash.ComputeHash(bytes)); }
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    internal static void ValidateEncodedLength(long length)
    { if (length > MaximumDecodedBytes) throw new InvalidDataException("The encoded landing share exceeds its byte limit."); }
    private static void RequireExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a " + Extension + " landing file.");
    }
    private static byte[] ReadEntry(CaptureArchiveReader archive, string name)
    {
        using var input = archive.Open(name); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }
    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    { using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); output.Write(bytes, 0, bytes.Length); }
    internal static byte[] Serialize<T>(T value, long maximum)
    {
        using var output = new LimitedMemoryStream(maximum);
        new DataContractJsonSerializer(typeof(T)).WriteObject(output, value); return output.ToArray();
    }
    internal static T Deserialize<T>(byte[] bytes)
    { using var input = new MemoryStream(bytes, false); return Deserialize<T>(input); }
    internal static T Deserialize<T>(Stream input, LandingShareReadBudget? budget = null)
    {
        budget ??= new LandingShareReadBudget();
        RequireSingleJsonObject(input, budget);
        input.Position = 0;
        var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 64, MaxArrayLength = (int)MaximumDecodedBytes,
            MaxStringContentLength = (int)MaximumDecodedBytes, MaxBytesPerRead = 65536, MaxNameTableCharCount = 65536 };
        using var reader = JsonReaderWriterFactory.CreateJsonReader(input, quotas);
        var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
        { MaxItemsInObjectGraph = LandingShareReadBudget.MaximumGraphItems });
        var value = (T)(serializer.ReadObject(reader) ?? throw new InvalidDataException("The shared landing data is empty."));
        if (reader.Read()) throw new InvalidDataException("The shared landing data has an unexpected trailing value.");
        return value;
    }
    private static void RequireSingleJsonObject(Stream input, LandingShareReadBudget budget)
    {
        // DataContractJsonSerializer stops at the first root and its reader can
        // ignore following bytes. Check framing before its schema/JSON parser.
        // Account for JSON shape before the serializer allocates arrays, strings
        // or extension-data objects. A short numeric token can represent a much
        // larger typed value; raw UTF-8 byte length alone does not bound its graph.
        var depth = 0; var started = false; var quoted = false; var escaped = false; var scalar = false;
        var buffer = new byte[65536];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            long allocation = 0;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                var whitespace = value == ' ' || value == '\t' || value == '\r' || value == '\n';
                if (!started)
                {
                    if (whitespace) continue;
                    if (value != '{') throw new InvalidDataException("The shared landing data must contain one JSON object.");
                    started = true; depth = 1; allocation += LandingShareReadBudget.ObjectBytes; continue;
                }
                if (depth == 0)
                { if (!whitespace) throw new InvalidDataException("The shared landing data has an unexpected trailing value."); continue; }
                if (quoted)
                {
                    allocation += LandingShareReadBudget.TextByteCost;
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == '"') quoted = false;
                }
                else if (value == '"') { quoted = true; scalar = false; allocation += LandingShareReadBudget.StringBytes; }
                else if (value == '{' || value == '[')
                { depth++; scalar = false; allocation += value == '{' ? LandingShareReadBudget.ObjectBytes : LandingShareReadBudget.ArrayBytes; }
                else if (value == '}' || value == ']') { depth--; scalar = false; }
                else if (whitespace || value == ',' || value == ':') scalar = false;
                else
                {
                    if (!scalar) allocation += LandingShareReadBudget.ScalarBytes;
                    scalar = true; allocation += LandingShareReadBudget.TextByteCost;
                }
                if (depth > 64) throw new InvalidDataException("The shared landing data exceeds its nesting limit.");
            }
            budget.Reserve(allocation);
        }
        if (!started || depth != 0 || quoted) throw new InvalidDataException("The shared landing data is truncated.");
    }
    private static bool IsImportFailure(Exception error) => error is InvalidDataException || error is IOException || error is UnauthorizedAccessException ||
        error is SerializationException || error is XmlException || error is ArgumentException || error is InvalidOperationException || error is FormatException;

    private sealed class IncomingPackage
    {
        internal IncomingPackage(LandingRecord[] records, string hash)
        { Records = records; Hash = hash; }
        internal LandingRecord[] Records { get; }
        internal string Hash { get; }
    }

    private sealed class LimitedMemoryStream : MemoryStream
    {
        private readonly long _maximum;
        internal LimitedMemoryStream(long maximum) { _maximum = maximum; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > _maximum - Position) throw new InvalidDataException("The landing share exceeds its decoded size limit.");
            base.Write(buffer, offset, count);
        }
        public override void WriteByte(byte value)
        {
            if (Position >= _maximum) throw new InvalidDataException("The landing share exceeds its decoded size limit.");
            base.WriteByte(value);
        }
    }
    [DataContract]
    private sealed class Manifest
    {
        [DataMember(Name = "format", Order = 1, IsRequired = true)] public string Format = "";
        [DataMember(Name = "version", Order = 2, IsRequired = true)] public int Version;
        [DataMember(Name = "contacts", Order = 3, IsRequired = true)] public int Contacts;
        [DataMember(Name = "sha256", Order = 4, IsRequired = true)] public string Sha256 = "";
    }
}

public sealed class LandingShareImportResult
{
    internal LandingShareImportResult(IReadOnlyList<LandingRecord> imported, int duplicates, string? preferredRecordId,
        IReadOnlyList<LandingShareImportFailure> failures)
    { Imported = imported; DuplicateCount = duplicates; PreferredRecordId = preferredRecordId; Failures = failures; }
    public IReadOnlyList<LandingRecord> Imported { get; }
    public int DuplicateCount { get; }
    public string? PreferredRecordId { get; }
    public IReadOnlyList<LandingShareImportFailure> Failures { get; }
}

public sealed class LandingShareImportFailure
{
    internal LandingShareImportFailure(string path, string message) { Path = path; Message = message; }
    public string Path { get; }
    public string Message { get; }
}
