using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LandingStats.App.Runtime;

namespace LandingStats.App.RegressionTests;

internal static class RuntimeDiagnosticsRegressionTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "runtime.log");
            try { throw new InvalidOperationException("owner failure", new IOException("original failure")); }
            catch (Exception error) { RuntimeDiagnosticLog.Append(path, "owner_failed component=fixture\r\nforged_record", error); }
            var lines = File.ReadAllLines(path);
            Check(lines.Length == 1 && lines[0].Contains("owner_failed component=fixture\\r\\nforged_record"), "Multiline diagnostics split a record.");
            Check(new[] { "pid=", "version=", "build=", "qpc=", "qpc_frequency=", "owner failure", "original failure", nameof(RuntimeDiagnosticsRegressionTests) }
                .All(lines[0].Contains), "Process identity, nested exception or stack trace was lost.");
            Parallel.For(0, 100, index => RuntimeDiagnosticLog.Append(path, "parallel_record=" + index));
            Check(File.ReadAllLines(path).Length == 101, "Concurrent diagnostic writes were lost.");
            File.WriteAllText(path, new string('x', 2 * 1024 * 1024), new UTF8Encoding(false));
            RuntimeDiagnosticLog.Append(path, "after_rotation");
            Check(File.ReadAllText(path).Contains("after_rotation") && new FileInfo(path + ".previous").Length == 2 * 1024 * 1024,
                "Rotation lost the preceding diagnostic window.");
            File.WriteAllText(path, new string('y', 2 * 1024 * 1024), new UTF8Encoding(false));
            RuntimeDiagnosticLog.Append(path, new string('z', 100000));
            Check(File.ReadAllText(path).Contains("[truncated]") && new FileInfo(path).Length < 20000 &&
                File.ReadAllText(path + ".previous")[0] == 'y', "Log retention or record size is unbounded.");
            // A file where the parent directory should be simulates inaccessible diagnostics.
            RuntimeDiagnosticLog.Append(Path.Combine(path, "blocked.log"), "must_not_throw");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                RuntimeDiagnosticLog.Append(path, "locked_log_must_not_throw");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
