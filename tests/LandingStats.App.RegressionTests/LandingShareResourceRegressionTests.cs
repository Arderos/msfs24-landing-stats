using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Models;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class LandingShareResourceRegressionTests
{
    internal static void RunAll()
    {
        JsonBudgetRejectsBeforeObjectAllocation();
        RepeatedBlocksShareOneBudget();
        BufferedJsonPreflightPreservesEscapes();
        ProjectionArithmeticCannotOverflow();
        PublicImportRejectsAmplificationAndContinuesTheBatch();
    }

    private static void JsonBudgetRejectsBeforeObjectAllocation()
    {
        foreach (var json in new[]
        {
            "{\"values\":[" + string.Join(",", Enumerable.Repeat("0", 10000)) + "]}",
            "{\"text\":\"" + new string('x', 48000) + "\"}",
            "{\"ignored\":[" + string.Join(",", Enumerable.Repeat("{}", 100)) + "]}",
        })
        {
            Probe.Created = 0;
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
            RejectBudget(() => LandingShareArchive.Deserialize<Probe>(input, new LandingShareReadBudget(128 * 1024)));
            Check(Probe.Created == 0, "JSON resource admission ran after typed object allocation.");
        }
    }

    private static void RepeatedBlocksShareOneBudget()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"values\":[" + string.Join(",", Enumerable.Repeat("0", 1000)) + "]}");
        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var independent = new MemoryStream(bytes, false);
            Check(LandingShareArchive.Deserialize<Probe>(independent, new LandingShareReadBudget(64 * 1024)).Values.Length == 1000,
                "An independently admissible numeric block was rejected.");
        }
        var shared = new LandingShareReadBudget(64 * 1024);
        using (var first = new MemoryStream(bytes, false)) LandingShareArchive.Deserialize<Probe>(first, shared);
        Probe.Created = 0;
        using var second = new MemoryStream(bytes, false);
        RejectBudget(() => LandingShareArchive.Deserialize<Probe>(second, shared));
        Check(Probe.Created == 0, "The next block allocated before the aggregate package budget rejected it.");
    }

    private static void ProjectionArithmeticCannotOverflow()
    {
        RejectBudget(() => new LandingShareReadBudget().ReserveSeries(int.MaxValue, int.MaxValue));
        RejectBudget(() => new LandingShareReadBudget().ReserveInputs(int.MaxValue, int.MaxValue));
    }

    private static void BufferedJsonPreflightPreservesEscapes()
    {
        var prefix = new string('a', 65526);
        var suffix = new string('b', 30);
        // The first escape is split across the preflight reader's buffer edge.
        var json = "{\"text\":\"" + prefix + "\\\"\\\\\\u0416\\ud83d\\ude42" + suffix + "\"}";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var value = LandingShareArchive.Deserialize<Probe>(input);
        Check(value.Text == prefix + "\"\\\u0416\ud83d\ude42" + suffix,
            "Buffered resource admission changed escaped or non-ASCII JSON text.");
    }

    private static void PublicImportRejectsAmplificationAndContinuesTheBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "landing-share-resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = new LandingRecord
            {
                Id = "resource-source", RecordingEpisodeId = "resource-episode", ContactNumber = 1, ContactCount = 1,
                TimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), Simulator = "MSFS 2024",
                AircraftTitle = "Resource fixture", AircraftType = "A320",
            };
            var core = LandingShareContent.Create(source).CorePayload;
            var normal = Path.Combine(root, "normal.lslanding");
            WriteShare(normal, Contents(core, Pose(3)), 1);
            var amplified = Path.Combine(root, "amplified.lslanding");
            // This valid pose feature is only a few MiB on the wire. Expanding
            // its timeline into full points exceeds the separate object budget.
            WriteShare(amplified, Contents(core, Pose(140000)), 1);

            var flood = Path.Combine(root, "metadata-flood.lslanding");
            var contactCount = (int)(LandingShareReadBudget.MaximumBytes / LandingShareReadBudget.ObjectBytes) + 1;
            using (var contents = new MemoryStream())
            {
                using var writer = new BinaryWriter(contents, Encoding.UTF8, true);
                writer.Write(Encoding.ASCII.GetBytes("LSLAND01")); writer.Write(contactCount);
                // Supply the minimum declared framing size so metadata admission,
                // rather than a truncated-count guard, rejects before iteration.
                writer.Write(new byte[contactCount * 8]); writer.Flush();
                WriteShare(flood, contents.ToArray(), contactCount);
            }
            var repository = new LandingRepository(Path.Combine(root, "history"));
            var result = LandingShareArchive.Import(repository, new[] { amplified, flood, normal });
            Check(result.Failures.Count == 2 && result.Failures.All(failure => failure.Message.Contains("memory budget")),
                "Resource amplification did not fail at the explicit package budget.");
            Check(result.Imported.Count == 1 && repository.LoadAll().Count == 1 && result.Imported[0].Series.Count == 3,
                "A rejected package published records or prevented the following normal import.");
            var exported = Path.Combine(root, "roundtrip.lslanding");
            LandingShareArchive.Export(repository, result.Imported[0].Id, exported);
            var duplicate = LandingShareArchive.Import(repository, new[] { exported });
            Check(duplicate.Failures.Count == 0 && duplicate.DuplicateCount == 1 && duplicate.Imported.Count == 0,
                "Resource admission changed ordinary retained bytes or package identity.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static LandingSharePayload Pose(int count)
    {
        var zero = string.Join(",", Enumerable.Repeat("0", count));
        var unknown = string.Join(",", Enumerable.Repeat("null", count));
        var ground = string.Join(",", Enumerable.Repeat("false", count));
        var columns = "{\"lat\":[" + unknown + "],\"lon\":[" + unknown + "],\"altFt\":[" + unknown +
            "],\"groundFt\":[" + unknown + "],\"headingTrueDeg\":[" + unknown + "]}";
        var json = "{\"time\":[" + zero + "],\"columns\":" + columns + ",\"pitch\":[" + unknown +
            "],\"bank\":[" + unknown + "],\"onGround\":[" + ground + "]}";
        return LandingSharePayload.FromBytes(Encoding.UTF8.GetBytes(json));
    }

    private static byte[] Contents(LandingSharePayload core, LandingSharePayload pose)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("LSLAND01")); writer.Write(1);
        writer.Write(core.ByteCount); core.WriteTo(stream); writer.Write(1);
        var type = Encoding.UTF8.GetBytes("org.landingstats.pose");
        writer.Write(type.Length); writer.Write(type); writer.Write(1); writer.Write((byte)0);
        writer.Write(pose.ByteCount); pose.WriteTo(stream); writer.Flush();
        return stream.ToArray();
    }

    private static void WriteShare(string path, byte[] contents, int count)
    {
        using var hash = SHA256.Create();
        var checksum = BitConverter.ToString(hash.ComputeHash(contents)).Replace("-", "").ToLowerInvariant();
        var manifest = Encoding.UTF8.GetBytes("{\"format\":\"MSFS Landing Stats landing\",\"version\":1,\"contacts\":" +
            count.ToString(CultureInfo.InvariantCulture) + ",\"sha256\":\"" + checksum + "\"}");
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        void Entry(string name, byte[] bytes)
        { using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open(); entry.Write(bytes, 0, bytes.Length); }
        Entry("manifest.json", manifest); Entry("contents.bin", contents);
    }

    [DataContract]
    private sealed class Probe
    {
        internal static int Created;
        [DataMember(Name = "values")] internal double[] Values = Array.Empty<double>();
        [DataMember(Name = "text")] internal string Text = "";
        [OnDeserializing] private void OnDeserializing(StreamingContext context) { Created++; }
    }

    private static void RejectBudget(Action action)
    {
        try { action(); }
        catch (InvalidDataException error) when (error.Message.Contains("memory budget")) { return; }
        throw new InvalidOperationException("The share exceeded its resource budget without a bounded rejection.");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
