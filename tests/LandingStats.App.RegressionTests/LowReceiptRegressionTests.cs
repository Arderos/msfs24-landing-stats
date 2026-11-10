using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class LowReceiptRegressionTests
{
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-low-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UnreadableReceiptRetainsRecovery(Path.Combine(root, "unreadable"));
            DirectoryShardRetainsRecovery(Path.Combine(root, "directory"));
            ConfirmedAbsenceRetiresRecovery(Path.Combine(root, "absent"));
        }
        finally
        {
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Unsafe receipt fixture cleanup.");
            Directory.Delete(root, true);
        }
    }

    private static void UnreadableReceiptRetainsRecovery(string root)
    {
        var fixture = new CompletedReceipt(root);
        fixture.Corrupt();
        File.WriteAllBytes(fixture.Shard, fixture.Original);
        var recovered = 0; fixture.Box.Recovered += _ => recovered++;
        using (var held = new FileStream(fixture.Shard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Check(fixture.Box.TryRead(fixture.Id, refresh: true) == null && fixture.Box.HasRecoveryFailures &&
                File.Exists(fixture.Quarantine) && recovered == 0,
                "An exclusively locked receipt was mistaken for deletion of its missing active record.");
        }
        // File attributes remain visible on some NTFS configurations despite
        // this ACL. Reading the real receipt must still fail, never prove absence.
        var originalAccess = File.GetAccessControl(fixture.Shard, AccessControlSections.Access);
        var originalDescriptor = originalAccess.GetSecurityDescriptorBinaryForm();
        var deniedAccess = File.GetAccessControl(fixture.Shard, AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        deniedAccess.AddAccessRule(new FileSystemAccessRule(identity.User!,
            FileSystemRights.ReadData | FileSystemRights.ReadAttributes, AccessControlType.Deny));
        try
        {
            File.SetAccessControl(fixture.Shard, deniedAccess);
            var denied = false;
            try { File.ReadAllBytes(fixture.Shard); }
            catch (UnauthorizedAccessException) { denied = true; }
            Check(denied, "Temporary receipt ACL did not deny actual file access.");
            Check(fixture.Box.TryRead(fixture.Id, refresh: true) == null && fixture.Box.HasRecoveryFailures &&
                File.Exists(fixture.Quarantine) && recovered == 0,
                "An inaccessible existing receipt retired pending quarantine recovery.");
        }
        finally
        {
            // FileSecurity persists only modified sections. Mark the saved DACL
            // for publication rather than passing back an untouched snapshot.
            originalAccess.SetSecurityDescriptorBinaryForm(originalDescriptor, AccessControlSections.Access);
            File.SetAccessControl(fixture.Shard, originalAccess);
        }
        fixture.AssertRecovered();
        Check(recovered == 1, "Receipt recovery was not reported exactly when accessible data proved success.");
    }

    private static void DirectoryShardRetainsRecovery(string root)
    {
        var fixture = new CompletedReceipt(root);
        fixture.Corrupt();
        var retained = fixture.Shard + ".retained";
        var corrupted = File.ReadAllBytes(fixture.Shard);
        File.Move(fixture.Shard, retained);
        Directory.CreateDirectory(fixture.Shard);
        var recovered = 0; fixture.Box.Recovered += _ => recovered++;
        try
        {
            Check(!new FileInfo(fixture.Shard).Exists, "Directory fixture does not exercise the former false-absence branch.");
            Check(fixture.Box.TryRead(fixture.Id, refresh: true) == null && fixture.Box.HasQuarantinedEntries &&
                File.Exists(fixture.Quarantine) && recovered == 0 && File.ReadAllBytes(retained).SequenceEqual(corrupted),
                "A directory at the receipt shard path was treated as absent and retired its recovery obligation.");
            var receipts = new DurableReceiptIndex<OutboxEntry>(Path.Combine(fixture.OutboxRoot, "receipts"));
            var rejected = false;
            try { receipts.Contains(fixture.Id); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "The shared receipt index accepted a directory as an empty shard.");
        }
        finally
        {
            Directory.Delete(fixture.Shard);
            File.Move(retained, fixture.Shard);
        }
        File.WriteAllBytes(fixture.Shard, fixture.Original);
        fixture.AssertRecovered();
        Check(recovered == 1, "Repair did not retire the directory-shaped shard failure.");
    }

    private static void ConfirmedAbsenceRetiresRecovery(string root)
    {
        var outboxRoot = Path.Combine(root, "outbox"); var archives = Path.Combine(root, "archives");
        Directory.CreateDirectory(archives);
        var box = new DurableOutbox(outboxRoot, archives); var id = Guid.NewGuid();
        var archive = Path.Combine(archives, id.ToString("N") + ".zip");
        File.WriteAllBytes(archive, new byte[] { 7, 8, 9 }); var hash = EpisodeTransactionStore.Hash(archive);
        box.Register(id, archive, hash, false, DateTime.UtcNow);
        var metadata = Path.Combine(outboxRoot, id.ToString("N") + ".json");
        var quarantine = Path.Combine(outboxRoot, "quarantine", id.ToString("N") + ".json");
        File.WriteAllText(metadata, "{broken metadata");
        Check(box.TryRead(id, refresh: true) == null && box.HasQuarantinedEntries && File.Exists(quarantine), "Missing-entry fixture did not establish quarantine.");
        File.Delete(metadata);
        Check(box.TryRead(id, refresh: true) == null && !box.HasRecoveryFailures && !box.HasQuarantinedEntries &&
            !File.Exists(quarantine) && EpisodeTransactionStore.Hash(archive) == hash,
            "Confirmed absence retained impossible work or deleted the independent archive bytes.");
        var receipts = new DurableReceiptIndex<OutboxEntry>(Path.Combine(outboxRoot, "receipts"));
        Check(!receipts.Contains(id), "An absent receipt directory was not recognized as empty.");
        Directory.CreateDirectory(Path.Combine(outboxRoot, "receipts"));
        Check(!receipts.Contains(id), "An absent shard in an existing receipt directory was not recognized as empty.");
    }

    private sealed class CompletedReceipt
    {
        public CompletedReceipt(string root)
        {
            OutboxRoot = Path.Combine(root, "outbox"); var archives = Path.Combine(root, "archives");
            Directory.CreateDirectory(archives); Box = new DurableOutbox(OutboxRoot, archives); Id = Guid.NewGuid();
            var archive = Path.Combine(archives, Id.ToString("N") + ".zip"); File.WriteAllBytes(archive, new byte[] { 1, 2, 3 });
            Box.Register(Id, archive, EpisodeTransactionStore.Hash(archive), false, DateTime.UtcNow);
            var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload);
            Check(Box.Acknowledge(Box.TryClaim(Id, worker)!), "Receipt fixture delivery did not complete.");
            Check(!File.Exists(Path.Combine(OutboxRoot, Id.ToString("N") + ".json")), "Terminal receipt was not compacted out of active metadata.");
            Shard = Path.Combine(OutboxRoot, "receipts", Id.ToString("N").Substring(0, 2) + ".json");
            Original = File.ReadAllBytes(Shard);
            Quarantine = Path.Combine(OutboxRoot, "quarantine", Id.ToString("N") + ".json");
        }
        public readonly DurableOutbox Box;
        public readonly Guid Id;
        public readonly string OutboxRoot, Shard, Quarantine;
        public readonly byte[] Original;
        public void Corrupt()
        {
            File.WriteAllText(Shard, "{broken receipt");
            Check(Box.TryRead(Id, refresh: true) == null && Box.HasQuarantinedEntries && File.Exists(Quarantine),
                "Damaged compacted receipt did not retain a recovery obligation.");
        }
        public void AssertRecovered()
        {
            var entry = Box.TryRead(Id, refresh: true);
            var evidence = " State=" + (entry?.State.ToString() ?? "unreadable") +
                "; recovery=" + Box.HasRecoveryFailures + "; quarantined=" + Box.HasQuarantinedEntries +
                "; sidecar=" + File.Exists(Quarantine) + "; last failure=" + Box.RecoveryErrors.LastOrDefault();
            Check(entry?.State == OutboxState.Delivered, "Repaired receipt did not restore its delivered state." + evidence);
            Check(!Box.HasRecoveryFailures, "Repaired receipt retained a pending storage operation." + evidence);
            Check(!Box.HasQuarantinedEntries, "Repaired receipt retained its quarantine status." + evidence);
            Check(!File.Exists(Quarantine), "Repaired receipt retained its durable quarantine sidecar." + evidence);
            Check(File.ReadAllBytes(Shard).SequenceEqual(Original), "Repaired receipt bytes differ from the original committed receipt." + evidence);
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
