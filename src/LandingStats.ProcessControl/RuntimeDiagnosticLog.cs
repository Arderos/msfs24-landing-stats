using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace LandingStats.App.Runtime;

// Rare lifecycle/failure records are flushed before a guardian can retire the
// process. Never put pipe capabilities, command lines or payloads in this log.
internal static class RuntimeDiagnosticLog
{
    private const long MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumRecordCharacters = 16384;
    private static readonly object Gate = new object();
    private static string? _processIdentity;

    public static void Write(string stream, string message, Exception? error = null)
    {
        try
        {
            Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MSFS Landing Stats", "Runtime", stream + ".log"), message, error);
        }
        catch (Exception) { /* Resolving the log location must not interrupt recovery. */ }
    }

    internal static void Append(string path, string message, Exception? error = null)
    {
        try
        {
            var detail = message + (error == null ? "" : " error=" + error);
            if (detail.Length > MaximumRecordCharacters) detail = detail.Substring(0, MaximumRecordCharacters) + " [truncated]";
            detail = detail.Replace("\r", "\\r").Replace("\n", "\\n");
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
                {
                    var previous = path + ".previous";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                }
                File.AppendAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " +
                    (_processIdentity ??= Identity()) + " qpc=" + Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture) + " " + detail + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch (Exception) { /* Diagnostic I/O must not take ownership of recovery. */ }
    }

    public static string Age(long now, long timestamp) => timestamp == 0 ? "missing" :
        ((now - timestamp) * 1000d / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture);

    private static string Identity()
    {
        using var process = Process.GetCurrentProcess();
        var assembly = Assembly.GetEntryAssembly() ?? typeof(RuntimeDiagnosticLog).Assembly;
        return "pid=" + process.Id.ToString(CultureInfo.InvariantCulture) + " version=" + assembly.GetName().Version +
            " build=" + assembly.ManifestModule.ModuleVersionId.ToString("N") + " qpc_frequency=" + Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture);
    }
}
