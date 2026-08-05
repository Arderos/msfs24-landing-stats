using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LandingStats.App;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.App.TelemetryUpload;
using LandingStats.App.Updates;
using LandingStats.Core;
using UpdaterProgram = LandingStats.App.Updater.Program;

namespace LandingStats.App.RegressionTests;

internal static class Program
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _failures;

    private static int Main()
    {
        Run("deduplicator keeps the last same-time frame", DeduplicatorKeepsLastFrame);
        Run("approach timeout re-arms capture", ApproachTimeoutRearmsCapture);
        Run("pre-roll uses monotonic receipt time and remains bounded", PreRollUsesReceiptTimeAndRemainsBounded);
        Run("full telemetry gate has AGL hysteresis and RAW override", FullTelemetryGateHasHysteresis);
        Run("raw debug streams into a temporary queue zip", RawDebugStreamsIntoZip);
        Run("raw telemetry queue never blocks its producer", RawTelemetryQueueNeverBlocksProducer);
        Run("telemetry identity is stable, protected, and signs", TelemetryIdentityIsStableAndSigns);
        Run("updater accepts a signed manifest and rejects tampering", UpdaterVerifiesSignedManifest);
        Run("updater accepts the format-3 single executable manifest shape", UpdaterAcceptsSingleExecutableManifestShape);
        Run("updater cleanup refuses paths outside its private root", UpdaterCleanupRefusesUnsafePath);
        Run("updater installs one valid executable transactionally", UpdaterInstallsSingleExecutable);
        Run("updater rolls back an invalid executable replacement", UpdaterRollsBackInvalidExecutableReplacement);
        Run("valid frame clears transient error state", ValidFrameClearsTransientErrorState);
        Run("legacy SimConnect controller path is absent", LegacyControllerPathIsAbsent);
        Run("compact frame matches the SimConnect payload contract", CompactFrameMatchesPayloadContract);
        Run("telemetry schema v5 reads current and v4 rows", TelemetrySchemaReadsCurrentAndV4Rows);
        Run("telemetry CSV preserves doubles and rejects header-row schema mismatch", TelemetryCsvIsStrictAndRoundTrips);
        Run("header wind uses the contact-time sample", HeaderWindUsesContactTimeSample);
        Run("landing history uses lazy columnar v7 details", LandingHistoryUsesLazyColumnarDetails);
        Run("landing index reconciles committed details after an interrupted save", LandingIndexReconcilesCommittedDetails);
        Run("landing filenames are culture invariant", LandingFilenamesAreCultureInvariant);
        Run("airport cache is never overwritten after a transient read failure", AirportCacheSurvivesTransientReadFailure);
        Run("chart ranges discard non-finite telemetry", ChartRangesDiscardNonFiniteTelemetry);
        Run("closure reconstruction survives history round-trip", ClosureReconstructionSurvivesHistoryRoundTrip);
        Run("bounce history shows the latest contact first", BounceHistoryShowsLatestContactFirst);
        Run("columnar v7 is smaller than the object layout", ColumnarV7IsSmallerThanObjectLayout);
        Run("stored controller columns retain only live sources", StoredControllerColumnsAreCompact);
        Run("closure reconstruction keeps the raw latch independent", ClosureReconstructionKeepsRawLatchIndependent);
        Run("closure reconstruction requires five fit points", ClosureReconstructionRequiresFiveFitPoints);
        Run("closure reconstruction requires five distinct timestamps", ClosureReconstructionRequiresDistinctTimestamps);
        Run("closure reconstruction never extrapolates before its history", ClosureReconstructionRequiresEvaluationBracket);
        Run("closure reconstruction sanitizes an early ground spike", ClosureReconstructionSanitizesEarlyGroundSpike);
        Run("closure reconstruction is unavailable without geometry", ClosureReconstructionRequiresGeometry);
        Run("closure reconstruction marks last-air contact-time fallback", ClosureReconstructionMarksContactTimeFallback);
        Run("closure reconstruction accepts permuted staggered mains", ClosureReconstructionAcceptsPermutedStaggeredMains);
        Run("closure reconstruction accepts A340 center main conservatively", ClosureReconstructionAcceptsCenterMainConservatively);
        Run("closure reconstruction rejects five-point topology", ClosureReconstructionRejectsFivePointTopology);
        Run("closure reconstruction keeps sustained main-gear bounces", ClosureReconstructionKeepsMainGearBounces);
        Run("closure reconstruction rejects mixed main-nose contact", ClosureReconstructionRejectsMixedMainNoseContact);
        Run("closure reconstruction rejects nose-first contact without changing raw metrics", ClosureReconstructionRejectsNoseFirstContact);
        Run("telemetry geometry recovers an analytic arm from permuted points", TelemetryGeometryRecoversAnalyticArm);

        Console.WriteLine(_failures == 0
            ? "All regression tests passed."
            : $"{_failures} regression test(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void DeduplicatorKeepsLastFrame()
    {
        var first = new TelemetrySample { SimulationTimeSeconds = 10, Sequence = 1 };
        var replacement = new TelemetrySample { SimulationTimeSeconds = 10, Sequence = 2 };
        var later = new TelemetrySample { SimulationTimeSeconds = 10.02, Sequence = 3 };

        var result = TelemetryDeduplicator.Deduplicate(new[] { first, replacement, later });

        Equal(2, result.Count, "deduplicated frame count");
        Same(replacement, result[0], "latest same-time frame");
        Same(later, result[1], "later frame");
    }

    private static void ApproachTimeoutRearmsCapture()
    {
        using var recorder = NewRecorder();
        Invoke(recorder, "ProcessSample", ApproachSample(1));
        NotNull(Field(recorder, "_episodeSamples"), "episode should start below 500 ft");

        Invoke(recorder, "ProcessSample", ApproachSample(302));
        Null(Field(recorder, "_episodeSamples"), "timed-out episode should be discarded");
        Equal(true, Field(recorder, "_armed"), "recorder should be armed after timeout");

        Invoke(recorder, "ProcessSample", ApproachSample(302.02));
        NotNull(Field(recorder, "_episodeSamples"), "next descending frame should start a fresh episode");
    }

    private static void PreRollUsesReceiptTimeAndRemainsBounded()
    {
        var buffer = new PreRollBuffer(15.0, 3);
        buffer.Add(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 100 }, 0.0);
        buffer.Add(new TelemetrySample { Sequence = 2, SimulationTimeSeconds = 100 }, 14.0);
        buffer.Add(new TelemetrySample { Sequence = 3, SimulationTimeSeconds = 0 }, 16.0);

        var afterFrozenAndFallbackTimes = buffer.ToArray();
        Equal(2, afterFrozenAndFallbackTimes.Length, "receipt-time window count");
        Equal(2L, afterFrozenAndFallbackTimes[0].Sequence, "frozen simulator time cannot retain stale head");

        for (var sequence = 4; sequence <= 20; sequence++)
        {
            buffer.Add(new TelemetrySample { Sequence = sequence, SimulationTimeSeconds = 0 }, 16.0);
        }

        var bounded = buffer.ToArray();
        Equal(3, bounded.Length, "hard sample limit");
        Equal(18L, bounded[0].Sequence, "hard limit retains newest samples");
        Equal(20L, bounded[2].Sequence, "hard limit retains tail");
    }

    private static void FullTelemetryGateHasHysteresis()
    {
        using var recorder = NewRecorder();
        Invoke(recorder, "SetFullTelemetryForAgl", 2900.0);
        Equal(true, Field(recorder, "_fullTelemetryEnabled"), "full telemetry below the enable gate");
        Invoke(recorder, "SetFullTelemetryForAgl", 3200.0);
        Equal(true, Field(recorder, "_fullTelemetryEnabled"), "hysteresis should retain full telemetry");
        Invoke(recorder, "SetFullTelemetryForAgl", 3600.0);
        Equal(false, Field(recorder, "_fullTelemetryEnabled"), "full telemetry above the disable gate");
        Invoke(recorder, "SetRawDebugEnabled", true);
        Equal(true, Field(recorder, "_fullTelemetryEnabled"), "RAW should override the altitude gate");
        Invoke(recorder, "SetRawDebugEnabled", false);
        Equal(false, Field(recorder, "_fullTelemetryEnabled"), "disabling RAW should restore the altitude gate");
    }

    private static void RawDebugStreamsIntoZip()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-raw-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new RawCaptureRepository(root);
            string? completedPath = null;
            using (var session = repository.StartCapture(
                       Array.Empty<TelemetrySample>(), "MSFS", "Test aircraft", "TEST", "TEST", Array.Empty<string>(), DateTime.UtcNow))
            {
                session.ChunkCompleted += (_, args) => completedPath = args.Path;
                session.Write(new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 });
                session.Write(new TelemetrySample { Sequence = 2, SimulationTimeSeconds = 1.02 });
            }

            NotNull(completedPath, "completed RAW path");
            Equal(true, File.Exists(completedPath!), "RAW zip exists");
            using (var archive = ZipFile.OpenRead(completedPath!))
            using (var reader = new StreamReader(archive.GetEntry("telemetry.csv")!.Open()))
            {
                var lines = reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                Equal(3, lines.Length, "RAW header plus two samples");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void RawTelemetryQueueNeverBlocksProducer()
    {
        using var queue = new BoundedTelemetryQueue(1);
        Equal(true, queue.TryAdd(new TelemetrySample { Sequence = 1 }), "first queued sample");
        var stopwatch = Stopwatch.StartNew();
        Equal(false, queue.TryAdd(new TelemetrySample { Sequence = 2 }), "full queue rejects without dropping an accepted sample");
        stopwatch.Stop();
        Equal(true, stopwatch.Elapsed < TimeSpan.FromMilliseconds(100), "full queue returns immediately");
    }

    private static void TelemetryIdentityIsStableAndSigns()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var endpoint = new Uri("https://telemetry.example.test/");
            var firstStore = new TelemetryUploadIdentityStore(root);
            var first = firstStore.Identity();
            var message = Encoding.ASCII.GetBytes("signed telemetry fixture");
            var signature = Convert.FromBase64String(first.Sign(message));
            using (var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false })
            {
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Convert.FromBase64String(first.PublicModulus),
                    Exponent = Convert.FromBase64String(first.PublicExponent),
                });
                Equal(true, rsa.VerifyData(message, CryptoConfig.MapNameToOID("SHA256"), signature), "identity signature");
            }

            firstStore.AcceptConsent();
            firstStore.MarkEnrolled(endpoint, true);
            var secondStore = new TelemetryUploadIdentityStore(root);
            var second = secondStore.Identity();
            Equal(first.InstallId, second.InstallId, "stable install id");
            Equal(first.PublicModulus, second.PublicModulus, "stable public key");
            Equal(true, secondStore.ConsentAccepted, "persisted consent");
            Equal(true, secondStore.IsEnrolled(endpoint), "persisted enrollment");
            var stored = File.ReadAllText(Path.Combine(root, "identity.json"));
            Equal(false, stored.Contains("<RSAKeyValue>"), "private key must not be stored as plaintext XML");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void UpdaterVerifiesSignedManifest()
    {
        const string manifest = "format=2\nversion=0.7.1\npackage=MSFS-Landing-Stats.zip\npackage-size=1\npackage-sha256=0000000000000000000000000000000000000000000000000000000000000000\nupdater=MSFS-Landing-Stats.Updater.exe\nupdater-size=1\nupdater-sha256=1111111111111111111111111111111111111111111111111111111111111111\n";
        const string signature = "c7UOGSyNQQxx5454aO2GCoPkhosnnvRrN9G0T3d8T1ZtRr2tK9lEHtCrF5iIS0zhSsQMwPctBKhnSBlajnMakvSbttgLQTrlV72eWO4JWB/gCciFZIs9uJYmTQxHaALJAdAiKP8huIdXgaeEENJvHQDSvcfv395T/ydSRKL4BFuKqnksCf7GrjUNuoGAnInmRG15NqdxvdsFkMYnenhOABM2G/NJ1ECRZ9LHB5fPUoEBHHYBzSROO6glbHQhW4tU8JR/X03acQ2WpZk57ty3fsBClkju4HO0FHAd6j2Huy/Szzj867MJMHuICRjVzUfkR7L+qnMTSdlWYsbLQSO7WLqYD0EmZ6i7T7axQrsqm3vt6Js6P+HWmtYntvOskgmlsPkiRxV+kdBEF9GkIiNuB4ox/9sW6yxBYvPli7z1qUXVXWZ86P39hlKiwJ8iCdi3/ipF5DV1VYnCMRmqTQsKnEs4a2eQvYMa1+tm4sf7HZXVbGPovwxAuvHo8oeuIb8U";
        using (var updater = new ReleaseUpdater(new UpdateFixtureHandler(manifest, signature)))
        {
            var result = updater.CheckAndInstallAsync(new Version(0, 7, 1), CancellationToken.None).GetAwaiter().GetResult();
            Equal(ReleaseUpdateState.Current, result.State, "signed manifest state");
        }
        using (var updater = new ReleaseUpdater(new UpdateFixtureHandler(manifest.Replace("0.7.1", "9.9.9"), signature)))
        {
            var result = updater.CheckAndInstallAsync(new Version(0, 7, 1), CancellationToken.None).GetAwaiter().GetResult();
            Equal(ReleaseUpdateState.Rejected, result.State, "tampered manifest state");
        }
    }

    private static void UpdaterCleanupRefusesUnsafePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "landing-stats-unsafe-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(directory, "keep.txt");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(marker, "keep");
            ReleaseUpdater.BeginCompletedUpdateCleanup(new[] { "app", "--finish-update", "1", directory });
            Equal(true, File.Exists(marker), "unsafe cleanup marker");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static void UpdaterAcceptsSingleExecutableManifestShape()
    {
        const string manifest = "format=3\nversion=0.7.3\npackage=MSFS-Landing-Stats.exe\npackage-size=488021\npackage-sha256=0000000000000000000000000000000000000000000000000000000000000000\nupdater=MSFS-Landing-Stats.Updater.exe\nupdater-size=113152\nupdater-sha256=1111111111111111111111111111111111111111111111111111111111111111\n";
        var protocolType = typeof(ReleaseUpdater).Assembly.GetType(
            "LandingStats.UpdateProtocol.ReleaseUpdateProtocol",
            true)!;
        var parse = protocolType.GetMethod("ParseManifest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parsed = parse.Invoke(null, new object[] { new UTF8Encoding(false).GetBytes(manifest) })!;
        var packageAsset = parsed.GetType().GetProperty("PackageAsset")!.GetValue(parsed) as string;
        Equal("MSFS-Landing-Stats.exe", packageAsset, "format-3 package asset");
    }

    private static void UpdaterInstallsSingleExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-single-update-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "MSFS-Landing-Stats.exe");
        var replacement = Path.Combine(root, "replacement.exe");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(target, "old");
            var search = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string? builtExecutable = null;
            while (search != null && builtExecutable == null)
            {
                var candidate = Path.Combine(search.FullName, "artifacts", "MSFS-Landing-Stats.exe");
                if (File.Exists(candidate))
                {
                    builtExecutable = candidate;
                }
                search = search.Parent;
            }
            if (builtExecutable == null) throw new FileNotFoundException("Built single executable fixture is unavailable");
            File.Copy(builtExecutable, replacement);

            UpdaterProgram.InstallExecutableTransactionally(replacement, target, new Version(0, 7, 3));
            Equal(false, File.Exists(replacement), "replacement moved into place");
            Equal(true, new FileInfo(target).Length > 100, "installed executable length");
            Equal(0, Directory.GetFiles(root, ".msfs-landing-stats-backup-*.exe").Length, "transaction backup removed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void UpdaterRollsBackInvalidExecutableReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-update-rollback-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "MSFS-Landing-Stats.exe");
        var replacement = Path.Combine(root, "replacement.exe");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(target, "known-good-old-executable");
            File.WriteAllText(replacement, "invalid-new-executable");

            var failed = false;
            try
            {
                UpdaterProgram.InstallExecutableTransactionally(replacement, target, new Version(0, 7, 3));
            }
            catch (InvalidDataException)
            {
                failed = true;
            }
            Equal(true, failed, "invalid executable transaction failure");
            Equal("known-good-old-executable", File.ReadAllText(target), "single executable rollback content");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void ValidFrameClearsTransientErrorState()
    {
        using var recorder = NewRecorder();
        SetField(recorder, "_recoverStatusAfterFrame", true);
        Invoke(recorder, "RecoverStatusAfterFrame");
        Equal(false, Field(recorder, "_recoverStatusAfterFrame"), "transient error recovery flag");
    }

    private static void LegacyControllerPathIsAbsent()
    {
        var type = RecorderType();
        Null(type.GetMethod("OnRecvControllersList", InstancePrivate), "legacy controller-list handler");
        Null(type.GetNestedType("InputGroups", BindingFlags.NonPublic), "legacy input-group enum");
    }

    private static void CompactFrameMatchesPayloadContract()
    {
        var frameType = typeof(MainWindow).Assembly.GetType(
                            "LandingStats.App.Telemetry.SimFrameData",
                            true)
                        ?? throw new TypeLoadException("SimFrameData");
        Equal(20, TelemetrySample.CapturedContactPointCount, "documented contact-point count");
        Equal(4, TelemetrySample.CapturedEngineCount, "documented SimConnect engine count");
        Equal(892, Marshal.SizeOf(frameType), "packed frame size");
        var guardType = typeof(MainWindow).Assembly.GetType(
                            "LandingStats.App.Telemetry.SimGuardData",
                            true)
                        ?? throw new TypeLoadException("SimGuardData");
        Equal(32, Marshal.SizeOf(guardType), "packed guard size");
    }

    private static void TelemetrySchemaReadsCurrentAndV4Rows()
    {
        var sample = new TelemetrySample
        {
            Sequence = 42,
            SimulationTimeSeconds = 123.5,
            MotionSimulation = true,
            GroundAltitudeFeet = 1740.25,
        };
        sample.ContactPointCompression[19] = 0.375;
        sample.ContactPointPosition[19] = 0.875;
        sample.ContactPointOnGround[19] = true;

        var currentLine = TelemetryCsv.Format(sample);
        Equal(5, TelemetryCsv.SchemaVersion, "current telemetry schema");
        Equal(160, TelemetryCsv.Header.Split(',').Length, "compact telemetry columns");
        Equal(325, TelemetryCsv.V4Header.Split(',').Length, "v4 telemetry columns");
        Equal(TelemetryCsv.Header.Split(',').Length, currentLine.Split(',').Length, "header and row column count");
        Equal(true, TelemetryCsv.TryParse(currentLine, out var current), "parse current telemetry row");
        Equal(0.375, current.ContactPointCompression[19], "current contact compression");
        Equal(true, current.ContactPointOnGround[19], "current contact state");

        var currentColumns = TelemetryCsv.Header.Split(',');
        var currentValues = currentLine.Split(',');
        var valuesByColumn = currentColumns
            .Select((column, index) => new { column, value = currentValues[index] })
            .ToDictionary(item => item.column, item => item.value, StringComparer.Ordinal);
        var v4Line = string.Join(",", TelemetryCsv.V4Header.Split(',').Select(column =>
            valuesByColumn.TryGetValue(column, out var value) ? value : "0"));

        Equal(true, TelemetryCsv.TryParse(v4Line, out var v4), "parse v4 telemetry row");
        Equal(0.375, v4.ContactPointCompression[19], "v4 contact compression");
        Equal(true, v4.ContactPointOnGround[19], "v4 contact state");
    }

    private static void TelemetryCsvIsStrictAndRoundTrips()
    {
        var value = BitConverter.Int64BitsToDouble(0x3FD5555555555555);
        var sample = new TelemetrySample
        {
            Sequence = 7,
            HostElapsedSeconds = value,
            SimulationTimeSeconds = 10.0,
        };
        var line = TelemetryCsv.Format(sample);
        Equal(true, TelemetryCsv.TryParse(line, out var parsed), "round-trip row parse");
        Equal(
            BitConverter.DoubleToInt64Bits(value),
            BitConverter.DoubleToInt64Bits(parsed.HostElapsedSeconds),
            "binary64 round trip");

        var root = Path.Combine(Path.GetTempPath(), "landing-stats-csv-schema-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "truncated.csv");
            var truncated = string.Join(",", line.Split(',').Take(22));
            File.WriteAllText(path, TelemetryCsv.Header + Environment.NewLine + truncated + Environment.NewLine);
            var rejected = false;
            try
            {
                TelemetryCsv.ReadFile(path);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            Equal(true, rejected, "current header with legacy-width row is rejected");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void HeaderWindUsesContactTimeSample()
    {
        var record = new LandingRecord();
        record.Series.Add(new LandingSeriesPoint
        {
            TimeSeconds = -1,
            WindDirectionDegrees = 90,
            WindSpeedKnots = 5,
        });
        record.Series.Add(new LandingSeriesPoint
        {
            TimeSeconds = 0.02,
            WindDirectionDegrees = 275,
            WindSpeedKnots = 13.6,
        });

        Equal("wind 275°/14 kt", record.WindDisplay, "contact wind display");
    }

    private static void LandingHistoryUsesLazyColumnarDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-history-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(root);
            var record = new LandingRecord
            {
                Id = "columnar-test",
                TimestampUtc = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
                AircraftTitle = "Test aircraft",
                InertialFpm = 123,
                SurfaceFpm = double.NaN,
                WindSpeedKnotsAtContact = 12,
                WindDirectionDegreesAtContact = 240,
                RawControllerSourceIndices = new List<int> { 5 },
            };
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = -0.05,
                InertialFpm = 123,
                GForce = double.NaN,
                RawControllerYAxisPercent = new[] { 42.0 },
                RawControllerYAxisValid = new[] { true },
                RawControllerYAxisAgeSeconds = new[] { 0.0 },
            });
            repository.Save(record);
            using (var file = File.OpenRead(repository.IndexPath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            {
                var indexJson = reader.ReadToEnd();
                Equal(false, indexJson.Contains("NaN"), "summary index contains no NaN literal");
                Equal(false, indexJson.Contains("INF"), "summary index contains no infinity literal");
            }

            File.Delete(repository.IndexPath);
            var reloaded = new LandingRepository(root);
            var summary = reloaded.LoadAll().Single();
            Equal(true, summary.IsSummaryOnly, "history row is summary-only");
            Equal(0, summary.Series.Count, "summary has no detail series");
            AssertNoStorageSentinel(summary);
            var detail = reloaded.LoadDetail(summary) ?? throw new InvalidOperationException("detail did not load");
            Equal(false, detail.IsSummaryOnly, "selected record is a detail");
            Equal(1, detail.Series.Count, "columnar series point count");
            Equal(123.0, detail.Series[0].InertialFpm, "columnar inertial value");
            Equal(true, double.IsNaN(detail.SurfaceFpm), "summary NaN round-trip");
            Equal(true, double.IsNaN(detail.Series[0].GForce), "column NaN round-trip");
            Equal(5, detail.RawControllerSourceIndices[0], "controller source mapping");
            AssertNoStorageSentinel(detail);

            var detailPath = Directory.EnumerateFiles(root, "*.landing.json.gz").Single();
            using (var file = File.OpenRead(detailPath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            {
                var json = reader.ReadToEnd();
                Equal(true, json.Contains("\"series\""), "columnar series object is present");
                Equal(false, json.Contains("\"TimeSeconds\""), "per-point property names are absent");
                Equal(false, json.Contains("NaN"), "v7 JSON contains no NaN literal");
                Equal(false, json.Contains("INF"), "v7 JSON contains no infinity literal");
            }

            var legacy = new LandingRecord
            {
                FormatVersion = 6,
                Id = "legacy-test",
                TimestampUtc = new DateTime(2026, 8, 4, 11, 0, 0, DateTimeKind.Utc),
            };
            legacy.Series.Add(new LandingSeriesPoint { TimeSeconds = 0, InertialFpm = 321 });
            reloaded.Save(legacy);
            File.Delete(reloaded.IndexPath);
            var rebuilt = new LandingRepository(root);
            var legacySummary = rebuilt.LoadAll().Single(item => item.Id == legacy.Id);
            var legacyDetail = rebuilt.LoadDetail(legacySummary) ?? throw new InvalidOperationException("legacy detail did not load");
            Equal(321.0, legacyDetail.Series[0].InertialFpm, "legacy v6 detail remains readable");

            var columnsType = typeof(MainWindow).Assembly.GetType("LandingStats.App.Storage.LandingSeriesColumns", true)
                              ?? throw new TypeLoadException("LandingSeriesColumns");
            var columns = Activator.CreateInstance(columnsType, true) ?? throw new InvalidOperationException("columns construction failed");
            columnsType.GetProperty("Time")!.SetValue(columns, new double[1]);
            var rejected = false;
            try
            {
                columnsType.GetMethod("Validate")!.Invoke(columns, new object[] { 0 });
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
                rejected = true;
            }
            Equal(true, rejected, "mismatched column lengths are rejected");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void LandingIndexReconcilesCommittedDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-index-reconcile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new LandingRepository(root);
            var first = new LandingRecord { TimestampUtc = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc) };
            repository.Save(first);
            var staleIndex = File.ReadAllBytes(repository.IndexPath);

            var second = new LandingRecord { TimestampUtc = first.TimestampUtc.AddMinutes(1) };
            repository.Save(second);
            File.WriteAllBytes(repository.IndexPath, staleIndex);

            var recovered = new LandingRepository(root).LoadAll();
            Equal(2, recovered.Count, "reconciled landing count");
            Equal(true, recovered.Any(record => record.Id == second.Id), "detail missing from stale index is recovered");
            Equal(2, new LandingRepository(root).LoadAll().Count, "reconciled index is persisted");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void LandingFilenamesAreCultureInvariant()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-culture-path-" + Guid.NewGuid().ToString("N"));
        var previousCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var record = new LandingRecord
            {
                TimestampUtc = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc),
            };
            var path = new LandingRepository(root).Save(record);
            Equal(true, Path.GetFileName(path).StartsWith("20260805-010203Z-", StringComparison.Ordinal), "invariant UTC filename");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previousCulture;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void AirportCacheSurvivesTransientReadFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-airport-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "airports.json.gz");
        try
        {
            var repository = new AirportFacilityRepository(path);
            repository.MergeAndSave(new[]
            {
                new AirportFacility { Ident = "LBSF", Region = "BG", LatitudeDegrees = 42.7, LongitudeDegrees = 23.4 },
            });

            var rejected = false;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    repository.MergeAndSave(new[]
                    {
                        new AirportFacility { Ident = "LBPD", Region = "BG", LatitudeDegrees = 42.1, LongitudeDegrees = 24.7 },
                    });
                }
                catch (IOException)
                {
                    rejected = true;
                }
            }

            Equal(true, rejected, "transient cache read failure is propagated");
            var restored = repository.Load();
            Equal(1, restored.Count, "existing airport count after failed merge");
            Equal("LBSF", restored[0].Ident, "existing airport survives failed merge");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void ChartRangesDiscardNonFiniteTelemetry()
    {
        var finite = ChartValueSanitizer.FiniteValues(new[]
        {
            double.NaN,
            12.5,
            double.PositiveInfinity,
            -3.0,
            double.NegativeInfinity,
        });
        Equal(2, finite.Length, "finite chart value count");
        Near(12.5, finite[0], 0, "first finite chart value");
        Near(-3.0, finite[1], 0, "second finite chart value");
    }

    private static void StoredControllerColumnsAreCompact()
    {
        var samples = new List<TelemetrySample>();
        for (var index = 0; index < 32; index++)
        {
            var time = -1.55 + index * 0.05;
            var sample = new TelemetrySample
            {
                SimulationTimeSeconds = time,
                OnGround = false,
                AboveGroundLevelFeet = Math.Max(0, -time * 100),
                PilotPitchInputPercent = index * 2,
            };
            sample.RawControllerYAxisValid[5] = true;
            sample.RawControllerYAxisPercent[5] = index * 2;
            samples.Add(sample);
        }

        var result = new TouchdownResult { ContactNumber = 1, EstimatedContactTimeSeconds = 0 };
        var record = LandingRecordFactory.Create(result, samples, "Test", "TEST");
        Equal(1, record.RawControllerSourceIndices.Count, "stored controller column count");
        Equal(5, record.RawControllerSourceIndices[0], "original controller source index");
        Equal(1, record.Series[0].RawControllerYAxisPercent.Length, "per-point controller width");
        Equal(5, record.RawPitchInputSourceIndex, "pitch selector retains original source index");
    }

    private static void ClosureReconstructionSurvivesHistoryRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-reconstruction-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new TouchdownResult
            {
                ContactNumber = 1,
                EstimatedContactTimeSeconds = 0,
                ClosureReconstructionModel = "quad250-tc-minus-75ms-pitch-v1",
                ClosureReconstructionAvailable = true,
                ReconstructedClosureFpm = 300.0,
                ReconstructedInertialFpm = 270.0,
                ReconstructedTerrainFpm = 20.0,
                ReconstructedPitchFpm = 10.0,
                ClosureReconstructionResidualFpm = 7.0,
                ClosureReconstructionUncertaintyFpm = 10.0,
                ClosureReconstructionFitPointCount = 7,
                ClosureReconstructionLongitudinalArmFeet = -7.5,
                ClosureReconstructionGeometryQuality = 0.84,
                ClosureReconstructionArmRecoveredFromTelemetry = true,
            };
            var record = LandingRecordFactory.Create(result, Array.Empty<TelemetrySample>(), "Test", "TEST");

            Equal(true, record.HasClosureReconstruction, "factory reconstruction availability");
            Equal(result.ClosureReconstructionModel, record.ClosureReconstructionModel, "factory model id");
            Near(result.ReconstructedClosureFpm, record.ReconstructedClosureFpm, 1e-12, "factory modeled closure");
            Equal(true, record.ClosureGeometryDisplay.Contains("telemetry"), "telemetry geometry provenance display");

            var repository = new LandingRepository(root);
            repository.Save(record);
            var summary = repository.LoadAll().Single();
            Equal(true, summary.HasClosureReconstruction, "summary reconstruction availability");
            Near(7.0, summary.ClosureReconstructionResidualFpm, 1e-12, "summary residual");
            Near(0.84, summary.ClosureReconstructionGeometryQuality, 1e-12, "summary geometry quality");
            AssertNoStorageSentinel(summary);

            var detail = repository.LoadDetail(summary) ?? throw new InvalidOperationException("reconstruction detail did not load");
            Equal(result.ClosureReconstructionModel, detail.ClosureReconstructionModel, "detail model id");
            Near(300.0, detail.ReconstructedClosureFpm, 1e-12, "detail modeled closure");
            Near(270.0, detail.ReconstructedInertialFpm, 1e-12, "detail inertial component");
            Near(20.0, detail.ReconstructedTerrainFpm, 1e-12, "detail terrain component");
            Near(10.0, detail.ReconstructedPitchFpm, 1e-12, "detail pitch component");
            Near(10.0, detail.ClosureReconstructionUncertaintyFpm, 1e-12, "detail uncertainty");
            Equal(7, detail.ClosureReconstructionFitPointCount, "detail fit point count");
            Near(-7.5, detail.ClosureReconstructionLongitudinalArmFeet, 1e-12, "detail longitudinal arm");
            Equal(true, detail.ClosureReconstructionArmRecoveredFromTelemetry, "detail arm provenance");
            AssertNoStorageSentinel(detail);

            var legacyPath = Path.Combine(root, "20260804-000000Z-legacy-missing-reconstruction.landing.json.gz");
            using (var file = File.Create(legacyPath))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal, false))
            using (var writer = new StreamWriter(gzip))
            {
                writer.Write("{\"layout\":7,\"summary\":{\"FormatVersion\":7,\"Id\":\"legacy-missing-reconstruction\",\"TimestampUtc\":\"\\/Date(0)\\/\"}}");
            }

            File.Delete(repository.IndexPath);
            var rebuilt = new LandingRepository(root);
            var legacy = rebuilt.LoadAll().Single(item => item.Id == "legacy-missing-reconstruction");
            Equal(false, legacy.HasClosureReconstruction, "legacy reconstruction availability");
            Equal("reconstruction unavailable", legacy.ClosureModelDisplay, "legacy reconstruction display");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void ClosureReconstructionKeepsRawLatchIndependent()
    {
        const double armFeet = -8.0;
        const double contactTime = 0.010;
        const double evaluationOffset = -0.075;
        var expectedInertial = -Velocity(evaluationOffset) * 60.0;
        var expectedTerrain = GroundDerivative(evaluationOffset) * 60.0;
        var expectedPitch = PitchRate(evaluationOffset) * armFeet *
                            Math.Cos(PitchRadians(evaluationOffset)) *
                            Math.Cos(BankRadians(evaluationOffset)) * 60.0;
        var expectedClosure = expectedInertial + expectedTerrain + expectedPitch;
        var rawLatch = expectedClosure + 7.0;
        var samples = ReconstructionSamples(contactTime, rawLatch);

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, result.ContactTimeEstimatedFromCompression, "compression contact time");
        Near(contactTime, result.EstimatedContactTimeSeconds, 1e-9, "estimated contact time");
        Equal(true, result.ClosureReconstructionAvailable, "reconstruction availability");
        Equal("quad250-tc-minus-75ms-pitch-v1", result.ClosureReconstructionModel, "frozen model id");
        Near(expectedInertial, result.ReconstructedInertialFpm, 1e-8, "reconstructed inertial component");
        Near(expectedTerrain, result.ReconstructedTerrainFpm, 1e-8, "reconstructed terrain component");
        Near(expectedPitch, result.ReconstructedPitchFpm, 1e-8, "reconstructed pitch component");
        Near(expectedClosure, result.ReconstructedClosureFpm, 1e-8, "reconstructed closure");
        Near(rawLatch, result.LatchedNormalFpm, 1e-8, "raw simulator latch");
        Near(7.0, result.ClosureReconstructionResidualFpm, 1e-8, "model residual");
        Near(10.0, result.ClosureReconstructionUncertaintyFpm, 1e-8, "primary uncertainty");
        Near(armFeet, result.ClosureReconstructionLongitudinalArmFeet, 1e-8, "passport arm");
        Equal(true, double.IsNaN(result.ClosureReconstructionGeometryQuality), "passport geometry quality");
        Equal(false, result.ClosureReconstructionArmRecoveredFromTelemetry, "passport arm provenance");

        var changedLatch = TouchdownAnalysis.Analyze(
            ReconstructionSamples(contactTime, rawLatch + 100.0),
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();
        Near(result.ReconstructedClosureFpm, changedLatch.ReconstructedClosureFpm, 1e-10, "target-independent model");
        Near(rawLatch + 100.0, changedLatch.LatchedNormalFpm, 1e-8, "changed raw latch");

        var nonzeroOmegaY = ReconstructionSamples(contactTime, rawLatch);
        foreach (var sample in nonzeroOmegaY)
        {
            sample.RotationVelocityBodyYRadiansPerSecond = 0.75;
        }

        var frozenWithoutYawBank = TouchdownAnalysis.Analyze(
            nonzeroOmegaY,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();
        Near(
            result.ReconstructedClosureFpm,
            frozenWithoutYawBank.ReconstructedClosureFpm,
            1e-10,
            "v1 deliberately omits omega-Y yaw-bank term");
    }

    private static void ClosureReconstructionRequiresGeometry()
    {
        var samples = ReconstructionSamples(0.010, 280.0);
        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "reconstruction without an arm");
        Equal(true, double.IsNaN(result.ReconstructedClosureFpm), "unavailable modeled closure");
        Near(280.0, result.LatchedNormalFpm, 1e-8, "raw latch remains available");
    }

    private static void ClosureReconstructionRequiresFiveFitPoints()
    {
        const double armFeet = -8.0;
        var options = new TouchdownAnalysisOptions
        {
            LongitudinalMainGearArmFeet = armFeet,
            RecoverLongitudinalMainGearArmFromTelemetry = false,
        };

        var fourPoint = TouchdownAnalysis.Analyze(
            SparseReconstructionSamples(4),
            options).Single();
        Equal(false, fourPoint.ClosureReconstructionAvailable, "four-point reconstruction availability");
        Equal(0, fourPoint.ClosureReconstructionFitPointCount, "four-point fit count");

        var fivePoint = TouchdownAnalysis.Analyze(
            SparseReconstructionSamples(5),
            options).Single();
        Equal(true, fivePoint.ClosureReconstructionAvailable, "five-point reconstruction availability");
        Equal(5, fivePoint.ClosureReconstructionFitPointCount, "five-point fit count");
    }

    private static void ClosureReconstructionRequiresDistinctTimestamps()
    {
        var samples = SparseReconstructionSamples(3);
        var airborne = samples.Where(sample => !sample.OnGround).ToArray();
        samples.Insert(1, CloneTelemetrySample(airborne[0]));
        samples.Insert(3, CloneTelemetrySample(airborne[1]));
        samples.Insert(5, CloneTelemetrySample(airborne[2]));

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "duplicate timestamps cannot satisfy fit minimum");
    }

    private static void ClosureReconstructionRequiresEvaluationBracket()
    {
        var samples = DenseShortHistoryReconstructionSamples();
        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "tc-75 ms outside sampled history");
    }

    private static void ClosureReconstructionSanitizesEarlyGroundSpike()
    {
        const double armFeet = -8.0;
        var baselineSamples = ReconstructionSamples(0.010, 280.0).Skip(4).ToList();
        var baseline = TouchdownAnalysis.Analyze(
            baselineSamples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        var spikedSamples = ReconstructionSamples(0.010, 280.0).Skip(4).ToList();
        spikedSamples[1].GroundAltitudeFeet += 10.0;
        var repaired = TouchdownAnalysis.Analyze(
            spikedSamples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, repaired.ClosureReconstructionAvailable, "early-spike reconstruction availability");
        Near(baseline.ReconstructedTerrainFpm, repaired.ReconstructedTerrainFpm, 0.05, "early ground spike repair");
    }

    private static void ClosureReconstructionMarksContactTimeFallback()
    {
        const double armFeet = -8.0;
        var samples = ReconstructionSamples(0.010, 280.0);
        foreach (var sample in samples)
        {
            Array.Clear(sample.ContactPointCompression, 0, sample.ContactPointCompression.Length);
        }

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ContactTimeEstimatedFromCompression, "last-air contact-time provenance");
        Equal(true, result.ClosureReconstructionAvailable, "fallback reconstruction availability");
        Near(15.0, result.ClosureReconstructionUncertaintyFpm, 1e-8, "fallback uncertainty");
    }

    private static void ClosureReconstructionAcceptsPermutedStaggeredMains()
    {
        const double armFeet = -8.0;
        var samples = ReconstructionSamples(
            0.010,
            280.0,
            firstMainIndex: 17,
            secondMainIndex: 4,
            noseIndex: 11,
            secondMainDelaySeconds: 2.50,
            noseContactTime: 4.80,
            sampleEndTime: 5.20);

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, result.ClosureReconstructionAvailable, "permuted staggered-main reconstruction");
        Near(armFeet, result.ClosureReconstructionLongitudinalArmFeet, 1e-12, "permuted topology arm");
    }

    private static void ClosureReconstructionAcceptsCenterMainConservatively()
    {
        var samples = ReconstructionSamples(0.010, 280.0);
        AddSettledMainPoint(samples, 7);

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(true, result.ClosureReconstructionAvailable, "four-point reconstruction availability");
        Near(15.0, result.ClosureReconstructionUncertaintyFpm, 1e-12, "center-main uncertainty");
    }

    private static void ClosureReconstructionRejectsFivePointTopology()
    {
        var samples = ReconstructionSamples(0.010, 280.0);
        AddSettledMainPoint(samples, 7);
        AddSettledMainPoint(samples, 8);

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "five-point reconstruction availability");
        Near(280.0, result.LatchedNormalFpm, 1e-8, "five-point raw latch fallback");
    }

    private static void AddSettledMainPoint(IReadOnlyList<TelemetrySample> samples, int point)
    {
        foreach (var sample in samples)
        {
            if (!sample.OnGround)
            {
                continue;
            }

            sample.ContactPointOnGround[point] = true;
            sample.ContactPointCompression[point] = (sample.SimulationTimeSeconds - 0.010) * 100.0;
        }
    }

    private static void ClosureReconstructionKeepsMainGearBounces()
    {
        const double armFeet = -8.0;
        var samples = ReconstructionSamples(0.010, 280.0, noseContactTime: 0.575);
        foreach (var sample in samples)
        {
            var time = sample.SimulationTimeSeconds;
            var firstMainContact = time >= 0.025 - 1e-9 && time <= 0.100 + 1e-9;
            var settledMainContact = time >= 0.275 - 1e-9;
            var mainContact = firstMainContact || settledMainContact;
            sample.OnGround = mainContact;
            sample.TouchdownNormalVelocityFps = mainContact ? 280.0 / 60.0 : 0.0;
            Array.Clear(sample.ContactPointOnGround, 0, sample.ContactPointOnGround.Length);
            Array.Clear(sample.ContactPointCompression, 0, sample.ContactPointCompression.Length);
            if (mainContact)
            {
                sample.ContactPointOnGround[1] = true;
                sample.ContactPointOnGround[2] = true;
                var crossing = firstMainContact ? 0.010 : 0.260;
                sample.ContactPointCompression[1] = (time - crossing) * 100.0;
                sample.ContactPointCompression[2] = (time - crossing) * 100.0;
            }

            if (time >= 0.575 - 1e-9)
            {
                sample.ContactPointOnGround[0] = true;
            }
        }

        var results = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = armFeet,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            });

        Equal(2, results.Count, "main-bounce contact count");
        Equal(true, results[0].ClosureReconstructionAvailable, "first main contact reconstruction");
        Equal(true, results[1].ClosureReconstructionAvailable, "main recontact reconstruction");
        Near(15.0, results[0].ClosureReconstructionUncertaintyFpm, 1e-12, "pre-bounce uncertainty");
        Near(15.0, results[1].ClosureReconstructionUncertaintyFpm, 1e-12, "recontact uncertainty");
    }

    private static void ClosureReconstructionRejectsNoseFirstContact()
    {
        const double rawLatchFpm = 280.0;
        var samples = ReconstructionSamples(0.010, rawLatchFpm);
        foreach (var sample in samples)
        {
            var time = sample.SimulationTimeSeconds;
            Array.Clear(sample.ContactPointOnGround, 0, sample.ContactPointOnGround.Length);
            Array.Clear(sample.ContactPointCompression, 0, sample.ContactPointCompression.Length);
            if (!sample.OnGround)
            {
                continue;
            }

            sample.ContactPointOnGround[11] = true;
            sample.ContactPointCompression[11] = (time - 0.010) * 100.0;
            if (time >= 0.350 - 1e-9)
            {
                sample.ContactPointOnGround[4] = true;
                sample.ContactPointOnGround[17] = true;
                sample.ContactPointCompression[4] = (time - 0.335) * 100.0;
                sample.ContactPointCompression[17] = (time - 0.335) * 100.0;
            }
        }

        var legacy = TouchdownAnalysis.Analyze(samples).Single();
        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "nose-first reconstruction availability");
        Near(rawLatchFpm, result.LatchedNormalFpm, 1e-8, "nose-first raw latch");
        Near(legacy.LatchedNormalFpm, result.LatchedNormalFpm, 1e-12, "raw latch independent of topology gate");
        Near(legacy.InertialVerticalFpm, result.InertialVerticalFpm, 1e-12, "inertial metric independent of topology gate");
        Near(legacy.PeakG, result.PeakG, 1e-12, "peak G independent of topology gate");
    }

    private static void ClosureReconstructionRejectsMixedMainNoseContact()
    {
        const double rawLatchFpm = 280.0;
        var samples = ReconstructionSamples(0.010, rawLatchFpm);
        foreach (var sample in samples)
        {
            var time = sample.SimulationTimeSeconds;
            if (time >= 0.050 - 1e-9 && time <= 0.100 + 1e-9)
            {
                sample.ContactPointOnGround[0] = true;
                sample.ContactPointCompression[0] = (time - 0.035) * 100.0;
            }
        }

        var result = TouchdownAnalysis.Analyze(
            samples,
            new TouchdownAnalysisOptions
            {
                LongitudinalMainGearArmFeet = -8.0,
                RecoverLongitudinalMainGearArmFromTelemetry = false,
            }).Single();

        Equal(false, result.ClosureReconstructionAvailable, "mixed main-nose reconstruction availability");
        Near(rawLatchFpm, result.LatchedNormalFpm, 1e-8, "mixed main-nose raw latch fallback");
    }

    private static void TelemetryGeometryRecoversAnalyticArm()
    {
        const double pitchArmFeet = -12.0;
        const double datumOffsetFeet = 4.0;
        const double expectedArmFeet = pitchArmFeet + datumOffsetFeet;
        const double groundAltitudeFeet = 100.0;
        const double baseHeightFeet = 14.0;
        const double compressionCoefficient = -0.02;
        const double step = 0.025;
        const int firstMainIndex = 17;
        const int secondMainIndex = 4;
        const int noseIndex = 11;
        var samples = new List<TelemetrySample>();
        var integratedPlane = 0.0;
        var integratedPlaneAtContact = double.NaN;
        var previousVertical = 0.0;

        for (var index = 0; index <= 200; index++)
        {
            var time = -4.0 + index * step;
            var onGround = time >= -1e-9;
            var pitch = onGround ? 0.05 + 0.02 * Math.Sin(5.0 * time) + 0.05 * time : 0.0;
            var compression = onGround ? 20.0 + 5.0 * Math.Cos(7.0 * time) : 0.0;
            var omegaX = 0.01 + 0.004 * Math.Sin(1.7 * time) + 0.001 * time;
            var worldY = -4.0 + 0.15 * Math.Sin(0.9 * time);
            var rigidVerticalPerFoot = -omegaX;
            var verticalAtDatum = worldY + datumOffsetFeet * rigidVerticalPerFoot;
            if (index > 0)
            {
                integratedPlane += 0.5 * step * (previousVertical + verticalAtDatum);
            }

            previousVertical = verticalAtDatum;
            var sample = new TelemetrySample
            {
                Sequence = index,
                SimulationTimeSeconds = time,
                OnGround = onGround,
                VelocityWorldYFps = worldY,
                RotationVelocityBodyXRadiansPerSecond = omegaX,
                PitchDegrees = pitch * 180.0 / Math.PI,
                GroundAltitudeFeet = groundAltitudeFeet,
                PlaneAltitudeFeet = integratedPlane,
            };
            if (onGround)
            {
                if (double.IsNaN(integratedPlaneAtContact))
                {
                    integratedPlaneAtContact = integratedPlane;
                }

                sample.ContactPointOnGround[noseIndex] = time >= 0.60;
                sample.ContactPointOnGround[firstMainIndex] = true;
                sample.ContactPointOnGround[secondMainIndex] = true;
                sample.ContactPointCompression[firstMainIndex] = compression;
                sample.ContactPointCompression[secondMainIndex] = compression;
                sample.PlaneAltitudeFeet = groundAltitudeFeet + baseHeightFeet +
                                           pitchArmFeet * pitch +
                                           compressionCoefficient * compression;
            }

            samples.Add(sample);
        }

        var contactIndex = samples.FindIndex(sample => sample.OnGround);
        var contactPlane = samples[contactIndex].PlaneAltitudeFeet;
        var airborneShift = contactPlane - integratedPlaneAtContact;
        for (var index = 0; index < contactIndex; index++)
        {
            samples[index].PlaneAltitudeFeet += airborneShift;
        }

        Equal(true, TelemetryGeometryCalibration.TryCalibrate(samples, out var calibration), "geometry calibration");
        Near(pitchArmFeet, calibration.PitchArmFeet, 1e-8, "pitch arm");
        Near(datumOffsetFeet, calibration.DatumOffsetFeet, 1e-8, "datum offset");
        Near(expectedArmFeet, calibration.LongitudinalArmFeet, 1e-8, "longitudinal arm");
        Equal(4, calibration.DatumPhaseCount, "datum phase count");
        Equal(true, calibration.Quality >= 0.20 && calibration.Quality <= 1.0, "geometry quality range");
    }

    private static List<TelemetrySample> ReconstructionSamples(
        double contactTime,
        double rawLatchFpm,
        int firstMainIndex = 1,
        int secondMainIndex = 2,
        int noseIndex = 0,
        double secondMainDelaySeconds = 0.0,
        double noseContactTime = 0.30,
        double sampleEndTime = 0.70)
    {
        var samples = new List<TelemetrySample>();
        var finalIndex = (int)Math.Ceiling((sampleEndTime + 0.30) / 0.025);
        for (var index = 0; index <= finalIndex; index++)
        {
            var time = -0.30 + index * 0.025;
            var x = time - contactTime;
            var onGround = time >= 0.025 - 1e-9;
            var pitchDegrees = PitchRadians(x) * 180.0 / Math.PI;
            if (onGround)
            {
                pitchDegrees += 2.5 * (time - 0.025);
            }

            var sample = new TelemetrySample
            {
                Sequence = index,
                SimulationTimeSeconds = time,
                OnGround = onGround,
                TouchdownNormalVelocityFps = onGround ? rawLatchFpm / 60.0 : 0.0,
                VelocityWorldYFps = Velocity(x),
                GroundAltitudeFeet = GroundAltitude(x),
                PlaneAltitudeFeet = GroundAltitude(x) + 10.0,
                RotationVelocityBodyXRadiansPerSecond = PitchRate(x),
                PitchDegrees = pitchDegrees,
                BankDegrees = BankRadians(x) * 180.0 / Math.PI,
            };
            if (onGround)
            {
                sample.ContactPointOnGround[firstMainIndex] = true;
                sample.ContactPointCompression[firstMainIndex] = (time - contactTime) * 100.0;
                if (time >= 0.025 + secondMainDelaySeconds - 1e-9)
                {
                    sample.ContactPointOnGround[secondMainIndex] = true;
                    sample.ContactPointCompression[secondMainIndex] =
                        (time - contactTime - secondMainDelaySeconds) * 100.0;
                }

                if (time >= noseContactTime - 1e-9)
                {
                    sample.ContactPointOnGround[noseIndex] = true;
                    sample.ContactPointCompression[noseIndex] = (time - noseContactTime) * 100.0;
                }
            }

            samples.Add(sample);
        }

        return samples;
    }

    private static List<TelemetrySample> DenseShortHistoryReconstructionSamples()
    {
        const double contactTime = 0.010;
        var samples = new List<TelemetrySample>();
        for (var index = 0; index < 5; index++)
        {
            var time = -0.050 + index * 0.0125;
            var x = time - contactTime;
            samples.Add(new TelemetrySample
            {
                Sequence = index,
                SimulationTimeSeconds = time,
                OnGround = false,
                VelocityWorldYFps = Velocity(x),
                GroundAltitudeFeet = GroundAltitude(x),
                PlaneAltitudeFeet = GroundAltitude(x) + 10.0,
                RotationVelocityBodyXRadiansPerSecond = PitchRate(x),
                PitchDegrees = PitchRadians(x) * 180.0 / Math.PI,
                BankDegrees = BankRadians(x) * 180.0 / Math.PI,
            });
        }

        var ground = ReconstructionSamples(contactTime, 280.0).Where(sample => sample.OnGround);
        samples.AddRange(ground);
        return samples;
    }

    private static TelemetrySample CloneTelemetrySample(TelemetrySample source)
    {
        return new TelemetrySample
        {
            Sequence = source.Sequence + 1000,
            HostElapsedSeconds = source.HostElapsedSeconds,
            SimulationTimeSeconds = source.SimulationTimeSeconds,
            OnGround = source.OnGround,
            VelocityWorldYFps = source.VelocityWorldYFps,
            GroundAltitudeFeet = source.GroundAltitudeFeet,
            PlaneAltitudeFeet = source.PlaneAltitudeFeet,
            RotationVelocityBodyXRadiansPerSecond = source.RotationVelocityBodyXRadiansPerSecond,
            PitchDegrees = source.PitchDegrees,
            BankDegrees = source.BankDegrees,
        };
    }

    private static List<TelemetrySample> SparseReconstructionSamples(int airbornePointCount)
    {
        var full = ReconstructionSamples(0.010, 280.0);
        var airborne = full.Where(sample => !sample.OnGround).ToList();
        var selected = airborne.Skip(Math.Max(0, airborne.Count - airbornePointCount));
        return selected.Concat(full.Where(sample => sample.OnGround)).ToList();
    }

    private static double Velocity(double x) => -4.0 + 2.0 * x + 3.0 * x * x;

    private static double GroundAltitude(double x) => 100.0 + 0.4 * x + 0.2 * x * x;

    private static double GroundDerivative(double x) => 0.4 + 0.4 * x;

    private static double PitchRate(double x) => 0.01 + 0.02 * x - 0.01 * x * x;

    private static double PitchRadians(double x) => 0.08 + 0.01 * x + 0.01 * x * x;

    private static double BankRadians(double x) => 0.02 - 0.005 * x;

    private static void ColumnarV7IsSmallerThanObjectLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-size-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var legacyRoot = Path.Combine(root, "v6");
            var columnarRoot = Path.Combine(root, "v7");
            var legacy = RepresentativeRecord(6, false);
            var columnar = RepresentativeRecord(7, true);
            var legacyPath = new LandingRepository(legacyRoot).Save(legacy);
            var columnarPath = new LandingRepository(columnarRoot).Save(columnar);
            var legacyBytes = new FileInfo(legacyPath).Length;
            var columnarBytes = new FileInfo(columnarPath).Length;
            if (columnarBytes >= legacyBytes)
            {
                throw new InvalidOperationException($"columnar detail is not smaller: v7={columnarBytes}, v6={legacyBytes}");
            }

            Console.WriteLine($"  storage sample: v6 {legacyBytes:N0} bytes, v7 {columnarBytes:N0} bytes ({100.0 * columnarBytes / legacyBytes:F1}%)");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void BounceHistoryShowsLatestContactFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-stats-bounce-order-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timestamp = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            var repository = new LandingRepository(root);
            repository.Save(new LandingRecord { Id = "contact-1", TimestampUtc = timestamp, ContactNumber = 1, ContactCount = 2 });
            repository.Save(new LandingRecord { Id = "contact-2", TimestampUtc = timestamp, ContactNumber = 2, ContactCount = 2 });
            var history = new LandingRepository(root).LoadAll();
            Equal(2, history[0].ContactNumber, "top history row contact number");
            Equal(1, history[1].ContactNumber, "second history row contact number");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static LandingRecord RepresentativeRecord(int formatVersion, bool compactControllers)
    {
        var record = new LandingRecord
        {
            FormatVersion = formatVersion,
            Id = "size-" + formatVersion,
            TimestampUtc = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
            AircraftTitle = "Representative aircraft",
        };
        if (compactControllers) record.RawControllerSourceIndices.Add(5);
        for (var index = 0; index < 600; index++)
        {
            var rawWidth = compactControllers ? 1 : TelemetrySample.CapturedControllerCount;
            var rawValues = new double[rawWidth];
            var rawValid = new bool[rawWidth];
            var rawAges = new double[rawWidth];
            rawValues[compactControllers ? 0 : 5] = Math.Sin(index * 0.1) * 30;
            rawValid[compactControllers ? 0 : 5] = true;
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = -15 + index * 0.05,
                InertialFpm = 200 + Math.Sin(index * 0.03) * 50,
                IndicatedFpm = 220 + Math.Sin(index * 0.02) * 45,
                GForce = 1 + Math.Sin(index * 0.05) * 0.1,
                AglFeet = Math.Max(0, 500 - index),
                PitchDegrees = 4 + Math.Sin(index * 0.01),
                PilotPitchPercent = Math.Sin(index * 0.1) * 30,
                WindSpeedKnots = 12,
                WindDirectionDegrees = 240,
                OnGround = index >= 300,
                RawControllerYAxisPercent = rawValues,
                RawControllerYAxisValid = rawValid,
                RawControllerYAxisAgeSeconds = rawAges,
            });
        }

        return record;
    }

    private static TelemetrySample ApproachSample(double timeSeconds)
    {
        return new TelemetrySample
        {
            SimulationTimeSeconds = timeSeconds,
            MotionSimulation = true,
            OnGround = false,
            AboveGroundLevelFeet = 400,
            VelocityWorldYFps = -5,
        };
    }

    private static IDisposable NewRecorder()
    {
        var recorder = Activator.CreateInstance(
            RecorderType(),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new object[] { IntPtr.Zero },
            null);
        return (IDisposable)(recorder ?? throw new InvalidOperationException("Recorder construction failed."));
    }

    private static Type RecorderType()
    {
        return typeof(MainWindow).Assembly.GetType(
                   "LandingStats.App.Telemetry.SimConnectLandingRecorder",
                   true)
               ?? throw new TypeLoadException("SimConnectLandingRecorder");
    }

    private static object? Field(object target, string name)
    {
        return target.GetType().GetField(name, InstancePrivate)?.GetValue(target)
               ?? (target.GetType().GetField(name, InstancePrivate) == null
                   ? throw new MissingFieldException(target.GetType().FullName, name)
                   : null);
    }

    private static object? RecorderProperty(object target, string name)
    {
        return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)
               ?? throw new MissingMemberException(target.GetType().FullName, name);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, InstancePrivate)
                    ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static void Invoke(object target, string name, params object[] arguments)
    {
        var method = target.GetType().GetMethod(name, InstancePrivate | BindingFlags.Public)
                     ?? throw new MissingMethodException(target.GetType().FullName, name);
        method.Invoke(target, arguments);
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failures++;
            var failure = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            Console.Error.WriteLine($"FAIL {name}: {failure}");
        }
    }

    private static void Equal<T>(T expected, object? actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, (T)actual!))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"{message}: expected {expected:R} ± {tolerance:R}, got {actual:R}");
        }
    }

    private static void Same(object expected, object? actual, string message)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: references differ");
        }
    }

    private static void NotSame(object first, object? second, string message)
    {
        if (ReferenceEquals(first, second))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Null(object? value, string message)
    {
        if (value != null)
        {
            throw new InvalidOperationException($"{message}: expected null");
        }
    }

    private static void NotNull(object? value, string message)
    {
        if (value == null)
        {
            throw new InvalidOperationException($"{message}: expected a value");
        }
    }

    private static void AssertNoStorageSentinel(object? value)
    {
        if (value == null || value is string || value is DateTime)
        {
            return;
        }

        if (value is double number)
        {
            if (number == LandingRecord.NonFiniteStorageSentinel)
            {
                throw new InvalidOperationException("storage sentinel leaked into a restored record");
            }

            return;
        }

        if (value is IEnumerable sequence)
        {
            foreach (var item in sequence)
            {
                AssertNoStorageSentinel(item);
            }

            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum)
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetCustomAttribute<DataMemberAttribute>() != null)
            {
                AssertNoStorageSentinel(property.GetValue(value));
            }
        }
    }

    private sealed class UpdateFixtureHandler : HttpMessageHandler
    {
        private readonly byte[] _manifest;
        private readonly byte[] _signature;

        public UpdateFixtureHandler(string manifest, string signature)
        {
            _manifest = Encoding.UTF8.GetBytes(manifest);
            _signature = Encoding.ASCII.GetBytes(signature + "\n");
        }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = request.RequestUri!.AbsolutePath.EndsWith("update-manifest.sig", StringComparison.Ordinal)
                ? _signature
                : _manifest;
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(bytes),
            });
        }
    }
}
