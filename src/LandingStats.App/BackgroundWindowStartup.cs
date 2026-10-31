using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LandingStats.App;

internal static class BackgroundWindowStartup
{
    public static void Configure(Window window)
    {
        window.ShowActivated = false;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ConfigureFirstShow(window);
        else window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        var window = (Window)sender!;
        window.SourceInitialized -= OnSourceInitialized;
        ConfigureFirstShow(window);
    }

    private static void ConfigureFirstShow(Window window)
    {
        // ShowActivated=false preserves focus, but a newly created window can
        // still cover windows below a foreground splash screen. Adjust the
        // first native show before it becomes visible, including WPF's z-order
        // changes during Show. Later taskbar/user activation is unrestricted.
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("The startup window has no native source.");
        HwndSourceHook? hook = null;
        hook = (IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int windowPositionChanging = 0x0046;
            const uint noZOrder = 0x0004, showWindow = 0x0040;
            if (message != windowPositionChanging) return IntPtr.Zero;
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            if ((position.Flags & showWindow) == 0) return IntPtr.Zero;
            position.InsertAfter = new IntPtr(1); // HWND_BOTTOM
            position.Flags &= ~noZOrder;
            Marshal.StructureToPtr(position, lParam, false);
            source.RemoveHook(hook!);
            return IntPtr.Zero;
        };
        source.AddHook(hook);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
}
