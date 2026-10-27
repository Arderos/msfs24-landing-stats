using System;
using System.IO;
using System.Linq;
using System.Threading;
using LandingStats.App.Telemetry;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class MsfsControlProfileMonitorRegressionTests
{
    internal static void RunAll()
    {
        OldAircraftCompletionIsDiscarded();
        ProfileReadFailureRemainsOptional();
        ResetDiscardsPendingRead();
        SimulatorVersionChangeInvalidatesCachedStore();
        NoSimulatorDoesNotStartDiscovery();
        MissingStoreCanBeDiscoveredOnNextIdentity();
        ReloadSurvivesAircraftChange();
        FailedReloadCannotRestoreCachedSelections();
    }

    private static void ReloadSurvivesAircraftChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "msfs-profile-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        MsfsControlProfileMonitor? monitor = null;
        try
        {
            var oldStore = MsfsControlProfileRegressionTests.StoreFixture(directory, "Joystick L-Axis Y");
            var changedStore = MsfsControlProfileRegressionTests.StoreFixture(directory, "Joystick R-Axis X");
            var loads = 0;
            monitor = new MsfsControlProfileMonitor(_ => oldStore, _ =>
            {
                var load = Interlocked.Increment(ref loads);
                if (load == 2) { entered.Set(); release.Wait(); }
                return load == 1 ? oldStore : changedStore;
            });
            monitor.Update(12, "aircraft-A", "AIRPLANE");
            Require(SpinWait.SpinUntil(() => monitor.TryTake(out _), TimeSpan.FromSeconds(5)), "Initial profile snapshot did not arrive.");
            Invalidate(monitor);
            monitor.TryTake(out _);
            Require(entered.Wait(TimeSpan.FromSeconds(5)), "Profile reload did not start.");
            monitor.Update(12, "aircraft-B", "AIRPLANE");
            release.Set();
            AircraftControlsMessage? observed = null;
            Require(SpinWait.SpinUntil(() => monitor.TryTake(out observed), TimeSpan.FromSeconds(5)), "Current aircraft did not receive its profile snapshot.");
            Require(observed!.AircraftPath == "aircraft-B" && observed.Bindings.Single().AxisIndex == 3 && observed.ProfileEvidence ==
                MsfsControlProfileEvidence.WithResolution(changedStore.ProfileEvidence, changedStore.Resolve("aircraft-B", "AIRPLANE")),
                "An aircraft change during profile reload restored the obsolete cached selections.");
        }
        finally
        {
            release.Set(); monitor?.Dispose();
            if (monitor != null) Require(SpinWait.SpinUntil(() => !monitor.IsRefreshing, TimeSpan.FromSeconds(5)), "Profile reload did not stop.");
            Directory.Delete(directory, true);
        }
    }

    private static void FailedReloadCannotRestoreCachedSelections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "msfs-profile-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MsfsControlProfileMonitor? monitor = null;
        try
        {
            var initial = MsfsControlProfileRegressionTests.StoreFixture(directory, "Joystick L-Axis Y");
            var changed = MsfsControlProfileRegressionTests.StoreFixture(directory, "Joystick R-Axis X");
            var discoveries = 0; var loads = 0;
            monitor = new MsfsControlProfileMonitor(_ => Interlocked.Increment(ref discoveries) == 1 ? initial : changed,
                _ => Interlocked.Increment(ref loads) == 1 ? initial : throw new IOException("Changed profile unavailable"));
            monitor.Update(12, "aircraft-A", "AIRPLANE");
            Require(SpinWait.SpinUntil(() => monitor.TryTake(out _), TimeSpan.FromSeconds(5)), "Initial profile snapshot did not arrive.");
            Invalidate(monitor); monitor.TryTake(out _);
            Require(SpinWait.SpinUntil(() => !monitor.IsRefreshing && monitor.Diagnostics.Any(value => value.Contains("Changed profile unavailable")),
                TimeSpan.FromSeconds(5)), "Profile reload error was not reported.");
            monitor.TryTake(out _);
            monitor.Update(12, "aircraft-B", "AIRPLANE");
            AircraftControlsMessage? observed = null;
            Require(SpinWait.SpinUntil(() => monitor.TryTake(out observed), TimeSpan.FromSeconds(5)), "Profile rediscovery did not finish.");
            Require(discoveries == 2 && observed!.Bindings.Single().AxisIndex == 3, "A later aircraft reused the old cached store after a failed reload.");
        }
        finally { monitor?.Dispose(); Directory.Delete(directory, true); }
    }

    // Deliver the same invalidation as FileSystemWatcher without scheduling the
    // assertion around nondeterministic OS notification latency.
    private static void Invalidate(MsfsControlProfileMonitor monitor) => typeof(MsfsControlProfileMonitor)
        .GetMethod("Invalidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(monitor, null);

    private static void NoSimulatorDoesNotStartDiscovery()
    {
        var reads = 0;
        using var monitor = new MsfsControlProfileMonitor(_ => { reads++; throw new InvalidOperationException(); }, _ => throw new InvalidOperationException());
        Require(!monitor.TryTake(out _) && reads == 0 && !monitor.IsRefreshing, "Startup without simulator identity started profile I/O.");
    }

    private static void MissingStoreCanBeDiscoveredOnNextIdentity()
    {
        var reads = 0; var owner = Thread.CurrentThread.ManagedThreadId;
        using var monitor = new MsfsControlProfileMonitor(_ =>
        {
            Require(Thread.CurrentThread.ManagedThreadId != owner, "Profile discovery ran on its caller dispatcher.");
            Interlocked.Increment(ref reads); return MsfsControlProfileStore.Load("");
        }, MsfsControlProfileStore.Load);
        for (var i = 0; i < 2; i++)
        {
            monitor.Update(12, "aircraft", "AIRPLANE");
            Require(SpinWait.SpinUntil(() => monitor.TryTake(out _), TimeSpan.FromSeconds(5)), "Unavailable discovery did not return diagnostics.");
        }
        Require(reads == 2, "A missing profile store remained cached after a new identity update.");
    }

    private static void OldAircraftCompletionIsDiscarded()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var discoveries = 0;
        var missing = Path.Combine(Path.GetTempPath(), "absent-msfs-profiles-" + Guid.NewGuid().ToString("N"));
        using var monitor = new MsfsControlProfileMonitor(_ =>
        {
            if (Interlocked.Increment(ref discoveries) == 1) { entered.Set(); release.Wait(); }
            return MsfsControlProfileStore.Load(missing);
        }, MsfsControlProfileStore.Load);
        monitor.Update(12, "aircraft-A", "AIRPLANE");
        Require(entered.Wait(TimeSpan.FromSeconds(5)), "The asynchronous profile load did not start.");
        monitor.Update(12, "aircraft-B", "AIRPLANE"); release.Set();
        AircraftControlsMessage? observed = null;
        Require(SpinWait.SpinUntil(() =>
        {
            if (!monitor.TryTake(out var value)) return false;
            Require(value!.AircraftPath != "aircraft-A", "A stale aircraft load published its mappings.");
            observed = value; return true;
        }, TimeSpan.FromSeconds(5)), "The current aircraft profile was not published.");
        Require(observed!.AircraftPath == "aircraft-B", "Only the current loaded aircraft may publish a profile.");
        monitor.Update(12, "aircraft-C", "AIRPLANE");
        Require(SpinWait.SpinUntil(() => monitor.TryTake(out observed), TimeSpan.FromSeconds(5)), "The cached profile did not resolve another aircraft.");
        Require(discoveries == 2, "Changing aircraft must reuse the immutable profile snapshot after discovery completes.");
        monitor.Update(12, "aircraft-C", "AIRPLANE");
        Require(SpinWait.SpinUntil(() => monitor.TryTake(out observed), TimeSpan.FromSeconds(5)), "A repeated identity request must republish after an input-stream gap.");
        Require(discoveries == 2 && observed!.Context.Contains("aircraft-C"), "Republishing must retain the aircraft identity and reuse the loaded snapshot.");
    }

    private static void ProfileReadFailureRemainsOptional()
    {
        using var monitor = new MsfsControlProfileMonitor(_ => throw new IOException("Profile store locked"), _ => throw new IOException());
        monitor.Update(12, "aircraft", "AIRPLANE");
        AircraftControlsMessage? observed = null;
        Require(SpinWait.SpinUntil(() => monitor.TryTake(out observed), TimeSpan.FromSeconds(5)), "A failed profile read did not publish fallback availability.");
        Require(observed!.Bindings.Count == 0 && monitor.Diagnostics.Any(d => d.Contains("Profile store locked")), "A read failure must expose an empty mapping and its reason.");
    }

    private static void SimulatorVersionChangeInvalidatesCachedStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "msfs-version-monitor-" + Guid.NewGuid().ToString("N"));
        var oldPath = Path.Combine(root, "old"); var newPath = Path.Combine(root, "new");
        Directory.CreateDirectory(oldPath); Directory.CreateDirectory(newPath);
        MsfsControlProfileMonitor? monitor = null;
        try
        {
            var oldStore = MsfsControlProfileRegressionTests.StoreFixture(oldPath, "Joystick L-Axis Y");
            var newStore = MsfsControlProfileRegressionTests.StoreFixture(newPath, "Joystick R-Axis X");
            var versions = new System.Collections.Generic.List<uint>();
            monitor = new MsfsControlProfileMonitor(version =>
            { lock (versions) versions.Add(version); return version == 11 ? oldStore : newStore; }, MsfsControlProfileStore.Load);
            AircraftControlsMessage? observed = null;
            foreach (var version in new uint[] { 11, 12, 11 })
            {
                monitor.Update(version, "same-aircraft", "AIRPLANE");
                Require(SpinWait.SpinUntil(() => monitor.TryTake(out observed), TimeSpan.FromSeconds(5)), "Simulator version change did not publish its profile.");
                Require(observed!.Bindings.Single().AxisIndex == (version == 11 ? 1 : 3), "Simulator version change reused the other installation's cached bindings.");
            }
            lock (versions) Require(versions.SequenceEqual(new uint[] { 11, 12, 11 }), "Authoritative application version did not reach discovery.");
        }
        finally { monitor?.Dispose(); Directory.Delete(root, true); }
    }

    private static void ResetDiscardsPendingRead()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var missing = Path.Combine(Path.GetTempPath(), "absent-msfs-profiles-" + Guid.NewGuid().ToString("N"));
        using var monitor = new MsfsControlProfileMonitor(_ =>
        { entered.Set(); release.Wait(); return MsfsControlProfileStore.Load(missing); }, MsfsControlProfileStore.Load);
        monitor.Update(12, "previous-flight", "AIRPLANE");
        Require(entered.Wait(TimeSpan.FromSeconds(5)), "The asynchronous profile load did not start.");
        monitor.Reset(); release.Set();
        Require(SpinWait.SpinUntil(() => !monitor.IsRefreshing, TimeSpan.FromSeconds(5)), "The discarded profile load did not finish.");
        Require(!monitor.TryTake(out _), "A reset source must not publish mappings from its previous flight.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
