using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using LandingStats.Core;

namespace LandingStats.App.Models;

/// <summary>Exact, path-free geometry inputs for one prepared physical segment.</summary>
[DataContract]
public sealed class AnalysisGeometrySnapshot : IExtensibleDataObject
{
    public ExtensionDataObject? ExtensionData { get; set; }
    [DataMember(Order = 1)] public int Version { get; set; } = 2;
    [DataMember(Order = 2)] public string CoordinateFrame { get; set; } = "velocity_reference_longitudinal_feet";
    [DataMember(Order = 3)] public long FirstSequence { get; set; }
    [DataMember(Order = 4)] public long LastSequence { get; set; }
    [DataMember(Order = 5)] public double FirstSimulationTime { get; set; }
    [DataMember(Order = 6)] public double LastSimulationTime { get; set; }
    [DataMember(Order = 7)] public int SampleCount { get; set; }
    [DataMember(Order = 8)] public string Source { get; set; } = "Unavailable";
    [DataMember(Order = 9, EmitDefaultValue = false)] public double? LongitudinalArmFeet { get; set; }
    [DataMember(Order = 10, EmitDefaultValue = false)] public double? Quality { get; set; }
    [DataMember(Order = 11)] public int[] MainIndices { get; set; } = Array.Empty<int>();
    [DataMember(Order = 12)] public double[] MainArmsFeet { get; set; } = Array.Empty<double>();
    [DataMember(Order = 13)] public int[] NoseIndices { get; set; } = Array.Empty<int>();
    [DataMember(Order = 14)] public bool RecoverFromTelemetry { get; set; }
    [DataMember(Order = 15)] public string ConfigOutcome { get; set; } = "unknown";
    [DataMember(Order = 16)] public string InputFingerprintFormat { get; set; } = TelemetryBinaryCodec.FingerprintFormat;
    [DataMember(Order = 17)] public string InputSha256 { get; set; } = string.Empty;
    // Zero is the absent pre-revision field and means the frozen legacy model.
    [DataMember(Order = 18, EmitDefaultValue = false)] public int DatumCalibrationVersion { get; set; }

    public static AnalysisGeometrySnapshot Capture(IReadOnlyList<TelemetrySample> samples, TouchdownAnalysisOptions options, string configOutcome)
    {
        if (samples.Count == 0) throw new ArgumentException("Cannot bind geometry to an empty segment.", nameof(samples));
        var snapshot = new AnalysisGeometrySnapshot
        {
            FirstSequence = samples[0].Sequence, LastSequence = samples[samples.Count - 1].Sequence,
            FirstSimulationTime = samples[0].SimulationTimeSeconds, LastSimulationTime = samples[samples.Count - 1].SimulationTimeSeconds,
            SampleCount = samples.Count, LongitudinalArmFeet = options.LongitudinalMainGearArmFeet,
            Source = options.LongitudinalMainGearArmFeet.HasValue ? options.LongitudinalMainGearArmSource.ToString() : "Unavailable",
            Quality = options.LongitudinalMainGearArmQuality,
            MainIndices = options.MainGearContactPoints.Select(p => p.ContactPointIndex).ToArray(),
            MainArmsFeet = options.MainGearContactPoints.Select(p => p.LongitudinalArmFeet).ToArray(),
            NoseIndices = options.NoseGearContactPointIndices.ToArray(),
            RecoverFromTelemetry = options.RecoverLongitudinalMainGearArmFromTelemetry,
            ConfigOutcome = configOutcome,
            DatumCalibrationVersion = (int)options.DatumCalibrationVersion,
            InputSha256 = TelemetryBinaryCodec.Fingerprint(samples),
        };
        snapshot.Validate();
        return snapshot;
    }

