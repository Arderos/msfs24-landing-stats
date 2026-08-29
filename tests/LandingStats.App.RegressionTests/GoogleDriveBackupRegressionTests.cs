using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class GoogleDriveBackupRegressionTests
{
    public static void OAuthUsesPkceAndDriveFileScope()
    {
        var uri = GoogleOAuthClient.BuildAuthorizationUri(
            "desktop-client.apps.googleusercontent.com",
            "desktop-client-secret",
            "http://127.0.0.1:49152/",
            out var codeVerifier);
        var query = ParseQuery(uri.Query);

        Equal("code", query["response_type"], "authorization response type");
        Equal("S256", query["code_challenge_method"], "PKCE method");
        Assert(query["code_challenge"].Length >= 43, "PKCE challenge is missing");
        Assert(codeVerifier.Length >= 43, "PKCE verifier is missing");
        Assert(!query.ContainsKey("client_secret"), "desktop client value leaked into the browser request");
        Equal("offline", query["access_type"], "offline access");
        Equal("consent", query["prompt"], "refresh-token consent prompt");
        Equal("https://www.googleapis.com/auth/drive.file", query["scope"], "Drive scope");
    }

    public static void TokenStoreProtectsRefreshToken()
    {
        var root = TemporaryDirectory("google-token");
        var path = Path.Combine(root, "token.json");
        try
        {
            var store = new GoogleOAuthTokenStore(path);
            store.Save("refresh-token-value", "pilot@example.invalid");

            Assert(File.Exists(path), "the token file was not written");
            var disk = File.ReadAllText(path);
            Assert(!disk.Contains("refresh-token-value"), "the refresh token was stored as plaintext");
            var loaded = store.Load();
            Assert(loaded != null, "the protected token did not round-trip");
            Equal("refresh-token-value", loaded!.RefreshToken, "refresh token round-trip");
            Equal("pilot@example.invalid", loaded.AccountEmail, "account label round-trip");

            Assert(store.HasRefreshToken(), "cached signed-in state was not initialized");
            File.Delete(path);
            Assert(store.HasRefreshToken(), "signed-in state unexpectedly repeated disk and DPAPI work");

            store.Clear();
            Assert(!File.Exists(path), "sign-out did not remove the local token file");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void BackupSynchronizesAndPropagatesDeletes()
    {
        var root = TemporaryDirectory("google-backup");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var remoteRepository = new LandingRepository(Path.Combine(root, "remote-seed"));
            remoteRepository.Save(Record("remote-landing", 220));
            var remoteSettings = new GoogleDriveCloudSettings
            {
                Language = "ru",
                StartWithSimulator = true,
            };

            using (var seeder = Service(
                       drive,
                       remoteRepository,
                       Path.Combine(root, "seed-state.json"),
                       () => remoteSettings,
                       value => remoteSettings = value))
            {
                seeder.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            var localRepository = new LandingRepository(Path.Combine(root, "local"));
            var localLanding = Record("local-landing", 310);
            localRepository.Save(localLanding);
            localLanding.Airport = "LBSF";
            localLanding.Runway = "09";
            localRepository.UpdateSummary(localLanding);
            var localSettings = new GoogleDriveCloudSettings
            {
                Language = "en",
                StartWithSimulator = false,
            };
            var clientState = Path.Combine(root, "client-state.json");
            using var client = Service(
                drive,
                localRepository,
                clientState,
                () => localSettings,
                value => localSettings = value);

            var first = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(1, first.UploadedLandings, "first sync upload count");
            Equal(1, first.DownloadedLandings, "first sync download count");
            Assert(localRepository.Contains("local-landing"), "local landing disappeared during union sync");
            Assert(localRepository.Contains("remote-landing"), "cloud landing was not restored locally");
            Equal("ru", localSettings.Language, "cloud language did not win first sync");
            Equal(true, localSettings.StartWithSimulator, "cloud auto-start did not win first sync");
            Assert(
                localRepository.ExportForBackup("local-landing")
                    .SequenceEqual(localRepository.ExportForBackup("local-landing")),
                "backup export is not deterministic");

            var updatedLocal = localRepository.LoadAll().Single(record => record.Id == "local-landing");
            updatedLocal.Runway = "09L";
            localRepository.UpdateSummary(updatedLocal);
            var landingMetadataUpload = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(1, landingMetadataUpload.UploadedLandings, "updated landing metadata upload count");

            var metadataRepository = new LandingRepository(Path.Combine(root, "metadata-restore"));
            var metadataSettings = new GoogleDriveCloudSettings();
            using (var metadataClient = Service(
                       drive,
                       metadataRepository,
                       Path.Combine(root, "metadata-state.json"),
                       () => metadataSettings,
                       value => metadataSettings = value))
            {
                metadataClient.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                var restoredLocal = metadataRepository.LoadAll().Single(record => record.Id == "local-landing");
                Equal("LBSF", restoredLocal.Airport, "resolved airport did not survive cloud restore");
                Equal("09L", restoredLocal.Runway, "resolved runway did not survive cloud restore");

                restoredLocal.Runway = "09R";
                metadataRepository.UpdateSummary(restoredLocal);
                var otherDeviceUpdate = metadataClient.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Equal(1, otherDeviceUpdate.UploadedLandings, "other-device metadata upload count");
            }

            var landingMetadataDownload = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(1, landingMetadataDownload.DownloadedLandings, "updated landing metadata download count");
            Equal(
                "09R",
                localRepository.LoadAll().Single(record => record.Id == "local-landing").Runway,
                "other-device landing metadata did not update locally");

            localSettings = new GoogleDriveCloudSettings
            {
                Language = "ru",
                StartWithSimulator = false,
            };
            var settingsUpdate = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(settingsUpdate.UploadedSettings, "a local settings change was not uploaded");
            var cloudSettings = GoogleDriveCloudSettings.Deserialize(drive.ActiveSettingsBytes());
            Equal(false, cloudSettings.StartWithSimulator, "the cloud settings update was incorrect");

            client.QueueLandingDeletionAsync("remote-landing", CancellationToken.None)
                .GetAwaiter().GetResult();
            client.CancelLandingDeletionAsync("remote-landing", CancellationToken.None)
                .GetAwaiter().GetResult();
            localRepository.Delete("remote-landing");
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(localRepository.Contains("remote-landing"), "a cancelled deletion should restore from active cloud data");
            Assert(!drive.IsLandingTrashed("remote-landing"), "a cancelled deletion reached Drive trash");

            client.DeleteLandingAsync("local-landing", CancellationToken.None)
                .GetAwaiter().GetResult();
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(!localRepository.Contains("local-landing"), "an application deletion was restored locally");
            Assert(drive.IsLandingTrashed("local-landing"), "an application deletion was not moved to Drive trash");

            var freshRepository = new LandingRepository(Path.Combine(root, "fresh-device"));
            var freshSettings = new GoogleDriveCloudSettings();
            using (var freshClient = Service(
                       drive,
                       freshRepository,
                       Path.Combine(root, "fresh-state.json"),
                       () => freshSettings,
                       value => freshSettings = value))
            {
                freshClient.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            Assert(freshRepository.Contains("remote-landing"), "active cloud landing did not restore to a fresh device");
            Assert(!freshRepository.Contains("local-landing"), "trashed cloud landing restored to a fresh device");
            Equal(false, freshSettings.StartWithSimulator, "fresh device did not download cloud settings");

            drive.TrashLanding("remote-landing");
            var cloudDelete = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(1, cloudDelete.UploadedLandings, "manually removed cloud backup recreation count");
            Assert(localRepository.Contains("remote-landing"), "manual Drive deletion removed local history");
            Equal(1, drive.ActiveLandingCount("remote-landing"), "manual Drive deletion was not repaired from local history");

            drive.TrashLandingsFolderOnly();
            var folderRepair = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(1, folderRepair.UploadedLandings, "removed Landings folder backup recreation count");
            Assert(localRepository.Contains("remote-landing"), "removed Drive folder deleted local history");
            Equal(1, drive.ActiveLandingCountInActiveFolder("remote-landing"), "landing was not copied into the replacement folder");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void AccountSwitchStartsWithSafeUnion()
    {
        var root = TemporaryDirectory("google-account-switch");
        try
        {
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("kept-landing", 240));
            var settings = new GoogleDriveCloudSettings();
            var statePath = Path.Combine(root, "state.json");

            var firstDrive = new FakeGoogleDriveApi("account-a-");
            using (var first = Service(
                       firstDrive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                first.QueueLandingDeletionAsync("kept-landing", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            var secondDrive = new FakeGoogleDriveApi("account-b-");
            using (var second = Service(
                       secondDrive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                var result = second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Equal(1, result.UploadedLandings, "account-switch union upload count");
            }

            Assert(repository.Contains("kept-landing"), "switching to an empty Drive deleted local history");
            Equal(1, secondDrive.ActiveLandingCount("kept-landing"), "new account did not receive local history");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void DeleteDuringAccountSwitchReturnsToOriginalAccount()
    {
        var root = TemporaryDirectory("google-account-switch-delete-race");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("switch-delete", 278));
            var settings = new GoogleDriveCloudSettings();
            using var client = Service(
                drive,
                repository,
                Path.Combine(root, "state.json"),
                () => settings,
                value => settings = value);
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            drive.SetAccountPermissionId("fake-account-b");
            drive.BlockNextList();
            var accountSwitch = client.SyncAsync(CancellationToken.None);
            drive.WaitUntilListIsBlocked().GetAwaiter().GetResult();
            Assert(client.DeleteLandingLocally("switch-delete"), "switch landing was not deleted locally");
            drive.ReleaseBlockedList();
            accountSwitch.GetAwaiter().GetResult();
            client.QueueLandingDeletionAsync("switch-delete", CancellationToken.None).GetAwaiter().GetResult();

            var switchedState = new GoogleDriveSyncStateRepository(Path.Combine(root, "state.json")).Load();
            Equal("fake-account-b", switchedState.AccountPermissionId, "current account after switch");
            Assert(!switchedState.PendingDeletes.Contains("switch-delete"), "original delete leaked into account B");
            Assert(
                !switchedState.PendingDeletesByAccount.TryGetValue("fake-account-b", out var accountBDeletes) ||
                !accountBDeletes.Contains("switch-delete", StringComparer.Ordinal),
                "delayed queue attributed account A delete to account B");

            drive.SetAccountPermissionId("fake-account");
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert(!repository.Contains("switch-delete"), "account-switch race resurrected the deleted landing");
            Assert(drive.IsApplicationDeletedLanding("switch-delete"), "account-switch race lost the original account delete");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void DeleteWaitsForActiveSyncAndIsNotLost()
    {
        var root = TemporaryDirectory("google-delete-race");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("delete-me", 280));
            var settings = new GoogleDriveCloudSettings();
            var statePath = Path.Combine(root, "state.json");
            using var client = Service(
                drive,
                repository,
                statePath,
                () => settings,
                value => settings = value);
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            drive.BlockNextList();
            var sync = client.SyncAsync(CancellationToken.None);
            drive.WaitUntilListIsBlocked().GetAwaiter().GetResult();
            var delete = client.DeleteLandingAsync("delete-me", CancellationToken.None);
            Assert(delete.IsCompleted, "delete intent waited for the active network synchronization gate");
            Assert(!repository.Contains("delete-me"), "local delete waited for the network synchronization gate");
            Assert(
                new GoogleDriveSyncStateRepository(statePath).Load().PendingDeletes.Contains("delete-me"),
                "delete intent was not durable before the active sync completed");
            drive.ReleaseBlockedList();
            sync.GetAwaiter().GetResult();
            delete.GetAwaiter().GetResult();

            Assert(!repository.Contains("delete-me"), "durable delete restored the local landing");
            Assert(drive.IsLandingTrashed("delete-me"), "durable delete did not reach Drive trash");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void LegacyStateMigrationPreservesLateDelete()
    {
        var root = TemporaryDirectory("google-legacy-state-delete-race");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("legacy-delete", 282));
            var settings = new GoogleDriveCloudSettings();
            var statePath = Path.Combine(root, "state.json");
            using (var client = Service(
                       drive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                var legacyStateRepository = new GoogleDriveSyncStateRepository(statePath);
                var legacyState = legacyStateRepository.Load();
                legacyState.AccountPermissionId = null;
                legacyState.FormatVersion = 3;
                legacyStateRepository.Save(legacyState);

                drive.BlockListAfter(1);
                var sync = client.SyncAsync(CancellationToken.None);
                drive.WaitUntilListIsBlocked().GetAwaiter().GetResult();
                Assert(client.DeleteLandingLocally("legacy-delete"), "legacy landing was not deleted locally");
                client.QueueLandingDeletionAsync("legacy-delete", CancellationToken.None)
                    .GetAwaiter().GetResult();
                drive.ReleaseBlockedList();
                sync.GetAwaiter().GetResult();
            }

            Assert(
                new GoogleDriveSyncStateRepository(statePath).Load().PendingDeletes.Contains("legacy-delete"),
                "v3-to-v4 migration overwrote a late durable delete");
            using (var restarted = Service(
                       drive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                restarted.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            Assert(!repository.Contains("legacy-delete"), "legacy migration resurrected a late delete");
            Assert(drive.IsApplicationDeletedLanding("legacy-delete"), "legacy migration stranded a late delete");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void OfflineDeleteSurvivesRestart()
    {
        var root = TemporaryDirectory("google-offline-delete");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("delete-offline", 260));
            var settings = new GoogleDriveCloudSettings();
            var statePath = Path.Combine(root, "state.json");
            using (var firstProcess = Service(
                       drive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                firstProcess.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(firstProcess.DeleteLandingLocally("delete-offline"), "offline landing was not deleted locally");
                firstProcess.QueueLandingDeletionAsync("delete-offline", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            Assert(!repository.Contains("delete-offline"), "offline delete was undone before restart");
            using (var restarted = Service(
                       drive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                restarted.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            Assert(!repository.Contains("delete-offline"), "offline delete was resurrected after restart");
            Assert(drive.IsLandingTrashed("delete-offline"), "offline delete did not reach Drive after restart");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void LateDeleteCannotBeResurrected()
    {
        var root = TemporaryDirectory("google-late-delete");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var seedRepository = new LandingRepository(Path.Combine(root, "seed"));
            seedRepository.Save(Record("delete-during-download", 275));
            var settings = new GoogleDriveCloudSettings();
            using (var seed = Service(
                       drive,
                       seedRepository,
                       Path.Combine(root, "seed-state.json"),
                       () => settings,
                       value => settings = value))
            {
                seed.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            var repository = new LandingRepository(Path.Combine(root, "local"));
            var statePath = Path.Combine(root, "local-state.json");
            using var client = Service(
                drive,
                repository,
                statePath,
                () => settings,
                value => settings = value);
            drive.BlockNextDownload();
            var sync = client.SyncAsync(CancellationToken.None);
            drive.WaitUntilDownloadIsBlocked().GetAwaiter().GetResult();

            Assert(!client.DeleteLandingLocally("delete-during-download"), "remote-only landing unexpectedly existed locally");
            client.QueueLandingDeletionAsync("delete-during-download", CancellationToken.None)
                .GetAwaiter().GetResult();
            drive.ReleaseBlockedDownload();
            sync.GetAwaiter().GetResult();

            Assert(!repository.Contains("delete-during-download"), "active download resurrected a late delete");
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(!repository.Contains("delete-during-download"), "late delete was restored on the next sync");
            Assert(drive.IsLandingTrashed("delete-during-download"), "late delete did not reach Drive trash");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void InAppDeletePropagatesAcrossDevices()
    {
        var root = TemporaryDirectory("google-cross-device-delete");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var firstRepository = new LandingRepository(Path.Combine(root, "first"));
            firstRepository.Save(Record("shared-delete", 290));
            var secondRepository = new LandingRepository(Path.Combine(root, "second"));
            var firstSettings = new GoogleDriveCloudSettings();
            var secondSettings = new GoogleDriveCloudSettings();
            using var first = Service(
                drive,
                firstRepository,
                Path.Combine(root, "first-state.json"),
                () => firstSettings,
                value => firstSettings = value);
            using var second = Service(
                drive,
                secondRepository,
                Path.Combine(root, "second-state.json"),
                () => secondSettings,
                value => secondSettings = value);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(secondRepository.Contains("shared-delete"), "second device did not receive the landing");

            first.DeleteLandingAsync("shared-delete", CancellationToken.None).GetAwaiter().GetResult();
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert(!firstRepository.Contains("shared-delete"), "first device resurrected its deleted landing");
            Assert(!secondRepository.Contains("shared-delete"), "second device re-uploaded an in-app deletion");
            Equal(0, drive.ActiveLandingCount("shared-delete"), "deleted landing became active in Drive again");
            Assert(drive.IsApplicationDeletedLanding("shared-delete"), "Drive deletion provenance was not retained");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void InAppDeleteClaimsManuallyTrashedLanding()
    {
        var root = TemporaryDirectory("google-manual-trash-delete");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var firstRepository = new LandingRepository(Path.Combine(root, "first"));
            firstRepository.Save(Record("manual-trash-delete", 295));
            var secondRepository = new LandingRepository(Path.Combine(root, "second"));
            var firstSettings = new GoogleDriveCloudSettings();
            var secondSettings = new GoogleDriveCloudSettings();
            using var first = Service(
                drive,
                firstRepository,
                Path.Combine(root, "first-state.json"),
                () => firstSettings,
                value => firstSettings = value);
            using var second = Service(
                drive,
                secondRepository,
                Path.Combine(root, "second-state.json"),
                () => secondSettings,
                value => secondSettings = value);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            drive.ManuallyTrashLanding("manual-trash-delete");

            first.DeleteLandingAsync("manual-trash-delete", CancellationToken.None).GetAwaiter().GetResult();
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert(!firstRepository.Contains("manual-trash-delete"), "first device restored a manually trashed deletion");
            Assert(!secondRepository.Contains("manual-trash-delete"), "second device re-uploaded a claimed deletion");
            Assert(drive.IsApplicationDeletedLanding("manual-trash-delete"), "manual trash was not marked as an in-app deletion");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void PendingDeleteSurvivesAccountRoundTrip()
    {
        var root = TemporaryDirectory("google-delete-account-roundtrip");
        try
        {
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("account-delete", 245));
            var settings = new GoogleDriveCloudSettings();
            var statePath = Path.Combine(root, "state.json");
            var firstDrive = new FakeGoogleDriveApi("first-");
            using (var first = Service(
                       firstDrive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(first.DeleteLandingLocally("account-delete"), "account landing was not deleted locally");
                first.QueueLandingDeletionAsync("account-delete", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            var secondDrive = new FakeGoogleDriveApi("second-");
            using (var second = Service(
                       secondDrive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            Equal(0, secondDrive.ActiveLandingCount("account-delete"), "old-account delete leaked into a new account");

            using (var returned = Service(
                       firstDrive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                returned.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            Assert(firstDrive.IsApplicationDeletedLanding("account-delete"), "pending delete was lost after returning to its account");
            Assert(!repository.Contains("account-delete"), "returning to the original account resurrected the landing");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void UnscopedDeleteNeverAppliesToFirstAccount()
    {
        var root = TemporaryDirectory("google-unscoped-delete");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var seedRepository = new LandingRepository(Path.Combine(root, "seed"));
            seedRepository.Save(Record("ambiguous-delete", 235));
            var settings = new GoogleDriveCloudSettings();
            using (var seed = Service(
                       drive,
                       seedRepository,
                       Path.Combine(root, "seed-state.json"),
                       () => settings,
                       value => settings = value))
            {
                seed.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            var statePath = Path.Combine(root, "local-state.json");
            new GoogleDriveSyncStateRepository(statePath).AddPendingDelete("ambiguous-delete");
            var repository = new LandingRepository(Path.Combine(root, "local"));
            using (var client = Service(
                       drive,
                       repository,
                       statePath,
                       () => settings,
                       value => settings = value))
            {
                client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            Equal(1, drive.ActiveLandingCount("ambiguous-delete"), "unscoped delete damaged the first account");
            Assert(repository.Contains("ambiguous-delete"), "safe union did not restore the ambiguous local deletion");
            Assert(!drive.IsApplicationDeletedLanding("ambiguous-delete"), "unscoped delete gained false cloud provenance");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void LocalDeleteDoesNotRequireDriveStateStorage()
    {
        var root = TemporaryDirectory("google-local-delete");
        try
        {
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("local-only", 180));
            var invalidStatePath = Path.Combine(root, "state-directory");
            Directory.CreateDirectory(invalidStatePath);
            var settings = new GoogleDriveCloudSettings();
            using var client = Service(
                new FakeGoogleDriveApi(),
                repository,
                invalidStatePath,
                () => settings,
                value => settings = value);

            Assert(client.DeleteLandingLocally("local-only"), "local-only landing was not deleted");
            Assert(!repository.Contains("local-only"), "Drive state storage blocked a local-only delete");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void IdenticalFirstSyncDuplicatesConverge()
    {
        var root = TemporaryDirectory("google-duplicates");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("same-landing", 190));
            var settings = new GoogleDriveCloudSettings();
            using var client = Service(
                drive,
                repository,
                Path.Combine(root, "state.json"),
                () => settings,
                value => settings = value);
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            drive.DuplicateActiveLanding("same-landing");

            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(1, drive.ActiveLandingCount("same-landing"), "identical duplicate count after reconciliation");
            Assert(repository.Contains("same-landing"), "duplicate reconciliation lost the local landing");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void SimultaneousFirstSignInConvergesToOneRoot()
    {
        var root = TemporaryDirectory("google-root-race");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("root-race", 205));
            drive.InjectCompetingRootOnNextCreate(
                "competing-root",
                repository.SerializeForBackup(Record("competing-root", 225)));
            var settings = new GoogleDriveCloudSettings();
            using var client = Service(
                drive,
                repository,
                Path.Combine(root, "state.json"),
                () => settings,
                value => settings = value);

            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            Equal(2, drive.ActiveRootCount(), "race fixture did not retain both active roots");
            Equal(1, drive.ActiveLandingCount("root-race"), "landing count after root convergence");
            Equal(1, drive.ActiveLandingCount("competing-root"), "non-canonical root landing was ignored");
            Assert(repository.Contains("competing-root"), "non-canonical root landing was not merged into local history");
            Assert(drive.ActiveSettingsBytes().Length > 0, "settings were not stored under the converged root");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void CanonicalRootChangePreservesDeleteIntent()
    {
        var root = TemporaryDirectory("google-canonical-root-delete");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("canonical-delete", 255));
            var settings = new GoogleDriveCloudSettings();
            using var client = Service(
                drive,
                repository,
                Path.Combine(root, "state.json"),
                () => settings,
                value => settings = value);
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(client.DeleteLandingLocally("canonical-delete"), "canonical landing was not deleted locally");
            client.QueueLandingDeletionAsync("canonical-delete", CancellationToken.None)
                .GetAwaiter().GetResult();

            drive.AddRootWithId("000-concurrent-root");
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert(!repository.Contains("canonical-delete"), "canonical root change resurrected the landing");
            Assert(drive.IsApplicationDeletedLanding("canonical-delete"), "canonical root change stranded delete intent");
            Equal(0, drive.ActiveLandingCount("canonical-delete"), "canonical root change left active cloud data");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void TrashedCanonicalRootPreservesDeleteIntent()
    {
        var root = TemporaryDirectory("google-trashed-root-delete");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("trashed-root-delete", 265));
            var settings = new GoogleDriveCloudSettings();
            using var client = Service(
                drive,
                repository,
                Path.Combine(root, "state.json"),
                () => settings,
                value => settings = value);
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            var landingBytes = repository.ExportForBackup("trashed-root-delete");
            Assert(client.DeleteLandingLocally("trashed-root-delete"), "root landing was not deleted locally");
            client.QueueLandingDeletionAsync("trashed-root-delete", CancellationToken.None)
                .GetAwaiter().GetResult();

            drive.AddRootWithLanding("zzz-replacement-root", "trashed-root-delete", landingBytes);
            drive.TrashAllRootsExcept("zzz-replacement-root");
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert(!repository.Contains("trashed-root-delete"), "trashed canonical root resurrected the landing");
            Assert(drive.IsApplicationDeletedLanding("trashed-root-delete"), "root replacement stranded delete intent");
            Equal(0, drive.ActiveLandingCount("trashed-root-delete"), "replacement root retained deleted cloud data");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void ConcurrentLandingEditsAreRejectedWithoutLoss()
    {
        var root = TemporaryDirectory("google-landing-conflict");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var firstRepository = new LandingRepository(Path.Combine(root, "first"));
            var secondRepository = new LandingRepository(Path.Combine(root, "second"));
            firstRepository.Save(Record("shared", 300));
            secondRepository.Save(Record("shared", 300));
            var firstSettings = new GoogleDriveCloudSettings();
            var secondSettings = new GoogleDriveCloudSettings();
            using var first = Service(
                drive,
                firstRepository,
                Path.Combine(root, "first-state.json"),
                () => firstSettings,
                value => firstSettings = value);
            using var second = Service(
                drive,
                secondRepository,
                Path.Combine(root, "second-state.json"),
                () => secondSettings,
                value => secondSettings = value);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            var firstRecord = firstRepository.LoadAll().Single();
            firstRecord.Runway = "09L";
            firstRepository.UpdateSummary(firstRecord);
            var secondRecord = secondRepository.LoadAll().Single();
            secondRecord.Runway = "09R";
            secondRepository.UpdateSummary(secondRecord);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            secondSettings.Language = "ru";
            var conflict = second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(1, conflict.ConflictedLandingIds.Count, "landing conflict count");
            Equal("shared", conflict.ConflictedLandingIds[0], "conflicting landing id");
            Assert(conflict.UploadedSettings, "a landing conflict blocked settings synchronization");
            Equal("09R", secondRepository.LoadAll().Single().Runway, "conflicting local landing was modified");
            Equal(1, drive.ActiveLandingCount("shared"), "sequential conflict changed cloud revision count");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void SiblingRevisionConflictsAreIsolated()
    {
        var root = TemporaryDirectory("google-sibling-conflicts");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("shared", 320));
            var settings = new GoogleDriveCloudSettings();
            using var client = Service(
                drive,
                repository,
                Path.Combine(root, "state.json"),
                () => settings,
                value => settings = value);
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            var original = repository.ExportForBackup("shared");
            var left = repository.DeserializeBackup(original);
            left.Runway = "01L";
            var leftBytes = repository.SerializeForBackup(left);
            var right = repository.DeserializeBackup(original);
            right.Runway = "01R";
            drive.ForkActiveLanding("shared", leftBytes);
            drive.ForkActiveLanding("shared", repository.SerializeForBackup(right));
            drive.ForkActiveSettings(GoogleDriveCloudSettings.Serialize(new GoogleDriveCloudSettings
            {
                Language = "ru",
                StartWithSimulator = false,
            }));
            drive.ForkActiveSettings(GoogleDriveCloudSettings.Serialize(new GoogleDriveCloudSettings
            {
                Language = "en",
                StartWithSimulator = true,
            }));
            repository.Save(Record("unrelated", 180));

            var result = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.ConflictedLandingIds.Contains("shared"), "sibling landing conflict was not reported");
            Assert(result.SettingsConflict, "sibling settings conflict was not reported");
            Equal(1, drive.ActiveLandingCount("unrelated"), "unrelated landing was blocked by sibling conflicts");
            Assert(repository.Contains("shared"), "sibling conflict removed the local landing");

            var repeat = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(repeat.ConflictedLandingIds.Contains("shared"), "repeat sync lost the landing conflict warning");
            Assert(repeat.SettingsConflict, "repeat sync lost the settings conflict warning");
            Equal(1, drive.ActiveLandingCount("unrelated"), "repeat conflict sync duplicated unrelated data");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void MetadataOnlyLandingConflictMergesResolvedValues()
    {
        var root = TemporaryDirectory("google-metadata-merge");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var firstRepository = new LandingRepository(Path.Combine(root, "first"));
            var secondRepository = new LandingRepository(Path.Combine(root, "second"));
            firstRepository.Save(Record("shared", 215));
            secondRepository.Save(Record("shared", 215));
            var firstSettings = new GoogleDriveCloudSettings();
            var secondSettings = new GoogleDriveCloudSettings();
            using var first = Service(
                drive,
                firstRepository,
                Path.Combine(root, "first-state.json"),
                () => firstSettings,
                value => firstSettings = value);
            using var second = Service(
                drive,
                secondRepository,
                Path.Combine(root, "second-state.json"),
                () => secondSettings,
                value => secondSettings = value);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            var cloudResolved = firstRepository.LoadAll().Single();
            cloudResolved.Airport = "LBSF";
            cloudResolved.AirportDistanceNauticalMiles = 1.2;
            firstRepository.UpdateSummary(cloudResolved);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            var locallyResolved = secondRepository.LoadAll().Single();
            locallyResolved.Runway = "09";
            secondRepository.UpdateSummary(locallyResolved);
            var merged = second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            Equal(1, merged.ResolvedLandingConflicts, "resolved metadata conflict count");
            Equal(0, merged.ConflictedLandingIds.Count, "resolved metadata conflict remained blocked");
            Assert(merged.HistoryChanged, "metadata merge did not notify the UI to reload history");
            var result = secondRepository.LoadAll().Single();
            Equal("LBSF", result.Airport, "merged airport");
            Equal("09", result.Runway, "merged runway");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void MissingLocalSettingsRestoreFromCloud()
    {
        var root = TemporaryDirectory("google-missing-settings");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var seedRepository = new LandingRepository(Path.Combine(root, "seed"));
            var cloudSettings = new GoogleDriveCloudSettings
            {
                Language = "ru",
                StartWithSimulator = true,
            };
            using (var seed = Service(
                       drive,
                       seedRepository,
                       Path.Combine(root, "seed-state.json"),
                       () => cloudSettings,
                       value => cloudSettings = value))
            {
                seed.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            var localSettings = new GoogleDriveCloudSettings();
            var localIsReadable = false;
            using var client = Service(
                drive,
                new LandingRepository(Path.Combine(root, "local")),
                Path.Combine(root, "local-state.json"),
                () => localSettings,
                value => localSettings = value,
                () => localIsReadable);
            var result = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.DownloadedSettings, "unreadable local settings did not restore from Drive");
            Equal("ru", localSettings.Language, "restored language");
            Equal(true, localSettings.StartWithSimulator, "restored auto-start preference");
            Assert(!result.UploadedSettings, "defaults overwrote valid cloud settings");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void SettingsRecoverySurvivesOfflineRestart()
    {
        var root = TemporaryDirectory("google-settings-restart");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var cloudSettings = new GoogleDriveCloudSettings
            {
                Language = "ru",
                StartWithSimulator = true,
            };
            using (var seed = Service(
                       drive,
                       new LandingRepository(Path.Combine(root, "seed")),
                       Path.Combine(root, "seed-state.json"),
                       () => cloudSettings,
                       value => cloudSettings = value))
            {
                seed.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            var settingsPath = Path.Combine(root, "settings.json");
            var firstProcess = new ApplicationSettingsRepository(settingsPath);
            Equal(false, firstProcess.TryLoad(out var fallback), "missing settings are unreadable");
            firstProcess.MarkGoogleDriveRestorePending();
            fallback.Language = "en";
            fallback.StartWithSimulator = false;
            fallback.GoogleDrivePromptAnswered = true;
            firstProcess.Save(fallback);

            var restarted = new ApplicationSettingsRepository(settingsPath);
            Equal(true, restarted.TryLoad(out var restartedSettings), "fallback settings file is readable after restart");
            Assert(restarted.GoogleDriveRestorePending, "pending cloud restore marker did not survive restart");

            var localSettings = new GoogleDriveCloudSettings
            {
                Language = restartedSettings.Language,
                StartWithSimulator = restartedSettings.StartWithSimulator,
            };
            using var client = Service(
                drive,
                new LandingRepository(Path.Combine(root, "local")),
                Path.Combine(root, "local-state.json"),
                () => localSettings,
                value => localSettings = value,
                () => !restarted.GoogleDriveRestorePending);
            var result = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert(result.DownloadedSettings, "pending recovery did not download cloud settings after restart");
            Equal("ru", localSettings.Language, "restart recovery restored language");
            Equal(true, localSettings.StartWithSimulator, "restart recovery restored auto-start");
            Assert(!result.UploadedSettings, "restart fallback overwrote cloud settings");
            // Applying cloud settings writes a readable settings file before the
            // enclosing sync completes. Completion must still clear the durable
            // marker rather than keying off that in-memory readability flag.
            restartedSettings.Language = localSettings.Language;
            restartedSettings.StartWithSimulator = localSettings.StartWithSimulator;
            restarted.Save(restartedSettings);
            Assert(restarted.GoogleDriveRestorePending, "cloud apply cleared recovery before sync completion");
            restarted.CompleteGoogleDriveRestore(restartedSettings);
            Assert(!restarted.GoogleDriveRestorePending, "successful recovery marker was not cleared");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void ConcurrentSettingsEditsAreRejectedWithoutLoss()
    {
        var root = TemporaryDirectory("google-settings-conflict");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var firstSettings = new GoogleDriveCloudSettings
            {
                Language = "en",
                StartWithSimulator = false,
            };
            var secondSettings = firstSettings.Clone();
            using var first = Service(
                drive,
                new LandingRepository(Path.Combine(root, "first")),
                Path.Combine(root, "first-state.json"),
                () => firstSettings,
                value => firstSettings = value);
            using var second = Service(
                drive,
                new LandingRepository(Path.Combine(root, "second")),
                Path.Combine(root, "second-state.json"),
                () => secondSettings,
                value => secondSettings = value);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            firstSettings.Language = "ru";
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            secondSettings.StartWithSimulator = true;

            var resolved = second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(resolved.ResolvedSettingsConflict, "settings conflict was not resolved");
            Assert(resolved.UploadedSettings, "resolved settings were not uploaded");
            Equal("en", secondSettings.Language, "winning local language was modified");
            Equal(true, secondSettings.StartWithSimulator, "winning local auto-start was modified");
            var cloud = GoogleDriveCloudSettings.Deserialize(drive.ActiveSettingsBytes());
            Equal("en", cloud.Language, "resolved cloud language");
            Equal(true, cloud.StartWithSimulator, "resolved cloud auto-start");

            var convergence = first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(convergence.DownloadedSettings, "other device did not converge after conflict resolution");
            Equal("en", firstSettings.Language, "converged language");
            Equal(true, firstSettings.StartWithSimulator, "converged auto-start");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void SettingsChangedDuringDownloadArePreserved()
    {
        var root = TemporaryDirectory("google-settings-download-race");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var firstSettings = new GoogleDriveCloudSettings
            {
                Language = "en",
                StartWithSimulator = false,
            };
            var secondSettings = firstSettings.Clone();
            using var first = Service(
                drive,
                new LandingRepository(Path.Combine(root, "first")),
                Path.Combine(root, "first-state.json"),
                () => firstSettings,
                value => firstSettings = value);
            using var second = Service(
                drive,
                new LandingRepository(Path.Combine(root, "second")),
                Path.Combine(root, "second-state.json"),
                () => secondSettings,
                value => secondSettings = value);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            firstSettings.Language = "ru";
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            drive.BlockNextDownload();
            var sync = second.SyncAsync(CancellationToken.None);
            drive.WaitUntilDownloadIsBlocked().GetAwaiter().GetResult();
            secondSettings.StartWithSimulator = true;
            drive.ReleaseBlockedDownload();

            var result = sync.GetAwaiter().GetResult();
            Assert(result.SettingsChangedDuringSync, "settings race was not reported");
            Equal("en", secondSettings.Language, "download overwrote a concurrently edited language");
            Equal(true, secondSettings.StartWithSimulator, "download overwrote a concurrently edited auto-start value");

            var convergence = second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(convergence.UploadedSettings, "preserved local settings did not converge on the next sync");
            var cloud = GoogleDriveCloudSettings.Deserialize(drive.ActiveSettingsBytes());
            Equal("en", cloud.Language, "converged cloud language after settings race");
            Equal(true, cloud.StartWithSimulator, "converged cloud auto-start after settings race");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void FutureSettingsRemainUntouched()
    {
        var root = TemporaryDirectory("google-future-settings");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var settings = new GoogleDriveCloudSettings
            {
                Language = "en",
                StartWithSimulator = false,
            };
            using var client = Service(
                drive,
                new LandingRepository(Path.Combine(root, "local")),
                Path.Combine(root, "state.json"),
                () => settings,
                value => settings = value);
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            var futureBytes = Encoding.UTF8.GetBytes(
                "{\"formatVersion\":2,\"language\":\"ru\",\"startWithSimulator\":true," +
                "\"futureOption\":{\"enabled\":true}}");
            drive.ReplaceActiveSettings(futureBytes);

            var first = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(2, first.UnsupportedSettingsFormatVersion, "future settings warning version");
            Assert(drive.ActiveSettingsBytes().SequenceEqual(futureBytes), "old client rewrote future settings");
            Equal("en", settings.Language, "future settings changed local language");
            Equal(false, settings.StartWithSimulator, "future settings changed local auto-start");

            var second = client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(2, second.UnsupportedSettingsFormatVersion, "repeat future settings warning version");
            Assert(drive.ActiveSettingsBytes().SequenceEqual(futureBytes), "repeat sync rewrote future settings");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    public static void UnchangedLandingSyncUsesLightweightFingerprint()
    {
        var root = TemporaryDirectory("google-fingerprint");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var repository = new LandingRepository(Path.Combine(root, "local"));
            repository.Save(Record("unchanged", 210));
            var settings = new GoogleDriveCloudSettings();
            using var client = Service(
                drive,
                repository,
                Path.Combine(root, "state.json"),
                () => settings,
                value => settings = value);
            client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();

            var detail = Directory.EnumerateFiles(repository.RootPath, "*.landing.json.gz").Single();
            using (new FileStream(detail, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                client.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static GoogleDriveBackupService Service(
        IGoogleDriveApi drive,
        LandingRepository repository,
        string statePath,
        Func<GoogleDriveCloudSettings> readSettings,
        Action<GoogleDriveCloudSettings> applySettings,
        Func<bool>? settingsPersistedAndReadable = null)
    {
        return new GoogleDriveBackupService(
            drive,
            repository,
            new object(),
            new GoogleDriveSyncStateRepository(statePath),
            () => Task.FromResult(new GoogleDriveLocalSettings(
                readSettings(),
                settingsPersistedAndReadable?.Invoke() ?? true)),
            (value, expectedLocalHash) =>
            {
                var currentHash = GoogleDriveBackupService.Hash(
                    GoogleDriveCloudSettings.Serialize(readSettings().Clone()));
                if (!string.Equals(currentHash, expectedLocalHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(false);
                }
                applySettings(value);
                return Task.FromResult(true);
            });
    }

    private static LandingRecord Record(string id, double inertialFpm) => new LandingRecord
    {
        Id = id,
        TimestampUtc = new DateTime(2026, 8, 29, 1, 2, 3, DateTimeKind.Utc),
        AircraftTitle = "Regression aircraft",
        InertialFpm = inertialFpm,
        SurfaceFpm = inertialFpm,
    };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&')
            .Select(part => part.Split(new[] { '=' }, 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part.Length == 2 ? part[1].Replace("+", " ") : string.Empty),
                StringComparer.Ordinal);
    }

    private static string TemporaryDirectory(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "landing-stats-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class FakeGoogleDriveApi : IGoogleDriveApi
    {
        private readonly Dictionary<string, Entry> _files = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly string _idPrefix;
        private string _accountPermissionId;
        private int _nextId;
        private TaskCompletionSource<bool>? _nextListBlocked;
        private TaskCompletionSource<bool>? _nextListRelease;
        private int _listCallsBeforeBlock;
        private TaskCompletionSource<bool>? _nextDownloadBlocked;
        private TaskCompletionSource<bool>? _nextDownloadRelease;
        private bool _injectCompetingRootOnNextCreate;
        private string? _competingRootLandingId;
        private byte[]? _competingRootLandingBytes;

        public FakeGoogleDriveApi(string idPrefix = "")
        {
            _idPrefix = idPrefix;
            _accountPermissionId = string.IsNullOrWhiteSpace(idPrefix)
                ? "fake-account"
                : "fake-account-" + idPrefix.TrimEnd('-');
        }

        public Task<string> GetAccountPermissionIdAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_accountPermissionId);
        }

        public void SetAccountPermissionId(string accountPermissionId)
        {
            if (string.IsNullOrWhiteSpace(accountPermissionId))
            {
                throw new ArgumentException("An account identity is required.", nameof(accountPermissionId));
            }
            _accountPermissionId = accountPermissionId;
        }

        public async Task<IReadOnlyList<GoogleDriveFile>> ListApplicationFilesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocked = _nextListBlocked;
            var release = _nextListRelease;
            if (blocked != null && release != null && _listCallsBeforeBlock > 0)
            {
                _listCallsBeforeBlock--;
            }
            else if (blocked != null && release != null)
            {
                blocked.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                _nextListBlocked = null;
                _nextListRelease = null;
            }
            return _files.Values.Select(ToFile).ToArray();
        }

        public Task<GoogleDriveFile> CreateFolderAsync(
            string name,
            string? parentId,
            IReadOnlyDictionary<string, string> appProperties,
            CancellationToken cancellationToken)
        {
            var created = Create(
                name,
                "application/vnd.google-apps.folder",
                parentId == null ? Array.Empty<string>() : new[] { parentId },
                appProperties,
                Array.Empty<byte>());
            if (_injectCompetingRootOnNextCreate &&
                parentId == null &&
                appProperties.TryGetValue("kind", out var kind) &&
                string.Equals(kind, "root", StringComparison.Ordinal))
            {
                _injectCompetingRootOnNextCreate = false;
                var competingRoot = Create(
                    name,
                    "application/vnd.google-apps.folder",
                    Array.Empty<string>(),
                    appProperties,
                    Array.Empty<byte>());
                var competingFolder = Create(
                    "Landings",
                    "application/vnd.google-apps.folder",
                    new[] { competingRoot.Id },
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["kind"] = "landings-folder",
                    },
                    Array.Empty<byte>());
                var landingBytes = _competingRootLandingBytes ?? throw new InvalidOperationException(
                    "The competing root landing payload is missing.");
                var landingId = _competingRootLandingId ?? throw new InvalidOperationException(
                    "The competing root landing id is missing.");
                Create(
                    landingId + ".landing.json.gz",
                    "application/gzip",
                    new[] { competingFolder.Id },
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["kind"] = "landing",
                        ["landingId"] = landingId,
                        ["sha256"] = GoogleDriveBackupService.Hash(landingBytes),
                    },
                    landingBytes);
            }
            return Task.FromResult(created);
        }

        public Task<GoogleDriveFile> UploadFileAsync(
            string name,
            string mimeType,
            string parentId,
            IReadOnlyDictionary<string, string> appProperties,
            byte[] content,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Create(name, mimeType, new[] { parentId }, appProperties, content));
        }

        public Task<GoogleDriveFile> UpdateFileAsync(
            string fileId,
            string name,
            string mimeType,
            IReadOnlyDictionary<string, string> appProperties,
            byte[] content,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _files[fileId];
            entry.Name = name;
            entry.MimeType = mimeType;
            entry.Properties = Properties(appProperties);
            entry.Content = content.ToArray();
            return Task.FromResult(ToFile(entry));
        }

        public Task<GoogleDriveFile> UpdateAppPropertiesAsync(
            string fileId,
            IReadOnlyDictionary<string, string> appProperties,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _files[fileId];
            entry.Properties = Properties(appProperties);
            return Task.FromResult(ToFile(entry));
        }

        public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocked = _nextDownloadBlocked;
            var release = _nextDownloadRelease;
            if (blocked != null && release != null)
            {
                blocked.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                _nextDownloadBlocked = null;
                _nextDownloadRelease = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return _files[fileId].Content.ToArray();
        }

        public Task TrashFileAsync(string fileId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files[fileId].Trashed = true;
            return Task.CompletedTask;
        }

        public byte[] ActiveSettingsBytes() => _files.Values.Single(entry =>
            !entry.Trashed && entry.Property("kind") == "settings").Content.ToArray();

        public int ActiveRootCount() => _files.Values.Count(entry =>
            !entry.Trashed && entry.Property("kind") == "root");

        public void AddRootWithId(string id)
        {
            var entry = new Entry
            {
                Id = id,
                Name = "MSFS Landing Stats",
                MimeType = "application/vnd.google-apps.folder",
                Parents = Array.Empty<string>(),
                Properties = Properties(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = "root",
                }),
                Content = Array.Empty<byte>(),
            };
            _files.Add(entry.Id, entry);
        }

        public void AddRootWithLanding(string rootId, string landingId, byte[] landingBytes)
        {
            AddRootWithId(rootId);
            var folder = Create(
                "Landings",
                "application/vnd.google-apps.folder",
                new[] { rootId },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = "landings-folder",
                },
                Array.Empty<byte>());
            Create(
                landingId + ".landing.json.gz",
                "application/gzip",
                new[] { folder.Id },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = "landing",
                    ["landingId"] = landingId,
                    ["sha256"] = GoogleDriveBackupService.Hash(landingBytes),
                },
                landingBytes);
        }

        public void TrashAllRootsExcept(string retainedRootId)
        {
            foreach (var root in _files.Values.Where(entry =>
                         !entry.Trashed &&
                         entry.Property("kind") == "root" &&
                         !string.Equals(entry.Id, retainedRootId, StringComparison.Ordinal)))
            {
                root.Trashed = true;
            }
        }

        public bool IsLandingTrashed(string landingId) => _files.Values.Any(entry =>
            entry.Trashed && entry.Property("landingId") == landingId);

        public bool IsApplicationDeletedLanding(string landingId) => _files.Values.Any(entry =>
            entry.Trashed &&
            entry.Property("landingId") == landingId &&
            string.Equals(entry.Property("deletedByApplication"), "true", StringComparison.OrdinalIgnoreCase));

        public void ManuallyTrashLanding(string landingId)
        {
            var entry = _files.Values.Single(value =>
                !value.Trashed && value.Property("landingId") == landingId);
            entry.Trashed = true;
        }

        public int ActiveLandingCount(string landingId) => _files.Values.Count(entry =>
            !entry.Trashed && entry.Property("landingId") == landingId);

        public int ActiveLandingCountInActiveFolder(string landingId)
        {
            var activeFolders = _files.Values
                .Where(entry => !entry.Trashed && entry.Property("kind") == "landings-folder")
                .Select(entry => entry.Id)
                .ToHashSet(StringComparer.Ordinal);
            return _files.Values.Count(entry =>
                !entry.Trashed &&
                entry.Property("landingId") == landingId &&
                entry.Parents.Any(activeFolders.Contains));
        }

        public void TrashLandingsFolderOnly()
        {
            var folder = _files.Values.Single(entry =>
                !entry.Trashed && entry.Property("kind") == "landings-folder");
            folder.Trashed = true;
        }

        public void DuplicateActiveLanding(string landingId)
        {
            var source = _files.Values.Single(entry =>
                !entry.Trashed && entry.Property("landingId") == landingId);
            Create(source.Name, source.MimeType, source.Parents, source.Properties, source.Content);
        }

        public void ForkActiveLanding(string landingId, byte[] content)
        {
            var source = _files.Values.Single(entry =>
                !entry.Trashed &&
                entry.Property("landingId") == landingId &&
                string.IsNullOrWhiteSpace(entry.Property("baseSha256")));
            var properties = Properties(source.Properties);
            properties["sha256"] = GoogleDriveBackupService.Hash(content);
            properties["baseSha256"] = source.Property("sha256") ?? throw new InvalidOperationException(
                "The active landing does not have a revision hash.");
            Create(source.Name, source.MimeType, source.Parents, properties, content);
        }

        public void ForkActiveSettings(byte[] content)
        {
            var source = _files.Values.Single(entry =>
                !entry.Trashed &&
                entry.Property("kind") == "settings" &&
                string.IsNullOrWhiteSpace(entry.Property("baseSha256")));
            var properties = Properties(source.Properties);
            properties["sha256"] = GoogleDriveBackupService.Hash(content);
            properties["baseSha256"] = source.Property("sha256") ?? throw new InvalidOperationException(
                "The active settings do not have a revision hash.");
            Create(source.Name, source.MimeType, source.Parents, properties, content);
        }

        public void ReplaceActiveSettings(byte[] content)
        {
            var source = _files.Values.Single(entry =>
                !entry.Trashed && entry.Property("kind") == "settings");
            source.Content = content.ToArray();
            source.Properties["sha256"] = GoogleDriveBackupService.Hash(content);
        }

        public void InjectCompetingRootOnNextCreate(string landingId, byte[] landingBytes)
        {
            _injectCompetingRootOnNextCreate = true;
            _competingRootLandingId = landingId;
            _competingRootLandingBytes = landingBytes.ToArray();
        }

        public void BlockNextList()
        {
            BlockListAfter(0);
        }

        public void BlockListAfter(int completedCalls)
        {
            if (completedCalls < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(completedCalls));
            }
            _listCallsBeforeBlock = completedCalls;
            _nextListBlocked = new TaskCompletionSource<bool>();
            _nextListRelease = new TaskCompletionSource<bool>();
        }

        public Task WaitUntilListIsBlocked() =>
            _nextListBlocked?.Task ?? throw new InvalidOperationException("No Drive list is blocked.");

        public void ReleaseBlockedList()
        {
            _nextListRelease?.TrySetResult(true);
        }

        public void BlockNextDownload()
        {
            _nextDownloadBlocked = new TaskCompletionSource<bool>();
            _nextDownloadRelease = new TaskCompletionSource<bool>();
        }

        public Task WaitUntilDownloadIsBlocked() =>
            _nextDownloadBlocked?.Task ?? throw new InvalidOperationException("No Drive download is blocked.");

        public void ReleaseBlockedDownload()
        {
            _nextDownloadRelease?.TrySetResult(true);
        }

        public void TrashLanding(string landingId)
        {
            var entry = _files.Values.Single(value =>
                !value.Trashed && value.Property("landingId") == landingId);
            entry.Trashed = true;
        }

        private GoogleDriveFile Create(
            string name,
            string mimeType,
            IReadOnlyList<string> parents,
            IReadOnlyDictionary<string, string> appProperties,
            byte[] content)
        {
            var entry = new Entry
            {
                Id = _idPrefix + "file-" + (++_nextId),
                Name = name,
                MimeType = mimeType,
                Parents = parents.ToArray(),
                Properties = Properties(appProperties),
                Content = content.ToArray(),
            };
            _files.Add(entry.Id, entry);
            return ToFile(entry);
        }

        private static Dictionary<string, string> Properties(IReadOnlyDictionary<string, string> source)
        {
            var values = source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            values["application"] = "msfs-landing-stats";
            return values;
        }

        private static GoogleDriveFile ToFile(Entry entry) => new GoogleDriveFile(
            entry.Id,
            entry.Name,
            entry.MimeType,
            entry.Trashed,
            entry.Properties,
            entry.Parents);

        private sealed class Entry
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string MimeType { get; set; } = string.Empty;
            public bool Trashed { get; set; }
            public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
            public string[] Parents { get; set; } = Array.Empty<string>();
            public byte[] Content { get; set; } = Array.Empty<byte>();

            public string? Property(string key) => Properties.TryGetValue(key, out var value) ? value : null;
        }
    }
}
