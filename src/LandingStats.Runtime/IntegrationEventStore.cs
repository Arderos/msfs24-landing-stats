using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LandingStats.App.Runtime;

/// <summary>A bounded, atomic, replayable projection of committed episode results.
/// The episode transaction remains the authority and can repair a missed append.</summary>
internal sealed class IntegrationEventStore
{
    internal const int MaximumEvents = 4096;
    internal const int MaximumBytes = 32 * 1024 * 1024;
    private readonly string _path;
    private readonly object _gate = new object();
    private JObject? _state;
    public IntegrationEventStore(string root) { _path = Path.Combine(root, "events-v1.json"); }

    public string StreamId { get { lock (_gate) return (string)State["streamId"]!; } }
    public long LatestSequence { get { lock (_gate) return (long)State["nextSequence"]! - 1; } }
    private JObject State => _state ?? (_state = Load());
    private static JObject Empty(DateTime floor) => new JObject
    {
        ["version"] = 1, ["streamId"] = Guid.NewGuid().ToString("N"),
        ["nextSequence"] = 1L, ["retentionFloorUtc"] = floor.ToString("O", CultureInfo.InvariantCulture), ["events"] = new JArray()
    };
    private JObject Load()
    {
        if (!File.Exists(_path)) { var fresh = Empty(DateTime.MinValue); Save(fresh); return fresh; }
        try
        {
            if (new FileInfo(_path).Length > MaximumBytes) throw new InvalidDataException();
            using var reader = new JsonTextReader(new StringReader(File.ReadAllText(_path, Encoding.UTF8))) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException();
            var checksum = (string?)value["checksum"];
            var verified = (JObject)value.DeepClone(); verified.Remove("checksum");
            if (checksum == null || checksum != Checksum(verified)) throw new InvalidDataException();
            if ((int?)value["version"] != 1 || !Guid.TryParseExact((string?)value["streamId"], "N", out _) ||
                !(value["events"] is JArray events) || events.Count > MaximumEvents || value["nextSequence"]?.Type != JTokenType.Integer || (long?)value["nextSequence"] < 1 ||
                !DateTime.TryParse((string?)value["retentionFloorUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                throw new InvalidDataException();
            long previous = 0;
            foreach (var item in events.OfType<JObject>())
            {
                var sequence = (long?)item["sequence"] ?? 0;
                if (sequence <= previous || (previous != 0 && sequence != previous + 1) || sequence >= (long)value["nextSequence"]! ||
                    string.IsNullOrEmpty((string?)item["episodeId"]) || ((string)item["episodeId"]!).Length > 256 ||
                    ((int?)item["revision"] != 1 && (int?)item["revision"] != 2) || !(item["payload"] is JObject) ||
                    Encoding.UTF8.GetByteCount(item.ToString(Formatting.None)) > 256 * 1024 ||
                    !DateTime.TryParse((string?)item["timestampUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                    throw new InvalidDataException();
                previous = sequence;
            }
            if (events.Count != 0 && previous != (long)value["nextSequence"]! - 1) throw new InvalidDataException();
            if (events.Any(item => !(item is JObject))) throw new InvalidDataException();
            return value;
        }
        catch (Exception error) when (error is JsonException || error is InvalidDataException || error is FormatException || error is OverflowException || error is ArgumentException)
        {
            // Preserve corrupt bytes for diagnosis. A new epoch forces clients to
            // rebuild from canonical history; never silently trust a broken cursor.
            var quarantine = _path + ".corrupt-" + Guid.NewGuid().ToString("N");
            File.Move(_path, quarantine);
            // The corrupt file cannot provide a trustworthy retention floor.
            // Rebuild from durable analyses, including results not yet in history.
            var replacement = Empty(DateTime.MinValue); Save(replacement); return replacement;
        }
    }
    private void Save(JObject state)
    {
        state.Remove("checksum"); state["checksum"] = Checksum(state);
        var bytes = Encoding.UTF8.GetBytes(state.ToString(Formatting.None));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Integration journal exceeds its byte bound.");
        DurableFile.Write(_path, output => output.Write(bytes, 0, bytes.Length));
    }
    private static string Checksum(JObject state)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(state.ToString(Formatting.None))));
    }
    public bool Publish(string episode, DateTime timestampUtc, int revision, JObject payload)
    {
        lock (_gate)
        {
            var current = State;
            var floor = DateTime.Parse((string)current["retentionFloorUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var id = episode; var existing = ((JArray)current["events"]!).OfType<JObject>().Where(e => (string?)e["episodeId"] == id).ToArray();
            // The floor prevents replay of wholly expired episodes. A retained
            // older episode still needs its later metadata revision delivered.
            if (timestampUtc <= floor && existing.Length == 0) return false;
            if (existing.Any(e => (int)e["revision"]! >= revision)) return false;
            var next = (JObject)current.DeepClone(); var events = (JArray)next["events"]!;
            var sequence = (long)next["nextSequence"]!;
            events.Add(new JObject
            {
                ["sequence"] = sequence, ["type"] = revision == 1 ? "LandingResultAvailable" : "LandingResultUpdated",
                ["episodeId"] = id, ["revision"] = revision, ["timestampUtc"] = timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["publishedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["payload"] = payload.DeepClone()
            });
            next["nextSequence"] = checked(sequence + 1);
            while (events.Count > MaximumEvents || Encoding.UTF8.GetByteCount(next.ToString(Formatting.None)) > MaximumBytes)
            {
                if (events.Count <= 1) throw new InvalidDataException("An integration result exceeds the journal bound.");
                var removedTime = DateTime.Parse((string)events[0]!["timestampUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                floor = removedTime > floor ? removedTime : floor;
                // Retire only a contiguous sequence prefix. Late revisions can have
                // an older episode timestamp than events a client has already read.
                events[0]!.Remove();
                next["retentionFloorUtc"] = floor.ToString("O", CultureInfo.InvariantCulture);
            }
            Save(next); _state = next; return true;
        }
    }
    public bool RefreshDeletionFingerprint(string fingerprint)
    {
        lock (_gate)
        {
            var current = State;
            if ((string?)current["deletionFingerprint"] == fingerprint) return false;
            if (current["deletionFingerprint"] == null && ((JArray)current["events"]!).Count == 0)
            {
                var initial = (JObject)current.DeepClone(); initial["deletionFingerprint"] = fingerprint;
                Save(initial); _state = initial; return false;
            }
            var next = Empty(DateTime.Parse((string)current["retentionFloorUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            next["deletionFingerprint"] = fingerprint;
            Save(next); _state = next; return true;
        }
    }
    public JObject Read(string? streamId, long? afterSequence, int limit)
    {
        lock (_gate)
        {
            var state = State; var events = (JArray)state["events"]!; var next = (long)state["nextSequence"]!;
            var oldest = events.Count == 0 ? next : (long)events[0]!["sequence"]!;
            var after = afterSequence ?? oldest - 1;
            if ((streamId != null && streamId != (string)state["streamId"]!) || after < oldest - 1 || after >= next)
                throw new IntegrationApiException("ResyncRequired", "The event cursor is no longer valid. Rebuild from landings.list and resume with the current stream.");
            var selected = new List<JToken>(); var bytes = 1024;
            foreach (var candidate in events.Where(e => (long)e["sequence"]! > after).Take(limit))
            {
                var size = Encoding.UTF8.GetByteCount(candidate.ToString(Formatting.None));
                if (bytes + size > 450 * 1024) break;
                selected.Add(candidate); bytes += size;
            }
            var cursor = selected.Count == 0 ? after : (long)selected[selected.Count - 1]["sequence"]!;
            return new JObject { ["streamId"] = state["streamId"]!.DeepClone(), ["events"] = new JArray(selected.Select(e => e.DeepClone())),
                ["nextSequence"] = cursor, ["oldestSequence"] = oldest, ["latestSequence"] = next - 1, ["hasMore"] = events.Any(e => (long)e["sequence"]! > cursor) };
        }
    }
    public JObject? FindEpisode(string episodeId)
    {
        lock (_gate) return ((JArray)State["events"]!).OfType<JObject>().LastOrDefault(e => (string?)e["episodeId"] == episodeId)?["payload"]?.DeepClone() as JObject;
    }
    public string? FindEpisodeForContact(string id)
    {
        lock (_gate) return ((JArray)State["events"]!).OfType<JObject>().LastOrDefault(e => ((JArray?)e["payload"]?["contacts"])?.Any(c => (string?)c["id"] == id) == true)?["episodeId"]?.Value<string>();
    }
    public JObject[] MissingHistoryContacts(ISet<string> historyIds)
    {
        lock (_gate)
        {
            return ((JArray)State["events"]!).OfType<JObject>().Reverse().GroupBy(e => (string)e["episodeId"]!, StringComparer.Ordinal)
                .Select(group => group.First()).SelectMany(e => ((JArray?)e["payload"]?["contacts"]) ?? new JArray()).OfType<JObject>()
                .Where(contact => !historyIds.Contains((string)contact["id"]!)).Select(contact => (JObject)contact.DeepClone()).ToArray();
        }
    }
    public bool CoversCapture(Guid capture, DateTime timestamp, int revision)
    {
        lock (_gate)
        {
            var state = State;
            var id = capture.ToString("N");
            var retained = ((JArray)state["events"]!).OfType<JObject>().Where(e => (string?)e["payload"]?["captureId"] == id).ToArray();
            if (retained.Length == 0)
                return timestamp <= DateTime.Parse((string)state["retentionFloorUtc"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var matches = retained.Where(e => (int)e["revision"]! >= revision).ToArray();
            if (matches.Length == 0) return false;
            var expected = (int?)matches[0]["payload"]?["captureEpisodeCount"];
            return expected.HasValue && expected > 0 && matches.All(e => (int?)e["payload"]?["captureEpisodeCount"] == expected) &&
                matches.Select(e => (string)e["episodeId"]!).Distinct(StringComparer.Ordinal).Count() == expected;
        }
    }
}

internal sealed class IntegrationApiException : Exception
{
    public IntegrationApiException(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
}
