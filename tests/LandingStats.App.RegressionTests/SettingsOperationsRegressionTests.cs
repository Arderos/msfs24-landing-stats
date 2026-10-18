using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Settings;

namespace LandingStats.App.RegressionTests;

internal static class SettingsOperationsRegressionTests
{
    public static void RunAll()
    {
        QueueSerializesAndDrainsAcceptedWork().GetAwaiter().GetResult();
        SaveSnapshotsAndRollbackStayOffCaller().GetAwaiter().GetResult();
        CachedTokenStateDoesNotWaitForDiskWriter();
        CommittedSettingsDoNotDependOnTheDispatcher();
    }

    private static void CommittedSettingsDoNotDependOnTheDispatcher()
    {
        var root = Path.Combine(Path.GetTempPath(), "settings-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var complete = false;
        try
        {
            var repository = new ApplicationSettingsRepository(Path.Combine(root, "settings.json"));
            var baseline = new ApplicationSettings { Language = "en", AutomaticTelemetryUpload = false };
            repository.Save(baseline);
            var owner = new ApplicationSettingsOwner(repository, new SimulatorAutoStartManager());
            var read = owner.Read();
            var cloud = owner.ReadCloudAsync().GetAwaiter().GetResult();
            var expectedHash = GoogleDriveBackupService.Hash(GoogleDriveCloudSettings.Serialize(cloud.Settings));
            // A failing view notification cannot roll back or falsely fail a committed transaction.
            owner.Changed += () => throw new InvalidOperationException("fixture projection unavailable");
            Assert(Task.Run(() => owner.ApplyCloudAsync(new GoogleDriveCloudSettings { Language = "ru" }, expectedHash)).GetAwaiter().GetResult(),
                "cloud settings required a dispatcher or rejected the committed hash");
            var local = baseline.Clone(); local.AutomaticTelemetryUpload = true;
            owner.SaveAsync(baseline, local).GetAwaiter().GetResult();
            var stored = repository.Load();
            Assert(stored.Language == "ru" && stored.AutomaticTelemetryUpload && local.Language == "ru",
                "a local edit based on an older view overwrote concurrent cloud settings");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var next = owner.Snapshot.Settings.Clone(); next.GoogleDrivePromptAnswered = true;
            var saving = owner.SaveAsync(owner.Snapshot.Settings, next, () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); });
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "settings owner did not begin the accepted save");
            var pendingRead = owner.ReadCloudAsync();
            Assert(!pendingRead.IsCompleted, "cloud observed settings before an accepted save committed");
            release.Set(); saving.GetAwaiter().GetResult();
            Assert(pendingRead.GetAwaiter().GetResult().Settings.Language == "ru" && repository.Load().GoogleDrivePromptAnswered,
                "cloud read or local save required an unavailable UI continuation");
            Assert(!owner.ApplyCloudAsync(new GoogleDriveCloudSettings { Language = "en" }, expectedHash).GetAwaiter().GetResult(),
                "cloud overwrote settings after its expected hash became stale");
            complete = true;
        }
        finally { if (complete && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }

    private static async Task QueueSerializesAndDrainsAcceptedWork()
    {
        var queue = new SettingsOperationQueue();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new ApplicationSettings();
        var secondStarted = false;
        var first = queue.RunAsync(async () =>
        {
            await release.Task;
            committed.Language = "ru";
            return true;
        }, false);
        var second = queue.RunAsync(() =>
        {
            secondStarted = true;
            // The snapshot is taken when the operation actually starts, not
            // when it is queued behind another local or cloud mutation.
            var snapshot = committed.Clone();
            Assert(snapshot.Language == "ru", "queued snapshot sees prior committed language");
            snapshot.GoogleDrivePromptAnswered = true;
            committed.CopyFrom(snapshot);
            return Task.FromResult(true);
        }, false);
        var drained = queue.Pending;
        Assert(!first.IsCompleted && !secondStarted && !drained.IsCompleted,
            "queued work does not block or overlap its predecessor");
        queue.StopAccepting();
        Assert(!await queue.RunAsync(() => throw new InvalidOperationException("rejected work ran"), false),
            "closing rejects new mutations");
        release.SetResult(true);
        await drained;
        Assert(await first && await second && committed.GoogleDrivePromptAnswered,
            "closing drains every already accepted mutation");

        var recoveryQueue = new SettingsOperationQueue();
        var failed = recoveryQueue.RunAsync<bool>(() => throw new IOException("injected save failure"), false);
        try { await failed; throw new InvalidOperationException("injected failure was hidden"); }
        catch (IOException) { }
        Assert(await recoveryQueue.RunAsync(() => Task.FromResult(true), false),
            "failed save does not poison the next mutation");
        await recoveryQueue.Pending;
    }

    private static async Task SaveSnapshotsAndRollbackStayOffCaller()
    {
        var root = Path.Combine(Path.GetTempPath(), "settings-operations-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? pending = null;
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path,
                "{\"schemaVersion\":99,\"language\":\"en\",\"futureOption\":{\"enabled\":true}}");
            var repository = new ApplicationSettingsRepository(path);
            var original = repository.Load();
            var snapshot = original.Clone();
            snapshot.Language = "ru";
            var callerThread = Thread.CurrentThread.ManagedThreadId;
            var workerThread = callerThread;
            pending = SettingsPersistence.SaveAsync(repository, snapshot, () =>
            {
                workerThread = Thread.CurrentThread.ManagedThreadId;
                entered.Set();
                release.Wait();
            });
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "worker enters the delayed mutation");
            Assert(!pending.IsCompleted && workerThread != callerThread,
                "filesystem/mutex work yields without blocking the caller");
            original.GoogleDrivePromptAnswered = true;
            Assert(original.Language == "en", "uncommitted preference is not published");
            release.Set();
            await pending;
            var saved = repository.Load();
            Assert(saved.Language == "ru" && !saved.GoogleDrivePromptAnswered,
                "save uses a detached snapshot");
            Assert(saved.SchemaVersion == 99 && File.ReadAllText(path).Contains("futureOption"),
                "detached snapshot preserves unknown future settings");

            var invalidPath = Path.Combine(root, "settings-is-a-directory");
            Directory.CreateDirectory(invalidPath);
            var applied = false;
            var rolledBack = false;
            try
            {
                await SettingsPersistence.SaveAsync(new ApplicationSettingsRepository(invalidPath), snapshot,
                    () => applied = true, () => rolledBack = true);
                throw new InvalidOperationException("invalid settings target unexpectedly saved");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Assert(applied && rolledBack, "failed settings save rolls back the applied simulator preference");
            Assert(original.Language == "en", "failed persistence leaves published settings unchanged");

            try
            {
                await SettingsPersistence.SaveAsync(new ApplicationSettingsRepository(invalidPath), snapshot,
                    () => { }, () => throw new InvalidOperationException("injected rollback failure"));
                throw new InvalidOperationException("rollback failure was hidden");
            }
            catch (AggregateException exception)
            {
                Assert(exception.InnerExceptions.Count == 2, "save and rollback failures are both retained");
            }
        }
        finally
        {
            release.Set();
            if (pending != null) await pending;
            Directory.Delete(root, true);
        }
    }

    private static void CachedTokenStateDoesNotWaitForDiskWriter()
    {
        var root = Path.Combine(Path.GetTempPath(), "token-cache-test-" + Guid.NewGuid().ToString("N"));
        var store = new GoogleOAuthTokenStore(Path.Combine(root, "token.json"));
        Assert(!store.HasRefreshToken(), "missing isolated token starts signed out");
        var gate = typeof(GoogleOAuthTokenStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            lock (gate)
            {
                entered.Set();
                release.Wait();
            }
        });
        try
        {
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "simulated token writer holds its lock");
            var reader = Task.Run(() => store.HasRefreshToken());
            Assert(reader.Wait(TimeSpan.FromSeconds(5)) && !reader.Result,
                "cached signed-in presentation does not wait for a token writer lock");
        }
        finally
        {
            release.Set();
            writer.GetAwaiter().GetResult();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
