using System;
using System.IO;
using System.Threading;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Settings;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static partial class GoogleDriveBackupRegressionTests
{
    public static void CorruptAutoStartDoesNotBlockCloudSync()
    {
        foreach (var xml in new[] { "<broken", "<unsupported/>" })
        {
            var root = TemporaryDirectory("followup-autostart");
            try
            {
                var drive = new FakeGoogleDriveApi();
                var remote = new LandingRepository(Path.Combine(root, "remote"));
                remote.Save(Record("remote", 220));
                var remoteSettings = new GoogleDriveCloudSettings { Language = "ru", StartWithSimulator = true };
                using var seed = Service(drive, remote, Path.Combine(root, "seed.json"), () => remoteSettings, value => remoteSettings = value);
                seed.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                var profile = Path.Combine(root, "profile"); Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile, "UserCfg.opt"), "test profile");
                var portable = Path.Combine(root, "portable.exe");
                File.Copy(typeof(LandingStats.App.App).Assembly.Location, portable);
                var settings = new ApplicationSettingsRepository(Path.Combine(root, "settings.json"));
                settings.Save(new ApplicationSettings { StartWithSimulator = false });
                var autoStart = new SimulatorAutoStartManager(new[] { new SimulatorAutoStartManager.SimulatorProfile("test", profile) }, () => portable);
                var owner = new ApplicationSettingsOwner(settings, autoStart);
                owner.InitializeAutoStartAsync().GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(profile, "exe.xml"), xml);
                var local = new LandingRepository(Path.Combine(root, "local"));
                using var client = new GoogleDriveBackupService(drive, local, new object(),
                    new GoogleDriveSyncStateRepository(Path.Combine(root, "state.json")), owner.ReadCloudAsync, owner.ApplyCloudAsync);
                var first = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(first.DownloadedSettings && local.Contains("remote") && settings.Load().Language == "ru" &&
                    settings.Load().StartWithSimulator == true && !string.IsNullOrEmpty(owner.LastAutoStartError),
                    "Invalid exe.xml blocked cloud settings/history or hid the local error.");
                var second = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(!second.DownloadedSettings && second.FailedLandings.Count == 0,
                    "Unchanged cloud data was retried after a completed sync.");
                Equal(xml, File.ReadAllText(Path.Combine(profile, "exe.xml")), "Unsupported local XML was overwritten.");
            }
            finally { DeleteDirectory(root); }
        }
    }
}
