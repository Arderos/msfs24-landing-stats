using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LandingStats.App.Controls;
using LandingStats.App.Models;

namespace LandingStats.App.RegressionTests;

// Runs the actual HwndHost/render/update code with a scripted API boundary.
// The child is a STATIC window: no GPU, driver reset, synthetic mouse input,
// application startup, crash log or simulator profile is involved.
internal static class LandingChartRecoveryRegressionTests
{
    private const uint Paint = 0x000f, MouseMove = 0x0200;
    private const int OutOfMemory = unchecked((int)0x8007000e);
    private static readonly int[] RecoverableErrors =
    {
        unchecked((int)0x88760868), // DEVICELOST
        unchecked((int)0x88760869), // DEVICENOTRESET
        unchecked((int)0x88760870), // DEVICEREMOVED
        unchecked((int)0x88760827), // DRIVERINTERNALERROR
        unchecked((int)0x88760874), // DEVICEHUNG
        unchecked((int)0x8876017c), // OUTOFVIDEOMEMORY
        OutOfMemory,
    };

    internal static void RunAll()
    {
        NativeWindowUsesTheRasterizedThemeBackground();
        RecoverablePresentationFailuresResumeOnAnUnchangedHover();
        InitialUploadFailureResumesOnPaintOrMouse();
        FailedLayerRemovalRestoresTheLatestFrame();
        HiddenAndModalChangesSurviveDeviceUnavailability();
        DeferredPresentationRemainsPending();
        PermanentFailuresAreReportedOnce();
    }

    private static void NativeWindowUsesTheRasterizedThemeBackground()
    {
        using var fixture = new Fixture();
        fixture.Show();
        var pixels = fixture.Backend.Layers[0];
        var rasterArgb = (uint)pixels[3] << 24 | (uint)pixels[2] << 16 | (uint)pixels[1] << 8 | pixels[0];
        Check(fixture.Backend.BackgroundArgb == rasterArgb,
            "The native initial paint/clear color differs from the chart scene's actual theme background.");
    }

