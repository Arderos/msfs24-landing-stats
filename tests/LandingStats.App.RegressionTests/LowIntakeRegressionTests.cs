using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;

namespace LandingStats.App.RegressionTests;

internal static class LowIntakeRegressionTests
{
    public static void RunAll() => Task.Run(RetireOnlyExplicitlyDeclinedWork).GetAwaiter().GetResult();

    private static async Task RetireOnlyExplicitlyDeclinedWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-low-intake-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy);
        var source = Path.Combine(legacy, "unknown_raw.zip"); var original = new byte[] { 7, 4, 2 };
        File.WriteAllBytes(source, original);
        var archives = Path.Combine(root, "archives");
        var outbox = new DurableOutbox(Path.Combine(root, "outbox"), archives);
        var projection = new RuntimePresentation((_, __) => Task.CompletedTask);
        var failed = new ConcurrentQueue<Guid>(); var recovered = new ConcurrentQueue<Guid>();
        var consent = 1;
        var intake = new ArchiveIntakeOwner(Path.Combine(root, "intake"), archives, legacy, Path.Combine(root, "identity"),
            new EpisodeTransactionStore(Path.Combine(root, "episodes"), new string('a', 64)), outbox, () => { },
            readLegacyConsent: () => Volatile.Read(ref consent) == 2 ? throw new IOException("Consent is temporarily locked.") : Volatile.Read(ref consent) == 1);
        intake.Failed += (id, reason) => { failed.Enqueue(id); projection.Failure("storage.reports", id, reason); };
        intake.Recovered += id => { recovered.Enqueue(id); projection.DeliveryRecovered(id); };
        using var sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
        FileStream? manualLock = null;
        using var generation = new ManagedComponentGeneration(1, intake.RunAsync);
        try
        {
            await generation.StartAsync(CancellationToken.None);
            await Until(() => failed.Count == 1, "Blocked legacy archive did not publish its failure.");
            var legacyFailure = failed.Single();
            var manual = Path.Combine(legacy, "manual_bug_raw.zip"); File.WriteAllBytes(manual, new byte[] { 3, 5, 8 });
            manualLock = new FileStream(manual, FileMode.Open, FileAccess.Read, FileShare.None);
            intake.Wake();
            await Until(() => failed.Distinct().Count() == 2, "Independent manual request did not retain its own failure.");
            var manualFailure = failed.Single(id => id != legacyFailure);
            Volatile.Write(ref consent, 2); intake.Wake();
            await Until(() => failed.Distinct().Count() == 3, "Unreadable consent did not report its independent dependency failure.");
            Check(!recovered.Contains(legacyFailure), "Unreadable consent was treated as explicit opt-out.");
            Volatile.Write(ref consent, 0); intake.Wake();
            await Until(() => recovered.Contains(legacyFailure) && projection.Snapshot.DeliveryFailure?.Entity == manualFailure,
                "Explicit opt-out retained a stale legacy error or hid the independent manual error.");
            Check(File.Exists(source) && !outbox.Enumerate().Any(), "Opt-out discarded or admitted the unclassified report.");
            manualLock.Dispose(); intake.Wake();
            await Until(() => projection.Snapshot.DeliveryFailure == null && !File.Exists(manual),
                "Manual delivery did not recover independently of legacy opt-out.");
            sourceLock.Dispose();
            Check(File.ReadAllBytes(source).SequenceEqual(original) && outbox.Enumerate().Count == 1 && !outbox.Read(outbox.Enumerate().Single()).Automatic,
                "Consent retirement changed source bytes or blocked explicitly requested delivery.");
        }
        finally
        {
            sourceLock.Dispose(); manualLock?.Dispose(); await generation.StopAsync(TimeSpan.FromSeconds(10));
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }
    private static async Task Until(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(message); await Task.Delay(25); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
