extern alias ApplicationHost;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LandingStats.App;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;
using Microsoft.Win32;
using Protocol = ApplicationHost::LandingStats.UpdateProtocol.ReleaseUpdateProtocol;

namespace LandingStats.App.RegressionTests;

internal static class LandingFileActivationRegressionTests
{
    internal static void RunAll()
    {
        RegistrationTracksPortableLocation();
        ArgumentsAndExplicitActivation();
        AssociationFailureHandoff();
        RequestsSurviveStartupAndRecovery().GetAwaiter().GetResult();
        FailedHandlerRetainsRequest().GetAwaiter().GetResult();
        CancelledHandlerKeepsReceiverAlive().GetAwaiter().GetResult();
        ApplicationShutdownStopsActivationIntake();
        SignalSurvivesReceiverReplacement().GetAwaiter().GetResult();
        ForegroundGrantTargetsReceiver().GetAwaiter().GetResult();
        SecondaryLaunchesAndInvalidRequests();
    }

    private static void ApplicationShutdownStopsActivationIntake()
    {
        using var fixture = new InboxFixture();
        Directory.CreateDirectory(fixture.Root);
        foreach (var mode in new[] { "activation-stop", "activation-acknowledge" })
        {
            using var process = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--app-guardian-probe " + mode + " " + OwnedProcess.Quote(fixture.Root))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
            if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(); throw new TimeoutException("Activation shutdown fixture timed out."); }
            Check(process.ExitCode == 0, "Intentional shutdown reported a cancelled activation: " +
                (File.Exists(Path.Combine(fixture.Root, "failure")) ? File.ReadAllText(Path.Combine(fixture.Root, "failure")) : process.ExitCode.ToString()));
        }
    }

    internal static int ApplicationShutdownFixture(App app, string directory, bool acknowledgeExit)
    {
        try
        {
            Task.Run(async () =>
            {
                var root = Path.Combine(directory, acknowledgeExit ? "acknowledge" : "window-close");
                var signal = "Local\\ls-activation-close-" + Guid.NewGuid().ToString("N");
                using (var requests = new LandingFileActivation(root, signal)) requests.Submit(new[] { Path.Combine(root, "pending.lslanding") });
                var admitted = Completion<bool>(); var pending = Completion<bool>(); var reported = 0;
                Check(app.StartFileActivationIntake(_ => { admitted.TrySetResult(true); return pending.Task; },
                    _ => Interlocked.Increment(ref reported), root, signal), "Live App did not admit activation.");
                var receiver = (LandingFileActivation)typeof(App).GetField("_landingFileActivation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
                await Within(admitted.Task);
                if (acknowledgeExit) await App.AcknowledgeExitAsync(GuardianStopReason.UserExit);
                else App.StopFileActivationIntake();
                pending.TrySetCanceled();
                await Within(receiver.Completion);
                Check(reported == 0 && Directory.GetFiles(root, "*.request").Length == 1,
                    "Expected shutdown cancellation produced a crash report or acknowledged an unfinished durable request.");
            }).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure"), error.ToString()); return 1; }
    }

    // Called by the regression executable before creating WPF state. This is a
    // second launcher process with the same durable submission and mutex order.
    internal static int SubmitFixture(string[] args)
    {
        using var inbox = new LandingFileActivation(args[1], args[2]);
        inbox.Submit(LandingFileActivation.FileArguments(args.Skip(4)));
        if (!GuardianInstance.TryAcquire(out var owner, args[3], activateExisting: false)) return 0;
        owner!.Dispose(); return 1;
    }

    private static void RegistrationTracksPortableLocation()
    {
        var keyPath = @"Software\MSFSLandingStats.RegressionTests\Sharing-" + Guid.NewGuid().ToString("N");
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, true) ?? throw new IOException("Cannot create isolated registry fixture.");
        try
        {
            var first = @"C:\Flight Tools\Посадки\MSFS-Landing-Stats.exe";
            var second = @"D:\Moved flight tools\MSFS-Landing-Stats.exe";
            using (var unrelated = key.CreateSubKey(@".lslanding\UserChoice")) unrelated!.SetValue("ProgId", "Other.Application");
            Check(LandingFileAssociation.Register(key, first), "Initial registration made no change.");
            Check(!LandingFileAssociation.Register(key, first), "Unchanged registration keeps rewriting registry values.");
            Check(LandingFileAssociation.Register(key, second), "Moved executable did not refresh the association.");
            using var command = key.OpenSubKey(LandingFileAssociation.ProgId + @"\shell\open\command");
            using var icon = key.OpenSubKey(LandingFileAssociation.ProgId + @"\DefaultIcon");
            using var extension = key.OpenSubKey(LandingFileAssociation.Extension);
            using var untouched = key.OpenSubKey(@".lslanding\UserChoice");
            Check((string?)command!.GetValue("") == "\"" + second + "\" \"%1\"", "Portable file-open command is not fully quoted/current.");
            Check((string?)icon!.GetValue("") == "\"" + second + "\",0", "File icon does not follow the portable executable.");
            Check((string?)extension!.GetValue("") == LandingFileAssociation.ProgId, "Extension points to a different ProgID.");
            Check((string?)untouched!.GetValue("ProgId") == "Other.Application", "Registration changed an unrelated user choice.");
        }
        finally { key.Close(); Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
    }

    private static void ArgumentsAndExplicitActivation()
    {
        var relative = "flight with spaces-Посадка.lslanding";
        var parsed = LandingFileActivation.FileArguments(new[] { SimulatorAutoStartManager.LaunchArgument, relative, relative, "SECOND.LSLANDING", "--other" });
        Check(parsed.Length == 2 && parsed[0] == Path.GetFullPath(relative) && parsed[1] == Path.GetFullPath("SECOND.LSLANDING"),
            "File routing lost non-existent, Unicode, relative or multiple file arguments.");
        Check(LandingFileActivation.WithoutFileArguments(new[] { relative, "--other", SimulatorAutoStartManager.LaunchArgument })
            .SequenceEqual(new[] { "--other", SimulatorAutoStartManager.LaunchArgument }), "File arguments leak into guardian restart arguments.");
        var variable = Protocol.BackgroundRestartEnvironmentVariable; var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");
            var explicitOpen = new Window();
            App.ConfigureStartupWindow(explicitOpen, new[] { SimulatorAutoStartManager.LaunchArgument, relative });
            Check(explicitOpen.ShowActivated, "Explicit file activation inherited a background-only startup policy.");
            Check(Environment.GetEnvironmentVariable(variable) == null, "One-shot background environment was not consumed.");
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    private static async Task RequestsSurviveStartupAndRecovery()
    {
        using var fixture = new InboxFixture();
        var expected = new[] { Path.Combine(fixture.Root, "one.lslanding"), Path.Combine(fixture.Root, "two with spaces-Ж.LSLANDING") };
        fixture.Submit(expected);
        var request = Directory.GetFiles(fixture.Root, "*.request").Single();
        var accepted = Completion<string[]>(); var finish = Completion<bool>();
        using (var first = fixture.Receiver())
        {
            first.Start(paths => { accepted.TrySetResult(paths); return finish.Task; }, error => accepted.TrySetException(error));
            Check((await Within(accepted.Task)).SequenceEqual(expected), "Pending cold-start request changed its paths.");
            Check(File.Exists(request), "Queued UI work was acknowledged before import completion.");
            first.Dispose(); finish.SetCanceled();
            await Within(first.Completion);
            Check(File.Exists(request), "Shutdown lost a pending import.");
        }
        using (var recovered = fixture.Receiver())
        using (var deleted = new DeletionWitness(fixture.Root, Path.GetFileName(request)))
        {
            var calls = 0;
            recovered.Start(paths => { Check(paths.SequenceEqual(expected), "Recovery changed the pending request."); Interlocked.Increment(ref calls); return Task.CompletedTask; }, deleted.Fail);
            await Within(deleted.Deleted);
            Check(calls == 1 && !File.Exists(request), "Recovered import was not acknowledged exactly once by the receiver.");
            recovered.Dispose(); await Within(recovered.Completion);
        }
    }

    private static async Task FailedHandlerRetainsRequest()
    {
        using var fixture = new InboxFixture();
        fixture.Submit(new[] { Path.Combine(fixture.Root, "retained.lslanding") });
        var request = Directory.GetFiles(fixture.Root, "*.request").Single();
        using (var failed = fixture.Receiver())
        {
            var reported = Completion<Exception>();
            failed.Start(_ => throw new IOException("Unfinished import fixture."), error => reported.TrySetResult(error));
            Check(await Within(reported.Task) is IOException, "An unexpected import failure was suppressed.");
            failed.Dispose(); await Within(failed.Completion);
        }
        Check(File.Exists(request), "An unsuccessful import was acknowledged.");
        using var recovered = fixture.Receiver();
        using var deleted = new DeletionWitness(fixture.Root, Path.GetFileName(request));
        recovered.Start(_ => Task.CompletedTask, deleted.Fail);
        await Within(deleted.Deleted);
        recovered.Dispose(); await Within(recovered.Completion);
    }

    private static async Task SignalSurvivesReceiverReplacement()
    {
        using var fixture = new InboxFixture();
        using var first = fixture.Receiver();
        first.Start(_ => Task.CompletedTask, error => throw new InvalidOperationException("Unexpected first receiver failure.", error));
        // A submitting process may retain this handle beyond the old App's
        // lifetime, so the named kernel object survives the receiver restart.
        using var producer = EventWaitHandle.OpenExisting(fixture.Signal, System.Security.AccessControl.EventWaitHandleRights.Modify);
        first.Dispose(); await Within(first.Completion);
        var expected = Path.Combine(fixture.Root, "after replacement.lslanding");
        fixture.Submit(new[] { expected });
        var request = Directory.GetFiles(fixture.Root, "*.request").Single();
        using var deleted = new DeletionWitness(fixture.Root, Path.GetFileName(request));
        using var replacement = fixture.Receiver();
        replacement.Start(paths =>
        {
            Check(paths.SequenceEqual(new[] { expected }), "A surviving producer signal changed the recovered request.");
            return Task.CompletedTask;
        }, deleted.Fail);
        await Within(deleted.Deleted);
        replacement.Dispose(); await Within(replacement.Completion);
    }

    private static void AssociationFailureHandoff()
    {
        var variable = LandingFileAssociation.RegistrationErrorEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "Registry access denied");
            Check(LandingFileAssociation.TakeStartupError() == "Registry access denied" &&
                LandingFileAssociation.TakeStartupError() == null,
                "The launcher association failure reaches its owning App once without leaking into child workers.");
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    private static async Task CancelledHandlerKeepsReceiverAlive()
    {
        using var fixture = new InboxFixture();
        fixture.Submit(new[] { Path.Combine(fixture.Root, "cancelled.lslanding") });
        var retained = Directory.GetFiles(fixture.Root, "*.request").Single();
        var reported = Completion<Exception>();
        var secondHandled = Completion<bool>();
        var calls = 0;
        using var receiver = fixture.Receiver();
        receiver.Start(paths =>
        {
            if (Interlocked.Increment(ref calls) == 1) return Task.FromCanceled(new CancellationToken(true));
            if (paths.Any(path => Path.GetFileName(path) == "later.lslanding")) secondHandled.TrySetResult(true);
            return Task.CompletedTask;
        }, error => reported.TrySetResult(error));
        Check(await Within(reported.Task) is OperationCanceledException && File.Exists(retained) && !receiver.Completion.IsCompleted,
            "An operation cancellation is reported and retained without retiring the live receiver.");
        using var deleted = new DeletionWitness(fixture.Root, Path.GetFileName(retained));
        fixture.Submit(new[] { Path.Combine(fixture.Root, "later.lslanding") });
        await Within(secondHandled.Task); await Within(deleted.Deleted);
        Check(calls == 3, "A later activation must drain both durable requests without a timed retry loop.");
        receiver.Dispose(); await Within(receiver.Completion);
    }

    private static async Task ForegroundGrantTargetsReceiver()
    {
        using var fixture = new InboxFixture();
        using var sender = fixture.Receiver();
        var granted = 0;
        Check(!sender.AllowReceiverForeground(processId => granted = processId) && granted == 0,
            "A cold launch granted foreground permission without a receiver.");
        var received = Completion<string[]>();
        using var receiver = fixture.Receiver();
        receiver.Start(paths => { received.TrySetResult(paths); return Task.CompletedTask; }, error => received.TrySetException(error));
        Check(sender.AllowReceiverForeground(processId => granted = processId) && granted == Process.GetCurrentProcess().Id,
            "Explicit file activation did not target the live receiver PID.");
        using (var writer = new BinaryWriter(File.Create(Path.Combine(fixture.Root, ".receiver"))))
        { writer.Write(granted); writer.Write(0L); } // Reused/stale PID with a different creation time.
        granted = 0;
        Check(!sender.AllowReceiverForeground(processId => granted = processId) && granted == 0,
            "A stale receiver identity granted permission to a different process lifetime.");
        var expected = Path.Combine(fixture.Root, "stale identity still imports.lslanding");
        sender.Submit(new[] { expected });
        Check((await Within(received.Task)).SequenceEqual(new[] { expected }), "A stale foreground identity blocked durable import delivery.");
        receiver.Dispose(); await Within(receiver.Completion);
    }

    private static void SecondaryLaunchesAndInvalidRequests()
    {
        using var fixture = new InboxFixture();
        var mutex = "Local\\ls-sharing-test-" + Guid.NewGuid().ToString("N");
        Check(GuardianInstance.TryAcquire(out var owner, mutex, activateExisting: false), "Cannot create isolated guardian owner.");
        using (owner!)
        {
            owner!.StartActivation(() => Task.FromResult(true));
            RunSecondaryLaunches(fixture, mutex).GetAwaiter().GetResult();
        } // Mutex release stays on its owning thread.
    }

    private static async Task RunSecondaryLaunches(InboxFixture fixture, string mutex)
    {
            // Invalid inbox data must be reported and retained separately without
            // blocking genuine secondary launches or allocating its claimed size.
            Directory.CreateDirectory(fixture.Root);
            var invalid = Path.Combine(fixture.Root, "00000000000000000000000000000000.request");
            using (var output = new BinaryWriter(File.Create(invalid)))
            { output.Write(0x3141464c); output.Write(1); output.Write(int.MaxValue); }
            var received = new List<string>(); var allReceived = Completion<bool>(); var errors = new List<Exception>();
            using var receiver = fixture.Receiver();
            receiver.Start(paths =>
            {
                lock (received)
                {
                    received.AddRange(paths);
                    if (received.Count == 3) allReceived.TrySetResult(true);
                }
                return Task.CompletedTask;
            }, error => { lock (errors) errors.Add(error); });
            var executable = Assembly.GetExecutingAssembly().Location;
            var firstPaths = new[] { Path.Combine(fixture.Root, "a.lslanding"), Path.Combine(fixture.Root, "b with spaces.lslanding") };
            var secondPath = Path.Combine(fixture.Root, "c-Ю.lslanding");
            using var first = OwnedProcess.Start(executable, new[] { "--landing-file-submit", fixture.Root, fixture.Signal, mutex }.Concat(firstPaths).ToArray());
            using var second = OwnedProcess.Start(executable, new[] { "--landing-file-submit", fixture.Root, fixture.Signal, mutex, secondPath });
            Check(await Within(first.Exit) == 0 && await Within(second.Exit) == 0, "A secondary launch acquired another owner or lost its request.");
            await Within(allReceived.Task);
            lock (received) Check(received.OrderBy(value => value).SequenceEqual(firstPaths.Concat(new[] { secondPath }).OrderBy(value => value)),
                "Secondary launch delivery lost or duplicated a file.");
            lock (errors) Check(errors.Count == 1 && errors[0] is InvalidDataException, "Malformed activation data was not reported explicitly.");
            Check(File.Exists(invalid + ".invalid"), "Invalid activation request was discarded without retained evidence.");
            receiver.Dispose(); await Within(receiver.Completion);
    }

    private sealed class InboxFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ls-file-open-" + Guid.NewGuid().ToString("N"));
        internal readonly string Signal = "Local\\ls-file-open-" + Guid.NewGuid().ToString("N");
        internal LandingFileActivation Receiver() => new LandingFileActivation(Root, Signal);
        internal void Submit(string[] paths) { using var sender = Receiver(); sender.Submit(paths); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class DeletionWitness : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly TaskCompletionSource<bool> _deleted = Completion<bool>();
        internal DeletionWitness(string root, string file)
        {
            _watcher = new FileSystemWatcher(root, file) { NotifyFilter = NotifyFilters.FileName };
            _watcher.Deleted += (_, _) => _deleted.TrySetResult(true);
            _watcher.Error += (_, error) => _deleted.TrySetException(error.GetException());
            _watcher.EnableRaisingEvents = true;
        }
        internal Task Deleted => _deleted.Task;
        internal void Fail(Exception error) => _deleted.TrySetException(error);
        public void Dispose() => _watcher.Dispose();
    }

    private static TaskCompletionSource<T> Completion<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Within<T>(Task<T> task)
    { await Within((Task)task); return await task; }
    private static async Task Within(Task task)
    {
        using var cancellation = new CancellationTokenSource();
        var deadline = Task.Delay(TimeSpan.FromSeconds(15), cancellation.Token);
        if (await Task.WhenAny(task, deadline) != task) throw new TimeoutException("File activation fixture did not complete.");
        cancellation.Cancel(); await task;
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
