using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using LandingStats.App.Settings;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.App;

public partial class App : Application
{
    internal const string SimulatorAutoStartEnvironmentVariable = "MSFS_LANDING_STATS_SIMULATOR_AUTOSTART";
    private ApplicationInstanceGuard? _instanceGuard;
    private Runtime.LandingFileActivation? _landingFileActivation;
    private bool _fileActivationStopped;
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> RecoveryRequests = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
    internal static void RequestRuntimeRecovery(string component, long generation)
    { RecoveryRequests.AddOrUpdate(component, generation, (_, previous) => Math.Max(previous, generation)); }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // The updater and launcher inherit this process-only flag on restart.
        if (Array.IndexOf(e.Args, SimulatorAutoStartManager.LaunchArgument) >= 0)
        {
            Environment.SetEnvironmentVariable(SimulatorAutoStartEnvironmentVariable, "1");
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        base.OnStartup(e);
        try
        {
            // A bundled App has already been registered and its file arguments
            // committed by the outer launcher. Direct developer launches use the
            // same durable handoff before the inner single-instance boundary.
            var associationError = Runtime.LandingFileAssociation.TakeStartupError();
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SimulatorAutoStartManager.LauncherPathEnvironmentVariable)))
            {
                try { Runtime.LandingFileAssociation.Register(typeof(App).Assembly.Location); }
                catch (Exception error) { associationError = error.Message; WriteCrashLog("file-association", error); }
            }
            var files = Runtime.LandingFileActivation.FileArguments(e.Args);
            if (files.Length != 0)
            {
                using var requests = new Runtime.LandingFileActivation();
                requests.Submit(files);
                Environment.SetEnvironmentVariable(UpdateProtocol.ReleaseUpdateProtocol.BackgroundRestartEnvironmentVariable, null);
            }
            await ConnectGuardianAsync();
            await AdmitStartupAsync(async () =>
            {
            if (!ApplicationInstanceGuard.TryAcquire(out _instanceGuard))
            {
                // A direct launch can race the outer single-instance check.
                // Acknowledge this duplicate's exit instead of retrying forever.
                await AcknowledgeExitAsync(Runtime.GuardianStopReason.UserExit);
                ShutdownMode = ShutdownMode.OnExplicitShutdown; Shutdown(0); return;
            }
            var window = new MainWindow(); Volatile.Write(ref _runtimeWindow, window);
            ConfigureStartupWindow(window, e.Args);
            MainWindow = window; MainWindow.Show();
            if (!string.IsNullOrEmpty(associationError)) window.ShowLandingSharingError(LocalizationManager.Format("Sharing.AssociationFailedFormat", associationError!));
            try
            {
                StartFileActivationIntake(paths => Dispatcher.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _intentionalExit) != 0) return Task.FromCanceled(new CancellationToken(true));
                    ActivateFromGuardian();
                    return window.QueueLandingImports(paths);
                }).Task.Unwrap(), error =>
                {
                    WriteCrashLog("file-activation", error);
                    _ = Dispatcher.BeginInvoke(new Action(() => window.ShowLandingSharingError(error.Message)));
                });
            }
            catch (Exception error) { WriteCrashLog("file-activation-start", error); window.ShowLandingSharingError(error.Message); }
            });
        }
        catch (Exception error) { WriteCrashLog("startup", error); await Dispatcher.InvokeAsync(() => Shutdown(70)); }
    }

    internal bool StartFileActivationIntake(Func<string[], Task> handle, Action<Exception> failed,
        string? root = null, string? signalName = null)
    {
        // Window construction can overlap a guardian stop. Recheck admission
        // at the receiver's ownership boundary, shared with every disposal path.
        lock (_intentGate)
        {
            if (_fileActivationStopped || _stopIntent != null) return false;
            if (_landingFileActivation != null) throw new InvalidOperationException("File activation is already admitted.");
            var receiver = new Runtime.LandingFileActivation(root, signalName);
            try { receiver.Start(handle, failed); }
            catch { receiver.Dispose(); throw; }
            _landingFileActivation = receiver;
            return true;
        }
    }

    internal async Task AdmitStartupAsync(Func<Task> startup)
    {
        try
        {
            await Dispatcher.InvokeAsync(() => Volatile.Read(ref _intentionalExit) != 0
                ? Task.CompletedTask : startup()).Task.Unwrap().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _intentionalExit) != 0)
        {
            // Session end can retire the dispatcher between the guardian
            // handshake and window admission. Its acknowledged exit owns this
            // cancellation; no window or activation receiver should start.
        }
    }

    internal static void ConfigureStartupWindow(Window window, string[] arguments)
    {
        var variable = UpdateProtocol.ReleaseUpdateProtocol.BackgroundRestartEnvironmentVariable;
        var fileOpen = Array.Exists(arguments, Runtime.LandingFileActivation.IsLandingFile);
        if (!fileOpen && (Array.IndexOf(arguments, SimulatorAutoStartManager.LaunchArgument) >= 0 ||
            Environment.GetEnvironmentVariable(variable) == "1")) BackgroundWindowStartup.Configure(window);
        Environment.SetEnvironmentVariable(variable, null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        StopFileActivationIntake();
        _instanceGuard?.Dispose();
        _instanceGuard = null;
        _guardian?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        WriteCrashLog("dispatcher", eventArgs.Exception);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        WriteCrashLog("app-domain", eventArgs.ExceptionObject as Exception);
    }

    private static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MSFS Landing Stats");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "crash.log");
            if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
            {
                var previous = path + ".previous";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            var text = new StringBuilder()
                .AppendLine(DateTime.UtcNow.ToString("O"))
                .AppendLine($"source={source}")
                .AppendLine(exception?.ToString() ?? "Unknown unhandled exception")
                .AppendLine()
                .ToString();
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch
        {
            // Never hide the original failure because crash reporting failed.
        }
    }
}
