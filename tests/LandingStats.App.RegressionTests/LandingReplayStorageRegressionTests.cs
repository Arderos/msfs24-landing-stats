using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class LandingReplayStorageRegressionTests
{
    public static void RunAll()
    {
        var samples = Enumerable.Range(0, 5).Select(i =>
        {
            var sample = new TelemetrySample {
                SimulationTimeSeconds = 10 + i * .1, LatitudeDegrees = 0, LongitudeDegrees = 25.1 + i * .0001,
                MotionSimulation = true,
                PlaneAltitudeFeet = 220 + i * .25, GroundAltitudeFeet = 210,
                HeadingTrueDegrees = 359 + i * .1, PitchDegrees = -2, BankDegrees = 3,
                GForce = 1.2, AboveGroundLevelFeet = 10, OnGround = i >= 2,
            };
            foreach (var axis in new[] { 0, 8, 11 })
            {
                sample.RawControllerAxesPercent[axis] = axis + i;
                sample.RawControllerAxesValid[axis] = true;
                sample.RawControllerAxesAgeSeconds[axis] = .02;
            }
            return sample;
        }).ToArray();
        var result = new TouchdownResult { SimulationTimeSeconds = 10.2, EstimatedContactTimeSeconds = 10.2 };
        var record = LandingRecordFactory.Create(result, samples, "Test", "Test");
        record.RecordingEpisodeId = "stable-capture-identity:2";
        Assert(LandingRepository.CreateSummary(record).RecordingEpisodeId == record.RecordingEpisodeId,
            "summary cloning preserves physical episode identity");
        var episodeSamples = samples.Concat(new[] { new TelemetrySample { SimulationTimeSeconds = 32,
            MotionSimulation = true, AboveGroundLevelFeet = 0, OnGround = true } }).ToArray();
        var episode = LandingRecordFactory.Create(result, episodeSamples, "Test", "Test", contactCount: 3, episodeEndSimulationTimeSeconds: 32);
        Assert(episode.Series.Last().TimeSeconds > 21, "first bounce contact retains final episode rollout");
        Assert(record.Series.Count == 5, "all fixture samples retained");
        Assert(record.RawControllerAxesSourceIndices.SequenceEqual(new[] { 0, 8, 11 }), "compact axes preserve device mapping");
        Assert(record.Series[0].LatitudeDegrees == 0 && record.Series[0].LongitudeDegrees == 25.1, "zero latitude is real data");
        record.RunwayGeometry = new RunwayFacility {
            AirportIdent = "TEST", LatitudeDegrees = 0, LongitudeDegrees = 25.1, AltitudeMeters = 64,
            LengthMeters = 2200, WidthMeters = 45, HeadingTrueDegrees = 90, PrimaryName = "09", SecondaryName = "27",
        };
        record.Series[1].RawControllerAxesPercent[1] = double.NaN;
        record.Series[1].RawControllerAxesAgeSeconds[1] = double.PositiveInfinity;
        var directory = Path.Combine(Path.GetTempPath(), "landing-replay-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(directory);
            repository.Save(record);
            var summary = new LandingRepository(directory).LoadAll().Single();
            var restored = repository.LoadDetail(summary)!;
            Assert(summary.RecordingEpisodeId == record.RecordingEpisodeId && restored.RecordingEpisodeId == record.RecordingEpisodeId,
                "index and detail round-trips preserve physical episode identity");
            Assert(summary.RunwayGeometry?.PrimaryName == "09", "runway retained in summary");
            Assert(restored.Series[4].LongitudeDegrees == samples[4].LongitudeDegrees, "GPS precision survives persistence");
            Assert(restored.Series[4].HeadingTrueDegrees == samples[4].HeadingTrueDegrees, "true heading survives persistence");
            Assert(restored.Series[4].RawControllerAxesPercent.SequenceEqual(new[] { 4.0, 12.0, 15.0 }), "all raw axes survive persistence");
            Assert(double.IsNaN(restored.Series[1].RawControllerAxesPercent[1]) && !restored.Series[1].RawControllerAxesValid[1], "nonfinite input restored unavailable");
            var bytes = repository.ExportForBackup(record.Id);
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var json = reader.ReadToEnd();
            Assert(json.Contains("\"pose\":") && json.Contains("\"inputs\":") && json.Contains("null"), "optional blocks use strict nullable JSON");
            Assert(!json.Contains("NaN") && !json.Contains("Infinity"), "no nonfinite JSON tokens");
            var cloud = new LandingRepository(Path.Combine(directory, "cloud")).Import(bytes);
            Assert(cloud.RunwayGeometry?.WidthMeters == 45 && cloud.RawControllerAxesSourceIndices.SequenceEqual(record.RawControllerAxesSourceIndices), "cloud import preserves runway and mapping");
            Assert(cloud.Series[0].PlaneAltitudeFeet == 220, "cloud import preserves replay positions");
            Assert(cloud.RecordingEpisodeId == record.RecordingEpisodeId, "cloud import preserves physical episode identity");

            record.RecordingEpisodeId = null;
            var legacyBytes = repository.SerializeForBackup(record);
            using var legacyInput = new MemoryStream(legacyBytes);
            using var legacyGzip = new GZipStream(legacyInput, CompressionMode.Decompress);
            using var legacyReader = new StreamReader(legacyGzip, Encoding.UTF8);
            Assert(!legacyReader.ReadToEnd().Contains("RecordingEpisodeId"), "legacy records do not gain fabricated identity metadata");
            Assert(repository.DeserializeBackup(legacyBytes).RecordingEpisodeId == null,
                "pre-identity v7 files remain readable with an absent identity");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        var file = LandingRecordFile.FromRecord(record);
        file.Pose!.Latitude = Array.Empty<double?>();
        Reject(() => file.ToRecord(), "truncated pose is rejected");
        file = LandingRecordFile.FromRecord(record);
        file.Inputs!.Valid[0] = Array.Empty<bool>();
        Reject(() => file.ToRecord(), "misaligned input row is rejected");
        file = LandingRecordFile.FromRecord(record);
        file.Summary.RawControllerAxesSourceIndices[1] = 0;
        Reject(() => file.ToRecord(), "duplicate axis mapping rejected");
        file = LandingRecordFile.FromRecord(record);
        file.Inputs = null;
        Reject(() => file.ToRecord(), "missing mapped axes block is rejected");
        var runwayAltitude = record.RunwayGeometry!.AltitudeMeters;
        record.RunwayGeometry.AltitudeMeters = double.NaN;
        Reject(() => LandingRecordFile.FromRecord(record), "runway metadata cannot emit nonfinite JSON");
        record.RunwayGeometry.AltitudeMeters = runwayAltitude;

        // A pre-feature v7 record has neither optional block; all original metrics remain usable.
        foreach (var point in record.Series)
        {
            point.LatitudeDegrees = point.LongitudeDegrees = point.PlaneAltitudeFeet = point.GroundAltitudeFeet = point.HeadingTrueDegrees = null;
            point.RawControllerAxesPercent = point.RawControllerAxesAgeSeconds = Array.Empty<double>();
            point.RawControllerAxesValid = Array.Empty<bool>();
        }
        record.RawControllerAxesSourceIndices.Clear();
        file = LandingRecordFile.FromRecord(record);
        Assert(file.Pose == null && file.Inputs == null, "legacy data does not gain invented channels");
        var legacy = file.ToRecord();
        Assert(!LandingReplayTrack.TryCreate(legacy, out _) && legacy.Series.Count == 5, "legacy retains charts without fabricated replay");
    }

    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
