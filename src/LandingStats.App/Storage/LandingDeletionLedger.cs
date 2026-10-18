using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Runtime;

namespace LandingStats.App.Storage;

/// <summary>A delete wins over delayed analysis, index repair and cloud imports, including after a crash.</summary>
internal sealed class LandingDeletionLedger
{
    // Every read and replacement is bounded. The trie can grow without expiring
    // any deletion; each split publishes all children before removing its parent.
    internal const int MaximumShardEntries = 256;
    internal const int MaximumShardBytes = 256 * 1024;
    private const int MaximumIndexedIdBytes = 64 * 1024;
    private const int Magic = 0x314C444C; // LDL1
    private static readonly object Gate = new object();
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly string _root;
    private readonly string _index;
    public LandingDeletionLedger(string root)
    { _root = Path.Combine(root, ".deleted"); _index = Path.Combine(_root, "index-v1"); }

    public bool Contains(string id)
    {
        var hash = Hash(id); var legacyName = LegacyName(id);
        lock (Gate)
        {
            var legacy = File.Exists(LegacyPath(legacyName));
            if (legacy) TryMigrateLegacy(legacyName[0]);
            return legacy || ContainsIndexed(hash);
        }
    }

    public void Add(string id)
    {
        var hash = Hash(id); var legacyName = LegacyName(id);
        lock (Gate)
        {
            TryMigrateLegacy(legacyName[0]);
            if (File.Exists(LegacyPath(legacyName)) || ContainsIndexed(hash)) return;
            var bytes = Utf8.GetBytes(id);
            if (bytes.Length > MaximumIndexedIdBytes)
            {
                // Preserve compatibility with arbitrary legacy IDs without
                // admitting an unbounded value into a bounded index block.
                DurableFile.Write(LegacyPath(legacyName), stream => stream.Write(bytes, 0, bytes.Length));
                return;
            }
            Insert(hash.Substring(0, 1), new Dictionary<string, string>(StringComparer.Ordinal) { [hash] = id });
        }
    }

    private bool ContainsIndexed(string hash)
    {
        var directory = _index;
        for (var depth = 0; depth < hash.Length; depth++)
        {
            directory = Path.Combine(directory, hash[depth].ToString());
            if (!Directory.Exists(directory)) return false;
            var path = Path.Combine(directory, "entries.bin");
            if (File.Exists(path) && Read(path, hash.Substring(0, depth + 1)).ContainsKey(hash)) return true;
        }
        return false;
    }

