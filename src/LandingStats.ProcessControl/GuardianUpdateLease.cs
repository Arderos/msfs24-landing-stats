using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.App.Runtime;

// Prepared helpers wait for an explicit commit event before any download or
// replacement. Until commit their entire process tree remains guardian-owned.
internal sealed class GuardianUpdateLease
{
    private readonly OwnedProcess _process;
    private readonly EventWaitHandle _commit;
    private int _committed;
    private GuardianUpdateLease(OwnedProcess process, EventWaitHandle commit) { _process = process; _commit = commit; }
    public int ProcessId => _process.Id;
    public static async Task<GuardianUpdateLease> PrepareAsync(string executable, IReadOnlyList<string> arguments, string directory,
        string readyName, string commitName, CancellationToken token)
    {
        using var ready = GuardianSignals.CreateEvent(readyName);
        var commit = GuardianSignals.CreateEvent(commitName); OwnedProcess? process = null;
        try
        {
            token.ThrowIfCancellationRequested();
            process = OwnedProcess.Start(executable, arguments, directory, hideWindow: false);
            var watch = Stopwatch.StartNew();
            while (!ready.WaitOne(0))
            {
                token.ThrowIfCancellationRequested();
                if (process.Exit.IsCompleted) throw new IOException("The verified updater exited before its ready acknowledgement.");
                if (watch.Elapsed >= TimeSpan.FromSeconds(10)) throw new TimeoutException("The verified updater did not confirm its target.");
                await Task.Delay(20, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return new GuardianUpdateLease(process, commit);
        }
        catch
        {
            try { if (process != null) await process.TerminateAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            finally { process?.Dispose(); commit.Dispose(); }
            throw;
        }
    }
    public void Commit()
    {
        if (Volatile.Read(ref _committed) != 0) throw new InvalidOperationException("Updater handoff already committed.");
        _process.Detach(() => _commit.Set()); Interlocked.Exchange(ref _committed, 1);
    }
    public async Task RetireAsync()
    {
        try { if (Volatile.Read(ref _committed) == 0) await _process.TerminateAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        finally { _process.Dispose(); _commit.Dispose(); }
    }
}
