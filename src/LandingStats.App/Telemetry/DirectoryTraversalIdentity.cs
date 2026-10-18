using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LandingStats.App.Telemetry;

internal static class DirectoryTraversalIdentity
{
    internal static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0) return full;
            using var handle = CreateFile(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) return full;
            var buffer = new StringBuilder(32768); var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (count == 0 || count >= buffer.Capacity) return full;
            var target = buffer.ToString();
            return target.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + target.Substring(8) :
                target.StartsWith(@"\\?\", StringComparison.Ordinal) ? target.Substring(4) : target;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return full; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
