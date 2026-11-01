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

    private static void RevisedAndPartialEpisodes(Fixture fixture)
    {
        var repository = fixture.Repository("revisions");
        var original = LandingShareArchive.Import(repository, new[] { fixture.Share }).Imported;
        var changed = Decode<Payload>(ReadPayload(fixture.Share));
        changed.Contacts[0].Summary.Airport = "Changed airport";
        var revisedFile = fixture.PathFor("revision.lslanding");
        WriteShare(revisedFile, Encode(changed), 2);
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
        void PayloadCase(string name, Action<Payload> mutate)
        { var value = Decode<Payload>(payload); mutate(value); WriteShare(Case(name), Encode(value), 2); }
        File.WriteAllText(Case("plain"), "not an archive");
        var bytes = File.ReadAllBytes(fixture.Share); File.WriteAllBytes(Case("truncated"), bytes.Take(bytes.Length - 8).ToArray());
        WriteShare(Case("future"), payload, 2, version: 2);
        WriteShare(Case("count"), payload, 3);
        WriteShare(Case("checksum"), payload, 2, checksum: new string('0', 64));
        WriteShare(Case("path"), payload, 2, extra: "../outside.json");
        WriteShare(Case("extra"), payload, 2, extra: "extra.json");
        WriteShare(Case("duplicate"), payload, 2, extra: "landings.json");
        WriteShare(Case("trailing"), payload.Concat(Encoding.UTF8.GetBytes(" {} ")).ToArray(), 2);
        WriteShare(Case("null-payload"), Encoding.UTF8.GetBytes("{\"contacts\":null}"), 2);
        WriteShare(Case("empty"), Encode(new Payload()), 0);
        PayloadCase("future-layout", value => value.Contacts[0].LayoutVersion = 8);
        PayloadCase("future-record", value => value.Contacts[0].Summary.FormatVersion = 8);
        PayloadCase("missing-summary", value => value.Contacts[0].Summary = null!);
        PayloadCase("missing-series", value => value.Contacts[0].Series = null!);
        PayloadCase("null-engine", value => value.Contacts[0].Engines.Add(null!));
        PayloadCase("null-contact", value => value.Contacts.Add(null!));
        PayloadCase("bad-column", value => value.Contacts[0].Series.Time = new[] { 0.0 });
        PayloadCase("duplicate-id", value => value.Contacts[1].Summary.Id = value.Contacts[0].Summary.Id);
        PayloadCase("mixed-episode", value => value.Contacts[1].Summary.RecordingEpisodeId = "another-episode");
        PayloadCase("duplicate-number", value => value.Contacts[1].Summary.ContactNumber = 1);
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
    }

    private static byte[] ReadPayload(string path)
    { using var input = File.OpenRead(path); using var zip = new ZipArchive(input); using var entry = zip.GetEntry("landings.json")!.Open(); using var data = new MemoryStream(); entry.CopyTo(data); return data.ToArray(); }
    private static void WriteShare(string path, byte[] payload, int count, int version = 1, string? checksum = null, string? extra = null)
    {
        using var input = File.Create(path); using var zip = new ZipArchive(input, ZipArchiveMode.Create);
        using var hash = SHA256.Create();
        var manifest = "{\"format\":\"MSFS Landing Stats landing\",\"version\":" + version + ",\"contacts\":" + count + ",\"sha256\":\"" +
            (checksum ?? BitConverter.ToString(hash.ComputeHash(payload)).Replace("-", "").ToLowerInvariant()) + "\"}";
        void Entry(string name, byte[] data) { using var stream = zip.CreateEntry(name).Open(); stream.Write(data, 0, data.Length); }
        Entry("manifest.json", Encoding.UTF8.GetBytes(manifest)); Entry("landings.json", payload);
        if (extra != null) Entry(extra, new byte[0]);
    }
    private static int FindSignature(byte[] data, uint signature)
    { for (var index = 0; index <= data.Length - 4; index++) if (BitConverter.ToUInt32(data, index) == signature) return index; throw new InvalidOperationException("ZIP signature missing."); }
    private static byte[] Encode<T>(T value)
    { using var data = new MemoryStream(); new DataContractJsonSerializer(typeof(T)).WriteObject(data, value); return data.ToArray(); }
    private static T Decode<T>(byte[] bytes)
    { using var data = new MemoryStream(bytes); return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(data)!; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DataContract] private sealed class Payload
    { [DataMember(Name = "contacts", Order = 1)] public List<LandingRecordFile> Contacts = new List<LandingRecordFile>(); }

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
