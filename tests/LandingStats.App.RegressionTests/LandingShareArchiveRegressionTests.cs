using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class LandingShareArchiveRegressionTests
{
    internal static void RunAll()
    {
        using var fixture = new Fixture();
        RoundTripAndProvenance(fixture);
        SharingPreservesMeasurementProvenance(fixture);
        LegacyRawControllerColumns(fixture);
        DeleteAndReimport(fixture);
        RevisedAndPartialEpisodes(fixture);
        RejectUntrustedFiles(fixture);
        BatchRetainsValidFiles(fixture);
        FailedExportPreservesDestination(fixture);
    }

    private static void RoundTripAndProvenance(Fixture fixture)
    {
        var repository = fixture.Repository("roundtrip");
        repository.Save(fixture.First); // Identical source ID must never replace the owner's landing.
        var ownerBytes = repository.Export(fixture.First.Id);
        var result = LandingShareArchive.Import(repository, new[] { fixture.Share });
        Check(result.Failures.Count == 0 && result.Imported.Count == 2 && result.DuplicateCount == 0, "Episode import failed.");
        Check(result.PreferredRecordId == result.Imported[0].Id, "Import must return a selectable landing.");
        Check(repository.Export(fixture.First.Id).SequenceEqual(ownerBytes), "Sharing replaced an own landing.");
        Check(repository.LoadAll().Count == 3 && !repository.LoadAll().Single(record => record.Id == fixture.First.Id).IsImported,
            "Own/imported history provenance differs.");

        var first = result.Imported.Single(record => record.ContactNumber == 1);
        Check(first.IsImported && first.ImportedSourceId == fixture.First.Id && first.ImportedSourceEpisodeId == fixture.First.RecordingEpisodeId &&
            first.Id != fixture.First.Id && first.ImportedPackageHash?.Length == 64, "Imported identity/provenance was not retained.");
        Check(LandingReplayContacts.ForEpisode(first, repository.LoadAll()).Count == 2, "Imported contacts did not retain their episode.");
        Check(first.Series.Count == fixture.First.Series.Count && first.Engines[0].Points[1].ThrottlePercent == 40 &&
            first.ContactPoints[0].Points[1].CompressionPercent == 20 && double.IsNaN(first.SurfaceFpm), "Chart samples changed during sharing.");
        Check(first.RunwayGeometry?.LengthMeters == 3000 && first.AnalysisGeometry?.InputSha256 == fixture.First.AnalysisGeometry?.InputSha256 &&
            first.SourceEvidenceTimeline == fixture.First.SourceEvidenceTimeline, "Recorded geometry or source evidence was lost.");
        Check(LandingReplayTrack.TryCreate(first, out var track) && track!.Frames.Count == 3, "Imported recorded positions cannot replay independently.");
        var inputs = ReplayControlInputs.Resolve(first);
        Check(inputs.Pitch?.IsExplicitBinding == true && inputs.Pitch.SourceIndex == 0, "Recorded controller assignments were lost.");
        Check(first.ControllerTimeline!.Catalogs.SequenceEqual(fixture.First.ControllerTimeline!.Catalogs) &&
            first.Series[1].RawControllerAxesPercent[0] == 12, "Controller provenance or physical columns changed.");

        var duplicate = LandingShareArchive.Import(repository, new[] { fixture.Share });
        Check(duplicate.Failures.Count == 0 && duplicate.Imported.Count == 0 && duplicate.DuplicateCount == 2 &&
            duplicate.PreferredRecordId != null, "Repeated share import was not idempotent/selectable.");
        var reexport = fixture.PathFor("reexport.lslanding");
        LandingShareArchive.Export(repository, first.Id, reexport);
        Check(ReadPayload(reexport).SequenceEqual(ReadPayload(fixture.Share)), "Re-export changed canonical contact contents.");
        duplicate = LandingShareArchive.Import(repository, new[] { reexport });
        Check(duplicate.Imported.Count == 0 && duplicate.DuplicateCount == 2 && duplicate.Failures.Count == 0, "Re-export created nested imported copies.");

        var reopened = new LandingRepository(repository.RootPath);
        var summary = reopened.LoadAll().Single(record => record.Id == first.Id);
        Check(summary.IsImported && summary.ImportedPackageHash == first.ImportedPackageHash && summary.ImportedSourceId == first.ImportedSourceId,
            "The history index lost import provenance on restart.");
        var backup = fixture.Repository("backup");
        var restored = backup.Import(reopened.ExportForBackup(first.Id), first.Id);
        Check(restored.IsImported && restored.ImportedSourceEpisodeId == first.ImportedSourceEpisodeId && restored.ImportedPackageHash == first.ImportedPackageHash,
            "Existing backup serialization lost sharing provenance.");
    }

    private static void SharingPreservesMeasurementProvenance(Fixture fixture)
    {
        foreach (var version in Enumerable.Range(1, LandingRecord.CurrentFormatVersion))
        {
            var source = fixture.Repository("source-v" + version);
            var record = Fixture.Record(1, 1, "source-v" + version);
            record.FormatVersion = version;
            record.InertialExtrapolated = version >= 4;
            record.InertialFitDurationSeconds = .2;
            record.LatchUpdateDetected = version >= 4;
            record.LatchUpdateOffsetSeconds = .05;
            source.Save(record);
            var before = new LandingPresentation(record);
            var path = fixture.PathFor("source-v" + version + ".lslanding");
            LandingShareArchive.Export(source, record.Id, path);
            var destination = fixture.Repository("import-v" + version);
            var result = LandingShareArchive.Import(destination, new[] { path });
            Check(result.Failures.Count == 0 && result.Imported.Count == 1, "Supported source version could not be imported.");
            var imported = result.Imported.Single();
            var after = new LandingPresentation(imported);
            Check(imported.FormatVersion == version && imported.InertialFpm.Equals(record.InertialFpm) &&
                imported.SurfaceFpm.Equals(record.SurfaceFpm) && imported.InertialExtrapolated == record.InertialExtrapolated &&
                imported.InertialFitDurationSeconds == record.InertialFitDurationSeconds &&
                imported.LatchUpdateDetected == record.LatchUpdateDetected && imported.LatchUpdateOffsetSeconds == record.LatchUpdateOffsetSeconds &&
                after.InertialQualityDisplay == before.InertialQualityDisplay && after.SurfaceQualityDisplay == before.SurfaceQualityDisplay,
                "Sharing changed measurement values or their recorded quality semantics.");
            var reopened = new LandingRepository(destination.RootPath);
            Check(reopened.LoadAll().Single().FormatVersion == version,
                "The imported summary lost its source version after reopening history.");
            var reexport = fixture.PathFor("reexport-v" + version + ".lslanding");
            LandingShareArchive.Export(reopened, imported.Id, reexport);
            Check(ReadPayload(path).SequenceEqual(ReadPayload(reexport)), "Source provenance changed canonical re-export identity.");
            var duplicate = LandingShareArchive.Import(reopened, new[] { reexport });
            Check(duplicate.Failures.Count == 0 && duplicate.Imported.Count == 0 && duplicate.DuplicateCount == 1,
                "Re-export of a supported older source created a duplicate contact.");
        }
    }

    private static void DeleteAndReimport(Fixture fixture)
    {
        var repository = fixture.Repository("delete");
        var first = LandingShareArchive.Import(repository, new[] { fixture.Share }).Imported[0];
        Check(repository.Delete(first.Id), "Fixture contact deletion failed.");
        repository = new LandingRepository(repository.RootPath);
        var restored = LandingShareArchive.Import(repository, new[] { fixture.Share });
        Check(restored.Failures.Count == 0 && restored.Imported.Count == 1 && restored.DuplicateCount == 1 &&
            restored.Imported[0].Id != first.Id && repository.IsDeleted(first.Id), "Explicit reimport did not preserve the deletion tombstone.");
        Check(LandingReplayContacts.ForEpisode(restored.Imported[0], repository.LoadAll()).Count == 2, "Restored contact detached from its surviving episode.");
        repository = new LandingRepository(repository.RootPath);
        var repeated = LandingShareArchive.Import(repository, new[] { fixture.Share });
        Check(repeated.Failures.Count == 0 && repeated.Imported.Count == 0 && repeated.DuplicateCount == 2, "Restored provenance was not persistent/idempotent.");
    }

    private static void LegacyRawControllerColumns(Fixture fixture)
    {
        LandingRecord Record(int version, int width = 8)
        {
            var record = Fixture.Record(1, 1, "legacy-raw-v" + version);
            record.FormatVersion = version;
            record.RawPitchInputSourceIndex = Math.Max(0, width - 1);
            record.RawPitchInputCorrelation = .9;
            foreach (var point in record.Series)
            {
                point.RawControllerYAxisPercent = Enumerable.Range(0, width).Select(index => 10.0 + index + point.TimeSeconds).ToArray();
                point.RawControllerYAxisValid = Enumerable.Range(0, width).Select(index => index != 1).ToArray();
                point.RawControllerYAxisAgeSeconds = Enumerable.Range(0, width).Select(index => .01 * index).ToArray();
            }
            return record;
        }

        foreach (var version in Enumerable.Range(1, 6))
        {
            var original = Record(version);
            var normalized = LandingRecordFile.FromRecord(original).ToRecord();
            Check(original.RawControllerSourceIndices.Count == 0 && normalized.RawControllerSourceIndices.SequenceEqual(Enumerable.Range(0, 8)),
                "Legacy direct source indices are normalized in a detached columnar copy without changing the source record.");
            var source = fixture.Repository("legacy-raw-source-v" + version); source.Save(original);
            var package = fixture.PathFor("legacy-raw-v" + version + ".lslanding");
            LandingShareArchive.Export(source, original.Id, package);
            var destination = fixture.Repository("legacy-raw-import-v" + version);
            var result = LandingShareArchive.Import(destination, new[] { package });
            Check(result.Failures.Count == 0 && result.Imported.Count == 1, "Legacy nonempty raw-controller columns must export and import.");
            var imported = result.Imported.Single();
            Check(imported.FormatVersion == version && imported.RawControllerSourceIndices.SequenceEqual(Enumerable.Range(0, 8)) &&
                imported.StoredRawControllerIndex(7) == original.StoredRawControllerIndex(7),
                "Legacy source version and raw controller addressing changed during sharing.");
            for (var index = 0; index < original.Series.Count; index++)
                Check(imported.Series[index].RawControllerYAxisPercent.SequenceEqual(original.Series[index].RawControllerYAxisPercent) &&
                    imported.Series[index].RawControllerYAxisValid.SequenceEqual(original.Series[index].RawControllerYAxisValid) &&
                    imported.Series[index].RawControllerYAxisAgeSeconds.SequenceEqual(original.Series[index].RawControllerYAxisAgeSeconds),
                    "Legacy raw-controller values, validity, or ages were lost.");
            var reopened = new LandingRepository(destination.RootPath);
            var reexport = fixture.PathFor("legacy-raw-reexport-v" + version + ".lslanding");
            LandingShareArchive.Export(reopened, imported.Id, reexport);
            Check(ReadPayload(package).SequenceEqual(ReadPayload(reexport)), "Legacy raw-controller contents changed on canonical re-export.");
        }

        Check(LandingRecordFile.FromRecord(Record(6, 3)).Summary.RawControllerSourceIndices.SequenceEqual(new[] { 0, 1, 2 }) &&
            LandingRecordFile.FromRecord(Record(6, 0)).ToRecord().Series.All(point => point.RawControllerYAxisPercent.Length == 0),
            "Legacy normalization derives its width and preserves genuinely empty raw columns.");
        var explicitMapping = Record(6, 3); explicitMapping.RawControllerSourceIndices = new List<int> { 7, 4, 2 };
        Check(LandingRecordFile.FromRecord(explicitMapping).Summary.RawControllerSourceIndices.SequenceEqual(explicitMapping.RawControllerSourceIndices),
            "An explicit legacy source mapping must remain authoritative.");
        var inconsistentRows = Record(6); inconsistentRows.Series[1].RawControllerYAxisPercent = new[] { 1.0 };
        var inconsistentValidity = Record(6); inconsistentValidity.Series[1].RawControllerYAxisValid = Array.Empty<bool>();
        var inconsistentAges = Record(6); inconsistentAges.Series[1].RawControllerYAxisAgeSeconds = Array.Empty<double>();
        var inconsistentMapping = Record(6); inconsistentMapping.RawControllerSourceIndices = new List<int> { 0 };
        foreach (var invalid in new[] { inconsistentRows, inconsistentValidity, inconsistentAges, inconsistentMapping, Record(7) })
        {
            var rejected = false;
            try { LandingRecordFile.FromRecord(invalid); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Malformed row widths and undeclared modern mappings must fail instead of being inferred or truncated.");
        }
    }

    private static void RevisedAndPartialEpisodes(Fixture fixture)
    {
        var repository = fixture.Repository("revisions");
        var original = LandingShareArchive.Import(repository, new[] { fixture.Share }).Imported;
        var changed = ReadContacts(ReadPayload(fixture.Share));
        ChangeCore(changed[0], core => core.Airport = "Changed airport");
        var revisedFile = fixture.PathFor("revision.lslanding");
        WriteShare(revisedFile, WriteContacts(changed), 2);
        var revised = LandingShareArchive.Import(repository, new[] { revisedFile });
        Check(revised.Failures.Count == 0 && revised.Imported.Count == 2 && revised.Imported[0].RecordingEpisodeId != original[0].RecordingEpisodeId,
            "A changed shared episode reused a previous revision identity.");
        Check(LandingReplayContacts.ForEpisode(original[0], repository.LoadAll()).Count == 2 &&
            LandingReplayContacts.ForEpisode(revised.Imported[0], repository.LoadAll()).Count == 2, "Episode revisions have ambiguous replay contacts.");
        Check(repository.Delete(original[1].Id), "Partial fixture deletion failed.");
        var partialFile = fixture.PathFor("partial.lslanding");
        LandingShareArchive.Export(repository, original[0].Id, partialFile);
        var partial = LandingShareArchive.Import(repository, new[] { partialFile });
        Check(partial.Failures.Count == 0 && partial.Imported.Count == 1 && partial.Imported[0].RecordingEpisodeId != original[0].RecordingEpisodeId,
            "A subset share was mixed with a different package revision.");
    }

    private static void RejectUntrustedFiles(Fixture fixture)
    {
        var payload = ReadPayload(fixture.Share);
        var cases = new List<string>();
        string Case(string name) { var path = fixture.PathFor(name + ".lslanding"); cases.Add(path); return path; }
        void PayloadCase(string name, Action<List<LandingShareData>> mutate)
        { var value = ReadContacts(payload); mutate(value); WriteShare(Case(name), WriteContacts(value), 2); }
        File.WriteAllText(Case("plain"), "not an archive");
        var bytes = File.ReadAllBytes(fixture.Share); File.WriteAllBytes(Case("truncated"), bytes.Take(bytes.Length - 8).ToArray());
        WriteShare(Case("unpublished-json-container"), Encoding.UTF8.GetBytes("{\"contacts\":[]}"), 2, contentsName: "landings.json");
        WriteShare(Case("version-0"), payload, 2, version: 0);
        WriteShare(Case("future"), payload, 2, version: 2);
        WriteShare(Case("count"), payload, 3);
        WriteShare(Case("checksum"), payload, 2, checksum: new string('0', 64));
        WriteShare(Case("path"), payload, 2, extra: "../outside.json");
        WriteShare(Case("extra"), payload, 2, extra: "extra.json");
        WriteShare(Case("duplicate"), payload, 2, extra: "contents.bin");
        WriteShare(Case("trailing"), payload.Concat(Encoding.UTF8.GetBytes(" {} ")).ToArray(), 2);
        WriteShare(Case("empty"), WriteContacts(Array.Empty<LandingShareData>()), 0);
        PayloadCase("future-core", value => ChangeCore(value[0], core => core.Version = 2));
        PayloadCase("future-record", value => ChangeBlock<LandingRecord>(value[0], "metrics", record => record.FormatVersion = LandingRecord.CurrentFormatVersion + 1));
        PayloadCase("missing-record-version", value => ChangeBlock<LandingRecord>(value[0], "metrics", record => record.FormatVersion = 0));
        PayloadCase("negative-record-version", value => ChangeBlock<LandingRecord>(value[0], "metrics", record => record.FormatVersion = -1));
        PayloadCase("missing-metrics", value => value[0].Blocks.Single(block => block.Type == "org.landingstats.metrics").Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("null")));
        PayloadCase("missing-series", value => ChangeBlock<SeriesFeature>(value[0], "series", series => series.Columns = null!));
        PayloadCase("null-engine", value => ChangeBlock<EnginesFeature>(value[0], "engines", engines => engines.Columns = new LandingEngineColumns[] { null! }));
        PayloadCase("missing-core", value => value[0].Core = Convert.ToBase64String(Encoding.UTF8.GetBytes("null")));
        PayloadCase("bad-column", value => ChangeBlock<SeriesFeature>(value[0], "series", series => series.Columns.Time = new[] { 0.0 }));
        PayloadCase("duplicate-id", value => ChangeCore(value[1], core => core.SourceId = Decode<CoreIdentity>(Convert.FromBase64String(value[0].Core)).SourceId));
        PayloadCase("mixed-episode", value => ChangeCore(value[1], core => core.EpisodeId = "another-episode"));
        PayloadCase("duplicate-number", value => { foreach (var contact in value) ChangeCore(contact, core => core.ContactNumber = 1); });
        // Reject a forged expansion size before attempting any decompression.
        var oversized = (byte[])bytes.Clone(); var central = FindSignature(oversized, 0x02014b50);
        Array.Copy(BitConverter.GetBytes((uint)(LandingShareArchive.MaximumDecodedBytes + 1)), 0, oversized, central + 24, 4);
        File.WriteAllBytes(Case("oversized"), oversized);
        var wrongExtension = fixture.PathFor("wrong.zip"); File.Copy(fixture.Share, wrongExtension); cases.Add(wrongExtension);
        var repository = fixture.Repository("untrusted");
        var result = LandingShareArchive.Import(repository, cases);
        Check(result.Imported.Count == 0 && result.Failures.Count == cases.Count && repository.LoadAll().Count == 0,
            "Malformed input escaped validation: " + string.Join(", ", cases.Except(result.Failures.Select(failure => failure.Path))));
    }

    private static void BatchRetainsValidFiles(Fixture fixture)
    {
        var otherRepository = fixture.Repository("other-source");
        var other = Fixture.Record(1, 1, "other-episode"); otherRepository.Save(other);
        var otherFile = fixture.PathFor("other.lslanding"); LandingShareArchive.Export(otherRepository, other.Id, otherFile);
        var badFile = fixture.PathFor("batch-bad.lslanding"); File.WriteAllText(badFile, "bad");
        var repository = fixture.Repository("batch");
        var result = LandingShareArchive.Import(repository, new[] { fixture.Share, badFile, otherFile, fixture.Share });
        Check(result.Imported.Count == 3 && result.Failures.Count == 1 && result.Failures[0].Path == badFile && result.DuplicateCount == 2 &&
            new LandingRepository(repository.RootPath).LoadAll().Count == 3, "Batch import did not retain valid files around a failed file.");
    }

    private static void FailedExportPreservesDestination(Fixture fixture)
    {
        var path = fixture.PathFor("existing.lslanding"); var before = new byte[] { 3, 1, 4 }; File.WriteAllBytes(path, before);
        var failed = false;
        try { LandingShareArchive.Export(fixture.Source, "missing", path); }
        catch (FileNotFoundException) { failed = true; }
        Check(failed && File.ReadAllBytes(path).SequenceEqual(before), "A failed export damaged an existing destination.");
        // Successful replacement publishes the complete ZIP over an existing file.
        LandingShareArchive.Export(fixture.Source, fixture.First.Id, path);
        Check(ReadPayload(path).SequenceEqual(ReadPayload(fixture.Share)), "Atomic export replacement changed its contents.");

        var repository = fixture.Repository("missing-share-data");
        var imported = LandingShareArchive.Import(repository, new[] { fixture.Share }).Imported[0];
        imported.ShareData = null;
        repository.Save(imported);
        File.WriteAllBytes(path, before);
        failed = false;
        try { LandingShareArchive.Export(repository, imported.Id, path); }
        catch (InvalidDataException) { failed = true; }
        Check(failed && File.ReadAllBytes(path).SequenceEqual(before), "An imported contact without retained share contents must fail export without damaging its destination.");
    }

    private static byte[] ReadPayload(string path)
    { using var input = File.OpenRead(path); using var zip = new ZipArchive(input); using var entry = zip.GetEntry("contents.bin")!.Open(); using var data = new MemoryStream(); entry.CopyTo(data); return data.ToArray(); }
    private static void WriteShare(string path, byte[] payload, int count, int version = 1, string? checksum = null, string? extra = null, string contentsName = "contents.bin")
    {
        using var input = File.Create(path); using var zip = new ZipArchive(input, ZipArchiveMode.Create);
        using var hash = SHA256.Create();
        var manifest = "{\"format\":\"MSFS Landing Stats landing\",\"version\":" + version + ",\"contacts\":" + count + ",\"sha256\":\"" +
            (checksum ?? BitConverter.ToString(hash.ComputeHash(payload)).Replace("-", "").ToLowerInvariant()) + "\"}";
        void Entry(string name, byte[] data) { using var stream = zip.CreateEntry(name).Open(); stream.Write(data, 0, data.Length); }
        Entry("manifest.json", Encoding.UTF8.GetBytes(manifest)); Entry(contentsName, payload);
        if (extra != null) Entry(extra, new byte[0]);
    }
    // Independent wire helpers let malformed cases reach the current public import boundary.
    private static List<LandingShareData> ReadContacts(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        Check(Encoding.ASCII.GetString(reader.ReadBytes(8)) == "LSLAND01", "Unexpected share contents header.");
        string ReadBytes() => Convert.ToBase64String(reader.ReadBytes(reader.ReadInt32()));
        var count = reader.ReadInt32();
        var contacts = new List<LandingShareData>();
        for (var index = 0; index < count; index++)
        {
            var contact = new LandingShareData { Core = ReadBytes() };
            var blockCount = reader.ReadInt32();
            for (var block = 0; block < blockCount; block++)
                contact.Blocks.Add(new LandingShareBlock { Type = Encoding.UTF8.GetString(Convert.FromBase64String(ReadBytes())),
                    Version = reader.ReadInt32(), Required = reader.ReadByte() != 0, Data = ReadBytes() });
            contacts.Add(contact);
        }
        Check(input.Position == input.Length, "Unexpected trailing share contents.");
        return contacts;
    }
    private static byte[] WriteContacts(IReadOnlyList<LandingShareData> contacts)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        void WriteBytes(byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
        writer.Write(Encoding.ASCII.GetBytes("LSLAND01")); writer.Write(contacts.Count);
        foreach (var contact in contacts)
        {
            WriteBytes(Convert.FromBase64String(contact.Core)); writer.Write(contact.Blocks.Count);
            foreach (var block in contact.Blocks)
            {
                WriteBytes(Encoding.UTF8.GetBytes(block.Type)); writer.Write(block.Version); writer.Write((byte)(block.Required ? 1 : 0));
                WriteBytes(Convert.FromBase64String(block.Data));
            }
        }
        writer.Flush(); return output.ToArray();
    }
    private static void ChangeCore(LandingShareData contact, Action<CoreIdentity> mutation)
    { var core = Decode<CoreIdentity>(Convert.FromBase64String(contact.Core)); mutation(core); contact.Core = Convert.ToBase64String(Encode(core)); }
    private static void ChangeBlock<T>(LandingShareData contact, string feature, Action<T> mutation)
    {
        var block = contact.Blocks.Single(candidate => candidate.Type == "org.landingstats." + feature);
        var value = Decode<T>(Convert.FromBase64String(block.Data)); mutation(value); block.Data = Convert.ToBase64String(Encode(value));
    }
    private static int FindSignature(byte[] data, uint signature)
    { for (var index = 0; index <= data.Length - 4; index++) if (BitConverter.ToUInt32(data, index) == signature) return index; throw new InvalidOperationException("ZIP signature missing."); }
    private static byte[] Encode<T>(T value)
    { using var data = new MemoryStream(); new DataContractJsonSerializer(typeof(T)).WriteObject(data, value); return data.ToArray(); }
    private static T Decode<T>(byte[] bytes)
    { using var data = new MemoryStream(bytes); return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(data)!; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DataContract] private sealed class CoreIdentity : IExtensibleDataObject
    {
        [DataMember(Name = "version")] public int Version;
        [DataMember(Name = "sourceId")] public string SourceId = "";
        [DataMember(Name = "episodeId")] public string EpisodeId = "";
        [DataMember(Name = "airport")] public string Airport = "";
        [DataMember(Name = "contactNumber")] public int ContactNumber;
        public ExtensionDataObject? ExtensionData { get; set; }
    }
    [DataContract] private sealed class SeriesFeature
    {
        [DataMember(Name = "columns")] public LandingSeriesColumns Columns = null!;
        [DataMember(Name = "sourceIndices")] public int[] SourceIndices = Array.Empty<int>();
    }
    [DataContract] private sealed class EnginesFeature
    { [DataMember(Name = "columns")] public LandingEngineColumns[] Columns = Array.Empty<LandingEngineColumns>(); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "landing-share-test-" + Guid.NewGuid().ToString("N"));
        internal LandingRepository Source { get; }
        internal LandingRecord First { get; }
        internal string Share { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root); Source = Repository("source"); First = Record(1, 2, "source-episode");
            var second = Record(2, 2, "source-episode"); Source.Save(First); Source.Save(second);
            Share = PathFor("source.lslanding"); LandingShareArchive.Export(Source, second.Id, Share);
        }
        internal string PathFor(string name) => Path.Combine(_root, name);
        internal LandingRepository Repository(string name) => new LandingRepository(PathFor(name));
        public void Dispose() { Directory.Delete(_root, true); }
        internal static LandingRecord Record(int number, int count, string episode)
        {
            var record = new LandingRecord { TimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), ContactNumber = number,
                ContactCount = count, RecordingEpisodeId = episode, SurfaceFpm = double.NaN, InertialFpm = -100, Simulator = "MSFS 2024",
                AircraftTitle = "Shared aircraft", AircraftType = "A320", ControllerAxisCount = 8, RawControllerAxesSourceIndices = new List<int> { 0 },
                ControlInputSources = new List<string> { "Recorded stick" },
                RunwayGeometry = new RunwayFacility { AirportIdent = "TEST", LatitudeDegrees = 42, LongitudeDegrees = 23, AltitudeMeters = 500,
                    HeadingTrueDegrees = 90, LengthMeters = 3000, WidthMeters = 45 } };
            var engine = new LandingEngineSeries { EngineNumber = 1 }; record.Engines.Add(engine);
            var contact = new LandingContactSeries { ContactPointIndex = 2 }; record.ContactPoints.Add(contact);
            var raw = new List<TelemetrySample>();
            for (var i = 0; i < 3; i++)
            {
                var time = (i - 1) * .1;
                record.Series.Add(new LandingSeriesPoint { TimeSeconds = time, InertialFpm = -100 + i, GForce = 1 + i * .1, PitchDegrees = 3, BankDegrees = 1,
                    LatitudeDegrees = 42, LongitudeDegrees = 23 + i * .0001, PlaneAltitudeFeet = 1650, GroundAltitudeFeet = 1640, HeadingTrueDegrees = 90,
                    RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(), RawControllerYAxisAgeSeconds = Array.Empty<double>(),
                    RawControllerAxesPercent = new[] { 10.0 + i * 2 }, RawControllerAxesValid = new[] { true }, RawControllerAxesAgeSeconds = new[] { .01 } });
                engine.Points.Add(new LandingEnginePoint { TimeSeconds = time, ThrottlePercent = 40, N1Percent = 50, Rpm = 1000 });
                contact.Points.Add(new LandingContactPoint { TimeSeconds = time, CompressionPercent = 20, PositionPercent = 100, OnGround = i > 0 });
                raw.Add(new TelemetrySample { Sequence = i, SimulationTimeSeconds = time + 100,
                    SourceEvidence = new ReplaySourceEvidence(Guid.Parse("11111111-1111-1111-1111-111111111111"), 1, 1, i, i, 10,
                        SdkReplayState.Inactive, new ReplaySourceObservation(SourceObservationAvailability.Available, SourceObservationReason.None, 0, 0, 1)) });
            }
            record.SourceEvidenceTimeline = ReplaySourceTimelineCodec.EncodeText(raw);
            record.AnalysisGeometry = AnalysisGeometrySnapshot.Capture(raw, new TouchdownAnalysisOptions(), "recorded_fixture");
            var device = new ControllerDescriptor(0, 0, "Recorded stick", "product", 255, new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), Guid.NewGuid(), Guid.NewGuid());
            var catalog = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers), 0, 1, 100,
                new ControllerCatalogMessage(1, new[] { device }, new[] { new ControllerBinding(ControllerControlKind.Pitch, -1, device.InstanceId, 0, false, "MSFS profile") })));
            record.ControllerTimeline = new ControllerCatalogTimeline { SampleCount = 3, Catalogs = new[] { Convert.ToBase64String(catalog) }, Transitions = new[] { new[] { 0, 0 } } };
            return record;
        }
    }
}
