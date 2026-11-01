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
using LandingStats.App.Runtime;
using LandingStats.Archives;

namespace LandingStats.App.Storage;

/// <summary>Portable, self-contained episode files. Callers serialize repository access.</summary>
public static class LandingShareArchive
{
    public const string Extension = ".lslanding";
    private const string FormatName = "MSFS Landing Stats landing";
    private const string ManifestName = "manifest.json", PayloadName = "landings.json";
    private const int Version = 1, MaximumManifestBytes = 4096;
    // Sharing uses the established bounded ZIP reader, including central/local
    // directory agreement and streamed expansion limits. Nothing is extracted.
    public const long MaximumDecodedBytes = CaptureArchiveFormat.MaximumDecodedBytes;

    public static void Export(LandingRepository repository, string selectedId, string destination)
    {
        if (repository == null) throw new ArgumentNullException(nameof(repository));
        RequireExtension(destination);
        var summaries = repository.LoadAll();
        var selected = summaries.FirstOrDefault(record => record.Id == selectedId) ??
            throw new FileNotFoundException("The selected landing is no longer available.");
        var contacts = LandingReplayContacts.ForEpisode(selected, summaries)
            .Select(summary => repository.LoadDetail(summary) ?? throw new InvalidDataException("A landing detail could not be read."))
            .ToArray();
        var payload = CanonicalPayload(contacts);
        var bytes = Serialize(payload, MaximumDecodedBytes - MaximumManifestBytes);
        var manifest = Serialize(new Manifest { Format = FormatName, Version = Version, Contacts = contacts.Length, Sha256 = Hash(bytes) }, MaximumManifestBytes);
        DurableFile.Write(destination, output =>
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
            WriteEntry(zip, ManifestName, manifest); WriteEntry(zip, PayloadName, bytes);
        });
    }

    public static LandingShareImportResult Import(LandingRepository repository, IEnumerable<string> paths)
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
                var payload = Read(path);
                var records = payload.Contacts.Select(contact => contact.ToRecord()).ToArray();
                var canonical = CanonicalPayload(records);
                var packageHash = Hash(Serialize(canonical, MaximumDecodedBytes - MaximumManifestBytes));
                records = canonical.Contacts.Select(contact => contact.ToRecord()).ToArray();
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
                    var id = Hash(Encoding.UTF8.GetBytes("landing-share-v1\n" + packageHash + "\n" + sourceId)).Substring(0, 32);
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
            catch (Exception error) when (IsImportFailure(error))
            { failures.Add(new LandingShareImportFailure(path, error.Message)); }
        }
        return new LandingShareImportResult(imported, duplicates, preferred, failures);
    }

    private static Payload Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new CaptureArchiveReader(input, leaveOpen: true);
        if (archive.Format != CaptureArchiveFormat.LegacyZip || archive.Entries.Count != 2 ||
            archive.GetEntry(ManifestName) == null || archive.GetEntry(PayloadName) == null)
            throw new InvalidDataException("The file is not a supported landing share.");
        if (archive.GetEntry(ManifestName)!.Length > MaximumManifestBytes)
            throw new InvalidDataException("The landing share descriptor exceeds its size limit.");
        var manifest = Deserialize<Manifest>(ReadEntry(archive, ManifestName));
        if (manifest.Format != FormatName || manifest.Version != Version)
            throw new InvalidDataException("The landing share format is unsupported. A newer application version may be required.");
        if (manifest.Contacts < 1 || !IsHash(manifest.Sha256))
            throw new InvalidDataException("The landing share descriptor is invalid.");
        var bytes = ReadEntry(archive, PayloadName);
        if (!string.Equals(Hash(bytes), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The landing share checksum does not match its contents.");
        var payload = Deserialize<Payload>(bytes);
        if (payload.Contacts == null || payload.Contacts.Count != manifest.Contacts)
            throw new InvalidDataException("The landing share contact count differs from its descriptor.");
        // ToRecord validates every recorded column and optional snapshot. Never
        // let newer record layouts fall through as an empty or partial landing.
        foreach (var contact in payload.Contacts)
        {
            if (contact?.Summary == null || contact.LayoutVersion != 7 || contact.Summary.FormatVersion != LandingRecord.CurrentFormatVersion)
                throw new InvalidDataException("A shared landing detail uses an unsupported version.");
            if (contact.Series == null || contact.Engines == null || contact.Contacts == null ||
                contact.Engines.Any(engine => engine == null) || contact.Contacts.Any(point => point == null))
                throw new InvalidDataException("A shared landing detail is incomplete.");
            _ = contact.ToRecord();
        }
        return payload;
    }

    private static Payload CanonicalPayload(IReadOnlyList<LandingRecord> records)
    {
        if (records.Count == 0) throw new InvalidDataException("The landing share contains no contacts.");
        var first = records[0];
        var originalEpisode = first.IsImported ? first.ImportedSourceEpisodeId : first.RecordingEpisodeId;
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
            var sourceId = original.IsImported ? original.ImportedSourceId : original.Id;
            var sourceEpisode = original.IsImported ? original.ImportedSourceEpisodeId : original.RecordingEpisodeId;
            if (!string.Equals(originalEpisode, sourceEpisode, StringComparison.Ordinal))
                throw new InvalidDataException("The shared contacts belong to different landing episodes.");
            if (!ValidIdentity(sourceId) || original.ContactNumber < 1 || original.ContactCount < original.ContactNumber ||
                original.TimestampUtc == default || original.FormatVersion < 1 || original.FormatVersion > LandingRecord.CurrentFormatVersion ||
                original.IsSummaryOnly)
                throw new InvalidDataException("A shared landing contact is invalid or incomplete.");
            var copy = LandingRecordFile.FromRecord(original).ToRecord();
            copy.FormatVersion = LandingRecord.CurrentFormatVersion;
            copy.Id = sourceId!; copy.RecordingEpisodeId = episode;
            copy.IsImported = false; copy.ImportedSourceId = null; copy.ImportedSourceEpisodeId = null; copy.ImportedPackageHash = null;
            normalized.Add(copy);
        }
        if (normalized.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Count ||
            LandingReplayContacts.ForEpisode(normalized[0], normalized).Count != normalized.Count)
            throw new InvalidDataException("The shared contacts do not form one unambiguous landing episode.");
        return new Payload { Contacts = normalized.OrderBy(record => record.ContactNumber).ThenBy(record => record.Id, StringComparer.Ordinal)
            .Select(LandingRecordFile.FromRecord).ToList() };
    }

    private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value!.Length <= 512 && !value.Any(char.IsControl);
    private static bool IsHash(string? value) => value != null && value.Length == 64 && value.All(character =>
        character >= '0' && character <= '9' || character >= 'a' && character <= 'f' || character >= 'A' && character <= 'F');
    private static string Hash(byte[] bytes)
    { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
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
    private static byte[] Serialize<T>(T value, long maximum)
    {
        using var output = new LimitedMemoryStream(maximum);
        new DataContractJsonSerializer(typeof(T)).WriteObject(output, value); return output.ToArray();
    }
    private static T Deserialize<T>(byte[] bytes)
    {
        RequireSingleJsonObject(bytes);
        var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 64, MaxArrayLength = (int)MaximumDecodedBytes,
            MaxStringContentLength = (int)MaximumDecodedBytes, MaxBytesPerRead = 65536, MaxNameTableCharCount = 65536 };
        using var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, quotas);
        var value = (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(reader) ?? throw new InvalidDataException("The shared landing data is empty."));
        if (reader.Read()) throw new InvalidDataException("The shared landing data has an unexpected trailing value.");
        return value;
    }
    private static void RequireSingleJsonObject(byte[] bytes)
    {
        // DataContractJsonSerializer stops at the first root and its reader can
        // ignore following bytes. Check framing before its schema/JSON parser.
        var depth = 0; var started = false; var quoted = false; var escaped = false;
        foreach (var value in bytes)
        {
            var whitespace = value == ' ' || value == '\t' || value == '\r' || value == '\n';
            if (!started)
            {
                if (whitespace) continue;
                if (value != '{') throw new InvalidDataException("The shared landing data must contain one JSON object.");
                started = true; depth = 1; continue;
            }
            if (depth == 0)
            { if (!whitespace) throw new InvalidDataException("The shared landing data has an unexpected trailing value."); continue; }
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') quoted = false;
            }
            else if (value == '"') quoted = true;
            else if (value == '{' || value == '[') depth++;
            else if (value == '}' || value == ']') depth--;
            if (depth > 64) throw new InvalidDataException("The shared landing data exceeds its nesting limit.");
        }
        if (!started || depth != 0 || quoted) throw new InvalidDataException("The shared landing data is truncated.");
    }
    private static bool IsImportFailure(Exception error) => error is InvalidDataException || error is IOException || error is UnauthorizedAccessException ||
        error is SerializationException || error is XmlException || error is ArgumentException || error is InvalidOperationException;

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
    [DataContract]
    private sealed class Payload
    { [DataMember(Name = "contacts", Order = 1, IsRequired = true)] public List<LandingRecordFile> Contacts = new List<LandingRecordFile>(); }
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
