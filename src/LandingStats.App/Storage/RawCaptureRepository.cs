using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Core;

namespace LandingStats.App.Storage;

public sealed class RawCaptureRepository
{
    public const int ChunkMaximumSamples = 30000;

    public RawCaptureRepository(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSFS Landing Stats",
            "Telemetry Queue");
    }

    public string RootPath { get; }

    public RawCaptureSession StartCapture(
        IReadOnlyList<TelemetrySample> initialSamples,
        string simulator,
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<string> controlInputSources,
        DateTime timestampUtc)
    {
        Directory.CreateDirectory(RootPath);
        var session = new RawCaptureSession(
            RootPath,
            timestampUtc,
            simulator,
            aircraftTitle,
            aircraftType,
            aircraftModel,
            controlInputSources);
        foreach (var sample in initialSamples ?? Array.Empty<TelemetrySample>())
        {
            session.Write(sample);
        }

        return session;
    }

    internal static string ApplicationVersion()
    {
        var assembly = typeof(RawCaptureRepository).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return informational?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }

    internal static string ApplicationAuthor()
    {
        var assembly = typeof(RawCaptureRepository).Assembly;
        return assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "unknown";
    }

    internal static string SingleLine(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value!.Trim().Replace("\r", " ").Replace("\n", " ");
    }
}

public sealed class RawCaptureChunkEventArgs : EventArgs
{
    public RawCaptureChunkEventArgs(string path, int sampleCount, bool captureContinues)
    {
        Path = path;
        SampleCount = sampleCount;
        CaptureContinues = captureContinues;
    }

    public string Path { get; }

    public int SampleCount { get; }

    public bool CaptureContinues { get; }
}

public sealed class RawCaptureSession : IDisposable
{
    private const int QueueCapacity = 2048;

    private readonly string _rootPath;
    private readonly string _simulator;
    private readonly string _aircraftTitle;
    private readonly string _aircraftType;
    private readonly string _aircraftModel;
    private readonly IReadOnlyList<string> _controlInputSources;
    private readonly BlockingCollection<TelemetrySample> _samples =
        new BlockingCollection<TelemetrySample>(new ConcurrentQueue<TelemetrySample>(), QueueCapacity);
    private readonly Task _writerTask;
    private DateTime _nextChunkStartedUtc;
    private Exception? _failure;
    private int _disposed;

    internal RawCaptureSession(
        string rootPath,
        DateTime timestampUtc,
        string simulator,
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<string> controlInputSources)
    {
        _rootPath = rootPath;
        _nextChunkStartedUtc = timestampUtc;
        _simulator = simulator;
        _aircraftTitle = aircraftTitle;
        _aircraftType = aircraftType;
        _aircraftModel = aircraftModel;
        _controlInputSources = new List<string>(controlInputSources ?? Array.Empty<string>());
        _writerTask = Task.Run(WriteLoop);
    }

    public event EventHandler<RawCaptureChunkEventArgs>? ChunkCompleted;

    public event Action<Exception>? Failed;

    public Exception? Failure => Volatile.Read(ref _failure);

