using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class LandingImpactStorageRegressionTests
{
    public static void RunAll()
    {
        LegacyDisplaysStoredMeasurement();
        foreach (var version in Enumerable.Range(1, 7)) ExistingHistoryIsNotRecalculated(version);
        NativePersistenceAndNoFallback();
        GDetailShowsWindowOnlyForNewRecords();
    }

    private static void LegacyDisplaysStoredMeasurement()
    {
        var record = Legacy("legacy", 7);
        record.InertialFpm = 800;
        record.PeakG2Seconds = 4;
        foreach (var peak in new[] { 1.1, 1.2, 1.499999999, 1.5 })
        {
            record.PeakG150Milliseconds = peak;
            Assert(record.HasImpactClassification, "valid saved peak is displayed without recomputation");
            Equal(peak < 1.2, record.IsSmoothLanding, "smooth threshold");
            Equal(peak >= 1.2 && peak < 1.5, record.IsNormalLanding, "normal threshold");
            Equal(peak >= 1.5, record.IsFirmLanding, "firm threshold uses unrounded G");
            Equal(peak.ToString("F2", CultureInfo.CurrentCulture), new LandingStats.App.Presentation.LandingPresentation(record).ImpactPeakDisplay, "legacy display uses the saved 150-ms G");
            Equal(0, record.ImpactAnalysisVersion, "display does not migrate the record");
            Assert(record.ImpactPeakG == null, "display does not add a derived peak");
        }
        foreach (var peak in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            record.PeakG150Milliseconds = peak;
            Assert(!record.HasImpactClassification, "invalid legacy peak is not a smooth landing");
        }
    }

    private static void ExistingHistoryIsNotRecalculated(int version)
    {
        WithDirectory(root =>
        {
            var repository = new LandingRepository(root);
            var record = Legacy("old-" + version, version);
            // The series deliberately disagrees with the stored result. It must not be analyzed.
            record.Series.Add(new LandingSeriesPoint {
                TimeSeconds = 0, OnGround = true, GForce = 4,
                RawControllerYAxisPercent = Array.Empty<double>(),
                RawControllerYAxisValid = Array.Empty<bool>(),
                RawControllerYAxisAgeSeconds = Array.Empty<double>() });
            var payloadBefore = repository.SerializeForBackup(record);
            var path = repository.Save(record);
            var canonicalHash = Hash(File.ReadAllBytes(path));
            var indexHash = Hash(File.ReadAllBytes(repository.IndexPath));
            var indexTimestamp = File.GetLastWriteTimeUtc(repository.IndexPath);

            var reopened = new LandingRepository(root);
            var summary = reopened.LoadAll().Single();
            var detail = reopened.LoadDetail(summary)!;
            Assert(summary.IsNormalLanding && detail.IsNormalLanding, "old summary and detail use existing G only");
            Equal(1.25, detail.PeakG150Milliseconds, "old measured G unchanged");
            Equal(0, detail.ImpactAnalysisVersion, "old detail not migrated");
            Equal(0, summary.ImpactAnalysisVersion, "old index not migrated");
            Assert(detail.ImpactPeakG == null && summary.ImpactPeakG == null, "no adaptive result invented for old history");
            Equal(canonicalHash, Hash(reopened.Export(record.Id)), "manual export remains the original canonical file");
            Equal(Hash(payloadBefore), Hash(reopened.ExportForBackup(record.Id)), "cloud payload unchanged");
            Equal(indexHash, Hash(File.ReadAllBytes(repository.IndexPath)), "startup and selection do not rewrite the index");
            Equal(indexTimestamp, File.GetLastWriteTimeUtc(repository.IndexPath), "existing index timestamp unchanged");
            Equal(canonicalHash, Hash(File.ReadAllBytes(path)), "canonical history remains byte-identical");
            Assert(!Inflate(reopened.Export(record.Id)).Contains("Impact"), "old serialized contract has no added default impact fields");

            var imported = new LandingRepository(Path.Combine(root, "cloud-copy")).Import(payloadBefore);
            Equal(0, imported.ImpactAnalysisVersion, "old cloud import is not recalculated");
            Assert(imported.IsNormalLanding, "old cloud import retains stored measurement");
        });
    }

    private static void NativePersistenceAndNoFallback()
    {
        WithDirectory(root =>
        {
            var result = new TouchdownResult {
                SimulationTimeSeconds = 10, EstimatedContactTimeSeconds = 9.98,
                PeakG150Milliseconds = 1.01, PeakG = 4,
                Impact = new TouchdownImpactResult(TouchdownImpactStatus.Settled, 1.500000123, 0.175, 0.85) };
            var record = LandingRecordFactory.Create(result, Array.Empty<TelemetrySample>(), "Test", "Test");
            var repository = new LandingRepository(root);
            repository.Save(record);
            var summary = new LandingRepository(root).LoadAll().Single();
            var detail = repository.LoadDetail(summary)!;
            Equal(TouchdownImpactAnalysis.CurrentVersion, detail.ImpactAnalysisVersion, "native version persisted");
            Equal(1.500000123, detail.ImpactPeakG!.Value, "native peak precision persisted");
            Near(0.195, detail.ImpactPeakOffsetSeconds!.Value, "native peak offset uses the record contact reference");
            Near(0.87, detail.ImpactEndOffsetSeconds!.Value, "native boundary offset uses the record contact reference");
            Near(0.85, detail.ImpactWindowSeconds!.Value, "window duration excludes the estimated-contact offset");
            Equal(detail.ImpactWindowSeconds, summary.ImpactWindowSeconds, "window duration survives summary storage");
            Equal(nameof(TouchdownImpactStatus.Settled), detail.ImpactStatus, "native quality persisted");
            Equal(1.01, detail.PeakG150Milliseconds, "legacy statistic not overwritten");
            Equal(4.0, detail.PeakG2Seconds, "two-second statistic not overwritten");
            Assert(summary.IsFirmLanding && detail.IsFirmLanding, "summary and detail use the native adaptive peak");
            var imported = new LandingRepository(Path.Combine(root, "native-copy")).Import(repository.ExportForBackup(record.Id));
            Equal(detail.ImpactPeakG, imported.ImpactPeakG, "native adaptive result survives cloud export/import");
            Equal(detail.ImpactWindowSeconds, imported.ImpactWindowSeconds, "window duration survives cloud export/import");
            Assert(imported.IsFirmLanding, "cloud copy uses native classification");

            record.ImpactStatus = nameof(TouchdownImpactStatus.Incomplete);
            Assert(!record.HasImpactClassification, "incomplete native impact cannot fall back to old smooth G");
            Equal(record.ImpactPeakG!.Value.ToString("F2", CultureInfo.CurrentCulture), new LandingStats.App.Presentation.LandingPresentation(record).ImpactPeakDisplay, "incomplete observed peak remains visible");
            record.ImpactStatus = nameof(TouchdownImpactStatus.InvalidTelemetry);
            Assert(!record.HasImpactClassification, "invalid native impact cannot fall back to legacy G");
            record.ImpactStatus = nameof(TouchdownImpactStatus.Settled);
            record.ImpactAnalysisVersion = TouchdownImpactAnalysis.CurrentVersion + 1;
            Assert(!record.HasImpactClassification, "unknown future native version cannot fall back to legacy G");

            var unavailable = LandingRecordFactory.Create(new TouchdownResult { PeakG150Milliseconds = 1.1 },
                Array.Empty<TelemetrySample>(), "Test", "Test");
            Assert(unavailable.ImpactPeakG == null && unavailable.ImpactEndOffsetSeconds == null &&
                unavailable.ImpactPeakOffsetSeconds == null, "unavailable native values are nullable in the file contract");
            Assert(!unavailable.HasImpactClassification, "new unavailable result does not fall back to the old peak");
        });
    }

    private static void GDetailShowsWindowOnlyForNewRecords()
    {
        try
        {
            LocalizationManager.Apply("en");
            var legacy = Legacy("legacy-detail", 7);
            Equal(new LandingStats.App.Presentation.LandingPresentation(legacy).G2SecondsDisplay, new LandingStats.App.Presentation.LandingPresentation(legacy).GDetailDisplay, "legacy detail still shows saved two-second G");
            Equal(LocalizationManager.Text("Help.PeakG2"), new LandingStats.App.Presentation.LandingPresentation(legacy).GDetailTooltip, "legacy help describes the old metric");
            var record = LandingRecordFactory.Create(new TouchdownResult {
                SimulationTimeSeconds = 10, EstimatedContactTimeSeconds = 9.98,
                Impact = new TouchdownImpactResult(TouchdownImpactStatus.Settled, 1.3, .1, .74)
            }, Array.Empty<TelemetrySample>(), "Test", "Test");
            Equal("window 0.74 s", new LandingStats.App.Presentation.LandingPresentation(record).GDetailDisplay, "new detail shows actual adaptive duration");
            Equal(LocalizationManager.Text("Help.ImpactWindow"), new LandingStats.App.Presentation.LandingPresentation(record).GDetailTooltip, "new help describes the adaptive window");
            LocalizationManager.Apply("ru");
            Equal("окно 0,74 с", new LandingStats.App.Presentation.LandingPresentation(record).GDetailDisplay, "Russian window caption and decimal separator");
            record.ImpactStatus = nameof(TouchdownImpactStatus.Liftoff);
            Equal("окно 0,74 с", new LandingStats.App.Presentation.LandingPresentation(record).GDetailDisplay, "liftoff completes the contact window");
            record.ImpactStatus = nameof(TouchdownImpactStatus.Incomplete);
            Equal("неполное окно", new LandingStats.App.Presentation.LandingPresentation(record).GDetailDisplay, "incomplete window never displays a stale duration");
            record.ImpactStatus = nameof(TouchdownImpactStatus.InvalidTelemetry);
            Equal(LocalizationManager.Text("Model.NotAvailable"), new LandingStats.App.Presentation.LandingPresentation(record).GDetailDisplay, "invalid telemetry does not invent a duration");
            record.ImpactStatus = nameof(TouchdownImpactStatus.Settled);
            foreach (var duration in new double?[] { null, double.NaN, double.PositiveInfinity, -1, 0 })
            {
                record.ImpactWindowSeconds = duration;
                Equal(LocalizationManager.Text("Model.NotAvailable"), new LandingStats.App.Presentation.LandingPresentation(record).GDetailDisplay, "missing or invalid duration is unavailable");
            }
            record.ImpactWindowSeconds = .74;
            record.ImpactAnalysisVersion++;
            Equal(LocalizationManager.Text("Model.NotAvailable"), new LandingStats.App.Presentation.LandingPresentation(record).GDetailDisplay, "unknown version does not display a stale window");
        }
        finally { LocalizationManager.Apply("en"); }
    }

    private static LandingRecord Legacy(string id, int version) => new LandingRecord {
        Id = id, FormatVersion = version, TimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        InertialFpm = 800, PeakG150Milliseconds = 1.25, PeakG2Seconds = 4 };
    private static string Inflate(byte[] bytes) { using var memory = new MemoryStream(bytes); using var gzip = new GZipStream(memory, CompressionMode.Decompress); using var reader = new StreamReader(gzip); return reader.ReadToEnd(); }
    private static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(bytes)); }
    private static void WithDirectory(Action<string> action) {
        var path = Path.Combine(Path.GetTempPath(), "landing-impact-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); try { action(path); } finally { Directory.Delete(path, true); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message + ": expected " + expected + ", got " + actual); }
    private static void Near(double expected, double actual, string message) { if (Math.Abs(expected - actual) > 1e-12) throw new InvalidOperationException(message); }
}
