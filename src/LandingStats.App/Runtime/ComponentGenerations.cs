using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class ProcessComponentGeneration : IIsolatedComponentGeneration
{
    private readonly ProcessWorkerHost _host;
    private readonly TimeSpan _startup;
    private readonly string? _fixture;
    private readonly TaskCompletionSource<bool> _exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public ProcessComponentGeneration(ProcessWorkerHost host, TimeSpan startup, string? fixture = null)
    { _host = host; _startup = startup; _fixture = fixture; }
    public Task Completion => _host.Completion;
    public Task Exit => _exit.Task;
    public ComponentHealthMessage? Health => _host.LastHealth;
    public Task StartAsync(CancellationToken cancellationToken) => _host.StartAsync(_startup, _fixture, cancellationToken);
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken) =>
        (await _host.CommandAsync(new WorkerCommandMessage(WorkerCommand.Probe), cancellationToken).ConfigureAwait(false)).Outcome == CommandOutcome.Completed;
    public async Task StopAsync(TimeSpan deadline)
    { await _host.StopAsync(deadline).ConfigureAwait(false); _exit.TrySetResult(true); }
    public async Task TerminateAsync(TimeSpan deadline)
    { await _host.TerminateAsync(deadline).ConfigureAwait(false); _exit.TrySetResult(true); }
    public void Dispose() => _host.Dispose();
}

/// <summary>Only the owning loop calls Progress, so a probe cannot be answered by an unrelated timer.</summary>
internal sealed class ManagedComponentContext
{
    // Flows with the owning async operation, including finite jobs several layers
    // below it. The scope covers native exit only, never parent storage callbacks.
    internal static readonly AsyncLocal<ManagedComponentContext?> Current = new AsyncLocal<ManagedComponentContext?>();
    private int _processRetirements, _delayedProcessRetirements;
    internal bool WaitingForProcessExit => Volatile.Read(ref _processRetirements) != 0;
    internal bool ProcessExitDelayed => Volatile.Read(ref _delayedProcessRetirements) != 0;
    internal ProcessRetirement TrackProcessRetirement() => new ProcessRetirement(this);
    internal sealed class ProcessRetirement : IDisposable
    {
        private ManagedComponentContext? _owner;
        private bool _delayed;
        public ProcessRetirement(ManagedComponentContext owner) { _owner = owner; Interlocked.Increment(ref owner._processRetirements); }
        public void ReportDelayedExit()
        { if (!_delayed && _owner != null) { _delayed = true; Interlocked.Increment(ref _owner._delayedProcessRetirements); } }
        public void Dispose()
        { var owner = Interlocked.Exchange(ref _owner, null); if (owner != null) { if (_delayed) Interlocked.Decrement(ref owner._delayedProcessRetirements); Interlocked.Decrement(ref owner._processRetirements); } }
    }
    private readonly ConcurrentQueue<TaskCompletionSource<bool>> _probes = new ConcurrentQueue<TaskCompletionSource<bool>>();
    private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private ComponentHealthMessage? _health;
    private long _ownerProgress, _usefulProgress;
    private int _pendingProbes, _ended;
    private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
    public ManagedComponentContext(long generation, CancellationToken token) { Generation = generation; CancellationToken = token; }
    public long Generation { get; }
    public CancellationToken CancellationToken { get; }
    public ComponentHealthMessage? Health => Volatile.Read(ref _health);
    internal Task Ready => _ready.Task;
    public void MarkReady() => _ready.TrySetResult(true);
    public async Task WaitForDependencyAsync(Task operation, DependencyWait dependency)
    {
        // Cancellation relinquishes this waiter, not the durable operation.
        // Observe its eventual failure even when shutdown has already returned.
        _ = operation.ContinueWith(task => { _ = task.Exception; }, System.Threading.CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        while (!operation.IsCompleted)
        {
            CancellationToken.ThrowIfCancellationRequested();
            Progress(false, dependency);
            await Task.WhenAny(operation, Task.Delay(250, CancellationToken)).ConfigureAwait(false);
        }
        await operation.ConfigureAwait(false);
    }
    public Task<bool> WaitForWorkAsync(TimeSpan maximumIdle) => _wake.WaitAsync(maximumIdle, CancellationToken);
    public void SignalWork()
    { try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
    public void Progress(bool useful, DependencyWait waitingFor = DependencyWait.None, int queueDepth = 0, long oldestPendingQpc = 0, Guid operationId = default)
    {
        var health = new ComponentHealthMessage(waitingFor == DependencyWait.None ? ComponentPhase.Running : ComponentPhase.Waiting,
            waitingFor, ++_ownerProgress, useful ? ++_usefulProgress : _usefulProgress, Stopwatch.GetTimestamp(), oldestPendingQpc, queueDepth, operationId);
        Volatile.Write(ref _health, health);
        while (_probes.TryDequeue(out var probe)) { Interlocked.Decrement(ref _pendingProbes); probe.TrySetResult(true); }
    }
    internal async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _ended) != 0) return false;
        if (Interlocked.Increment(ref _pendingProbes) > 128)
        { Interlocked.Decrement(ref _pendingProbes); throw new InvalidOperationException("Managed owner probe queue is full."); }
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _probes.Enqueue(result);
        if (Volatile.Read(ref _ended) != 0) result.TrySetResult(false);
        SignalWork();
        using (cancellationToken.Register(() => result.TrySetCanceled())) return await result.Task.ConfigureAwait(false);
    }
    internal void End(Exception? error)
    {
        Interlocked.Exchange(ref _ended, 1);
        _ready.TrySetException(error ?? new InvalidOperationException("Owner ended before completing startup recovery."));
        while (_probes.TryDequeue(out var probe)) { Interlocked.Decrement(ref _pendingProbes); probe.TrySetResult(false); }
    }
    internal void Dispose() => _wake.Dispose();
}

