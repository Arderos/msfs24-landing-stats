using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class BinaryTelemetryRegressionTests
{
    public static void RunAll()
    {
        // Independently packed BINARY64/1 vectors, fixed rather than derived by
        // the implementation under test. A new wire field must not alter them.
        Assert(TelemetryBinaryCodec.Fingerprint(Array.Empty<TelemetrySample>()) == "d4f2203be269bbaa016cc9ad6fab0d9540aec3f9dcd7505bfc887fc511dd61a8", "empty persisted fingerprint drifted");
        Assert(TelemetryBinaryCodec.Fingerprint(new[] { new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 } }) ==
            "6d53d164c505c363ee93d00011328d3693b71bd277dcd42256e4a9ddfd085b93", "persisted BINARY64/1 projection drifted");
        Assert(TelemetryBinaryCodec.Fingerprint(new[] { PersistedProjectionVector() }) ==
            "327a0ae355369286198110d62d9275884a224222d0c6bcfa9d8545aed4ae6a55", "persisted nonzero field order drifted");
        var legacyGolden = new TelemetrySample { Sequence = 1, SimulationTimeSeconds = 1 };
        const string legacyHash = "2bd2015df6b2f86135156154aca92da049a64435b97c1ee87b2608daf9b77f05";
        Assert(Sha(Encoding.UTF8.GetBytes(GeometryFingerprintCsvV7.Format(legacyGolden) + "\n")) == legacyHash, "persisted CSV v7 projection drifted");
        var legacySnapshot = AnalysisGeometrySnapshot.Capture(new[] { legacyGolden }, new TouchdownAnalysisOptions(), "golden");
        legacySnapshot.Version = 1; legacySnapshot.InputFingerprintFormat = "telemetry_csv_v7"; legacySnapshot.InputSha256 = legacyHash;
        legacySnapshot.Restore(new[] { legacyGolden });
        for (var seed = 0; seed < 32; seed++)
        {
            var sample = Vector(seed);
            var encoded = TelemetryBinaryCodec.Encode(sample);
            Assert(encoded.Length == TelemetryBinaryCodec.EncodedSize, "variable telemetry length");
            var restored = TelemetryBinaryCodec.Decode(encoded);
            Equal(sample, restored);
            Assert(encoded.SequenceEqual(TelemetryBinaryCodec.Encode(restored)), "binary roundtrip changed bytes");
            restored.RawControllerAxesPercent[0] = 1000;
            Assert(Bits(sample.RawControllerAxesPercent[0]) != Bits(restored.RawControllerAxesPercent[0]), "shared source array");
            Reject(() => TelemetryBinaryCodec.Decode(encoded.Take(encoded.Length - 1).ToArray()));
            Reject(() => TelemetryBinaryCodec.Decode(encoded.Concat(new byte[] { 0 }).ToArray()));
            encoded[0]++;
            Reject(() => TelemetryBinaryCodec.Decode(encoded));
        }

        var binding = new[] { Vector(8), Vector(9) };
        binding[0].SimulationTimeSeconds = 10;
        binding[1].SimulationTimeSeconds = 11;
        var snapshot = AnalysisGeometrySnapshot.Capture(binding, new TouchdownAnalysisOptions(), "portable_fixture");
        Assert(snapshot.Version == 2 && snapshot.InputFingerprintFormat == TelemetryBinaryCodec.FingerprintFormat, "new version missing");
        snapshot.Restore(binding);
        var saved = binding[1].VelocityWorldYFps;
        binding[1].VelocityWorldYFps = BitConverter.Int64BitsToDouble(Bits(saved) ^ 1);
        Reject(() => snapshot.Restore(binding));
        binding[1].VelocityWorldYFps = saved;

        // Keep the strict v1 reader: neither a wrong hash nor a mislabeled v2 is accepted.
        snapshot.Version = 1;
        snapshot.InputFingerprintFormat = "telemetry_csv_v7";
        snapshot.InputSha256 = Sha(Encoding.UTF8.GetBytes(string.Concat(binding.Select(s => TelemetryCsv.Format(s) + "\n"))));
        snapshot.Restore(binding);
        snapshot.InputSha256 = TelemetryBinaryCodec.Fingerprint(binding);
        Reject(() => snapshot.Restore(binding));
        snapshot.InputFingerprintFormat = TelemetryBinaryCodec.FingerprintFormat;
        Reject(snapshot.Validate);

        var zero = new TelemetrySample { SimulationTimeSeconds = 1 };
        var negativeZero = TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(zero));
        negativeZero.GForce = BitConverter.Int64BitsToDouble(long.MinValue);
        negativeZero.AnalysisTimeSeconds = BitConverter.Int64BitsToDouble(0x7ff8000000000001);
        Assert(TelemetryBinaryCodec.Fingerprint(new[] { zero }) == TelemetryBinaryCodec.Fingerprint(new[] { negativeZero }), "CSV numeric equivalence changed");
        negativeZero.GForce = double.Epsilon;
        Assert(TelemetryBinaryCodec.Fingerprint(new[] { zero }) != TelemetryBinaryCodec.Fingerprint(new[] { negativeZero }), "tiny finite change ignored");

        var axis = Vector(5);
        var projection = TelemetryBinaryCodec.Decode(TelemetryBinaryCodec.Encode(axis));
        projection.RawControllerAxesPercent[1] = 1234;
        projection.RawControllerAxesAgeSeconds[1] = 5678;
        projection.RawControllerAxesValid[1] = !projection.RawControllerAxesValid[1];
        Assert(TelemetryBinaryCodec.Fingerprint(new[] { axis }) == TelemetryBinaryCodec.Fingerprint(new[] { projection }), "duplicate non-CSV Y array changed projection");
        projection.RawControllerYAxisPercent[0] = 1234;
        Assert(TelemetryBinaryCodec.Fingerprint(new[] { axis }) != TelemetryBinaryCodec.Fingerprint(new[] { projection }), "persisted Y ignored");

        var culture = CultureInfo.CurrentCulture;
        try
        {
            var before = GoldenFingerprint();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert(before == GoldenFingerprint(), "culture-dependent hash");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static TelemetrySample PersistedProjectionVector()
    {
        var sample = new TelemetrySample
        {
            Sequence = 0x0102030405060708, HostElapsedSeconds = .125, SimulationTimeSeconds = 123.5,
            SimulationDeltaSeconds = .125, AnalysisTimeSeconds = 123.5, SimulatorState = TelemetrySimulatorState.Running,
            PauseFlags = 4, FlightSegment = 7, OnGround = true, MotionSimulation = true,
            TouchdownNormalVelocityFps = 1.25,
            VerticalSpeedFps = 2.25,
            VelocityWorldYFps = 3.25,
            GForce = 4.25,
            AboveGroundLevelFeet = 5.25,
            PitchDegrees = 6.25,
            BankDegrees = 7.25,
            LatitudeDegrees = 8.25,
            LongitudeDegrees = 9.25,
            IndicatedAirspeedKnots = 10.25,
            GroundSpeedKnots = 11.25,
            PlaneAltitudeFeet = 12.25,
            GroundAltitudeFeet = 13.25,
            RotationVelocityBodyXRadiansPerSecond = 14.25,
            RotationVelocityBodyYRadiansPerSecond = 15.25,
            RotationVelocityBodyZRadiansPerSecond = 16.25,
            AccelerationBodyXFps2 = 17.25,
            AccelerationBodyZFps2 = 18.25,
            HeadingTrueDegrees = 19.25,
            AngleOfAttackDegrees = 20.25,
            SideslipDegrees = 21.25,
            AmbientWindVelocityKnots = 22.25,
            AmbientWindDirectionDegrees = 23.25,
            ElevatorPosition = 24.25,
            ElevatorTrimRadians = 25.25,
            AileronPosition = 26.25,
            RudderPosition = 27.25,
            ElevatorDeflectionPercentOver100 = 28.25,
            AileronLeftDeflectionPercentOver100 = 29.25,
            AileronRightDeflectionPercentOver100 = 30.25,
            RudderDeflectionPercentOver100 = 31.25,
            SpoilersLeftPosition = 32.25,
            SpoilersRightPosition = 33.25,
            FlapsLeftPercent = 34.25,
            FlapsRightPercent = 35.25,
            BrakeLeftPosition = 36.25,
            BrakeRightPosition = 37.25,
            TotalWeightPounds = 38.25,
            CgPercent = 39.25,
            NumberOfEngines = 2, PilotRollInputPercent = 50.25, PilotPitchInputPercent = 51.25, RudderPedalInputPercent = 52.25,
            AxisElevatorSetPercent = 53.25, AxisElevatorSetValid = true, AxisElevatorSetAgeSeconds = 54.25,
        };
        for (var i = 0; i < 4; i++)
        { sample.EngineThrottlePercent[i] = 60 + i * .25; sample.EngineN1Percent[i] = 70 + i * .25;
            sample.EngineRpm[i] = 80 + i * .25; sample.EngineReversePercent[i] = 90 + i * .25; }
        for (var i = 0; i < 8; i++)
        { sample.RawControllerDeviceId[i] = i; sample.RawControllerYAxisPercent[i] = 100 + i * .25;
            sample.RawControllerYAxisValid[i] = i % 2 == 0; sample.RawControllerYAxisAgeSeconds[i] = 110 + i * .25; }
        for (var i = 0; i < 48; i++)
        { sample.RawControllerAxesPercent[i] = 200 + i * .25; sample.RawControllerAxesValid[i] = i % 2 == 0;
            sample.RawControllerAxesAgeSeconds[i] = 300 + i * .25; }
        for (var i = 0; i < 20; i++)
        { sample.ContactPointCompression[i] = 10 + i * .25; sample.ContactPointPosition[i] = 20 + i * .25; sample.ContactPointOnGround[i] = i % 2 == 0; }
        return sample;
    }

    public static string GoldenFingerprint() => TelemetryBinaryCodec.Fingerprint(Enumerable.Range(0, 32).Select(Vector).ToArray());

    private static readonly long[] Values =
    {
        0, long.MinValue, 1, unchecked((long)0x8000000000000001),
        0x0010000000000000, 0x7fefffffffffffff, 0x7ff0000000000000,
        unchecked((long)0xfff0000000000000), 0x7ff8000000000001,
        unchecked((long)0xfff8000000001234), 0x3fb999999999999a, 0x4028b0a3d70a3d71,
        0x405edd2f1a9fbe77, 0x416b13114c000000, 0x3e45798ee2308c3a, 0x7ff0000000000001,
    };

    public static TelemetrySample Vector(int seed)
    {
        var sample = new TelemetrySample();
        var index = seed;
        foreach (var property in typeof(TelemetrySample).GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var type = property.PropertyType;
            if (property.Name == nameof(TelemetrySample.AnalysisDiscontinuity) || property.Name == nameof(TelemetrySample.ControllerCatalog) || property.Name == nameof(TelemetrySample.SourceEvidence)) continue;
            if (type.IsArray)
            {
                var array = (Array)property.GetValue(sample)!;
                for (var i = 0; i < array.Length; i++) array.SetValue(Value(type.GetElementType()!, index++), i);
            }
            else property.SetValue(sample, Value(type, index++));
        }
        sample.PauseFlags = -1;
        if (double.IsInfinity(sample.AnalysisTimeSeconds)) sample.AnalysisTimeSeconds = double.NaN;
        return sample;
    }

    private static object Value(Type type, int index)
    {
        if (type == typeof(double)) return BitConverter.Int64BitsToDouble(Values[index % Values.Length]);
        if (type == typeof(bool)) return index % 2 == 0;
        if (type == typeof(long)) return long.MaxValue - index;
        if (type == typeof(int)) return int.MinValue + index;
        if (type == typeof(TelemetrySimulatorState)) return (TelemetrySimulatorState)(index % 4);
        throw new InvalidOperationException("Uncovered telemetry property: " + type);
    }

    private static void Equal(TelemetrySample expected, TelemetrySample actual)
    {
        foreach (var property in typeof(TelemetrySample).GetProperties())
        {
            var a = property.GetValue(expected);
            var b = property.GetValue(actual);
            if (a is Array x && b is Array y)
            {
                Assert(x.Length == y.Length && !ReferenceEquals(x, y), property.Name + " array ownership");
                for (var i = 0; i < x.Length; i++) Assert(Same(x.GetValue(i), y.GetValue(i)), property.Name + "[" + i + "]");
            }
            else Assert(Same(a, b), property.Name);
        }
    }

    private static bool Same(object? a, object? b) => a is double x && b is double y ? Bits(x) == Bits(y) : Equals(a, b);
    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
    private static string Sha(byte[] value)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
    }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("invalid payload accepted"); }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
