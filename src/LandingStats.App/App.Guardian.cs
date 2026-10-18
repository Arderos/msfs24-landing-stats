using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LandingStats.App.Runtime;

namespace LandingStats.App;

public partial class App
{
    private GuardianClient? _guardian;
    private MainWindow? _runtimeWindow;
    private long _uiProgressQpc = Stopwatch.GetTimestamp();
    private static long _shutdownProgressQpc;
    internal static long ShutdownProgressQpc => Interlocked.Read(ref _shutdownProgressQpc);
    internal static void ReportShutdownProgress() => Interlocked.Exchange(ref _shutdownProgressQpc, Stopwatch.GetTimestamp());
    private int _uiProbePending, _intentionalExit;
    private Task? _stopIntent;
    private readonly object _intentGate = new object();
    private async Task ConnectGuardianAsync()
    {
        var configuration = Environment.GetEnvironmentVariable(GuardianDescriptor.EnvironmentName);
        if (string.IsNullOrEmpty(configuration)) return; // Direct developer launch has no resident bundle.
        var descriptor = GuardianDescriptor.Parse(configuration);
        Environment.SetEnvironmentVariable(GuardianDescriptor.EnvironmentName, null);
        _guardian = new GuardianClient(descriptor, ReadGuardianHealth, ActivateFromGuardian, PrepareGuardianRecoveryAsync);
        await _guardian.ConnectAsync();
        _ = _guardian.Completion.ContinueWith(_ =>
        {
            if (Volatile.Read(ref _intentionalExit) == 0)
            {
                // The launcher owns this application's entire job. If its
                // authenticated control path is lost, never keep an orphaned
                // recorder that a new launcher could mistake for a replacement.
                Environment.Exit(72);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private GuardianStatus ReadGuardianHealth()
    {
        if (Interlocked.CompareExchange(ref _uiProbePending, 1, 0) == 0)
        {
            try
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                { Interlocked.Exchange(ref _uiProgressQpc, Stopwatch.GetTimestamp()); Interlocked.Exchange(ref _uiProbePending, 0); }), DispatcherPriority.Background);
            }
            catch (InvalidOperationException) { }
        }
        var window = Volatile.Read(ref _runtimeWindow);
        return window?.ReadGuardianHealth(Interlocked.Read(ref _uiProgressQpc)) ?? new GuardianStatus(false, 0, Interlocked.Read(ref _uiProgressQpc), false);
    }
    private void ActivateFromGuardian()
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            var window = MainWindow;
            if (window == null || Volatile.Read(ref _intentionalExit) != 0) return;
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }), DispatcherPriority.Background);
    }
    private Task<bool> PrepareGuardianRecoveryAsync() => Volatile.Read(ref _runtimeWindow)?.PrepareGuardianRecoveryAsync() ?? Task.FromResult(false);
    internal static Task AcknowledgeExitAsync(GuardianStopReason reason)
    {
        if (!(Current is App app)) return Task.CompletedTask;
        lock (app._intentGate)
        {
            if (app._stopIntent != null) return app._stopIntent;
            Interlocked.Exchange(ref app._intentionalExit, 1);
            return app._stopIntent = app._guardian == null ? Task.CompletedTask : app.RequireExitAcknowledgementAsync(reason);
        }
    }
    private async Task RequireExitAcknowledgementAsync(GuardianStopReason reason)
    {
        try
        {
            if (!await _guardian!.StopAsync(reason, CancellationToken.None).ConfigureAwait(false)) throw new InvalidOperationException("Application exit was not acknowledged.");
        }
        catch (Exception error) { WriteCrashLog("exit-intent", error); }
    }
    internal static GuardianClient? Guardian => (Current as App)?._guardian;
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try { Volatile.Read(ref _runtimeWindow)?.DrainSessionEnding(TimeSpan.FromSeconds(15)); }
        catch (Exception error) { WriteCrashLog("session-exit-intent", error); }
        base.OnSessionEnding(e);
    }
}
