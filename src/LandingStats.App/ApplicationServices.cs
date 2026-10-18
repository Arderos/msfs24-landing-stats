using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.GoogleDrive;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using LandingStats.App.Storage;
using LandingStats.App.Updates;

namespace LandingStats.App;

// Application composition is independent of window construction and UI callbacks.
// The only runtime callback is its replaceable presentation projection.
internal sealed class ApplicationServices
{
    public ApplicationSettingsRepository SettingsRepository { get; } = new ApplicationSettingsRepository();
    public SimulatorAutoStartManager AutoStart { get; } = new SimulatorAutoStartManager();
    public LandingRepository Landings { get; } = new LandingRepository();
    public RawCaptureRepository LegacyRaw { get; } = new RawCaptureRepository(null, false);
    public AirportFacilityRepository Airports { get; } = new AirportFacilityRepository();
    public ReleaseUpdater Updater { get; } = new ReleaseUpdater();
    public GoogleOAuthTokenStore Tokens { get; } = new GoogleOAuthTokenStore();
    public DeferredGoogleDriveApi CloudBinding { get; } = new DeferredGoogleDriveApi();
    public object LandingGate { get; } = new object();
    public object AirportGate { get; } = new object();
    public ApplicationSettingsOwner Settings { get; }
    public GoogleDriveBackupService Backup { get; }
    public ApplicationServices()
    {
        Settings = new ApplicationSettingsOwner(SettingsRepository, AutoStart);
        Backup = new GoogleDriveBackupService(CloudBinding, Landings, LandingGate, new GoogleDriveSyncStateRepository(),
            Settings.ReadCloudAsync, Settings.ApplyCloudAsync);
    }
    public LandingRuntime CreateRuntime(Func<RuntimePresentationSnapshot, CancellationToken, Task> present, Action<string, long> escalate)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSFS Landing Stats");
        return new LandingRuntime(Path.Combine(root, "Runtime"), AppDomain.CurrentDomain.BaseDirectory, Path.Combine(root, "Telemetry Upload"),
            Landings, LandingGate, present, escalate, legacyQueue: LegacyRaw.RootPath, airportRepository: Airports, airportGate: AirportGate,
            cloud: new RuntimeCloudServices(Tokens, Backup, CloudBinding, Settings.CompleteCloudRestoreAsync));
    }
    public Task<Exception?> DeleteLandingAsync(string id) => Task.Run(async () =>
    {
        Backup.DeleteLandingLocally(id);
        try { await Backup.QueueLandingDeletionAsync(id, CancellationToken.None).ConfigureAwait(false); return (Exception?)null; }
        catch (Exception error) { return error; }
    });
}
