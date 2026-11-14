using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.Archives;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class PausedRawArchiveRegressionTests
{
    internal static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "paused-raw-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var knownState in new[] { false, true })
            {
                // SIM_FRAME continues during a pause. Preserve every host-time
                // observation even when the simulator clock and pose are fixed.
                var samples = Enumerable.Range(0, RawCaptureRepository.ChunkMaximumSamples).Select(index => new TelemetrySample
                {
                    Sequence = index + 1, HostElapsedSeconds = index / 30.0, SimulationTimeSeconds = 120,
                    AnalysisTimeSeconds = knownState ? 120 : double.NaN,
                    SimulatorState = knownState ? TelemetrySimulatorState.Paused : TelemetrySimulatorState.Legacy,
                    PauseFlags = knownState ? 1 : -1, MotionSimulation = false,
                    AboveGroundLevelFeet = 300, PlaneAltitudeFeet = 1300, GroundAltitudeFeet = 1000,
                    LatitudeDegrees = 42, LongitudeDegrees = 23, GForce = 1,
                }).ToArray();
                foreach (var zstd in new[] { false, true })
                {
                    var directory = Path.Combine(root, knownState + "-" + zstd);
                    var archives = Path.Combine(directory, "archives");
                    var store = new RawChunkStore(Path.Combine(directory, "chunks"), archives,
                        new DurableOutbox(Path.Combine(directory, "outbox"), archives), () => zstd);
                    var path = store.Publish(Guid.NewGuid(), 1, new RawCaptureMetadata
                    {
                        TimestampUtcTicks = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc).Ticks,
                        Simulator = "MSFS", Title = "Synthetic paused RAW receiver contract",
                    }, samples);
                    using (var input = File.OpenRead(path))
                    using (var archive = new CaptureArchiveReader(input))
                    using (var reader = new StreamReader(archive.Open("telemetry.csv")))
                    {
                        Check(archive.Format == (zstd ? 2 : 1), "RAW fixture used the wrong negotiated archive format.");
                        Check(reader.ReadLine() == TelemetryCsv.Header, "RAW fixture changed the telemetry schema.");
                        foreach (var sample in samples)
                            Check(reader.ReadLine() == TelemetryCsv.Format(sample), "RAW publication discarded or changed a paused observation.");
                        Check(reader.ReadLine() == null, "RAW publication added observations.");
                    }
                    if (!zstd)
                    {
                        using var zip = ZipFile.OpenRead(path);
                        var telemetry = zip.GetEntry("telemetry.csv")!;
                        Check(telemetry.Length > 25L * telemetry.CompressedLength,
                            "Paused RAW fixture no longer exercises the receiver's former compression-ratio rejection.");
                        var fixture = Environment.GetEnvironmentVariable("MSFSLS_PAUSED_RAW_FIXTURE");
                        if (knownState && !string.IsNullOrWhiteSpace(fixture)) File.Copy(path, fixture, true);
                    }
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
