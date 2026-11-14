using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

// Opt-in: needs a real desktop adapter supporting windowed D3D9Ex FLIPEX.
// This window remains offscreen and never activates or synthesizes input.
internal static class LandingChartNativeSmokeTests
{
    internal static void Run()
    {
        var application = Application.Current;
        var previousShutdown = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var panel = new LandingChartPanel
        {
            Record = new LandingRecord
            {
                Series = Enumerable.Range(0, 5).Select(index => new LandingSeriesPoint
                {
                    TimeSeconds = index - 2, InertialFpm = -500 + index * 100,
                    IndicatedFpm = -400 + index * 80, GForce = 1 + index * .05,
                    AglFeet = 100 - index * 25, OnGround = index >= 2,
                }).ToList(),
            },
        };
        panel.SetLaneHeights(400, 80, 80, 26);
        Exception? failure = null;
        panel.RenderingFailed += (_, error) => failure = error;
        var backdrop = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        backdrop.SetBinding(Image.SourceProperty, new Binding(nameof(LandingChartPanel.OverlaySnapshot)) { Source = panel });
        var modalOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), Visibility = Visibility.Collapsed,
            Child = new Border { Background = Brushes.White, Width = 200, Height = 180,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var view = new Grid { Background = Brushes.Black };
        view.Children.Add(backdrop);
        view.Children.Add(panel);
        view.Children.Add(modalOverlay);
        var window = new Window
        {
            Width = 1000, Height = 586, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Content = view,
        };
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(handle)!;
        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000); // WS_EX_NOACTIVATE
        source.AddHook(KeepOffscreen);
        IntPtr chartHandle = IntPtr.Zero;
        try
        {
            window.Show();
            DrainDispatcher();
            Check(failure == null, "Native chart initialization failed: " + failure);
            Check(!window.IsActive && GetForegroundWindow() != handle, "Native smoke window took foreground ownership.");
            chartHandle = panel.Handle;
            Check(chartHandle != IntPtr.Zero && IsWindow(chartHandle), "The chart has no live native child.");
            Check(panel.GetPresentationInfo().Contains("D3D9Ex HAL; FLIPEX;") &&
                  panel.GetPresentationInfo().Contains("IMMEDIATE"),
                "The actual swapchain does not use hardware FLIPEX presentation.");
            var initial = panel.GetPresentationDiagnostics();
            Check(initial.PresentCalls > 0 && initial.LastError >= 0 && initial.LayerCount == 2,
                "Initial native presentation failed or retained unrequested hover layers.");

            panel.SetHoverTime(1);
            var hovered = panel.GetPresentationDiagnostics();
            Check(hovered.PresentCalls > initial.PresentCalls && hovered.UploadCalls > initial.UploadCalls && hovered.LayerCount == 5,
                "A changed synchronized hover was not uploaded and presented on every chart lane.");
            panel.SetHoverTime(1.1);
            var sameSample = panel.GetPresentationDiagnostics();
            Check(sameSample.UploadCalls == hovered.UploadCalls,
                "A synchronized hover in the same nearest sample uploaded redundant pixels.");

            panel.Visibility = Visibility.Hidden;
            DrainDispatcher();
            var hidden = panel.GetPresentationDiagnostics();
            panel.SetHoverTime(null);
            Check(panel.GetPresentationDiagnostics().UploadCalls == hidden.UploadCalls,
                "A hidden chart uploaded pixels before becoming visible.");
            panel.Visibility = Visibility.Visible;
            DrainDispatcher();
            var cleared = panel.GetPresentationDiagnostics();
            Check(cleared.LayerCount == 2 && cleared.PresentCalls > hidden.PresentCalls,
                "Restoring a hidden chart retained its old hover layers.");

            panel.Visibility = Visibility.Hidden;
            panel.SetHoverTime(-1);
            panel.Visibility = Visibility.Visible;
            DrainDispatcher();
            var restored = panel.GetPresentationDiagnostics();
            Check(restored.LayerCount == 5 && restored.UploadCalls > cleared.UploadCalls && restored.LastError >= 0,
                "Restoring a chart lost the hover time updated while hidden.");
            Check(panel.OverlaySnapshot == null, "Ordinary native hover created a displayed WPF snapshot.");

            panel.SetOverlayVisible(true);
            DrainDispatcher();
            var snapshot = panel.OverlaySnapshot;
            Check(snapshot != null && snapshot.IsFrozen && panel.Visibility == Visibility.Hidden &&
                  panel.Handle == chartHandle && IsWindow(chartHandle),
                "Opening a modal did not preserve a frozen chart background and the existing native child.");
            Check(ReferenceEquals(backdrop.Source, snapshot) && !backdrop.IsHitTestVisible,
                "The ordinary WPF image binding did not receive the frozen, noninteractive chart backdrop.");
            VerifyModalDimming(view, modalOverlay);
            var modal = panel.GetPresentationDiagnostics();
            panel.SetOverlayVisible(true);
            panel.SetHoverTime(1);
            DrainDispatcher();
            Check(ReferenceEquals(snapshot, panel.OverlaySnapshot) && panel.Visibility == Visibility.Hidden,
                "A nested modal or hidden replay hover rerasterized the frozen chart background.");
            Check(panel.GetPresentationDiagnostics().PresentCalls == modal.PresentCalls &&
                  panel.GetPresentationDiagnostics().UploadCalls == modal.UploadCalls,
                "A hidden modal chart continued native uploads or presentation.");

            panel.Mode = LandingChartMode.Attitude;
            DrainDispatcher();
            var modeSnapshot = panel.OverlaySnapshot;
            Check(modeSnapshot != null && !ReferenceEquals(snapshot, modeSnapshot),
                "A chart mode change behind a modal retained its old snapshot.");
            panel.Record = new LandingRecord
            {
                Series = Enumerable.Range(0, 5).Select(index => new LandingSeriesPoint
                {
                    TimeSeconds = index - 2, PitchDegrees = index * 2, BankDegrees = -index,
                    GForce = 1 + index * .1, AglFeet = 200 - index * 50,
                }).ToList(),
            };
            DrainDispatcher();
            var recordSnapshot = panel.OverlaySnapshot;
            Check(recordSnapshot != null && !ReferenceEquals(modeSnapshot, recordSnapshot),
                "A changed landing behind a modal retained the previous record snapshot.");
            window.Width = 1100;
            DrainDispatcher();
            var resizedSnapshot = panel.OverlaySnapshot;
            Check(resizedSnapshot != null && recordSnapshot != null &&
                  resizedSnapshot.PixelWidth > recordSnapshot.PixelWidth && panel.Visibility == Visibility.Hidden,
                "Resizing under a modal did not refresh its chart background.");
            Check(panel.GetPresentationDiagnostics().PresentCalls == modal.PresentCalls &&
                  panel.GetPresentationDiagnostics().UploadCalls == modal.UploadCalls,
                "Refreshing a modal snapshot resumed the hidden native presentation.");
            panel.SetHoverTime(1);
            modalOverlay.Visibility = Visibility.Collapsed;
            panel.SetOverlayVisible(false);
            DrainDispatcher();
            var resumed = panel.GetPresentationDiagnostics();
            Check(panel.OverlaySnapshot == null && backdrop.Source == null && panel.Visibility == Visibility.Visible &&
                  panel.Handle == chartHandle && resumed.LayerCount == 5 &&
                  resumed.PresentCalls > modal.PresentCalls && resumed.UploadCalls > modal.UploadCalls,
                "Closing the final modal did not restore the current native chart and release the snapshot.");
            Check(failure == null && !window.IsActive && GetForegroundWindow() != handle,
                "Native updates failed or changed foreground ownership.");
            Console.WriteLine("Native chart FLIPEX smoke passed: presents=" + resumed.PresentCalls +
                              ", uploads=" + resumed.UploadCalls + ", occluded=" + resumed.Occluded + ".");
        }
        finally
        {
            try { window.Close(); }
            finally
            {
                panel.Dispose();
                if (!source.IsDisposed) source.RemoveHook(KeepOffscreen);
                application.ShutdownMode = previousShutdown;
            }
        }
        Check(chartHandle != IntPtr.Zero && !IsWindow(chartHandle), "Disposing the chart left its native window alive.");
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void VerifyModalDimming(FrameworkElement view, Border overlay)
    {
        byte[] Render()
        {
            var width = (int)Math.Ceiling(view.ActualWidth);
            var height = (int)Math.Ceiling(view.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }
        var background = Render();
        overlay.Visibility = Visibility.Visible;
        DrainDispatcher();
        var dimmed = Render();
        // Find a real chart label/curve in the left quarter, away from the
        // opaque dialog. Its pixels must remain visible under the dimming layer.
        var width = (int)Math.Ceiling(view.ActualWidth);
        var brightest = -1; var brightness = -1;
        for (var y = 0; y < (int)Math.Ceiling(view.ActualHeight); y++)
            for (var x = 0; x < width / 4; x++)
            {
                var offset = (y * width + x) * 4;
                var candidate = background[offset] + background[offset + 1] + background[offset + 2];
                if (candidate > brightness) { brightness = candidate; brightest = offset; }
            }
        Check(brightest >= 0 && brightness > 180, "The modal backdrop contains no visible chart content.");
        for (var channel = 0; channel < 3; channel++)
            Check(Math.Abs(dimmed[brightest + channel] - background[brightest + channel] * 127.0 / 255) <= 2,
                "The frozen chart did not participate in WPF modal dimming or disappeared behind the dialog.");
    }

    private static IntPtr KeepOffscreen(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0046) return IntPtr.Zero; // WM_WINDOWPOSCHANGING
        var position = Marshal.PtrToStructure<WindowPosition>(lParam);
        position.Flags = (position.Flags | 0x0010u) & ~0x0004u; // NOACTIVATE, HWND_BOTTOM
        position.InsertAfter = new IntPtr(1);
        if ((position.Flags & 0x0002u) == 0) position.X = position.Y = -10000;
        Marshal.StructureToPtr(position, lParam, false);
        return IntPtr.Zero;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
}
