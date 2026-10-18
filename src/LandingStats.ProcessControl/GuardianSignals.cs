using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Threading;

namespace LandingStats.App.Runtime;

// Separate from the health pipe: an explicit exit must survive that pipe
// breaking between the user's action and the guardian's next observation.
internal sealed class GuardianSignals : IDisposable
{
    private readonly EventWaitHandle[] _stop = new EventWaitHandle[4];
    private readonly EventWaitHandle _ack, _update;
    private GuardianSignals(GuardianDescriptor descriptor, bool create)
    {
        EventWaitHandle? ack = null, update = null;
        try
        {
            for (var i = 0; i < _stop.Length; i++) _stop[i] = Open("Local\\" + descriptor.Pipe + "-stop-" + (i + 1), create);
            ack = Open("Local\\" + descriptor.Pipe + "-stop-ack", create);
            update = Open("Local\\" + descriptor.Pipe + "-update-accepted", create);
            _ack = ack; _update = update;
        }
        catch { foreach (var item in _stop) item?.Dispose(); ack?.Dispose(); update?.Dispose(); throw; }
    }
    public static GuardianSignals Create(GuardianDescriptor descriptor) => new GuardianSignals(descriptor, true);
    public static GuardianSignals Open(GuardianDescriptor descriptor) => new GuardianSignals(descriptor, false);
    internal static EventWaitHandle CreateEvent(string name, EventResetMode mode = EventResetMode.ManualReset, bool allowMediumIntegrity = false)
    {
        var security = new EventWaitHandleSecurity(); security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new EventWaitHandleAccessRule(identity.User ?? throw new InvalidOperationException("Missing user identity."), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        var handle = new EventWaitHandle(false, mode, name, out var created, security);
        if (!created) { handle.Dispose(); throw new InvalidOperationException("Guardian signal identity is already in use."); }
        if (allowMediumIntegrity)
        {
            // LABEL_SECURITY_INFORMATION needs WRITE_OWNER, not the audit SACL
            // privilege requested by the managed event-security constructor.
            try { SetActivationIntegrity(handle); } catch { handle.Dispose(); throw; }
        }
        return handle;
    }
    private static void SetActivationIntegrity(EventWaitHandle handle)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;ME)", 1, out var descriptor, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var label, out _) || !present)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            // Only the activation hint crosses integrity levels. Authenticated
            // stop/update events retain their original process integrity label.
            var error = SetSecurityInfo(handle.SafeWaitHandle.DangerousGetHandle(), 6, 0x10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, label);
            if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
        }
        finally { LocalFree(descriptor); }
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(IntPtr handle, int type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    private static EventWaitHandle Open(string name, bool create) => create ? CreateEvent(name) :
        EventWaitHandle.OpenExisting(name, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize);
    public void RequestStop(GuardianStopReason reason)
    {
        if (reason < GuardianStopReason.UserExit || reason > GuardianStopReason.Update) throw new ArgumentOutOfRangeException(nameof(reason));
        _stop[(int)reason - 1].Set();
    }
    public GuardianStopReason ObserveStop()
    {
        for (var i = 0; i < _stop.Length; i++)
            if (_stop[i].WaitOne(0) && (i != 3 || UpdateAccepted)) return (GuardianStopReason)(i + 1);
        return GuardianStopReason.None;
    }
    public void AcknowledgeStop() => _ack.Set();
    public bool StopAcknowledged => _ack.WaitOne(0);
    public bool UpdateAccepted => _update.WaitOne(0);
    public void AcceptUpdate() => _update.Set();
    public void Dispose() { foreach (var item in _stop) item.Dispose(); _ack.Dispose(); _update.Dispose(); }
}