    public void Write(TelemetrySample sample)
    {
        if (sample == null)
        {
            throw new ArgumentNullException(nameof(sample));
        }

        if (Volatile.Read(ref _disposed) != 0 || Failure != null)
        {
            return;
        }

        try
        {
            _samples.Add(sample);
        }
        catch (InvalidOperationException)
        {
            // The capture was stopped concurrently with the final SimConnect callback.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _samples.CompleteAdding();
        try
        {
            _writerTask.GetAwaiter().GetResult();
        }
        finally
        {
            _samples.Dispose();
        }
    }

    private void WriteLoop()
    {
        StreamingChunk? chunk = null;
        try
        {
            foreach (var sample in _samples.GetConsumingEnumerable())
            {
                chunk ??= new StreamingChunk(
                    _rootPath,
                    _nextChunkStartedUtc,
                    _simulator,
                    _aircraftTitle,
                    _aircraftType,
                    _aircraftModel,
                    _controlInputSources);
                chunk.Write(sample);
                if (chunk.SampleCount < RawCaptureRepository.ChunkMaximumSamples)
                {
                    continue;
                }

                var completed = chunk.Complete();
                chunk = null;
                _nextChunkStartedUtc = DateTime.UtcNow;
                RaiseChunkCompleted(completed.Path, completed.SampleCount, true);
            }

            if (chunk != null && chunk.SampleCount > 0)
            {
                var completed = chunk.Complete();
                chunk = null;
                RaiseChunkCompleted(completed.Path, completed.SampleCount, false);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _failure, exception);
            chunk?.Dispose();
            try
            {
                Failed?.Invoke(exception);
            }
            catch
            {
                // A UI error handler must not replace the storage failure.
            }
        }
    }

    private void RaiseChunkCompleted(string path, int count, bool continues)
    {
        try
        {
            ChunkCompleted?.Invoke(this, new RawCaptureChunkEventArgs(path, count, continues));
        }
        catch
        {
            // Storage is complete even if a presentation callback fails.
        }
    }

    private sealed class StreamingChunk : IDisposable
    {
        private readonly string _path;
        private readonly string _temporaryPath;
        private readonly DateTime _timestampUtc;
        private readonly string _simulator;
        private readonly string _aircraftTitle;
        private readonly string _aircraftType;
        private readonly string _aircraftModel;
        private readonly IReadOnlyList<string> _controlInputSources;
        private FileStream? _file;
        private ZipArchive? _archive;
        private StreamWriter? _telemetryWriter;
        private bool _completed;

        public StreamingChunk(
            string rootPath,
            DateTime timestampUtc,
            string simulator,
            string aircraftTitle,
            string aircraftType,
            string aircraftModel,
            IReadOnlyList<string> controlInputSources)
        {
            _timestampUtc = timestampUtc;
            _simulator = simulator;
            _aircraftTitle = aircraftTitle;
            _aircraftType = aircraftType;
            _aircraftModel = aircraftModel;
            _controlInputSources = controlInputSources;
            var captureId = Guid.NewGuid().ToString("N");
            var safeTimestamp = timestampUtc.ToString("yyyyMMdd_HHmmss'Z'", CultureInfo.InvariantCulture);
            _path = Path.Combine(rootPath, $"{safeTimestamp}_{captureId}_raw.zip");
            _temporaryPath = _path + ".tmp";
            _file = new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _archive = new ZipArchive(_file, ZipArchiveMode.Create, true);
            var entry = _archive.CreateEntry("telemetry.csv", CompressionLevel.Optimal);
            _telemetryWriter = new StreamWriter(entry.Open(), new UTF8Encoding(false), 65536, false);
            _telemetryWriter.WriteLine(TelemetryCsv.Header);
        }

        public int SampleCount { get; private set; }

        public void Write(TelemetrySample sample)
        {
            _telemetryWriter!.WriteLine(TelemetryCsv.Format(sample));
            SampleCount++;
        }

        public (string Path, int SampleCount) Complete()
        {
            _telemetryWriter!.Dispose();
            _telemetryWriter = null;
            WriteSession();
            _archive!.Dispose();
            _archive = null;
            _file!.Dispose();
            _file = null;
            File.Move(_temporaryPath, _path);
            _completed = true;
            return (_path, SampleCount);
        }

        public void Dispose()
        {
            _telemetryWriter?.Dispose();
            _archive?.Dispose();
            _file?.Dispose();
            _telemetryWriter = null;
            _archive = null;
            _file = null;
            if (!_completed && File.Exists(_temporaryPath))
            {
                File.Delete(_temporaryPath);
            }
        }

        private void WriteSession()
        {
            var entry = _archive!.CreateEntry("session.txt", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false), 4096, false))
            {
                writer.WriteLine($"Generated by MSFS Landing Stats {RawCaptureRepository.ApplicationVersion()} by {RawCaptureRepository.ApplicationAuthor()}");
                writer.WriteLine($"capture_utc={_timestampUtc.ToString("O", CultureInfo.InvariantCulture)}");
                writer.WriteLine($"telemetry_schema={TelemetryCsv.SchemaVersion.ToString(CultureInfo.InvariantCulture)}");
                writer.WriteLine("source_period=SIM_FRAME");
                writer.WriteLine("sample_policy=all_received_frames_no_downsampling");
                writer.WriteLine($"sample_count={SampleCount.ToString(CultureInfo.InvariantCulture)}");
                writer.WriteLine($"simulator={RawCaptureRepository.SingleLine(_simulator)}");
                writer.WriteLine($"aircraft_title={RawCaptureRepository.SingleLine(_aircraftTitle)}");
                writer.WriteLine($"aircraft_type={RawCaptureRepository.SingleLine(_aircraftType)}");
                writer.WriteLine($"aircraft_model={RawCaptureRepository.SingleLine(_aircraftModel)}");
                writer.WriteLine($"control_input_source_count={_controlInputSources.Count.ToString(CultureInfo.InvariantCulture)}");
                for (var index = 0; index < _controlInputSources.Count; index++)
                {
                    writer.WriteLine($"control_input_source_{index.ToString(CultureInfo.InvariantCulture)}={RawCaptureRepository.SingleLine(_controlInputSources[index])}");
                }
            }
        }
    }
}
