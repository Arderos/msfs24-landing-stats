using System;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.App.Runtime;

/// <summary>Stops intake atomically and drains every accepted resource owner.</summary>
internal sealed class OperationLifetime
{
    private readonly object _gate = new object();
    private readonly TaskCompletionSource<bool> _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;
    private int _active;

    internal IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (_stopping) return null;
            _active++;
            return new Lease(this);
        }
    }

    internal Task Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_active == 0) _drained.TrySetResult(true);
            return _drained.Task;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _active--;
            if (_stopping && _active == 0) _drained.TrySetResult(true);
        }
    }

    private sealed class Lease : IDisposable
    {
        private OperationLifetime? _owner;
        internal Lease(OperationLifetime owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
