using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LandingStats.App;

// Browsers and mail clients belong to the desktop session. The admission broker
// has its own caller-owned job; only its desktop clients outlive that caller.
internal static class ExternalUriLauncher
{
    internal static void Open(string value)
    {
        var uri = Validate(value);
        var broker = Path.Combine(Path.GetDirectoryName(typeof(ExternalUriLauncher).Assembly.Location)!, "LandingStats.WorkerHost.exe");
        LaunchBroker(broker, "--open-external-uri " + Quote(uri.AbsoluteUri), TimeSpan.FromSeconds(30));
    }

    private static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeMailto))
            throw new ArgumentException("Only browser and email links are supported.", nameof(value));
        return uri;
    }

    internal static int RunBroker(string value)
    {
        try { ExecuteShellUri(Validate(value).AbsoluteUri); return 0; }
        catch (Win32Exception error) { return error.NativeErrorCode == 0 ? 31 : error.NativeErrorCode; }
        catch (ArgumentException) { return 87; }
    }

    internal static void ExecuteShellUri(string value)
    {
        // ShellExecute must run outside the runtime job. NOASYNC waits for shell
        // admission (including DDE), never for the browser/mail client's exit.
        // NO_UI returns association errors to our caller instead of a hidden
        // system dialog. ShowNormal is independent of the broker's startup.
        var info = new ShellExecuteInfo { Size = Marshal.SizeOf(typeof(ShellExecuteInfo)), Mask = 0x100 | 0x400,
            Verb = "open", File = value, Directory = Environment.SystemDirectory, Show = 1 };
        if (!ShellExecuteEx(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void LaunchBroker(string executable, string arguments, TimeSpan admissionDeadline)
    {
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot own the external application broker.");
        // The caller alone owns this handle, so an abrupt caller exit retires
        // even a broker blocked in a shell extension. Shell-created clients
        // automatically leave this job and remain owned by the desktop.
        var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 | 0x1000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobLimits))))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = CreateDetached(executable, arguments, job);
        var wait = WaitForSingleObject(process, checked((uint)admissionDeadline.TotalMilliseconds));
        CompleteAdmission(process, wait);
    }

    internal static void CompleteAdmission(SafeWaitHandle process, uint wait)
    {
        if (wait == 258)
        {
            // A process can exit immediately after the deadline observation.
            // Read that result if available; job closure retires a still-live
            // broker without TerminateProcess's exited-process access error.
            wait = WaitForSingleObject(process, 0);
            if (wait == 258) throw new Win32Exception(1460, "The desktop did not acknowledge opening the external application in time.");
        }
        if (wait != 0 || !GetExitCodeProcess(process, out var result)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (result != 0) throw new Win32Exception(unchecked((int)result), "Cannot open the external application.");
    }

    internal static void StartDetached(string executable, string arguments)
    {
        using var process = CreateDetached(executable, arguments);
    }

    private static SafeWaitHandle CreateDetached(string executable, string arguments, SafeWaitHandle? job = null)
    {
        var startup = new StartupInfoEx { Info = new StartupInfo { Size = (uint)Marshal.SizeOf(typeof(StartupInfoEx)), Flags = 1, ShowWindow = 1 } };
        var command = new StringBuilder(Quote(executable) + " " + arguments);
        IntPtr list = IntPtr.Zero, jobList = IntPtr.Zero; var initialized = false;
        try
        {
            if (job != null)
            {
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                list = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
                // Assign at creation: even caller death before CreateProcess
                // returns cannot leave an unowned helper behind.
                if (!UpdateProcThreadAttribute(list, 0, new IntPtr(0x0002000d), jobList, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                startup.Attributes = list;
            }
            // CREATE_BREAKAWAY_FROM_JOB is opt-in, never used for LS workers.
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                0x01000000 | 0x08000000 | 0x00080000, IntPtr.Zero, Environment.SystemDirectory, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the external application.");
            CloseHandle(process.Thread);
            return new SafeWaitHandle(process.Process, true);
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(list);
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            GC.KeepAlive(job);
        }
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(new[] { '\0', '"' }) >= 0) throw new ArgumentException("Invalid external application argument.");
        var trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size; public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedBytes; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size; public uint Mask; public IntPtr Window; public string? Verb, File, Parameters, Directory;
        public int Show; public IntPtr Instance, IdList; public string? Class; public IntPtr ClassKey;
        public uint HotKey; public IntPtr Icon, Process;
    }
    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeWaitHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeWaitHandle job, int informationClass, ref JobLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string executable, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
