using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandingStats.App.Models;

namespace LandingStats.App.Controls;

// One child HWND presents the complete interactive chart block. The chart scene
// is detached from WPF's displayed tree, including hover text and the timeline.
public sealed class LandingChartPanel : HwndHost
{
    private static readonly DependencyPropertyKey OverlaySnapshotPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(OverlaySnapshot), typeof(BitmapSource), typeof(LandingChartPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty OverlaySnapshotProperty = OverlaySnapshotPropertyKey.DependencyProperty;
    private const uint WmSize = 0x0005, WmPaint = 0x000f, WmCancelMode = 0x001f,
        WmDisplayChange = 0x007e, WmDpiChangedAfterParent = 0x02e3, WmShowWindow = 0x0018;
    private const uint WmMouseMove = 0x0200, WmLeftDown = 0x0201, WmLeftUp = 0x0202,
        WmLeftDouble = 0x0203, WmRightDown = 0x0204, WmCaptureChanged = 0x0215, WmLeave = 0x02a3;
    private readonly ChartPanelScene _scene = new ChartPanelScene();
    private readonly ChartInputCallback _callback;
    private readonly IChartPresentation _presentation;
    private readonly Action<string, Exception?> _reportError;
    private readonly ToolTip _helpTip = new ToolTip { Placement = PlacementMode.MousePoint, MaxWidth = 390 };
    private IntPtr _window;
    private bool _nativeWindow, _pendingRebuild, _rebuildRequested, _destroying, _failed, _built, _dispatching;
    private bool _hoverPending;
    private bool _presentPending, _presentationAvailable = true;
    private bool _overlayVisible;
    private double? _pendingHoverTime;
    private double _dpiX = 96, _dpiY = 96, _lastX, _lastY;

    public event EventHandler<LandingChartHoverEventArgs>? HoverTimeChanged;
    public event EventHandler<LandingChartZoomEventArgs>? ZoomRangeSelected;
    internal event EventHandler<Exception>? RenderingFailed;
    internal event EventHandler<bool>? RenderingAvailabilityChanged;

    public LandingChartPanel() : this(new Native()) { }

    internal LandingChartPanel(IChartPresentation presentation, Action<string, Exception?>? reportError = null)
    {
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _reportError = reportError ?? App.WriteCrashLog;
        _callback = ReceiveInput;
        _scene.Invalidated += (_, __) => ScheduleRebuild();
        _scene.HoverTimeChanged += (_, args) => HoverTimeChanged?.Invoke(this, args);
        _scene.ZoomRangeSelected += (_, args) => ZoomRangeSelected?.Invoke(this, args);
        SizeChanged += (_, __) => ScheduleRebuild();
        IsVisibleChanged += (_, __) =>
        {
            if (!IsVisible)
            {
                _built = false;
                _helpTip.IsOpen = false;
                _scene.CancelSelection();
                _scene.UpdateHover(_lastX, _lastY, true);
            }
            ScheduleRebuild();
        };
    }

    public LandingRecord? Record { get => _scene.Record; set => _scene.SetRecord(value); }
    public BitmapSource? OverlaySnapshot => (BitmapSource?)GetValue(OverlaySnapshotProperty);
    public LandingChartMode Mode { get => _scene.Mode; set => _scene.SetMode(value); }
    public void SetIsolatedSeries(int? index) => _scene.SetIsolatedSeries(index);
    public void SetZoomRange(double? start, double? end) => _scene.SetZoomRange(start, end);
    public void SetReplayVisible(bool visible) => _scene.SetReplayVisible(visible);
    public void RefreshLocalization() => _scene.RefreshLocalization();
    internal void SetLaneHeights(double main, double g, double altitude, double timeline) =>
        _scene.SetLaneHeights(main, g, altitude, timeline);

    internal void SetOverlayVisible(bool visible)
    {
        VerifyAccess();
        var changed = _overlayVisible != visible;
        _overlayVisible = visible;
        if (visible && !_failed)
        {
            // Capture before hiding the HWND cancels pointer interaction. This
            // WPF image participates in ordinary modal dimming and clipping.
            if (changed) RefreshOverlaySnapshot();
        }
        else ClearValue(OverlaySnapshotPropertyKey);
        Visibility = visible || _failed ? Visibility.Hidden : Visibility.Visible;
    }

    public void SetHoverTime(double? time)
    {
        VerifyAccess();
        if (_dispatching)
        {
            _hoverPending = true; _pendingHoverTime = time;
            return;
        }
        Render(() => _scene.SetHoverTime(time), WmMouseMove);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _destroying = false; _built = false;
        try
        {
            _window = _presentation.Create(hwndParent.Handle, _callback, _scene.BackgroundArgb);
            if (_window == IntPtr.Zero) Check(_presentation.LastError(IntPtr.Zero));
            if (_window == IntPtr.Zero) throw new InvalidOperationException("Chart presentation returned no window.");
            _nativeWindow = true;
            ScheduleRebuild();
        }
        catch (Exception error)
        {
            // HwndHost requires a valid child even when the native library or
            // window fails. The placeholder hides behind the WPF error message.
            _window = Native.CreateWindowEx(0, "STATIC", string.Empty, 0x40000000,
                0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), error.Message);
            _nativeWindow = false;
            ReportFailure(error);
        }
        return new HandleRef(this, _window);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _destroying = true;
        _helpTip.IsOpen = false;
        try
        {
            if (_window != IntPtr.Zero)
            {
                if (_nativeWindow) _presentation.Destroy(_window);
                else if (!Native.DestroyWindow(_window))
                    Trace.TraceError(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).ToString());
            }
        }
        finally { _window = IntPtr.Zero; _nativeWindow = false; _built = false; }
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) _scene.Dispose(); }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        _rebuildRequested = true;
        QueueRebuild();
    }

    private void QueueRebuild()
    {
        if (!_rebuildRequested || _pendingRebuild || _dispatching || _destroying || _failed) return;
        _pendingRebuild = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _pendingRebuild = false;
            if (_dispatching || _window == IntPtr.Zero || _destroying || _failed || (!IsVisible && !_overlayVisible) ||
                ActualWidth <= 0 || ActualHeight <= 0) return;
            if (_overlayVisible) { RefreshOverlaySnapshot(); return; }
            Render(() => Array.Empty<ChartPanelLayer>(), WmSize);
        }));
    }

    private void RefreshOverlaySnapshot()
    {
        if (_failed || _destroying || ActualWidth <= 0 || ActualHeight <= 0) return;
        var retryOnEvent = false;
        _dispatching = true;
        try
        {
            _rebuildRequested = false;
            var dpi = VisualTreeHelper.GetDpi(this);
            _scene.Resize(ActualWidth, ActualHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY);
            SetValue(OverlaySnapshotPropertyKey, _scene.CreateSnapshot());
            // Snapshot construction never consumes the pending native frame.
            _built = false;
        }
        catch (Exception error) { retryOnEvent = HandleRenderFailure(error); }
        finally { _dispatching = false; if (!retryOnEvent) QueueRebuild(); }
    }

    private void Render(Func<IReadOnlyList<ChartPanelLayer>> update, uint reason)
    {
        if (_failed || _destroying) return;
        var retryOnEvent = false;
        _dispatching = true;
        try
        {
            var canPresent = _nativeWindow && _window != IntPtr.Zero && IsVisible && ActualWidth > 0 && ActualHeight > 0;
            if (canPresent && _rebuildRequested)
            {
                _rebuildRequested = false;
                var dpi = VisualTreeHelper.GetDpi(this);
                _dpiX = dpi.PixelsPerInchX; _dpiY = dpi.PixelsPerInchY;
                _scene.Resize(ActualWidth, ActualHeight, _dpiX, _dpiY);
            }
            var changes = update();
            if (_hoverPending)
            {
                _hoverPending = false;
                var latest = _scene.SetHoverTime(_pendingHoverTime);
                if (latest.Count > 0)
                {
                    var combined = new List<ChartPanelLayer>(changes);
                    combined.AddRange(latest);
                    changes = combined;
                }
            }
            if (!canPresent) { _built = false; return; }
            var rebuilt = _scene.StaticDirty || !_built;
            if (rebuilt) changes = _scene.BuildStaticLayers();
            if (rebuilt || changes.Count > 0 || _presentPending || reason == WmPaint || reason == WmSize)
            {
                Apply(changes);
                Present(reason);
                _built = true;
                retryOnEvent = _presentPending && !_presentationAvailable;
            }
        }
        catch (Exception error) { retryOnEvent = HandleRenderFailure(error); }
        finally { _dispatching = false; if (!retryOnEvent) QueueRebuild(); }
    }

    private void ReceiveInput(IntPtr context, uint message, int x, int y, int delta, uint flags)
    {
        if (_destroying || _failed || _window == IntPtr.Zero) return;
        // Never unwind a managed exception through the Win32 callback.
        try
        {
            if (message == WmSize || message == WmDisplayChange || message == WmDpiChangedAfterParent || message == WmShowWindow)
            {
                ScheduleRebuild(); return;
            }
            // A failed initial frame/partial upload still needs the next input
            // or paint to rebuild it; _built must not make recovery unreachable.
            if (_dispatching) return;
            var dx = x * 96.0 / _dpiX; var dy = y * 96.0 / _dpiY;
            Render(() =>
            {
                switch (message)
                {
                    case WmMouseMove:
                        _lastX = dx; _lastY = dy;
                        UpdateHelp(_scene.GetHelpText(dx, dy));
                        Check(_presentation.SetCursor(_window, _scene.IsOverPlot(dx, dy) ? 1 : 0));
                        if (_scene.HasSelection) _scene.UpdateSelection(dx, dy);
                        return _scene.UpdateHover(dx, dy);
                    case WmLeave:
                        UpdateHelp(null);
                        if (!_scene.HasSelection) return _scene.UpdateHover(_lastX, _lastY, true);
                        break;
                    case WmLeftDown:
                        UpdateHelp(null);
                        _scene.BeginSelection(dx, dy);
                        return _scene.UpdateHover(dx, dy);
                    case WmLeftUp:
                        _scene.EndSelection(dx, dy);
                        return _scene.UpdateHover(dx, dy);
                    case WmLeftDouble:
                    case WmRightDown:
                        _scene.CancelSelection(); _scene.ResetZoom();
                        break;
                    case WmCaptureChanged:
                    case WmCancelMode:
                        if (_scene.HasSelection)
                        {
                            _scene.CancelSelection();
                            return _scene.UpdateHover(_lastX, _lastY, true);
                        }
                        break;
                }
                return Array.Empty<ChartPanelLayer>();
            }, message);
        }
        catch (Exception error) { HandleRenderFailure(error); }
    }

    private void Apply(IReadOnlyList<ChartPanelLayer> layers)
    {
        foreach (var layer in layers)
        {
            if (layer.Remove) { Check(_presentation.RemoveLayer(_window, layer.Id)); continue; }
            if (layer.Width <= 0 || layer.Height <= 0 || layer.Stride < layer.Width * 4 || layer.Bgra.Length < (long)layer.Stride * layer.Height)
                throw new InvalidDataException("The chart returned an invalid raster layer.");
            var pinned = GCHandle.Alloc(layer.Bgra, GCHandleType.Pinned);
            try
            {
                Check(_presentation.UploadLayer(_window, layer.Id, pinned.AddrOfPinnedObject(), layer.Width,
                    layer.Height, layer.Stride, (float)(layer.XDip * _dpiX / 96), (float)(layer.YDip * _dpiY / 96)));
            }
            finally { pinned.Free(); }
        }
    }

    private void UpdateHelp(string? text)
    {
        if (text == null) { if (_helpTip.IsOpen) _helpTip.IsOpen = false; return; }
        if (Equals(_helpTip.Tag, text) && _helpTip.IsOpen) return;
        _helpTip.Tag = text;
        _helpTip.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 370 };
        _helpTip.IsOpen = true;
    }

    private void Present(uint reason)
    {
        var result = _presentation.Present(_window, reason);
        if (IsRecoverable(result))
        {
            // Lost/reset/removed devices are recovered by the native backend on
            // the next real input, paint, display or visibility notification.
            ReportUnavailable(Marshal.GetExceptionForHR(result));
            return;
        }
        Check(result);
        // Occlusion, mode changes and a zero-sized surface are deferred work,
        // not a displayed frame. Preserve recovery even for an unchanged hover.
        _presentPending = result != 0;
        if (!_presentationAvailable && result == 0)
        {
            _presentationAvailable = true;
            RenderingAvailabilityChanged?.Invoke(this, true);
        }
    }

    internal string GetPresentationInfo()
    {
        if (!_nativeWindow) return string.Empty;
        var description = new StringBuilder(1024);
        Check(_presentation.GetPathInfo(_window, description, (uint)description.Capacity));
        return description.ToString();
    }

    internal ChartPresentationDiagnostics GetPresentationDiagnostics()
    {
        Check(_presentation.GetDiagnostics(_window, out var diagnostics,
            (uint)Marshal.SizeOf(typeof(ChartPresentationDiagnostics))));
        return diagnostics;
    }

    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }

    private static bool IsRecoverable(int result) => unchecked((uint)result) is
        0x88760868 or 0x88760869 or 0x88760870 or 0x88760827 or // lost, not-reset, removed, driver error
        0x88760874 or 0x8876017c or 0x8007000e; // hung, video memory, allocation

    private bool HandleRenderFailure(Exception error)
    {
        if (!IsRecoverable(error.HResult)) { ReportFailure(error); return false; }
        // Apply may have accepted only part of the current frame. Rebuild every
        // layer from the latest scene on the next real event, including removals.
        _built = false;
        ReportUnavailable(error);
        return true;
    }

    private void ReportUnavailable(Exception? error)
    {
        _presentPending = true;
        if (!_presentationAvailable) return;
        _presentationAvailable = false;
        _reportError("chart-device-unavailable", error);
        RenderingAvailabilityChanged?.Invoke(this, false);
    }

    private void ReportFailure(Exception error)
    {
        if (_failed) return;
        _failed = true;
        _reportError("chart-presentation", error);
        try { Dispatcher.BeginInvoke(new Action(() => RenderingFailed?.Invoke(this, error))); }
        catch (InvalidOperationException dispatchError) { Trace.TraceError(new AggregateException(error, dispatchError).ToString()); }
    }

    private sealed class Native : IChartPresentation
    {
        private const string Library = "LandingStats.Graphics.Native.dll";
        public IntPtr Create(IntPtr parent, ChartInputCallback callback, uint backgroundArgb) => Flip_Create(parent, callback, IntPtr.Zero, null, backgroundArgb);
        public void Destroy(IntPtr window) => Flip_Destroy(window);
        public int LastError(IntPtr window) => Flip_LastError(window);
        public int UploadLayer(IntPtr window, int id, IntPtr bgra, int width, int height, int stride, float x, float y) => Flip_UploadLayer(window, id, bgra, width, height, stride, x, y);
        public int RemoveLayer(IntPtr window, int id) => Flip_RemoveLayer(window, id);
        public int Present(IntPtr window, uint reason) => Flip_Present(window, reason);
        public int SetCursor(IntPtr window, int kind) => Flip_SetCursor(window, kind);
        public int GetDiagnostics(IntPtr window, out ChartPresentationDiagnostics diagnostics, uint size) => Flip_GetDiagnostics(window, out diagnostics, size);
        public int GetPathInfo(IntPtr window, StringBuilder buffer, uint characters) => Flip_GetPathInfo(window, buffer, characters);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern IntPtr Flip_Create(IntPtr parent, ChartInputCallback callback, IntPtr context, string? logPath, uint backgroundArgb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void Flip_Destroy(IntPtr window);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Flip_LastError(IntPtr window);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Flip_UploadLayer(IntPtr window, int id, IntPtr bgra, int width, int height, int stride, float x, float y);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Flip_RemoveLayer(IntPtr window, int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Flip_Present(IntPtr window, uint reason);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Flip_SetCursor(IntPtr window, int kind);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Flip_GetDiagnostics(IntPtr window, out ChartPresentationDiagnostics diagnostics, uint size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int Flip_GetPathInfo(IntPtr window, StringBuilder buffer, uint characters);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(IntPtr window);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct ChartPresentationDiagnostics
{
    internal uint Size, Adapter;
    internal ulong PresentCalls, Occluded, ModeChanged, ResetCalls, UploadCalls, UploadedBytes, DroppedLogRecords;
    internal long QpcFrequency, MaximumPresentTicks;
    internal int LastError;
    internal uint LayerCount;
}
