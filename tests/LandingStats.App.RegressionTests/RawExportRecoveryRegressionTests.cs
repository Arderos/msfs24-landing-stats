using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LandingStats.App.Runtime;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class RawExportRecoveryRegressionTests
{
    public static void RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "rx-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(root);
        var done = false;
        try { for (var scenario = 0; scenario < 3; scenario++) Scenario(Path.Combine(root, scenario.ToString()), scenario); Quota(Path.Combine(root, "q")); done = true; }
        finally { if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
    private static void Scenario(string root, int scenario)
    {
        var raw = new RawEffectStore(Path.Combine(root, "raw")); var archives = Path.Combine(root, "archives"); var outboxRoot = Path.Combine(root, "outbox");
        var outbox = new DurableOutbox(outboxRoot, archives); var chunks = new RawChunkStore(Path.Combine(root, "chunks"), archives, outbox);
        var timestamp = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc); var session = Guid.NewGuid();
        var samples = Enumerable.Range(1, 8).Select(i => new TelemetrySample { Sequence = i, SimulationTimeSeconds = i * .1, MotionSimulation = true,
            OnGround = i % 2 == 0, AboveGroundLevelFeet = i, LatitudeDegrees = 42.1, LongitudeDegrees = 23.1, VelocityWorldYFps = -i }).ToArray();
        DurableRuntimeInput Input(int n) => new DurableRuntimeInput(new RuntimeInput(StableRuntimeIdentity.Command(session, n), JournalRecordKind.RuntimeCommand,
            n, n, timestamp, new RecordingCommand(RecordingCommandKind.Tick).Encode()), new JournalCursor(1, n));
        var start = new RawDebugCaptureStartedEventArgs(samples.Take(5).ToArray(), timestamp, "MSFS", "Original raw aircraft", "T", "M", new[] { "original stick" });
        raw.Commit(Input(1), new object[] { start });
        for (var i = 5; i < samples.Length; i++) raw.Commit(Input(i - 3), new object[] { new RawDebugSampleEventArgs(samples[i]) });
        raw.Commit(Input(5), new object[] { EventArgs.Empty });
        var capture = StableRuntimeIdentity.Command(Input(1).Input.Id, 0);
        var chunkIds = Enumerable.Range(1, 3).Select(n => StableRuntimeIdentity.Command(capture, n)).ToArray();
        var blocked = scenario == 0 ? Path.Combine(raw.Root, "export.json") : Path.Combine(outboxRoot, chunkIds[1].ToString("N") + ".json");
        if (scenario != 2) Directory.CreateDirectory(blocked);
        var failedPresentation = false;
        var owner = new RawExportOwner(raw, chunks, (_, __, ___) =>
        {
            if (scenario == 2 && !failedPresentation) { failedPresentation = true; throw new IOException("fixture presentation failure after checkpoint"); }
        }, chunkSize: 3);
        try { owner.Advance(); throw new InvalidOperationException("RAW fault fixture failed to interrupt its transaction."); }
        catch (IOException) { }
        var first = outbox.Read(chunkIds[0]); var firstHash = first.Hash;
        var rows = new Dictionary<Guid, string[]> { [first.Id] = ReadRows(first.Path, timestamp) };
        Check(outbox.Acknowledge(outbox.TryClaim(first.Id, new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Upload))!),
            "Manual RAW delivery unexpectedly required automatic collection consent.");
        Check(!File.Exists(first.Path), "Delivered RAW archive remained at its queue path.");
        if (scenario != 2) Directory.Delete(blocked);
        owner.Advance();
        Check(outbox.Enumerate().Count == 2 && outbox.Read(first.Id).State == OutboxState.Delivered && outbox.Read(first.Id).Hash == firstHash && !File.Exists(first.Path),
            "RAW retry duplicated a chunk, altered its bytes or recreated an acknowledged archive.");
        foreach (var id in chunkIds.Skip(1)) rows[id] = ReadRows(outbox.Read(id).Path, timestamp);
        var recoveredRows = chunkIds.SelectMany(id => rows[id]).ToArray();
        Check(recoveredRows.SequenceEqual(samples.Select(TelemetryCsv.Format)), "RAW recovery lost/reordered samples at a pre-roll chunk boundary.");
        Check(chunkIds.All(id => !outbox.Read(id).Automatic && outbox.Read(id).CapturedUtcTicks == timestamp.Ticks),
            "RAW chunks changed their capture policy or original wall clock.");
        Check(raw.Enumerate().Count() == 1, "RAW source batches accumulated after their registered chunks and export checkpoint.");
        raw.Commit(Input(1), new object[] { start });
        Check(raw.Enumerate().Count() == 1, "Replaying an older recording checkpoint recreated already consumed RAW effects.");
        new RawExportOwner(new RawEffectStore(raw.Root), chunks, (_, __, ___) => throw new InvalidOperationException("Replayed completed chunk"), 3).Advance();
        Check(!File.Exists(first.Path), "Cold RAW owner recreated an acknowledged archive.");
        Check(!Directory.GetFiles(Path.Combine(root, "chunks"), "archive.zip", SearchOption.AllDirectories).Any(), "Private prepared RAW ZIPs accumulated after durable publication.");
    }
    private static string[] ReadRows(string path, DateTime timestamp)
    {
        using var zip = ZipFile.OpenRead(path); using var session = new StreamReader(zip.GetEntry("session.txt")!.Open());
        var text = session.ReadToEnd();
        Check(text.Contains("capture_utc=" + timestamp.ToString("O")) && text.Contains("aircraft_title=Original raw aircraft") && text.Contains("control_input_source_0=original stick"),
            "RAW metadata came from the resumed flight or export time.");
        using var telemetry = new StreamReader(zip.GetEntry("telemetry.csv")!.Open());
        Check(telemetry.ReadLine() == TelemetryCsv.Header, "RAW schema changed unexpectedly."); var lines = new List<string>();
        string? line; while ((line = telemetry.ReadLine()) != null) lines.Add(line); return lines.ToArray();
    }
    private static void Quota(string root)
    {
        var input = new DurableRuntimeInput(new RuntimeInput(Guid.NewGuid(), JournalRecordKind.RuntimeCommand, 1, 1, DateTime.UtcNow,
            new RecordingCommand(RecordingCommandKind.Tick).Encode()), new JournalCursor(1, 1));
        var limited = new RawEffectStore(root, 1);
        try { limited.Commit(input, new object[] { EventArgs.Empty }); throw new InvalidOperationException("RAW quota was ignored."); }
        catch (IOException) { }
        Check(!limited.Enumerate().Any(), "Full RAW storage published a partial effect batch.");
        var restored = new RawEffectStore(root); restored.Commit(input, new object[] { EventArgs.Empty });
        Check(restored.Read(input.Input.Id).Effects.Count == 1, "RAW quota failure could not be retried from the accepted input.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
