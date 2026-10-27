extern alias ApplicationHost;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LandingStats.App.Runtime;
using LandingStats.App.Updates;
using LandingStats.App.Settings;
using Protocol = ApplicationHost::LandingStats.UpdateProtocol.ReleaseUpdateProtocol;
using UpdaterProgram = LandingStats.App.Updater.Program;

namespace LandingStats.App.RegressionTests;

internal static class UpdateActivationRegressionTests
{
    public static void RunAll()
    {
        foreach (var background in new[] { false, true })
        {
            WireRoundTrip(background).GetAwaiter().GetResult();
            InvocationAndRestart(background);
        }
        BackgroundWindowDoesNotActivate(Array.Empty<string>(), update: true);
        BackgroundWindowDoesNotActivate(new[] { SimulatorAutoStartManager.LaunchArgument }, update: false);
        DuplicateBackgroundLaunchDoesNotActivate();
    }
    private static async Task WireRoundTrip(bool background)
    {
        var identity = GuardianDescriptor.Create(Guid.NewGuid(), 1);
        var request = new GuardianUpdateRequest(@"C:\fixture\updater.exe", @"C:\fixture\app.exe", "1.2.3",
            "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N"), Protocol.ChannelManifestName, 10,
            new string('a', 64), restartInBackground: background);
        using var bytes = new MemoryStream();
        using var sender = new GuardianWire(bytes, identity);
        await sender.SendAsync(new GuardianPacket(GuardianMessageKind.Update, 1, update: request), CancellationToken.None);
        var packet = bytes.ToArray();
        bytes.Position = 0;
        using var receiver = new GuardianWire(bytes, identity);
        var received = (await receiver.ReadAsync(CancellationToken.None)).Update!;
        Check(received.RestartInBackground == background && received.Target == request.Target && received.DeadlineQpc == request.DeadlineQpc,
            "Guardian changed the update activation policy or identity.");
        // An old client has no activation field. Its request remains readable.
        Array.Resize(ref packet, packet.Length - 1);
        Array.Copy(BitConverter.GetBytes(packet.Length - 4), packet, 4);
        using var legacyBytes = new MemoryStream(packet);
        using var legacy = new GuardianWire(legacyBytes, identity);
        Check(!(await legacy.ReadAsync(CancellationToken.None)).Update!.RestartInBackground, "Legacy update handoff changed its default.");
    }
    private static void InvocationAndRestart(bool background)
    {
        var target = Assembly.GetExecutingAssembly().Location;
        var ready = "Local\\MSFSLandingStatsUpdate-" + Guid.NewGuid().ToString("N");
        var commit = "Local\\MSFSLandingStatsUpdateCommit-" + Guid.NewGuid().ToString("N");
        var direct = new[] { "--apply", "--parent-pid", "1", "--target", target, "--version", "1.2.3", "--ready-event", ready };
        foreach (var arguments in new[] { direct, direct.Concat(new[] { "--manifest", Protocol.ChannelManifestName }).ToArray(),
            direct.Concat(new[] { "--manifest", Protocol.ChannelManifestName, "--commit-event", commit }).ToArray() })
        {
            Check(UpdaterProgram.ParseInvocation(arguments).RestartInBackground, "An older client without focus information activated its upgrade.");
            var command = arguments.Concat(new[] { background ? "--background" : "--foreground" }).ToArray();
            var invocation = UpdaterProgram.ParseInvocation(command);
            Check(invocation.RestartInBackground == background && invocation.TargetPath == target,
                "Updater lost the activation policy in a direct or guardian invocation.");
            foreach (var cleanup in new[] { "", "--finish-update 1 fixture" })
            {
                var start = UpdaterProgram.CreateRestartStartInfo(invocation.TargetPath, cleanup, invocation.RestartInBackground);
                Check((start.EnvironmentVariables[Protocol.BackgroundRestartEnvironmentVariable] == "1") == background && start.Arguments == cleanup,
                    "Successful update or rollback lost its activation policy or cleanup arguments.");
            }
        }
        try { UpdaterProgram.ParseInvocation(direct.Concat(new[] { "--background", "--background" }).ToArray()); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Duplicate updater switch was accepted.");
    }
    private static void BackgroundWindowDoesNotActivate(string[] arguments, bool update)
    {
        var variable = Protocol.BackgroundRestartEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        var shutdown = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new Window { Width = 180, Height = 80, ShowInTaskbar = false, Title = "Update activation regression" };
        try
        {
            Environment.SetEnvironmentVariable(variable, update ? "1" : null);
            App.ConfigureStartupWindow(window, arguments);
            Check(!window.ShowActivated && Environment.GetEnvironmentVariable(variable) == null, "Background startup was not consumed before Show.");
            var activations = 0; window.Activated += (_, _) => activations++;
            var wasForeground = ReleaseUpdater.IsCurrentProcessForeground();
            window.Show();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.ApplicationIdle);
            Dispatcher.PushFrame(frame);
            Check(!window.IsActive && activations == 0, "Background update or simulator autostart activated its WPF window.");
            Check(wasForeground || !ReleaseUpdater.IsCurrentProcessForeground(), "Background update took the foreground from another process.");
            var ordinary = new Window();
            App.ConfigureStartupWindow(ordinary, Array.Empty<string>());
            Check(ordinary.ShowActivated, "One background update disabled normal activation on later launches.");
            ordinary.Close();
        }
        finally { window.Close(); Application.Current.ShutdownMode = shutdown; Environment.SetEnvironmentVariable(variable, previous); }
    }
    private static void DuplicateBackgroundLaunchDoesNotActivate()
    {
        var name = "Local\\LandingStats.UpdateActivationTest-" + Guid.NewGuid().ToString("N");
        Check(GuardianInstance.TryAcquire(out var first, name), "Could not own the unique test instance.");
        using (first!)
        using (var activated = EventWaitHandle.OpenExisting(name + ".Activate"))
        {
            Check(!GuardianInstance.TryAcquire(out var duplicate, name, activateExisting: false) && duplicate == null,
                "A second background launcher acquired the existing instance.");
            Check(!activated.WaitOne(0), "A background relaunch activated an existing instance.");
            Check(!GuardianInstance.TryAcquire(out duplicate, name) && duplicate == null && activated.WaitOne(0),
                "An explicit user launch no longer activates the existing instance.");
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
