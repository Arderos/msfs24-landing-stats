using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>The optional public endpoint has the same process retirement barrier
/// as other workers. External requests never run on acquisition or UI threads.</summary>
internal sealed class IntegrationApiRuntimeOwner
{
    private readonly Func<IntegrationApiData> _createData;
    private readonly string _executable;
    private readonly Guid _session;
    private readonly RuntimeClockEpoch _clock;
    private readonly Action<string, long> _escalate;
    private readonly Action<SupervisionSnapshot> _health;
    private readonly Action<string> _failure;
    private readonly Func<long, string?>? _fixture;
    private readonly ComponentRecoveryOptions _options;
    private readonly SemaphoreSlim _changes = new SemaphoreSlim(1, 1);
    private readonly ConcurrentQueue<Request> _requests = new ConcurrentQueue<Request>();
    private ComponentSupervisor? _supervisor;
    private Generation? _current;
    private IntegrationApiData? _data;
    private ManagedComponentContext? _owner;
    private int _enabled, _stopping, _pending;
    private long _nextReconcile, _generation;

    public IntegrationApiRuntimeOwner(Func<IntegrationApiData> createData, string executable, Guid session, RuntimeClockEpoch clock,
        Action<string, long> escalate, Action<SupervisionSnapshot> health, Action<string> failure,
        ComponentRecoveryOptions options, Func<long, string?>? fixture = null)
    { _createData = createData; _executable = executable; _session = session; _clock = clock;
        _escalate = escalate; _health = health; _failure = failure; _options = options; _fixture = fixture; }

    public bool Enabled => Volatile.Read(ref _enabled) != 0;
    public SupervisionSnapshot Snapshot => Project(Volatile.Read(ref _supervisor)?.Snapshot);
    private SupervisionSnapshot Project(SupervisionSnapshot? state) => new SupervisionSnapshot("integration.api",
        Interlocked.Read(ref _generation), state?.State ?? SupervisionState.Stopped, state?.Failures ?? 0, state?.Reason ?? "disabled");
    public long ObservationQpc => Volatile.Read(ref _supervisor)?.ObservationQpc ?? Stopwatch.GetTimestamp();
    internal ProcessWorkerHost? CurrentHost => Volatile.Read(ref _current)?.Host;

