#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define CHART_FLIP_BUILD
#include "ChartFlipNative.h"
#include "ChartDeviceRecovery.h"
#include "ChartInitialBackground.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <d3d9.h>
#include <iomanip>
#include <map>
#include <memory>
#include <new>
#include <sstream>
#include <string>
#include <vector>
#include <windowsx.h>
static_assert(sizeof(FlipDiagnostics) == 88, "Managed/native diagnostics ABI must match");

// Event-driven native presentation surface. WPF supplies premultiplied chart
// layers; this child HWND owns their opaque Direct3D9Ex FLIPEX presentation.
namespace
{
using LandingStatsGraphics::ChartDeviceRecovery;
using LandingStatsGraphics::ChartRecoveryRetry;
using LandingStatsGraphics::DeviceRecoveryAction;
constexpr wchar_t ClassName[] = L"LandingStatsChartFlipSurface";
constexpr size_t MaximumPresentRecords = 262144;
constexpr UINT_PTR RecoveryTimerId = 1;
ULONGLONG MonotonicMilliseconds() noexcept { return GetTickCount64(); }
thread_local HRESULT threadError = S_OK;
HINSTANCE module = nullptr;
ATOM windowClass = 0;
INIT_ONCE classOnce = INIT_ONCE_STATIC_INIT;
template <class T> void Release(T *&value) noexcept
{
    if (value)
    {
        value->Release();
        value = nullptr;
    }
}
HRESULT Win32Error() noexcept
{
    DWORD e = GetLastError();
    return e ? HRESULT_FROM_WIN32(e) : E_FAIL;
}
struct Vertex
{
    float x, y, z, rhw, u, v;
};
struct Record
{
    LONGLONG begin, end;
    HRESULT result;
    UINT reason;
};
struct Layer
{
    int width = 0, height = 0;
    float x = 0, y = 0;
    std::vector<unsigned char> pixels;
    IDirect3DTexture9 *texture = nullptr;
    bool dirty = true;
    ~Layer()
    {
        Release(texture);
    }
};
struct Surface
{
    HWND window = nullptr;
    FlipInputCallback callback = nullptr;
    void *context = nullptr;
    IDirect3D9Ex *d3d = nullptr;
    IDirect3DDevice9Ex *device = nullptr;
    IDirect3DSwapChain9 *chain = nullptr;
    D3DPRESENT_PARAMETERS pp{};
    D3DCAPS9 caps{};
    UINT adapter = 0;
    int width = 1, height = 1, mouseX = 0, mouseY = 0;
    bool owned = false, initialized = false, resetNeeded = false, trackingMouse = false, inPresent = false,
         destroying = false;
    HRESULT lastError = S_OK;
    ChartDeviceRecovery recovery;
    LandingStatsGraphics::ChartInitialBackground initialBackground;
    ChartRecoveryRetry recoveryRetry;
    ULONGLONG (*clockMilliseconds)() noexcept = MonotonicMilliseconds;
    ULONGLONG recoveryTimerDue = 0;
    bool recoveryTimerArmed = false;
    HCURSOR cursor = nullptr;
    HANDLE logFile = INVALID_HANDLE_VALUE;
    std::map<int, Layer> layers;
    std::vector<Record> records;
    std::ostringstream messages;
    std::wstring pathInfo;
    FlipDiagnostics diagnostics{};
    explicit Surface(uint32_t backgroundArgb) : initialBackground(backgroundArgb)
    {
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        diagnostics.size = sizeof(diagnostics);
        diagnostics.qpcFrequency = frequency.QuadPart;
        messages.imbue(std::locale::classic());
    }
    ~Surface()
    {
        CancelRecoveryTimer();
        ReleaseDevice();
        Release(d3d);
        if (logFile != INVALID_HANDLE_VALUE)
            CloseHandle(logFile);
    }
    bool CanRecoverWhileVisible() const noexcept
    {
        return !destroying && width > 0 && height > 0 && IsWindowVisible(window) &&
            !IsIconic(GetAncestor(window, GA_ROOT));
    }
    void CancelRecoveryTimer() noexcept
    {
        if (recoveryTimerArmed) KillTimer(window, RecoveryTimerId);
        recoveryTimerArmed = false;
        recoveryTimerDue = 0;
    }
    HRESULT ScheduleRecoveryTimer() noexcept
    {
        if (!recoveryRetry.CanRetryAutomatically() || !CanRecoverWhileVisible())
        {
            CancelRecoveryTimer();
            return S_OK;
        }
        if (recoveryTimerArmed && recoveryTimerDue == recoveryRetry.DueMilliseconds()) return S_OK;
        CancelRecoveryTimer();
        const DWORD remaining = recoveryRetry.RemainingMilliseconds(clockMilliseconds());
        if (!SetTimer(window, RecoveryTimerId, std::max<DWORD>(1, remaining), nullptr)) return Win32Error();
        recoveryTimerArmed = true;
        recoveryTimerDue = recoveryRetry.DueMilliseconds();
        return S_OK;
    }
    HRESULT DeferFailure(HRESULT error) noexcept
    {
        recoveryRetry.RecordFailure(error, clockMilliseconds());
        const HRESULT scheduled = ScheduleRecoveryTimer();
        return FAILED(scheduled) ? Remember(scheduled, "SetTimer recovery") : error;
    }
    HRESULT Remember(HRESULT hr, const char *operation) noexcept
    {
        lastError = hr;
        threadError = hr;
        if (ChartDeviceRecovery::IsRecoverable(hr))
        {
            recovery.RecordFailure(hr);
            resetNeeded = true;
        }
        if (FAILED(hr) && logFile != INVALID_HANDLE_VALUE)
        {
            try
            {
                messages << "ERROR " << operation << " hr=0x" << std::hex << static_cast<unsigned long>(hr) << std::dec
                         << '\n';
            }
            catch (...)
            {
                ++diagnostics.droppedLogRecords;
            }
        }
        return hr;
    }
    void ReleaseTextures() noexcept
    {
        for (auto &entry : layers)
        {
            Release(entry.second.texture);
            entry.second.dirty = true;
        }
    }
    void ReleaseDevice() noexcept
    {
        ReleaseTextures();
        Release(chain);
        Release(device);
    }
    HRESULT Configure()
    {
        HRESULT hr;
#define STATE(call)                                                                                                    \
    do                                                                                                                 \
    {                                                                                                                  \
        hr = (call);                                                                                                   \
        if (FAILED(hr))                                                                                                \
            return Remember(hr, #call);                                                                                \
    } while (false)
        STATE(device->SetFVF(D3DFVF_XYZRHW | D3DFVF_TEX1));
        STATE(device->SetRenderState(D3DRS_LIGHTING, FALSE));
        STATE(device->SetRenderState(D3DRS_ZENABLE, FALSE));
        STATE(device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE));
        STATE(device->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE));
        STATE(device->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_ONE));
        STATE(device->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA));
        STATE(device->SetRenderState(D3DRS_BLENDOP, D3DBLENDOP_ADD));
        STATE(device->SetRenderState(D3DRS_ALPHATESTENABLE, FALSE));
        STATE(device->SetRenderState(D3DRS_SRGBWRITEENABLE, FALSE));
        STATE(device->SetSamplerState(0, D3DSAMP_MINFILTER, D3DTEXF_POINT));
        STATE(device->SetSamplerState(0, D3DSAMP_MAGFILTER, D3DTEXF_POINT));
        STATE(device->SetSamplerState(0, D3DSAMP_MIPFILTER, D3DTEXF_NONE));
        STATE(device->SetSamplerState(0, D3DSAMP_ADDRESSU, D3DTADDRESS_CLAMP));
        STATE(device->SetSamplerState(0, D3DSAMP_ADDRESSV, D3DTADDRESS_CLAMP));
        STATE(device->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_SELECTARG1));
        STATE(device->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE));
        STATE(device->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_SELECTARG1));
        STATE(device->SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE));
        STATE(device->SetTextureStageState(1, D3DTSS_COLOROP, D3DTOP_DISABLE));
