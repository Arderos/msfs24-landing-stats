using System;
using System.Diagnostics;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
using Newtonsoft.Json.Linq;

namespace LandingStats.App.Runtime;

/// <summary>One immutable observation per source, with JSON produced only on
/// request. This does not request additional simulator streams or retain a track.</summary>
internal sealed class IntegrationTelemetrySnapshot
{
    private readonly object _gate = new object();
    private AircraftMessage? _aircraft;
    private TelemetrySample? _sample;
    private GuardFrameMessage? _guard;
    private DateTime _sampleUtc, _guardUtc;
    private long _sampleQpc, _guardQpc;
    private bool _connected;
    private int? _pauseFlags;

    public void Observe(RecordingRuntimeEffects effects, DateTime utc)
    {
        lock (_gate)
        {
            var message = effects.SourceObservation;
            if (message?.Payload is LifecycleMessage lifecycle)
            {
                if (lifecycle.State == SourceLifecycle.Connected)
                { _connected = true; _aircraft = null; _sample = null; _guard = null; _sampleQpc = 0; _guardQpc = 0; _pauseFlags = null; }
                else if (lifecycle.State == SourceLifecycle.Disconnected || lifecycle.State == SourceLifecycle.SimulatorQuit) _connected = false;
                else if (lifecycle.State == SourceLifecycle.PauseFlags) _pauseFlags = lifecycle.Value;
            }
            if (message?.Payload is AircraftMessage aircraft) _aircraft = aircraft;
            if (message?.Payload is GuardFrameMessage guard)
            { _guard = guard; _guardUtc = utc; _guardQpc = message.CaptureQpc; }
            if (effects.SourceSample != null && message != null && (message.CaptureQpc - _sampleQpc >= Stopwatch.Frequency / 4 || _sample == null))
            { _sample = effects.SourceSample; _sampleUtc = utc; _sampleQpc = message.CaptureQpc; }
            // Waiting describes recorder readiness, not native transport. Ingress
            // overflow can reset the recorder while retaining the same connection.
            if (effects.SimulatorConnected.HasValue) _connected = effects.SimulatorConnected.Value;
        }
    }

    public JObject Read()
    {
        AircraftMessage? aircraft; TelemetrySample? sample; GuardFrameMessage? guard;
        DateTime sampleUtc, guardUtc; long sampleQpc, guardQpc; bool connected; int? pause;
        lock (_gate)
        { aircraft = _aircraft; sample = _sample; guard = _guard; sampleUtc = _sampleUtc; guardUtc = _guardUtc;
            sampleQpc = _sampleQpc; guardQpc = _guardQpc; connected = _connected; pause = _pauseFlags; }
        var now = Stopwatch.GetTimestamp();
        var sampleAge = sampleQpc == 0 ? (double?)null : Math.Max(0, (now - sampleQpc) / (double)Stopwatch.Frequency);
        var guardAge = guardQpc == 0 ? (double?)null : Math.Max(0, (now - guardQpc) / (double)Stopwatch.Frequency);
        var useSample = sample != null && (guard == null || sampleAge <= guardAge);
        var age = useSample ? sampleAge : guardAge;
        var available = connected && age.HasValue && age.Value <= 2;
        var result = new JObject
        {
            ["availability"] = !connected ? "disconnected" : !age.HasValue ? "not_observed" : !available ? "stale" : useSample ? "full" : "guard",
            ["connected"] = connected, ["ageSeconds"] = age, ["stale"] = !available, ["pauseFlags"] = pause,
            ["observedUtc"] = age.HasValue ? (useSample ? sampleUtc : guardUtc).ToString("O") : null,
            ["aircraft"] = aircraft == null ? JValue.CreateNull() : new JObject
                { ["title"] = aircraft.Title, ["type"] = aircraft.Type, ["model"] = aircraft.Model, ["simulator"] = aircraft.Simulator },
            ["scope"] = "Latest observations from existing recorder subscriptions; no additional simulator subscription is created.",
            ["fullFrameAgeSeconds"] = sampleAge,
            ["onGround"] = useSample ? sample!.OnGround : guard == null ? (bool?)null : guard.OnGround,
            ["motionSimulation"] = useSample ? sample!.MotionSimulation : guard == null ? (bool?)null : guard.MotionSimulation,
            ["simulationTimeSeconds"] = Number(useSample ? sample!.SimulationTimeSeconds : guard?.SimulationTime ?? double.NaN),
            ["aboveGroundLevelFeet"] = Number(useSample ? sample!.AboveGroundLevelFeet : guard?.AglFeet ?? double.NaN),
            ["verticalVelocityFeetPerSecond"] = Number(useSample ? sample!.VelocityWorldYFps : guard?.VerticalVelocity ?? double.NaN),
        };
        // Full values retain their own timestamp/age when the guard has become
        // newer. Consumers must not mistake an old approach frame for live data.
        result["fullFrame"] = sample == null ? JValue.CreateNull() : new JObject
        {
            ["observedUtc"] = sampleUtc.ToString("O"), ["ageSeconds"] = sampleAge,
            ["stale"] = !connected || sampleAge > 2, ["sequence"] = sample.Sequence,
            ["simulatorState"] = sample.SimulatorState.ToString(),
            ["latitudeDegrees"] = Number(sample.LatitudeDegrees), ["longitudeDegrees"] = Number(sample.LongitudeDegrees),
            ["altitudeFeet"] = Number(sample.PlaneAltitudeFeet), ["groundAltitudeFeet"] = Number(sample.GroundAltitudeFeet),
            ["indicatedAirspeedKnots"] = Number(sample.IndicatedAirspeedKnots), ["groundSpeedKnots"] = Number(sample.GroundSpeedKnots),
            ["verticalSpeedFeetPerMinute"] = Number(sample.VerticalSpeedFps * 60), ["gForce"] = Number(sample.GForce),
            ["pitchDegrees"] = Number(sample.PitchDegrees), ["bankDegrees"] = Number(sample.BankDegrees),
            ["headingTrueDegrees"] = Number(sample.HeadingTrueDegrees), ["angleOfAttackDegrees"] = Number(sample.AngleOfAttackDegrees),
            ["sideslipDegrees"] = Number(sample.SideslipDegrees), ["windSpeedKnots"] = Number(sample.AmbientWindVelocityKnots),
            ["windDirectionDegrees"] = Number(sample.AmbientWindDirectionDegrees), ["weightPounds"] = Number(sample.TotalWeightPounds),
            ["cgPercent"] = Number(sample.CgPercent),
            ["controls"] = new JObject
            {
                ["elevator"] = Number(sample.ElevatorPosition), ["aileron"] = Number(sample.AileronPosition),
                ["rudder"] = Number(sample.RudderPosition), ["elevatorTrimRadians"] = Number(sample.ElevatorTrimRadians),
                ["flapsLeftPercent"] = Number(sample.FlapsLeftPercent), ["flapsRightPercent"] = Number(sample.FlapsRightPercent),
                ["brakeLeft"] = Number(sample.BrakeLeftPosition), ["brakeRight"] = Number(sample.BrakeRightPosition),
            },
        };
        return result;
    }
    private static JToken Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? JValue.CreateNull() : new JValue(value);
}
