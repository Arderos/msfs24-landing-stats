using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RestartReadinessRegressionTests
{
    public static void RunAll() => Task.Run(CheckReadiness).GetAwaiter().GetResult();

    private static async Task CheckReadiness()
    {
        var identity = new RecoveryJournalIdentity(Guid.NewGuid(), Guid.NewGuid(),
            new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        var callbacks = 0; var commits = 0;
        var presentation = new RuntimePresentation((_, _) => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; });
        TaskCompletionSource<bool>? holdCommit = null;
        TaskCompletionSource<bool>? commitEntered = null;
        var recording = new RecordingRuntimeOwner(identity, async (input, effects) =>
        {
            presentation.Recording(identity, effects);
            var held = holdCommit;
            if (held != null) { commitEntered!.TrySetResult(true); await held.Task.ConfigureAwait(false); }
            Interlocked.Increment(ref commits);
        }, (_, _) => Task.CompletedTask, error => throw new InvalidOperationException("Unexpected recording failure.", error));
        using var owner = new ManagedComponentGeneration(1, recording.RunAsync);
        using var ui = new ManagedComponentGeneration(1, presentation.RunAsync);
        using var lifetime = new CancellationTokenSource();
        long sequence = 0;
        DurableRuntimeInput Input(JournalRecordKind kind, byte[] bytes) => new DurableRuntimeInput(
            new RuntimeInput(Guid.NewGuid(), kind, Stopwatch.GetTimestamp(), identity.Clock.OriginQpc, DateTime.UtcNow, bytes),
            new JournalCursor(1, ++sequence));
        try
        {
            await owner.StartAsync(CancellationToken.None);
            await ui.StartAsync(CancellationToken.None);
            await recording.RestoreAsync(null, null);
            var ready = recording.WaitForSafeRestartAsync(lifetime.Token);
            Require(!ready.IsCompleted, "New recording state bypassed the stable safe-state policy.");
            using (var canceled = new CancellationTokenSource())
            {
                var separateWait = recording.WaitForSafeRestartAsync(canceled.Token);
                canceled.Cancel();
                await Canceled(separateWait);
                Require(!ready.IsCompleted, "Canceling one waiter changed another waiter's readiness.");
            }
            await Deadline(ready);
            Require(recording.SafeToRestart && presentation.Snapshot.Revision == 0,
                "Natural safe transition required an unrelated presentation event.");
            Require(recording.WaitForSafeRestartAsync(lifetime.Token).IsCompleted,
                "A caller arriving after the safe transition lost its notification.");

            // Restore invalidates the old generation's permission before replay starts.
            var restore = recording.RestoreAsync(null, null);
            Require(!recording.SafeToRestart, "Restore retained the previous safe permission.");
            var recovered = recording.WaitForSafeRestartAsync(lifetime.Token);
            Require(!recovered.IsCompleted, "Restored state reused a completed readiness signal.");
            await Deadline(restore); await Deadline(recovered);

            // An input already accepted but still committing must revoke readiness.
            holdCommit = Signal(); commitEntered = Signal();
            await recording.PublishAsync(Input(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.Tick).Encode()));
            await Deadline(commitEntered.Task);
            var committing = recording.WaitForSafeRestartAsync(lifetime.Token);
            Require(!recording.SafeToRestart && !committing.IsCompleted, "In-flight effects retained stale restart permission.");
            holdCommit.TrySetResult(true); holdCommit = null;
            await Deadline(committing);
            var beforeHello = Volatile.Read(ref commits);

            // A connected simulator without ground evidence is unsafe even if the
            // previous idle state had already passed its five-second policy.
            var simulator = new WorkerIdentity(identity.RuntimeId, Guid.NewGuid(), 1, ComponentRole.Simulator);
            await recording.PublishAsync(Input(JournalRecordKind.SourceMessage, RuntimeWire.Encode(
                new RuntimeEnvelope(simulator, 0, 1, Stopwatch.GetTimestamp(),
                    new WorkerHello("fixture", new string('a', 64), 1, identity.Clock.Id, identity.Clock.Frequency)))));
            await Until(() => Volatile.Read(ref commits) > beforeHello);
            holdCommit = Signal(); commitEntered = Signal();
            await recording.PublishAsync(Input(JournalRecordKind.SourceMessage, RuntimeWire.Encode(
                new RuntimeEnvelope(simulator, 1, 2, Stopwatch.GetTimestamp(), new LifecycleMessage(SourceLifecycle.Connected)))));
            await Deadline(commitEntered.Task);
            var unsafeWait = recording.WaitForSafeRestartAsync(lifetime.Token);
            Require(!unsafeWait.IsCompleted, "Unknown simulator ground state authorized restart.");
            holdCommit.TrySetResult(true); holdCommit = null;
            var closing = recording.CloseIntakeAsync();
            try { await Deadline(unsafeWait); throw new InvalidOperationException("Closed recording left its readiness waiter successful."); }
            catch (InvalidOperationException error) when (error.Message == "Recording intake has stopped.") { }
            await Deadline(closing);
        }
        finally
        {
            lifetime.Cancel(); holdCommit?.TrySetResult(true);
            await recording.CloseIntakeAsync();
            await owner.StopAsync(TimeSpan.FromSeconds(2));
            await ui.StopAsync(TimeSpan.FromSeconds(2));
        }
    }
    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Deadline(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(12))).ConfigureAwait(false) != task)
            throw new TimeoutException("Restart readiness did not complete.");
        await task.ConfigureAwait(false);
    }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Recording input did not commit.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
    private static async Task Canceled(Task task)
    {
        try { await Deadline(task); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Readiness ignored cancellation.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
