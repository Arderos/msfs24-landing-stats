using System;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
namespace LandingStats.App.Runtime;
internal sealed class TransportFixtureRole : IWorkerRole
{
    private readonly string _mode;
    public TransportFixtureRole(string mode) { _mode = mode; }
    // Real process fixtures exercise the same job, handshake, pipe and output bounds.
    // They never load a simulator, controller, credential store or network client.
    public async Task RunAsync(WorkerExecutionContext context)
    {
        var mode = _mode;
        if (mode == "legacy-quota") mode = "normal";
        if (mode == "crash") Environment.Exit(73);
        if (mode == "hang") { await Task.Delay(Timeout.Infinite).ConfigureAwait(false); return; }
        if (mode != "normal" && mode != "owner-stall" && mode != "source-flood" && mode != "descendant" && mode != "landing") throw new ArgumentException("Unknown worker fixture.");
        using var descendant = mode == "descendant" ? Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LandingStats.WorkerHost.exe"), Arguments = "--fixture-child", UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        }) : null;
        long progress = 0; var initial = Stopwatch.GetTimestamp();
        if (mode == "landing")
        {
            if (context.Identity.Role != ComponentRole.Simulator) throw new ArgumentException("Landing fixture needs the simulator role.");
            if (!context.Transport.TryPublish(new LifecycleMessage(SourceLifecycle.Connected), 1, Stopwatch.GetTimestamp()) ||
                !context.Transport.TryPublish(new AircraftMessage("fixture", "Fixture aircraft", "T", "M"), 1, Stopwatch.GetTimestamp()))
                throw new IOException("Fixture output overflow.");
        }
        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (mode != "owner-stall")
            {
                while (context.Transport.TryTakeCommand(out var envelope))
                {
                    var command = envelope!.Payload as WorkerCommandMessage;
                    if (!context.Transport.TryPublish(new CommandResultMessage(command == null ? CommandOutcome.Rejected : CommandOutcome.Completed,
                            command == null ? "fixture_no_scenery" : descendant == null ? "owner_observed" : "child:" + descendant.Id.ToString(CultureInfo.InvariantCulture)), 0,
                            Stopwatch.GetTimestamp(), envelope.CorrelationId, envelope.CommandRevision)) throw new IOException("Fixture output overflow.");
                    if (command?.Command == WorkerCommand.Stop) return;
                }
                progress++;
                if (mode == "source-flood" && !context.Transport.TryPublish(new TelemetryFrameMessage(new TelemetrySample
                        { Sequence = progress, SimulationTimeSeconds = progress / 40.0, MotionSimulation = true }), 1, Stopwatch.GetTimestamp()))
                    throw new IOException("Fixture source output overflow.");
                if (mode == "landing" && progress <= 220 && !context.Transport.TryPublish(new TelemetryFrameMessage(new TelemetrySample
                    { Sequence = progress, SimulationTimeSeconds = progress / 10.0, SimulationDeltaSeconds = .1, MotionSimulation = true,
                        OnGround = progress >= 20, AboveGroundLevelFeet = progress < 20 ? 100 : 0, VelocityWorldYFps = progress < 20 ? -3 : 0,
                        LatitudeDegrees = 42, LongitudeDegrees = 23, GForce = 1 }), 1, Stopwatch.GetTimestamp()))
                    throw new IOException("Fixture source output overflow.");
            }
            if (!context.Transport.TryPublish(new ComponentHealthMessage(ComponentPhase.Running, DependencyWait.Idle,
                    progress, progress, mode != "owner-stall" ? Stopwatch.GetTimestamp() : initial, 0, 0, Guid.Empty), 0, Stopwatch.GetTimestamp()))
                throw new IOException("Fixture output overflow.");
            await Task.Delay(25, context.CancellationToken).ConfigureAwait(false);
        }
    }
}
