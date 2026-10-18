using System;
using System.IO;
using System.Text;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Settings;

namespace LandingStats.App.RegressionTests;

internal static class IntegrationApiSettingsRegressionTests
{
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "integration-api-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            var repository = new ApplicationSettingsRepository(path);
            Require(!repository.Load().IntegrationApiEnabled, "fresh installs must not expose local data");
            File.WriteAllText(path, "{\"schemaVersion\":4,\"language\":\"en\"}");
            Require(!repository.Load().IntegrationApiEnabled, "upgrades must not implicitly enable the API");

            var owner = new ApplicationSettingsOwner(repository, new SimulatorAutoStartManager());
            var baseline = owner.Read().Settings;
            var cloudBefore = owner.ReadCloudAsync().GetAwaiter().GetResult();
            var cloudBytes = GoogleDriveCloudSettings.Serialize(cloudBefore.Settings);
            var enabled = baseline.Clone(); enabled.IntegrationApiEnabled = true;
            var notifications = 0;
            owner.Changed += () => notifications++;
            owner.SaveAsync(baseline, enabled).GetAwaiter().GetResult();
            Require(repository.Load().IntegrationApiEnabled && owner.Snapshot.Settings.IntegrationApiEnabled && enabled.IntegrationApiEnabled,
                "explicit opt-in must survive persistence and committed snapshots");
            var copy = new ApplicationSettings(); copy.CopyFrom(enabled.Clone());
            Require(copy.IntegrationApiEnabled && notifications == 1, "cloning, copy and notification preserve the setting");
            Require(File.ReadAllText(path).Contains("\"integrationApiEnabled\":true"), "opt-in must be serialized explicitly");

            var cloudAfter = owner.ReadCloudAsync().GetAwaiter().GetResult();
            Require(Encoding.UTF8.GetString(cloudBytes) == Encoding.UTF8.GetString(GoogleDriveCloudSettings.Serialize(cloudAfter.Settings)),
                "local API preference must never alter the cloud settings payload or hash");
            var expectedHash = GoogleDriveBackupService.Hash(cloudBytes);
            Require(owner.ApplyCloudAsync(new GoogleDriveCloudSettings { Language = "ru" }, expectedHash).GetAwaiter().GetResult(),
                "local API preference must not invalidate a concurrent cloud language update");
            Require(repository.Load().IntegrationApiEnabled, "cloud settings must preserve the local API preference");

            // A local edit queued from a stale view changes only its own field.
            var stale = baseline.Clone(); stale.AutomaticTelemetryUpload = false;
            owner.SaveAsync(baseline, stale).GetAwaiter().GetResult();
            Require(stale.IntegrationApiEnabled && stale.Language == "ru", "unrelated stale saves must not disable the API or revert cloud language");

            var previous = owner.Snapshot.Settings; var disabled = previous.Clone(); disabled.IntegrationApiEnabled = false;
            var applied = false; var rolledBack = false;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    owner.SaveAsync(previous, disabled, () => applied = true, () => rolledBack = true).GetAwaiter().GetResult();
                    throw new InvalidOperationException("locked settings file unexpectedly replaced");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Require(applied && rolledBack && owner.Snapshot.Settings.IntegrationApiEnabled,
                    "failed durable save must roll back the live change and leave the committed preference intact");
                Require(!repository.Load().IntegrationApiEnabled, "unreadable settings must fail closed");
            }
            owner.SaveAsync(previous, disabled).GetAwaiter().GetResult();
            Require(!repository.Load().IntegrationApiEnabled && File.ReadAllText(path).Contains("\"integrationApiEnabled\":false"),
                "disabling must persist across a restart");
            var currentCloud = owner.ReadCloudAsync().GetAwaiter().GetResult();
            var unknownRemoteSetting = GoogleDriveCloudSettings.Deserialize(Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"language\":\"en\",\"integrationApiEnabled\":true}"));
            Require(owner.ApplyCloudAsync(unknownRemoteSetting,
                GoogleDriveBackupService.Hash(GoogleDriveCloudSettings.Serialize(currentCloud.Settings))).GetAwaiter().GetResult(),
                "cloud fixture must apply its supported language setting");
            Require(!repository.Load().IntegrationApiEnabled, "another computer cannot opt this computer into the API");
            foreach (var invalid in new[] { "{invalid", "null" })
            {
                File.WriteAllText(path, invalid);
                Require(!repository.TryLoad(out var unreadable) && !unreadable.IntegrationApiEnabled,
                    "invalid settings must fail closed");
            }
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
