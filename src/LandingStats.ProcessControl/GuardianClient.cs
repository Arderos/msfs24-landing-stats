using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.App.Runtime;

internal sealed class GuardianClient : IDisposable
{
    private readonly GuardianDescriptor _descriptor;
    private readonly NamedPipeClientStream _pipe;
    private readonly GuardianWire _wire;
    private readonly Func<GuardianStatus> _health;
    private readonly Action _activate;
    private readonly Func<Task<bool>> _prepare;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly SemaphoreSlim _requests = new SemaphoreSlim(1, 1);
    private GuardianSignals? _signals;
    private Process? _parent;
    private TaskCompletionSource<GuardianPacket>? _pending;
    private long _request = 1;
    private int _preparing, _disposed;
    private Task _completion = Task.CompletedTask;
    public GuardianClient(GuardianDescriptor descriptor, Func<GuardianStatus> health, Action activate, Func<Task<bool>> prepare)
    {
        _descriptor = descriptor; _health = health; _activate = activate; _prepare = prepare;
        _pipe = new NamedPipeClientStream(".", descriptor.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous); _wire = new GuardianWire(_pipe, descriptor);
    }
    public Task Completion => _completion;
    public async Task ConnectAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var abort = timeout.Token.Register(() => _pipe.Dispose());
        await _pipe.ConnectAsync(10000, timeout.Token).ConfigureAwait(false); GuardianPipes.VerifyServer(_pipe, _descriptor.Parent);
        await _wire.SendAsync(new GuardianPacket(GuardianMessageKind.Hello, 1, _descriptor.Token), timeout.Token).ConfigureAwait(false);
        var hello = await _wire.ReadAsync(timeout.Token).ConfigureAwait(false);
        if (hello.Kind != GuardianMessageKind.Ack || hello.Request != 1 || !hello.Accepted) throw new InvalidDataException("Guardian rejected the application.");
        _parent = Process.GetProcessById(_descriptor.Parent); _ = _parent.Handle;
        _signals = GuardianSignals.Open(_descriptor);
        _completion = RunAsync();
    }
    private async Task RunAsync()
    {
        var reader = ReadPumpAsync(); var writer = HealthPumpAsync();
        try
        {
            var ended = await Task.WhenAny(reader, writer).ConfigureAwait(false);
            if (!_lifetime.IsCancellationRequested)
                RuntimeDiagnosticLog.Write("runtime", "guardian_channel_ended endpoint=" + (ended == reader ? "reader" : "health_writer") + " status=" + ended.Status, ended.Exception);
            await ended.ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Cancel(); _wire.Dispose(); Volatile.Read(ref _pending)?.TrySetException(new IOException("Guardian channel closed."));
            try { await Task.WhenAll(reader, writer).ConfigureAwait(false); } catch { }
        }
    }
    private async Task ReadPumpAsync()
    {
        while (true)
        {
            var packet = await _wire.ReadAsync(_lifetime.Token).ConfigureAwait(false);
            if (packet.Kind == GuardianMessageKind.Ack)
            {
                var current = Interlocked.Read(ref _request);
                if (packet.Request > current || packet.Request <= 1) throw new InvalidDataException("Unknown guardian acknowledgement.");
                if (packet.Request == current) Volatile.Read(ref _pending)?.TrySetResult(packet);
            }
            else if (packet.Kind == GuardianMessageKind.Activate) _activate();
            else if (packet.Kind == GuardianMessageKind.PrepareRecovery)
            {
                if (Interlocked.Exchange(ref _preparing, 1) == 0)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var ready = await _prepare().ConfigureAwait(false);
                            await _wire.SendAsync(new GuardianPacket(ready ? GuardianMessageKind.RecoveryReady : GuardianMessageKind.RecoveryDeferred), _lifetime.Token).ConfigureAwait(false);
                            if (!ready) Interlocked.Exchange(ref _preparing, 0);
                        }
                        catch { /* The guardian retains its finite preparation deadline. */ }
                    });
            }
            else throw new InvalidDataException("Unexpected guardian control packet.");
        }
    }
    private async Task HealthPumpAsync()
    {
        while (true)
        {
            await _wire.SendAsync(new GuardianPacket(GuardianMessageKind.Status, status: _health()), _lifetime.Token).ConfigureAwait(false);
            await Task.Delay(250, _lifetime.Token).ConfigureAwait(false);
        }
    }
    public async Task<bool> StopAsync(GuardianStopReason reason, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var signals = _signals ?? throw new InvalidOperationException("Guardian handshake is incomplete.");
        signals.RequestStop(reason);
        var deadline = Stopwatch.StartNew();
        // Accepted intent outlives caller/channel cancellation. The parent polls
        // this event before considering any failure/restart decision.
        while (!signals.StopAcknowledged)
        {
            if (_parent!.HasExited) return true;
            if (deadline.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("Guardian did not acknowledge exit intent.");
            await Task.Delay(10).ConfigureAwait(false);
        }
        return true;
    }
    public async Task<bool> UpdateAsync(GuardianUpdateRequest update, CancellationToken token)
    {
        try { return await RequestAsync(request => new GuardianPacket(GuardianMessageKind.Update, request, update: update), token).ConfigureAwait(false); }
        catch when (_signals?.UpdateAccepted == true) { return true; }
    }
    private async Task<bool> RequestAsync(Func<long, GuardianPacket> packet, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        await _requests.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<GuardianPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = Interlocked.Increment(ref _request); Volatile.Write(ref _pending, completion);
            await _wire.SendAsync(packet(request), cancellation.Token).ConfigureAwait(false);
            // Once sent, this control operation owns its finite acknowledgement
            // deadline. Caller cancellation cannot discard a successful handoff.
            using var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            acknowledgement.CancelAfter(TimeSpan.FromSeconds(15));
            using (acknowledgement.Token.Register(() => completion.TrySetCanceled())) return (await completion.Task.ConfigureAwait(false)).Accepted;
        }
        finally { Volatile.Write(ref _pending, null); _requests.Release(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _wire.Dispose();
        _ = _completion.ContinueWith(task => { _ = task.Exception; _signals?.Dispose(); _parent?.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
