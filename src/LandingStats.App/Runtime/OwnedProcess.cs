using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace LandingStats.App.Runtime;

/// <summary>A process is assigned to its kill-on-close job by CreateProcess itself, before any child code runs.</summary>
internal sealed partial class OwnedProcess : IDisposable
{
    private readonly NativeHandle _job;
    private readonly NativeHandle _process;
    private readonly object _lifetimeGate = new object();
    private Task? _termination;
    private int _disposed;
    private OwnedProcess(NativeHandle job, NativeHandle process, int id)
    { _job = job; _process = process; Id = id; Exit = ObserveExitAsync(); }
    public int Id { get; }
    public Task<int> Exit { get; }

    public static OwnedProcess Start(string executable, IReadOnlyList<string> arguments, string? directory = null, Action<int>? beforeResume = null, string? jobName = null,
        IReadOnlyDictionary<string, string>? environment = null, bool hideWindow = true)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Worker executable is missing.", executable);
        var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
        if (command.Length >= 32767) throw new ArgumentException("Worker command line exceeds the Windows limit.");
        var job = Native.CreateJobObject(IntPtr.Zero, jobName);
        var jobError = Marshal.GetLastWin32Error();
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the worker job."); }
        if (jobName != null && jobError == 183) { job.Dispose(); throw new IOException("This exact worker identity still owns a job."); }
        NativeHandle? process = null;
        IntPtr attributeList = IntPtr.Zero, jobList = IntPtr.Zero, environmentBlock = IntPtr.Zero;
        var initialized = false;
        var information = new Native.ProcessInformation();
        try
        {
            var limits = new Native.JobLimits { Basic = new Native.BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(Native.JobLimits))))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot configure the worker job.");
            IntPtr size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot size worker startup attributes.");
            attributeList = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot initialize worker startup attributes.");
            initialized = true;
            jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            // PROC_THREAD_ATTRIBUTE_JOB_LIST (Windows 10+). No create/assign orphan window,
            // including a parent crash while the new primary thread is still suspended.
            if (!Native.UpdateProcThreadAttribute(attributeList, 0, new IntPtr(0x0002000d), jobList, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot bind the worker to its owner job.");
            var startup = new Native.StartupInfoEx { Attributes = attributeList };
            startup.Info.Size = (uint)Marshal.SizeOf(typeof(Native.StartupInfoEx));
            startup.Info.Flags = 1; startup.Info.ShowWindow = (ushort)(hideWindow ? 0 : 1);
            if (environment != null) environmentBlock = CreateEnvironment(environment);
            if (!Native.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                    0x08000000 | 0x00080000 | 0x00000004 | 0x00000400, environmentBlock, directory ?? Path.GetDirectoryName(executable), ref startup, out information))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start the owned worker.");
            process = new NativeHandle(information.Process);
            beforeResume?.Invoke(checked((int)information.ProcessId));
            if (Native.ResumeThread(information.Thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resume the owned worker.");
            return new OwnedProcess(job, process, checked((int)information.ProcessId));
        }
        catch
        {
            // Closing the job also terminates a child created successfully but not resumed.
            job.Dispose(); process?.Dispose(); throw;
        }
        finally
        {
            if (information.Thread != IntPtr.Zero) Native.CloseHandle(information.Thread);
            if (initialized) Native.DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private static IntPtr CreateEnvironment(IReadOnlyDictionary<string, string> overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry value in Environment.GetEnvironmentVariables()) values[(string)value.Key] = (string)value.Value;
        foreach (var value in overrides)
        {
            if (string.IsNullOrEmpty(value.Key) || value.Key.IndexOfAny(new[] { '=', '\0' }) >= 0 || value.Value == null || value.Value.IndexOf('\0') >= 0)
                throw new ArgumentException("Invalid child process environment.");
            values[value.Key] = value.Value;
        }
        return Marshal.StringToHGlobalUni(string.Concat(values.Select(pair => pair.Key + "=" + pair.Value + "\0")) + "\0");
    }

    public Task TerminateAsync(TimeSpan exitDeadline)
    {
        if (exitDeadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(exitDeadline));
        lock (_lifetimeGate)
        {
            // Concurrent stop/timeout paths share one confirmation operation.
            // Dispose cannot close the job while that operation still queries it.
            if (_termination != null && (!_termination.IsCompleted || _termination.Status == TaskStatus.RanToCompletion)) return _termination;
            if (_disposed != 0) throw new ObjectDisposedException(nameof(OwnedProcess));
            return _termination = TerminateOwnedAsync(exitDeadline);
        }
    }

    // Only the verified updater handoff uses this: after its ready/commit
    // handshake the helper must outlive the resident launcher it replaces.
    internal void Detach(Action commit)
    {
        lock (_lifetimeGate)
        {
            if (_disposed != 0 || _termination != null) throw new InvalidOperationException("A retiring process cannot be handed off.");
            var limits = new Native.JobLimits();
            if (!Native.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf(typeof(Native.JobLimits))))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot hand off the updater job.");
            try { commit(); }
            catch
            {
                limits.Basic.Flags = 0x2000;
                Native.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf(typeof(Native.JobLimits)));
                throw;
            }
            _disposed = 1; _job.Dispose();
            _ = Exit.ContinueWith(task => { _ = task.Exception; _process.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    private async Task TerminateOwnedAsync(TimeSpan exitDeadline)
    {
        if (!Native.TerminateJobObject(_job, 0x4c530001))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot terminate the owned worker job.");
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (!Native.QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf(typeof(Native.JobAccounting)), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot confirm worker job termination.");
            if (Exit.IsCompleted && accounting.ActiveProcesses == 0) break;
            if (deadline.Elapsed >= exitDeadline) throw new TimeoutException("Worker tree exit is not confirmed; its replacement must not start.");
            await Task.Delay(20).ConfigureAwait(false);
        }
        await Exit.ConfigureAwait(false);
    }
    private async Task<int> ObserveExitAsync()
    {
        while (true)
        {
            var wait = Native.WaitForSingleObject(_process, 0);
            if (wait == 0)
            {
                if (!Native.GetExitCodeProcess(_process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return unchecked((int)code);
            }
            if (wait != 258) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot observe the owned worker.");
            await Task.Delay(50).ConfigureAwait(false);
        }
    }
    public void Dispose()
    {
        Task? termination; bool defer;
        lock (_lifetimeGate)
        {
            if (_disposed != 0) return;
            _disposed = 1; termination = _termination; defer = termination != null && !termination.IsCompleted;
            if (!defer) _job.Dispose();
        }
        if (defer)
            _ = termination!.ContinueWith(_ => _job.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // Observation retains its handle until the OS has signalled exit.
        _ = Exit.ContinueWith(_ => _process.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    internal static string Quote(string value)
    {
        if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument.");
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else { result.Append('\\', slashes); result.Append(c); }
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeHandle() : base(true) { }
        public NativeHandle(IntPtr handle) : base(true) { SetHandle(handle); }
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
        { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
        { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobLimits
        { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobAccounting
        { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        {
            public uint Size; public string? Reserved, Desktop, Title;
            public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
            public ushort ShowWindow, ReservedBytes; public IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern NativeHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern NativeHandle OpenJobObject(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(NativeHandle job, int informationClass, ref JobLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(string executable, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(NativeHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(NativeHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(NativeHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(NativeHandle job, int informationClass, out JobAccounting accounting, uint length, IntPtr returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
