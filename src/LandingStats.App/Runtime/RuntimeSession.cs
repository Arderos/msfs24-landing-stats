using System;
using System.IO;
using System.Runtime.Serialization;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class RuntimeSession
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Runtime { get; set; }
    [DataMember(Order = 3)] public Guid Capture { get; set; }
    [DataMember(Order = 4)] public Guid Clock { get; set; }
    [DataMember(Order = 5)] public long Origin { get; set; }
    [DataMember(Order = 6)] public long Frequency { get; set; }
    [DataMember(Order = 7)] public string Engine { get; set; } = "";
    public RecoveryJournalIdentity Identity => new RecoveryJournalIdentity(Runtime, Capture, new RuntimeClockEpoch(Clock, Origin, Frequency));
    public static IDisposable AcquireOwnership(string root)
    {
        Directory.CreateDirectory(root);
        if (Guid.TryParseExact(Path.GetFileName(root), "N", out _)) return AcquireExistingOwnership(root);
        return new FileStream(Path.Combine(root, "runtime.owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    // Readers must never recreate a session that retention has just retired.
    // The external lease lets retention rename an exclusively held session on Windows.
    public static IDisposable AcquireExistingOwnership(string root)
    {
        if (!Guid.TryParseExact(Path.GetFileName(root), "N", out var id))
            return new FileStream(Path.Combine(root, "runtime.owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Runtime session was already retired.");
        // Windows does not rename a directory with an open child handle, even
        // with FILE_SHARE_DELETE. Keep the ownership handle beside sessions.
        var owners = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, ".owners"); Directory.CreateDirectory(owners);
        var lease = new FileStream(Path.Combine(owners, id.ToString("N") + ".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Runtime session was already retired.");
            var legacy = Path.Combine(root, "runtime.owner");
            if (File.Exists(legacy))
                using (var probe = new FileStream(legacy, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    public static RuntimeSession Create(string root, RecoveryJournalIdentity identity, string engine)
    {
        if (!EpisodeTransactionStore.ValidHash(engine)) throw new ArgumentException("A retained engine identity is required.");
        var path = Path.Combine(root, "runtime.json");
        if (File.Exists(path)) throw new InvalidOperationException("Runtime session identity is immutable.");
        var session = new RuntimeSession { Runtime = identity.RuntimeId, Capture = identity.CaptureSessionId, Clock = identity.Clock.Id,
            Origin = identity.Clock.OriginQpc, Frequency = identity.Clock.Frequency, Engine = engine };
        EngineReference.Write(root, engine);
        DurableFile.WriteJson(path, session); return session;
    }
    public static RuntimeSession Read(string root)
    {
        var session = DurableFile.ReadJson<RuntimeSession>(Path.Combine(root, "runtime.json"), 4096);
        if (session.Version != 1 || !EpisodeTransactionStore.ValidHash(session.Engine)) throw new InvalidDataException("Invalid runtime session.");
        _ = session.Identity; return session;
    }
}
