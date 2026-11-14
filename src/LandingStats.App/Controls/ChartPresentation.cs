using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LandingStats.App.Controls;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ChartInputCallback(IntPtr context, uint message, int x, int y, int delta, uint flags);

// Keeps HWND/pixel submission separate from scene generation. Tests substitute
// the graphics boundary, so they exercise the real panel without a desktop GPU.
internal interface IChartPresentation
{
    IntPtr Create(IntPtr parent, ChartInputCallback callback, uint backgroundArgb);
    void Destroy(IntPtr window);
    int LastError(IntPtr window);
    int UploadLayer(IntPtr window, int id, IntPtr bgra, int width, int height, int stride, float x, float y);
    int RemoveLayer(IntPtr window, int id);
    int Present(IntPtr window, uint reason);
    int SetCursor(IntPtr window, int kind);
    int GetDiagnostics(IntPtr window, out ChartPresentationDiagnostics diagnostics, uint size);
    int GetPathInfo(IntPtr window, StringBuilder buffer, uint characters);
}