    private static void RecoverablePresentationFailuresResumeOnAnUnchangedHover()
    {
        using var fixture = new Fixture();
        fixture.Show();
        fixture.Panel.SetHoverTime(0);
        var originalPixels = fixture.Backend.Layers.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone());
        foreach (var error in RecoverableErrors)
        {
            var transitions = fixture.Availability.Count;
            var reports = fixture.Reports.Count;
            fixture.Backend.PresentResults.Enqueue(error);
            fixture.Backend.Emit(Paint);
            Check(fixture.Failures.Count == 0 && fixture.Panel.IsVisible &&
                  fixture.Availability.Skip(transitions).SequenceEqual(new[] { false }),
                "A recoverable Present result permanently disabled or hid the chart: " + Hex(error));
            var attempts = fixture.Backend.PresentCalls;
            Drain();
            Check(fixture.Backend.PresentCalls == attempts,
                "A transient failure scheduled a rendering retry without an external event.");

            fixture.Backend.PresentResults.Enqueue(error);
            fixture.Panel.SetHoverTime(0);
            Check(fixture.Backend.PresentCalls == attempts + 1 && fixture.Availability.Count == transitions + 1 &&
                  fixture.Reports.Count == reports + 1,
                "An unchanged hover lost pending presentation or repeatedly announced the same outage.");
            fixture.Panel.SetHoverTime(0);
            Check(fixture.Backend.PresentCalls == attempts + 2 &&
                  fixture.Availability.Skip(transitions).SequenceEqual(new[] { false, true }) &&
                  fixture.Backend.Layers.Count == 5,
                "A later successful presentation failed to restore the current chart and availability.");
            Check(originalPixels.All(pair => fixture.Backend.Layers[pair.Key].SequenceEqual(pair.Value)),
                "A transient presentation failure corrupted the retained static or hover pixels.");
            fixture.Panel.SetHoverTime(0);
            Check(fixture.Backend.PresentCalls == attempts + 2,
                "A recovered chart retained unnecessary presentation work for an unchanged sample.");
        }
    }

    private static void InitialUploadFailureResumesOnPaintOrMouse()
    {
        foreach (var notification in new[] { Paint, MouseMove })
        {
            using var fixture = new Fixture();
            fixture.Backend.UploadResults.Enqueue(0);
            fixture.Backend.UploadResults.Enqueue(OutOfMemory);
            fixture.Show();
            Check(fixture.Backend.UploadCalls >= 2 && fixture.Backend.PresentCalls == 0 &&
                  fixture.Failures.Count == 0 && fixture.Availability.SequenceEqual(new[] { false }),
                "An interrupted first layer batch did not remain recoverable before the first frame.");
            var partialStaticPixels = (byte[])fixture.Backend.Layers[0].Clone();
            var handle = fixture.Panel.Handle;
            fixture.Backend.Emit(notification);
            Check(fixture.Backend.PresentCalls == 1 && fixture.Backend.Layers.Count >= 2 &&
                  fixture.Backend.SuccessfulLayerUploads.Count(id => id == 0) == 2 &&
                  fixture.Panel.Handle == handle && fixture.Availability.SequenceEqual(new[] { false, true }),
                "Paint/mouse input could not rebuild all layers after a partially uploaded initial frame.");
            Check(fixture.Backend.Layers[0].SequenceEqual(partialStaticPixels),
                "Rebuilding a partial batch changed the already rasterized static chart content.");
        }
    }

    private static void FailedLayerRemovalRestoresTheLatestFrame()
    {
        using var fixture = new Fixture();
        fixture.Show();
        fixture.Panel.SetHoverTime(0);
        Check(fixture.Backend.Layers.Count == 5, "The layer-removal fixture has no hover overlays.");
        fixture.Backend.RemoveResults.Enqueue(OutOfMemory);
        fixture.Panel.SetHoverTime(null);
        Check(fixture.Failures.Count == 0 && fixture.Availability.SequenceEqual(new[] { false }),
            "A transient layer-update failure entered the permanent error path.");
        fixture.Backend.Emit(Paint);
        Check(fixture.Backend.Layers.Count == 2 && fixture.Availability.SequenceEqual(new[] { false, true }),
            "Recovery retained old hover layers after the managed scene had already removed them.");
    }

    private static void HiddenAndModalChangesSurviveDeviceUnavailability()
    {
        using var fixture = new Fixture();
        fixture.Show();
        var previousStaticPixels = (byte[])fixture.Backend.Layers[0].Clone();
        fixture.Backend.PresentResults.Enqueue(RecoverableErrors[0]);
        fixture.Backend.Emit(Paint);
        fixture.Panel.SetOverlayVisible(true);
        Drain();
        var attempts = fixture.Backend.PresentCalls;
        fixture.Panel.Record = Record(300);
        fixture.Panel.SetHoverTime(1);
        Drain();
        Check(fixture.Panel.OverlaySnapshot != null && fixture.Backend.PresentCalls == attempts,
            "An unavailable chart lost its modal backdrop or presented through the modal overlay.");
        fixture.Panel.SetOverlayVisible(false);
        Drain();
        Check(fixture.Failures.Count == 0 && fixture.Panel.IsVisible && fixture.Panel.OverlaySnapshot == null &&
              fixture.Backend.Layers.Count == 5 && fixture.Availability.SequenceEqual(new[] { false, true }),
            "Closing the modal failed to restore the latest scene after device unavailability.");
        Check(!fixture.Backend.Layers[0].SequenceEqual(previousStaticPixels),
            "Device recovery restored stale static pixels after a record change behind the modal.");
    }

    private static void DeferredPresentationRemainsPending()
    {
        using var fixture = new Fixture();
        fixture.Show();
        fixture.Panel.SetHoverTime(0);
        foreach (var result in new[] { 1, 0x08760878, 0x08760877 }) // S_FALSE, OCCLUDED, MODE_CHANGED
        {
            fixture.Backend.PresentResults.Enqueue(result);
            fixture.Backend.Emit(Paint);
            var attempts = fixture.Backend.PresentCalls;
            fixture.Panel.SetHoverTime(0);
            Check(fixture.Backend.PresentCalls == attempts + 1 && fixture.Failures.Count == 0,
                "A deferred frame was treated as displayed or as a permanent failure.");
        }
    }

    private static void PermanentFailuresAreReportedOnce()
    {
        using var fixture = new Fixture();
        fixture.Show();
        fixture.Backend.PresentResults.Enqueue(unchecked((int)0x8876086c)); // D3DERR_INVALIDCALL
        fixture.Backend.Emit(Paint);
        Drain();
        var attempts = fixture.Backend.PresentCalls;
        fixture.Backend.Emit(MouseMove);
        fixture.Panel.SetHoverTime(1);
        Drain();
        Check(fixture.Failures.Count == 1 && fixture.Reports.Count == 1 && fixture.Backend.PresentCalls == attempts,
            "A permanent renderer contract failure was suppressed or retried as a temporary GPU outage.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ScriptedPresentation Backend = new ScriptedPresentation();
        internal readonly List<Exception> Failures = new List<Exception>();
        internal readonly List<bool> Availability = new List<bool>();
        internal readonly List<string> Reports = new List<string>();
        internal readonly LandingChartPanel Panel;
        private readonly Window _window;
        private readonly HwndSource _source;
        private readonly ShutdownMode _shutdownMode;

        internal Fixture()
        {
            _shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Panel = new LandingChartPanel(Backend, (category, _) => Reports.Add(category))
            {
                Record = Record(),
            };
            Panel.SetLaneHeights(280, 70, 70, 26);
            Panel.RenderingFailed += (_, error) => Failures.Add(error);
            Panel.RenderingAvailabilityChanged += (_, available) => Availability.Add(available);
            _window = new Window
            {
                Width = 800, Height = 446, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                Content = Panel,
            };
            var handle = new WindowInteropHelper(_window).EnsureHandle();
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
            _source = HwndSource.FromHwnd(handle)!;
            _source.AddHook(KeepOffscreen);
        }

        internal void Show()
        {
            _window.Show();
            Drain();
            Check(!_window.IsActive && GetForegroundWindow() != new WindowInteropHelper(_window).Handle,
                "A recovery test window took foreground ownership.");
        }

        public void Dispose()
        {
            var child = Panel.Handle;
            try { _window.Close(); }
            finally
            {
                Panel.Dispose();
                if (!_source.IsDisposed) _source.RemoveHook(KeepOffscreen);
                Application.Current.ShutdownMode = _shutdownMode;
            }
            Check(child == IntPtr.Zero || !IsWindow(child), "Disposing a recovering chart leaked its child window.");
        }
    }

    private sealed class ScriptedPresentation : IChartPresentation
    {
        internal readonly Queue<int> PresentResults = new Queue<int>();
        internal readonly Queue<int> UploadResults = new Queue<int>();
        internal readonly Queue<int> RemoveResults = new Queue<int>();
        internal readonly Dictionary<int, byte[]> Layers = new Dictionary<int, byte[]>();
        internal readonly List<int> SuccessfulLayerUploads = new List<int>();
        internal int PresentCalls, UploadCalls;
        internal uint BackgroundArgb;
        private ChartInputCallback? _callback;

        public IntPtr Create(IntPtr parent, ChartInputCallback callback, uint backgroundArgb)
        {
            _callback = callback;
            BackgroundArgb = backgroundArgb;
            return CreateWindowEx(0, "STATIC", string.Empty, 0x40000000, 0, 0, 1, 1,
                parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        public void Destroy(IntPtr window) { DestroyWindow(window); _callback = null; }
        public int LastError(IntPtr window) => Marshal.GetHRForLastWin32Error();
        public int SetCursor(IntPtr window, int kind) => 0;
        public int GetPathInfo(IntPtr window, StringBuilder buffer, uint characters) => 0;
        public int GetDiagnostics(IntPtr window, out ChartPresentationDiagnostics diagnostics, uint size)
        { diagnostics = default; return 0; }
        public int Present(IntPtr window, uint reason)
        { PresentCalls++; return Next(PresentResults); }
        public int UploadLayer(IntPtr window, int id, IntPtr bgra, int width, int height, int stride, float x, float y)
        {
            UploadCalls++;
            var result = Next(UploadResults);
            if (result < 0) return result;
            var pixels = new byte[checked(stride * height)];
            Marshal.Copy(bgra, pixels, 0, pixels.Length);
            Layers[id] = pixels;
            SuccessfulLayerUploads.Add(id);
            return result;
        }
        public int RemoveLayer(IntPtr window, int id)
        {
            var result = Next(RemoveResults);
            if (result >= 0) Layers.Remove(id);
            return result;
        }
        internal void Emit(uint message) => _callback!(IntPtr.Zero, message, 180, 100, 0, 0);
        private static int Next(Queue<int> results) => results.Count == 0 ? 0 : results.Dequeue();
    }

    private static LandingRecord Record(double verticalSpeedOffset = 0) => new LandingRecord
    {
        Series = Enumerable.Range(0, 5).Select(index => new LandingSeriesPoint
        {
            TimeSeconds = index - 2, InertialFpm = -500 + index * 100 + verticalSpeedOffset,
            IndicatedFpm = -400 + index * 80, GForce = 1 + index * .05,
            AglFeet = 100 - index * 25, OnGround = index >= 2,
        }).ToList(),
    };

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static string Hex(int result) => "0x" + unchecked((uint)result).ToString("X8");
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static IntPtr KeepOffscreen(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0046) return IntPtr.Zero;
        var position = Marshal.PtrToStructure<WindowPosition>(lParam);
        position.Flags = (position.Flags | 0x0010u) & ~0x0004u;
        position.InsertAfter = new IntPtr(1);
        if ((position.Flags & 0x0002u) == 0) position.X = position.Y = -10000;
        Marshal.StructureToPtr(position, lParam, false);
        return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
}
