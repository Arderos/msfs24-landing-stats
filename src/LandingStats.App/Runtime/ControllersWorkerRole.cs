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
    // Polling and publication cadence are independent of device enumeration.
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    internal static readonly TimeSpan EnumerationInterval = TimeSpan.FromSeconds(5);
    private readonly Func<IControllerReader> _create;
    private readonly TimeSpan _enumerationInterval;
    public ControllersWorkerRole() : this(() => new DirectInputJoystickReader()) { }
    internal ControllersWorkerRole(Func<IControllerReader> create, TimeSpan? enumerationInterval = null)
    { _create = create; _enumerationInterval = enumerationInterval ?? EnumerationInterval; }
    public Task RunAsync(WorkerExecutionContext context) => Task.Factory.StartNew(() => Run(context), CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private void Run(WorkerExecutionContext context)
    {
        var owner = new WorkerOwner(context);
        owner.Progress(false, DependencyWait.Device);
        using var reader = _create();
        long revision = 1;
        owner.Publish(reader.CreateCatalog(revision), Stopwatch.GetTimestamp());
        var nextEnumeration = Stopwatch.GetTimestamp() + (long)(_enumerationInterval.TotalSeconds * Stopwatch.Frequency);
        var clocks = new long[reader.Count];
        var sample = new TelemetrySample();
        sample.ResizeControllers(reader.Count, reader.AxisCount);
        var publishedValues = new double[sample.RawControllerAxesPercent.Length]; var publishedValid = new bool[sample.RawControllerAxesValid.Length];
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
            if (clocks.Length != reader.Count)
            {
                clocks = new long[reader.Count];
                publishedValues = new double[checked(reader.Count * reader.AxisCount)];
                publishedValid = new bool[publishedValues.Length];
            }
            reader.ReadInto(sample, clocks);
            var changed = catalogChanged || publishedAt == 0;
            for (var i = 0; i < sample.RawControllerAxesPercent.Length && !changed; i++)
                changed = sample.RawControllerAxesValid[i] != publishedValid[i] ||
                    sample.RawControllerAxesValid[i] && sample.RawControllerAxesPercent[i] != publishedValues[i];
            // Poll at 100 Hz, publish every change immediately and refresh steady
            // measurements at 10 Hz, safely below the joiner's 250 ms TTL.
            if (changed || began - publishedAt >= Stopwatch.Frequency / 10)
            {
                publishedAt = Stopwatch.GetTimestamp();
                owner.Publish(new ControllerSnapshotMessage(revision, clocks, sample.RawControllerAxesPercent, sample.RawControllerAxesValid, reader.AxisCount), publishedAt);
                Array.Copy(sample.RawControllerAxesPercent, publishedValues, publishedValues.Length); Array.Copy(sample.RawControllerAxesValid, publishedValid, publishedValid.Length);
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
