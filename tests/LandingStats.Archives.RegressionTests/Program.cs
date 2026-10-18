using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LandingStats.Archives;

internal static class Program
{
    private static readonly Dictionary<string, byte[]> Members = new Dictionary<string, byte[]>
    {
        ["telemetry.csv"] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => i + ",0.12345678901234567,-0,NaN,Infinity\r\n"))),
        ["session.txt"] = Encoding.UTF8.GetBytes("telemetry_schema=7\naircraft_title=Самолёт\n"),
        ["decisions.json"] = Encoding.UTF8.GetBytes("{\"entries\":[]}"),
        ["controller-catalogs.json"] = Encoding.UTF8.GetBytes("{\"catalogs\":[]}"),
        ["landing-results.json"] = Encoding.UTF8.GetBytes("{\"records\":[]}"),
        ["source-state.bin"] = Enumerable.Range(0, 137).Select(i => (byte)i).ToArray(),
    };
    private static int Main(string[] args)
    {
        try
        {
            Tests(); Console.WriteLine("PASS legacy/versioned archives, framing, corruption and bounds");
            if (args.Length == 2 && args[0] == "--emit-fixtures")
            {
                var root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
                File.WriteAllBytes(Path.Combine(root, "legacy.zip"), Write(false));
                File.WriteAllBytes(Path.Combine(root, "zstd.zip"), Write(true));
                var expected = Path.Combine(root, "expected"); Directory.CreateDirectory(expected);
                foreach (var member in Members) File.WriteAllBytes(Path.Combine(expected, member.Key), member.Value);
            }
            else if (args.Length == 3 && (args[0] == "--repack" || args[0] == "--verify")) Corpus(args[1], args[2], args[0] == "--repack");
            else if (args.Length != 0) return 2;
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static byte[] Write(bool zstd)
    {
        using var bytes = new MemoryStream();
        using (var archive = new CaptureArchiveWriter(bytes, zstd, true))
            foreach (var member in Members)
            {
                try { using var output = archive.CreateEntry(member.Key); output.Write(member.Value, 0, member.Value.Length); }
                catch (Exception error) { Console.Error.WriteLine("Writing " + member.Key + ": " + error); throw; }
            }
        return bytes.ToArray();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Read(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var archive = new CaptureArchiveReader(input);
        foreach (var member in archive.Entries) { using var stream = archive.Open(member.Name); stream.CopyTo(Stream.Null); }
    }
    private static void Reject(byte[] bytes)
    { try { Read(bytes); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Accepted malformed archive."); }
    private static byte[] Change(byte[] bytes, string name, Func<byte[], byte[]> change, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var result = new MemoryStream(); using var input = new MemoryStream(bytes); using var source = new ZipArchive(input, ZipArchiveMode.Read);
        using (var output = new ZipArchive(result, ZipArchiveMode.Create, true))
            foreach (var entry in source.Entries)
            {
                using var content = new MemoryStream(); using (var stream = entry.Open()) stream.CopyTo(content);
                var data = entry.FullName == name ? change(content.ToArray()) : content.ToArray();
                using var destination = output.CreateEntry(entry.FullName, entry.FullName == name ? compression : CompressionLevel.NoCompression).Open();
                destination.Write(data, 0, data.Length);
            }
        return result.ToArray();
    }
    private static void Tests()
    {
        foreach (var format in new[] { false, true })
        {
            using var input = new MemoryStream(Write(format)); using var archive = new CaptureArchiveReader(input);
            Check(archive.Format == (format ? 2 : 1) && archive.Entries.Count == Members.Count, "Wrong format/member count.");
            foreach (var member in Members)
            { using var stream = archive.Open(member.Key); using var actual = new MemoryStream(); stream.CopyTo(actual); Check(member.Value.SequenceEqual(actual.ToArray()), "Member bytes changed."); }
        }
        var bytes = Write(true);
        DirectoryInterpretationChecks(bytes);
        Reject(Change(bytes, CaptureArchiveFormat.ManifestName, data => data.Take(data.Length - 1).ToArray()));
        Reject(Change(bytes, CaptureArchiveFormat.ManifestName, data => data.Concat(new byte[] { 0 }).ToArray()));
        Reject(Change(bytes, CaptureArchiveFormat.ManifestName, data => { data[0] = 0; return data; }));
        Reject(Change(bytes, CaptureArchiveFormat.ManifestName, data => { data[data.Length - 1] ^= 1; return data; }));
        Reject(Change(bytes, CaptureArchiveFormat.ManifestName, data => { var offset = 9 + 1 + data[9] + 1; Array.Copy(BitConverter.GetBytes(257L * 1024 * 1024), 0, data, offset, 8); return data; }));
        Reject(Change(bytes, "telemetry.csv.zst", data => data.Take(data.Length - 1).ToArray()));
        Reject(Change(bytes, "telemetry.csv.zst", data => data.Concat(new byte[] { 0 }).ToArray()));
        Reject(Change(bytes, "telemetry.csv.zst", data => data.Concat(data).ToArray()));
        Reject(Change(bytes, "telemetry.csv.zst", data => { data[4] |= 8; return data; }));
        Reject(Change(bytes, "telemetry.csv.zst", data => { data[5] = 0xff; return data; }));
        Read(Change(bytes, "telemetry.csv.zst", data => data, CompressionLevel.Optimal));
        Read(Change(bytes, CaptureArchiveFormat.ManifestName, data => data, CompressionLevel.Optimal));
        var count = bytes.ToArray(); count[count.Length - 12] = 5; Reject(count);
        var length = bytes.ToArray(); length[length.Length - 10] = 0xff; Reject(length);
        for (var n = 1; n <= 22; n++) Reject(bytes.Take(bytes.Length - n).ToArray());
        try { using var limited = new CaptureArchiveReader(new MemoryStream(bytes), maximumDecodedBytes: 100); throw new InvalidOperationException("Expansion bound ignored."); }
        catch (InvalidDataException) { }
        using var unfinished = new MemoryStream(); var broken = new CaptureArchiveWriter(unfinished, true, true); var pending = broken.CreateEntry("telemetry.csv");
        try { broken.Dispose(); throw new InvalidOperationException("Published unfinished member."); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Capture member did not finish", StringComparison.Ordinal)) { }
        pending.Dispose();
    }
    private static void DirectoryInterpretationChecks(byte[] bytes)
    {
        var start = BitConverter.ToUInt32(bytes, bytes.Length - 6);
        var directory = bytes.Skip((int)start).Take(bytes.Length - 22 - (int)start).ToArray();
        var name = Encoding.ASCII.GetBytes("session.txt"); var header = new byte[46];
        Array.Copy(BitConverter.GetBytes(0x02014b50u), header, 4);
        Array.Copy(BitConverter.GetBytes((ushort)name.Length), 0, header, 28, 2);
        Array.Copy(BitConverter.GetBytes((ushort)(directory.Length + 76)), 0, header, 32, 2);
        using var ambiguous = new MemoryStream(); using var writer = new BinaryWriter(ambiguous, Encoding.UTF8, true);
        writer.Write(bytes, 0, (int)start); writer.Write(header); writer.Write(name);
        var realStart = ambiguous.Position; writer.Write(directory);
        var end64 = ambiguous.Position;
        writer.Write(0x06064b50u); writer.Write(44L); writer.Write((ushort)45); writer.Write((ushort)45);
        writer.Write(0); writer.Write(0); writer.Write(7L); writer.Write(7L); writer.Write((long)directory.Length); writer.Write(realStart);
        writer.Write(0x07064b50u); writer.Write(0); writer.Write(end64); writer.Write(1);
        var boundedSize = (uint)(ambiguous.Position - start);
        writer.Write(0x06054b50u); writer.Write(0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(boundedSize); writer.Write(start); writer.Write((ushort)0);
        Reject(ambiguous.ToArray());
        byte[] WithComment(string comment)
        {
            var content = Encoding.ASCII.GetBytes(comment); var result = bytes.Concat(content).ToArray();
            Array.Copy(BitConverter.GetBytes((ushort)content.Length), 0, result, bytes.Length - 2, 2); return result;
        }
        Read(WithComment("ordinary comment")); Reject(WithComment("ambiguous PK\u0005\u0006 suffix"));
        try { Read(WithComment("\u0005\u0006" + new string('\0', 19278))); throw new InvalidOperationException("Accepted straddling directory marker."); }
        catch (InvalidDataException error) when (error.Message.Contains("Ambiguous")) { }
        var invalidName = bytes.ToArray(); invalidName[(int)start + 46] = 0xff; Reject(invalidName);
    }
    private static string Hash(Stream stream) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void Corpus(string manifest, string directory, bool repack)
    {
        Directory.CreateDirectory(directory); var timer = Stopwatch.StartNew(); long sourceBytes = 0, encodedBytes = 0; var count = 0; var rejected = 0;
        foreach (var line in File.ReadLines(manifest))
        {
            var split = line.IndexOf('\t'); var hash = line.Substring(0, split); var path = line.Substring(split + 1);
            using var input = File.OpenRead(path); Check(Hash(input) == hash, "Original capture hash changed: " + path); input.Position = 0;
            CaptureArchiveReader sourceReader;
            try { sourceReader = new CaptureArchiveReader(input, true); }
            catch (InvalidDataException error) { Console.WriteLine("REJECT source=" + hash + " reason=" + error.Message); rejected++; continue; }
            using var source = sourceReader; var destination = Path.Combine(directory, hash + ".zip");
            if (repack && !File.Exists(destination))
            {
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var archive = new CaptureArchiveWriter(output, true);
                foreach (var member in source.Entries) { using var raw = source.Open(member.Name); using var encoded = archive.CreateEntry(member.Name); raw.CopyTo(encoded); }
            }
            using var restored = File.OpenRead(destination); using var verify = new CaptureArchiveReader(restored, true);
            Check(verify.Format == 2 && verify.Entries.Count == source.Entries.Count, "Corpus members differ.");
            foreach (var member in source.Entries)
            { using var raw = source.Open(member.Name); using var actual = verify.Open(member.Name); Check(Hash(raw) == Hash(actual), "Corpus member bytes differ."); }
            sourceBytes += input.Length; encodedBytes += restored.Length;
            if (++count % 100 == 0) Console.WriteLine("verified=" + count);
        }
        Console.WriteLine($"PASS byte_roundtrip={count} rejected_sources={rejected} original_bytes={sourceBytes} new_bytes={encodedBytes} elapsed_ms={timer.ElapsedMilliseconds} peak_working_set={Process.GetCurrentProcess().PeakWorkingSet64}");
    }
}
