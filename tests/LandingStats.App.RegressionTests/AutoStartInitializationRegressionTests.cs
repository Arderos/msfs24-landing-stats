using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Settings;

namespace LandingStats.App.RegressionTests;

internal static class AutoStartInitializationRegressionTests
{
    internal static void RunAll()
    {
        foreach (var enabled in new[] { false, true })
        foreach (var cloud in new[] { false, true })
        foreach (var initializeFirst in new[] { false, true })
            PreferenceChangeAndInitializationAgree(enabled, cloud, initializeFirst);
        CloudPreferenceWithoutLocalSimulator();
        UndecidedPreferenceRemainsUndecided();
        InitializationFailureDoesNotBlockLaterChanges();
    }

    private static void PreferenceChangeAndInitializationAgree(bool enabled, bool cloud, bool initializeFirst)
    {
        WithFixture(enabled, (repository, manager, owner, root) =>
        {
            var baseline = owner.Read().Settings;
            var expectedHash = GoogleDriveBackupService.Hash(GoogleDriveCloudSettings.Serialize(owner.ReadCloudAsync().GetAwaiter().GetResult().Settings));
            Task Change()
            {
                if (cloud) return ApplyCloud();
                var desired = baseline.Clone(); desired.StartWithSimulator = !enabled;
                return owner.SaveAsync(baseline, desired,
                    () => manager.SetEnabled(!enabled), () => manager.SetEnabled(enabled));
            }
            async Task ApplyCloud()
            {
                Check(await owner.ApplyCloudAsync(new GoogleDriveCloudSettings { StartWithSimulator = !enabled }, expectedHash).ConfigureAwait(false),
                    "The unchanged expected settings hash rejected the cloud preference.");
            }

            if (initializeFirst)
            {
                // Hold the real configuration writer boundary so initialization
                // cannot finish before the competing preference is submitted.
                // Initialization must reserve the settings owner before doing I/O.
                using var configurationMutex = new Mutex(false, "Local\\MSFSLandingStatsExeXml");
                var ownsMutex = false;
                Task? initialization = null; Task? change = null;
                try
                {
                    try { ownsMutex = configurationMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    Check(ownsMutex, "Could not hold the isolated configuration write boundary.");
                    initialization = owner.InitializeAutoStartAsync();
                    change = Change();
                    Check(!initialization.IsCompleted && !change.IsCompleted,
                        "A preference committed while its initial simulator configuration was still in progress.");
                }
                finally { if (ownsMutex) configurationMutex.ReleaseMutex(); }
                Task.WhenAll(initialization!, change!).GetAwaiter().GetResult();
            }
            else
            {
                // The caller still holds the old snapshot. Initialization must
                // read the owner's committed value after either kind of change.
                Change().GetAwaiter().GetResult();
                owner.InitializeAutoStartAsync().GetAwaiter().GetResult();
            }

            Check(owner.AutoStartInitialized, "Initialization did not publish completion.");
            Check(repository.Load().StartWithSimulator == !enabled && owner.Snapshot.Settings.StartWithSimulator == !enabled,
                "Initialization overwrote a committed preference.");
            Check(XmlEnabled(root) == !enabled, "Simulator auto-start disagrees with the final local/cloud preference.");
        });
    }

    private static void UndecidedPreferenceRemainsUndecided()
    {
        WithFixture(null, (repository, manager, owner, root) =>
        {
            Check(owner.InitializeAutoStartAsync().GetAwaiter().GetResult() == null && owner.AutoStartInitialized,
                "An undecided preference must complete initialization without configuring auto-start.");
            Check(repository.Load().StartWithSimulator == null && !File.Exists(Path.Combine(root, "exe.xml")),
                "Initialization decided or persisted an unanswered preference.");
            var cloud = owner.ReadCloudAsync().GetAwaiter().GetResult();
            Check(owner.ApplyCloudAsync(new GoogleDriveCloudSettings { StartWithSimulator = true },
                GoogleDriveBackupService.Hash(GoogleDriveCloudSettings.Serialize(cloud.Settings))).GetAwaiter().GetResult(),
                "A cloud preference could not be applied after undecided initialization.");
            Check(XmlEnabled(root), "The first preference after initialization did not configure auto-start.");
        });
    }

    private static void InitializationFailureDoesNotBlockLaterChanges()
    {
        WithFixture(true, (repository, manager, owner, root) =>
        {
            var portable = Path.Combine(root, "portable.exe");
            File.Delete(portable);
            try
            {
                owner.InitializeAutoStartAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException("An invalid auto-start target was accepted.");
            }
            catch (FileNotFoundException) { }
            Check(owner.AutoStartInitialized, "Initial configuration failure suppressed future preference updates.");
            var cloud = owner.ReadCloudAsync().GetAwaiter().GetResult();
            Check(owner.ApplyCloudAsync(new GoogleDriveCloudSettings { StartWithSimulator = false },
                GoogleDriveBackupService.Hash(GoogleDriveCloudSettings.Serialize(cloud.Settings))).GetAwaiter().GetResult(),
                "A later preference could not recover from initial configuration failure.");
            Check(repository.Load().StartWithSimulator == false && !XmlEnabled(root),
                "A later disable did not remove the previously configured entry.");
        });
    }

    private static void CloudPreferenceWithoutLocalSimulator()
    {
        WithFixture(false, (repository, manager, owner, root) =>
        {
            owner.InitializeAutoStartAsync().GetAwaiter().GetResult();
            File.Delete(Path.Combine(root, "UserCfg.opt"));
            void Apply(bool enabled)
            {
                var cloud = owner.ReadCloudAsync().GetAwaiter().GetResult();
                Check(owner.ApplyCloudAsync(new GoogleDriveCloudSettings { StartWithSimulator = enabled, Language = "ru" },
                    GoogleDriveBackupService.Hash(GoogleDriveCloudSettings.Serialize(cloud.Settings))).GetAwaiter().GetResult(),
                    "Cloud preferences were rejected on a PC without the simulator.");
            }
            Apply(true);
            Check(repository.Load().Language == "ru" && repository.Load().StartWithSimulator == true &&
                !string.IsNullOrEmpty(owner.LastAutoStartError), "An unavailable local auto-start blocked portable preferences or hid its error.");
            Apply(false);
            Check(owner.LastAutoStartError == null && repository.Load().StartWithSimulator == false,
                "Successful local disable did not clear the previous configuration error.");
        });
    }

    private static bool XmlEnabled(string root)
    {
        var path = Path.Combine(root, "exe.xml");
        return File.Exists(path) && File.ReadAllText(path).Contains("<Name>" + SimulatorAutoStartManager.EntryName + "</Name>");
    }

    private static void WithFixture(bool? enabled, Action<ApplicationSettingsRepository, SimulatorAutoStartManager, ApplicationSettingsOwner, string> test)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "autostart-initialization-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "UserCfg.opt"), "isolated test profile");
            var portable = Path.Combine(root, "portable.exe");
            File.Copy(typeof(ApplicationSettingsOwner).Assembly.Location, portable);
            var repository = new ApplicationSettingsRepository(Path.Combine(root, "settings.json"));
            repository.Save(new ApplicationSettings { StartWithSimulator = enabled });
            var manager = new SimulatorAutoStartManager(new[] { new SimulatorAutoStartManager.SimulatorProfile("test", root) }, () => portable);
            if (enabled.HasValue) manager.SetEnabled(enabled.Value);
            test(repository, manager, new ApplicationSettingsOwner(repository, manager), root);
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
