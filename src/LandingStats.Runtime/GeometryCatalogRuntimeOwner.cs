using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

// One refresh after physics becomes available in a connection. Aircraft and
// flight changes within that connection reuse the same complete catalog.
internal sealed class GeometryCatalogRuntimeOwner
{
    private readonly object _gate = new object();
    private readonly Func<string, CancellationToken, Task> _refresh;
    private readonly Action<Exception> _failure;
    private ManagedComponentContext? _owner;
    private string _simulator = "";
    private bool _physics;
    private Request? _request;
    public GeometryCatalogRuntimeOwner(Func<string, CancellationToken, Task> refresh, Action<Exception> failure)
    { _refresh = refresh; _failure = failure; }

    public void Observe(RecordingRuntimeEffects effects)
    {
        lock (_gate)
        {
            var payload = effects.SourceObservation?.Payload;
            if (payload is LifecycleMessage lifecycle)
            {
                if (lifecycle.State == SourceLifecycle.Connected || lifecycle.State == SourceLifecycle.Disconnected || lifecycle.State == SourceLifecycle.SimulatorQuit)
                { _request?.Ready.TrySetResult(false); _request = null; _physics = false; _simulator = ""; }
                else if (lifecycle.State == SourceLifecycle.PhysicsReady) _physics = true;
            }
            if (payload is AircraftMessage aircraft) _simulator = aircraft.Simulator;
            if (effects.SourceSample?.MotionSimulation == true) _physics = true;
            if (!_physics || string.IsNullOrWhiteSpace(_simulator) || _request != null) return;
            _request = new Request(_simulator); _owner?.SignalWork();
        }
    }

    public async Task<bool> WaitReadyAsync(string simulator, CancellationToken token)
    {
        Task<bool> ready;
        lock (_gate)
        {
            if (_request == null || _request.Simulator != simulator) return false;
            ready = _request.Ready.Task;
        }
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetCanceled()))
        {
            if (await Task.WhenAny(ready, cancelled.Task).ConfigureAwait(false) != ready) token.ThrowIfCancellationRequested();
            return await ready.ConfigureAwait(false);
        }
    }

    public async Task RunAsync(ManagedComponentContext owner)
    {
        lock (_gate) _owner = owner;
        try
        {
            owner.MarkReady();
            while (true)
            {
                owner.CancellationToken.ThrowIfCancellationRequested();
                Request? request;
                lock (_gate) request = _request;
                if (request != null && !request.Ready.Task.IsCompleted)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(owner.CancellationToken);
                    deadline.CancelAfter(TimeSpan.FromMinutes(2));
                    var start = Stopwatch.GetTimestamp(); var operationId = Guid.NewGuid();
                    try
                    {
                        var refresh = _refresh(request.Simulator, deadline.Token);
                        // Keep owning the child until its whole job exits, including
                        // cancellation. Startup/recording never await this loop.
                        while (!refresh.IsCompleted)
                        {
                            owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: start, operationId: operationId);
                            await Task.WhenAny(refresh, Task.Delay(250)).ConfigureAwait(false);
                        }
                        await refresh.ConfigureAwait(false); request.Ready.TrySetResult(true);
                    }
                    catch (Exception error) when (!owner.CancellationToken.IsCancellationRequested)
                    { request.Ready.TrySetResult(false); _failure(error); }
                    owner.Progress(true);
                }
                owner.Progress(false, DependencyWait.Idle);
                await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        finally { lock (_gate) { _owner = null; _request?.Ready.TrySetResult(false); } }
    }
    private sealed class Request
    {
        public Request(string simulator) { Simulator = simulator; }
        public readonly string Simulator;
        public readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
