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
            await ConnectGuardianAsync();
            await Dispatcher.InvokeAsync(async () =>
            {
            if (!ApplicationInstanceGuard.TryAcquire(out _instanceGuard))
            {
                // A direct launch can race the outer single-instance check.
                // Acknowledge this duplicate's exit instead of retrying forever.
                await AcknowledgeExitAsync(Runtime.GuardianStopReason.UserExit);
                ShutdownMode = ShutdownMode.OnExplicitShutdown; Shutdown(0); return;
            }
            var window = new MainWindow(); Volatile.Write(ref _runtimeWindow, window);
            MainWindow = window; MainWindow.Show();
            }).Task.Unwrap();
        }
        catch (Exception error) { WriteCrashLog("startup", error); await Dispatcher.InvokeAsync(() => Shutdown(70)); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
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
