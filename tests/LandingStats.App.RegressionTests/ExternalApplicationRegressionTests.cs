using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LandingStats.App.RegressionTests;

internal static class ExternalApplicationRegressionTests
{
    private static string Executable => Assembly.GetExecutingAssembly().Location;
    internal static void RunAll() => Task.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? detached = null;
        try
        {
            using var parent = OwnedProcess.Start(Executable, new[] { "--external-fixture", "parent", root }, allowExternalApplications: true);
            var deadline = Stopwatch.StartNew();
            while (!Ready(root, "detached") || !Ready(root, "owned"))
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("External process fixture did not start.");
                await Task.Delay(10);
            }
            detached = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(root, "detached"))));
            using var owned = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(root, "owned"))));
            await parent.TerminateAsync(TimeSpan.FromSeconds(5));
            if (detached.HasExited || !owned.HasExited) throw new Exception("Desktop application lifetime or owned descendant containment is incorrect.");
            foreach (var uri in new[] { "file:///C:/Windows/notepad.exe", "cmd:echo", "relative-path" })
            {
                try { ExternalUriLauncher.Open(uri); throw new Exception("An unsupported URI was launched."); }
                catch (ArgumentException) { }
            }
            var shellJob = "Local\\ls-external-shell-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(root, "shell-owner-job"), shellJob);
            using var shell = OwnedProcess.Start(Executable, new[] { "--external-fixture", "shell-check", root }, jobName: shellJob, allowExternalApplications: true);
            if (await Task.WhenAny(shell.Exit, Task.Delay(20000)) != shell.Exit) throw new TimeoutException("Shell admission fixture timed out.");
            if (await shell.Exit != 0) throw new Exception("Shell admission failed: " + File.ReadAllText(Path.Combine(root, "shell-error")));
            await CallerDeathRetiresBroker(root);
            ExitedBrokerWinsDeadlineRace(root);
        }
        finally
        {
            if (detached != null) { if (!detached.HasExited) detached.Kill(); detached.WaitForExit(); detached.Dispose(); }
            Directory.Delete(root, true);
        }
    }).GetAwaiter().GetResult();

    private static bool Ready(string root, string mode)
    {
        var path = Path.Combine(root, mode);
        try { return File.Exists(path) && int.TryParse(File.ReadAllText(path), out _); }
        catch (IOException) { return false; }
    }
    internal static int Fixture(string mode, string root)
    {
        if (mode == "shell-check")
        {
            try { ShellAdmission(root); return 0; }
            catch (Exception error) { File.WriteAllText(Path.Combine(root, "shell-error"), error.ToString()); return 1; }
        }
        if (mode == "shell-broker")
        {
            try { ExternalUriLauncher.ExecuteShellUri(root); return 0; }
            catch (Win32Exception error) { return error.NativeErrorCode; }
        }
        if (mode == "shell-handler")
        {
            GetStartupInfo(out var startup);
            // Windows may delegate protocol activation to Explorer, whose
            // environment need not match the broker's. Carry fixture identity
            // in the URI instead of relying on inherited environment state.
            var output = Uri.UnescapeDataString(root.Substring(root.LastIndexOf("&output=", StringComparison.Ordinal) + 8));
            var result = Path.Combine(output, "shell-result");
            File.WriteAllLines(result + ".tmp", new[] { root, startup.ShowWindow.ToString(),
                startup.Flags.ToString(), Process.GetCurrentProcess().Id.ToString(), Environment.CurrentDirectory });
            File.Move(result + ".tmp", result);
            using var finish = EventWaitHandle.OpenExisting("Local\\" + new Uri(root).Scheme + ".finish");
            finish.WaitOne(TimeSpan.FromSeconds(20));
            return 0;
        }
        if (mode == "shell-hang")
        {
            File.WriteAllText(Path.Combine(root, "hanging-broker"), Process.GetCurrentProcess().Id.ToString());
            Thread.Sleep(Timeout.Infinite); return 0;
        }
        if (mode == "broker-owner")
        {
            ExternalUriLauncher.LaunchBroker(Executable, "--external-fixture hanging-broker-with-client " + OwnedProcess.Quote(root), TimeSpan.FromSeconds(30));
            return 0;
        }
        if (mode == "hanging-broker-with-client")
        {
            using var client = Process.Start(new ProcessStartInfo(Executable, "--external-fixture detached " + OwnedProcess.Quote(root))
                { UseShellExecute = false, CreateNoWindow = true });
            File.WriteAllText(Path.Combine(root, "hanging-broker"), Process.GetCurrentProcess().Id.ToString());
            Thread.Sleep(Timeout.Infinite); return 0;
        }
        if (mode == "exit-success") return 0;
        if (mode == "exit-error") return 1155;
        if (mode == "parent")
        {
            using var child = OwnedProcess.Start(Executable, new[] { "--external-fixture", "middle", root }, allowExternalApplications: true);
            Thread.Sleep(Timeout.Infinite);
        }
        else if (mode == "middle")
        {
            ExternalUriLauncher.StartDetached(Executable, "--external-fixture detached " + OwnedProcess.Quote(root));
            using var child = Process.Start(new ProcessStartInfo(Executable, "--external-fixture owned " + OwnedProcess.Quote(root))
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            Thread.Sleep(Timeout.Infinite);
        }
        else if (mode == "detached" || mode == "owned")
        {
            File.WriteAllText(Path.Combine(root, mode), Process.GetCurrentProcess().Id.ToString());
            Thread.Sleep(Timeout.Infinite);
        }
        return 0;
    }

    private static void ShellAdmission(string root)
    {
        var scheme = "ls-external-fixture-" + Guid.NewGuid().ToString("N");
        var key = @"Software\Classes\" + scheme;
        var failedKey = key + "-missing";
        using var finish = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\" + scheme + ".finish");
        Process? handler = null;
        try
        {
            using (var association = Registry.CurrentUser.CreateSubKey(key))
            {
                association.SetValue("", "Landing Stats regression fixture"); association.SetValue("URL Protocol", "");
                using var command = association.CreateSubKey(@"shell\open\command");
                command.SetValue("", OwnedProcess.Quote(Executable) + " --external-fixture shell-handler \"%1\"");
            }
            var uri = scheme + "://open/path%20with%20spaces?first=a%26b&second=%22quoted%22&output=" + Uri.EscapeDataString(root);
            ExternalUriLauncher.LaunchBroker(Executable, "--external-fixture shell-broker " + OwnedProcess.Quote(uri), TimeSpan.FromSeconds(10));
            var output = Path.Combine(root, "shell-result");
            var until = Stopwatch.StartNew();
            while (!File.Exists(output))
            { if (until.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Shell handler did not start."); Thread.Sleep(10); }
            var result = File.ReadAllLines(output);
            handler = Process.GetProcessById(int.Parse(result[3]));
            if (result[0] != uri) throw new Exception("Shell URI quoting changed escaped characters or query separators.");
            if (result[1] != "1" || (uint.Parse(result[2]) & 1) == 0)
                throw new Exception("A newly launched shell application did not receive STARTF_USESHOWWINDOW with SW_SHOWNORMAL.");
            if (handler.HasExited) throw new Exception("Broker admission waited for or terminated its desktop client.");
            if (!string.Equals(Path.GetFullPath(result[4]), Path.GetFullPath(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("The shell client inherited the versioned runtime directory: " + result[4]);
            if (IsOwnedByJob(handler, File.ReadAllText(Path.Combine(root, "shell-owner-job"))))
                throw new Exception("The admitted shell client remains inside its Landing Stats caller's job.");

            using (var association = Registry.CurrentUser.CreateSubKey(failedKey))
            {
                association.SetValue("", "Missing regression handler"); association.SetValue("URL Protocol", "");
                using var command = association.CreateSubKey(@"shell\open\command");
                command.SetValue("", OwnedProcess.Quote(Path.Combine(root, "missing-handler.exe")) + " \"%1\"");
            }
            try
            {
                ExternalUriLauncher.LaunchBroker(Executable, "--external-fixture shell-broker " + OwnedProcess.Quote(scheme + "-missing://open"), TimeSpan.FromSeconds(10));
                throw new Exception("Missing shell handler was reported as successful admission.");
            }
            catch (Win32Exception error) when (error.NativeErrorCode != 0) { }
            try
            {
                ExternalUriLauncher.LaunchBroker(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"),
                    "--open-external-uri \"file:///C:/unsupported\"", TimeSpan.FromSeconds(10));
                throw new Exception("The production broker accepted an unsupported URI scheme.");
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 87) { }
            try
            {
                ExternalUriLauncher.LaunchBroker(Executable, "--external-fixture shell-hang " + OwnedProcess.Quote(root), TimeSpan.FromSeconds(2));
                throw new Exception("Unresponsive shell admission was reported as successful.");
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1460) { }
            var hanging = int.Parse(File.ReadAllText(Path.Combine(root, "hanging-broker")));
            Process? timedOut = null;
            try { timedOut = Process.GetProcessById(hanging); }
            catch (ArgumentException) { /* Windows has already reaped the retired broker. */ }
            using (timedOut)
                if (timedOut != null && !timedOut.WaitForExit(5000)) throw new Exception("The timed-out broker survived caller job closure.");
        }
        finally
        {
            finish.Set();
            if (handler != null) { if (!handler.HasExited) handler.Kill(); handler.WaitForExit(); handler.Dispose(); }
            Registry.CurrentUser.DeleteSubKeyTree(key, false); Registry.CurrentUser.DeleteSubKeyTree(failedKey, false);
        }
    }

    private static async Task CallerDeathRetiresBroker(string root)
    {
        var directory = Path.Combine(root, "caller-death"); Directory.CreateDirectory(directory);
        var callerJob = "Local\\ls-external-owner-" + Guid.NewGuid().ToString("N");
        using var caller = OwnedProcess.Start(Executable, new[] { "--external-fixture", "broker-owner", directory }, jobName: callerJob, allowExternalApplications: true);
        Process? broker = null, client = null;
        try
        {
            var until = Stopwatch.StartNew();
            while (!Ready(directory, "hanging-broker") || !Ready(directory, "detached"))
            { if (until.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Owned broker fixture did not start."); await Task.Delay(10); }
            broker = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(directory, "hanging-broker"))));
            client = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(directory, "detached"))));
            if (IsOwnedByJob(broker, callerJob) || IsOwnedByJob(client, callerJob))
                throw new Exception("The external broker or its client inherited the runtime caller's job.");
            await caller.TerminateAsync(TimeSpan.FromSeconds(5));
            if (!broker.WaitForExit(5000) || client.HasExited)
                throw new Exception("Caller death must retire its hung broker and preserve the admitted desktop client.");
        }
        finally
        {
            foreach (var process in new[] { broker, client })
                if (process != null) { if (!process.HasExited) process.Kill(); process.WaitForExit(); process.Dispose(); }
        }
    }

    private static bool IsOwnedByJob(Process process, string name)
    {
        // The test executor or Windows may own other jobs. Query the exact LS
        // job instead of mistaking membership in any job for LS ownership.
        using var job = OpenJobObject(4, false, name);
        if (job.IsInvalid || !IsProcessInJob(process.Handle, job, out var owned)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return owned;
    }

    private static void ExitedBrokerWinsDeadlineRace(string root)
    {
        foreach (var mode in new[] { "exit-success", "exit-error" })
        {
            using var process = Process.Start(new ProcessStartInfo(Executable, "--external-fixture " + mode + " " + OwnedProcess.Quote(root))
                { UseShellExecute = false, CreateNoWindow = true })!;
            using var handle = new SafeWaitHandle(process.Handle, false);
            if (!process.WaitForExit(5000)) throw new TimeoutException("Exit-race fixture did not finish.");
            // Model an expired initial wait followed by the actual native exit.
            // TerminateProcess on this handle would return ERROR_ACCESS_DENIED.
            try
            {
                ExternalUriLauncher.CompleteAdmission(handle, 258);
                if (mode == "exit-error") throw new Exception("A broker's actual failure was lost after the deadline raced its exit.");
            }
            catch (Win32Exception error) when (mode == "exit-error" && error.NativeErrorCode == 1155) { }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedBytes; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetStartupInfoW", CharSet = CharSet.Unicode)]
    private static extern void GetStartupInfo(out StartupInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, SafeWaitHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenJobObject(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
}
