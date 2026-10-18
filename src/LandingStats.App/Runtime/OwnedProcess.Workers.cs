using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed partial class OwnedProcess
{
    internal static string JobName(WorkerIdentity worker) => "Global\\MSFS.LandingStats." + worker.AppSessionId.ToString("N") + "." + worker.InstanceId.ToString("N");
    // Used only after exclusive application ownership has been established.
    // Opening the exact named job avoids PID reuse and confirms all descendants,
    // including a former HTTP attempt, have exited before its claim is released.
    internal static async Task ConfirmFormerWorkerExitedAsync(WorkerIdentity worker, TimeSpan deadline, CancellationToken cancellationToken)
    {
        using var job = Native.OpenJobObject(0x0004 | 0x0008, false, JobName(worker)); // QUERY | TERMINATE
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2) return; // A destroyed job has no remaining processes.
            throw new Win32Exception(error, "Cannot inspect the former worker job.");
        }
        if (!Native.TerminateJobObject(job, 0x4c530002)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot stop the former worker job.");
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Native.QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf(typeof(Native.JobAccounting)), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot confirm former worker exit.");
            if (accounting.ActiveProcesses == 0) return;
            if (watch.Elapsed >= deadline) throw new TimeoutException("Former worker tree still owns its operation.");
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

}
