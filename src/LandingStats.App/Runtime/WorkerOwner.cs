using System;
using System.Diagnostics;
using System.IO;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>Commands, health and output limits are shared by every worker role.</summary>
internal sealed class WorkerOwner
{
    private readonly WorkerExecutionContext _context;
    private long _ownerProgress, _usefulProgress, _lastHealth;
    public WorkerOwner(WorkerExecutionContext context) { _context = context; }
    public bool Stopping { get; private set; }
    public void Publish(RuntimePayload payload, long capturedAt, long connection = 0)
    {
        if (!_context.Transport.TryPublish(payload, connection, capturedAt)) throw new IOException("Worker output capacity was exhausted.");
    }
    public void Progress(bool useful, DependencyWait waitingFor = DependencyWait.None, Func<RuntimeEnvelope, CommandResultMessage>? commandHandler = null,
        long oldestPendingQpc = 0, Guid operationId = default)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        _ownerProgress++; if (useful) _usefulProgress++;
        // Only the actual owner consumes these commands. A live pipe pump cannot
        // make a hung native operation look healthy.
        while (_context.Transport.TryTakeCommand(out var envelope))
        {
            var command = envelope!.Payload as WorkerCommandMessage;
            CommandResultMessage result;
            if (command?.Command == WorkerCommand.Stop) { Stopping = true; result = new CommandResultMessage(CommandOutcome.Completed, "stopping"); }
            else if (command?.Command == WorkerCommand.Probe) result = new CommandResultMessage(CommandOutcome.Completed, "owner_observed");
            else result = commandHandler?.Invoke(envelope) ?? new CommandResultMessage(CommandOutcome.Failed, "unsupported_command");
            if (!_context.Transport.TryPublish(result, 0, Stopwatch.GetTimestamp(), envelope.CorrelationId, envelope.CommandRevision))
                throw new IOException("Worker command output capacity was exhausted.");
        }
        var now = Stopwatch.GetTimestamp();
        if (_lastHealth == 0 || now - _lastHealth >= Stopwatch.Frequency / 10 || Stopping)
        {
            Publish(new ComponentHealthMessage(Stopping ? ComponentPhase.Stopping : waitingFor == DependencyWait.None ? ComponentPhase.Running : ComponentPhase.Waiting,
                waitingFor, _ownerProgress, _usefulProgress, now, oldestPendingQpc, _context.Transport.PendingCount, operationId), now);
            _lastHealth = now;
        }
    }
}
