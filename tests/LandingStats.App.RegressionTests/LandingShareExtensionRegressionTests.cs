using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class LandingShareExtensionRegressionTests
{
    private const string FutureType = "org.example.future-control-evidence";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LSLAND01");

    internal static void RunAll()
    {
        using var fixture = new Fixture();
        NativeFormatAndCanonicalOrder(fixture);
        OpaqueDataSurvivesHistoryAndBackup(fixture);
        KnownBlocksRemainIndependent(fixture);
        OpaqueChangesAndSubsetsHaveSeparateIdentity(fixture);
        InvalidFeaturesAndFramingAreAtomic(fixture);
        ExportRejectsInvalidRetainedData(fixture);
        RetainedPayloadStorageUsesBoundedChunks();
    }

    private static void RetainedPayloadStorageUsesBoundedChunks()
    {
        var bytes = Enumerable.Range(0, 100000).Select(index => (byte)(index % 251)).ToArray();
        var text = Convert.ToBase64String(bytes);
        var original = new LandingShareData { Core = text,
            Blocks = new List<LandingShareBlock> { new LandingShareBlock { Type = FutureType, Version = 1, Data = text } } };
        var serializer = new DataContractJsonSerializer(typeof(LandingShareData));
        using var output = new MemoryStream();
        serializer.WriteObject(output, original);
        var stored = Encoding.UTF8.GetString(output.ToArray());
        Check(stored.Contains("\"coreChunks\"") && stored.Contains("\"dataChunks\"") &&
            !stored.Contains("\"core\":") && !stored.Contains("\"data\":"), "History must stream original bytes through chunk collections.");
        var restored = Decode<LandingShareData>(output.ToArray());
        Check(restored.Core == text && restored.Blocks[0].Data == text, "Chunked history changed original binary bytes.");
        var legacy = "{\"core\":\"" + text + "\",\"blocks\":[{\"type\":\"" + FutureType +
            "\",\"version\":1,\"required\":false,\"data\":\"" + text + "\"}]}";
        restored = Decode<LandingShareData>(Encoding.UTF8.GetBytes(legacy));
        Check(restored.Core == text && restored.Blocks[0].Data == text, "Earlier local detail payloads must remain readable.");
        foreach (var chunks in new[] { "[\"\"]", "[\"AQ==\",\"Ag==\"]", "[\"" + new string('A', 32772) + "\"]" })
        {
            var failed = false;
            try { Decode<LandingShareData>(Encoding.UTF8.GetBytes("{\"coreChunks\":" + chunks + ",\"blocks\":[]}")); }
            catch (Exception error) when (error is SerializationException || error is FormatException) { failed = true; }
            Check(failed, "Malformed chunk framing must not amplify retained history allocations.");
        }
    }

    private static void NativeFormatAndCanonicalOrder(Fixture fixture)
    {
        var contacts = ReadContacts(ReadContents(fixture.Share));
        Check(contacts.Select(contact => ReadCore(contact).SourceId).SequenceEqual(new[] { "source-a", "source-z" }),
            "Native sharing must order contacts by their source identity.");
        foreach (var contact in contacts)
            Check(contact.Blocks.Select(block => block.Type).SequenceEqual(contact.Blocks.Select(block => block.Type).OrderBy(type => type, StringComparer.Ordinal)),
                "Native sharing must order feature blocks by their ordinal type.");

        var repository = fixture.Repository("canonical-order");
        var original = Import(repository, fixture.Share, 2);
        contacts.Reverse();
        foreach (var contact in contacts) contact.Blocks.Reverse();
        var reordered = fixture.PathFor("reordered.lslanding");
        WriteShare(reordered, WriteContacts(contacts), contacts.Count);
        var duplicate = LandingShareArchive.Import(repository, new[] { reordered });
        Check(duplicate.Failures.Count == 0 && duplicate.Imported.Count == 0 && duplicate.DuplicateCount == 2 && repository.LoadAll().Count == 2,
            "Equivalent contact/block ordering must retain the same package identity.");
        var reexport = fixture.PathFor("canonical-order-export.lslanding");
        LandingShareArchive.Export(repository, original.Imported[0].Id, reexport);
        Check(ReadContents(reexport).SequenceEqual(ReadContents(fixture.Share)), "Re-export must publish canonical contents.");
    }

    private static void OpaqueDataSurvivesHistoryAndBackup(Fixture fixture)
    {
        var contacts = ReadContacts(ReadContents(fixture.Share));
        var opaque = Enumerable.Range(0, 49157).Select(index => (byte)((index * 73) % 256)).ToArray();
        foreach (var contact in contacts)
        {
            // Whitespace, unknown nested values, and long strings remain original bytes.
            var core = Encoding.UTF8.GetString(contact.Core);
            contact.Core = Encoding.UTF8.GetBytes(" \n{\"future\":{\"opaque\":[null,true,{\"label\":\"" + new string('x', 8193) +
                "\"}]}," + core.Substring(core.IndexOf('{') + 1) + "\t ");
            contact.Blocks.Add(new Block { Type = FutureType, Version = 73, Data = opaque });
            contact.Blocks.Add(new Block { Type = "org.example.empty-extension", Version = 1, Data = Array.Empty<byte>() });
            SortBlocks(contact);
        }
        var original = WriteContacts(contacts);
        var path = fixture.PathFor("opaque.lslanding");
        WriteShare(path, original, contacts.Count);
        var repository = fixture.Repository("opaque-history");
        var result = Import(repository, path, 2);
        Check(result.Imported.All(record => record.Series.Count == 3 && record.Engines.Count == 1 && record.ContactPoints.Count == 1),
            "Unknown optional features must not suppress supported landing data.");
        foreach (var record in result.Imported) AssertRetained(record, contacts.Single(contact => ReadCore(contact).SourceId == record.ImportedSourceId));
        Check(repository.LoadAll().All(record => record.ShareData == null), "History summaries must not retain the complete share payload.");

        repository = new LandingRepository(repository.RootPath);
        var summaries = repository.LoadAll();
        foreach (var summary in summaries)
        {
            Check(summary.ShareData == null, "Restart loaded raw sharing data into the summary index.");
            var detail = repository.LoadDetail(summary)!;
            AssertRetained(detail, contacts.Single(contact => ReadCore(contact).SourceId == detail.ImportedSourceId));
            // Local enrichment and recalculation must not rewrite the recorded exchange bytes.
            detail.Airport = "Resolved after import";
            detail.Runway = "27";
            detail.AirportDistanceNauticalMiles = 1.25;
            detail.InertialFpm = -999;
            detail.ImpactAnalysisVersion = 42;
            detail.ImpactPeakG = 2.5;
            detail.Series[0].GForce = 7;
            repository.Save(detail);
        }
        repository = new LandingRepository(repository.RootPath);
        var saved = fixture.PathFor("opaque-saved.lslanding");
        LandingShareArchive.Export(repository, result.Imported[0].Id, saved);
        Check(ReadContents(saved).SequenceEqual(original), "Save/restart or local metadata enrichment changed retained exchange bytes.");

        var restored = fixture.Repository("opaque-restored");
        foreach (var summary in repository.LoadAll()) restored.Import(repository.ExportForBackup(summary.Id), summary.Id);
        restored = new LandingRepository(restored.RootPath);
        foreach (var summary in restored.LoadAll())
            AssertRetained(restored.LoadDetail(summary)!, contacts.Single(contact => ReadCore(contact).SourceId == summary.ImportedSourceId));
        var restoredPath = fixture.PathFor("opaque-restored.lslanding");
        LandingShareArchive.Export(restored, result.Imported[0].Id, restoredPath);
        Check(ReadContents(restoredPath).SequenceEqual(original), "Backup/restore changed unknown core or feature bytes.");
        var duplicate = LandingShareArchive.Import(restored, new[] { path, saved, restoredPath });
        Check(duplicate.Failures.Count == 0 && duplicate.Imported.Count == 0 && duplicate.DuplicateCount == 6 && restored.LoadAll().Count == 2,
            "Round-trip copies of the same retained payload must not create duplicate landings.");
    }

    private static void KnownBlocksRemainIndependent(Fixture fixture)
    {
        var contacts = ReadContacts(ReadContents(fixture.Share));
        foreach (var contact in contacts)
            foreach (var type in new[] { "org.landingstats.metrics", "org.landingstats.series" })
            {
                var block = contact.Blocks.Single(value => value.Type == type);
                block.Version = int.MaxValue;
                block.Data = new byte[] { 0xff, 0, 0xfe, 1, 2 };
            }
        var contents = WriteContacts(contacts);
        var future = fixture.PathFor("future-known-versions.lslanding");
        WriteShare(future, contents, contacts.Count);
        var repository = fixture.Repository("future-known-versions");
        var imported = Import(repository, future, 2);
        foreach (var record in imported.Imported)
        {
            Check(record.InertialFpm == -100 && record.AircraftTitle == "Extensible share fixture" && double.IsNaN(record.WeightPounds),
                "An unsupported metrics version must retain the stable core and represent unavailable metrics as missing.");
            Check(record.Series.Count == 3 && double.IsNaN(record.Series[1].GForce) && record.Series[1].PitchDegrees == 3 &&
                record.Series[1].BankDegrees == 1 && record.Series[1].LatitudeDegrees == 42 && record.Series[1].RawControllerAxesPercent[0] == 12,
                "Supported pose and input blocks must remain usable without a supported general series block.");
            Check(record.Engines.Count == 1 && record.Engines[0].Points[1].ThrottlePercent == 40 &&
                record.ContactPoints.Count == 1 && record.ContactPoints[0].Points[1].CompressionPercent == 20 &&
                record.ControllerTimeline?.SampleCount == 3 && record.SourceEvidenceTimeline == fixture.First.SourceEvidenceTimeline,
                "A future metrics/series version must not suppress independent supported blocks.");
            AssertRetained(record, contacts.Single(contact => ReadCore(contact).SourceId == record.ImportedSourceId));
        }
        repository = new LandingRepository(repository.RootPath);
        var reexport = fixture.PathFor("future-known-versions-export.lslanding");
        LandingShareArchive.Export(repository, imported.Imported[0].Id, reexport);
        Check(ReadContents(reexport).SequenceEqual(contents), "Unsupported known-type versions must be preserved byte for byte.");

        foreach (var contact in contacts) contact.Blocks.Clear();
        var coreOnly = fixture.PathFor("core-only.lslanding");
        WriteShare(coreOnly, WriteContacts(contacts), contacts.Count);
        var minimal = Import(fixture.Repository("core-only"), coreOnly, 2);
        Check(minimal.Imported.All(record => record.InertialFpm == -100 && record.Series.Count == 0 && record.Engines.Count == 0 &&
            record.ContactPoints.Count == 0 && record.ControllerTimeline == null && double.IsNaN(record.WeightPounds)),
            "The stable core must be readable without any optional feature blocks.");
    }

    private static void OpaqueChangesAndSubsetsHaveSeparateIdentity(Fixture fixture)
    {
        var contacts = ReadContacts(ReadContents(fixture.Share));
        foreach (var contact in contacts)
        { contact.Blocks.Add(new Block { Type = FutureType, Version = 1, Data = new byte[] { 0, 1, 2, 3 } }); SortBlocks(contact); }
        var initialPath = fixture.PathFor("opaque-revision-initial.lslanding");
        WriteShare(initialPath, WriteContacts(contacts), contacts.Count);
        var repository = fixture.Repository("opaque-revisions");
        var original = Import(repository, initialPath, 2);
        contacts[0].Blocks.Single(block => block.Type == FutureType).Data[2] ^= 0xff;
        var revisedPath = fixture.PathFor("opaque-revision-changed.lslanding");
        WriteShare(revisedPath, WriteContacts(contacts), contacts.Count);
        var revised = Import(repository, revisedPath, 2);
        Check(revised.Imported[0].ImportedPackageHash != original.Imported[0].ImportedPackageHash &&
            revised.Imported[0].RecordingEpisodeId != original.Imported[0].RecordingEpisodeId && repository.LoadAll().Count == 4,
            "An opaque-byte change must produce a separate package revision and episode.");
        Check(original.Imported.All(first => revised.Imported.All(second => first.Id != second.Id)), "Revised opaque data reused a prior imported contact identity.");

        Check(repository.Delete(original.Imported[1].Id), "The subset fixture contact could not be deleted.");
        var subsetPath = fixture.PathFor("opaque-subset.lslanding");
        LandingShareArchive.Export(repository, original.Imported[0].Id, subsetPath);
        var subsetContents = ReadContacts(ReadContents(subsetPath));
        Check(subsetContents.Count == 1 && subsetContents[0].Blocks.Single(block => block.Type == FutureType).Data.SequenceEqual(new byte[] { 0, 1, 2, 3 }),
            "Exporting a surviving subset changed its retained feature bytes.");
        var subset = Import(repository, subsetPath, 1);
        Check(subset.Imported[0].ImportedPackageHash != original.Imported[0].ImportedPackageHash &&
            subset.Imported[0].RecordingEpisodeId != original.Imported[0].RecordingEpisodeId &&
            subset.Imported[0].RecordingEpisodeId != revised.Imported[0].RecordingEpisodeId,
            "A contact subset must have an independent package revision and episode.");
        var repeated = LandingShareArchive.Import(repository, new[] { subsetPath });
        Check(repeated.Failures.Count == 0 && repeated.DuplicateCount == 1 && repeated.Imported.Count == 0,
            "The subset's independent identity must remain idempotent.");
    }

    private static void InvalidFeaturesAndFramingAreAtomic(Fixture fixture)
    {
        var bytes = ReadContents(fixture.Share);
        var cases = new List<string>();
        void Add(string name, byte[] contents, int count = 2)
        { var path = fixture.PathFor("invalid-" + name + ".lslanding"); WriteShare(path, contents, count); cases.Add(path); }
        void Mutate(string name, Action<List<Contact>> mutation)
        { var contacts = ReadContacts(bytes); mutation(contacts); Add(name, WriteContacts(contacts)); }
        // The first contact stays valid: validation must finish before publishing any contact.
        Mutate("required-unknown", contacts => contacts[1].Blocks.Add(new Block { Type = FutureType, Version = 1, Required = true }));
        Mutate("required-future-version", contacts => { var block = contacts[1].Blocks.Single(value => value.Type == "org.landingstats.series"); block.Version++; block.Required = true; });
        Mutate("duplicate-type", contacts => { var block = contacts[1].Blocks[0]; contacts[1].Blocks.Add(new Block { Type = block.Type, Version = block.Version + 1 }); });
        Mutate("empty-type", contacts => contacts[1].Blocks.Add(new Block { Type = "", Version = 1 }));
        Mutate("zero-version", contacts => contacts[1].Blocks.Add(new Block { Type = FutureType, Version = 0 }));
        Mutate("negative-version", contacts => contacts[1].Blocks.Add(new Block { Type = FutureType, Version = -1 }));
        Mutate("invalid-utf8-core", contacts => contacts[1].Core = new byte[] { 0xff, 0xfe, 0 });
        Mutate("incomplete-core", contacts => contacts[1].Core = Encoding.UTF8.GetBytes("{}"));
        Mutate("trailing-core", contacts => contacts[1].Core = contacts[1].Core.Concat(Encoding.UTF8.GetBytes(" {} ")).ToArray());
        Mutate("duplicate-source", contacts => contacts[1].Core = contacts[0].Core);
        foreach (var type in ReadContacts(bytes)[1].Blocks.Select(block => block.Type))
        {
            var feature = type.Substring(type.LastIndexOf('.') + 1);
            Mutate("malformed-" + feature, contacts => contacts[1].Blocks.Single(block => block.Type == type).Data = new byte[] { 0xff, 0, 0xfe });
            if (feature != "metrics")
                Mutate("incomplete-" + feature, contacts => contacts[1].Blocks.Single(block => block.Type == type).Data = Encoding.UTF8.GetBytes("{}"));
        }

        Add("wrong-magic", Replace(bytes, 0, new byte[] { (byte)'X' }));
        Add("negative-contact-count", ReplaceInt32(bytes, 8, -1));
        Add("unbounded-contact-count", ReplaceInt32(bytes, 8, int.MaxValue));
        Add("manifest-contact-count", bytes, 3);
        Add("negative-core-length", ReplaceInt32(bytes, 12, -1));
        Add("unbounded-core-length", ReplaceInt32(bytes, 12, int.MaxValue));
        var frames = ReadContacts(bytes);
        var last = frames[1];
        var firstBlock = last.Blocks[0];
        Add("negative-block-count", ReplaceInt32(bytes, last.BlockCountOffset, -1));
        Add("unbounded-block-count", ReplaceInt32(bytes, last.BlockCountOffset, int.MaxValue));
        Add("negative-type-length", ReplaceInt32(bytes, firstBlock.TypeLengthOffset, -1));
        Add("unbounded-type-length", ReplaceInt32(bytes, firstBlock.TypeLengthOffset, int.MaxValue));
        Add("invalid-utf8-type", Replace(bytes, firstBlock.TypeLengthOffset + 4, new byte[] { 0xff }));
        Add("invalid-required-flag", Replace(bytes, firstBlock.RequiredOffset, new byte[] { 2 }));
        Add("negative-data-length", ReplaceInt32(bytes, firstBlock.DataLengthOffset, -1));
        Add("unbounded-data-length", ReplaceInt32(bytes, firstBlock.DataLengthOffset, int.MaxValue));
        Add("truncated-data", bytes.Take(bytes.Length - 1).ToArray());
        Add("trailing-contents", bytes.Concat(new byte[] { 0 }).ToArray());
        Add("truncated-header", bytes.Take(11).ToArray());

        var oversizedPath = fixture.PathFor("invalid-expanded-limit.lslanding");
        var zip = File.ReadAllBytes(fixture.Share);
        var central = FindSignature(zip, 0x02014b50);
        zip = Replace(zip, central + 24, BitConverter.GetBytes((uint)(LandingShareArchive.MaximumDecodedBytes + 1)));
        File.WriteAllBytes(oversizedPath, zip);
        cases.Add(oversizedPath);

        var repository = fixture.Repository("invalid-atomic");
        var existing = Fixture.Record(1, 1, "unrelated", "existing-owner");
        repository.Save(existing);
        var original = repository.Export(existing.Id);
        foreach (var path in cases)
        {
            var rejected = LandingShareArchive.Import(repository, new[] { path });
            Check(rejected.Failures.Count == 1 && rejected.Imported.Count == 0 && rejected.DuplicateCount == 0,
                "Malformed extension/framing was accepted: " + Path.GetFileName(path));
            Check(repository.LoadAll().Count == 1 && repository.Export(existing.Id).SequenceEqual(original),
                "Rejected package partially changed history: " + Path.GetFileName(path));
        }
        Check(new LandingRepository(repository.RootPath).LoadAll().Single().Id == existing.Id, "A rejected package left imported history after restart.");
    }

    private static void ExportRejectsInvalidRetainedData(Fixture fixture)
    {
        var repository = fixture.Repository("invalid-export");
        var imported = Import(repository, fixture.Share, 2);
        var detail = repository.LoadDetail(repository.LoadAll().Single(record => record.Id == imported.Imported[0].Id))!;
        detail.ShareData!.Blocks.Add(new LandingShareBlock { Type = FutureType, Version = 1, Required = true, Data = "" });
        repository.Save(detail);
        var destination = fixture.PathFor("invalid-export-existing.lslanding");
        var before = new byte[] { 3, 1, 4, 1, 5 };
        File.WriteAllBytes(destination, before);
        var failed = false;
        try { LandingShareArchive.Export(repository, detail.Id, destination); }
        catch (InvalidDataException) { failed = true; }
        Check(failed && File.ReadAllBytes(destination).SequenceEqual(before), "Invalid retained features must fail export without damaging the destination.");
    }

    private static LandingShareImportResult Import(LandingRepository repository, string path, int count)
    {
        var result = LandingShareArchive.Import(repository, new[] { path });
        Check(result.Failures.Count == 0 && result.Imported.Count == count && result.DuplicateCount == 0,
            "Share import failed: " + Path.GetFileName(path) + "; " + string.Join("; ", result.Failures.Select(failure => failure.Message)));
        return result;
    }

    private static void AssertRetained(LandingRecord record, Contact expected)
    {
        var data = record.ShareData;
        Check(data != null && Convert.FromBase64String(data.Core).SequenceEqual(expected.Core), "The original core bytes were lost.");
        Check(data!.Blocks.Count == expected.Blocks.Count, "Retained feature count changed.");
        foreach (var block in expected.Blocks)
        {
            var actual = data.Blocks.Single(value => value.Type == block.Type);
            Check(actual.Version == block.Version && actual.Required == block.Required && Convert.FromBase64String(actual.Data).SequenceEqual(block.Data),
                "Original feature bytes or descriptor changed: " + block.Type);
        }
    }

    private static byte[] ReadContents(string path)
    {
        using var input = File.OpenRead(path);
        using var zip = new ZipArchive(input);
        Check(zip.Entries.Count == 2 && zip.GetEntry("manifest.json") != null && zip.GetEntry("contents.bin") != null,
            "Native exports must use the manifest/contents container.");
        byte[] Read(string name)
        { using var stream = zip.GetEntry(name)!.Open(); using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray(); }
        var bytes = Read("contents.bin");
        var manifest = Decode<Manifest>(Read("manifest.json"));
        Check(manifest.Version == 1 && manifest.Format == "MSFS Landing Stats landing" && manifest.Contacts == ReadContacts(bytes).Count && manifest.Sha256 == Hash(bytes),
            "Native manifest does not describe its exact binary contents.");
        return bytes;
    }

    private static void WriteShare(string path, byte[] contents, int count)
    {
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        var manifest = "{\"format\":\"MSFS Landing Stats landing\",\"version\":1,\"contacts\":" + count.ToString(CultureInfo.InvariantCulture) +
            ",\"sha256\":\"" + Hash(contents) + "\"}";
        void Entry(string name, byte[] bytes)
        { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes, 0, bytes.Length); }
        Entry("manifest.json", Encoding.UTF8.GetBytes(manifest));
        Entry("contents.bin", contents);
    }

    // Independent wire fixture helpers exercise the public boundary, not the production decoder.
    private static List<Contact> ReadContacts(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        Check(reader.ReadBytes(Magic.Length).SequenceEqual(Magic), "Invalid synthetic share header.");
        var contacts = new List<Contact>();
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var contact = new Contact { Core = reader.ReadBytes(reader.ReadInt32()), BlockCountOffset = checked((int)input.Position) };
            var blockCount = reader.ReadInt32();
            for (var j = 0; j < blockCount; j++)
            {
                var block = new Block { TypeLengthOffset = checked((int)input.Position) };
                block.Type = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                block.Version = reader.ReadInt32();
                block.RequiredOffset = checked((int)input.Position);
                block.Required = reader.ReadByte() != 0;
                block.DataLengthOffset = checked((int)input.Position);
                block.Data = reader.ReadBytes(reader.ReadInt32());
                contact.Blocks.Add(block);
            }
            contacts.Add(contact);
        }
        Check(input.Position == input.Length, "Synthetic share fixture has trailing data.");
        return contacts;
    }

    private static byte[] WriteContacts(IReadOnlyList<Contact> contacts)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(Magic);
        writer.Write(contacts.Count);
        foreach (var contact in contacts)
        {
            writer.Write(contact.Core.Length); writer.Write(contact.Core); writer.Write(contact.Blocks.Count);
            foreach (var block in contact.Blocks)
            {
                var type = Encoding.UTF8.GetBytes(block.Type);
                writer.Write(type.Length); writer.Write(type); writer.Write(block.Version); writer.Write((byte)(block.Required ? 1 : 0));
                writer.Write(block.Data.Length); writer.Write(block.Data);
            }
        }
        writer.Flush();
        return output.ToArray();
    }

    private static void SortBlocks(Contact contact) => contact.Blocks = contact.Blocks.OrderBy(block => block.Type, StringComparer.Ordinal).ToList();
    private static CoreIdentity ReadCore(Contact contact) => Decode<CoreIdentity>(contact.Core);
    private static T Decode<T>(byte[] data)
    { using var input = new MemoryStream(data); return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(input)!; }
    private static string Hash(byte[] data)
    { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
    private static byte[] ReplaceInt32(byte[] source, int offset, int value) => Replace(source, offset, BitConverter.GetBytes(value));
    private static byte[] Replace(byte[] source, int offset, byte[] value)
    { var copy = (byte[])source.Clone(); Array.Copy(value, 0, copy, offset, value.Length); return copy; }
    private static int FindSignature(byte[] bytes, uint value)
    { for (var i = 0; i <= bytes.Length - 4; i++) if (BitConverter.ToUInt32(bytes, i) == value) return i; throw new InvalidOperationException("ZIP signature is missing."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Contact
    { internal byte[] Core = Array.Empty<byte>(); internal List<Block> Blocks = new List<Block>(); internal int BlockCountOffset; }
    private sealed class Block
    {
        internal string Type = "";
        internal int Version = 1;
        internal bool Required;
        internal byte[] Data = Array.Empty<byte>();
        internal int TypeLengthOffset, RequiredOffset, DataLengthOffset;
    }
    [DataContract] private sealed class CoreIdentity
    { [DataMember(Name = "sourceId")] public string SourceId = ""; }
    [DataContract] private sealed class Manifest
    {
        [DataMember(Name = "format")] public string Format = "";
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "contacts")] public int Contacts { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 = "";
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "landing-share-extension-test-" + Guid.NewGuid().ToString("N"));
        internal LandingRecord First { get; }
        internal string Share { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var source = Repository("source");
            First = Record(1, 2, "source-episode", "source-z");
            source.Save(First);
            var second = Record(2, 2, "source-episode", "source-a");
            source.Save(second);
            Share = PathFor("native.lslanding");
            LandingShareArchive.Export(source, second.Id, Share);
        }
        internal string PathFor(string name) => Path.Combine(_root, name);
        internal LandingRepository Repository(string name) => new LandingRepository(PathFor(name));
        public void Dispose() => Directory.Delete(_root, true);

        internal static LandingRecord Record(int number, int count, string episode, string id)
        {
            var record = new LandingRecord
            {
                Id = id, TimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), ContactNumber = number, ContactCount = count,
                RecordingEpisodeId = episode, InertialFpm = -100, SurfaceFpm = double.NaN, Simulator = "MSFS 2024",
                AircraftTitle = "Extensible share fixture", AircraftType = "A320", ControllerAxisCount = 8,
                RawControllerAxesSourceIndices = new List<int> { 0 }, ControlInputSources = new List<string> { "Recorded stick" },
                WeightPounds = 123456,
            };
            var engine = new LandingEngineSeries { EngineNumber = 1 }; record.Engines.Add(engine);
            var contact = new LandingContactSeries { ContactPointIndex = 2 }; record.ContactPoints.Add(contact);
            var raw = new List<TelemetrySample>();
            for (var i = 0; i < 3; i++)
            {
                var time = (i - 1) * .1;
                record.Series.Add(new LandingSeriesPoint
                {
                    TimeSeconds = time, InertialFpm = -100 + i, GForce = 1 + i * .1, PitchDegrees = 3, BankDegrees = 1,
                    LatitudeDegrees = 42, LongitudeDegrees = 23 + i * .0001, PlaneAltitudeFeet = 1650, GroundAltitudeFeet = 1640, HeadingTrueDegrees = 90,
                    RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(), RawControllerYAxisAgeSeconds = Array.Empty<double>(),
                    RawControllerAxesPercent = new[] { 10.0 + i * 2 }, RawControllerAxesValid = new[] { true }, RawControllerAxesAgeSeconds = new[] { .01 },
                });
                engine.Points.Add(new LandingEnginePoint { TimeSeconds = time, ThrottlePercent = 40, N1Percent = 50, Rpm = 1000 });
                contact.Points.Add(new LandingContactPoint { TimeSeconds = time, CompressionPercent = 20, PositionPercent = 100, OnGround = i > 0 });
                raw.Add(new TelemetrySample
                {
                    Sequence = i, SimulationTimeSeconds = time + 100,
                    SourceEvidence = new ReplaySourceEvidence(Guid.Parse("11111111-1111-1111-1111-111111111111"), 1, 1, i, i, 10,
                        SdkReplayState.Inactive, new ReplaySourceObservation(SourceObservationAvailability.Available, SourceObservationReason.None, 0, 0, 1)),
                });
            }
            record.SourceEvidenceTimeline = ReplaySourceTimelineCodec.EncodeText(raw);
            var device = new ControllerDescriptor(0, 0, "Recorded stick", "product", 255, new uint[8], Enumerable.Repeat(65535u, 8).ToArray(),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("33333333-3333-3333-3333-333333333333"));
            var catalog = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers), 0, 1, 100,
                new ControllerCatalogMessage(1, new[] { device }, new[] { new ControllerBinding(ControllerControlKind.Pitch, -1, device.InstanceId, 0, false, "MSFS profile") })));
            record.ControllerTimeline = new ControllerCatalogTimeline
            { SampleCount = 3, Catalogs = new[] { Convert.ToBase64String(catalog) }, Transitions = new[] { new[] { 0, 0 } } };
            return record;
        }
    }
}
