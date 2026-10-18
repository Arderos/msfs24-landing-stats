using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LandingStats.App.Runtime;

/// <summary>Cancelable per-owner waits without changing the system-wide timer period or spinning.</summary>
internal sealed class OwnerCadence : IDisposable
{
    private readonly WaitHandle _timer;
    private readonly WaitHandle[] _wait;
    public OwnerCadence(CancellationToken cancellationToken)
        : this(cancellationToken, flags => CreateWaitableTimerExW(IntPtr.Zero, null, flags, 0x00100002), Marshal.GetLastWin32Error) { }
    internal OwnerCadence(CancellationToken cancellationToken, Func<uint, SafeWaitHandle> create, Func<int> lastError)
    {
        var handle = create(2); // SYNCHRONIZE | TIMER_MODIFY_STATE
        HighResolution = !handle.IsInvalid;
        if (handle.IsInvalid)
        {
            var error = lastError(); handle.Dispose();
            if (error != 87) throw new Win32Exception(error); // Older Windows can reject the high-resolution flag.
            handle = create(0);
            if (handle.IsInvalid) { error = lastError(); handle.Dispose(); throw new Win32Exception(error); }
        }
        _timer = new TimerWaitHandle(handle); _wait = new[] { cancellationToken.WaitHandle, _timer };
    }
    public bool HighResolution { get; }
    public void Wait(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        var relative = -Math.Max(1, duration.Ticks);
        if (!SetWaitableTimer(_timer.SafeWaitHandle, ref relative, 0, IntPtr.Zero, IntPtr.Zero, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        WaitHandle.WaitAny(_wait);
    }
    public void Dispose() => _timer.Dispose();
    private sealed class TimerWaitHandle : WaitHandle { public TimerWaitHandle(SafeWaitHandle handle) { SafeWaitHandle = handle; } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle handle, ref long dueTime, int period, IntPtr callback, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
