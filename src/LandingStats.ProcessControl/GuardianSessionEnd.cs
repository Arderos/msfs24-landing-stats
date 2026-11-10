using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LandingStats.App.Runtime;

// WPF shuts down on WM_QUERYENDSESSION. Keep guardian ownership until Windows
// confirms WM_ENDSESSION, and restart the drained App if Windows cancels it.
internal sealed class GuardianSessionEnd : IDisposable
{
    private readonly object _gate = new object();
    private TaskCompletionSource<bool>? _decision;
    private readonly TaskCompletionSource<IntPtr> _ready = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Func<Task?> _application;
    private readonly Func<Task>? _requestApplicationStop;
    private readonly Func<bool>? _applicationStoppingForSession;
    internal GuardianSessionEnd(Func<Task?> application, Func<Task>? requestApplicationStop = null, Func<bool>? applicationStoppingForSession = null)
    {
        _application = application;
        _requestApplicationStop = requestApplicationStop;
        _applicationStoppingForSession = applicationStoppingForSession;
        _thread = new Thread(() =>
        {
            try
            {
                using var window = new SessionWindow(this);
                _ready.TrySetResult(window.Handle);
                Application.Run();
            }
            catch (Exception error) { _ready.TrySetException(error); lock (_gate) _decision?.TrySetException(error); }
        }) { IsBackground = true, Name = "LS session notifications" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
        _ = _ready.Task.GetAwaiter().GetResult();
    }
    internal IntPtr Handle => _ready.Task.GetAwaiter().GetResult();
    internal void BeginApplication()
    {
        lock (_gate)
            if (_decision?.Task.IsCompleted == true) _decision = null;
    }
    internal Task<bool> ConfirmAsync()
    {
        lock (_gate)
        {
            // Restart Manager may close only App. Windows owes this window an
            // ENDSESSION only after it received QUERYENDSESSION itself. An
            // unobserved query is therefore an ordinary terminal App stop.
            return _decision?.Task ?? Task.FromResult(true);
        }
    }
    private static TaskCompletionSource<bool> NewDecision() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Query() { lock (_gate) { if (_decision == null || _decision.Task.IsCompleted) _decision = NewDecision(); } }
    private void End(bool confirmed)
    {
        lock (_gate)
        {
            _decision?.TrySetResult(confirmed);
            // Windows can cancel before it queries App at all. Retain false
            // only for an App that acknowledged this session stop; otherwise
            // it must not restart that live App on a later, unrelated close.
            if (!confirmed && _applicationStoppingForSession?.Invoke() == false) _decision = null;
        }
        if (!confirmed) return;
        // Match the App's bounded shutdown drain; do not kill its tree as soon
        // as the query arrives. Returning permits Windows to terminate us.
        try
        {
            // Restart Manager may target only the resident launcher. A
            // confirmed end must explicitly drain its App over the existing
            // authenticated control channel before Windows retires the job.
            var request = _requestApplicationStop?.Invoke() ?? Task.CompletedTask;
            Task.WhenAll(request, _application() ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(15));
        }
        catch (AggregateException error) { System.Diagnostics.Debug.WriteLine(error); }
    }
    public void Dispose()
    {
        PostMessage(Handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
        _thread.Join();
    }
    private sealed class SessionWindow : NativeWindow, IDisposable
    {
        private readonly GuardianSessionEnd _owner;
        internal SessionWindow(GuardianSessionEnd owner)
        { _owner = owner; CreateHandle(new CreateParams { Caption = "Landing Stats session owner" }); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0011) { _owner.Query(); message.Result = new IntPtr(1); return; }
            if (message.Msg == 0x0016) { _owner.End(message.WParam != IntPtr.Zero); message.Result = IntPtr.Zero; return; }
            if (message.Msg == 0x0010) { Application.ExitThread(); return; }
            base.WndProc(ref message);
        }
        public void Dispose() => DestroyHandle();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
