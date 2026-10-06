using System;
using System.Threading.Tasks;

namespace LandingStats.App.Settings;

// Owned by the dispatcher. Operations retain its context for snapshots and
// commits, and explicitly move filesystem work to SettingsPersistence.
internal sealed class SettingsOperationQueue
{
    private Task _pending = Task.CompletedTask;

    public Task Pending => _pending;
    public bool IsStopping { get; private set; }

    public void StopAccepting() => IsStopping = true;

    public Task<T> RunAsync<T>(Func<Task<T>> operation, T rejectedResult)
    {
        if (IsStopping) return Task.FromResult(rejectedResult);

        var previous = _pending;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Publish before invoking operation: nested enqueue and shutdown must
        // also see work whose asynchronous prefix has not yielded yet.
        _pending = completion.Task;
        return ExecuteAsync(previous, operation, completion);
    }

    private static async Task<T> ExecuteAsync<T>(
        Task previous, Func<Task<T>> operation, TaskCompletionSource<bool> completion)
    {
        try
        {
            await previous;
            return await operation();
        }
        finally
        {
            // Drain and later mutations are not poisoned by a failed save.
            completion.TrySetResult(true);
        }
    }
}
