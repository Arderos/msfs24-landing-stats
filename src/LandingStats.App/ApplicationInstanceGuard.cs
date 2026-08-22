using System;
using System.Threading;

namespace LandingStats.App;

internal sealed class ApplicationInstanceGuard : IDisposable
{
    internal const string MutexName = "Local\\MSFSLandingStats.Application";

    private Mutex? _mutex;
    private bool _ownsMutex;

    private ApplicationInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static bool TryAcquire(out ApplicationInstanceGuard? guard)
    {
        return TryAcquire(MutexName, out guard);
    }

    internal static bool TryAcquire(string mutexName, out ApplicationInstanceGuard? guard)
    {
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            throw new ArgumentException("A single-instance mutex name is required.", nameof(mutexName));
        }

        var mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new ApplicationInstanceGuard(mutex);
        return true;
    }

    public void Dispose()
    {
        var mutex = _mutex;
        _mutex = null;
        if (mutex == null)
        {
            return;
        }

        try
        {
            if (_ownsMutex)
            {
                mutex.ReleaseMutex();
                _ownsMutex = false;
            }
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
