using System.IO;
using System.Threading;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static partial class GoogleDriveBackupRegressionTests
{
    public static void DamagedLandingAndExpiredTrashRemainIsolated()
    {
        var root = TemporaryDirectory("google-damaged-and-trash");
        try
        {
            var drive = new FakeGoogleDriveApi();
            var source = new LandingRepository(Path.Combine(root, "source"));
            source.Save(Record("good", 200)); source.Save(Record("damaged", 300));
            var sourceSettings = new GoogleDriveCloudSettings { Language = "ru" };
            using var first = Service(drive, source, Path.Combine(root, "first.json"), () => sourceSettings, value => sourceSettings = value);
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            drive.CorruptLanding("damaged");
            var restored = new LandingRepository(Path.Combine(root, "restored"));
            var restoredSettings = new GoogleDriveCloudSettings { Language = "en" };
            using var second = Service(drive, restored, Path.Combine(root, "second.json"), () => restoredSettings, value => restoredSettings = value);
            var result = second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.FailedLandings.ContainsKey("damaged"), "Damaged remote content was not isolated by landing identity.");
            Assert(restored.Contains("good") && !restored.Contains("damaged"), "Damaged landing blocked or polluted other history.");
            Equal("ru", restoredSettings.Language, "Damaged landing blocked settings synchronization");
            first.DeleteLandingLocally("good"); first.QueueLandingDeletionAsync("good", CancellationToken.None).GetAwaiter().GetResult();
            first.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            drive.EmptyTrash();
            second.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(!restored.Contains("good") && drive.ActiveLandingCount("good") == 0,
                "A device resurrected an application deletion after Drive trash was emptied.");
        }
        finally { DeleteDirectory(root); }
    }
}
