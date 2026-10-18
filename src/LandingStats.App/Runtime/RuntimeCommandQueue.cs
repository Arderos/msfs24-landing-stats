using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class RuntimeCommandOutcome
{
    public RuntimeCommandOutcome(RuntimeEnvelope request, CommandResultMessage result) { Request = request; Result = result; }
    public RuntimeEnvelope Request { get; }
    public CommandResultMessage Result { get; }
    public byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var request = RuntimeWire.Encode(Request);
        var response = RuntimeWire.Encode(new RuntimeEnvelope(Request.Worker, 0, 1, 0, Result, Request.CorrelationId, Request.CommandRevision));
        writer.Write(request.Length); writer.Write(request); writer.Write(response.Length); writer.Write(response); return stream.ToArray();
    }
    public static RuntimeCommandOutcome Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        RuntimeEnvelope Read() => RuntimeWire.Decode(RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, 65536)));
        var request = Read(); var response = Read();
        if (!(request.Payload is RuntimeCommand) || !(response.Payload is CommandResultMessage result) || !request.Worker.Equals(response.Worker) ||
            request.CorrelationId != response.CorrelationId || request.CommandRevision != response.CommandRevision || stream.Position != stream.Length)
            throw new InvalidDataException("Command outcome crossed its request context.");
        return new RuntimeCommandOutcome(request, result);
    }
}

[DataContract]
internal sealed class RuntimeCommandBatch
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public Guid Input { get; set; }
    [DataMember(Order = 3)] public long Segment { get; set; }
    [DataMember(Order = 4)] public long Sequence { get; set; }
    [DataMember(Order = 5)] public byte[][] Commands { get; set; } = Array.Empty<byte[]>();
    public JournalCursor Cursor => new JournalCursor(Segment, Sequence);
}
[DataContract]
internal sealed class RuntimeCommandProgress
{
    [DataMember(Order = 1)] public Guid Input { get; set; }
    [DataMember(Order = 2)] public int Completed { get; set; }
}

