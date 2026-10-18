using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;
using Microsoft.Win32.SafeHandles;

namespace LandingStats.App.Runtime;

/// <summary>One process generation. The supervisor may replace it only after Exit has completed.</summary>
internal sealed class ProcessWorkerHost : IDisposable
{
    private readonly string _executable;
    private readonly RuntimeClockEpoch _clock;
    private readonly ulong _requiredCapabilities;
    private readonly Func<RuntimeEnvelope, long, Task> _receive;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly SemaphoreSlim _sourceWriter = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _controlWriter = new SemaphoreSlim(1, 1);
    private readonly OperationLifetime _operations = new OperationLifetime();
    private readonly object _commandGate = new object();
    private readonly Dictionary<Guid, PendingCommand> _commands = new Dictionary<Guid, PendingCommand>();
    private NamedPipeServerStream? _pipe, _controlPipe;
    private OwnedProcess? _process;
    private IDisposable? _engineUse;
    private static readonly System.Collections.Concurrent.ConcurrentBag<IDisposable> UnconfirmedEnginePins = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
    private long _sourceSentSequence, _controlSentSequence, _receivedSequence, _controlReceivedSequence, _acknowledgedSequence, _revision;
    private long _sourceCaptureQpc, _controlCaptureQpc, _lastMessageQpc;
    private ComponentHealthMessage? _lastHealth;
    private string? _failureLog;
    private Exception? _controlEnded;
    public string FailureDiagnostic { get; private set; } = "";
    private int _started, _stopping, _disposed;
    public ProcessWorkerHost(string executable, WorkerIdentity identity, RuntimeClockEpoch clock, Func<RuntimeEnvelope, long, Task> receive,
        ulong requiredCapabilities = RuntimeCapabilities.Basic)
    { _executable = Path.GetFullPath(executable); Identity = identity; _clock = clock; _receive = receive; _requiredCapabilities = requiredCapabilities; }
    public WorkerIdentity Identity { get; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public Task SourceCompletion { get; private set; } = Task.CompletedTask;
    public Task ControlCompletion { get; private set; } = Task.CompletedTask;
    public Task<int> Exit => _process?.Exit ?? Task.FromResult(0);
    public int ProcessId => _process?.Id ?? 0;
    public ulong Capabilities { get; private set; }
    public ComponentHealthMessage? LastHealth => Volatile.Read(ref _lastHealth);
    public long LastMessageQpc => Interlocked.Read(ref _lastMessageQpc);
    public long LastReceivedSequence => Interlocked.Read(ref _receivedSequence);
    public long LastAcknowledgedSequence => Interlocked.Read(ref _acknowledgedSequence);

    public async Task StartAsync(TimeSpan handshakeDeadline, string? fixture = null, CancellationToken cancellationToken = default)
    {
        using var owner = Enter();
        if (Interlocked.Exchange(ref _started, 1) != 0 || Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("Worker generation has already started or stopped.");
        var name = "ls-runtime-" + Guid.NewGuid().ToString("N");
        var tokenBytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(tokenBytes);
        var launchToken = BitConverter.ToString(tokenBytes).Replace("-", "").ToLowerInvariant();
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        using (var current = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(current.User ?? throw new InvalidOperationException("The runtime has no Windows user identity."),
                PipeAccessRights.FullControl, AccessControlType.Allow));
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536,
            security, HandleInheritability.None);
        _controlPipe = new NamedPipeServerStream(name + "-control", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384,
            security, HandleInheritability.None);
        var arguments = new List<string>
        {
            "--pipe=" + name, "--session=" + Identity.AppSessionId.ToString("N"), "--worker=" + Identity.InstanceId.ToString("N"),
            "--generation=" + Identity.Generation.ToString(CultureInfo.InvariantCulture), "--role=" + ((byte)Identity.Role).ToString(CultureInfo.InvariantCulture),
            "--clock=" + _clock.Id.ToString("N"), "--origin=" + _clock.OriginQpc.ToString(CultureInfo.InvariantCulture),
            "--frequency=" + _clock.Frequency.ToString(CultureInfo.InvariantCulture), "--token=" + launchToken,
            "--parent=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
        };
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(_executable)!, "LandingStats.Runtime.dll"))) arguments.RemoveAt(arguments.Count - 1);
        _failureLog = Path.Combine(Path.GetTempPath(), "ls-worker-" + Identity.InstanceId.ToString("N") + ".log");
        arguments.Add("--failure-log=" + _failureLog);
        if (fixture != null) arguments.Add("--fixture=" + fixture);
        try
        {
            var directory = Path.GetDirectoryName(_executable)!;
            if (File.Exists(Path.Combine(directory, "engine.json")))
                _engineUse = ProcessingEngineLease.Open(Path.GetDirectoryName(directory)!, Path.GetFileName(directory)).AcquireUse();
            var handshake = new WorkerHandshake(Identity, RuntimeBuildIdentity.Read(Path.GetDirectoryName(_executable)!), launchToken, _requiredCapabilities, _clock);
            var controlHandshake = new WorkerHandshake(Identity, RuntimeBuildIdentity.Read(Path.GetDirectoryName(_executable)!), launchToken, _requiredCapabilities, _clock);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(handshakeDeadline);
            deadline.Token.ThrowIfCancellationRequested();
            _process = OwnedProcess.Start(_executable, arguments, jobName: OwnedProcess.JobName(Identity));
            using var cancelRead = deadline.Token.Register(() => { _pipe.Dispose(); _controlPipe.Dispose(); });
            var connections = Task.WhenAll(_pipe.WaitForConnectionAsync(deadline.Token), _controlPipe.WaitForConnectionAsync(deadline.Token));
            if (await Task.WhenAny(connections, _process.Exit).ConfigureAwait(false) != connections)
                throw new IOException("Worker exited before connecting (exit " + await _process.Exit.ConfigureAwait(false) + ").");
            await connections.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var pid) || pid != _process.Id ||
                !GetNamedPipeClientProcessId(_controlPipe.SafePipeHandle, out var controlPid) || controlPid != _process.Id)
                throw new InvalidDataException("The connected pipe does not belong to the launched worker.");
            var hello = await RuntimeWire.ReadAsync(_pipe, deadline.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("Worker exited before handshake.");
            handshake.Accept(hello);
            var controlHello = await RuntimeWire.ReadAsync(_controlPipe, deadline.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("Worker control handshake is missing.");
            controlHandshake.Accept(controlHello);
            Capabilities = ((WorkerHello)hello.Payload).Capabilities;
            if (Capabilities != ((WorkerHello)controlHello.Payload).Capabilities) throw new InvalidDataException("Worker channels advertised different capabilities.");
            await ReceiveAsync(hello).ConfigureAwait(false);
            ValidateControl(controlHello);
            await SendPayloadAsync(new ControlAckMessage(controlHello.Sequence), Guid.Empty, 0, deadline.Token).ConfigureAwait(false);
            await SendPayloadAsync(new WorkerCommandMessage(WorkerCommand.Start), Guid.NewGuid(), Interlocked.Increment(ref _revision), deadline.Token).ConfigureAwait(false);
            SourceCompletion = ReadPumpAsync();
            ControlCompletion = ReadControlPumpAsync();
            Completion = FirstCompletionAsync(SourceCompletion, ControlCompletion);
        }
        catch (Exception error)
        {
            var connectionState = "data=" + Connected(_pipe) + ", control=" + Connected(_controlPipe);
            _pipe.Dispose(); _controlPipe.Dispose();
            if (_process != null) await _process.TerminateAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            ReadFailureDiagnostic();
            if (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested) throw new OperationCanceledException("Worker startup cancelled.", error, cancellationToken);
            throw new IOException("Worker startup failed (" + connectionState + "): " + error.Message + " " + FailureDiagnostic, error);
        }
    }
    private static bool Connected(NamedPipeServerStream pipe)
    { try { return pipe.IsConnected; } catch (ObjectDisposedException) { return false; } }
    private async Task ReadPumpAsync()
    {
        using var owner = Enter();
        while (true)
        {
            var message = await RuntimeWire.ReadAsync(_pipe!, _lifetime.Token).ConfigureAwait(false);
            if (message == null) return;
            await ReceiveAsync(message).ConfigureAwait(false);
        }
    }
    private async Task ReadControlPumpAsync()
    {
        using var owner = Enter();
        Exception? failure = null;
        try
        {
            while (true)
            {
                var message = await RuntimeWire.ReadAsync(_controlPipe!, _lifetime.Token).ConfigureAwait(false);
                if (message == null) return;
                ValidateControl(message);
                if (message.Payload is CommandResultMessage result)
                {
                    lock (_commandGate)
                        if (_commands.TryGetValue(message.CorrelationId, out var command))
                        {
                            if (command.Revision != message.CommandRevision) throw new InvalidDataException("Worker command revision mismatch.");
                            command.Completion.TrySetResult(result);
                        }
                }
                await SendPayloadAsync(new ControlAckMessage(message.Sequence), Guid.Empty, 0, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            lock (_commandGate)
            {
                _controlEnded = failure ?? new EndOfStreamException("Worker generation ended before command completion.");
                foreach (var completion in _commands.Values)
                    completion.Completion.TrySetException(_controlEnded);
            }
        }
    }
    private async Task ReceiveAsync(RuntimeEnvelope message)
    {
        var receipt = Stopwatch.GetTimestamp();
        if (!Identity.Equals(message.Worker) || !RuntimeSequence.IsNext(Interlocked.Read(ref _receivedSequence), message) ||
            message.CaptureQpc < _sourceCaptureQpc || message.CaptureQpc > receipt ||
            message.Payload is DurableAckMessage || message.Payload is ControlAckMessage || message.Payload is RuntimeCommand ||
            message.Payload is CommandResultMessage || message.Payload is ComponentHealthMessage)
            throw new InvalidDataException("Worker response identity or sequence is invalid.");
        Interlocked.Exchange(ref _receivedSequence, message.Sequence);
        _sourceCaptureQpc = message.CaptureQpc;
        await _receive(message, receipt).ConfigureAwait(false);
    }
    private void ValidateControl(RuntimeEnvelope message)
    {
        var receipt = Stopwatch.GetTimestamp();
        if (!Identity.Equals(message.Worker) || message.Sequence != _controlReceivedSequence + 1 || message.ConnectionGeneration != 0 ||
            message.CaptureQpc < _controlCaptureQpc || message.CaptureQpc > receipt ||
            !(message.Payload is ComponentHealthMessage || message.Payload is CommandResultMessage || message.Sequence == 1 && message.Payload is WorkerHello))
            throw new InvalidDataException("Invalid worker control stream.");
        _controlReceivedSequence = message.Sequence; _controlCaptureQpc = message.CaptureQpc;
        Interlocked.Exchange(ref _lastMessageQpc, receipt);
        if (message.Payload is ComponentHealthMessage health)
        {
            var previous = LastHealth;
            if (health.OwnerProgressQpc > message.CaptureQpc || health.OldestPendingQpc > message.CaptureQpc ||
                previous != null && (health.OwnerProgress < previous.OwnerProgress || health.UsefulProgress < previous.UsefulProgress || health.OwnerProgressQpc < previous.OwnerProgressQpc))
                throw new InvalidDataException("Worker health counters reversed or exceed capture time.");
            Volatile.Write(ref _lastHealth, health);
        }
    }
    public async Task AcknowledgeAsync(long durableSequence, CancellationToken cancellationToken = default)
    {
        using var owner = Enter();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = lifetime.Token;
        if (durableSequence <= 0 || durableSequence > LastReceivedSequence) throw new ArgumentOutOfRangeException(nameof(durableSequence));
        await _sourceWriter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (durableSequence <= _acknowledgedSequence) return;
            cancellationToken.ThrowIfCancellationRequested();
            await WriteFrameAsync(_pipe!, new RuntimeEnvelope(Identity, 0, _sourceSentSequence + 1, Stopwatch.GetTimestamp(),
                new DurableAckMessage(durableSequence))).ConfigureAwait(false);
            _sourceSentSequence++;
            Interlocked.Exchange(ref _acknowledgedSequence, durableSequence);
        }
        finally { _sourceWriter.Release(); }
    }
    public async Task<CommandResultMessage> CommandAsync(RuntimeCommand command, CancellationToken cancellationToken)
    {
        using var owner = Enter();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = lifetime.Token;
        if (Volatile.Read(ref _stopping) != 0 && !(command is WorkerCommandMessage basic && basic.Command == WorkerCommand.Stop)) throw new OperationCanceledException("Worker is stopping.");
        var correlation = Guid.NewGuid(); var revision = Interlocked.Increment(ref _revision);
        var result = new TaskCompletionSource<CommandResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_commandGate)
        {
            if (_controlEnded != null) throw new IOException("Worker control channel has ended.", _controlEnded);
            if (_commands.Count >= 128) throw new InvalidOperationException("Worker command queue is full.");
            _commands.Add(correlation, new PendingCommand(revision, result));
        }
        try
        {
            using (cancellationToken.Register(() => result.TrySetCanceled()))
            {
                await SendPayloadAsync(command, correlation, revision, cancellationToken).ConfigureAwait(false);
                return await result.Task.ConfigureAwait(false);
            }
        }
        finally { lock (_commandGate) _commands.Remove(correlation); }
    }
    private async Task SendPayloadAsync(RuntimePayload payload, Guid correlation, long revision, CancellationToken token)
    {
        await _controlWriter.WaitAsync(token).ConfigureAwait(false);
        try { await WriteOwnedAsync(payload, correlation, revision, token).ConfigureAwait(false); }
        finally { _controlWriter.Release(); }
    }
    private async Task WriteOwnedAsync(RuntimePayload payload, Guid correlation, long revision, CancellationToken token)
    {
        if (_controlPipe == null) throw new InvalidOperationException("Worker pipe has not started.");
        token.ThrowIfCancellationRequested();
        await WriteFrameAsync(_controlPipe, new RuntimeEnvelope(Identity, 0, _controlSentSequence + 1, Stopwatch.GetTimestamp(), payload, correlation, revision)).ConfigureAwait(false);
        _controlSentSequence++;
    }
    private async Task WriteFrameAsync(Stream pipe, RuntimeEnvelope envelope)
    {
        // Once started a frame is indivisible. A caller cancellation cannot leave
        // a half frame or consume a sequence number in a reusable generation.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var close = deadline.Token.Register(() => pipe.Dispose());
        try { await RuntimeWire.WriteAsync(pipe, envelope, deadline.Token).ConfigureAwait(false); }
        catch { _lifetime.Cancel(); throw; }
    }
    public async Task StopAsync(TimeSpan gracefulDeadline)
    {
        using var owner = Enter();
        Interlocked.Exchange(ref _stopping, 1);
        if (_process == null) return;
        using var deadline = new CancellationTokenSource(gracefulDeadline);
        try
        {
            if (!Exit.IsCompleted)
            {
                await CommandAsync(new WorkerCommandMessage(WorkerCommand.Stop), deadline.Token).ConfigureAwait(false);
                if (await Task.WhenAny(Exit, Task.Delay(gracefulDeadline, deadline.Token)).ConfigureAwait(false) != Exit)
                    throw new TimeoutException("Graceful worker exit expired.");
                await Exit.ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException || error is IOException || error is InvalidDataException || error is TimeoutException || error is ObjectDisposedException)
        { await _process.TerminateAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        // A role may have spawned descendants. Root exit alone is not a tree exit.
        await _process.TerminateAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }
    public Task TerminateAsync(TimeSpan exitDeadline) => _process?.TerminateAsync(exitDeadline) ?? Task.CompletedTask;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var drained = _operations.Stop();
        Interlocked.Exchange(ref _stopping, 1); _lifetime.Cancel(); _pipe?.Dispose(); _controlPipe?.Dispose();
        var pin = _engineUse; _engineUse = null;
        if (_process == null) pin?.Dispose();
        else _ = RetirePinnedProcessAsync(_process, pin);
        ReadFailureDiagnostic();
        // The pumps and accepted commands own their CTS/semaphore until they return.
        _ = drained.ContinueWith(_ => { _lifetime.Dispose(); _sourceWriter.Dispose(); _controlWriter.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static async Task RetirePinnedProcessAsync(OwnedProcess process, IDisposable? pin)
    {
        try { await process.TerminateAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); pin?.Dispose(); }
        catch { if (pin != null) UnconfirmedEnginePins.Add(pin); } // Unknown tree state retains bytes until the parent exits.
        finally { process.Dispose(); }
    }
    private IDisposable Enter() => _operations.TryEnter() ?? throw new ObjectDisposedException(nameof(ProcessWorkerHost));
    private void ReadFailureDiagnostic()
    {
        try
        {
            if (_failureLog == null || !File.Exists(_failureLog)) return;
            using (var file = File.OpenRead(_failureLog))
            using (var reader = new StreamReader(file))
            { var buffer = new char[8192]; FailureDiagnostic = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length)); }
            File.Delete(_failureLog);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static async Task FirstCompletionAsync(Task source, Task control)
    { var first = await Task.WhenAny(source, control).ConfigureAwait(false); await first.ConfigureAwait(false); }
    private sealed class PendingCommand
    {
        public PendingCommand(long revision, TaskCompletionSource<CommandResultMessage> completion) { Revision = revision; Completion = completion; }
        public long Revision { get; }
        public TaskCompletionSource<CommandResultMessage> Completion { get; }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
