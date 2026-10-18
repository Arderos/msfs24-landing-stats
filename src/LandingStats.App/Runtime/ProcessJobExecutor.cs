using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

/// <summary>The shared lifetime of an isolated finite job. Its supervised owner
/// supplies cancellation/deadline. Results commit before ACK, and cancellation
/// never returns ownership until the whole native job has actually exited.</summary>
internal static class ProcessJobExecutor
{
    public static async Task<T> RunAsync<T>(string executable, WorkerIdentity identity, RuntimeClockEpoch clock,
        RuntimeCommand command, Func<RuntimeEnvelope, T> commit, CancellationToken cancellationToken,
        string? fixture = null, Action<ProcessWorkerHost>? started = null, Action<WorkerIdentity>? retired = null,
        ulong requiredCapabilities = RuntimeCapabilities.Basic)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = new OperationLifetime();
        ProcessWorkerHost? host = null;
        using (host = new ProcessWorkerHost(executable, identity, clock, async (message, _) =>
        {
            using var callback = callbacks.TryEnter() ?? throw new OperationCanceledException("Worker result intake has retired.");
            try
            {
                if (message.Payload is WorkerHello) { await host!.AcknowledgeAsync(message.Sequence).ConfigureAwait(false); return; }
                var result = commit(message);
                await host!.AcknowledgeAsync(message.Sequence).ConfigureAwait(false); completion.TrySetResult(result);
            }
            catch (Exception error) { completion.TrySetException(error); throw; }
        }, requiredCapabilities))
        {
            try
            {
                await host.StartAsync(TimeSpan.FromSeconds(15), fixture, cancellationToken).ConfigureAwait(false);
                started?.Invoke(host);
                var commandTask = host.CommandAsync(command, cancellationToken);
                if (await Task.WhenAny(commandTask, host.Completion).ConfigureAwait(false) != commandTask)
                {
                    _ = commandTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    await host.Completion.ConfigureAwait(false);
                    throw new IOException("Worker ended before accepting its command.");
                }
                var accepted = await commandTask.ConfigureAwait(false);
                if (accepted.Outcome != CommandOutcome.Completed) throw new IOException("Worker job was rejected: " + accepted.Reason);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelled.TrySetCanceled()))
                {
                    var first = await Task.WhenAny(completion.Task, host.Completion, cancelled.Task).ConfigureAwait(false);
                    if (first == cancelled.Task) cancellationToken.ThrowIfCancellationRequested();
                    if (first == host.Completion && !completion.Task.IsCompleted)
                    { await host.Completion.ConfigureAwait(false); throw new IOException("Worker ended before committing its result."); }
                    return await completion.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                var drained = callbacks.Stop();
                try
                {
                    while (true)
                    {
                        try { await host.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); break; }
                        catch (Exception error) when (error is IOException || error is InvalidDataException || error is TimeoutException || error is System.ComponentModel.Win32Exception)
                        {
                            // An unconfirmed tree exit cannot become an ordinary
                            // retry. Keep ownership; the common supervisor will
                            // escalate this blocked managed owner to the App.
                            await Task.Delay(250).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    host.Dispose();
                    // A result already inside the parent's storage commit still
                    // owns its transaction after the native process has ended.
                    await drained.ConfigureAwait(false);
                }
                retired?.Invoke(identity);
            }
        }
    }
}
