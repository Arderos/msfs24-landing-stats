using System;
using System.IO;
using System.Runtime.Serialization;
using LandingStats.App.TelemetryUpload;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

// Only the parent persists capabilities from an accepted current upload claim.
// Unknown, expired or changed endpoints choose the legacy format for NEW files.
// Existing captures retain their original bytes and wait for a capable receiver.
internal sealed class CaptureArchivePolicy
{
    private readonly string _path;
    private readonly Func<string> _endpoint;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new object();
    public CaptureArchivePolicy(string path, Func<string>? endpoint = null, Func<DateTime>? now = null)
    { _path = path; _endpoint = endpoint ?? TelemetryUploadClient.ConfiguredEndpointKey; _now = now ?? (() => DateTime.UtcNow); }
    public bool UseZstd()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return false;
                var value = DurableFile.ReadJson<Observation>(_path, 4096); var age = _now().Ticks - value.ObservedUtcTicks;
                return value.Version == 1 && EpisodeTransactionStore.ValidHash(value.ReceiverKey) && value.ReceiverKey == _endpoint() &&
                    value.Zstd && age >= 0 && age < TimeSpan.FromHours(1).Ticks;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is SerializationException || error is UnauthorizedAccessException)
            { return false; }
        }
    }
    public void Observe(UploadResultMessage result)
    {
        if (result.ReceiverKey.Length == 0 || result.ReceiverKey != _endpoint()) return;
        lock (_gate) DurableFile.WriteJson(_path, new Observation { ReceiverKey = result.ReceiverKey, Zstd = result.SupportsArchiveV2, ObservedUtcTicks = _now().Ticks });
    }
    [DataContract]
    private sealed class Observation
    {
        [DataMember(Order = 1)] public int Version { get; set; } = 1;
        [DataMember(Order = 2)] public string ReceiverKey { get; set; } = "";
        [DataMember(Order = 3)] public bool Zstd { get; set; }
        [DataMember(Order = 4)] public long ObservedUtcTicks { get; set; }
    }
}
