#pragma once
#include <stdint.h>
#include <windows.h>

namespace LandingStatsGraphics
{
// GDI owns only the initial empty window. Once FLIPEX has presented a frame,
// this HWND must never mix GDI drawing with its flip-model swap chain, even
// during subsequent device loss/reset. The surrounding WPF UI shows status.
class ChartInitialBackground
{
    uint32_t argb_;
    bool hasPresentedFrame_ = false;

public:
    explicit ChartInitialBackground(uint32_t argb) noexcept : argb_(argb | 0xff000000u) { }
    uint32_t ClearColor() const noexcept { return argb_; }
    void RecordPresent(HRESULT result) noexcept
    {
        if (result == S_OK)
            hasPresentedFrame_ = true;
    }
    bool Paint(HDC target, const RECT &area) const noexcept
    {
        if (hasPresentedFrame_)
            return false;
        const COLORREF color = RGB((argb_ >> 16) & 0xff, (argb_ >> 8) & 0xff, argb_ & 0xff);
        const COLORREF previous = SetDCBrushColor(target, color);
        if (previous == CLR_INVALID)
            return false;
        const bool painted = FillRect(target, &area, static_cast<HBRUSH>(GetStockObject(DC_BRUSH))) != 0;
        SetDCBrushColor(target, previous);
        return painted;
    }
};
} // namespace LandingStatsGraphics
