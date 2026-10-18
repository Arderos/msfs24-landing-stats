using System.Linq;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.Runtime.Contracts;

public interface IWorkerRole
{
    Task RunAsync(WorkerExecutionContext context);
}

public interface IWorkerTransport
{
    // Called by the owner loop; never performs pipe or disk I/O on that loop.
    bool TryPublish(RuntimePayload payload, long connectionGeneration, long captureQpc,
        Guid correlationId = default, long revision = 0, Guid episodeId = default, Guid lookupId = default, LookupStage stage = LookupStage.None);
    bool TryTakeCommand(out RuntimeEnvelope? command);
    int PendingCount { get; }
}

public sealed class WorkerExecutionContext
{
    public WorkerExecutionContext(WorkerIdentity identity, RuntimeClockEpoch clock, IWorkerTransport transport, CancellationToken cancellationToken)
    { Identity = identity; Clock = clock; Transport = transport; CancellationToken = cancellationToken; }
    public WorkerIdentity Identity { get; }
    public RuntimeClockEpoch Clock { get; }
    public IWorkerTransport Transport { get; }
    public CancellationToken CancellationToken { get; }
}

public static class RuntimeBuildIdentity
{
    public static string Read(string directory)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        using (var sha = SHA256.Create())
            foreach (var name in File.Exists(Path.Combine(directory, "LandingStats.Runtime.dll"))
                ? Directory.EnumerateFiles(directory).Select(Path.GetFileName).Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name == "LandingStats.WorkerHost.exe" || name == "LandingStats.WorkerHost.exe.config").OrderBy(name => name, StringComparer.Ordinal)
                : (System.Collections.Generic.IEnumerable<string>)new[] { "MSFS-Landing-Stats.exe", "LandingStats.Core.dll", "LandingStats.Runtime.Contracts.dll", "LandingStats.WorkerHost.exe" })
            {
                writer.Write(name);
                using var file = File.OpenRead(Path.Combine(directory, name));
                writer.Write(sha.ComputeHash(file));
            }
        using var result = SHA256.Create();
        return BitConverter.ToString(result.ComputeHash(buffer.ToArray())).Replace("-", "").ToLowerInvariant();
    }
}
