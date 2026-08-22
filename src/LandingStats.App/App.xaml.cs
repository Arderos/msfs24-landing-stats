using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace LandingStats.App;

public partial class App : Application
{
    private ApplicationInstanceGuard? _instanceGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!ApplicationInstanceGuard.TryAcquire(out _instanceGuard))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        base.OnStartup(e);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        _instanceGuard?.Dispose();
        _instanceGuard = null;
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