/// <summary>Commands are durable effects of one accepted input. A lost reply may
/// retry the same logical operation, so role handlers must be idempotent. A batch
/// is collected only after both its replies and its recording checkpoint exist.</summary>
internal sealed class RuntimeCommandQueue
{
    private readonly object _gate = new object();
    private readonly string _root;
    private readonly Func<WorkerIdentity, ProcessWorkerHost?> _host;
    private readonly Func<RuntimeCommandOutcome, Task> _commitOutcome;
    private ManagedComponentContext? _owner;
    private JournalCursor? _checkpoint;
    private TaskCompletionSource<bool>? _drain;
    private const int MaximumBatches = 1024;
    public RuntimeCommandQueue(string root, Func<WorkerIdentity, ProcessWorkerHost?> host, Func<RuntimeCommandOutcome, Task> commitOutcome)
    { _root = Path.GetFullPath(root); _host = host; _commitOutcome = commitOutcome; }
    private string BatchPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".json");
    private string ProgressPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".progress");
    public void Commit(DurableRuntimeInput input, IReadOnlyList<RuntimeCommandDispatch> commands)
    {
        if (commands.Count == 0) return;
        if (commands.Count > 32) throw new InvalidDataException("Recording command batch exceeds its bound.");
        var batch = new RuntimeCommandBatch { Input = input.Input.Id, Segment = input.Cursor.Segment, Sequence = input.Cursor.Sequence,
            Commands = commands.Select((item, index) => RuntimeWire.Encode(new RuntimeEnvelope(item.Worker, 0, index + 1, input.Input.AcceptedQpc,
                item.Command, StableRuntimeIdentity.Command(input.Input.Id, index), 1))).ToArray() };
        lock (_gate)
        {
            if (File.Exists(BatchPath(batch.Input)))
            {
                var existing = Read(batch.Input);
                if (existing.Cursor.CompareTo(batch.Cursor) != 0 || existing.Commands.Length != batch.Commands.Length ||
                    existing.Commands.Where((bytes, index) => !bytes.SequenceEqual(batch.Commands[index])).Any())
                    throw new InvalidDataException("Recording command identity was reused with another effect.");
            }
            else
            {
                if (Enumerate().Length >= MaximumBatches) throw new IOException("Recording command queue is full; inputs remain in the journal.");
                DurableFile.WriteJson(BatchPath(batch.Input), batch);
            }
            _owner?.SignalWork();
        }
    }
    private Guid[] Enumerate() => Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.json")
        .Select(Path.GetFileNameWithoutExtension).Where(name => Guid.TryParseExact(name, "N", out _)).Select(Guid.Parse).ToArray() : Array.Empty<Guid>();
    private RuntimeCommandBatch Read(Guid id)
    {
        var batch = DurableFile.ReadJson<RuntimeCommandBatch>(BatchPath(id), 2 * 1024 * 1024);
        if (batch.Version != 1 || batch.Input != id || batch.Commands == null || batch.Commands.Length == 0 || batch.Commands.Length > 32)
            throw new InvalidDataException("Invalid recording command batch.");
        _ = batch.Cursor;
        for (var i = 0; i < batch.Commands.Length; i++)
        {
            var command = RuntimeWire.Decode(batch.Commands[i]);
            if (!(command.Payload is RuntimeCommand) || command.CorrelationId != StableRuntimeIdentity.Command(id, i))
                throw new InvalidDataException("Recording command identity differs from its batch.");
        }
        return batch;
    }
    private int Completed(RuntimeCommandBatch batch)
    {
        if (!File.Exists(ProgressPath(batch.Input))) return 0;
        var progress = DurableFile.ReadJson<RuntimeCommandProgress>(ProgressPath(batch.Input), 1024);
        if (progress.Input != batch.Input || progress.Completed < 0 || progress.Completed > batch.Commands.Length)
            throw new InvalidDataException("Invalid recording command progress.");
        return progress.Completed;
    }
    public void Collect(JournalCursor checkpoint)
    {
        lock (_gate)
            foreach (var id in Enumerate())
            {
                var batch = Read(id);
                if (batch.Cursor.CompareTo(checkpoint) <= 0 && Completed(batch) == batch.Commands.Length)
                { File.Delete(BatchPath(id)); File.Delete(ProgressPath(id)); }
            }
    }
    public void CheckpointCommitted(JournalCursor checkpoint)
    { lock (_gate) { if (_checkpoint == null || checkpoint.CompareTo(_checkpoint) > 0) _checkpoint = checkpoint; _owner?.SignalWork(); } }
    public Task DrainAsync()
    {
        lock (_gate)
        {
            if (_drain == null || _drain.Task.IsCompleted) _drain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _owner?.SignalWork(); return _drain.Task;
        }
    }
    public async Task RunAsync(ManagedComponentContext owner)
    {
        lock (_gate) { if (_owner != null) throw new InvalidOperationException("Commands already have an owner."); _owner = owner; }
        try
        {
            owner.MarkReady();
            while (true)
            {
                owner.CancellationToken.ThrowIfCancellationRequested();
                RuntimeCommandBatch? batch; int index;
                lock (_gate)
                {
                    if (_checkpoint != null) Collect(_checkpoint);
                    batch = Enumerate().Select(Read).Where(item => Completed(item) < item.Commands.Length).OrderBy(item => item.Segment).ThenBy(item => item.Sequence).FirstOrDefault();
                    index = batch == null ? 0 : Completed(batch);
                    if (batch == null) _drain?.TrySetResult(true);
                }
                if (batch == null)
                { owner.Progress(false, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); continue; }
                var request = RuntimeWire.Decode(batch.Commands[index]); var host = _host(request.Worker);
                owner.Progress(false, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: request.CorrelationId);
                CommandResultMessage result;
                if (host == null || host.Exit.IsCompleted)
                    result = new CommandResultMessage(CommandOutcome.Rejected, "source_retired");
                else
                    result = await host.CommandAsync((RuntimeCommand)request.Payload, owner.CancellationToken).ConfigureAwait(false);
                await owner.WaitForDependencyAsync(_commitOutcome(new RuntimeCommandOutcome(request, result)), DependencyWait.Storage).ConfigureAwait(false);
                lock (_gate) DurableFile.WriteJson(ProgressPath(batch.Input), new RuntimeCommandProgress { Input = batch.Input, Completed = index + 1 });
                owner.Progress(true);
            }
        }
        finally { lock (_gate) _owner = null; }
    }
}
