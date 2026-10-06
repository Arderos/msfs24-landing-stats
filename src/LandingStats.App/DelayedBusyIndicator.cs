using System;
using System.Windows.Threading;

namespace LandingStats.App;

// Dispatcher-owned presentation only: delaying a spinner never delays the work
// or the disabling of controls. Each new operation gets its own grace period.
internal sealed class DelayedBusyIndicator
{
    private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _busy;
    private object? _operation;
    private Action? _refresh;

    public DelayedBusyIndicator() => _timer.Tick += OnElapsed;
    public bool IsVisible { get; private set; }

    public bool Update(bool busy, object? operation, Action refresh)
    {
        if (!busy) Cancel();
        else if (!_busy || !Equals(_operation, operation))
        {
            Cancel();
            _busy = true;
            _operation = operation;
            _refresh = refresh;
            _timer.Start();
        }
        return IsVisible;
    }

    public void Cancel()
    {
        _timer.Stop();
        _busy = false;
        _operation = null;
        _refresh = null;
        IsVisible = false;
    }

    private void OnElapsed(object? sender, EventArgs eventArgs)
    {
        _timer.Stop();
        if (!_busy) return;
        IsVisible = true;
        var refresh = _refresh;
        _refresh = null;
        refresh?.Invoke();
    }
}
