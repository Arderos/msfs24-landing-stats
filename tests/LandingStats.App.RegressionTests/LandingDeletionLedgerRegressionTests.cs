using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class LandingDeletionLedgerRegressionTests
{
    internal static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "deletion-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ledger = new LandingDeletionLedger(root);
            var ids = new List<string>();
            for (var i = 0; ids.Count < LandingDeletionLedger.MaximumShardEntries + 2; i++)
            {
                var id = "deleted-synthetic-" + i;
                if (Hash(id)[0] < 16) ids.Add(id); // same first hexadecimal shard
            }
            // Real legacy receipts populate a full shard without using the new
            // format as a fixture-writing shortcut.
            foreach (var id in ids.Take(LandingDeletionLedger.MaximumShardEntries)) WriteLegacy(root, id);
            foreach (var id in ids.Take(LandingDeletionLedger.MaximumShardEntries))
                Check(ledger.Contains(id), "Legacy deletion lost during migration.");
            Check(!Directory.EnumerateFiles(Path.Combine(root, ".deleted"), "*.del").Any(), "Verified legacy receipts were not compacted.");
            var parent = Path.Combine(root, ".deleted", "index-v1", "0", "entries.bin");
            var parentBytes = File.ReadAllBytes(parent);
            var added = ids[LandingDeletionLedger.MaximumShardEntries];
            var child = Hex(Hash(added)[0] & 15);
            var barrier = Path.Combine(Path.GetDirectoryName(parent)!, child, "entries.bin");
            Directory.CreateDirectory(barrier);
            try { ledger.Add(added); throw new InvalidOperationException("Blocked split unexpectedly committed."); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Check(File.ReadAllBytes(parent).SequenceEqual(parentBytes), "Failed split removed its authoritative parent.");
            ledger = new LandingDeletionLedger(root);
            foreach (var id in ids.Take(LandingDeletionLedger.MaximumShardEntries))
                Check(ledger.Contains(id), "Interrupted split resurrected an old deletion.");
            Directory.Delete(barrier);
            ledger.Add(added);
            Check(!File.Exists(parent), "Completed split retained an unnecessary parent copy.");
            // Crash after child publication but before parent removal: both
            // generations coexist. Retry must merge children, not replace them.
            File.WriteAllBytes(parent, parentBytes);
            ledger = new LandingDeletionLedger(root); ledger.Add(ids.Last());
            foreach (var id in ids) Check(ledger.Contains(id), "Restart/split retry lost a permanent deletion.");
            Check(!ledger.Contains("never-deleted-synthetic"), "Index reports an unrelated landing as deleted.");
            var paths = Directory.GetFiles(Path.Combine(root, ".deleted", "index-v1"), "entries.bin", SearchOption.AllDirectories);
            var before = paths.ToDictionary(path => path, File.ReadAllBytes);
            ledger.Add(ids.Last());
            Check(before.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "Duplicate Add rewrote a committed deletion.");
            Check(paths.Length < ids.Count / 4 && paths.All(path => new FileInfo(path).Length <= LandingDeletionLedger.MaximumShardBytes),
                "Deletion shards did not reduce file count or exceeded the bounded read size.");

            var damaged = "damaged-legacy-payload";
            var damagedPath = WriteLegacy(root, damaged); File.WriteAllText(damagedPath, "unrelated payload");
            Check(new LandingDeletionLedger(root).Contains(damaged) && File.Exists(damagedPath),
                "An unverifiable legacy payload removed its durable filename witness.");

            var blockedRoot = Path.Combine(root, "blocked-migration");
            var legacy = WriteLegacy(blockedRoot, "retained-until-published");
            var blockedIndex = Path.Combine(blockedRoot, ".deleted", "index-v1"); File.WriteAllText(blockedIndex, "blocked");
            Check(new LandingDeletionLedger(blockedRoot).Contains("retained-until-published") && File.Exists(legacy),
                "Optional migration failure discarded the old receipt.");
            File.Delete(blockedIndex);
            Check(new LandingDeletionLedger(blockedRoot).Contains("retained-until-published") && !File.Exists(legacy),
                "Recovered migration did not resume idempotently.");

            var lastPath = Path.Combine(root, ".deleted", "index-v1", "0", Hex(Hash(ids.Last())[0] & 15), "entries.bin");
            var intact = File.ReadAllBytes(lastPath); var corrupt = (byte[])intact.Clone(); corrupt[12] ^= 1;
            File.WriteAllBytes(lastPath, corrupt);
            try { new LandingDeletionLedger(root).Contains(ids.Last()); throw new InvalidOperationException("Corrupt shard permitted resurrection."); }
            catch (InvalidDataException) { }
            File.WriteAllBytes(lastPath, intact);
            Check(new LandingDeletionLedger(root).Contains(ids.Last()), "Byte-preserved shard recovery lost its deletion.");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private static string WriteLegacy(string root, string id)
    {
        var directory = Path.Combine(root, ".deleted"); Directory.CreateDirectory(directory);
        var name = Convert.ToBase64String(Hash(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var path = Path.Combine(directory, name + ".del"); File.WriteAllText(path, id, new UTF8Encoding(false)); return path;
    }
    private static byte[] Hash(string id) { using var sha = SHA256.Create(); return sha.ComputeHash(Encoding.UTF8.GetBytes(id)); }
    private static string Hex(int value) => "0123456789abcdef"[value].ToString();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
