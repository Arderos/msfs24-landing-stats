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
    private readonly EventWaitHandle _activate, _check, _accepted;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private Task _pump = Task.CompletedTask;
    private GuardianInstance(Mutex mutex, EventWaitHandle activate, EventWaitHandle check, EventWaitHandle accepted)
    { _mutex = mutex; _activate = activate; _check = check; _accepted = accepted; _accepted.Reset(); }
    public static bool TryAcquire(out GuardianInstance? instance, string name = Name, bool activateExisting = true)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, name, out var created);
            if (created)
            {
                try { GuardianSignals.SetActivationIntegrity(mutex); }
                catch { mutex.Dispose(); throw; }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Compatibility with an already running older elevated launcher.
            if (activateExisting) SignalLegacyActivation(name);
            instance = null; return false;
        }
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
            while (!owns)
            {
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(name + (activateExisting ? ".Activate" : ".Check"), EventWaitHandleRights.Modify);
                    using var accepted = EventWaitHandle.OpenExisting(name + ".Accepted", EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize);
                    if (activateExisting) AllowSetForegroundWindow(-1);
                    accepted.Reset(); signal.Set();
                    // A secondary launch remains alive until the current App
                    // accepts it, or until it owns the retired guardian's mutex.
                    try { owns = WaitHandle.WaitAny(new WaitHandle[] { accepted, mutex }) == 1; }
                    catch (AbandonedMutexException) { owns = true; }
                    if (!owns) { mutex.Dispose(); instance = null; return false; }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // New owners publish Accepted before Activate. An existing
                    // Activate without Accepted therefore belongs to an older launcher.
                    try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns && TryLegacyActivation(name, activateExisting))
                    { mutex.Dispose(); instance = null; return false; }
                    if (!owns) { try { owns = mutex.WaitOne(20); } catch (AbandonedMutexException) { owns = true; } }
                }
            }
            var acceptedSignal = GuardianSignals.CreateOrOpenAcknowledgementEvent(name + ".Accepted");
            EventWaitHandle? check = null, activate = null;
            try
            {
                check = GuardianSignals.CreateOrOpenActivationEvent(name + ".Check");
                activate = GuardianSignals.CreateOrOpenActivationEvent(name + ".Activate");
                instance = new GuardianInstance(mutex, activate, check, acceptedSignal); return true;
            }
            catch { activate?.Dispose(); check?.Dispose(); acceptedSignal.Dispose(); throw; }
        }
        catch { if (owns) mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }
    private static bool TryLegacyActivation(string name, bool activate)
    {
        try
        {
            using var accepted = EventWaitHandle.OpenExisting(name + ".Accepted", EventWaitHandleRights.Synchronize);
            return false;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(name + ".Activate", EventWaitHandleRights.Modify);
                if (activate) { AllowSetForegroundWindow(-1); signal.Set(); }
                return true;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }
    }
    private static void SignalLegacyActivation(string name)
    {
        try { using var signal = EventWaitHandle.OpenExisting(name + ".Activate", EventWaitHandleRights.Modify); AllowSetForegroundWindow(-1); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException error) { Debug.WriteLine(error); }
        catch (UnauthorizedAccessException error) { Debug.WriteLine(error); }
    }
    public void StartActivation(Func<Task<bool>> activate, Func<bool>? ready = null)
    {
        _pump = Task.Run(async () =>
        {
            var foreground = false; var background = false;
            while (!_shutdown.IsCancellationRequested)
            {
                foreground |= _activate.WaitOne(0); background |= _check.WaitOne(0);
                if ((foreground || background) && (ready == null || ready()))
                {
                    try
                    {
                        if ((!foreground || await activate().ConfigureAwait(false)) && (ready == null || ready()))
                        { foreground = background = false; _accepted.Set(); }
                    }
                    catch (Exception error) { Debug.WriteLine("Activation awaits a ready application: " + error); }
                }
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
        _activate.Dispose(); _check.Dispose(); _accepted.Dispose(); _mutex.ReleaseMutex(); _mutex.Dispose(); _shutdown.Dispose();
    }
}
