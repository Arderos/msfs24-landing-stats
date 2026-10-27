using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class RuntimeSourceRetirement
{
    public RuntimeSourceRetirement(WorkerIdentity worker, long connection, long accepted, SourceGapReason reason, bool intentional)
    {
        if (connection < 0 || accepted < 0 || !Enum.IsDefined(typeof(SourceGapReason), reason) || (worker.Role != ComponentRole.Simulator && connection != 0))
            throw new ArgumentException("Invalid source retirement.");
        Worker = worker; Connection = connection; LastAccepted = accepted; Reason = reason; Intentional = intentional;
    }
    public WorkerIdentity Worker { get; }
    public long Connection { get; }
    public long LastAccepted { get; }
    public SourceGapReason Reason { get; }
    public bool Intentional { get; }
    public byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(Worker.AppSessionId.ToByteArray()); writer.Write(Worker.InstanceId.ToByteArray()); writer.Write(Worker.Generation);
        writer.Write((byte)Worker.Role); writer.Write(Connection); writer.Write(LastAccepted); writer.Write((byte)Reason); writer.Write(Intentional); return stream.ToArray();
    }
    public static RuntimeSourceRetirement Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported source retirement version.");
        var worker = new WorkerIdentity(new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)), new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)), reader.ReadInt64(), (ComponentRole)reader.ReadByte());
        var result = new RuntimeSourceRetirement(worker, reader.ReadInt64(), reader.ReadInt64(), (SourceGapReason)reader.ReadByte(), RuntimeCheckpointCodec.ReadBoolean(reader));
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected source retirement suffix."); return result;
    }
}

/// <summary>All source roles share durable ingress, cumulative ACK and the same retirement barrier.</summary>
internal sealed class RuntimeWorkerSource : IDisposable
{
    private readonly ComponentSupervisor _supervisor;
    private readonly DurableRuntimeDispatcher _dispatcher;
    private readonly Func<Guid, Task> _consumeRetirement;
    private SourceGeneration? _current;
    private int _stopping;
    public RuntimeWorkerSource(string name, string executable, Guid session, ComponentRole role, RuntimeClockEpoch clock,
        DurableRuntimeDispatcher dispatcher, Func<Guid, Task> consumeRetirement, Action<string, long> escalate,
        ComponentRecoveryOptions? options = null, Func<long, string?>? fixture = null)
    {
        _dispatcher = dispatcher; _consumeRetirement = consumeRetirement; options ??= new ComponentRecoveryOptions();
        var recovery = options;
        _supervisor = new ComponentSupervisor(name, generation =>
        {
            if (Volatile.Read(ref _stopping) != 0) throw new OperationCanceledException("Source is stopping.");
            var owner = new SourceGeneration(this, executable, new WorkerIdentity(session, Guid.NewGuid(), generation, role), clock, recovery.Startup, fixture?.Invoke(generation));
            Volatile.Write(ref _current, owner); return owner;
        }, async generation =>
        {
            var owner = Volatile.Read(ref _current);
            if (owner != null && owner.Identity.Generation == generation) await owner.RetireAsync().ConfigureAwait(false);
        }, escalate, options);
    }
    public SupervisionSnapshot Snapshot => _supervisor.Snapshot;
    public long ObservationQpc => _supervisor.ObservationQpc;
    public ProcessWorkerHost? CurrentHost => Volatile.Read(ref _current)?.Host;
    public event Action<SupervisionSnapshot>? Changed { add => _supervisor.Changed += value; remove => _supervisor.Changed -= value; }
    public void Start() => _supervisor.Start();
    public Task StopAsync() { Interlocked.Exchange(ref _stopping, 1); return _supervisor.StopAsync(); }
    public void Dispose() { Interlocked.Exchange(ref _stopping, 1); _supervisor.Dispose(); }

