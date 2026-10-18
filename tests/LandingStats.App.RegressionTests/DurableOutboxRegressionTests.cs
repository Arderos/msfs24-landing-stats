using System;
using System.IO;
using System.Linq;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class DurableOutboxRegressionTests
{
    public static void RunAll()
    {
        WithOutbox(WorkerAndPolicyFences);
        WithOutbox(RecoverReceiptAndQuarantine);
        WithOutbox(RetainCorruptAndUnrelatedFiles);
        WithOutbox(InvalidEntryDoesNotStopRecovery);
    }
    private static WorkerIdentity Worker(long generation, Guid? session = null) =>
        new WorkerIdentity(session ?? Guid.NewGuid(), Guid.NewGuid(), generation, ComponentRole.Upload);
    private static void InvalidEntryDoesNotStopRecovery(DurableOutbox box, string root, string queue)
    {
        var invalid = Register(box, queue, false); var good = Register(box, queue, false); var worker = Worker(1);
        var claim = box.TryClaim(good, worker)!;
        var entry = box.Read(invalid); entry.Hash = "invalid";
        DurableFile.WriteJson(Path.Combine(root, "outbox", invalid.ToString("N") + ".json"), entry);
        box.RecoverTerminalEntries(); box.RetireConfirmedWorker(worker); box.RecoverAfterPreviousRuntimeExited(Guid.NewGuid());
        Check(box.RecoveryErrors.Count == 3 && box.Read(good).State == OutboxState.Pending && File.Exists(claim.Path),
            "An invalid-data entry blocked recovery of another archive.");
        Check(box.Acknowledge(box.TryClaim(good, Worker(2))!), "A bad neighbor blocked a valid upload.");
        Check(File.Exists(Path.Combine(queue, invalid.ToString("N") + ".zip")), "Recovery deleted damaged archive evidence.");
    }
    private static Guid Register(DurableOutbox outbox, string queue, bool automatic)
    {
        var id = Guid.NewGuid(); var path = Path.Combine(queue, id.ToString("N") + ".zip");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        outbox.Register(id, path, EpisodeTransactionStore.Hash(path), automatic, DateTime.UtcNow);
        return id;
    }
    private static void WorkerAndPolicyFences(DurableOutbox box, string root, string queue)
    {
        box.SetAutomaticEnabled(true);
        var id = Register(box, queue, true); var worker = Worker(1); var claim = box.TryClaim(id, worker)!;
        Check(box.TryClaim(id, Worker(2)) == null, "Two workers owned one archive.");
        box.SetAutomaticEnabled(false);
        Check(!box.Acknowledge(claim) && !box.Reject(claim, 422) && File.Exists(claim.Path), "A late opted-out reply removed evidence.");
        box.RetireConfirmedWorker(worker);
        Check(box.TryClaim(id, Worker(2)) == null, "Opt-out did not survive worker retirement.");
        box = new DurableOutbox(Path.Combine(root, "outbox"), queue);
        Check(!box.Policy.AutomaticEnabled && box.TryClaim(id, Worker(2)) == null, "Opt-out did not survive app restart.");
        box.SetAutomaticEnabled(true);
        var successor = box.TryClaim(id, Worker(2))!;
        Check(successor.AttemptId != claim.AttemptId && successor.ContentHash == claim.ContentHash &&
            successor.PolicyRevision > claim.PolicyRevision, "Retry did not retain bytes with a fresh attempt/policy.");
        Check(!box.Acknowledge(claim) && !box.Retry(claim, "late"), "Old worker changed its successor's claim.");
        Check(box.Acknowledge(successor) && !File.Exists(claim.Path) && box.Read(id).State == OutboxState.Delivered,
            "Current receipt did not durably finish delivery.");
        box.Register(id, successor.Path, successor.ContentHash, true, DateTime.UtcNow);
        Check(box.TryClaim(id, Worker(3)) == null, "Replaying archive publication re-enqueued already delivered bytes.");
        var manual = Register(box, queue, false); var manualClaim = box.TryClaim(manual, Worker(3))!;
        box.SetAutomaticEnabled(false);
        Check(box.Acknowledge(manualClaim), "Automatic opt-out incorrectly cancelled the explicit manual claim.");
    }
    private static void RecoverReceiptAndQuarantine(DurableOutbox box, string root, string queue)
    {
        var worker = Worker(1); var id = Register(box, queue, false); var claim = box.TryClaim(id, worker)!;
        // A server commit whose response was lost leaves only a claimed file.
        box = new DurableOutbox(Path.Combine(root, "outbox"), queue);
        box.RecoverAfterPreviousRuntimeExited(Guid.NewGuid());
        var retry = box.TryClaim(id, Worker(1))!;
        Check(retry.AttemptId != claim.AttemptId && retry.ContentHash == claim.ContentHash && !box.Acknowledge(claim),
            "Crash recovery lost immutable bytes or accepted an orphaned result.");
        // Kill between durable receipt and unlink.
        var receipt = box.Read(id); receipt.State = OutboxState.Delivered; receipt.Version = 1; // Legacy absolute-path receipt.
        DurableFile.WriteJson(Path.Combine(root, "outbox", id.ToString("N") + ".json"), receipt);
        box.RecoverAfterPreviousRuntimeExited(Guid.NewGuid());
        Check(!File.Exists(retry.Path), "Receipt replay did not finish archive cleanup.");
        var rejected = Register(box, queue, false); var rejection = box.TryClaim(rejected, Worker(2))!;
        Check(box.Reject(rejection, 422), "Current permanent rejection was not accepted.");
        var entry = box.Read(rejected);
        Check(entry.State == OutboxState.Quarantined && !File.Exists(entry.Path) &&
            File.ReadAllBytes(entry.QuarantinePath).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Quarantine did not preserve exact bytes.");
        // Kill after move, before its final marker.
        entry.State = OutboxState.QuarantinePending; entry.Version = 1;
        DurableFile.WriteJson(Path.Combine(root, "outbox", rejected.ToString("N") + ".json"), entry);
        box.RecoverAfterPreviousRuntimeExited(Guid.NewGuid());
        Check(box.Read(rejected).State == OutboxState.Quarantined && box.TryClaim(rejected, Worker(3)) == null,
            "Quarantine recovery lost its terminal outcome.");
    }
    private static void RetainCorruptAndUnrelatedFiles(DurableOutbox box, string root, string queue)
    {
        var first = Register(box, queue, false); var worker = Worker(1); var claim = box.TryClaim(first, worker)!;
        File.WriteAllBytes(claim.Path, new byte[] { 9 });
        Throws<InvalidDataException>(() => box.Acknowledge(claim));
        Check(File.ReadAllBytes(claim.Path).SequenceEqual(new byte[] { 9 }) && box.Read(first).State == OutboxState.Claimed,
            "A changed archive was deleted or marked delivered.");
        var next = Register(box, queue, false); box.TryClaim(next, worker);
        var invalid = Path.Combine(root, "outbox", first.ToString("N") + ".json"); File.WriteAllText(invalid, "broken");
        box.RetireConfirmedWorker(worker);
        Check(box.Read(next).State == OutboxState.Pending && box.RecoveryErrors.Count == 1 && File.ReadAllText(invalid) == "broken",
            "A corrupt record blocked independent recovery or was removed.");
        var outside = Path.Combine(root, "outside.zip"); File.WriteAllBytes(outside, new byte[] { 5 });
        Throws<InvalidDataException>(() => box.Register(Guid.NewGuid(), outside, EpisodeTransactionStore.Hash(outside), false, DateTime.UtcNow));
        Check(File.Exists(outside), "Outbox modified an unowned path.");
    }
    private static void WithOutbox(Action<DurableOutbox, string, string> test)
    {
        var parent = Path.GetFullPath(Path.GetTempPath()); var root = Path.Combine(parent, "ob-" + Guid.NewGuid().ToString("N"));
        var queue = Path.Combine(root, "queue"); Directory.CreateDirectory(queue);
        try { test(new DurableOutbox(Path.Combine(root, "outbox"), queue), root, queue); }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test path.");
            Directory.Delete(root, true);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
