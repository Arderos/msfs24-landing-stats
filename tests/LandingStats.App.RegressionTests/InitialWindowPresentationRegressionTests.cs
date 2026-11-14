using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LandingStats.App.RegressionTests;

internal static class InitialWindowPresentationRegressionTests
{
    internal static void RunAll()
    {
        var application = Application.Current;
        var previousMode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            ThreadActivationWithoutNativeForeground();
            ActivationBeforeContentRendered();
            ContentRenderedBeforeActivation();
            ApplicationActivationWhileWindowAlreadyActive();
            foreach (var transition in new[] { "deactivate", "foreground", "hide", "minimize" })
                DeferredUntilReady(transition);
            ClosedBeforeQueuedPresentation();
            ClosedBeforeAnyActivation();
        }
        finally { application.ShutdownMode = previousMode; }
    }

    private static void ThreadActivationWithoutNativeForeground()
    {
        // Exercise the production native query independently of the modeled
        // foreground used by positive lifecycle tests. WPF's thread-local
        // active state must never consume a background window's refresh.
        using var fixture = new Fixture(useNativeForeground: true);
        var original = fixture.Root.RenderCount;
        fixture.SetThreadActive(true);
        Check(fixture.Window.IsActive && GetForegroundWindow() != fixture.Handle,
            "The fixture did not separate thread-local activation from native foreground ownership.");
        DrainDispatcher();
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 0,
            "Thread-local activation consumed the initial presentation without native foreground ownership.");
        RaiseApplicationActivation();
        DrainDispatcher();
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 0,
            "An application activation notification bypassed actual native foreground ownership.");
    }

    private static void ApplicationActivationWhileWindowAlreadyActive()
    {
        using var fixture = new Fixture();
        var original = fixture.Root.RenderCount;
        var windowActivations = 0;
        fixture.Window.Activated += (_, __) => windowActivations++;
        fixture.SetThreadActive(true);
        DrainDispatcher();
        Check(fixture.Window.IsActive && fixture.RefreshRequests == 0 && fixture.Root.RenderCount == original,
            "An active background window consumed presentation before gaining the foreground.");

        // WM_ACTIVATEAPP is an about-to-activate notification. Foreground
        // ownership may change after the event, without another WPF Window
        // activation because IsActive is already true.
        RaiseApplicationActivation();
        fixture.SetForeground(true);
        Check(fixture.RefreshRequests == 0, "Application activation presented synchronously.");
        DrainDispatcher();
        Check(windowActivations == 1 && fixture.Root.RenderCount == original + 1 && fixture.RefreshRequests == 1,
            "Application foreground activation did not wake an already-active window for its first presentation.");
        VerifyFinished(fixture);
    }

    private static void ActivationBeforeContentRendered()
    {
        using var fixture = new Fixture(holdContentRendered: true);
        var original = fixture.Root.RenderCount;
        fixture.SetActive(true);
        DrainDispatcher();
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 0,
            "Activation redrew the root before its first ContentRendered notification.");
        fixture.Window.ReleaseContentRendered();
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 0, "Presentation ran synchronously inside ContentRendered.");
        DrainDispatcher();
        Check(fixture.Root.RenderCount == original + 1 && fixture.RefreshRequests == 1, "First active content did not request exactly one root redraw and native presentation.");
        VerifyFinished(fixture);
    }

    private static void ContentRenderedBeforeActivation()
    {
        using var fixture = new Fixture();
        var original = fixture.Root.RenderCount;
        DrainDispatcher();
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 0, "Inactive background startup consumed the initial presentation.");
        fixture.SetActive(true);
        Check(!fixture.Window.IsActive, "A modeled foreground notification changed native thread activation.");
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 0, "Presentation ran synchronously inside the activation notification.");
        DrainDispatcher();
        Check(fixture.Root.RenderCount == original + 1 && fixture.RefreshRequests == 1, "The first background-to-active transition did not redraw and present the root once.");
        VerifyFinished(fixture);
    }

    private static void DeferredUntilReady(string transition)
    {
        using var fixture = new Fixture();
        var original = fixture.Root.RenderCount;
        fixture.SetActive(true);
        if (transition == "deactivate") fixture.SetActive(false);
        if (transition == "foreground") fixture.SetForeground(false);
        if (transition == "hide") fixture.Window.Hide();
        if (transition == "minimize")
        {
            ShowWindow(fixture.Handle, 7); // SW_SHOWMINNOACTIVE
            Check(fixture.Window.WindowState == WindowState.Minimized, "The fixture did not enter the minimized state.");
        }
        // Exercise the visibility/minimized predicates independently of
        // activation: a delayed managed notification can arrive during either
        // transition. These notifications never request native activation.
        if (transition == "hide" || transition == "minimize") fixture.SetActive(true);
        DrainDispatcher();
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 0,
            "A queued presentation redrew after the window became unready: " + transition);

        fixture.SetActive(false);
        if (transition == "hide") fixture.Window.Show();
        if (transition == "minimize") ShowWindow(fixture.Handle, 4); // SW_SHOWNOACTIVATE
        DrainDispatcher();
        Check(!fixture.Window.IsActive && fixture.Window.IsVisible && fixture.Window.WindowState != WindowState.Minimized,
            "The fixture did not restore without activation: " + transition);
        // Hide/restore may have its own native redraw. Only the subsequent
        // active transition is owned by the one-shot presentation helper.
        var restored = fixture.Root.RenderCount;
        fixture.SetActive(true);
        Check(fixture.Root.RenderCount == restored && fixture.RefreshRequests == 0, "A deferred presentation ran inside activation: " + transition);
        DrainDispatcher();
        Check(fixture.Root.RenderCount == restored + 1 && fixture.RefreshRequests == 1,
            "A cancelled first presentation was not retried when ready: " + transition);
        VerifyFinished(fixture);
    }

    private static void ClosedBeforeQueuedPresentation()
    {
        using var fixture = new Fixture();
        var original = fixture.Root.RenderCount;
        fixture.SetActive(true);
        fixture.Close();
        RaiseApplicationActivation();
        DrainDispatcher();
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 0, "Closing the window did not cancel its queued presentation.");
        fixture.AssertContentUnchanged();
    }

    private static void ClosedBeforeAnyActivation()
    {
        using var fixture = new Fixture(holdContentRendered: true);
        var original = fixture.Root.RenderCount;
        fixture.Close();
        RaiseApplicationActivation();
        fixture.Window.ReleaseContentRendered();
        DrainDispatcher();
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 0, "A closed, never-active window retained a presentation callback.");
    }

    private static void VerifyFinished(Fixture fixture)
    {
        var original = fixture.Root.RenderCount;
        for (var n = 0; n < 3; n++)
        {
            fixture.SetActive(false);
            fixture.SetActive(true);
            fixture.Window.ReleaseContentRendered();
            RaiseApplicationActivation();
            DrainDispatcher();
        }
        Check(fixture.Root.RenderCount == original && fixture.RefreshRequests == 1, "Completed initial presentation ran again on later activation.");
        fixture.AssertContentUnchanged();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly PresentationWindow Window;
        internal readonly CountingGrid Root = new CountingGrid { Background = Brushes.Black };
        internal readonly IntPtr Handle;
        internal int RefreshRequests;
        private readonly HwndSource _source;
        private readonly HwndSourceHook _isolation;
        private readonly object _context = new object();
        private readonly TextBlock _label = new TextBlock { Text = "Existing content", Foreground = Brushes.White };
        private readonly ListBox _selection = new ListBox { Items = { "first", "selected", "last" }, SelectedIndex = 1 };
        private bool _closed;
        private IntPtr _foreground;
        private int _selectionChanges, _contextChanges;

        internal Fixture(bool holdContentRendered = false, bool useNativeForeground = false)
        {
            Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });
            Root.RowDefinitions.Add(new RowDefinition());
            Root.Children.Add(_label); Grid.SetRow(_selection, 1); Root.Children.Add(_selection);
            Root.DataContext = _context;
            Window = new PresentationWindow
            {
                Width = 640, Height = 360, WindowStyle = WindowStyle.None,
                ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                Background = Brushes.Black, Content = Root, HoldContentRendered = holdContentRendered,
            };
            if (useNativeForeground) InitialWindowPresentation.Configure(Window, Root, OnPresentationTrace);
            else ConfigureWithForegroundBoundary(Window, Root, OnPresentationTrace, () => _foreground);
            Handle = new WindowInteropHelper(Window).EnsureHandle();
            _source = HwndSource.FromHwnd(Handle)!;
            // Keep native show/hide/state transitions offscreen and inactive.
            // Activation events are raised only through managed callbacks;
            // sending WM_ACTIVATE would also make DefWindowProc set focus.
            SetWindowLong(Handle, -20, GetWindowLong(Handle, -20) | 0x08000000); // WS_EX_NOACTIVATE
            _isolation = KeepOffscreen;
            _source.AddHook(_isolation);
            try
            {
                Window.Show(); DrainDispatcher();
                Check(Window.SawContentRendered && Root.RenderCount > 0,
                    "The fixture did not complete a real WPF content render.");
                Check(!Window.IsActive, "The fixture activated during its background Show.");
                AssertNoForeground();
            }
            catch
            {
                Close();
                throw;
            }
            _selection.SelectionChanged += (_, __) => _selectionChanges++;
            Root.DataContextChanged += (_, __) => _contextChanges++;
        }

        internal void SetActive(bool active)
        {
            SetForeground(active);
            Window.NotifyActivation(active);
            AssertNoForeground();
            ClearUpdateRegion();
        }

        internal void SetForeground(bool foreground) => _foreground = foreground ? Handle : IntPtr.Zero;

        internal void SetThreadActive(bool active)
        {
            Window.SetThreadActive(active);
            Check(Window.IsActive == active, "The managed activation callback did not update WPF's thread-local state.");
            AssertNoForeground();
            ClearUpdateRegion();
        }

        private void ClearUpdateRegion()
        {
            // Discard any earlier native update region. The deferred helper
            // must create its own full-client region after the notification.
            Check(ValidateRect(Handle, IntPtr.Zero), "Cannot clear the fixture's previous native update region.");
        }

        private void OnPresentationTrace(string stage)
        {
            if (stage != "initial_surface_refresh_requested") return;
            RefreshRequests++;
            // This callback runs synchronously after InvalidateRect and before
            // WM_PAINT can consume the native region. Inspect actual Win32
            // state, independently of the managed OnRender counter.
            Check(GetClientRect(Handle, out var client) && GetUpdateRect(Handle, out var update, false) &&
                client.Right > client.Left && client.Bottom > client.Top &&
                update.Left == client.Left && update.Top == client.Top &&
                update.Right == client.Right && update.Bottom == client.Bottom,
                "Initial presentation did not invalidate the complete native client area.");
        }

        internal void AssertContentUnchanged()
        {
            Check(ReferenceEquals(Window.Content, Root) && ReferenceEquals(Root.DataContext, _context) &&
                Root.Children.Contains(_label) && _label.Text == "Existing content" && _selection.SelectedIndex == 1 &&
                _selectionChanges == 0 && _contextChanges == 0,
                "Initial presentation replaced existing visuals or changed user data/selection.");
            AssertNoForeground();
        }

        private void AssertNoForeground() => Check(GetForegroundWindow() != Handle,
            "The offscreen presentation fixture took foreground ownership.");

        internal void Close()
        {
            if (_closed) return;
            _foreground = IntPtr.Zero;
            // Never let an isolation assertion prevent disposal or replace
            // the failure from the test body with another activation attempt.
            try { Window.Close(); }
            finally
            {
                _closed = true;
                if (!_source.IsDisposed) _source.RemoveHook(_isolation);
            }
            AssertNoForeground();
        }

        public void Dispose() => Close();
    }

    // Keep the real-native-query regression runnable against a previous app
    // assembly before the new injectable constructor is needed by later cases.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ConfigureWithForegroundBoundary(Window window, UIElement surface,
        Action<string> trace, Func<IntPtr> foregroundWindow) =>
        _ = new InitialWindowPresentation(window, surface, trace, foregroundWindow);

    private static void RaiseApplicationActivation() =>
        typeof(Application).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Application.Current, new object[] { EventArgs.Empty });

    private sealed class PresentationWindow : Window
    {
        internal bool HoldContentRendered;
        internal bool SawContentRendered;
        internal void NotifyActivation(bool active)
        {
            if (active) base.OnActivated(EventArgs.Empty);
            else base.OnDeactivated(EventArgs.Empty);
        }
        internal void SetThreadActive(bool active)
        {
            // Exercise WPF's native-message state callback without invoking
            // DefWindowProc or changing keyboard focus/desktop foreground.
            typeof(Window).GetMethod("HandleActivate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(this, new object[] { active });
        }
        protected override void OnContentRendered(EventArgs args)
        {
            SawContentRendered = true;
            if (!HoldContentRendered) base.OnContentRendered(args);
        }
        internal void ReleaseContentRendered()
        {
            Check(SawContentRendered, "Cannot release a notification before WPF rendered content.");
            HoldContentRendered = false;
            base.OnContentRendered(EventArgs.Empty);
        }
    }

    private sealed class CountingGrid : Grid
    {
        internal int RenderCount;
        protected override void OnRender(DrawingContext context)
        { RenderCount++; base.OnRender(context); }
    }

    private static IntPtr KeepOffscreen(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0046) return IntPtr.Zero; // WM_WINDOWPOSCHANGING
        var position = Marshal.PtrToStructure<WindowPosition>(lParam);
        position.Flags = (position.Flags | 0x0010u) & ~0x0004u; // NOACTIVATE, allow HWND_BOTTOM
        position.InsertAfter = new IntPtr(1);
        if ((position.Flags & 0x0002u) == 0) position.X = position.Y = -10000;
        Marshal.StructureToPtr(position, lParam, false);
        return IntPtr.Zero;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUpdateRect(IntPtr window, out NativeRect rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ValidateRect(IntPtr window, IntPtr rectangle);
}
