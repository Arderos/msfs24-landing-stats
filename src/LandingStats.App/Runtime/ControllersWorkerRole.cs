using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class ControllersWorkerRole : IWorkerRole
{
    // Initial limits remain explicit until measured with the supported devices.
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    internal static readonly TimeSpan EnumerationInterval = TimeSpan.FromSeconds(5);
    private readonly Func<WindowsJoystickReader> _create;
    private readonly TimeSpan _enumerationInterval;
    public ControllersWorkerRole() : this(() => new WindowsJoystickReader()) { }
    internal ControllersWorkerRole(Func<WindowsJoystickReader> create, TimeSpan? enumerationInterval = null)
    { _create = create; _enumerationInterval = enumerationInterval ?? EnumerationInterval; }
    public Task RunAsync(WorkerExecutionContext context) => Task.Factory.StartNew(() => Run(context), CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private void Run(WorkerExecutionContext context)
    {
        var owner = new WorkerOwner(context);
        owner.Progress(false, DependencyWait.Device);
        var reader = _create();
        long revision = 1;
        owner.Publish(reader.CreateCatalog(revision), Stopwatch.GetTimestamp());
        var nextEnumeration = Stopwatch.GetTimestamp() + (long)(_enumerationInterval.TotalSeconds * Stopwatch.Frequency);
        var clocks = new long[TelemetrySample.CapturedControllerCount];
        var sample = new TelemetrySample();
        var publishedValues = new double[48]; var publishedValid = new bool[48];
        long publishedAt = 0; var catalogChanged = true;
        var enumerating = false;
        using var cadence = new OwnerCadence(context.CancellationToken);
        while (!owner.Stopping)
        {
            var began = Stopwatch.GetTimestamp();
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!enumerating && began >= nextEnumeration)
            {
                reader.BeginRefresh(); enumerating = true;
            }
            reader.ReadInto(sample, clocks);
            var changed = catalogChanged || publishedAt == 0;
            for (var i = 0; i < 48 && !changed; i++)
                changed = sample.RawControllerAxesValid[i] != publishedValid[i] ||
                    sample.RawControllerAxesValid[i] && sample.RawControllerAxesPercent[i] != publishedValues[i];
            // Poll at 100 Hz, publish every change immediately and refresh steady
            // measurements at 10 Hz, safely below the joiner's 250 ms TTL.
            if (changed || began - publishedAt >= Stopwatch.Frequency / 10)
            {
                publishedAt = Stopwatch.GetTimestamp();
                owner.Publish(new ControllerSnapshotMessage(revision, clocks, sample.RawControllerAxesPercent, sample.RawControllerAxesValid), publishedAt);
                Array.Copy(sample.RawControllerAxesPercent, publishedValues, 48); Array.Copy(sample.RawControllerAxesValid, publishedValid, 48);
                catalogChanged = false;
            }
            if (enumerating && reader.RefreshNext(out var enumerationChanged))
            {
                enumerating = false;
                if (enumerationChanged) { owner.Publish(reader.CreateCatalog(++revision), Stopwatch.GetTimestamp()); catalogChanged = true; }
                nextEnumeration = Stopwatch.GetTimestamp() + (long)(_enumerationInterval.TotalSeconds * Stopwatch.Frequency);
            }
            // Successfully observing an empty catalog is useful work, not a fault.
            owner.Progress(true, reader.Count == 0 ? DependencyWait.Device : DependencyWait.None);
            var remaining = PollInterval.TotalSeconds - (Stopwatch.GetTimestamp() - began) / (double)Stopwatch.Frequency;
            if (remaining > 0 && !owner.Stopping) cadence.Wait(TimeSpan.FromSeconds(remaining));
        }
    }
}
