using System;
using System.Diagnostics;
using LandingStats.App.Runtime;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class IntegrationTelemetryRegressionTests
{
    public static void RunAll()
    {
        var projection = new IntegrationTelemetrySnapshot();
        Check((string?)projection.Read()["availability"] == "disconnected", "Missing source appears connected.");
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Simulator);
        long sequence = 0;
        void Observe(RuntimePayload payload, long qpc, TelemetrySample? sample = null) => projection.Observe(new RecordingRuntimeEffects
        { SourceObservation = new RuntimeEnvelope(worker, 1, ++sequence, qpc, payload), SourceSample = sample }, DateTime.UtcNow);
        var now = Stopwatch.GetTimestamp();
        Observe(new LifecycleMessage(SourceLifecycle.Connected), now);
        Observe(new AircraftMessage("MSFS", "Test aircraft", "A320", "Example"), now);
        Observe(new GuardFrameMessage(1, 12, true, 0, true), now);
        var guard = projection.Read();
        Check((string?)guard["availability"] == "guard" && guard["fullFrame"]!.Type == Newtonsoft.Json.Linq.JTokenType.Null,
            "Guard observation invented a full frame.");
        Check((string?)guard["aircraft"]?["title"] == "Test aircraft", "Current aircraft is missing.");
        var sample = new TelemetrySample { SimulationTimeSeconds = 2, LatitudeDegrees = double.NaN, VerticalSpeedFps = -2, GroundSpeedKnots = 120 };
        Observe(new TelemetryFrameMessage(sample), Stopwatch.GetTimestamp(), sample);
        var full = projection.Read();
        Check((string?)full["availability"] == "full" && (double?)full["fullFrame"]?["verticalSpeedFeetPerMinute"] == -120,
            "Full telemetry units changed.");
        Check(full["fullFrame"]?["latitudeDegrees"]?.Type == Newtonsoft.Json.Linq.JTokenType.Null, "Missing measurement became a number.");
        Observe(new LifecycleMessage(SourceLifecycle.Disconnected), Stopwatch.GetTimestamp());
        Check((bool?)projection.Read()["fullFrame"]?["stale"] == true, "Disconnected telemetry looks fresh.");
        Observe(new LifecycleMessage(SourceLifecycle.Connected), Stopwatch.GetTimestamp());
        Check(projection.Read()["aircraft"]!.Type == Newtonsoft.Json.Linq.JTokenType.Null && projection.Read()["fullFrame"]!.Type == Newtonsoft.Json.Linq.JTokenType.Null,
            "Reconnect reused prior aircraft/frame.");
        Observe(new GuardFrameMessage(3, 7, true, 0, false), Stopwatch.GetTimestamp() - 3 * Stopwatch.Frequency);
        Check((string?)projection.Read()["availability"] == "stale", "Old source capture time looks fresh after receipt.");
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
