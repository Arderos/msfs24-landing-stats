using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RuntimeRetentionRegressionTests
{
    public static void RunAll()
    {
        SharedQuotaPreservesCommittedBytes();
        ConcurrentProcessesCannotSpendTheSameBudget();
        ScratchRequiresActualRetirement();
        SessionCollectionRequiresBothCompletions();
        EnginesStayPinnedUntilEveryOwnerFinishes();
    }
    public static int Writer(string root, string prefix)
    {
        try
        {
            for (var i = 0; i < 256; i++)
                DurableFile.Write(Path.Combine(root, prefix + "-" + i + ".bin"), output => output.Write(new byte[32768], 0, 32768));
            return 1;
        }
        catch (IOException) { return 0; }
    }
    private static RuntimeStorageLimits Limits() => new RuntimeStorageLimits { Bytes = 2 * 1024 * 1024, MetadataReserve = 128 * 1024, Quantum = 4096 };
    private static void SharedQuotaPreservesCommittedBytes()
    {
        var root = Temporary();
        try
        {
            using var quota = RuntimeStorageQuota.Attach(root, Limits());
            var destination = Path.Combine(root, "immutable.bin"); var original = new byte[] { 7, 4, 9 };
            DurableFile.Write(destination, stream => stream.Write(original, 0, original.Length));
            Throws<IOException>(() => DurableFile.Write(destination, stream => stream.Write(new byte[4 * 1024 * 1024], 0, 4 * 1024 * 1024)));
            Check(File.ReadAllBytes(destination).SequenceEqual(original), "Quota failure replaced committed bytes.");
            Check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "Failed write left temporary growth.");
            var payload = Enumerable.Repeat((byte)9, 65536).ToArray();
            var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 0, 1000));
            var journalPath = Path.Combine(root, "accepted.wal"); long acknowledged = 0;
            using (var journal = RecoveryJournal.Open(journalPath, identity))
            {
                try { for (var i = 0; i < 100; i++) { journal.Append(JournalRecordKind.SourceMessage, i + 1, payload); acknowledged = journal.Commit().Sequence; } }
                catch (IOException) { }
                Check(acknowledged > 0 && journal.DurableSequence == acknowledged, "Quota failure invented an ACK.");
            }
            DurableFile.Write(Path.Combine(root, "completion.json"), stream => stream.Write(new byte[2048], 0, 2048));
            Check(new FileInfo(journalPath).Length >= acknowledged * payload.Length, "Durable prefix was removed.");
            Check(Bytes(root) <= Limits().Bytes, "Physical budget exceeded.");
            File.Delete(journalPath);
            DurableFile.Write(Path.Combine(root, "recovered.bin"), stream => stream.Write(payload, 0, payload.Length));
            // Lost/corrupted accounting is reconstructed conservatively from
            // files and live writer credits, never reset to zero.
            var counter = Path.Combine(root, ".quota", "allocated"); var corrupted = File.ReadAllBytes(counter);
            corrupted[4] ^= 64; File.WriteAllBytes(counter, corrupted);
            Check(Writer(root, "after-corruption") == 0 && Bytes(root) <= Limits().Bytes, "Corrupt counter bypassed quota.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void ConcurrentProcessesCannotSpendTheSameBudget()
    {
        var root = Temporary();
        try
        {
            using var quota = RuntimeStorageQuota.Attach(root, Limits());
            Process Start(string id) => Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--quota-writer \"" + root + "\" " + id) { UseShellExecute = false, CreateNoWindow = true })!;
            using var first = Start("first"); using var second = Start("second");
            Check(first.WaitForExit(30000) && second.WaitForExit(30000), "Quota writers did not terminate.");
            Check(first.ExitCode == 0 && second.ExitCode == 0, "A writer did not reach the shared limit.");
            Check(Bytes(root) <= Limits().Bytes, "Concurrent processes exceeded shared disk budget.");
            Check(Directory.EnumerateFiles(root, "first-*.bin").Any() && Directory.EnumerateFiles(root, "second-*.bin").Any(), "Only one writer exercised admission.");
            foreach (var file in Directory.EnumerateFiles(root, "*.bin")) File.Delete(file);
            DurableFile.Write(Path.Combine(root, "retry.bin"), stream => stream.Write(new byte[1024 * 1024], 0, 1024 * 1024));
            Check(Bytes(root) <= Limits().Bytes, "Dead writer reservation was not recovered safely.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void ScratchRequiresActualRetirement()
    {
        var root = Temporary();
        try
        {
            var id = Guid.NewGuid(); var directory = Path.Combine(root, id.ToString("N"));
            var parent = RuntimeScratchLease.Create(root, id);
            using (var worker = RuntimeScratchLease.EnterWorker(directory))
            {
                DurableFile.Write(Path.Combine(directory, "result.bin"), stream => stream.WriteByte(7));
                Throws<IOException>(() => RuntimeScratchLease.Collect(root, directory));
                Check(File.Exists(Path.Combine(directory, "result.bin")), "Active scratch was collected.");
            }
            parent.Dispose(); Check(!Directory.Exists(directory), "Retired scratch was not collected.");
            Throws<IOException>(() => RuntimeScratchLease.EnterWorker(directory));
            var unknown = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "evidence"), "keep");
            Check(!RuntimeScratchLease.Collect(root, unknown) && Directory.Exists(unknown), "Unknown orphan was deleted.");
            Throws<ArgumentException>(() => RuntimeScratchLease.Collect(root, Path.GetTempPath()));
        }
        finally { Directory.Delete(root, true); }
    }
    private static void SessionCollectionRequiresBothCompletions()
    {
        var root = Temporary();
        try
        {
            var engine = new string('a', 64); var sessions = Path.Combine(root, "sessions");
            string Create(bool recovered, bool raw, bool torn = false)
            {
                var id = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 0, 1000));
                var directory = Path.Combine(sessions, id.RuntimeId.ToString("N")); RuntimeSession.Create(directory, id, engine);
                Directory.CreateDirectory(Path.Combine(directory, "journal")); File.WriteAllBytes(Path.Combine(directory, "journal", "accepted.wal"), new byte[100]);
                if (recovered) DurableFile.WriteJson(Path.Combine(directory, "recovered.json"), new RecordingRecoveryBundle
                    { Runtime = id.RuntimeId, Engine = engine, LastSegment = 1, RecoveredTail = torn });
                if (raw) DurableFile.WriteJson(Path.Combine(directory, "raw-completed.json"), new RuntimeRawCompletion { Runtime = id.RuntimeId });
                return directory;
            }
            var current = Create(true, true); var completed = Create(true, true); var pending = Create(true, false); var corrupt = Create(true, true, true);
            var owner = new RuntimeRetentionOwner(root, current, new EpisodeTransactionStore(Path.Combine(root, "episodes"), engine));
            owner.Collect(); owner.Collect();
            Check(!Directory.Exists(completed), "Completed session retained a permanent directory.");
            Check(Directory.Exists(Path.Combine(current, "journal")) && Directory.Exists(Path.Combine(pending, "journal")), "Collection removed live or pending data.");
            Check(!Directory.Exists(corrupt) && File.Exists(Path.Combine(root, "evidence", Path.GetFileName(corrupt), "journal", "accepted.wal")), "Collection discarded repaired-session evidence.");
            Throws<DirectoryNotFoundException>(() => RuntimeSession.AcquireExistingOwnership(completed));
            Check(!Directory.Exists(completed), "A late reader recreated a collected session.");
            Check(!RuntimeSessionDirectories.Read(sessions).Contains(completed), "Collected session re-entered recovery.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void EnginesStayPinnedUntilEveryOwnerFinishes()
    {
        var root = Temporary();
        try
        {
            var engines = Path.Combine(root, "engines"); var source = Path.Combine(root, "deployment"); Directory.CreateDirectory(source);
            ProcessingEngineLease Engine(byte value)
            {
                File.WriteAllBytes(Path.Combine(source, "sample.dll"), new[] { value });
                return ProcessingEngineLease.CaptureFiles(engines, source, new[] { "sample.dll" });
            }
            var current = Engine(1); var pending = Engine(2); var running = Engine(3); var unused = Engine(4); var recoverable = Engine(5);
            var store = new EpisodeTransactionStore(Path.Combine(root, "episodes"), current.Identity);
            var id = Guid.NewGuid(); var episode = Path.Combine(store.Root, id.ToString("N"));
            DurableFile.WriteJson(Path.Combine(episode, "manifest.json"), new EpisodeWorkManifest
                { Id = id, Engine = pending.Identity, ArchiveId = Guid.NewGuid(), Stage = EpisodeWorkStage.InputsCommitted, InputHash = new string('b', 64) });
            var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(), new RuntimeClockEpoch(Guid.NewGuid(), 0, 1000));
            var session = Path.Combine(root, "sessions", identity.RuntimeId.ToString("N")); RuntimeSession.Create(session, identity, recoverable.Identity);
            var collector = new RuntimeRetentionOwner(root, "none", store);
            using (running.AcquireUse())
            {
                collector.Collect();
                Check(Directory.Exists(current.DirectoryPath) && Directory.Exists(pending.DirectoryPath) && Directory.Exists(running.DirectoryPath) &&
                    Directory.Exists(recoverable.DirectoryPath), "Collector removed a pinned processing engine.");
                Check(!Directory.Exists(unused.DirectoryPath), "Unused engine was never collected.");
            }
            collector.Collect(); Check(!Directory.Exists(running.DirectoryPath), "Retired job kept its engine permanently pinned.");
            // An unreadable manifest is not permission to assume no references.
            var preserved = Engine(6); File.WriteAllText(Path.Combine(episode, "manifest.json"), "broken");
            collector.Collect(); Check(Directory.Exists(preserved.DirectoryPath), "Invalid manifest allowed unsafe engine collection.");
            var future = Engine(7); collector.Collect();
            Check(!Directory.Exists(future.DirectoryPath) && Directory.Exists(preserved.DirectoryPath), "A legacy corrupt manifest pinned all future engines instead of its conservative snapshot.");
            File.WriteAllText(Path.Combine(episode, "metadata-failure.json"), "broken receipt");
            collector.Collect(); var afterRepair = Engine(8); collector.Collect();
            Check(!Directory.Exists(afterRepair.DirectoryPath) && Directory.Exists(preserved.DirectoryPath) &&
                Directory.GetFiles(episode, "metadata-failure.json.corrupt-*").Length == 1,
                "A damaged failure receipt blocked collection or discarded evidence.");
            DurableFile.WriteJson(Path.Combine(episode, "manifest.json"), new EpisodeWorkManifest { Id = id, Engine = "invalid" });
            collector.Collect(); Check(Directory.Exists(preserved.DirectoryPath), "Semantically invalid manifest allowed unsafe collection.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static long Bytes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    private static string Temporary() { var path = Path.Combine(Path.GetTempPath(), "ls-retention-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
