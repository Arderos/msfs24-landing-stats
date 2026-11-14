#pragma once
#include <stdint.h>
#include <windows.h>
#ifdef CHART_FLIP_BUILD
#define FLIP_API extern "C" __declspec(dllexport)
#else
#define FLIP_API extern "C" __declspec(dllimport)
#endif

// All entry points and callbacks run on the HWND's creating UI thread.
// Caller must keep callback/context alive until Flip_Destroy returns and must
// catch managed exceptions inside its callback. Coordinates are physical pixels.
// WM_SIZE uses x=width,y=height. WM_MOUSEWHEEL/HWHEEL uses signed delta.
typedef void(__cdecl *FlipInputCallback)(void *context, UINT message, int x, int y, int delta, UINT flags);

struct FlipDiagnostics
{
    uint32_t size;
    uint32_t adapter;
    uint64_t presentCalls;
    uint64_t occluded;
    uint64_t modeChanged;
    uint64_t resetCalls;
    uint64_t uploadCalls;
    uint64_t uploadedBytes;
    uint64_t droppedLogRecords;
    int64_t qpcFrequency;
    int64_t maximumPresentTicks;
    int32_t lastError;
    uint32_t layerCount;
};

// NULL/empty logPath disables diagnostic file creation and per-present records.
// An explicit path must not exist; diagnostic records are saved on destruction.
// Creates the child HWND only. GPU initialization is deferred to Flip_Present,
// so a temporary device failure never invalidates the window's input lifetime.
// backgroundArgb supplies the opaque application background for the initial
// pre-device paint and subsequent Direct3D clears; color is owned by the theme.
FLIP_API HWND __cdecl Flip_Create(HWND parent, FlipInputCallback callback, void *context, const wchar_t *logPath,
                                  uint32_t backgroundArgb);
FLIP_API void __cdecl Flip_Destroy(HWND window);
// Layout is immediate; GPU resize/reset occurs in the next Flip_Present.
FLIP_API HRESULT __cdecl Flip_Resize(HWND window, int width, int height);
// BGRA is copied before return; GPU upload is batched in Flip_Present.
// Premultiplied alpha; stride >= width*4; id >= 0.
// Pixel-sized textures are drawn at x,y without scaling, in ascending id order.
FLIP_API HRESULT __cdecl Flip_UploadLayer(HWND window, int id, const void *bgra, int width, int height, int stride,
                                          float x, float y);
FLIP_API HRESULT __cdecl Flip_RemoveLayer(HWND window, int id);
FLIP_API HRESULT __cdecl Flip_Present(HWND window, UINT reason);
// NULL retrieves the last creation/API error for the current calling thread.
FLIP_API HRESULT __cdecl Flip_LastError(HWND window);
FLIP_API HRESULT __cdecl Flip_GetDiagnostics(HWND window, FlipDiagnostics *diagnostics, UINT size);
FLIP_API HRESULT __cdecl Flip_GetPathInfo(HWND window, wchar_t *buffer, UINT characters);
// kind: 0 = Arrow, 1 = Cross. Retained for subsequent WM_SETCURSOR messages.
FLIP_API HRESULT __cdecl Flip_SetCursor(HWND window, int kind);
