using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Threading;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static partial class GoogleDriveBackupRegressionTests
{
    public static void RepeatedSettingsRecoverFromCleanupFailure() => RepeatedSettingsRecover(false);
    public static void LegacySettingsCyclesRecoverAcrossDevices() => RepeatedSettingsRecover(true);

    private static void RepeatedSettingsRecover(bool legacy)
    {
        var root = TemporaryDirectory("google-repeated-settings");
        try
        {
            var drive = new FakeGoogleDriveApi { LegacyRevisionMetadata = legacy };
            var settings = new GoogleDriveCloudSettings { Language = "en", StartWithSimulator = true };
            var statePath = Path.Combine(root, "state.json");
            var repository = new LandingRepository(Path.Combine(root, "landings"));
            using (var client = Service(drive, repository, statePath, () => settings, value => settings = value))
            {
                client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                settings.Language = "ru";
                client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                settings.Language = "en";
                drive.RejectTrash = true;
                AssertThrows<IOException>(() => client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult(),
                    "cleanup failure was not reported");
            }
            var active = drive.ListApplicationFilesAsync(CancellationToken.None).Result
                .Where(file => !file.Trashed && file.Property("kind") == "settings").ToArray();
            Equal(2, active.Length, "interrupted update must retain both revisions");
            var newest = active.OrderByDescending(file => file.CreatedUtc).First();
            var oldest = active.Single(file => file.Id != newest.Id);
            Equal(oldest.Property("sha256"), newest.Property("baseSha256"), "compatible predecessor checksum");
            if (!legacy) Equal(oldest.Id, newest.Property("baseFileId"), "predecessor file identity");
            // Legacy data has the exact A <-> B hash cycle observed in production.
            Equal(newest.Property("sha256"), oldest.Property("baseSha256"), "repeated content hash");
            drive.RejectTrash = false;

            using var restarted = Service(drive, repository, statePath, () => settings, value => settings = value);
            var recovered = restarted.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(!recovered.SettingsConflict, "repeated preferences left synchronization blocked");
            Equal("en", settings.Language, "recovery reverted the latest local preferences");
            Equal("en", GoogleDriveCloudSettings.Deserialize(drive.ActiveSettingsBytes()).Language, "recovered cloud language");
            Equal(true, GoogleDriveCloudSettings.Deserialize(drive.ActiveSettingsBytes()).StartWithSimulator, "recovered cloud auto-start");

            var otherSettings = new GoogleDriveCloudSettings { Language = "ru", StartWithSimulator = false };
            using var other = Service(drive, new LandingRepository(Path.Combine(root, "other")),
                Path.Combine(root, "other-state.json"), () => otherSettings, value => otherSettings = value);
            var joined = other.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(!joined.SettingsConflict, "another device could not join the recovered history");
            Equal("en", otherSettings.Language, "new device restored an obsolete language");
            Equal(true, otherSettings.StartWithSimulator, "new device restored obsolete auto-start");
            var repeated = restarted.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(!repeated.SettingsConflict && !repeated.UploadedSettings && !repeated.DownloadedSettings,
                "recovered settings did not converge on repeat sync");
            Assert(drive.ListApplicationFilesAsync(CancellationToken.None).Result.Any(file => file.Id == oldest.Id && file.Trashed),
                "superseded settings must remain recoverable in trash");
        }
        finally { DeleteDirectory(root); }
    }

    public static void RepeatedLandingRevisionsRecoverFromCleanupFailure()
    {
        foreach (var legacy in new[] { false, true })
        {
            var root = TemporaryDirectory("google-repeated-landing");
            try
            {
                var drive = new FakeGoogleDriveApi { LegacyRevisionMetadata = legacy };
                var repository = new LandingRepository(Path.Combine(root, "landings"));
                var original = Record("repeated", 125);
                repository.Save(original);
                var settings = new GoogleDriveCloudSettings();
                var statePath = Path.Combine(root, "state.json");
                using (var client = Service(drive, repository, statePath, () => settings, value => settings = value))
                {
                    client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var changed = repository.DeserializeBackup(repository.ExportForBackup(original.Id));
                    changed.Airport = "TEST";
                    repository.Save(changed);
                    client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                    repository.Save(original);
                    drive.RejectTrash = true;
                    var interrupted = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Assert(interrupted.FailedLandings.ContainsKey(original.Id), "landing cleanup failure was not reported");
                }
                Equal(2, drive.ActiveLandingCount(original.Id), "landing cleanup failure did not preserve both revisions");
                drive.RejectTrash = false;
                repository.Save(Record("unrelated", 210));
                using var restarted = Service(drive, repository, statePath, () => settings, value => settings = value);
                var recovered = restarted.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Equal(0, recovered.ConflictedLandingIds.Count, "landing revision cycle did not recover");
                Equal(0, recovered.FailedLandings.Count, "landing recovery failed");
                Equal(1, drive.ActiveLandingCount(original.Id), "recovered landing duplicated");
                Equal(1, drive.ActiveLandingCount("unrelated"), "unrelated landing backup blocked");
                Equal(original.Airport, repository.LoadAll().Single(record => record.Id == original.Id).Airport, "recovery reverted landing metadata");
                var repeat = restarted.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Equal(0, repeat.UploadedLandings, "recovered landing did not converge");
            }
            finally { DeleteDirectory(root); }
        }
    }

    public static void RevisionIdentitySurvivesRepeatedContents()
    {
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = Revision("z-first", "A", null, null, time);
        var second = Revision("a-second", "B", "A", first.Id, time);
        var third = Revision("m-third", "A", "B", second.Id, time);
        foreach (var order in new[] { new[] { first, second, third }, new[] { third, second, first } })
            Equal(third.Id, ResolveRevisions(order).Id, "same-time repeated content lost its actual descendant");

        // The deleted B predecessor must not be replaced by a later B occurrence.
        var previous = Revision("old", "A", "B", "deleted-B", time);
        var latest = Revision("new", "B", "A", previous.Id, time);
        Equal(latest.Id, ResolveRevisions(new[] { previous, latest }).Id, "missing predecessor rebound by content hash");

        // An old client can upload a hash-only child of a new client's revision.
        var oldClientChild = Revision("legacy-child", "B", "A", null, time.AddSeconds(1));
        Equal(oldClientChild.Id, ResolveRevisions(new[] { third, oldClientChild }).Id, "mixed-client ancestry failed");
        var sibling = Revision("sibling", "C", "A", first.Id, time.AddSeconds(2));
        Equal(sibling.Id, ResolveRevisions(new[] { first, second, sibling }).Id, "concurrent leaves did not use server ordering");
        Equal(sibling.Id, ResolveRevisions(new[] { sibling, second, first }).Id, "listing order changed concurrent resolution");
    }

    public static void InvalidRevisionAncestryIsRejected()
    {
        var first = Revision("first", "A", "B", "second", null);
        var second = Revision("second", "B", "A", "first", null);
        AssertThrows<InvalidDataException>(() => ResolveRevisions(new[] { first, second }), "actual file-ID cycle was accepted");
        var invalidHash = Revision("invalid", "C", "wrong", first.Id, null);
        AssertThrows<InvalidDataException>(() => ResolveRevisions(new[] { first, invalidHash }), "mismatched predecessor checksum was accepted");
    }

    public static void CreationTimeSurvivesWorkerBoundary()
    {
        var time = new DateTime(2026, 11, 7, 21, 31, 21, DateTimeKind.Utc).AddTicks(9120000);
        var file = Revision("file", "hash", "base", "parent", time);
        using var stream = new MemoryStream();
        var serializer = new DataContractJsonSerializer(typeof(CloudFileData));
        serializer.WriteObject(stream, CloudFileData.From(file));
        stream.Position = 0;
        var restored = ((CloudFileData)serializer.ReadObject(stream)!).Restore();
        Equal(file.CreatedUtc, restored.CreatedUtc, "worker creation timestamp");
        Equal("parent", restored.Property("baseFileId"), "worker predecessor identity");
        var provider = new Google.Apis.Drive.v3.Data.File { Id = "provider", CreatedTimeDateTimeOffset = new DateTimeOffset(time) };
        var convert = typeof(GoogleDriveApiClient).GetMethod("ToFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        var converted = (GoogleDriveFile)convert.Invoke(null, new object[] { provider })!;
        Equal((DateTime?)time, converted.CreatedUtc, "Drive creation timestamp was discarded");
        var legacy = CloudFileData.From(Revision("old", "hash", null, null, null));
        Equal((DateTime?)null, legacy.Restore().CreatedUtc, "absent legacy timestamp");
        legacy.CreatedUtcTicks = long.MaxValue;
        AssertThrows<InvalidDataException>(() => legacy.Restore(), "invalid timestamp accepted by worker protocol");
    }

    public static void RevisionRecoveryVerifiesRetainedContents()
    {
        var root = TemporaryDirectory("google-revision-integrity");
        try
        {
            var drive = new FakeGoogleDriveApi { LegacyRevisionMetadata = true };
            var settings = new GoogleDriveCloudSettings { Language = "en" };
            using var client = Service(drive, new LandingRepository(Path.Combine(root, "landings")),
                Path.Combine(root, "state.json"), () => settings, value => settings = value);
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            settings.Language = "ru";
            drive.RejectTrash = true;
            AssertThrows<IOException>(() => client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult(), "missing cleanup failure");
            drive.RejectTrash = false;
            drive.CorruptNewestSettings();
            AssertThrows<InvalidDataException>(() => client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult(), "corrupt selected copy passed integrity check");
            Equal(2, drive.ListApplicationFilesAsync(CancellationToken.None).Result.Count(file => !file.Trashed && file.Property("kind") == "settings"),
                "a valid alternative was removed before verifying the retained copy");
        }
        finally { DeleteDirectory(root); }
    }

    private static GoogleDriveFile Revision(string id, string hash, string? baseHash, string? parentId, DateTime? time)
    {
        var properties = new Dictionary<string, string> { ["sha256"] = hash, ["kind"] = "settings" };
        if (baseHash != null) properties["baseSha256"] = baseHash;
        if (parentId != null) properties["baseFileId"] = parentId;
        return new GoogleDriveFile(id, "settings.json", "application/json", false, properties, new[] { "root" }, time);
    }

    private static GoogleDriveFile ResolveRevisions(GoogleDriveFile[] files)
    {
        var method = typeof(GoogleDriveBackupService).GetMethod("ResolveRevisionGroup", BindingFlags.NonPublic | BindingFlags.Static)!;
        try { return (GoogleDriveFile)method.Invoke(null, new object[] { files, "settings" })!; }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException invalid) { throw invalid; }
    }
}
