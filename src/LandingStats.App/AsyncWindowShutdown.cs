using System;
using System.Threading.Tasks;

namespace LandingStats.App;

// Owned by the UI dispatcher. Keeping the state separate makes the close/reentry
// contract testable without constructing a window or touching user settings.
internal sealed class AsyncWindowShutdown
{
    public bool IsClosing { get; private set; }
    public bool CanClose { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;

    public Task BeginAsync(Func<Task> drain)
    {
        if (drain == null) throw new ArgumentNullException(nameof(drain));
        if (IsClosing) return Completion;
        IsClosing = true;
        Completion = CompleteAsync(drain);
        return Completion;
    }

    internal static async Task DrainAcceptedWorkAsync(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks); }
        catch
        {
            // WhenAll observes every accepted operation and waits for ALL of
            // them, including after one fails. Originating operations own their
            // diagnostics; a failed write must not abandon unrelated writes.
        }
    }

    private async Task CompleteAsync(Func<Task> drain)
    {
        // Even an idle application must leave the first Closing event before
        // requesting its final Close. This also lets the placeholder render.
        await Task.Yield();
        try { await drain(); }
        finally { CanClose = true; }
    }
}
