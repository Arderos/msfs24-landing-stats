using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using LandingStats.Core;

internal static class CorpusFingerprintCheck
{
    public static int Dump(string archive, string output)
    {
        var csv = output + ".csv";
        using var writer = new StreamWriter(output, false);
        try
        {
            using var zip = new ZipArchive(File.OpenRead(archive));
            using (var entry = zip.GetEntry("telemetry.csv")!.Open())
            using (var file = File.Create(csv)) entry.CopyTo(file);
            var raw = TelemetryCsv.ReadFile(csv);
            var groups = new[] { raw }.Concat(TelemetryKinematicDiscontinuity.SplitPhysicalSegments(raw)
                .Select(part => TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(part))));
            var group = 0;
            foreach (var samples in groups)
            {
                var index = 0;
                foreach (var sample in samples)
                {
                    foreach (var property in typeof(TelemetrySample).GetProperties().Where(p => p.Name != "AnalysisDiscontinuity").OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        var value = property.GetValue(sample);
                        var fields = value is IEnumerable values ? values.Cast<object>().Select(Bits) : new[] { Bits(value!) };
                        writer.WriteLine(group + ":" + index + ":" + property.Name + "\t" + string.Join(",", fields));
                    }
                    index++;
                }
                group++;
            }
            return 0;
        }
        finally { if (File.Exists(csv)) File.Delete(csv); }
    }

    private static string Bits(object value) => value is double number ? BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture)!;

    public static int Run(string manifest, string output)
    {
        var csv = output + ".csv";
        var count = 0;
        using var writer = new StreamWriter(output, false);
        try
        {
            foreach (var row in File.ReadLines(manifest))
            {
                var fields = row.Split(new[] { '\t' }, 2);
                var bytes = File.ReadAllBytes(fields[1]);
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != fields[0])
                        throw new InvalidDataException("Corpus bytes changed: " + fields[0]);
                using var zip = new ZipArchive(new MemoryStream(bytes, false));
                using (var entry = zip.GetEntry("telemetry.csv")!.Open())
                using (var file = File.Create(csv)) entry.CopyTo(file);
                var samples = TelemetryCsv.ReadFile(csv);
                var segments = TelemetryKinematicDiscontinuity.SplitPhysicalSegments(samples)
                    .Select(part => TelemetryBinaryCodec.Fingerprint(TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(part))));
                writer.WriteLine(fields[0] + "\t" + TelemetryBinaryCodec.Fingerprint(samples) + "\t" + string.Join(",", segments));
                count++;
            }
            Console.WriteLine("EXPORTED portable fingerprints (compare this file with the retained baseline): " + count);
            return 0;
        }
        finally { if (File.Exists(csv)) File.Delete(csv); }
    }
}