#undef STATE
        return S_OK;
    }
    HRESULT UploadTexture(Layer &layer)
    {
        if (!device)
            return Remember(D3DERR_INVALIDCALL, "UploadTexture without device");
        if (static_cast<UINT>(layer.width) > caps.MaxTextureWidth ||
            static_cast<UINT>(layer.height) > caps.MaxTextureHeight)
            return Remember(D3DERR_NOTAVAILABLE, "Layer exceeds texture caps");
        if (!layer.texture)
        {
            HRESULT hr =
                device->CreateTexture(static_cast<UINT>(layer.width), static_cast<UINT>(layer.height), 1,
                                      D3DUSAGE_DYNAMIC, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &layer.texture, nullptr);
            if (FAILED(hr))
                return Remember(hr, "CreateTexture dynamic A8R8G8B8");
        }
        D3DLOCKED_RECT locked{};
        HRESULT hr = layer.texture->LockRect(0, &locked, nullptr, D3DLOCK_DISCARD);
        if (FAILED(hr))
            return Remember(hr, "Texture LockRect DISCARD");
        const size_t rowBytes = static_cast<size_t>(layer.width) * 4;
        if (locked.Pitch < 0 || static_cast<size_t>(locked.Pitch) < rowBytes)
        {
            const HRESULT unlock = layer.texture->UnlockRect(0);
            return Remember(FAILED(unlock) ? unlock : E_UNEXPECTED, "Unexpected texture pitch");
        }
        for (int y = 0; y < layer.height; ++y)
            std::memcpy(static_cast<unsigned char *>(locked.pBits) + static_cast<size_t>(y) * locked.Pitch,
                        layer.pixels.data() + static_cast<size_t>(y) * rowBytes, rowBytes);
        hr = layer.texture->UnlockRect(0);
        if (SUCCEEDED(hr))
            layer.dirty = false;
        return FAILED(hr) ? Remember(hr, "Texture UnlockRect") : S_OK;
    }
    HRESULT RestoreLayers()
    {
        for (auto &entry : layers)
        {
            if (!entry.second.dirty)
                continue;
            const HRESULT hr = UploadTexture(entry.second);
            if (FAILED(hr))
                return hr;
        }
        return S_OK;
    }
    HRESULT SelectAdapter(UINT &selected) const
    {
        const HMONITOR monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        for (UINT i = 0; i < d3d->GetAdapterCount(); ++i)
            if (d3d->GetAdapterMonitor(i) == monitor)
            {
                selected = i;
                return S_OK;
            }
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }
    void Parameters() noexcept
    {
        pp = {};
        pp.BackBufferWidth = static_cast<UINT>(std::max(1, width));
        pp.BackBufferHeight = static_cast<UINT>(std::max(1, height));
        pp.BackBufferFormat = D3DFMT_X8R8G8B8;
        pp.BackBufferCount = 2;
        pp.MultiSampleType = D3DMULTISAMPLE_NONE;
        pp.SwapEffect = D3DSWAPEFFECT_FLIPEX;
        pp.hDeviceWindow = window;
        pp.Windowed = TRUE;
        pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
    }
    HRESULT CreateDevice(UINT selected)
    {
        ReleaseDevice();
        resetNeeded = true;
        adapter = selected;
        Parameters();
        HRESULT hr = d3d->GetDeviceCaps(adapter, D3DDEVTYPE_HAL, &caps);
        if (FAILED(hr))
            return Remember(hr, "GetDeviceCaps");
        if (!(caps.Caps2 & D3DCAPS2_DYNAMICTEXTURES))
            return Remember(D3DERR_NOTAVAILABLE, "Dynamic textures unsupported");
        const DWORD flags = D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE;
        hr = d3d->CreateDeviceEx(adapter, D3DDEVTYPE_HAL, window, flags, &pp, nullptr, &device);
        if (FAILED(hr))
            return Remember(hr, "CreateDeviceEx HAL FLIPEX");
        hr = device->GetSwapChain(0, &chain);
        if (FAILED(hr))
            return Remember(hr, "GetSwapChain");
        hr = Configure();
        if (FAILED(hr))
            return hr;
        D3DPRESENT_PARAMETERS actual{};
        hr = chain->GetPresentParameters(&actual);
        if (FAILED(hr))
            return Remember(hr, "GetPresentParameters");
        if (actual.SwapEffect != D3DSWAPEFFECT_FLIPEX || actual.PresentationInterval != D3DPRESENT_INTERVAL_IMMEDIATE ||
            actual.BackBufferFormat != D3DFMT_X8R8G8B8)
            return Remember(E_UNEXPECTED, "Unexpected presentation parameters");
        D3DADAPTER_IDENTIFIER9 id{};
        hr = d3d->GetAdapterIdentifier(adapter, 0, &id);
        if (FAILED(hr))
            return Remember(hr, "GetAdapterIdentifier");
        wchar_t description[512]{};
        if (!MultiByteToWideChar(CP_ACP, 0, id.Description, -1, description, ARRAYSIZE(description)))
            return Remember(Win32Error(), "Adapter description");
        std::wostringstream info;
        info << L"D3D9Ex HAL; FLIPEX; backbuffer X8R8G8B8 count=" << actual.BackBufferCount
             << L" (requested2); IMMEDIATE; swapchain Present override=NULL; premultiplied A8R8G8B8 POINT; adapter="
             << adapter << L" " << description;
        pathInfo = info.str();
        diagnostics.adapter = adapter;
        if (logFile != INVALID_HANDLE_VALUE)
            messages << "Device adapter=" << adapter << " " << id.Description
                     << "; FLIPEX/2/X8R8G8B8/IMMEDIATE, NULL override\n";
        hr = RestoreLayers();
        if (FAILED(hr))
            return hr;
        resetNeeded = false;
        recovery.Complete();
        return S_OK;
    }
    HRESULT EnsureDevice()
    {
        if (width <= 0 || height <= 0)
            return S_FALSE;
        if (recovery.Pending())
        {
            auto action = recovery.NextAction(device != nullptr);
            if (action == DeviceRecoveryAction::CheckDeviceState)
            {
                const HRESULT state = device->CheckDeviceState(window);
                action = recovery.ObserveDeviceState(state);
                if (action == DeviceRecoveryAction::Wait || action == DeviceRecoveryAction::Fatal)
                    return Remember(state, "CheckDeviceState");
            }
            if (action == DeviceRecoveryAction::RecreateRuntime)
            {
                // Enumerate a fresh adapter list after removal/driver restart.
                ReleaseDevice();
                Release(d3d);
            }
            else if (action == DeviceRecoveryAction::RecreateDevice)
            {
                // Release unusable/default-pool GPU resources before attempting
                // another allocation. CPU pixels survive and are uploaded again.
                ReleaseDevice();
            }
            resetNeeded = true;
        }
        if (!d3d)
        {
            const HRESULT created = Direct3DCreate9Ex(D3D_SDK_VERSION, &d3d);
            if (FAILED(created))
                return Remember(created, "Direct3DCreate9Ex");
        }
        UINT selected = 0;
        HRESULT hr = SelectAdapter(selected);
        if (FAILED(hr))
            return Remember(hr, "Monitor adapter selection");
        if (!device || !chain || selected != adapter)
            return CreateDevice(selected);
        if (!resetNeeded && pp.BackBufferWidth == static_cast<UINT>(width) &&
            pp.BackBufferHeight == static_cast<UINT>(height))
            return S_OK;
        resetNeeded = true;
        ReleaseTextures();
        Release(chain);
        Parameters();
        ++diagnostics.resetCalls;
        hr = device->ResetEx(&pp, nullptr);
        if (FAILED(hr))
            return Remember(hr, "ResetEx size/mode change");
        hr = device->GetSwapChain(0, &chain);
        if (FAILED(hr))
            return Remember(hr, "GetSwapChain after ResetEx");
        hr = Configure();
        if (FAILED(hr))
            return hr;
        hr = RestoreLayers();
        if (FAILED(hr))
            return hr;
        resetNeeded = false;
        recovery.Complete();
        if (logFile != INVALID_HANDLE_VALUE)
            messages << "ResetEx " << width << 'x' << height << '\n';
        return S_OK;
    }
    HRESULT Present(UINT reason)
    {
        if (inPresent)
            return Remember(D3DERR_INVALIDCALL, "Recursive Present");
        inPresent = true;
        struct Guard
        {
            bool &value;
            ~Guard()
            {
                value = false;
            }
        } guard{inPresent};
        HRESULT hr = EnsureDevice();
        if (hr != S_OK)
            return hr;
        hr = RestoreLayers();
        if (FAILED(hr))
            return hr;
        hr = device->Clear(0, nullptr, D3DCLEAR_TARGET, initialBackground.ClearColor(), 1.0f, 0);
        if (FAILED(hr))
            return Remember(hr, "Clear");
        hr = device->BeginScene();
        if (FAILED(hr))
            return Remember(hr, "BeginScene");
        for (auto &entry : layers)
        {
            auto &layer = entry.second;
            if (!layer.texture)
            {
                hr = D3DERR_INVALIDCALL;
                break;
            }
            // D3D9's half-pixel convention aligns texture texels to physical pixels.
            const float left = layer.x - 0.5f, top = layer.y - 0.5f;
            const float right = left + layer.width, bottom = top + layer.height;
            const Vertex quad[] = {{left, top, 0, 1, 0, 0},
                                   {right, top, 0, 1, 1, 0},
                                   {left, bottom, 0, 1, 0, 1},
                                   {right, bottom, 0, 1, 1, 1}};
            hr = device->SetTexture(0, layer.texture);
            if (FAILED(hr))
                break;
            hr = device->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, quad, sizeof(Vertex));
            if (FAILED(hr))
                break;
        }
        const HRESULT end = device->EndScene();
        if (FAILED(hr))
            return Remember(hr, "Draw layer");
        if (FAILED(end))
            return Remember(end, "EndScene");
        LARGE_INTEGER begin{}, finish{};
        QueryPerformanceCounter(&begin);
        hr = chain->Present(nullptr, nullptr, nullptr, nullptr, 0);
        QueryPerformanceCounter(&finish);
        ++diagnostics.presentCalls;
        diagnostics.maximumPresentTicks = std::max(diagnostics.maximumPresentTicks, finish.QuadPart - begin.QuadPart);
        if (logFile != INVALID_HANDLE_VALUE)
        {
            if (records.size() < MaximumPresentRecords)
                records.push_back({begin.QuadPart, finish.QuadPart, hr, reason});
            else
                ++diagnostics.droppedLogRecords;
        }
        if (hr == S_PRESENT_OCCLUDED)
            ++diagnostics.occluded;
        if (hr == S_PRESENT_MODE_CHANGED)
        {
            ++diagnostics.modeChanged;
            resetNeeded = true;
        }
        initialBackground.RecordPresent(hr);
        return Remember(hr, "SwapChain Present");
    }
    HRESULT TryPresent(UINT reason)
    {
        if (!CanRecoverWhileVisible())
        {
            CancelRecoveryTimer();
            return S_FALSE;
        }
        if (!recoveryRetry.CanAttempt(clockMilliseconds()))
        {
            // Do not call Remember or move the deadline for suppressed input
            // and replay events. CPU layers can still accept the latest scene.
            const HRESULT scheduled = ScheduleRecoveryTimer();
            if (FAILED(scheduled)) return Remember(scheduled, "SetTimer recovery");
            lastError = threadError = recoveryRetry.Error();
            return lastError;
        }
        CancelRecoveryTimer();
        HRESULT hr;
        try { hr = Present(reason); }
        catch (const std::bad_alloc &) { hr = Remember(E_OUTOFMEMORY, "Present allocation"); }
        catch (...) { hr = Remember(E_FAIL, "Present exception"); }
        if (hr == S_PRESENT_OCCLUDED) recoveryRetry.SuspendAutomaticRetry();
        else recoveryRetry.ResumeAutomaticRetry();
        if (ChartDeviceRecovery::IsRecoverable(hr)) return DeferFailure(hr);
        if (hr == S_OK || FAILED(hr))
            recoveryRetry.Complete();
        else if (recoveryRetry.Pending())
        {
            // Occluded/zero-sized surfaces resume through a real paint/show
            // event. Replay ticks remain rate-limited while they are hidden.
            recoveryRetry.Defer(clockMilliseconds());
            if (hr != S_PRESENT_OCCLUDED && hr != S_FALSE)
            {
                const HRESULT scheduled = ScheduleRecoveryTimer();
                if (FAILED(scheduled)) return Remember(scheduled, "SetTimer recovery");
            }
        }
        return hr;
    }
    void SaveLog()
    {
        if (logFile == INVALID_HANDLE_VALUE)
            return;
        messages << "PresentCalls=" << diagnostics.presentCalls << " Occluded=" << diagnostics.occluded
                 << " ModeChanged=" << diagnostics.modeChanged << " Resets=" << diagnostics.resetCalls
                 << " UploadCalls=" << diagnostics.uploadCalls << " UploadBytes=" << diagnostics.uploadedBytes
                 << " DroppedRecords=" << diagnostics.droppedLogRecords << " QPCFrequency=" << diagnostics.qpcFrequency
                 << '\n';
        messages << "QpcBegin,QpcEnd,DurationMs,HResult,Reason\n";
        for (const auto &r : records)
            messages << r.begin << ',' << r.end << ',' << std::fixed << std::setprecision(6)
                     << static_cast<double>(r.end - r.begin) * 1000.0 / static_cast<double>(diagnostics.qpcFrequency)
                     << ",0x" << std::hex << static_cast<unsigned long>(r.result) << std::dec << ',' << r.reason
                     << '\n';
        const std::string bytes = messages.str();
        DWORD written = 0;
        if (bytes.size() > MAXDWORD ||
            !WriteFile(logFile, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) ||
            written != bytes.size())
            threadError = Win32Error();
        CloseHandle(logFile);
        logFile = INVALID_HANDLE_VALUE;
    }
};