    private void Insert(string prefix, Dictionary<string, string> additions)
    {
        var directory = DirectoryFor(prefix);
        var path = Path.Combine(directory, "entries.bin");
        var entries = File.Exists(path) ? Read(path, prefix) : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in additions)
        {
            if (entries.TryGetValue(pair.Key, out var prior) && !string.Equals(prior, pair.Value, StringComparison.Ordinal))
                throw new InvalidDataException("Deletion identity hash collision.");
            entries[pair.Key] = pair.Value;
        }
        var size = 40L + entries.Values.Sum(id => 4L + Utf8.GetByteCount(id));
        // A partially published split leaves child directories and its complete
        // parent. Route/merge all parent entries again to finish it idempotently.
        var hasChildren = Directory.Exists(directory) && Directory.EnumerateDirectories(directory).Any(child =>
            Path.GetFileName(child).Length == 1 && "0123456789abcdef".IndexOf(Path.GetFileName(child)[0]) >= 0);
        if (hasChildren || entries.Count > MaximumShardEntries || size > MaximumShardBytes)
        {
            if (prefix.Length >= 64) throw new InvalidDataException("Deletion index cannot split colliding identities.");
            foreach (var group in entries.GroupBy(pair => pair.Key[prefix.Length]))
                Insert(prefix + group.Key, group.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        DurableFile.Write(path, stream =>
        {
            using var payload = new MemoryStream();
            using var writer = new BinaryWriter(payload, Utf8, true);
            writer.Write(Magic); writer.Write(entries.Count);
            foreach (var pair in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var bytes = Utf8.GetBytes(pair.Value); writer.Write(bytes.Length); writer.Write(bytes);
            }
            writer.Flush(); var content = payload.ToArray();
            using var sha = SHA256.Create(); var checksum = sha.ComputeHash(content);
            stream.Write(content, 0, content.Length); stream.Write(checksum, 0, checksum.Length);
        });
    }

    private static Dictionary<string, string> Read(string path, string prefix)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length < 40 || file.Length > MaximumShardBytes) throw new InvalidDataException("Deletion index size is invalid.");
        var bytes = new byte[(int)file.Length]; var offset = 0;
        while (offset < bytes.Length)
        {
            var read = file.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new InvalidDataException("Deletion index is truncated.");
            offset += read;
        }
        using var sha = SHA256.Create();
        if (!sha.ComputeHash(bytes, 0, bytes.Length - 32).SequenceEqual(bytes.Skip(bytes.Length - 32)))
            throw new InvalidDataException("Deletion index checksum is invalid; history writes remain blocked.");
        using var stream = new MemoryStream(bytes, 0, bytes.Length - 32, false);
        using var reader = new BinaryReader(stream, Utf8, true);
        if (reader.ReadInt32() != Magic) throw new InvalidDataException("Deletion index version is unsupported.");
        var count = reader.ReadInt32();
        if (count < 1 || count > MaximumShardEntries) throw new InvalidDataException("Deletion index count is invalid.");
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            if (stream.Length - stream.Position < 4) throw new InvalidDataException("Deletion index is truncated.");
            var size = reader.ReadInt32();
            if (size < 1 || size > MaximumIndexedIdBytes || size > stream.Length - stream.Position)
                throw new InvalidDataException("Deletion identity size is invalid.");
            var id = Utf8.GetString(reader.ReadBytes(size)); var hash = Hash(id);
            if (!hash.StartsWith(prefix, StringComparison.Ordinal) || entries.ContainsKey(hash))
                throw new InvalidDataException("Deletion identity belongs to a different shard or is duplicated.");
            entries.Add(hash, id);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Deletion index contains trailing data.");
        return entries;
    }

    private void TryMigrateLegacy(char bucket)
    {
        if (!Directory.Exists(_root)) return;
        try
        {
            // Limit one maintenance pass; subsequent accesses continue it.
            var paths = Directory.EnumerateFiles(_root, bucket + "*.del").Take(MaximumShardEntries).ToArray();
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            var migrated = new List<string>();
            foreach (var path in paths)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length < 1 || stream.Length > MaximumIndexedIdBytes) continue;
                var bytes = new byte[(int)stream.Length]; var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new InvalidDataException("Legacy deletion receipt is truncated.");
                    offset += read;
                }
                var id = Utf8.GetString(bytes); var hash = Hash(id);
                // The old filename is itself a durable delete witness, even if
                // its payload was damaged. Leave unverified receipts untouched.
                if (!string.Equals(Path.GetFileName(path), LegacyName(id) + ".del", StringComparison.Ordinal)) continue;
                entries[hash] = id; migrated.Add(path);
            }
            if (entries.Count == 0) return;
            foreach (var group in entries.GroupBy(pair => pair.Key[0]))
                Insert(group.Key.ToString(), group.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            foreach (var path in migrated) File.Delete(path);
        }
        catch (IOException) { } // Legacy witnesses remain authoritative on failure.
        catch (UnauthorizedAccessException) { }
        catch (DecoderFallbackException) { }
        catch (ArgumentException) { }
    }

    private string DirectoryFor(string prefix)
    {
        var path = _index;
        foreach (var character in prefix) path = Path.Combine(path, character.ToString());
        return path;
    }
    private string LegacyPath(string hash) => Path.Combine(_root, hash + ".del");
    private static string LegacyName(string id) => Convert.ToBase64String(HashBytes(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string id) => BitConverter.ToString(HashBytes(id)).Replace("-", string.Empty).ToLowerInvariant();
    private static byte[] HashBytes(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A landing id is required.", nameof(id));
        using var sha = SHA256.Create();
        return sha.ComputeHash(Utf8.GetBytes(id));
    }
}
