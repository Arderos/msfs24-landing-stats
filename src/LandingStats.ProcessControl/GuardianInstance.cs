using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.App.Runtime;

// The resident bootstrap owns this mutex on its entry thread for its entire
// lifetime, including backoff and recovery. Secondary launches only activate it.
internal sealed class GuardianInstance : IDisposable
{
    internal const string Name = "Local\\MSFSLandingStats.Guardian";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private Task _pump = Task.CompletedTask;
    private GuardianInstance(Mutex mutex, EventWaitHandle activate) { _mutex = mutex; _activate = activate; }
    public static bool TryAcquire(out GuardianInstance? instance, string name = Name, bool activateExisting = true)
    {
        Mutex mutex; bool created;
        try { mutex = new Mutex(true, name, out created); }
        catch (UnauthorizedAccessException) { if (activateExisting) ActivateExisting(name); instance = null; return false; }
        if (!created)
        {
            mutex.Dispose(); if (activateExisting) ActivateExisting(name);
            instance = null; return false;
        }
        try { instance = new GuardianInstance(mutex, GuardianSignals.CreateEvent(name + ".Activate", EventResetMode.AutoReset, allowMediumIntegrity: true)); return true; }
        catch { mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }
    private static void ActivateExisting(string name)
    {
        AllowSetForegroundWindow(-1); var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(2))
        {
            try { using var signal = EventWaitHandle.OpenExisting(name + ".Activate", EventWaitHandleRights.Modify); signal.Set(); break; }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) { break; }
        }
    }
    public void StartActivation(Func<Task> activate, Func<bool>? ready = null)
    {
        _pump = Task.Run(async () =>
        {
            var pending = false;
            while (!_shutdown.IsCancellationRequested)
            {
                pending |= _activate.WaitOne(0);
                if (pending && (ready == null || ready())) { try { await activate().ConfigureAwait(false); pending = false; } catch { } }
                await Task.Delay(100, _shutdown.Token).ConfigureAwait(false);
            }
        });
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
    public static bool ApplicationAlreadyRunning()
    { if (!Mutex.TryOpenExisting("Local\\MSFSLandingStats.Application", MutexRights.Synchronize, out var existing)) return false; existing.Dispose(); return true; }
    public void Dispose()
    {
        _shutdown.Cancel();
        try { _pump.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _activate.Dispose(); _mutex.ReleaseMutex(); _mutex.Dispose(); _shutdown.Dispose();
    }
}