Surface *GetSurface(HWND window) noexcept
{
    if (!window || GetWindowThreadProcessId(window, nullptr) != GetCurrentThreadId())
    {
        threadError = RPC_E_WRONG_THREAD;
        return nullptr;
    }
    if (static_cast<ATOM>(GetClassLongPtrW(window, GCW_ATOM)) != windowClass)
    {
        threadError = E_INVALIDARG;
        return nullptr;
    }
    auto *surface = reinterpret_cast<Surface *>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (!surface)
        threadError = E_HANDLE;
    return surface;
}
void Dispatch(Surface *s, UINT message, int x, int y, int delta, UINT flags)
{
    if (s->initialized && !s->destroying && s->callback)
        s->callback(s->context, message, x, y, delta, flags);
}
LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    auto *s = reinterpret_cast<Surface *>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        s = static_cast<Surface *>(reinterpret_cast<CREATESTRUCTW *>(lParam)->lpCreateParams);
        s->window = window;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(s));
    }
    if (!s)
        return DefWindowProcW(window, message, wParam, lParam);
    // These come from the real HWND. Replay's synthetic hover updates do not
    // clear occlusion or restart a paused recovery timer.
    if (message == WM_PAINT || message == WM_SIZE || message == WM_DISPLAYCHANGE ||
        message == WM_DPICHANGED_AFTERPARENT || (message == WM_SHOWWINDOW && wParam) ||
        (message >= WM_MOUSEFIRST && message <= WM_MOUSELAST))
        s->recoveryRetry.ResumeAutomaticRetry();
    try
    {
        switch (message)
        {
        case WM_DESTROY:
            s->destroying = true;
            s->CancelRecoveryTimer();
            return 0;
        case WM_NCDESTROY: {
            SetWindowLongPtrW(window, GWLP_USERDATA, 0);
            if (s->owned)
            {
                try
                {
                    s->SaveLog();
                }
                catch (...)
                {
                    threadError = E_FAIL;
                }
                delete s;
            }
            return DefWindowProcW(window, message, wParam, lParam);
        }
        case WM_ERASEBKGND:
            return 1;
        case WM_SETCURSOR:
            if (LOWORD(lParam) == HTCLIENT && s->cursor)
            {
                SetCursor(s->cursor);
                return TRUE;
            }
            break;
        case WM_PAINT: {
            PAINTSTRUCT paint{};
            const HDC target = BeginPaint(window, &paint);
            // Only the initial empty HWND uses GDI; after its first successful
            // flip, presentation and recovery remain entirely Direct3D-owned.
            s->initialBackground.Paint(target, paint.rcPaint);
            EndPaint(window, &paint);
            Dispatch(s, message, 0, 0, 0, 0);
            return 0;
        }
        case WM_SIZE:
            s->width = LOWORD(lParam);
            s->height = HIWORD(lParam);
            if (s->width <= 0 || s->height <= 0) s->CancelRecoveryTimer();
            // ResetEx can send window messages. Defer GPU work to the caller's
            // next batched Present instead of reentering it during WPF layout.
            Dispatch(s, message, s->width, s->height, 0, static_cast<UINT>(wParam));
            return 0;
        case WM_DISPLAYCHANGE:
            s->resetNeeded = true;
            Dispatch(s, message, 0, 0, 0, 0);
            return 0;
        case WM_DPICHANGED_AFTERPARENT:
            s->resetNeeded = true;
            Dispatch(s, message, 0, 0, 0, 0);
            return 0;
        case WM_SHOWWINDOW:
            if (!wParam)
            {
                s->CancelRecoveryTimer();
                Dispatch(s, WM_CANCELMODE, s->mouseX, s->mouseY, 0, 0);
                if (GetCapture() == window)
                    ReleaseCapture();
            }
            Dispatch(s, message, s->width, s->height, 0, static_cast<UINT>(wParam));
            return 0;
        case WM_TIMER:
            if (wParam == RecoveryTimerId)
            {
                // Native timers repeat by default; make this one-shot. A
                // failed attempt arms only its next bounded recovery deadline.
                s->CancelRecoveryTimer();
                if (s->recoveryRetry.Pending() && s->CanRecoverWhileVisible())
                    Dispatch(s, WM_PAINT, 0, 0, 0, 0);
                return 0;
            }
            break;
        case WM_MOUSEMOVE:
            s->mouseX = GET_X_LPARAM(lParam);
            s->mouseY = GET_Y_LPARAM(lParam);
            if (!s->trackingMouse)
            {
                TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE, window, 0};
                if (TrackMouseEvent(&track))
                    s->trackingMouse = true;
                else
                    s->Remember(Win32Error(), "TrackMouseEvent");
            }
            Dispatch(s, message, s->mouseX, s->mouseY, 0, static_cast<UINT>(wParam));
            return 0;
        case WM_MOUSELEAVE:
            s->trackingMouse = false;
            Dispatch(s, message, s->mouseX, s->mouseY, 0, 0);
            return 0;
        case WM_LBUTTONDOWN:
        case WM_RBUTTONDOWN:
        case WM_MBUTTONDOWN:
        case WM_XBUTTONDOWN:
        case WM_LBUTTONDBLCLK:
        case WM_RBUTTONDBLCLK:
        case WM_MBUTTONDBLCLK:
        case WM_XBUTTONDBLCLK:
            SetCapture(window);
            Dispatch(s, message, GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam), 0, static_cast<UINT>(wParam));
            return (message == WM_XBUTTONDOWN || message == WM_XBUTTONDBLCLK) ? TRUE : 0;
        case WM_LBUTTONUP:
        case WM_RBUTTONUP:
        case WM_MBUTTONUP:
        case WM_XBUTTONUP:
            // ReleaseCapture synchronously sends WM_CAPTURECHANGED. Commit the
            // managed drag first, otherwise that notification cancels it.
            Dispatch(s, message, GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam), 0, static_cast<UINT>(wParam));
            if (!(GET_KEYSTATE_WPARAM(wParam) & (MK_LBUTTON | MK_RBUTTON | MK_MBUTTON | MK_XBUTTON1 | MK_XBUTTON2)) &&
                GetCapture() == window)
                ReleaseCapture();
            return message == WM_XBUTTONUP ? TRUE : 0;
        case WM_CANCELMODE:
            Dispatch(s, message, s->mouseX, s->mouseY, 0, 0);
            if (GetCapture() == window)
                ReleaseCapture();
            return 0;
        case WM_MOUSEWHEEL:
        case WM_MOUSEHWHEEL: {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            if (!ScreenToClient(window, &point))
            {
                s->Remember(Win32Error(), "ScreenToClient wheel");
                return 0;
            }
            Dispatch(s, message, point.x, point.y, GET_WHEEL_DELTA_WPARAM(wParam), GET_KEYSTATE_WPARAM(wParam));
            return 0;
        }
        case WM_CAPTURECHANGED:
            Dispatch(s, message, s->mouseX, s->mouseY, 0, 0);
            return 0;
        }
    }
    catch (const std::bad_alloc &)
    {
        threadError = E_OUTOFMEMORY;
        s->lastError = E_OUTOFMEMORY;
    }
    catch (...)
    {
        threadError = E_FAIL;
        s->lastError = E_FAIL;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}
