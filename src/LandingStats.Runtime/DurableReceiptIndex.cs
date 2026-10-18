using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LandingStats.App.Runtime;

/// <summary>Terminal receipts need exact lookup, never a polling scan. Partition
/// them into at most 256 files and cache at most 16 immutable shard snapshots.
/// Callers remove their active record only after this publication is durable.</summary>
internal sealed class DurableReceiptIndex<T> where T : class
{
    [DataContract] private sealed class Shard
    {
        [DataMember(Order = 1)] public int Version { get; set; } = 1;
        [DataMember(Order = 2)] public Dictionary<string, string> Entries { get; set; } = new Dictionary<string, string>();
    }
    private sealed class Cached
    {
        public DateTime Stamp;
        public long Length;
        public Shard Value = new Shard();
    }
    private readonly string _root;
    private readonly Dictionary<string, Cached> _cache = new Dictionary<string, Cached>();
    private readonly object _gate = new object();
    public DurableReceiptIndex(string root) { _root = root; }
    private string Key(Guid id)
    { if (id == Guid.Empty) throw new ArgumentException("Receipt identity is required."); return id.ToString("N"); }
    private Shard Load(string key)
    {
        var prefix = key.Substring(0, 2); var path = Path.Combine(_root, prefix + ".json"); var file = new FileInfo(path);
        var stamp = file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue; var length = file.Exists ? file.Length : 0;
        if (_cache.TryGetValue(prefix, out var cached) && cached.Stamp == stamp && cached.Length == length) return cached.Value;
        var value = file.Exists ? DurableFile.ReadJson<Shard>(path, 64L * 1024 * 1024) : new Shard();
        if (value.Version != 1 || value.Entries == null || value.Entries.Count > 65536) throw new InvalidDataException("Invalid terminal receipt shard.");
        foreach (var item in value.Entries)
            if (!Guid.TryParseExact(item.Key, "N", out _) || !item.Key.StartsWith(prefix, StringComparison.Ordinal) || item.Value == null || item.Value.Length > 32768)
                throw new InvalidDataException("Invalid terminal receipt.");
        if (_cache.Count == 16) _cache.Clear();
        _cache[prefix] = new Cached { Stamp = stamp, Length = length, Value = value }; return value;
    }
    public bool Contains(Guid id) { lock (_gate) return Load(Key(id)).Entries.ContainsKey(Key(id)); }
    public T Read(Guid id)
    {
        lock (_gate)
        {
            if (!Load(Key(id)).Entries.TryGetValue(Key(id), out var json)) throw new FileNotFoundException("Terminal receipt is unavailable.");
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(input)!;
        }
    }
    public void Write(Guid id, T value)
    {
        lock (_gate)
        {
            var key = Key(id); var previous = Load(key);
            using var output = new MemoryStream(); new DataContractJsonSerializer(typeof(T)).WriteObject(output, value);
            if (output.Length > 32768) throw new InvalidDataException("Terminal receipt exceeds its bound.");
            var json = Encoding.UTF8.GetString(output.ToArray());
            if (previous.Entries.TryGetValue(key, out var existing))
            { if (existing != json) throw new InvalidDataException("Terminal receipt identity was reused."); return; }
            if (previous.Entries.Count >= 65536) throw new IOException("Terminal receipt shard is full; active record retained.");
            var next = new Shard { Entries = new Dictionary<string, string>(previous.Entries) { [key] = json } };
            // JSON escaping takes at most six bytes per UTF-16 code unit.
            if (next.Entries.Sum(item => 128L + item.Value.Length * 6L) > 64L * 1024 * 1024)
                throw new IOException("Terminal receipt shard byte limit reached; active record retained.");
            DurableFile.WriteJson(Path.Combine(_root, key.Substring(0, 2) + ".json"), next);
            _cache.Remove(key.Substring(0, 2));
        }
    }
}
