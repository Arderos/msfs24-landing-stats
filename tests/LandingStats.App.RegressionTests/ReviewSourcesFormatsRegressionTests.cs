using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ReviewSourcesFormatsRegressionTests
{
    internal static void RunAll()
    {
        DecodeValidation(); OptionalText(); GeometryCompatibility(); BankedDatum(); VersionedDatumSnapshots(); CatalogCodec(); CacheInvalidation(); ReviewNativeSourceRegressionTests.RunAll();
    }
    private static void DecodeValidation()
    {
        foreach (var sample in new[] { new TelemetrySample { AnalysisTimeSeconds = double.PositiveInfinity },
            new TelemetrySample { PauseFlags = -2 }, new TelemetrySample { FlightSegment = -1 } })
            Reject(() => TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(sample)));
        var evidence = ReplaySourceCoreRegressionTests.Evidence();
        var bytes = ReplaySourceTimelineCodec.Encode(new[] { new TelemetrySample { SourceEvidence = evidence } })!;
        for (var length = 0; length < bytes.Length; length++) Reject(() => ReplaySourceTimelineCodec.Decode(bytes.Take(length).ToArray(), 1));
        bytes[10] = 100; Reject(() => ReplaySourceTimelineCodec.Decode(bytes, 1));
        var result = new ReplaySourceAssessor().Observe(ReplaySourceCoreRegressionTests.Evidence(1000, 4000), true);
        Check(result.Kind == ReplaySourceKind.SuspiciousKinematics && result.Reason == ReplaySourceAssessmentReason.ObservationStale, "Kinematic suspicion erased the stale reason.");
        Check(new ReplaySourceAssessor().Observe(ReplaySourceCoreRegressionTests.Evidence(mask: 0)).Reason == ReplaySourceAssessmentReason.NoFreeze, "Unfrozen pose called partial freeze.");
        Check(new ReplaySourceAssessor().Observe(evidence, true).Kind == ReplaySourceKind.SuspiciousKinematics, "Frozen stationary evidence erased numerical suspicion.");
        var epoch = new RuntimeClockEpoch(Guid.Parse("22222222-2222-2222-2222-222222222222"), 1, 1000);
        var worker = new WorkerIdentity(Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"), 1, ComponentRole.Controllers);
        var catalog = new RuntimeEnvelope(worker, 0, 1, 2, new ControllerCatalogMessage(1, new[] { new ControllerDescriptor(0, 0, "Stick", "model", 3, new uint[6], Enumerable.Repeat(100u, 6).ToArray()) }));
        var snapshot = new RuntimeEnvelope(worker, 0, 2, 3, new ControllerSnapshotMessage(1, new long[8], new double[48], new bool[48]));
        var joined = new TelemetrySample(); new ControllerJoinDecision(epoch, 4, TimeSpan.FromMilliseconds(250), true, catalog, snapshot).Apply(joined);
        Check(joined.RawControllerAxesAgeSeconds.All(value => !double.IsNaN(value) && !double.IsInfinity(value)) && !joined.RawControllerAxesValid.Any(value => value), "Invalid native poll produced nonfinite age or valid axes.");
        Check(!TelemetryCsv.Format(joined).Split(',').Where((value, index) => TelemetryCsv.Header.Split(',')[index].Contains("age_s")).Contains("NaN"), "CSV invalid-poll ages violate the receiver schema.");
    }
    private static void OptionalText()
    {
        var source = new string('界', 255) + "\0\uD800";
        var value = OptionalMetadataText.Sanitize(source, 256, "unknown");
        Check(Encoding.UTF8.GetByteCount(value) <= 256 && value.IndexOf('\0') < 0, "Optional text exceeds wire bounds.");
        _ = new UTF8Encoding(false, true).GetBytes(value);
        var journal = new TelemetryDecisionJournal(); journal.Record(0, new string('x', 47) + "😀", null!, " ", new Dictionary<string, double> { ["\uD800"] = 1, [""] = 2 });
        var entry = journal.Snapshot().Entries.Single();
        _ = new UTF8Encoding(false, true).GetBytes(entry.Component + entry.Code + entry.Reason + string.Join("", entry.Details.Keys));
        Check(entry.Code.Length > 0 && entry.Reason.Length > 0 && entry.Details.Keys.All(key => key.Length > 0), "Required journal fields became empty.");
        Check(!SimulatorWorkerRole.IsReplacementStart(SimulatorFlowEvent.FLIGHT_START) && SimulatorWorkerRole.IsReplacementStart(SimulatorFlowEvent.FLT_LOAD), "Late FLIGHT_START cancels an active scenery lookup.");
    }
    private static void GeometryCompatibility()
    {
        var samples = new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 } };
        var snapshot = AnalysisGeometrySnapshot.Capture(samples, new TouchdownAnalysisOptions(), "fixture");
        var record = new LandingRecord { AnalysisGeometry = snapshot };
        var json = Serialize(record);
        Check(json.Contains("AnalysisGeometryV2") && !json.Contains("\"AnalysisGeometry\":"), "New snapshot breaks the published v1 member contract.");
        var old = Deserialize<LegacyRecord>(json); Check(old.AnalysisGeometry == null, "0.9.4 sees the v2 snapshot in its old member.");
        var restored = Deserialize<LandingRecord>(json); restored.AnalysisGeometry!.Restore(samples);
        snapshot.Version = 77; snapshot.InputFingerprintFormat = "future";
        var future = snapshot.Copy(); Check(future.Version == 77, "Unknown snapshot was dropped while copying."); Reject(() => future.Restore(samples));
        var unknown = Deserialize<LandingRecord>("{\"Id\":\"fixture\",\"FutureField\":{\"answer\":42}}");
        unknown.TimestampUtc = DateTime.UtcNow;
        Check(Serialize(unknown).Contains("FutureField"), "Unknown history field lost on roundtrip.");
        var summary = LandingRecordFile.SanitizedSummaryCopy(unknown);
        Check(Serialize(summary).Contains("FutureField"), "Unknown field lost through summary copy.");
        var futureSnapshot = Deserialize<AnalysisGeometrySnapshot>(Serialize(future).Insert(1, "\"FutureGeometry\":{\"answer\":43},"));
        unknown.AnalysisGeometry = futureSnapshot;
        unknown.ControllerTimeline = Deserialize<ControllerCatalogTimeline>("{\"version\":77,\"samples\":0,\"FutureCatalog\":{\"answer\":44}}");
        var detail = Deserialize<LandingRecordFile>(Serialize(LandingRecordFile.FromRecord(unknown)).Insert(1, "\"FutureDetail\":{\"answer\":45},"));
        var detailRoundtrip = Serialize(LandingRecordFile.FromRecord(detail.ToRecord()));
        foreach (var field in new[] { "FutureField", "FutureGeometry", "FutureCatalog", "FutureDetail" })
            Check(detailRoundtrip.Contains(field), "Detail conversion discarded optional unknown field " + field);
    }
    private static void BankedDatum()
    {
        var samples = BankedDatumSamples();
        Check(TelemetryGeometryCalibration.TryRecoverDatumOffset(samples, out var result), "Analytic banked datum failed calibration.");
        Check(Math.Abs(result.DatumOffsetFeet - 4) < 1e-8, "Bank/yaw projection did not recover independently generated 4 ft datum: " + result.DatumOffsetFeet);
    }
    private static List<TelemetrySample> BankedDatumSamples()
    {
        var samples = new List<TelemetrySample>(); var altitude = 1000.0; var previousVertical = 0.0;
        for (var i = 0; i <= 400; i++)
        {
            var time = i * .02; var pitch = .08 + .025 * Math.Sin(.9 * time); var bank = .35 + .1 * Math.Sin(.5 * time);
            var pitchRate = .0225 * Math.Cos(.9 * time); var yaw = .012 + .005 * Math.Cos(.7 * time);
            var q = (pitchRate + yaw * Math.Sin(bank)) / Math.Cos(bank);
            var vy = -5 + .2 * Math.Sin(time); var vertical = vy - 4 * Math.Cos(pitch) * pitchRate;
            if (i > 0) altitude += .01 * (previousVertical + vertical);
            previousVertical = vertical;
            samples.Add(new TelemetrySample { Sequence = i + 1, SimulationTimeSeconds = time, MotionSimulation = true,
                AboveGroundLevelFeet = 100, PlaneAltitudeFeet = altitude, GroundAltitudeFeet = 0, VelocityWorldYFps = vy,
                PitchDegrees = pitch * 180 / Math.PI, BankDegrees = bank * 180 / Math.PI,
                RotationVelocityBodyXRadiansPerSecond = q, RotationVelocityBodyYRadiansPerSecond = yaw });
        }
        return samples;
    }
    private static void VersionedDatumSnapshots()
    {
        var samples = SingleNoseGeometryRegressionTests.VersionedDatumTrace();
        var current = TouchdownAnalysis.Analyze(samples).Single();
        var legacy = TouchdownAnalysis.Analyze(samples, new TouchdownAnalysisOptions
        { DatumCalibrationVersion = TelemetryDatumCalibrationVersion.Legacy }).Single();
        // Fixed from retained P11 Core (SHA256 0cf9d33f...f9149), independently
        // executed on this synthetic trace; never derive expected bits here.
        foreach (var expected in new Dictionary<string, string>
        {
            [nameof(TouchdownResult.ClosureReconstructionLongitudinalArmFeet)] = "c0222de41949adce",
            [nameof(TouchdownResult.ReconstructedClosureFpm)] = "406d1b010e46905a",
            [nameof(TouchdownResult.ReconstructedPitchFpm)] = "c01fdcaac5cb18fc",
            [nameof(TouchdownResult.ClosureReconstructionGeometryQuality)] = "3fefed2acee50bfa",
        })
            Check(BitConverter.DoubleToInt64Bits((double)typeof(TouchdownResult).GetProperty(expected.Key)!.GetValue(legacy)!).ToString("x16") == expected.Value,
                "Frozen historical datum result changed: " + expected.Key);
        Check(current.ClosureReconstructionAvailable && legacy.ClosureReconstructionAvailable &&
            Math.Abs(current.ClosureReconstructionLongitudinalArmFeet - legacy.ClosureReconstructionLongitudinalArmFeet) > .1,
            "Versioned fixture does not distinguish the archived and corrected datum.");
        var snapshot = AnalysisGeometrySnapshot.Capture(samples, new TouchdownAnalysisOptions(), "fixture");
        var currentJson = Serialize(snapshot);
        Check(currentJson.Contains("\"DatumCalibrationVersion\":2"), "New geometry did not persist the corrected datum revision.");
        var restored = Deserialize<AnalysisGeometrySnapshot>(currentJson);
        Same(TouchdownAnalysis.Analyze(samples, restored.Restore(samples)).Single(), current, "New snapshot changed corrected analysis.");
        foreach (var schema in new[] { 1, 2 })
        {
            var archived = snapshot.Copy(); archived.Version = schema;
            if (schema == 1)
            {
                archived.InputFingerprintFormat = "telemetry_csv_v7";
                using var sha = SHA256.Create();
                archived.InputSha256 = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    string.Join("", samples.Select(sample => GeometryFingerprintCsvV7.Format(sample) + "\n"))))).Replace("-", "").ToLowerInvariant();
            }
            // Exact old serialized shape: the revision member did not exist.
            var oldJson = Serialize(archived).Replace(",\"DatumCalibrationVersion\":2", "");
            Check(!oldJson.Contains("DatumCalibrationVersion"), "Legacy fixture still contains the new revision.");
            var oldSnapshot = Deserialize<AnalysisGeometrySnapshot>(oldJson);
            var oldOptions = oldSnapshot.Restore(samples);
            Check(oldOptions.DatumCalibrationVersion == TelemetryDatumCalibrationVersion.Legacy, "Missing revision silently selected the current equation.");
            Same(TouchdownAnalysis.Analyze(samples, oldOptions).Single(), legacy, "Historical snapshot changed its archived computation.");
            var record = LandingRecordFile.FromRecord(new LandingRecord { AnalysisGeometry = oldSnapshot });
            Same(TouchdownAnalysis.Analyze(samples, Deserialize<LandingRecordFile>(Serialize(record)).ToRecord().AnalysisGeometry!.Restore(samples)).Single(),
                legacy, "Detail roundtrip changed archived datum revision.");
        }
        snapshot.DatumCalibrationVersion = 77;
        var future = Deserialize<AnalysisGeometrySnapshot>(Serialize(snapshot));
        Check(!future.IsSupported && future.Copy().DatumCalibrationVersion == 77, "Unknown datum revision was interpreted or discarded.");
        Reject(() => future.Restore(samples));

        void Same(TouchdownResult actual, TouchdownResult expected, string reason)
        {
            foreach (var property in typeof(TouchdownResult).GetProperties().Where(property => property.PropertyType == typeof(double)))
                Check(BitConverter.DoubleToInt64Bits((double)property.GetValue(actual)!) == BitConverter.DoubleToInt64Bits((double)property.GetValue(expected)!), reason + " " + property.Name);
            Check(actual.Sequence == expected.Sequence && actual.ContactNumber == expected.ContactNumber &&
                actual.ClosureReconstructionModel == expected.ClosureReconstructionModel, reason + " identity/model");
        }
    }
    private static void CatalogCodec()
    {
        var value = new RuntimeEnvelope(new WorkerIdentity(Guid.Parse("11111111-2222-3333-4444-555555555555"), Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"), 2, ComponentRole.Controllers), 0, 3, 4000,
            new ControllerCatalogMessage(5, new[] { new ControllerDescriptor(0, 7, "Fixture stick", "1234:5678:model", 3, new uint[6], Enumerable.Repeat(65535u, 6).ToArray()) }));
        var bytes = ControllerProvenanceCodec.Encode(value);
        Check(Encoding.ASCII.GetString(bytes, 0, 4) == "LCC1", "Long-term catalog uses live IPC framing.");
        Check(ControllerProvenanceCodec.Encode(ControllerProvenanceCodec.Decode(bytes)!).SequenceEqual(bytes), "Catalog archive changed on roundtrip.");
        Check(ControllerProvenanceCodec.Encode(ControllerProvenanceCodec.Decode(RuntimeWire.Encode(value))!).SequenceEqual(bytes), "Legacy catalog cannot be imported.");
        for (var length = 1; length < bytes.Length; length++) Reject(() => ControllerProvenanceCodec.Decode(bytes.Take(length).ToArray()));
        Reject(() => ControllerProvenanceCodec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        var future = new byte[] { (byte)'L', (byte)'C', (byte)'C', (byte)'2', 99, 88 };
        Check(ControllerProvenanceCodec.Decode(future) == null, "Future optional catalog was interpreted by the v1 reader.");
        var samples = new[] { new TelemetrySample { ControllerCatalog = future } };
        var timeline = ControllerCatalogTimeline.Capture(samples)!;
        var restored = new[] { new TelemetrySample() }; timeline.Apply(restored);
        Check(restored[0].ControllerCatalog!.SequenceEqual(future), "Future catalog bytes were discarded on recapture.");
        new ControllerCatalogTimeline { Version = 77 }.Apply(restored);
        Check(restored[0].ControllerCatalog!.SequenceEqual(future), "Unsupported timeline changed existing measured provenance.");
        Console.WriteLine("catalog_lcc1_fixture=" + Convert.ToBase64String(bytes));
    }
    private static void CacheInvalidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-geometry-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "common", "Packages", "Community", "fixture");
        var plane = Path.Combine(package, "SimObjects", "Airplanes", "Fixture"); var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(plane);
        try
        {
            File.WriteAllText(Path.Combine(package, "manifest.json"), "{\"content_type\":\"MISC\"}");
            File.WriteAllText(Path.Combine(plane, "aircraft.cfg"), "[FLTSIM.0]\ntitle = Fixture\natc_model = TEST\n");
            var cfg = Path.Combine(plane, "flight_model.cfg");
            string Geometry(int arm) => "[CONTACT_POINTS]\npoint.0 = 1, 20, 0, -9\npoint.1 = 1, " + arm + ", -10, -9\npoint.2 = 1, " + arm + ", 10, -9\n";
            File.WriteAllText(cfg, Geometry(-12));
            FlightModelGeometryResolver Resolver() => new FlightModelGeometryResolver(new[] { Path.Combine(root, "common", "Packages") }, false, null, cache);
            Check(Resolver().CatalogForTesting().Single().MainGearLongitudinalFeet == -12, "MISC aircraft beneath an unrelated common directory was lost.");
            var cachePath = Directory.GetFiles(cache).Single(); var written = File.GetLastWriteTimeUtc(cachePath);
            Check(Resolver().CatalogForTesting().Single().MainGearLongitudinalFeet == -12 && File.GetLastWriteTimeUtc(cachePath) == written, "Unchanged catalog rebuilt its persisted index.");
            var samples = BankedDatumSamples();
            for (var i = 1; i <= 3; i++)
            {
                var landed = samples[400].CopyForAnalysis(); landed.Sequence = 401 + i; landed.SimulationTimeSeconds = 8 + i * .02;
                landed.OnGround = true; landed.AboveGroundLevelFeet = 0; samples.Add(landed);
            }
            var id = Guid.NewGuid(); var episode = new LandingEpisodeEventArgs(1, samples, "MSFS", "Fixture", "TEST", "unknown",
                Array.Empty<AirportFacility>(), Array.Empty<string>(), persistentId: id);
            var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64));
            var manifest = store.CommitInputs(episode, DateTime.UtcNow, Array.Empty<AirportFacility>(), false, 1);
            var processor = new EpisodeProcessor(Resolver(), new LandingRepository(Path.Combine(root, "history")), new object());
            var frozen = processor.ResolveGeometryAsync(store.ReadInputs(manifest), manifest).GetAwaiter().GetResult();
            Check(frozen.Segments.Length == 1 && frozen.Segments[0]?.LongitudinalArmFeet != null, "Fixture did not resolve configured geometry before freeze.");
            store.CommitGeometry(manifest, frozen); var geometryHash = manifest.GeometryHash;
            File.WriteAllText(cfg, Geometry(-14)); File.SetLastWriteTimeUtc(cfg, DateTime.UtcNow.AddSeconds(1));
            Check(Resolver().CatalogForTesting().Single().MainGearLongitudinalFeet == -14, "Catalog reused modified geometry.");
            var changedProcessor = new EpisodeProcessor(Resolver(), new LandingRepository(Path.Combine(root, "history")), new object());
            var changed = changedProcessor.ResolveGeometryAsync(store.ReadInputs(manifest), manifest).GetAwaiter().GetResult();
            Check(Math.Abs(changed.Segments[0]!.LongitudinalArmFeet!.Value - frozen.Segments[0]!.LongitudinalArmFeet!.Value + 2) < 1e-10,
                "Changed CFG did not alter the unfrozen control case.");
            store.CommitGeometry(manifest, changed);
            var retained = store.ReadGeometry(store.Read(id));
            Check(manifest.GeometryHash == geometryHash && Serialize(retained) == Serialize(frozen), "Retry replaced committed geometry with changed CFG.");
            var retried = changedProcessor.ProcessAsync(store.ReadInputs(manifest).Episode, Array.Empty<AirportFacility>(), DateTime.UtcNow,
                persistHistory: false, geometryInputs: retained).GetAwaiter().GetResult();
            Check(retried.Records.Count > 0 && retried.Records.All(record => record.AnalysisGeometry!.LongitudinalArmFeet == frozen.Segments[0]!.LongitudinalArmFeet),
                "Analysis retry re-resolved installed geometry after its input commit.");
            var livery = Path.Combine(package, "SimObjects", "Airplanes", "Livery"); Directory.CreateDirectory(livery);
            File.WriteAllText(Path.Combine(livery, "aircraft.cfg"), "[VARIATION]\nbase_container = ..\\Fixture\n[FLTSIM.0]\ntitle = Livery\n");
            var updated = Resolver().CatalogForTesting();
            Check(updated.Any(match => match.AircraftTitle == "Livery" && match.MainGearLongitudinalFeet == -14), "Added livery or unrelated common ancestor broke base_container: " + string.Join(",", updated.Select(match => match.AircraftTitle + ":" + match.MainGearLongitudinalFeet)) + " cache=" + File.ReadAllText(cachePath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [DataContract] private sealed class LegacyRecord
    { [DataMember] public AnalysisGeometrySnapshot? AnalysisGeometry { get; set; } }
    private static string Serialize<T>(T value)
    { using var bytes = new MemoryStream(); new DataContractJsonSerializer(typeof(T)).WriteObject(bytes, value); return Encoding.UTF8.GetString(bytes.ToArray()); }
    private static T Deserialize<T>(string json)
    { using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(json)); return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(bytes)!; }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid archive data was accepted or returned the wrong error type."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