    private sealed class SourceGeneration : IIsolatedComponentGeneration
    {
        private readonly RuntimeWorkerSource _source;
        private readonly ProcessComponentGeneration _process;
        private readonly CancellationTokenSource _intake = new CancellationTokenSource();
        private readonly OperationLifetime _callbacks = new OperationLifetime();
        private Task _callbacksDrained = Task.CompletedTask;
        private RuntimeInputReceipt? _retirement;
        private long _lastAccepted, _connection;
        private int _disposed;
        public SourceGeneration(RuntimeWorkerSource source, string executable, WorkerIdentity identity, RuntimeClockEpoch clock, TimeSpan startup, string? fixture)
        {
            _source = source; Identity = identity;
            Host = new ProcessWorkerHost(executable, identity, clock, ReceiveAsync);
            _process = new ProcessComponentGeneration(Host, startup, fixture);
        }
        public WorkerIdentity Identity { get; }
        public ProcessWorkerHost Host { get; }
        public Task Completion => _process.Completion;
        public Task Exit => _process.Exit;
        public ComponentHealthMessage? Health => _process.Health;
        public Task StartAsync(CancellationToken token) => _process.StartAsync(token);
        public Task<bool> ProbeAsync(CancellationToken token) => _process.ProbeAsync(token);
        public Task StopAsync(TimeSpan deadline) => _process.StopAsync(deadline);
        public Task TerminateAsync(TimeSpan deadline) => _process.TerminateAsync(deadline);
        private async Task ReceiveAsync(RuntimeEnvelope envelope, long receiptQpc)
        {
            using var callback = _callbacks.TryEnter() ?? throw new OperationCanceledException("Source intake has retired.");
            if (envelope.ConnectionGeneration < _connection || !RuntimeSequence.IsNext(_lastAccepted, envelope) || (envelope.Sequence > 1 && envelope.Payload is WorkerHello))
                throw new InvalidDataException("Source context reversed inside its generation.");
            var bytes = RuntimeWire.Encode(envelope); RuntimeInputReceipt? receipt;
            var token = _intake.Token;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_source._dispatcher.TrySubmit(JournalRecordKind.SourceMessage, bytes, receiptQpc, out receipt)) break;
                await _source._dispatcher.WaitForCapacityAsync(bytes.Length, token).ConfigureAwait(false);
            }
            _lastAccepted = envelope.Sequence; _connection = envelope.ConnectionGeneration;
            // ACKs may finish out of order. The host writes a cumulative prefix
            // and ignores an ACK older than the highest prefix already sent.
            _ = AcknowledgeCommittedAsync(receipt!, envelope.Sequence);
        }
        private async Task AcknowledgeCommittedAsync(RuntimeInputReceipt receipt, long sequence)
        {
            try { await receipt.Durable.ConfigureAwait(false); await Host.AcknowledgeAsync(sequence).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException || error is ObjectDisposedException || error is IOException)
            { /* The journal still owns these bytes after the worker/pipe has ended. */ }
        }
        public async Task RetireAsync()
        {
            // The supervisor has already confirmed actual process-tree exit and
            // cancelled this generation's blocked intake. Finish its accepted prefix.
            SourceGapReason reason = SourceGapReason.ProcessExit;
            await _callbacksDrained.ConfigureAwait(false);
            try { await Host.SourceCompletion.ConfigureAwait(false); }
            catch (InvalidDataException) { reason = SourceGapReason.ProtocolFailure; }
            catch (Exception error) when (error is IOException || error is OperationCanceledException || error is ObjectDisposedException) { }
            if (_retirement == null)
            {
                var retired = new RuntimeSourceRetirement(Identity, _connection, _lastAccepted, reason, Volatile.Read(ref _source._stopping) != 0);
                var bytes = retired.Encode(); var time = Stopwatch.GetTimestamp();
                while (!_source._dispatcher.TrySubmit(JournalRecordKind.SourceRetirement, bytes, time, out _retirement))
                    await _source._dispatcher.WaitForCapacityAsync(bytes.Length, CancellationToken.None).ConfigureAwait(false);
            }
            await _retirement!.WaitAsync().ConfigureAwait(false);
            await _source._consumeRetirement(_retirement.Id).ConfigureAwait(false);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _callbacksDrained = _callbacks.Stop();
            _intake.Cancel(); _process.Dispose();
            _ = _callbacksDrained.ContinueWith(_ => _intake.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