BOOL CALLBACK RegisterWindow(PINIT_ONCE, void *, void **)
{
    WNDCLASSEXW cls{sizeof(cls)};
    cls.style = CS_DBLCLKS;
    cls.lpfnWndProc = WindowProcedure;
    cls.hInstance = module;
    cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    cls.lpszClassName = ClassName;
    windowClass = RegisterClassExW(&cls);
    return windowClass != 0;
}
} // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void *)
{
    // /MT CRT requires normal thread notifications.
    if (reason == DLL_PROCESS_ATTACH)
        module = instance;
    return TRUE;
}

HWND __cdecl Flip_Create(HWND parent, FlipInputCallback callback, void *context, const wchar_t *logPath,
                         uint32_t backgroundArgb)
{
    HWND window = nullptr;
    try
    {
        if (!parent || !IsWindow(parent) || !callback)
        {
            threadError = E_INVALIDARG;
            return nullptr;
        }
        if (GetWindowThreadProcessId(parent, nullptr) != GetCurrentThreadId())
        {
            threadError = RPC_E_WRONG_THREAD;
            return nullptr;
        }
        if (!InitOnceExecuteOnce(&classOnce, RegisterWindow, nullptr, nullptr))
        {
            threadError = Win32Error();
            return nullptr;
        }
        auto s = std::make_unique<Surface>(backgroundArgb);
        s->callback = callback;
        s->context = context;
        s->cursor = LoadCursorW(nullptr, IDC_ARROW);
        if (!s->cursor)
        {
            threadError = Win32Error();
            return nullptr;
        }
        if (logPath && *logPath)
        {
            s->logFile = CreateFileW(logPath, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW,
                                     FILE_ATTRIBUTE_NORMAL, nullptr);
            if (s->logFile == INVALID_HANDLE_VALUE)
            {
                threadError = Win32Error();
                return nullptr;
            }
            s->records.reserve(4096);
        }
        window = CreateWindowExW(0, ClassName, L"Landing Stats chart",
                                 WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN, 0, 0, 1, 1, parent, nullptr,
                                 module, s.get());
        if (!window)
        {
            threadError = Win32Error();
            return nullptr;
        }
        s->owned = true;
        Surface *raw = s.release();
        // The HWND must survive transient GPU initialization failures. The
        // first explicit Present creates the device and can report pending
        // recovery without replacing this child with a permanent placeholder.
        raw->initialized = true;
        threadError = S_OK;
        return window;
    }
    catch (const std::bad_alloc &)
    {
        threadError = E_OUTOFMEMORY;
    }
    catch (...)
    {
        threadError = E_FAIL;
    }
    const HRESULT error = threadError;
    if (window && IsWindow(window))
        DestroyWindow(window);
    threadError = error;
    return nullptr;
}
void __cdecl Flip_Destroy(HWND window)
{
    auto *s = GetSurface(window);
    if (!s)
        return;
    s->destroying = true;
    if (!DestroyWindow(window))
        threadError = Win32Error();
}
HRESULT __cdecl Flip_Resize(HWND window, int width, int height)
{
    auto *s = GetSurface(window);
    if (!s)
        return threadError;
    if (width < 0 || height < 0)
        return s->Remember(E_INVALIDARG, "Resize dimensions");
    if (width == s->width && height == s->height)
        return S_OK;
    s->lastError = S_OK;
    if (!SetWindowPos(window, nullptr, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE))
        return s->Remember(Win32Error(), "SetWindowPos resize");
    return s->lastError;
}
HRESULT __cdecl Flip_UploadLayer(HWND window, int id, const void *bgra, int width, int height, int stride, float x,
                                 float y)
{
    auto *s = GetSurface(window);
    if (!s)
        return threadError;
    try
    {
        if (id < 0 || !bgra || width <= 0 || height <= 0 || width > INT_MAX / 4 || stride < width * 4 ||
            !std::isfinite(x) || !std::isfinite(y))
            return s->Remember(E_INVALIDARG, "UploadLayer arguments");
        const size_t rowBytes = static_cast<size_t>(width) * 4;
        std::vector<unsigned char> pixels(rowBytes * static_cast<size_t>(height));
        for (int row = 0; row < height; ++row)
            std::memcpy(pixels.data() + static_cast<size_t>(row) * rowBytes,
                        static_cast<const unsigned char *>(bgra) + static_cast<size_t>(row) * stride, rowBytes);
        auto &layer = s->layers[id];
        if (layer.width != width || layer.height != height)
            Release(layer.texture);
        layer.width = width;
        layer.height = height;
        layer.x = x;
        layer.y = y;
        layer.pixels = std::move(pixels);
        layer.dirty = true;
        ++s->diagnostics.uploadCalls;
        s->diagnostics.uploadedBytes += rowBytes * static_cast<size_t>(height);
        // GPU uploads belong to the explicit batch Present. CPU layers remain
        // current while a lost device waits for the next real window event.
        return s->Remember(S_OK, "UploadLayer");
    }
    catch (const std::bad_alloc &)
    {
        return s->DeferFailure(s->Remember(E_OUTOFMEMORY, "UploadLayer allocation"));
    }
    catch (...)
    {
        return s->Remember(E_FAIL, "UploadLayer exception");
    }
}
HRESULT __cdecl Flip_RemoveLayer(HWND window, int id)
{
    auto *s = GetSurface(window);
    if (!s)
        return threadError;
    return s->layers.erase(id) ? S_OK : S_FALSE;
}
HRESULT __cdecl Flip_Present(HWND window, UINT reason)
{
    auto *s = GetSurface(window);
    if (!s)
        return threadError;
    try
    {
        return s->TryPresent(reason);
    }
    catch (const std::bad_alloc &)
    {
        return s->Remember(E_OUTOFMEMORY, "Present allocation");
    }
    catch (...)
    {
        return s->Remember(E_FAIL, "Present exception");
    }
}
HRESULT __cdecl Flip_LastError(HWND window)
{
    if (!window)
        return threadError;
    auto *s = GetSurface(window);
    return s ? s->lastError : threadError;
}
HRESULT __cdecl Flip_GetDiagnostics(HWND window, FlipDiagnostics *diagnostics, UINT size)
{
    auto *s = GetSurface(window);
    if (!s)
        return threadError;
    if (!diagnostics || size < sizeof(FlipDiagnostics))
        return E_INVALIDARG;
    s->diagnostics.lastError = s->lastError;
    s->diagnostics.layerCount = static_cast<uint32_t>(s->layers.size());
    *diagnostics = s->diagnostics;
    return S_OK;
}
HRESULT __cdecl Flip_GetPathInfo(HWND window, wchar_t *buffer, UINT characters)
{
    auto *s = GetSurface(window);
    if (!s)
        return threadError;
    if (!buffer || characters <= s->pathInfo.size())
        return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    std::memcpy(buffer, s->pathInfo.c_str(), (s->pathInfo.size() + 1) * sizeof(wchar_t));
    return S_OK;
}
HRESULT __cdecl Flip_SetCursor(HWND window, int kind)
{
    auto *s = GetSurface(window);
    if (!s)
        return threadError;
    if (kind != 0 && kind != 1)
        return s->Remember(E_INVALIDARG, "Cursor kind");
    const HCURSOR cursor = LoadCursorW(nullptr, kind == 1 ? IDC_CROSS : IDC_ARROW);
    if (!cursor)
        return s->Remember(Win32Error(), "LoadCursor");
    s->cursor = cursor;
    SetCursor(cursor);
    return S_OK;
}
