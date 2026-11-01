using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LandingStats.App.Runtime;

// Shared with the portable launcher: registration must name the executable the
// user moved, not the launcher's replaceable extracted runtime.
internal static class LandingFileAssociation
{
    internal const string Extension = ".lslanding";
    internal const string ProgId = "MSFSLandingStats.Landing";

    internal static bool Register(string executable)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes", true) ??
            throw new IOException("The current user's file associations are unavailable.");
        var changed = Register(classes, executable);
        if (changed) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED / SHCNF_IDLIST
        return changed;
    }

    // The root is injectable so regression tests never touch real associations.
    internal static bool Register(RegistryKey classes, string executable)
    {
        if (classes == null) throw new ArgumentNullException(nameof(classes));
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable) || executable.IndexOf('"') >= 0)
            throw new ArgumentException("A fully qualified executable path is required.", nameof(executable));
        executable = Path.GetFullPath(executable);
        var changed = Set(classes, ProgId, "", "MSFS Landing Stats landing");
        changed |= Set(classes, ProgId + @"\DefaultIcon", "", "\"" + executable + "\",0");
        changed |= Set(classes, ProgId + @"\shell", "", "open");
        changed |= Set(classes, ProgId + @"\shell\open\command", "", "\"" + executable + "\" \"%1\"");
        changed |= Set(classes, Extension, "", ProgId);
        changed |= Set(classes, Extension + @"\OpenWithProgids", ProgId, "");
        // UserChoice belongs to Windows. A user-selected different default is
        // respected; our ProgID and icon still follow the portable executable.
        return changed;
    }

    private static bool Set(RegistryKey root, string path, string name, string value)
    {
        using var key = root.CreateSubKey(path, true) ?? throw new IOException("Cannot update file association " + path + ".");
        if (string.Equals(key.GetValue(name) as string, value, StringComparison.Ordinal) && key.GetValueKind(name) == RegistryValueKind.String)
            return false;
        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
