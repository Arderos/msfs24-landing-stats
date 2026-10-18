using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>Composition root for acquisition, durable dispatch, recording and
/// commands. None of these owners runs on a presentation dispatcher. The sink
/// persists episode/raw effects before the recording checkpoint can advance.</summary>
internal sealed class ApplicationRuntime : IDisposable
{
    private readonly DurableRuntimeDispatcher _journal;
    private readonly RecordingRuntimeOwner _recording;
    private readonly RuntimeCommandQueue _commands;
    private readonly List<ComponentSupervisor> _managed = new List<ComponentSupervisor>();
    private readonly Dictionary<ComponentRole, RuntimeWorkerSource> _sources = new Dictionary<ComponentRole, RuntimeWorkerSource>();
    private readonly ComponentSupervisor _recordOwner, _journalOwner, _commandOwner, _tickOwner;
    private readonly object _lifecycle = new object();
    private readonly OperationLifetime _acceptedCommands = new OperationLifetime();
    private readonly CancellationTokenSource _commandLifetime = new CancellationTokenSource();
    private readonly Func<DurableRuntimeInput, RecordingRuntimeEffects, Task> _effects;
    private Task? _stop;
    private bool _started, _stopping;
    public ApplicationRuntime(string root, RecoveryJournalIdentity identity, string workerExecutable,
        Func<DurableRuntimeInput, RecordingRuntimeEffects, Task> commitEffects, Action<string, long> escalate,
        Func<ComponentRole, long, string?>? fixture = null, ComponentRecoveryOptions? recoveryOptions = null, Func<Task>? beforeCheckpoint = null)
    {
        Identity = identity; _effects = commitEffects; root = Path.GetFullPath(root);
        var catalog = RuntimeComponentCatalog.Create();
        ComponentRecoveryOptions Options(RuntimeComponent component) => recoveryOptions ?? catalog.Single(item => item.Component == component).Options;
        _commands = new RuntimeCommandQueue(Path.Combine(root, "commands"), FindHost,
            async outcome => { var receipt = await SubmitAsync(JournalRecordKind.CommandResult, outcome.Encode(), CancellationToken.None).ConfigureAwait(false); await receipt.WaitAsync().ConfigureAwait(false); });
        _recording = new RecordingRuntimeOwner(identity, async (input, effects) =>
        {
            await _effects(input, effects).ConfigureAwait(false);
            _commands.Commit(input, effects.Commands);
        }, async (cursor, bytes) =>
        {
            if (beforeCheckpoint != null) await beforeCheckpoint().ConfigureAwait(false);
            await _journal!.SaveCheckpointAsync(cursor, bytes).ConfigureAwait(false);
            _commands.CheckpointCommitted(cursor);
        }, error => _journal!.InvalidateConsumer(error));
        _journal = new DurableRuntimeDispatcher(Path.Combine(root, "journal"), identity, _recording.RestoreAsync, _recording.PublishAsync,
            prepare: checkpoint => new ControllerInputPreparation(checkpoint == null ? new ControllerJoiner(identity.Clock) :
                ControllerJoiner.RestoreCheckpoint(RecordingRuntimeState.RestoreCheckpoint(checkpoint, identity).CaptureJoinCheckpoint(), identity.Clock)));
        ComponentSupervisor Managed(string name, RuntimeComponent component, Func<ManagedComponentContext, Task> run)
        {
            var supervisor = new ComponentSupervisor(name, generation => new ManagedComponentGeneration(generation, run), _ => Task.CompletedTask, escalate, Options(component));
            supervisor.Changed += snapshot => HealthChanged?.Invoke(snapshot);
            _managed.Add(supervisor); return supervisor;
        }
        _recordOwner = Managed("recording", RuntimeComponent.Episodes, _recording.RunAsync);
        _journalOwner = Managed("journal", RuntimeComponent.IngressJournal, _journal.RunAsync);
        _commandOwner = Managed("commands", RuntimeComponent.Facilities, _commands.RunAsync);
        _tickOwner = Managed("recording.clock", RuntimeComponent.Episodes, async context =>
        {
            context.MarkReady();
            while (true)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: Guid.NewGuid());
                var receipt = await SubmitAsync(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.Tick).Encode(), context.CancellationToken).ConfigureAwait(false);
                await context.WaitForDependencyAsync(receipt.Durable, DependencyWait.Storage).ConfigureAwait(false); context.Progress(true);
                await context.WaitForWorkAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        });
        foreach (var role in new[] { ComponentRole.Simulator, ComponentRole.Controllers })
        {
            _sources.Add(role, new RuntimeWorkerSource("source." + role.ToString().ToLowerInvariant(), workerExecutable, identity.RuntimeId, role,
                identity.Clock, _journal, id => _recording.WaitForCheckpointAsync(id), escalate,
                Options(role == ComponentRole.Simulator ? RuntimeComponent.Simulator : RuntimeComponent.Controllers), generation => fixture?.Invoke(role, generation)));
            _sources[role].Changed += snapshot => HealthChanged?.Invoke(snapshot);
        }
    }
    public RecoveryJournalIdentity Identity { get; }
    public bool SafeToRestart => _recording.SafeToRestart;
    public long ObservationQpc => Math.Min(_managed.Min(item => item.ObservationQpc), _sources.Values.Min(item => item.ObservationQpc));
    public event Action<SupervisionSnapshot>? HealthChanged;
    public IReadOnlyList<SupervisionSnapshot> Health => _managed.Select(item => item.Snapshot).Concat(_sources.Values.Select(item => item.Snapshot)).ToArray();
    public ProcessWorkerHost? FindHost(WorkerIdentity identity)
    {
        if (!_sources.TryGetValue(identity.Role, out var source)) return null;
        var host = source.CurrentHost; return host != null && host.Identity.Equals(identity) ? host : null;
    }
    internal RuntimeWorkerSource Source(ComponentRole role) => _sources[role];
    public void Start()
    { StartOwners(); StartSources(); }
    public async Task StartConfiguredAsync(DeliveryPolicy policy, bool rawCapture = false, CancellationToken cancellationToken = default)
    {
        StartOwners();
        // Source handshakes and frames may only follow durable user intent.
        await ApplyDeliveryPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        if (rawCapture) await SetRawCaptureAsync(true, cancellationToken).ConfigureAwait(false);
        StartSources();
    }
    internal void StartOwners()
    {
        lock (_lifecycle)
        {
            if (_started || _stopping) throw new InvalidOperationException("Application runtime cannot be started twice.");
            _started = true;
            _recordOwner.Start(); _journalOwner.Start(); _commandOwner.Start(); _tickOwner.Start();
        }
    }
    internal void StartSources()
    { lock (_lifecycle) { if (!_stopping) foreach (var source in _sources.Values) source.Start(); } }
    private IDisposable AcceptCommand()
    {
        lock (_lifecycle)
        {
            if (!_started || _stopping) throw new InvalidOperationException("Application runtime is not accepting commands.");
            return _acceptedCommands.TryEnter()!;
        }
    }
    public async Task SetRawCaptureAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _commandLifetime.Token);
        cancellationToken = lifetime.Token;
        using var command = AcceptCommand();
        var receipt = await SubmitAsync(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.RawCapture, enabled).Encode(), cancellationToken).ConfigureAwait(false);
        await receipt.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task ApplyDeliveryPolicyAsync(DeliveryPolicy policy, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _commandLifetime.Token);
        cancellationToken = lifetime.Token;
        using var command = AcceptCommand();
        var receipt = await SubmitAsync(JournalRecordKind.RuntimeCommand,
            new RecordingCommand(RecordingCommandKind.DeliveryPolicy, policy.AutomaticEnabled, policy.Revision).Encode(), cancellationToken).ConfigureAwait(false);
        await receipt.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private async Task<RuntimeInputReceipt> SubmitAsync(JournalRecordKind kind, byte[] bytes, CancellationToken cancellationToken)
    {
        var arrival = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_journal.TrySubmit(kind, bytes, arrival, out var receipt)) return receipt!;
            await _journal.WaitForCapacityAsync(bytes.Length, cancellationToken).ConfigureAwait(false);
        }
    }
    public Task StopAsync()
    {
        lock (_lifecycle)
        {
            if (_stop != null) return _stop;
            _stopping = true;
            _commandLifetime.Cancel();
            return _stop = _started ? StopOwnersAsync() : Task.CompletedTask;
        }
    }
    private async Task StopOwnersAsync()
    {
        var failures = new List<Exception>();
        async Task Attempt(Func<Task> step)
        { try { await step().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); } }
        await Attempt(() => _acceptedCommands.Stop());
        await Attempt(() => _tickOwner.StopAsync());
        await Attempt(() => Task.WhenAll(_sources.Values.Select(source => source.StopAsync())));
        await Attempt(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stop = await SubmitAsync(JournalRecordKind.RuntimeCommand, new RecordingCommand(RecordingCommandKind.Stop).Encode(), deadline.Token).ConfigureAwait(false);
            await stop.WaitAsync(deadline.Token).ConfigureAwait(false);
            await _recording.WaitForCheckpointAsync(stop.Id, deadline.Token).ConfigureAwait(false);
        });
        await Attempt(() => _commandOwner.StopAsync());
        await Attempt(() => _recording.CloseIntakeAsync());
        await Attempt(() => _journal.CloseIntakeAsync());
        await Attempt(() => _journalOwner.StopAsync());
        await Attempt(() => _recordOwner.StopAsync());
        if (failures.Count != 0) throw new AggregateException("Runtime stopped with pending recovery work.", failures);
    }
    public void Dispose()
    {
        _ = StopAsync().ContinueWith(_ =>
        {
            foreach (var source in _sources.Values) source.Dispose();
            foreach (var owner in _managed) owner.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
