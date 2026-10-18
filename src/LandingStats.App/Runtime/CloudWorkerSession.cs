using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

// One connected credential epoch owns one sequential worker. A cancelled or
// failed request retires the whole session before its scratch bytes are freed.
internal sealed class CloudWorkerSession
{
    private readonly ProcessWorkerHost _host;
    private readonly OperationLifetime _callbacks = new OperationLifetime();
    private Func<RuntimeEnvelope, object>? _commit;
    private TaskCompletionSource<object>? _result;
    private readonly Task _started;
    private Task? _stopped;

    public CloudWorkerSession(string executable, WorkerIdentity identity, RuntimeClockEpoch clock, string? fixture,
        CancellationToken cancellation, Action<ProcessWorkerHost>? started)
    {
        _host = new ProcessWorkerHost(executable, identity, clock, async (message, _) =>
        {
            using var accepted = _callbacks.TryEnter() ?? throw new OperationCanceledException("Cloud session retired.");
            if (message.Payload is WorkerHello) { await _host!.AcknowledgeAsync(message.Sequence).ConfigureAwait(false); return; }
            var result = Volatile.Read(ref _result) ?? throw new InvalidDataException("Unsolicited cloud result.");
            try
            {
                var value = _commit!(message);
                await _host!.AcknowledgeAsync(message.Sequence).ConfigureAwait(false);
                result.TrySetResult(value);
            }
            catch (Exception error) { result.TrySetException(error); throw; }
        });
        _started = StartAsync(fixture, cancellation, started);
    }
    private async Task StartAsync(string? fixture, CancellationToken cancellation, Action<ProcessWorkerHost>? started)
    {
        await _host.StartAsync(TimeSpan.FromSeconds(15), fixture, cancellation).ConfigureAwait(false);
        started?.Invoke(_host);
    }
    public async Task<T> RunAsync<T>(RuntimeCommand command, Func<RuntimeEnvelope, T> commit, CancellationToken cancellation)
    {
        await _started.ConfigureAwait(false);
        if (_stopped != null) throw new IOException("Cloud session has retired.");
        var result = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commit = message => commit(message)!; Volatile.Write(ref _result, result);
        try
        {
            var accepted = await _host.CommandAsync(command, cancellation).ConfigureAwait(false);
            if (accepted.Outcome != CommandOutcome.Completed) throw new IOException("Cloud operation rejected: " + accepted.Reason);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellation.Register(() => cancelled.TrySetCanceled()))
            {
                var first = await Task.WhenAny(result.Task, _host.Completion, cancelled.Task).ConfigureAwait(false);
                if (first == cancelled.Task) cancellation.ThrowIfCancellationRequested();
                if (first == _host.Completion && !result.Task.IsCompleted)
                { await _host.Completion.ConfigureAwait(false); throw new IOException("Cloud worker ended before committing its result."); }
                return (T)await result.Task.ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _result, null); _commit = null; }
    }
    public Task StopAsync() => _stopped ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        try { await _started.ConfigureAwait(false); } catch { }
        var drained = _callbacks.Stop();
        try
        {
            while (true)
            {
                try { await _host.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); break; }
                catch (Exception error) when (error is IOException || error is TimeoutException || error is System.ComponentModel.Win32Exception)
                { await Task.Delay(250).ConfigureAwait(false); }
            }
        }
        finally { _host.Dispose(); await drained.ConfigureAwait(false); }
    }
}
