using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LandingStats.App;

// Isolates recovery from an incomplete initial WPF/DWM presentation. Configure
// before Show with the existing opaque root that fills the whole client area.
// The original trigger is unknown; this requests one complete render
// and presentation when the user first sees the active window.
internal sealed class InitialWindowPresentation
{
    private readonly Window _window;
    private readonly UIElement _surface;
    private readonly Action<string>? _trace;
    private readonly Application? _application;
    private readonly Func<IntPtr> _foregroundWindow;
    private DispatcherOperation? _pending;
    private bool _contentRendered, _finished;

    internal InitialWindowPresentation(Window window, UIElement surface, Action<string>? trace,
        Func<IntPtr> foregroundWindow)
    {
        _window = window;
        _surface = surface;
        _trace = trace;
        _foregroundWindow = foregroundWindow;
        _application = Application.Current;
        window.ContentRendered += OnContentRendered;
        window.Activated += OnReadyChanged;
        // A thread's active window need not own the desktop foreground.
        // WPF suppresses Window.Activated while IsActive is true;
        // Application.Activated also observes the later WM_ACTIVATEAPP.
        if (_application != null) _application.Activated += OnReadyChanged;
        window.StateChanged += OnReadyChanged;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.Closed += OnClosed;
    }

    internal static void Configure(Window window, UIElement opaqueRoot, Action<string>? trace = null) =>
        _ = new InitialWindowPresentation(window, opaqueRoot, trace, GetForegroundWindow);

    private bool Ready
    {
        get
        {
            if (!_contentRendered || !_window.IsVisible ||
                _window.WindowState == WindowState.Minimized) return false;
            var handle = new WindowInteropHelper(_window).Handle;
            return handle != IntPtr.Zero && _foregroundWindow() == handle;
        }
    }

    private void OnContentRendered(object? sender, EventArgs args)
    {
        if (!_contentRendered) _trace?.Invoke("first_content_rendered");
        _contentRendered = true;
        QueuePresentation();
    }

    private void OnReadyChanged(object? sender, EventArgs args) => QueuePresentation();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => QueuePresentation();

    private void QueuePresentation()
    {
        if (_finished || _pending != null || !_contentRendered) return;
        // Unwind native activation and let queued layout/render work finish.
        // The foreground can change after the activation notification, or be
        // lost before this callback. Check actual readiness only after unwind.
        _pending = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Present));
    }

    private void Present()
    {
        _pending = null;
        if (_finished || !Ready) return;
        Finish();
        // Window.InvalidateVisual does not redraw its content's background.
        // Refresh the full-size opaque root to regenerate the retained surface,
        // then invalidate the HWND to present the entire client area as well.
        _surface.InvalidateVisual();
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
            App.WriteCrashLog("window-presentation", new InvalidOperationException("The visible window has no native presentation source."));
        else if (!InvalidateRect(handle, IntPtr.Zero, false))
            App.WriteCrashLog("window-presentation", new Win32Exception(Marshal.GetLastWin32Error(), "Could not invalidate the complete window presentation."));
        else
            _trace?.Invoke("initial_surface_refresh_requested");
    }

    private void OnClosed(object? sender, EventArgs args) => Finish();

    private void Finish()
    {
        _finished = true;
        _pending?.Abort();
        _pending = null;
        _window.ContentRendered -= OnContentRendered;
        _window.Activated -= OnReadyChanged;
        if (_application != null) _application.Activated -= OnReadyChanged;
        _window.StateChanged -= OnReadyChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.Closed -= OnClosed;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
}
