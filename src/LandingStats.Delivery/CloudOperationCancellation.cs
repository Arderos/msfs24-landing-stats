using System;
using System.Threading;
namespace LandingStats.App.Runtime;
/// <summary>Propagates cancellation through queued UI work as well as API jobs.
/// An already accepted settings mutation still drains before this scope exits.</summary>
internal sealed class CloudOperationCancellation : IDisposable
{
    private static readonly AsyncLocal<State?> Current = new AsyncLocal<State?>();
    private readonly State? _previous;
    public CloudOperationCancellation(CancellationToken token, Action<bool>? presentation = null)
    { _previous = Current.Value; Current.Value = new State(token, presentation); }
    public static CancellationToken Token => Current.Value?.Token ?? CancellationToken.None;
    public static IDisposable WaitForPresentation() => new Wait(Current.Value);
    public void Dispose() { Current.Value = _previous; }
    private sealed class State
    {
        public State(CancellationToken token, Action<bool>? presentation) { Token = token; Presentation = presentation; }
        public readonly CancellationToken Token;
        public readonly Action<bool>? Presentation;
        public int Waiters;
    }
    private sealed class Wait : IDisposable
    {
        private State? _state;
        public Wait(State? state) { _state = state; if (state != null && Interlocked.Increment(ref state.Waiters) == 1) state.Presentation?.Invoke(true); }
        public void Dispose() { var state = Interlocked.Exchange(ref _state, null); if (state != null && Interlocked.Decrement(ref state.Waiters) == 0) state.Presentation?.Invoke(false); }
    }
}
