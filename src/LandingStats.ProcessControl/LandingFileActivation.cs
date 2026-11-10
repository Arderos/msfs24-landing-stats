using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LandingStats.App.Runtime;

// File-open intents outlive launcher/App handoff and guardian recovery. The
// single application owner acknowledges a request only after the UI's import
// operation completes. Re-delivery after a crash is safe at the import boundary.
internal sealed class LandingFileActivation : IDisposable
{
    private const int Magic = 0x3141464c; // LFA1
    private const int MaximumRequestBytes = 1024 * 1024;
    private const string ReceiverIdentityFile = ".receiver";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly string _root, _signalName;
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private EventWaitHandle? _signal;
    private Task _completion = Task.CompletedTask;
    private int _disposed;

    internal LandingFileActivation(string? root = null, string? signalName = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats", "FileImports", Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _signalName = signalName ?? DefaultSignalName();
    }

    internal Task Completion => _completion;
    internal string RootPath => _root;

    internal static bool IsLandingFile(string argument) =>
        argument != null && argument.EndsWith(LandingFileAssociation.Extension, StringComparison.OrdinalIgnoreCase);

    internal static string[] FileArguments(IEnumerable<string> arguments) => arguments.Where(IsLandingFile)
        .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string[] WithoutFileArguments(IEnumerable<string> arguments) => arguments.Where(argument => !IsLandingFile(argument)).ToArray();

    internal void Submit(IReadOnlyList<string> files)
    {
        if (files == null) throw new ArgumentNullException(nameof(files));
        if (files.Count == 0) return;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Utf8, true))
        {
            writer.Write(Magic); writer.Write(files.Count);
            foreach (var path in files)
            {
                var fullPath = NormalizePath(path);
                var encoded = Utf8.GetBytes(fullPath);
                writer.Write(encoded.Length); writer.Write(encoded);
                if (bytes.Length > MaximumRequestBytes) throw new InvalidDataException("The file-open request exceeds the Windows activation envelope.");
            }
        }
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".request");
        var temporary = destination + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { bytes.Position = 0; bytes.CopyTo(file); file.Flush(true); }
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        AllowReceiverForeground(processId => AllowSetForegroundWindow(processId));
        try
        {
            using var signal = EventWaitHandle.OpenExisting(_signalName, EventWaitHandleRights.Modify);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The App may not exist yet (or be recovering). Its first drain
            // observes the already committed request, independently of signals.
        }
    }

    internal void Start(Func<string[], Task> handle, Action<Exception> failed)
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (failed == null) throw new ArgumentNullException(nameof(failed));
        if (_signal != null || Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("File activation is already started or disposed.");
        Directory.CreateDirectory(_root);
        _signal = GuardianSignals.CreateOrOpenActivationEvent(_signalName);
        try { PublishReceiverIdentity(); }
        catch { _signal.Dispose(); _signal = null; throw; }
        _completion = Task.Run(async () =>
        {
            var handles = new WaitHandle[] { _signal!, _stop.Token.WaitHandle };
            while (!_stop.IsCancellationRequested)
            {
                string[] pending;
                try { pending = Directory.GetFiles(_root, "*.request").OrderBy(path => path, StringComparer.Ordinal).ToArray(); }
                catch (Exception error) { failed(error); pending = Array.Empty<string>(); }
                foreach (var path in pending)
                {
                    if (_stop.IsCancellationRequested) return;
                    string[] files;
                    try { files = Read(path); }
                    catch (FileNotFoundException) { continue; }
                    catch (Exception error)
                    {
                        failed(error);
                        try { File.Move(path, path + ".invalid"); }
                        catch (Exception quarantineError) { failed(quarantineError); }
                        continue;
                    }
                    try
                    {
                        await handle(files).ConfigureAwait(false);
                        if (_stop.IsCancellationRequested) return;
                        File.Delete(path);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                    catch (Exception error) { failed(error); } // One batch must not hide other queued requests.
                }
                if (WaitHandle.WaitAny(handles) == 1) return;
            }
        });
    }

    private void PublishReceiverIdentity()
    {
        var destination = Path.Combine(_root, ReceiverIdentityFile);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var process = Process.GetCurrentProcess())
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new BinaryWriter(output, Utf8, true);
                writer.Write(process.Id); writer.Write(process.StartTime.ToUniversalTime().Ticks);
                writer.Flush(); output.Flush(true);
            }
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // The per-user inbox identifies the existing receiver for Windows' targeted
    // foreground grant. PID reuse or a retired receiver must not grant another
    // process that permission. This hint never controls durable delivery.
    internal bool AllowReceiverForeground(Action<int> grant)
    {
        if (grant == null) throw new ArgumentNullException(nameof(grant));
        try
        {
            using var input = new FileStream(Path.Combine(_root, ReceiverIdentityFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length != sizeof(int) + sizeof(long)) return false;
            using var reader = new BinaryReader(input, Utf8);
            var processId = reader.ReadInt32(); var started = reader.ReadInt64();
            if (processId <= 0) return false;
            using var receiver = Process.GetProcessById(processId);
            using var current = Process.GetCurrentProcess();
            if (receiver.SessionId != current.SessionId || receiver.StartTime.ToUniversalTime().Ticks != started) return false;
            grant(processId); return true;
        }
        // A cold launch or concurrent receiver exit can leave no live identity.
        // A foreground hint is optional; the request is still committed/signaled.
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static string[] Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 8 || input.Length > MaximumRequestBytes) throw new InvalidDataException("Invalid file-open request size.");
        var data = new byte[(int)input.Length];
        var read = 0;
        while (read < data.Length)
        {
            var count = input.Read(data, read, data.Length - read);
            if (count == 0) throw new EndOfStreamException("Truncated file-open request.");
            read += count;
        }
        using var memory = new MemoryStream(data, false);
        using var reader = new BinaryReader(memory, Utf8);
        if (reader.ReadInt32() != Magic) throw new InvalidDataException("Unsupported file-open request.");
        var paths = new List<string>(); var countOfPaths = reader.ReadInt32();
        if (countOfPaths <= 0 || countOfPaths > MaximumRequestBytes / 4) throw new InvalidDataException("Invalid file-open request count.");
        for (var index = 0; index < countOfPaths; index++)
        {
            var length = reader.ReadInt32();
            if (length <= 0 || length > 32767 * 4 || length > memory.Length - memory.Position)
                throw new InvalidDataException("Invalid file-open path size.");
            paths.Add(NormalizePath(Utf8.GetString(reader.ReadBytes(length))));
        }
        if (memory.Position != memory.Length) throw new InvalidDataException("Trailing file-open request data.");
        return paths.ToArray();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || !Path.IsPathRooted(path) || !IsLandingFile(path))
            throw new InvalidDataException("A full landing-file path is required.");
        return Path.GetFullPath(path);
    }

    private static string DefaultSignalName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return "Local\\MSFSLandingStats.FileImports." + (identity.User?.Value ?? throw new InvalidOperationException("Missing user identity."));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        // Do not synchronously wait on the WPF thread while an import is awaiting
        // its dispatcher. Request files remain durable until successful handling.
        _ = _completion.ContinueWith(_ => { _signal?.Dispose(); _stop.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