internal sealed class ManagedComponentGeneration : IComponentGeneration
{
    private readonly Func<ManagedComponentContext, Task> _run;
    private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
    private readonly ManagedComponentContext _context;
    private readonly object _gate = new object();
    private readonly TaskCompletionSource<bool> _exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _cancelCallbacks = Task.CompletedTask;
    private Task _completion = Task.CompletedTask;
    private bool _started, _ownerFinished, _cancelRequested;
    private int _disposed;
    public ManagedComponentGeneration(long generation, Func<ManagedComponentContext, Task> run)
    { _run = run; _context = new ManagedComponentContext(generation, _cancellation.Token); }
    public Task Completion => _completion;
    public Task Exit => _exit.Task;
    public ComponentHealthMessage? Health => _context.Health;
    internal bool WaitingForProcessExit => _context.WaitingForProcessExit;
    internal bool ProcessExitDelayed => _context.ProcessExitDelayed;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started || Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("Managed owner already started or disposed.");
            cancellationToken.ThrowIfCancellationRequested();
            _started = true;
            _completion = Task.Run(RunOwnerAsync);
        }
        return _context.Ready;
    }
    private async Task RunOwnerAsync()
    {
        Exception? failure = null;
        var previous = ManagedComponentContext.Current.Value;
        ManagedComponentContext.Current.Value = _context;
        try { await _run(_context).ConfigureAwait(false); }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            Task callbacks;
            lock (_gate) { _ownerFinished = true; callbacks = _cancelCallbacks; }
            try { await callbacks.ConfigureAwait(false); }
            finally { ManagedComponentContext.Current.Value = previous; _context.End(failure); _exit.TrySetResult(true); }
        }
    }
    public Task<bool> ProbeAsync(CancellationToken cancellationToken) => _context.ProbeAsync(cancellationToken);
    public async Task StopAsync(TimeSpan deadline)
    {
        Task completion;
        lock (_gate)
        {
            if (!_started) { _ownerFinished = true; _exit.TrySetResult(true); return; }
            RequestCancellation();
            completion = _completion;
        }
        if (await Task.WhenAny(completion, Task.Delay(deadline)).ConfigureAwait(false) != completion)
            throw new TimeoutException("Managed owner has not released its state.");
        try { await completion.ConfigureAwait(false); } catch { /* Actual task completion still confirms retirement. */ }
    }
    public Task TerminateAsync(TimeSpan deadline) => throw new InvalidOperationException("A noncooperative managed owner requires runtime process recovery.");
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate)
        {
            if (!_started) { _ownerFinished = true; _context.End(null); _exit.TrySetResult(true); }
            else RequestCancellation();
        }
        _ = Exit.ContinueWith(_ => { _context.Dispose(); _cancellation.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void RequestCancellation()
    {
        if (_ownerFinished || _cancelRequested) return;
        _cancelRequested = true; _cancelCallbacks = Task.Run(() => _cancellation.Cancel());
    }
}
