using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using LandingStats.App;
using LandingStats.App.Models;
using LandingStats.App.Storage;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class Program
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _failures;

    private static int Main()
    {
        Run("deduplicator keeps the last same-time frame", DeduplicatorKeepsLastFrame);
        Run("approach timeout re-arms capture", ApproachTimeoutRearmsCapture);
        Run("full telemetry gate has AGL hysteresis and RAW override", FullTelemetryGateHasHysteresis);
        Run("raw debug streams directly into a zip", RawDebugStreamsIntoZip);
        Run("valid frame clears transient error state", ValidFrameClearsTransientErrorState);
        Run("legacy SimConnect controller path is absent", LegacyControllerPathIsAbsent);
        Run("compact frame matches the SimConnect payload contract", CompactFrameMatchesPayloadContract);
        Run("telemetry schema v5 reads current and v4 rows", TelemetrySchemaReadsCurrentAndV4Rows);
        Run("header wind uses the contact-time sample", HeaderWindUsesContactTimeSample);
        Run("landing history uses lazy columnar v7 details", LandingHistoryUsesLazyColumnarDetails);
        Run("bounce history shows the latest contact first", BounceHistoryShowsLatestContactFirst);
        Run("columnar v7 is smaller than the object layout", ColumnarV7IsSmallerThanObjectLayout);
        Run("stored controller columns retain only live sources", StoredControllerColumnsAreCompact);

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
}