    public async Task SetEnabledAsync(bool enabled)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0) throw new InvalidOperationException("Integration API is stopping.");
            if (Enabled == enabled) return;
            if (enabled)
            {
                // No journal access or worker launch while the setting is off.
                _data ??= await Task.Run(_createData).ConfigureAwait(false);
                var supervisor = new ComponentSupervisor("integration.api", _ =>
                {
                    var generation = Interlocked.Increment(ref _generation);
                    var instance = new Generation(this, new WorkerIdentity(_session, Guid.NewGuid(), generation, ComponentRole.IntegrationApi));
                    Volatile.Write(ref _current, instance); return instance;
                }, _ => Task.CompletedTask, (name, _) => _escalate(name, Interlocked.Read(ref _generation)), _options);
                supervisor.Changed += state => _health(Project(state));
                Volatile.Write(ref _supervisor, supervisor);
                Volatile.Write(ref _enabled, 1); Interlocked.Exchange(ref _nextReconcile, 0);
                supervisor.Start(); Volatile.Read(ref _owner)?.SignalWork();
            }
            else await DisableAsync().ConfigureAwait(false);
        }
        finally { _changes.Release(); }
    }
    private async Task DisableAsync()
    {
        Volatile.Write(ref _enabled, 0);
        Volatile.Read(ref _current)?.CancelRequests();
        var supervisor = Volatile.Read(ref _supervisor);
        if (supervisor != null)
        {
            await supervisor.StopAsync().ConfigureAwait(false);
            supervisor.Dispose(); Volatile.Write(ref _supervisor, null); Volatile.Write(ref _current, null);
        }
        while (_requests.TryDequeue(out _)) Interlocked.Decrement(ref _pending);
        _health(Snapshot);
    }
    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        await _changes.WaitAsync().ConfigureAwait(false);
        try { await DisableAsync().ConfigureAwait(false); }
        finally { _changes.Release(); }
    }

    public void Publish(Guid episode, ProcessedEpisode result, bool metadataReady)
    {
        if (!Enabled) return;
        try { Volatile.Read(ref _data)?.Publish(episode, result, metadataReady); }
        catch (Exception)
        { _failure("Integration event commit failed; durable analysis will be reconciled."); }
        Volatile.Read(ref _owner)?.SignalWork();
    }

    public async Task RunAsync(ManagedComponentContext owner)
    {
        Volatile.Write(ref _owner, owner); owner.MarkReady();
        try
        {
            while (true)
            {
                owner.CancellationToken.ThrowIfCancellationRequested();
                var worked = false;
                for (var index = 0; index < 8 && _requests.TryDequeue(out var request); index++)
                {
                    Interlocked.Decrement(ref _pending);
                    if (!Enabled || request.Generation.Cancelled) continue;
                    owner.Progress(false, DependencyWait.Storage, Volatile.Read(ref _pending), Stopwatch.GetTimestamp(), request.Message.RequestId);
                    using var query = CancellationTokenSource.CreateLinkedTokenSource(owner.CancellationToken, request.Generation.Token);
                    var response = _data!.Handle(request.Message.Json, query.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(owner.CancellationToken, request.Generation.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    try { await request.Generation.Host.CommandAsync(new IntegrationResponseCommand(request.Message.RequestId, response), deadline.Token).ConfigureAwait(false); }
                    catch (Exception error) when (error is OperationCanceledException || error is IOException || error is ObjectDisposedException) { }
                    worked = true; owner.Progress(true);
                }
                if (Enabled && Stopwatch.GetTimestamp() >= Interlocked.Read(ref _nextReconcile))
                {
                    owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.NewGuid());
                    try { _data!.ReconcileCommitted(owner.CancellationToken); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is IntegrationApiException)
                    { _failure("Integration journal reconciliation will retry."); }
                    Interlocked.Exchange(ref _nextReconcile, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
                    worked = true;
                }
                owner.Progress(worked, DependencyWait.Idle);
                await owner.WaitForWorkAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        finally { Interlocked.CompareExchange(ref _owner, null, owner); }
    }
    private sealed class Request
    {
        public Request(Generation generation, IntegrationRequestMessage message) { Generation = generation; Message = message; }
        public Generation Generation { get; }
        public IntegrationRequestMessage Message { get; }
    }
    private sealed class Generation : IComponentGeneration
    {
        private readonly IntegrationApiRuntimeOwner _parent;
        private readonly ProcessComponentGeneration _process;
        private readonly CancellationTokenSource _requests = new CancellationTokenSource();
        public Generation(IntegrationApiRuntimeOwner parent, WorkerIdentity identity)
        {
            _parent = parent; Host = new ProcessWorkerHost(parent._executable, identity, parent._clock, ReceiveAsync);
            _process = new ProcessComponentGeneration(Host, parent._options.Startup, parent._fixture?.Invoke(identity.Generation));
        }
        public ProcessWorkerHost Host { get; }
        public CancellationToken Token => _requests.Token;
        public bool Cancelled => _requests.IsCancellationRequested;
        public Task Completion => _process.Completion;
        public Task Exit => _process.Exit;
        public ComponentHealthMessage? Health => _process.Health;
        public Task StartAsync(CancellationToken token) => _process.StartAsync(token);
        public Task<bool> ProbeAsync(CancellationToken token) => _process.ProbeAsync(token);
        public void CancelRequests() => _requests.Cancel();
        public Task StopAsync(TimeSpan deadline) { CancelRequests(); return _process.StopAsync(deadline); }
        public Task TerminateAsync(TimeSpan deadline) { CancelRequests(); return _process.TerminateAsync(deadline); }
        private async Task ReceiveAsync(RuntimeEnvelope envelope, long receiptQpc)
        {
            if (envelope.Payload is IntegrationRequestMessage request)
            {
                if (Cancelled || !_parent.Enabled) throw new OperationCanceledException("Integration API disabled.");
                if (Interlocked.Increment(ref _parent._pending) > 32)
                { Interlocked.Decrement(ref _parent._pending); throw new IOException("Integration request queue is full."); }
                _parent._requests.Enqueue(new Request(this, request)); Volatile.Read(ref _parent._owner)?.SignalWork();
            }
            else if (!(envelope.Payload is WorkerHello)) throw new InvalidDataException("Unexpected integration worker payload.");
            await Host.AcknowledgeAsync(envelope.Sequence).ConfigureAwait(false);
        }
        public void Dispose() { CancelRequests(); _process.Dispose(); }
    }
}
