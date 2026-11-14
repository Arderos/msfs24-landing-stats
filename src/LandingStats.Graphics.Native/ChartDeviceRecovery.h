#pragma once
#include <d3d9.h>

namespace LandingStatsGraphics
{
enum class DeviceRecoveryAction
{
    None,
    CheckDeviceState,
    Wait,
    ResetDevice,
    RecreateDevice,
    RecreateRuntime,
    Fatal,
};

// Retained until device configuration and every CPU-backed layer are restored.
// The caller owns scheduling; this class only selects the required GPU action.
class ChartDeviceRecovery
{
    HRESULT error_ = S_OK;
    DeviceRecoveryAction action_ = DeviceRecoveryAction::None;

    static DeviceRecoveryAction Classify(HRESULT error) noexcept
    {
        switch (error)
        {
        case D3DERR_DEVICELOST:
            return DeviceRecoveryAction::CheckDeviceState;
        case D3DERR_DEVICENOTRESET:
            return DeviceRecoveryAction::ResetDevice;
        case D3DERR_DEVICEHUNG:
        case D3DERR_OUTOFVIDEOMEMORY:
        case E_OUTOFMEMORY:
            return DeviceRecoveryAction::RecreateDevice;
        case D3DERR_DEVICEREMOVED:
        case D3DERR_DRIVERINTERNALERROR:
            return DeviceRecoveryAction::RecreateRuntime;
        default:
            return DeviceRecoveryAction::None;
        }
    }

public:
    static bool IsRecoverable(HRESULT error) noexcept { return Classify(error) != DeviceRecoveryAction::None; }
    bool Pending() const noexcept { return action_ != DeviceRecoveryAction::None; }
    HRESULT Error() const noexcept { return error_; }
    void RecordFailure(HRESULT error) noexcept
    {
        const auto action = Classify(error);
        if (action != DeviceRecoveryAction::None)
        {
            error_ = error;
            action_ = action;
        }
    }
    DeviceRecoveryAction NextAction(bool hasDevice) const noexcept
    {
        if (!hasDevice && (action_ == DeviceRecoveryAction::CheckDeviceState ||
                           action_ == DeviceRecoveryAction::ResetDevice))
            return DeviceRecoveryAction::RecreateDevice;
        return action_;
    }
    DeviceRecoveryAction ObserveDeviceState(HRESULT state) noexcept
    {
        error_ = state;
        if (state == D3DERR_DEVICELOST || state == S_PRESENT_OCCLUDED)
        {
            action_ = DeviceRecoveryAction::CheckDeviceState;
            return DeviceRecoveryAction::Wait;
        }
        if (state == S_OK || state == S_PRESENT_MODE_CHANGED || state == D3DERR_DEVICENOTRESET)
            action_ = DeviceRecoveryAction::ResetDevice;
        else
        {
            action_ = Classify(state);
            if (action_ == DeviceRecoveryAction::None) action_ = DeviceRecoveryAction::Fatal;
        }
        return action_;
    }
    void Complete() noexcept
    {
        error_ = S_OK;
        action_ = DeviceRecoveryAction::None;
    }
};

// A monotonic deadline bounds expensive retries independently of the rate of
// mouse, paint and replay events. Suppressed events cannot postpone the retry.
class ChartRecoveryRetry
{
    HRESULT error_ = S_OK;
    ULONGLONG due_ = 0;
    DWORD delay_ = 0;
    bool automaticRetrySuspended_ = false;

public:
    static constexpr DWORD InitialDelayMilliseconds = 250;
    static constexpr DWORD MaximumDelayMilliseconds = 2000;
    bool Pending() const noexcept { return error_ != S_OK; }
    bool CanRetryAutomatically() const noexcept { return Pending() && !automaticRetrySuspended_; }
    void SuspendAutomaticRetry() noexcept { automaticRetrySuspended_ = true; }
    void ResumeAutomaticRetry() noexcept { automaticRetrySuspended_ = false; }
    HRESULT Error() const noexcept { return error_; }
    ULONGLONG DueMilliseconds() const noexcept { return due_; }
    DWORD RemainingMilliseconds(ULONGLONG now) const noexcept
    {
        return !Pending() || now >= due_ ? 0 : static_cast<DWORD>(due_ - now);
    }
    bool CanAttempt(ULONGLONG now) const noexcept { return RemainingMilliseconds(now) == 0; }
    void RecordFailure(HRESULT error, ULONGLONG now) noexcept
    {
        if (!ChartDeviceRecovery::IsRecoverable(error)) return;
        // A partial CPU upload may fail before Present gets to its gate. Such
        // an event still must not restart the outstanding recovery deadline.
        if (Pending() && !CanAttempt(now)) return;
        delay_ = delay_ == 0 ? InitialDelayMilliseconds :
            (delay_ < MaximumDelayMilliseconds ? delay_ * 2 : MaximumDelayMilliseconds);
        error_ = error;
        due_ = now + delay_;
    }
    void Defer(ULONGLONG now) noexcept
    {
        if (Pending()) due_ = now + delay_;
    }
    void Complete() noexcept
    {
        error_ = S_OK;
        due_ = 0;
        delay_ = 0;
        automaticRetrySuspended_ = false;
    }
};
} // namespace LandingStatsGraphics