    public TouchdownAnalysisOptions Restore(IReadOnlyList<TelemetrySample> samples)
    {
        Validate();
        if (samples.Count != SampleCount || samples.Count == 0 || samples[0].Sequence != FirstSequence ||
            samples[samples.Count - 1].Sequence != LastSequence ||
            samples[0].SimulationTimeSeconds != FirstSimulationTime || samples[samples.Count - 1].SimulationTimeSeconds != LastSimulationTime ||
            (Version == 1 ? LegacyFingerprint(samples) : TelemetryBinaryCodec.Fingerprint(samples)) != InputSha256)
            throw new InvalidDataException("Geometry snapshot belongs to another physical segment.");
        return new TouchdownAnalysisOptions
        {
            DatumCalibrationVersion = DatumCalibrationVersion == 0 ? TelemetryDatumCalibrationVersion.Legacy : (TelemetryDatumCalibrationVersion)DatumCalibrationVersion,
            LongitudinalMainGearArmFeet = LongitudinalArmFeet,
            LongitudinalMainGearArmSource = (TouchdownGeometrySource)Enum.Parse(typeof(TouchdownGeometrySource), Source),
            LongitudinalMainGearArmQuality = Quality,
            MainGearContactPoints = MainIndices.Select((index, i) => new TouchdownMainGearContactPoint(index, MainArmsFeet[i])).ToArray(),
            NoseGearContactPointIndices = (int[])NoseIndices.Clone(), RecoverLongitudinalMainGearArmFromTelemetry = RecoverFromTelemetry,
        };
    }

    public AnalysisGeometrySnapshot Copy()
    {
        // Copying display metadata must not invoke a version-specific analyzer.
        // Unknown snapshots remain opaque evidence until Restore is requested.
        var copy = (AnalysisGeometrySnapshot)MemberwiseClone();
        copy.MainIndices = MainIndices == null ? null! : (int[])MainIndices.Clone();
        copy.MainArmsFeet = MainArmsFeet == null ? null! : (double[])MainArmsFeet.Clone();
        copy.NoseIndices = NoseIndices == null ? null! : (int[])NoseIndices.Clone();
        return copy;
    }

    public bool IsSupported => (DatumCalibrationVersion == 0 || DatumCalibrationVersion == 1 || DatumCalibrationVersion == 2) &&
        (Version == 1 && InputFingerprintFormat == "telemetry_csv_v7" ||
        Version == 2 && InputFingerprintFormat == TelemetryBinaryCodec.FingerprintFormat);

    public void Validate()
    {
        if (!IsSupported ||
            CoordinateFrame != "velocity_reference_longitudinal_feet" || SampleCount <= 0 ||
            InputSha256 == null || InputSha256.Length != 64 ||
            InputSha256.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) ||
            !Finite(FirstSimulationTime) || !Finite(LastSimulationTime) || LastSimulationTime < FirstSimulationTime ||
            !Enum.GetNames(typeof(TouchdownGeometrySource)).Contains(Source) ||
            (LongitudinalArmFeet.HasValue && (!Finite(LongitudinalArmFeet.Value) || Math.Abs(LongitudinalArmFeet.Value) > 1000)) ||
            (Quality.HasValue && (!Finite(Quality.Value) || Quality < 0 || Quality > 1)) ||
            MainIndices == null || MainArmsFeet == null || NoseIndices == null || MainIndices.Length != MainArmsFeet.Length ||
            MainArmsFeet.Any(x => !Finite(x) || Math.Abs(x) > 1000) ||
            MainIndices.Concat(NoseIndices).Any(i => i < 0 || i >= TelemetrySample.CapturedContactPointCount) ||
            MainIndices.Concat(NoseIndices).Distinct().Count() != MainIndices.Length + NoseIndices.Length ||
            string.IsNullOrEmpty(ConfigOutcome) || ConfigOutcome.Length > 80 || ConfigOutcome.Any(c => !(c >= 'a' && c <= 'z') && c != '_'))
            throw new InvalidDataException("Invalid or unsupported analysis geometry snapshot.");
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static string LegacyFingerprint(IReadOnlyList<TelemetrySample> samples)
    {
        using var sha = SHA256.Create();
        using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            foreach (var sample in samples) { writer.Write(GeometryFingerprintCsvV7.Format(sample)); writer.Write('\n'); }
        return BitConverter.ToString(sha.Hash!).Replace("-", string.Empty).ToLowerInvariant();
    }
}
