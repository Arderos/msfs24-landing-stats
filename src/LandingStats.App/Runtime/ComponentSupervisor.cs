using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal interface IComponentGeneration : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task Completion { get; }
    // Successful completion means ownership really ended. Fault/cancellation is
    // inconclusive and never authorizes another generation.
    Task Exit { get; }
    ComponentHealthMessage? Health { get; }
    Task<bool> ProbeAsync(CancellationToken cancellationToken);
    Task StopAsync(TimeSpan deadline);
    Task TerminateAsync(TimeSpan deadline);
}
// An isolated generation owns an OS process tree, not mutable state on an App thread.
// Its delayed termination must be contained at that boundary.
internal interface IIsolatedComponentGeneration : IComponentGeneration { }
internal enum SupervisionState { Stopped, Starting, Recovering, Running, Waiting, Stopping, Escalating }
internal sealed class SupervisionSnapshot
{
    public SupervisionSnapshot(string component, long generation, SupervisionState state, int failures, string reason,
        bool settled = false, long unavailableSinceQpc = 0, string failure = "")
    { Component = component; Generation = generation; State = state; Failures = failures; Reason = reason;
        Settled = settled; UnavailableSinceQpc = unavailableSinceQpc; Failure = failure; }
    public string Component { get; }
    public long Generation { get; }
    public SupervisionState State { get; }
    public int Failures { get; }
    public string Reason { get; }
    public bool Settled { get; }
    public long UnavailableSinceQpc { get; }
    public string Failure { get; }
}
internal sealed class ComponentRecoveryOptions
{
    public TimeSpan Startup { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan OwnerStall { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan Probe { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan Stop { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan StableUsefulProgress { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan OperationDeadline { get; set; } = TimeSpan.FromMinutes(2);
    public void Validate()
    {
        if (Startup <= TimeSpan.Zero || OwnerStall <= TimeSpan.Zero || Probe <= TimeSpan.Zero || Stop <= TimeSpan.Zero ||
            CheckInterval <= TimeSpan.Zero || InitialBackoff < TimeSpan.Zero || MaximumBackoff < InitialBackoff || StableUsefulProgress < TimeSpan.Zero || OperationDeadline <= TimeSpan.Zero)
            throw new ArgumentException("Invalid component recovery deadlines.");
    }
}

/// <summary>The same lifecycle for process and managed owners; no permanent disable after repeated failures.</summary>
internal sealed class ComponentSupervisor : IDisposable
{
    private readonly string _name;
    private readonly Func<long, IComponentGeneration> _factory;
    private readonly Func<long, Task> _retire;
    private readonly Action<string, long> _escalate;
    private readonly ComponentRecoveryOptions _options;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly Func<double> _jitter;
    private readonly object _lifecycleGate = new object();
    private Task _completion = Task.CompletedTask;
    private SupervisionSnapshot _snapshot;
    private long _generation;
    private long _unavailableSinceQpc;
    private bool _settled;
    private bool _requiresStabilization;
    private string _failure = "";
    private long _escalatedGeneration = -1;
    private int _started, _stopped, _disposed, _failures;
    private long _observationQpc = Stopwatch.GetTimestamp();
    public ComponentSupervisor(string name, Func<long, IComponentGeneration> factory, Func<long, Task> retire,
        Action<string, long> escalate, ComponentRecoveryOptions? options = null, Func<double>? jitter = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A component name is required.");
        _name = name; _factory = factory; _retire = retire; _escalate = escalate;
        _options = options ?? new ComponentRecoveryOptions(); _options.Validate();
        var random = new Random(); _jitter = jitter ?? (() => random.NextDouble());
        _snapshot = new SupervisionSnapshot(name, 0, SupervisionState.Stopped, 0, "not_started");
    }
    public Task Completion => _completion;
    public long ObservationQpc => Interlocked.Read(ref _observationQpc);
    public SupervisionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action<SupervisionSnapshot>? Changed;
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_stopped != 0 || _started != 0) throw new InvalidOperationException("Component supervisor is already started or stopped.");
            _started = 1; _unavailableSinceQpc = Stopwatch.GetTimestamp(); _completion = Task.Run(RunAsync);
        }
    }
    public Task StopAsync()
    {
        lock (_lifecycleGate)
        {
            if (_stopped == 0) { _stopped = 1; _shutdown.Cancel(); }
            return _completion;
        }
    }
    private async Task RunAsync()
    {
        var fatal = false;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _observationQpc, Stopwatch.GetTimestamp());
                IComponentGeneration? owner = null;
                var failed = false;
                _generation++;
                Publish(_failures == 0 ? SupervisionState.Starting : SupervisionState.Recovering, "creating_owner");
                try
                {
                    owner = _factory(_generation);
                    var startup = owner.StartAsync(_shutdown.Token);
                    await AwaitDeadline(startup, _options.Startup, _shutdown.Token, "Component startup expired.").ConfigureAwait(false);
                    await MonitorAsync(owner).ConfigureAwait(false);
                    if (!_shutdown.IsCancellationRequested) throw new InvalidOperationException("Component owner ended unexpectedly.");
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                catch (Exception error)
                {
                    failed = true; if (_failures < int.MaxValue) _failures++;
                    MarkUnavailable(error.GetType().Name + ": " + Bound(error.Message));
                    Publish(SupervisionState.Recovering, error.GetType().Name + ": " + Bound(error.Message));
                }
                finally
                {
                    if (owner != null)
                    {
                        await EndOwnerAsync(owner).ConfigureAwait(false);
                        owner.Dispose();
                    }
                }
                // A failed callback is retried without replacing this retirement
                // boundary or admitting a second owner over unconsumed state.
                await RetireAsync().ConfigureAwait(false);
                if (_shutdown.IsCancellationRequested) break;
                if (failed) await WaitBackoffAsync(Backoff(_failures)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            fatal = true; MarkUnavailable(error.GetType().Name + ": " + Bound(error.Message));
            Publish(SupervisionState.Escalating, "supervision_failed: " + Bound(error.Message));
            Escalate(); throw;
        }
        finally { if (_shutdown.IsCancellationRequested && !fatal) Publish(SupervisionState.Stopped, "shutdown"); }
    }
    private async Task MonitorAsync(IComponentGeneration owner)
    {
        var ownerObserved = Stopwatch.StartNew();
        var usefulStable = Stopwatch.StartNew();
        var liveStable = Stopwatch.StartNew(); var observations = 0;
        long ownerCounter = -1, usefulCounter = 0;
        var sawUseful = false;
        while (!_shutdown.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _observationQpc, Stopwatch.GetTimestamp());
            if (owner.Completion.IsCompleted) { await owner.Completion.ConfigureAwait(false); return; }
            if (owner is ManagedComponentGeneration managed && managed.ProcessExitDelayed)
            {
                MarkUnavailable("Windows has not yet confirmed the worker process tree exit.");
                liveStable.Restart(); observations = 0; ownerObserved.Restart();
                Publish(SupervisionState.Recovering, "worker_exit_pending");
                await Task.Delay(_options.CheckInterval, _shutdown.Token).ConfigureAwait(false);
                continue;
            }
            var health = owner.Health;
            if (health != null)
            {
                if (health.OperationId != Guid.Empty && health.OldestPendingQpc > 0 &&
                    (Stopwatch.GetTimestamp() - health.OldestPendingQpc) / (double)Stopwatch.Frequency >= _options.OperationDeadline.TotalSeconds)
                    throw new TimeoutException("The active component operation exceeded its deadline.");
                if (health.OwnerProgress < ownerCounter || health.UsefulProgress < usefulCounter)
                    throw new InvalidOperationException("Component counters reversed within a generation.");
                if (health.OwnerProgress != ownerCounter)
                {
                    ownerCounter = health.OwnerProgress; ownerObserved.Restart(); observations++;
                    // First readiness is immediate, including an idle owner or absent simulator.
                    // Only recovery needs sustained progress; a transport handshake is not readiness.
                    var ready = health.OwnerProgress > 0 &&
                        (health.Phase == ComponentPhase.Running || health.Phase == ComponentPhase.Waiting);
                    if (!_settled && ready && (!_requiresStabilization ||
                        observations > 1 && liveStable.Elapsed >= _options.StableUsefulProgress))
                    { _settled = true; _requiresStabilization = false; _unavailableSinceQpc = 0; _failure = ""; }
                }
                if (health.UsefulProgress > usefulCounter)
                {
                    if (!sawUseful) { sawUseful = true; usefulStable.Restart(); }
                    usefulCounter = health.UsefulProgress;
                    if (usefulStable.Elapsed >= _options.StableUsefulProgress) _failures = 0;
                    Publish(SupervisionState.Running, "useful_progress");
                }
                else if (health.WaitingFor != DependencyWait.None) Publish(SupervisionState.Waiting, health.WaitingFor.ToString());
                else Publish(SupervisionState.Running, "owner_progress");
            }
            var boundedOperation = health != null && health.OperationId != Guid.Empty && health.OldestPendingQpc > 0;
            if (!boundedOperation && ownerObserved.Elapsed >= _options.OwnerStall)
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                probe.CancelAfter(_options.Probe);
                var check = owner.ProbeAsync(probe.Token);
                await AwaitDeadline(check, _options.Probe, _shutdown.Token, "Component owner did not answer its probe.").ConfigureAwait(false);
                if (!await check.ConfigureAwait(false)) throw new InvalidOperationException("Component owner rejected its probe.");
                // A probe proves liveness only. It never clears failure backoff or
                // declares recovery without useful work from this owner.
                ownerObserved.Restart();
            }
            await Task.Delay(_options.CheckInterval, _shutdown.Token).ConfigureAwait(false);
        }
    }
    private async Task EndOwnerAsync(IComponentGeneration owner)
    {
        Publish(SupervisionState.Stopping, _shutdown.IsCancellationRequested ? "shutdown" : "retiring_generation");
        try { await ObserveStopAsync(() => owner.StopAsync(_options.Stop), "Owner stop expired.").ConfigureAwait(false); }
        catch (Exception error) { RetirementFailure("stop", error); }
        var attempts = 0;
        while (!ConfirmedExit(owner))
        {
            if (WaitingForChildExit(owner))
            {
                Publish(SupervisionState.Recovering, "worker_exit_pending");
                Interlocked.Exchange(ref _observationQpc, Stopwatch.GetTimestamp());
                await Task.Delay(_options.CheckInterval).ConfigureAwait(false);
                continue;
            }
            try { await ObserveStopAsync(() => owner.TerminateAsync(_options.Stop), "Owner termination expired.").ConfigureAwait(false); }
            catch (Exception error) { RetirementFailure("termination", error); }
            if (ConfirmedExit(owner)) break;
            if (!(owner is IIsolatedComponentGeneration))
            {
                Publish(SupervisionState.Escalating, "owner_exit_unconfirmed");
                Escalate();
                await owner.Exit.ConfigureAwait(false);
                if (!ConfirmedExit(owner)) throw new InvalidOperationException("Owner exit remains unconfirmed.");
                break;
            }
            // The OS may still be releasing native I/O. Keep the process pinned and
            // the supervisor observable; never create a second owner or restart the App.
            Publish(SupervisionState.Recovering, "worker_exit_pending");
            var delay = Backoff(Math.Min(++attempts, 6));
            var watch = Stopwatch.StartNew();
            while (!ConfirmedExit(owner) && watch.Elapsed < delay)
            {
                Interlocked.Exchange(ref _observationQpc, Stopwatch.GetTimestamp());
                await Task.Delay(_options.CheckInterval).ConfigureAwait(false);
            }
        }
    }
    private async Task ObserveStopAsync(Func<Task> action, string failure)
    {
        // Even a native termination call can block before returning a Task.
        var operation = Task.Run(action);
        var watch = Stopwatch.StartNew();
        while (!operation.IsCompleted && watch.Elapsed < _options.Stop)
        {
            Interlocked.Exchange(ref _observationQpc, Stopwatch.GetTimestamp());
            await Task.WhenAny(operation, Task.Delay(_options.CheckInterval)).ConfigureAwait(false);
        }
        if (!operation.IsCompleted)
        {
            _ = operation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new TimeoutException(failure);
        }
        await operation.ConfigureAwait(false);
    }
    private void RetirementFailure(string phase, Exception error)
    {
        if (!_shutdown.IsCancellationRequested) MarkUnavailable(_failure.Length == 0 ? error.Message : _failure);
        Publish(SupervisionState.Recovering, phase + ": " + Bound(error.Message));
        System.Diagnostics.Trace.TraceError("{0} generation={1} {2}: {3}", _name, _generation, phase, error);
    }
    private void MarkUnavailable(string failure)
    {
        if (_unavailableSinceQpc == 0) _unavailableSinceQpc = Stopwatch.GetTimestamp();
        _settled = false; _requiresStabilization = true; _failure = failure;
    }
    private async Task RetireAsync()
    {
        while (true)
        {
            Interlocked.Exchange(ref _observationQpc, Stopwatch.GetTimestamp());
            try
            {
                var retirement = _retire(_generation);
                while (!retirement.IsCompleted)
                {
                    Interlocked.Exchange(ref _observationQpc, Stopwatch.GetTimestamp());
                    Publish(SupervisionState.Waiting, "retirement_checkpoint");
                    await Task.WhenAny(retirement, Task.Delay(_options.CheckInterval)).ConfigureAwait(false);
                }
                await retirement.ConfigureAwait(false); return;
            }
            catch (Exception error)
            {
                Publish(SupervisionState.Waiting, "retirement_checkpoint: " + Bound(error.Message));
                // Shutdown can leave a durable pending checkpoint for the next
                // runtime, but can never create another owner in this runtime.
                if (_shutdown.IsCancellationRequested) return;
                await WaitBackoffAsync(Backoff(Math.Max(1, _failures))).ConfigureAwait(false);
            }
        }
    }
    internal TimeSpan Backoff(int failures)
    {
        var steps = new[] { 1d, 2d, 5d, 10d, 30d, 60d };
        var capped = Math.Min(_options.MaximumBackoff.TotalMilliseconds, _options.InitialBackoff.TotalMilliseconds * steps[Math.Min(steps.Length - 1, Math.Max(0, failures - 1))]);
        var factor = 0.8 + Math.Max(0, Math.Min(1, _jitter())) * 0.4;
        return TimeSpan.FromMilliseconds(Math.Min(_options.MaximumBackoff.TotalMilliseconds, capped * factor));
    }
    private async Task WaitBackoffAsync(TimeSpan delay)
    {
        var deadline = Stopwatch.GetTimestamp() + delay.TotalSeconds * Stopwatch.Frequency;
        while (true)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _observationQpc, Stopwatch.GetTimestamp());
            var remaining = (deadline - Stopwatch.GetTimestamp()) / Stopwatch.Frequency;
            if (remaining <= 0) return;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(0.25, remaining)), _shutdown.Token).ConfigureAwait(false);
        }
    }
    private static bool ConfirmedExit(IComponentGeneration owner) => owner.Exit.Status == TaskStatus.RanToCompletion;
    private static bool WaitingForChildExit(IComponentGeneration owner) => owner is ManagedComponentGeneration managed && managed.WaitingForProcessExit;
    private static async Task AwaitDeadline(Task operation, TimeSpan deadline, CancellationToken cancellationToken, string failure)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var delay = Task.Delay(deadline, timer.Token);
            if (await Task.WhenAny(operation, delay).ConfigureAwait(false) != operation)
            {
                _ = operation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested(); throw new TimeoutException(failure);
            }
            await operation.ConfigureAwait(false);
        }
        finally { timer.Cancel(); }
    }
    private void Publish(SupervisionState state, string reason)
    {
        var previous = Snapshot;
        if (previous.Generation == _generation && previous.State == state && previous.Failures == _failures && previous.Reason == reason && previous.Settled == _settled && previous.UnavailableSinceQpc == _unavailableSinceQpc && previous.Failure == _failure) return;
        var snapshot = new SupervisionSnapshot(_name, _generation, state, _failures, reason, _settled, _unavailableSinceQpc, _failure);
        Volatile.Write(ref _snapshot, snapshot);
        // Presentation/diagnostics are observers, not owners of supervision.
        try { Changed?.Invoke(snapshot); } catch { }
    }
    private static string Bound(string value) => value.Length <= 512 ? value : value.Substring(0, 512);
    private void Escalate()
    {
        if (_escalatedGeneration == _generation) return;
        _escalatedGeneration = _generation; _escalate(_name, _generation);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ = StopAsync().ContinueWith(_ => _shutdown.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
