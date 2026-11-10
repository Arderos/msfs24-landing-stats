using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LandingStats.App.RegressionTests;

// Own the simulated application's windows on its own dispatcher and process.
// Readiness and splash closure are acknowledged after their actual UI actions;
// no timing delay is used to infer that a native window has appeared or closed.
internal sealed class BackgroundWindowRegressionFixture : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _commands;
    private readonly Task<string> _errors;
    public IntPtr Window { get; }

    public BackgroundWindowRegressionFixture()
    {
        _process = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--background-window-fixture")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        }) ?? throw new InvalidOperationException("Could not start the background-window fixture.");
        // Process.StandardInput inherits the console encoding on .NET Framework,
        // including a UTF-8 preamble. The control channel has its own wire encoding.
        _commands = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        _errors = _process.StandardError.ReadToEndAsync();
        try
        {
            var ready = ReadReply().Split(' ');
            if (ready.Length != 2 || ready[0] != "READY") throw new InvalidDataException("Unexpected window-fixture readiness.");
            Window = new IntPtr(long.Parse(ready[1], CultureInfo.InvariantCulture));
        }
        catch { Dispose(); throw; }
    }

    public void CloseSplash()
    {
        _commands.WriteLine("close-splash");
        if (ReadReply() != "CLOSED") throw new InvalidDataException("Window fixture did not acknowledge splash closure.");
    }

    private string ReadReply()
    {
        var reply = _process.StandardOutput.ReadLineAsync();
        if (!reply.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Window fixture did not answer.");
        return reply.Result ?? throw new InvalidOperationException("Window fixture ended before its reply: " +
            (_errors.IsCompleted ? _errors.Result : "stderr pending"));
    }

    public void Dispose()
    {
        try
        {
            _commands.Dispose();
            if (!_process.HasExited)
            {
                if (!_process.WaitForExit(10000)) { _process.Kill(); _process.WaitForExit(); }
            }
        }
        finally { _process.Dispose(); }
    }

    public static int Run()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var commands = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var simulator = new Window { Width = 180, Height = 80, Left = -10000, Top = -10000, ShowInTaskbar = false };
        var splash = new Window { Width = 180, Height = 80, Left = -10000, Top = -10000, ShowInTaskbar = false };
        simulator.Show(); splash.Show();
        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            Console.WriteLine("READY " + new WindowInteropHelper(simulator).Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
            Console.Out.Flush();
        }), DispatcherPriority.ApplicationIdle);
        _ = Task.Run(() =>
        {
            try
            {
                string? command;
                while ((command = commands.ReadLine()) != null)
                {
                    if (command != "close-splash") throw new InvalidDataException("Unknown window-fixture command.");
                    application.Dispatcher.Invoke(() => splash.Close());
                    Console.WriteLine("CLOSED"); Console.Out.Flush();
                }
                application.Dispatcher.Invoke(() => { splash.Close(); simulator.Close(); application.Shutdown(); });
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                application.Dispatcher.BeginInvoke(new Action(() => application.Shutdown(1)));
            }
        });
        return application.Run();
    }
}
