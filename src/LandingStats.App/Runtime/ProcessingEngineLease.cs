using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class EngineLeaseManifest
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public string Identity { get; set; } = "";
    [DataMember(Order = 3)] public EngineLeaseFile[] Files { get; set; } = Array.Empty<EngineLeaseFile>();
}
[DataContract]
internal sealed class EngineLeaseFile
{
    [DataMember(Order = 1)] public string Name { get; set; } = "";
    [DataMember(Order = 2)] public string Hash { get; set; } = "";
}

/// <summary>Pending inputs pin exact engine bytes, independently of updater cleanup or the build directory.</summary>
internal sealed class ProcessingEngineLease
{
    private static readonly object RetentionGate = new object();
    private ProcessingEngineLease(string directory, string identity) { DirectoryPath = directory; Identity = identity; }
    public string DirectoryPath { get; }
    public string Identity { get; }
    public IDisposable AcquireUse()
    {
        lock (RetentionGate)
        {
            var handles = new List<IDisposable>();
            try
            {
                handles.Add(new FileStream(Path.Combine(DirectoryPath, "engine.owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite));
                handles.Add(new FileStream(Path.Combine(DirectoryPath, "engine.json"), FileMode.Open, FileAccess.Read, FileShare.Read));
                var manifest = DurableFile.ReadJson<EngineLeaseManifest>(Path.Combine(DirectoryPath, "engine.json"), 128 * 1024);
                foreach (var file in manifest.Files)
                {
                    ValidateName(file.Name);
                    handles.Add(new FileStream(Path.Combine(DirectoryPath, file.Name), FileMode.Open, FileAccess.Read, FileShare.Read));
                }
                Open(Path.GetDirectoryName(DirectoryPath)!, Identity); // Verify the bytes that are now locked until process retirement.
                return new Use(handles);
            }
            catch { foreach (var handle in handles) handle.Dispose(); throw; }
        }
    }
    private sealed class Use : IDisposable
    {
        private readonly List<IDisposable> _handles;
        public Use(List<IDisposable> handles) { _handles = handles; }
        public void Dispose() { foreach (var handle in _handles) handle.Dispose(); _handles.Clear(); }
    }
    internal static bool CollectUnused(string root, string identity)
    {
        lock (RetentionGate)
        {
            // The caller has checked every durable session/episode reference.
            // This final lock excludes a parent still draining a completed job.
            var engine = Open(root, identity);
            RuntimeScratchLease.RequireUnlinked(engine.DirectoryPath);
            using (var probe = new FileStream(Path.Combine(engine.DirectoryPath, "engine.owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
            var retired = Path.Combine(Path.GetFullPath(root), ".retired-" + identity);
            Directory.Move(engine.DirectoryPath, retired);
            Directory.Delete(retired, true); return true;
        }
    }
    public static ProcessingEngineLease Capture(string root, string source)
    {
        source = Path.GetFullPath(source);
        // Only deployment binaries; no settings, credentials, captures or unrelated executables.
        var names = Directory.EnumerateFiles(source).Select(Path.GetFileName).Where(name =>
            (LandingStats.Packaging.RuntimePayload.IsAllowed(name) && !name.StartsWith("MSFS-Landing-Stats.", StringComparison.OrdinalIgnoreCase)) ||
            name == "LandingStats.Workers.Testing.dll") // Only present in isolated regression deployments, never packaged.
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        foreach (var required in new[] { "LandingStats.WorkerHost.exe", "LandingStats.Runtime.dll", "LandingStats.Core.dll", "LandingStats.Runtime.Contracts.dll" })
            if (!names.Contains(required)) throw new InvalidDataException("The processing engine deployment is incomplete: " + required);
        return CaptureFiles(root, source, names);
    }
    internal static ProcessingEngineLease CaptureFiles(string root, string source, IReadOnlyList<string> names)
    {
        if (names.Count == 0 || names.Count > 256 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new ArgumentException("Invalid processing engine file list.");
        var files = names.OrderBy(name => name, StringComparer.Ordinal).Select(name =>
        {
            ValidateName(name);
            return new EngineLeaseFile { Name = name, Hash = EpisodeTransactionStore.Hash(Path.Combine(source, name)) };
        }).ToArray();
        var identity = IdentityOf(files);
        var directory = Path.Combine(Path.GetFullPath(root), identity);
        var manifestPath = Path.Combine(directory, "engine.json");
        // A healthy copy can already be used by another job. Exclusive repair
        // ownership is needed only when its bytes actually need replacement.
        if (File.Exists(manifestPath))
        {
            try { return Open(root, identity); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is SerializationException) { }
        }
        Directory.CreateDirectory(directory);
        using var ownership = new FileStream(Path.Combine(directory, "engine.owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(manifestPath))
        {
            try { return Open(root, identity); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is SerializationException)
            { File.Copy(manifestPath, manifestPath + ".corrupt-" + Guid.NewGuid().ToString("N")); }
        }
        foreach (var unexpected in Directory.EnumerateFiles(directory).Where(path => Loadable(path) && !files.Any(file => file.Name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))))
            File.Move(unexpected, unexpected + ".corrupt-" + Guid.NewGuid().ToString("N"));
        foreach (var file in files)
        {
            var destination = Path.Combine(directory, file.Name);
            if (File.Exists(destination) && EpisodeTransactionStore.Hash(destination) != file.Hash)
                File.Move(destination, destination + ".corrupt-" + Guid.NewGuid().ToString("N"));
            if (!File.Exists(destination))
                DurableFile.Write(destination, target =>
                { using var input = File.OpenRead(Path.Combine(source, file.Name)); input.CopyTo(target); });
            if (!string.Equals(EpisodeTransactionStore.Hash(destination), file.Hash, StringComparison.Ordinal))
                throw new InvalidDataException("Processing engine changed during retention; no inputs are acknowledged.");
        }
        DurableFile.WriteJson(manifestPath, new EngineLeaseManifest { Identity = identity, Files = files });
        return new ProcessingEngineLease(directory, identity);
    }
    public static ProcessingEngineLease Open(string root, string identity)
    {
        if (identity == null || identity.Length != 64 || identity.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
            throw new ArgumentException("Invalid processing engine identity.");
        var directory = Path.Combine(Path.GetFullPath(root), identity);
        var manifest = DurableFile.ReadJson<EngineLeaseManifest>(Path.Combine(directory, "engine.json"), 128 * 1024);
        if (manifest.Version != 1 || manifest.Identity != identity || manifest.Files == null || manifest.Files.Length == 0 ||
            manifest.Files.Length > 256 || manifest.Files.Any(file => file == null || file.Hash == null || file.Hash.Length != 64) ||
            manifest.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length ||
            IdentityOf(manifest.Files) != identity) throw new InvalidDataException("Invalid processing engine manifest.");
        foreach (var file in manifest.Files)
        {
            ValidateName(file.Name);
            if (EpisodeTransactionStore.Hash(Path.Combine(directory, file.Name)) != file.Hash)
                throw new InvalidDataException("Retained processing engine checksum mismatch; inputs remain pending.");
        }
        if (Directory.EnumerateFiles(directory).Any(path => Loadable(path) && !manifest.Files.Any(file => file.Name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Retained engine contains an unlisted executable dependency.");
        return new ProcessingEngineLease(directory, identity);
    }
    private static bool Loadable(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase);
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name == "." || name == "..") throw new InvalidDataException("Invalid processing engine file name.");
    }
    private static string IdentityOf(IEnumerable<EngineLeaseFile> files)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("LS-ENGINE/1");
            foreach (var file in files.OrderBy(value => value.Name, StringComparer.Ordinal)) { writer.Write(file.Name); writer.Write(file.Hash); }
        }
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
    }
}
