#include "../../src/LandingStats.Graphics.Native/ChartDeviceRecovery.h"
#include "../../src/LandingStats.Graphics.Native/ChartInitialBackground.h"
#include <algorithm>
#include <iostream>
#include <stdexcept>

using LandingStatsGraphics::ChartDeviceRecovery;
using LandingStatsGraphics::ChartRecoveryRetry;
using LandingStatsGraphics::DeviceRecoveryAction;
using LandingStatsGraphics::ChartInitialBackground;

namespace
{
void Check(bool condition, const char *message)
{
    if (!condition)
        throw std::runtime_error(message);
}

void InitialBackgroundPaintsOnlyBeforeTheFirstFlip()
{
    struct Canvas
    {
        HDC dc = CreateCompatibleDC(nullptr);
        HBITMAP bitmap = nullptr;
        HGDIOBJ previous = nullptr;
        uint32_t *pixels = nullptr;
        ~Canvas()
        {
            if (previous) SelectObject(dc, previous);
            if (bitmap) DeleteObject(bitmap);
            if (dc) DeleteDC(dc);
        }
    } canvas;
    Check(canvas.dc != nullptr, "The initial-paint test could not create a memory DC.");
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = 8;
    info.bmiHeader.biHeight = -8;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    void *pixels = nullptr;
    canvas.bitmap = CreateDIBSection(canvas.dc, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
    Check(canvas.bitmap != nullptr && pixels != nullptr, "The initial-paint test could not create its pixel buffer.");
    canvas.pixels = static_cast<uint32_t *>(pixels);
    canvas.previous = SelectObject(canvas.dc, canvas.bitmap);
    Check(canvas.previous != nullptr && canvas.previous != HGDI_ERROR, "The initial-paint test could not select its bitmap.");
    constexpr uint32_t themeArgb = 0xff132435u, previousPixel = 0x00556677u;
    ChartInitialBackground background(themeArgb);
    Check(background.ClearColor() == themeArgb, "GPU clears do not use the same theme color as the initial window.");
    const RECT area{2, 1, 6, 7};
    for (const HRESULT pending : {E_OUTOFMEMORY, D3DERR_OUTOFVIDEOMEMORY, D3DERR_DEVICELOST, S_FALSE})
    {
        std::fill_n(canvas.pixels, 64, previousPixel);
        background.RecordPresent(pending);
        Check(background.Paint(canvas.dc, area), "An initial temporary failure left the native window unpainted.");
        GdiFlush();
        for (int y = 0; y < 8; ++y)
            for (int x = 0; x < 8; ++x)
            {
                const auto expected = x >= area.left && x < area.right && y >= area.top && y < area.bottom
                                          ? themeArgb & 0x00ffffffu : previousPixel;
                Check((canvas.pixels[y * 8 + x] & 0x00ffffffu) == expected,
                      "The initial background used the wrong theme color or painted outside the invalidated region.");
            }
    }
    background.RecordPresent(S_OK);
    for (const HRESULT later : {S_OK, D3DERR_DEVICELOST, D3DERR_DEVICENOTRESET, D3DERR_OUTOFVIDEOMEMORY,
                                E_OUTOFMEMORY, S_PRESENT_MODE_CHANGED})
    {
        std::fill_n(canvas.pixels, 64, previousPixel);
        background.RecordPresent(later);
        Check(!background.Paint(canvas.dc, area), "GDI resumed drawing into an HWND already owned by FLIPEX.");
        GdiFlush();
        Check(std::all_of(canvas.pixels, canvas.pixels + 64, [previousPixel](uint32_t pixel) { return pixel == previousPixel; }),
              "A later device loss/reset modified flip-owned pixels using GDI.");
    }
}

void LostDeviceWaitsForAnExternalEvent()
{
    ChartDeviceRecovery recovery;
    recovery.RecordFailure(D3DERR_DEVICELOST);
    Check(recovery.Pending() && recovery.NextAction(true) == DeviceRecoveryAction::CheckDeviceState,
          "A lost device must retain recovery until its state can be checked.");
    for (const HRESULT state : {D3DERR_DEVICELOST, S_PRESENT_OCCLUDED})
    {
        Check(recovery.ObserveDeviceState(state) == DeviceRecoveryAction::Wait,
              "An unavailable or occluded device was reset before it became available.");
        Check(recovery.Pending() && recovery.NextAction(true) == DeviceRecoveryAction::CheckDeviceState,
              "A deferred attempt lost the next event's device-state check.");
        Check(recovery.Error() == state, "The pending device-state result was lost.");
    }
    Check(recovery.ObserveDeviceState(S_OK) == DeviceRecoveryAction::ResetDevice,
          "A device that became available was not reset.");
    recovery.RecordFailure(S_OK);
    Check(recovery.Pending(), "One successful operation completed recovery before all layers were restored.");
    recovery.Complete();
    Check(!recovery.Pending() && recovery.NextAction(true) == DeviceRecoveryAction::None && recovery.Error() == S_OK,
          "Successful restoration did not clear the pending error.");
}

void ResetAndRecreationKeepTheirOwnershipBoundaries()
{
    for (const HRESULT state : {D3DERR_DEVICENOTRESET, S_PRESENT_MODE_CHANGED})
    {
        ChartDeviceRecovery recovery;
        recovery.RecordFailure(D3DERR_DEVICELOST);
        Check(recovery.ObserveDeviceState(state) == DeviceRecoveryAction::ResetDevice,
              "A resettable device incorrectly discarded its Direct3D runtime.");
        Check(recovery.NextAction(false) == DeviceRecoveryAction::RecreateDevice,
              "A missing device must be created instead of dereferenced for ResetEx.");
    }
    ChartDeviceRecovery absent;
    absent.RecordFailure(D3DERR_DEVICELOST);
    Check(absent.NextAction(false) == DeviceRecoveryAction::RecreateDevice,
          "An interrupted initial device creation has no device to query.");
    absent.RecordFailure(D3DERR_DEVICENOTRESET);
    Check(absent.NextAction(true) == DeviceRecoveryAction::ResetDevice,
          "DEVICENOTRESET did not select ResetEx for a retained device.");
}

void HangAndMemoryPressureCanRecoverAfterRepeatedFailure()
{
    for (const HRESULT error : {D3DERR_DEVICEHUNG, D3DERR_OUTOFVIDEOMEMORY, E_OUTOFMEMORY})
    {
        ChartDeviceRecovery recovery;
        recovery.RecordFailure(error);
        Check(recovery.NextAction(true) == DeviceRecoveryAction::RecreateDevice &&
                  recovery.NextAction(false) == DeviceRecoveryAction::RecreateDevice,
              "A hung or memory-starved device must release GPU resources before recreation.");
        // The recreation can itself fail while the simulator still occupies VRAM.
        recovery.RecordFailure(D3DERR_OUTOFVIDEOMEMORY);
        Check(recovery.Pending() && recovery.NextAction(false) == DeviceRecoveryAction::RecreateDevice,
              "A failed recreation permanently disabled future event-driven attempts.");
        recovery.RecordFailure(S_FALSE);
        Check(recovery.Pending(), "A deferred operation falsely reported a restored frame.");
        recovery.Complete();
        Check(!recovery.Pending(), "Successful device and texture restoration remained unavailable.");
    }
}

void RemovalReenumeratesTheRuntimeEvenAfterAnotherFailure()
{
    for (const HRESULT error : {D3DERR_DEVICEREMOVED, D3DERR_DRIVERINTERNALERROR})
    {
        ChartDeviceRecovery recovery;
        recovery.RecordFailure(D3DERR_DEVICELOST);
        Check(recovery.ObserveDeviceState(error) == DeviceRecoveryAction::RecreateRuntime,
              "Device removal discovered during recovery retained the stale adapter list.");
        Check(recovery.NextAction(false) == DeviceRecoveryAction::RecreateRuntime,
              "The lack of a device incorrectly cancelled runtime recreation.");
        recovery.RecordFailure(error);
        Check(recovery.Pending() && recovery.Error() == error, "Repeated driver failure discarded the recovery cause.");
        recovery.Complete();
        Check(!recovery.Pending(), "Recreated runtime never returned to normal presentation.");
    }
}

void FatalFailuresRemainFatal()
{
    for (const HRESULT error : {D3DERR_INVALIDCALL, D3DERR_NOTAVAILABLE, E_INVALIDARG, E_HANDLE, E_FAIL})
    {
        Check(!ChartDeviceRecovery::IsRecoverable(error), "A permanent error was converted into an endless retry.");
        ChartDeviceRecovery recovery;
        recovery.RecordFailure(D3DERR_DEVICELOST);
        Check(recovery.ObserveDeviceState(error) == DeviceRecoveryAction::Fatal && recovery.Error() == error,
              "An unexpected device-state failure did not remain observable as a fatal result.");
    }
    ChartDeviceRecovery healthy;
    healthy.RecordFailure(S_OK);
    healthy.RecordFailure(S_PRESENT_OCCLUDED);
    Check(!healthy.Pending(), "Successful or occluded presentation invented a device fault.");
}

void RetryBurstDoesNotMoveDeadline()
{
    ChartRecoveryRetry retry;
    retry.RecordFailure(D3DERR_OUTOFVIDEOMEMORY, 1000);
    const auto due = retry.DueMilliseconds();
    Check(due == 1250, "The first outage retry has no short bounded deadline.");
    for (ULONGLONG event = 0; event < 10000; ++event)
    {
        const auto now = 1000 + event % 250;
        Check(!retry.CanAttempt(now), "A burst of replay/input events bypassed the retry deadline.");
        // Even another CPU allocation failure must not keep postponing GPU work.
        retry.RecordFailure(E_OUTOFMEMORY, now);
        Check(retry.DueMilliseconds() == due && retry.Error() == D3DERR_OUTOFVIDEOMEMORY,
              "A suppressed event changed the pending error or restarted its deadline.");
    }
    Check(retry.CanAttempt(due) && retry.RemainingMilliseconds(due) == 0,
          "A quiet window cannot retry when its scheduled deadline arrives.");
}

void SustainedFailureIsRateLimited()
{
    for (const HRESULT failure : {D3DERR_OUTOFVIDEOMEMORY, E_OUTOFMEMORY, D3DERR_DEVICEHUNG})
    {
        ChartRecoveryRetry retry;
        unsigned attempts = 0;
        DWORD previousDelay = 0;
        // Model a replay producing one event per millisecond for a minute.
        for (ULONGLONG now = 0; now < 60000; ++now)
        {
            if (!retry.CanAttempt(now)) continue;
            ++attempts;
            retry.RecordFailure(failure, now);
            const auto delay = retry.RemainingMilliseconds(now);
            Check(delay >= ChartRecoveryRetry::InitialDelayMilliseconds &&
                      delay <= ChartRecoveryRetry::MaximumDelayMilliseconds && delay >= previousDelay,
                  "Repeated GPU failure lost the bounded increasing recovery interval.");
            previousDelay = delay;
        }
        Check(attempts <= 34 && attempts >= 30,
              "Sustained memory pressure recreated a device per replay tick or stopped eventual retries.");
        Check(previousDelay == ChartRecoveryRetry::MaximumDelayMilliseconds,
              "Long outages did not reach the maximum two-second recovery interval.");
    }
}

void SuccessAndOcclusionPreserveRetryOwnership()
{
    ChartRecoveryRetry retry;
    retry.RecordFailure(D3DERR_DEVICELOST, 0);
    retry.RecordFailure(D3DERR_DEVICELOST, 250);
    Check(retry.DueMilliseconds() == 750, "A second failed attempt did not back off.");
    // Occlusion is deferred work, not a displayed frame. The native caller
    // pauses the timer until a real show/paint; replay still obeys this deadline.
    retry.Defer(750);
    retry.SuspendAutomaticRetry();
    Check(retry.Pending() && !retry.CanAttempt(1249) && retry.CanAttempt(1250),
          "Occlusion cleared recovery or allowed immediate replay-driven polling.");
    for (ULONGLONG now = 751; now < 1250; ++now)
    {
        retry.RecordFailure(D3DERR_DEVICELOST, now);
        Check(!retry.CanRetryAutomatically(), "Replay rearmed the timer while the surface remained occluded.");
    }
    retry.ResumeAutomaticRetry();
    Check(retry.CanRetryAutomatically(), "A real visibility/input event could not resume automatic recovery.");
    retry.Complete();
    Check(!retry.Pending() && !retry.CanRetryAutomatically() && retry.CanAttempt(1250) && retry.Error() == S_OK,
          "A successful Present retained recovery scheduling on the healthy path.");
    retry.RecordFailure(D3DERR_OUTOFVIDEOMEMORY, 10000);
    Check(retry.RemainingMilliseconds(10000) == ChartRecoveryRetry::InitialDelayMilliseconds,
          "A new outage inherited the previous outage's long delay.");
    retry.Complete();
    retry.RecordFailure(E_INVALIDARG, 11000);
    Check(!retry.Pending(), "A fatal API error scheduled an automatic retry.");
}
} // namespace

int main()
{
    try
    {
        LostDeviceWaitsForAnExternalEvent();
        ResetAndRecreationKeepTheirOwnershipBoundaries();
        HangAndMemoryPressureCanRecoverAfterRepeatedFailure();
        RemovalReenumeratesTheRuntimeEvenAfterAnotherFailure();
        FatalFailuresRemainFatal();
        RetryBurstDoesNotMoveDeadline();
        SustainedFailureIsRateLimited();
        SuccessAndOcclusionPreserveRetryOwnership();
        InitialBackgroundPaintsOnlyBeforeTheFirstFlip();
        std::cout << "Native chart device recovery and initial paint: 9 scenario groups passed (no GPU or application profile used).\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << "Native chart device recovery FAILED: " << error.what() << '\n';
        return 1;
    }
}
