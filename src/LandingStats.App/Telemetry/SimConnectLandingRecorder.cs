using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

internal enum RecorderState
{
    Waiting,
    Connected,
    Recording,
    Error,
}

internal sealed class RecorderStatusEventArgs : EventArgs
{
    public RecorderStatusEventArgs(RecorderState state, string message)
    {
        State = state;
        Message = message;
    }

    public RecorderState State { get; }

    public string Message { get; }
}

internal sealed class LandingEpisodeEventArgs : EventArgs
{
    public LandingEpisodeEventArgs(
        IReadOnlyList<TelemetrySample> samples,
        string simulator,
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<AirportFacility> airportFacilities,
        IReadOnlyList<string> controlInputSources)
    {
        Samples = samples;
        Simulator = simulator;
        AircraftTitle = aircraftTitle;
        AircraftType = aircraftType;
        AircraftModel = aircraftModel;
        AirportFacilities = airportFacilities;
        ControlInputSources = controlInputSources;
    }

    public IReadOnlyList<TelemetrySample> Samples { get; }

    public string Simulator { get; }

    public string AircraftTitle { get; }

    public string AircraftType { get; }

    public string AircraftModel { get; }

    public IReadOnlyList<AirportFacility> AirportFacilities { get; }

    public IReadOnlyList<string> ControlInputSources { get; }
}

internal sealed class AirportFacilitiesEventArgs : EventArgs
{
    public AirportFacilitiesEventArgs(IReadOnlyList<AirportFacility> facilities)
    {
        Facilities = facilities;
    }

    public IReadOnlyList<AirportFacility> Facilities { get; }
}

internal sealed class RawDebugCaptureEventArgs : EventArgs
{
    public RawDebugCaptureEventArgs(
        IReadOnlyList<TelemetrySample> samples,
        DateTime startedUtc,
        string simulator,
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<string> controlInputSources)
    {
        Samples = samples;
        StartedUtc = startedUtc;
        Simulator = simulator;
        AircraftTitle = aircraftTitle;
        AircraftType = aircraftType;
        AircraftModel = aircraftModel;
        ControlInputSources = controlInputSources;
    }

    public IReadOnlyList<TelemetrySample> Samples { get; }

    public DateTime StartedUtc { get; }

    public string Simulator { get; }

    public string AircraftTitle { get; }

    public string AircraftType { get; }

    public string AircraftModel { get; }

    public IReadOnlyList<string> ControlInputSources { get; }
}

internal sealed class SimConnectLandingRecorder : IDisposable
{
    public const int WindowMessage = 0x0403;

    private const double CaptureStartAglFeet = 500.0;
    private const double CaptureStartDescentFps = -0.5;
    private const double PreRollSeconds = 15.0;
    private const double PostContactSeconds = 15.0;
    private const double GoAroundResetAglFeet = 1000.0;
    private const double MaximumApproachSeconds = 300.0;
    private const int RawDebugChunkMaximumSamples = 30000;
    private static readonly TimeSpan JoystickRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IntPtr _windowHandle;
    private readonly Stopwatch _hostClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<TelemetrySample> _preRoll = new Queue<TelemetrySample>();
    private readonly Dictionary<string, AirportFacility> _airportFacilities =
        new Dictionary<string, AirportFacility>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _simConnectOperations = new Dictionary<uint, string>();
    private readonly WindowsJoystickReader _windowsJoystickReader = new WindowsJoystickReader();

    private SimConnect? _simConnect;
    private List<TelemetrySample>? _episodeSamples;
    private List<TelemetrySample>? _rawDebugSamples;
    private DateTime _rawDebugStartedUtc;
    private DateTime _lastConnectAttemptUtc = DateTime.MinValue;
    private DateTime _nextJoystickRefreshUtc = DateTime.MinValue;
    private long _sequence;
    private double _lastSimulationTime = double.NaN;
    private double _episodeStartTime;
    private double? _lastContactTime;
    private bool _hasBeenAirborne;
    private bool _previousOnGround = true;
    private bool _armed = true;
    private string _simulator = "MSFS";
    private string _aircraftTitle = "Unknown aircraft";
    private string _aircraftType = "unknown";
    private string _aircraftModel = "unknown";
    private double _axisElevatorSetPercent;
    private double _axisElevatorSetReceivedHostSeconds = double.NaN;
    private bool _recoverStatusAfterFrame;
    private bool _disposed;

    public SimConnectLandingRecorder(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _retryTimer.Tick += OnRetryTimerTick;
    }

    public event EventHandler<RecorderStatusEventArgs>? StatusChanged;

    public event EventHandler<LandingEpisodeEventArgs>? EpisodeCompleted;

    public event EventHandler<AirportFacilitiesEventArgs>? AirportFacilitiesUpdated;

    public event EventHandler<RawDebugCaptureEventArgs>? RawDebugCaptureCompleted;

    public bool RawDebugEnabled => _rawDebugSamples != null;

    public void Start()
    {
        ThrowIfDisposed();
        _retryTimer.Start();
        TryConnect();
    }

    public void SetRawDebugEnabled(bool enabled)
    {
        ThrowIfDisposed();
        if (enabled == RawDebugEnabled)
        {
            return;
        }

        if (enabled)
        {
            if (_windowsJoystickReader.Refresh())
            {
                _preRoll.Clear();
            }

            _nextJoystickRefreshUtc = DateTime.UtcNow + JoystickRefreshInterval;
            _rawDebugStartedUtc = DateTime.UtcNow;
            _rawDebugSamples = new List<TelemetrySample>(RawDebugChunkMaximumSamples);
            foreach (var sample in _preRoll)
            {
                _rawDebugSamples.Add(sample);
            }

            return;
        }

        CompleteRawDebugCapture();
    }

    public IntPtr HandleWindowMessage(int message, ref bool handled)
    {
        if (message != WindowMessage || _simConnect == null)
        {
            return IntPtr.Zero;
        }

        handled = true;
        try
        {
            _simConnect.ReceiveMessage();
        }
        catch (Exception exception)
        {
            SetStatus(RecorderState.Error, $"SimConnect receive failed: {exception.Message}");
            Disconnect();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _retryTimer.Stop();
        if (_lastContactTime.HasValue)
        {
            CompleteEpisode(false);
        }

        CompleteRawDebugCapture();

        _simConnect?.Dispose();
        _simConnect = null;
        _hostClock.Stop();
    }

    private void OnRetryTimerTick(object? sender, EventArgs eventArgs)
    {
        var now = DateTime.UtcNow;
        if (_episodeSamples == null && _rawDebugSamples == null && now >= _nextJoystickRefreshUtc)
        {
            if (_windowsJoystickReader.Refresh())
            {
                // A controller slot must never refer to two devices inside the same retained pre-roll.
                _preRoll.Clear();
            }

            _nextJoystickRefreshUtc = now + JoystickRefreshInterval;
        }

        if (_simConnect == null && now - _lastConnectAttemptUtc >= TimeSpan.FromSeconds(2))
        {
            TryConnect();
        }
    }

    private void TryConnect()
    {
        if (_simConnect != null || _disposed)
        {
            return;
        }

        _lastConnectAttemptUtc = DateTime.UtcNow;
        SetStatus(RecorderState.Waiting, "Waiting for MSFS — retrying");
        try
        {
            var connection = new SimConnect(
                "MSFS Landing Stats",
                _windowHandle,
                WindowMessage,
                null,
                0);
            connection.OnRecvOpen += OnRecvOpen;
            connection.OnRecvQuit += OnRecvQuit;
            connection.OnRecvException += OnRecvException;
            connection.OnRecvSimobjectData += OnRecvSimobjectData;
            connection.OnRecvAirportList += OnRecvAirportList;
            connection.OnRecvEvent += OnRecvEvent;
            connection.OnRecvEventEx1 += OnRecvEventEx1;
            _simConnect = connection;
        }
        catch (COMException)
        {
            SetStatus(RecorderState.Waiting, "Waiting for MSFS — retrying");
        }
        catch (Exception exception)
        {
            SetStatus(RecorderState.Error, $"Connection failed: {exception.Message}");
        }
    }

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        try
        {
            RegisterFrameDefinition(sender);
            sender.RegisterDataDefineStruct<SimFrameData>(Definitions.Frame);
            sender.AddToDataDefinition(Definitions.Metadata, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0, SimConnect.SIMCONNECT_UNUSED);
            AddMetadataString(sender, "ATC TYPE");
            AddMetadataString(sender, "ATC MODEL");
            AddMetadataString(sender, "CATEGORY");
            sender.RegisterDataDefineStruct<AircraftMetadata>(Definitions.Metadata);
            sender.RequestDataOnSimObject(
                Requests.Frame,
                Definitions.Frame,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SIM_FRAME,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0,
                0,
                0);
            RequestAircraftMetadata(sender);
            RequestAirportFacilities(sender);
            RegisterControlEvents(sender);

            _simulator = string.IsNullOrWhiteSpace(data.szApplicationName) ? "MSFS" : data.szApplicationName.Trim();
            _recoverStatusAfterFrame = false;
            SetStatus(RecorderState.Connected, "Connected — waiting for 500 ft AGL");
        }
        catch (Exception exception)
        {
            SetStatus(RecorderState.Error, $"Telemetry setup failed: {exception.Message}");
            Disconnect();
        }
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        Disconnect();
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        var operation = _simConnectOperations.TryGetValue(data.dwSendID, out var value)
            ? $" · {value}"
            : string.Empty;
        _recoverStatusAfterFrame = true;
        SetStatus(
            RecorderState.Error,
            $"SimConnect error {data.dwException} · send {data.dwSendID} · index {data.dwIndex}{operation}");
    }

    private void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        ProcessControlEvent(data.uEventID, data.dwData);
    }

    private void OnRecvEventEx1(SimConnect sender, SIMCONNECT_RECV_EVENT_EX1 data)
    {
        ProcessControlEvent(data.uEventID, data.dwData0);
    }

    private void ProcessControlEvent(uint rawEventId, uint rawValue)
    {
        var eventId = (ControlEvents)rawEventId;
        if (eventId != ControlEvents.AxisElevatorSet)
        {
            return;
        }

        _axisElevatorSetPercent = NormalizeElevatorEvent(rawValue);
        _axisElevatorSetReceivedHostSeconds = _hostClock.Elapsed.TotalSeconds;
    }

    private void OnRecvAirportList(SimConnect sender, SIMCONNECT_RECV_AIRPORT_LIST data)
    {
        if ((Requests)data.dwRequestID != Requests.Airports)
        {
            return;
        }

        if (data.dwEntryNumber == 0)
        {
            _airportFacilities.Clear();
        }

        foreach (var value in data.rgData)
        {
            if (value is not SIMCONNECT_DATA_FACILITY_AIRPORT airport || string.IsNullOrWhiteSpace(airport.Ident))
            {
                continue;
            }

            var facility = new AirportFacility
            {
                Ident = airport.Ident.Trim(),
                Region = airport.Region?.Trim() ?? string.Empty,
                LatitudeDegrees = airport.Latitude,
                LongitudeDegrees = airport.Longitude,
                AltitudeMeters = airport.Altitude,
            };
            _airportFacilities[facility.Key] = facility;
        }

        if (data.dwOutOf == 0 || data.dwEntryNumber + 1 >= data.dwOutOf)
        {
            AirportFacilitiesUpdated?.Invoke(
                this,
                new AirportFacilitiesEventArgs(new List<AirportFacility>(_airportFacilities.Values)));
        }
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwData.Length == 0)
        {
            return;
        }

        if ((Requests)data.dwRequestID == Requests.Metadata)
        {
            var metadata = (AircraftMetadata)data.dwData[0];
            _aircraftTitle = ValueOrUnknown(metadata.Title, "Unknown aircraft");
            _aircraftType = ValueOrUnknown(metadata.AtcType, "unknown");
            _aircraftModel = ValueOrUnknown(metadata.AtcModel, "unknown");
            return;
        }

        if ((Requests)data.dwRequestID == Requests.Frame)
        {
            RecoverStatusAfterFrame();
            ProcessSample(ToSample((SimFrameData)data.dwData[0]));
        }
    }

    private void ProcessSample(TelemetrySample sample)
    {
        var sampleTime = TimeOf(sample);
        if (!double.IsNaN(_lastSimulationTime) && sample.SimulationTimeSeconds < _lastSimulationTime - 0.001)
        {
            if (_lastContactTime.HasValue)
            {
                CompleteEpisode();
            }
            else
            {
                DiscardEpisode();
            }

            ResetFlightDetection();
        }

        _lastSimulationTime = sample.SimulationTimeSeconds;
        AddToPreRoll(sample);
        AddRawDebugSample(sample);

        if (!sample.OnGround)
        {
            _hasBeenAirborne = true;
            if (sample.AboveGroundLevelFeet >= GoAroundResetAglFeet)
            {
                _armed = true;
            }
        }

        var isContact = _hasBeenAirborne && sample.OnGround && !_previousOnGround;
        var startedNow = false;
        if (_episodeSamples == null && _armed && (ShouldStartEpisode(sample) || isContact))
        {
            StartEpisode(sample, isContact);
            startedNow = true;
        }

        if (_episodeSamples != null && !startedNow)
        {
            _episodeSamples.Add(sample);
        }

        if (isContact)
        {
            _lastContactTime = sampleTime;
            SetStatus(RecorderState.Recording, "Contact detected — recording rollout");
        }

        _previousOnGround = sample.OnGround;

        if (_episodeSamples != null && _lastContactTime.HasValue && sampleTime - _lastContactTime.Value >= PostContactSeconds)
        {
            CompleteEpisode();
        }
        else if (_episodeSamples != null && !_lastContactTime.HasValue &&
                 sample.AboveGroundLevelFeet >= GoAroundResetAglFeet && sample.VelocityWorldYFps > 0)
        {
            DiscardEpisode();
            _armed = true;
            SetStatus(RecorderState.Connected, "Go-around — waiting for 500 ft AGL");
        }
        else if (_episodeSamples != null && !_lastContactTime.HasValue && sampleTime - _episodeStartTime >= MaximumApproachSeconds)
        {
            DiscardEpisode();
            _armed = true;
            SetStatus(RecorderState.Connected, "Approach window restarted — still armed");
        }
    }

    private void StartEpisode(TelemetrySample trigger, bool contactTriggered)
    {
        _episodeSamples = new List<TelemetrySample>(_preRoll.Count + 4096);
        foreach (var sample in _preRoll)
        {
            _episodeSamples.Add(sample);
        }

        _episodeStartTime = TimeOf(trigger);
        _lastContactTime = null;
        _armed = false;
        if (_simConnect != null)
        {
            RequestAircraftMetadata(_simConnect);
        }

        SetStatus(
            RecorderState.Recording,
            contactTriggered ? "Late start — contact capture in progress" : "Below 500 ft — landing capture in progress");
    }

    private void CompleteEpisode(bool refreshAirportFacilities = true)
    {
        var samples = _episodeSamples;
        _episodeSamples = null;
        _lastContactTime = null;
        _armed = true;
        _preRoll.Clear();
        if (samples == null || samples.Count == 0)
        {
            return;
        }

        EpisodeCompleted?.Invoke(
            this,
            new LandingEpisodeEventArgs(
                samples.ToArray(),
                _simulator,
                _aircraftTitle,
                _aircraftType,
                _aircraftModel,
                new List<AirportFacility>(_airportFacilities.Values),
                ControlInputSources()));

        if (refreshAirportFacilities && _simConnect != null && !_disposed)
        {
            RequestAirportFacilities(_simConnect);
        }
    }

    private void DiscardEpisode()
    {
        _episodeSamples = null;
        _lastContactTime = null;
    }

    private void CompleteRawDebugCapture()
    {
        var samples = _rawDebugSamples;
        _rawDebugSamples = null;
        RaiseRawDebugCapture(samples, _rawDebugStartedUtc);
    }

    private void AddRawDebugSample(TelemetrySample sample)
    {
        if (_rawDebugSamples == null)
        {
            return;
        }

        _rawDebugSamples.Add(sample);
        if (_rawDebugSamples.Count < RawDebugChunkMaximumSamples)
        {
            return;
        }

        var completed = _rawDebugSamples;
        var startedUtc = _rawDebugStartedUtc;
        _rawDebugStartedUtc = DateTime.UtcNow;
        _rawDebugSamples = new List<TelemetrySample>(RawDebugChunkMaximumSamples);
        RaiseRawDebugCapture(completed, startedUtc);
    }

    private void RaiseRawDebugCapture(IReadOnlyList<TelemetrySample>? samples, DateTime startedUtc)
    {
        if (samples == null || samples.Count == 0)
        {
            return;
        }

        RawDebugCaptureCompleted?.Invoke(
            this,
            new RawDebugCaptureEventArgs(
                samples,
                startedUtc,
                _simulator,
                _aircraftTitle,
                _aircraftType,
                _aircraftModel,
                ControlInputSources()));
    }

    private void AddToPreRoll(TelemetrySample sample)
    {
        _preRoll.Enqueue(sample);
        var sampleTime = TimeOf(sample);
        while (_preRoll.Count > 1 && sampleTime - TimeOf(_preRoll.Peek()) > PreRollSeconds)
        {
            _preRoll.Dequeue();
        }
    }

    private static bool ShouldStartEpisode(TelemetrySample sample)
    {
        return sample.MotionSimulation &&
               !sample.OnGround &&
               sample.AboveGroundLevelFeet >= -50 &&
               sample.AboveGroundLevelFeet <= CaptureStartAglFeet &&
               sample.VelocityWorldYFps <= CaptureStartDescentFps;
    }

    private void Disconnect()
    {
        if (_lastContactTime.HasValue)
        {
            CompleteEpisode(false);
        }
        else
        {
            DiscardEpisode();
        }

        _simConnect?.Dispose();
        _simConnect = null;
        _recoverStatusAfterFrame = false;
        ResetFlightDetection();
        if (!_disposed)
        {
            SetStatus(RecorderState.Waiting, "MSFS disconnected — retrying");
        }
    }

    private void ResetFlightDetection()
    {
        _preRoll.Clear();
        _hasBeenAirborne = false;
        _previousOnGround = true;
        _armed = true;
        _lastSimulationTime = double.NaN;
        _axisElevatorSetPercent = 0;
        _axisElevatorSetReceivedHostSeconds = double.NaN;
    }

    private TelemetrySample ToSample(SimFrameData frame)
    {
        var sample = new TelemetrySample
        {
            Sequence = _sequence++,
            HostElapsedSeconds = _hostClock.Elapsed.TotalSeconds,
            SimulationTimeSeconds = frame.SimulationTimeSeconds,
            SimulationDeltaSeconds = frame.SimulationDeltaSeconds,
            OnGround = frame.OnGround != 0,
            MotionSimulation = frame.MotionSimulation != 0,
            TouchdownNormalVelocityFps = frame.TouchdownNormalVelocityFps,
            VerticalSpeedFps = frame.VerticalSpeedFps,
            VelocityWorldYFps = frame.VelocityWorldYFps,
            VelocityBodyYFps = frame.VelocityBodyYFps,
            GForce = frame.GForce,
            MaxGForce = frame.MaxGForce,
            SemibodyLoadFactorY = frame.SemibodyLoadFactorY,
            AccelerationBodyYFps2 = frame.AccelerationBodyYFps2,
            AboveGroundLevelFeet = frame.AboveGroundLevelFeet,
            PitchDegrees = frame.PitchDegrees,
            BankDegrees = frame.BankDegrees,
            LatitudeDegrees = frame.LatitudeDegrees,
            LongitudeDegrees = frame.LongitudeDegrees,
            IndicatedAirspeedKnots = frame.IndicatedAirspeedKnots,
            GroundSpeedKnots = frame.GroundSpeedKnots,
            SimulationRate = frame.SimulationRate,
            PlaneAltitudeFeet = frame.PlaneAltitudeFeet,
            GroundAltitudeFeet = frame.GroundAltitudeFeet,
            AboveGroundMinusCgFeet = frame.AboveGroundMinusCgFeet,
            AccelerationWorldYFps2 = frame.AccelerationWorldYFps2,
            RotationVelocityBodyXRadiansPerSecond = frame.RotationVelocityBodyXRadiansPerSecond,
            RotationVelocityBodyYRadiansPerSecond = frame.RotationVelocityBodyYRadiansPerSecond,
            RotationVelocityBodyZRadiansPerSecond = frame.RotationVelocityBodyZRadiansPerSecond,
            TouchdownPitchDegrees = frame.TouchdownPitchDegrees,
            TouchdownBankDegrees = frame.TouchdownBankDegrees,
            VelocityWorldXFps = frame.VelocityWorldXFps,
            VelocityWorldZFps = frame.VelocityWorldZFps,
            VelocityBodyXFps = frame.VelocityBodyXFps,
            VelocityBodyZFps = frame.VelocityBodyZFps,
            AccelerationWorldXFps2 = frame.AccelerationWorldXFps2,
            AccelerationWorldZFps2 = frame.AccelerationWorldZFps2,
            AccelerationBodyXFps2 = frame.AccelerationBodyXFps2,
            AccelerationBodyZFps2 = frame.AccelerationBodyZFps2,
            RotationAccelerationBodyXRadiansPerSecond2 = frame.RotationAccelerationBodyXRadiansPerSecond2,
            RotationAccelerationBodyYRadiansPerSecond2 = frame.RotationAccelerationBodyYRadiansPerSecond2,
            RotationAccelerationBodyZRadiansPerSecond2 = frame.RotationAccelerationBodyZRadiansPerSecond2,
            SemibodyLoadFactorX = frame.SemibodyLoadFactorX,
            SemibodyLoadFactorZ = frame.SemibodyLoadFactorZ,
            SemibodyLoadFactorYDot = frame.SemibodyLoadFactorYDot,
            HeadingTrueDegrees = frame.HeadingTrueDegrees,
            TrueAirspeedKnots = frame.TrueAirspeedKnots,
            Mach = frame.Mach,
            AngleOfAttackDegrees = frame.AngleOfAttackDegrees,
            SideslipDegrees = frame.SideslipDegrees,
            AmbientWindVelocityKnots = frame.AmbientWindVelocityKnots,
            AmbientWindDirectionDegrees = frame.AmbientWindDirectionDegrees,
            ElevatorPosition = frame.ElevatorPosition,
            ElevatorTrimRadians = frame.ElevatorTrimRadians,
            AileronPosition = frame.AileronPosition,
            RudderPosition = frame.RudderPosition,
            ElevatorDeflectionPercentOver100 = frame.ElevatorDeflectionPercentOver100,
            AileronLeftDeflectionPercentOver100 = frame.AileronLeftDeflectionPercentOver100,
            AileronRightDeflectionPercentOver100 = frame.AileronRightDeflectionPercentOver100,
            RudderDeflectionPercentOver100 = frame.RudderDeflectionPercentOver100,
            SpoilersLeftPosition = frame.SpoilersLeftPosition,
            SpoilersRightPosition = frame.SpoilersRightPosition,
            FlapsHandlePercent = frame.FlapsHandlePercent,
            FlapsLeftPercent = frame.FlapsLeftPercent,
            FlapsRightPercent = frame.FlapsRightPercent,
            BrakeLeftPosition = frame.BrakeLeftPosition,
            BrakeRightPosition = frame.BrakeRightPosition,
            GearHandlePosition = frame.GearHandlePosition,
            GearTotalPercentExtended = frame.GearTotalPercentExtended,
            GearCenterPosition = frame.GearCenterPosition,
            GearLeftPosition = frame.GearLeftPosition,
            GearRightPosition = frame.GearRightPosition,
            TotalWeightPounds = frame.TotalWeightPounds,
            CgPercent = frame.CgPercent,
            OnAnyRunway = frame.OnAnyRunway != 0,
            SurfaceType = frame.SurfaceType,
            SurfaceCondition = frame.SurfaceCondition,
            SpoilersArmed = frame.SpoilersArmed != 0,
            NumberOfEngines = frame.NumberOfEngines,
            PilotRollInputPercent = frame.PilotRollInputPercent,
            PilotPitchInputPercent = frame.PilotPitchInputPercent,
            RudderPedalInputPercent = frame.RudderPedalInputPercent,
        };
        var hostSeconds = sample.HostElapsedSeconds;
        sample.AxisElevatorSetValid = !double.IsNaN(_axisElevatorSetReceivedHostSeconds);
        sample.AxisElevatorSetPercent = _axisElevatorSetPercent;
        sample.AxisElevatorSetAgeSeconds = sample.AxisElevatorSetValid
            ? Math.Max(0, hostSeconds - _axisElevatorSetReceivedHostSeconds)
            : 0;

        for (var index = 0; index < TelemetrySample.CapturedControllerCount; index++)
        {
            sample.RawControllerYAxisValid[index] = _windowsJoystickReader.TryReadYAxis(
                index,
                out var joystickId,
                out var rawYPercent);
            sample.RawControllerDeviceId[index] = joystickId;
            sample.RawControllerYAxisPercent[index] = rawYPercent;
            sample.RawControllerYAxisAgeSeconds[index] = 0;
        }

        CopyArray(frame.EngineThrottlePercent, sample.EngineThrottlePercent);
        CopyArray(frame.EngineN1Percent, sample.EngineN1Percent);
        CopyArray(frame.EngineRpm, sample.EngineRpm);
        CopyArray(frame.EngineReversePercent, sample.EngineReversePercent);
        CopyArray(frame.ContactPointCompression, sample.ContactPointCompression);
        CopyArray(frame.ContactPointPosition, sample.ContactPointPosition);
        if (frame.ContactPointOnGround != null)
        {
            var count = Math.Min(frame.ContactPointOnGround.Length, sample.ContactPointOnGround.Length);
            for (var index = 0; index < count; index++)
            {
                sample.ContactPointOnGround[index] = frame.ContactPointOnGround[index] != 0;
            }
        }

        return sample;
    }

    private static void CopyArray(double[]? source, double[] destination)
    {
        if (source != null)
        {
            Array.Copy(source, destination, Math.Min(source.Length, destination.Length));
        }
    }

    private static double TimeOf(TelemetrySample sample)
    {
        return Math.Abs(sample.SimulationTimeSeconds) > 0.000000001
            ? sample.SimulationTimeSeconds
            : sample.HostElapsedSeconds;
    }

    private static string ValueOrUnknown(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
    }

    private IReadOnlyList<string> ControlInputSources()
    {
        var sources = new List<string>();
        for (var index = 0; index < _windowsJoystickReader.Count; index++)
        {
            // WinMM and SimConnect use unrelated device ordering. Never pair their entries by array index.
            sources.Add(_windowsJoystickReader.DescribeSource(index));
        }

        return sources;
    }

    private void SetStatus(RecorderState state, string message)
    {
        StatusChanged?.Invoke(this, new RecorderStatusEventArgs(state, message));
    }

    private void RecoverStatusAfterFrame()
    {
        if (!_recoverStatusAfterFrame)
        {
            return;
        }

        _recoverStatusAfterFrame = false;
        if (_episodeSamples != null)
        {
            SetStatus(
                RecorderState.Recording,
                _lastContactTime.HasValue
                    ? "Contact detected — recording rollout"
                    : "Below 500 ft — landing capture in progress");
        }
        else
        {
            SetStatus(RecorderState.Connected, "Connected — waiting for 500 ft AGL");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SimConnectLandingRecorder));
        }
    }

    private static void RegisterFrameDefinition(SimConnect connection)
    {
        AddDouble(connection, "SIMULATION TIME", "seconds");
        AddDouble(connection, "SIMULATION DELTA TIME", "seconds");
        AddInt(connection, "SIM ON GROUND", "Bool");
        AddInt(connection, "MOTION SIMULATION", "Bool");
        AddDouble(connection, "PLANE TOUCHDOWN NORMAL VELOCITY", "Feet per second");
        AddDouble(connection, "VERTICAL SPEED", "Feet per second");
        AddDouble(connection, "VELOCITY WORLD Y", "Feet per second");
        AddDouble(connection, "VELOCITY BODY Y", "Feet per second");
        AddDouble(connection, "G FORCE", "GForce");
        AddDouble(connection, "MAX G FORCE", "GForce");
        AddDouble(connection, "SEMIBODY LOADFACTOR Y", "Number");
        AddDouble(connection, "ACCELERATION BODY Y", "Feet per second squared");
        AddDouble(connection, "PLANE ALT ABOVE GROUND", "Feet");
        AddDouble(connection, "PLANE PITCH DEGREES", "Degrees");
        AddDouble(connection, "PLANE BANK DEGREES", "Degrees");
        AddDouble(connection, "PLANE LATITUDE", "Degrees");
        AddDouble(connection, "PLANE LONGITUDE", "Degrees");
        AddDouble(connection, "AIRSPEED INDICATED", "Knots");
        AddDouble(connection, "GROUND VELOCITY", "Knots");
        AddDouble(connection, "SIMULATION RATE", "Number");
        AddDouble(connection, "PLANE ALTITUDE", "Feet");
        AddDouble(connection, "GROUND ALTITUDE", "Feet");
        AddDouble(connection, "PLANE ALT ABOVE GROUND MINUS CG", "Feet");
        AddDouble(connection, "ACCELERATION WORLD Y", "Feet per second squared");
        AddDouble(connection, "ROTATION VELOCITY BODY X", "Radians per second");
        AddDouble(connection, "ROTATION VELOCITY BODY Y", "Radians per second");
        AddDouble(connection, "ROTATION VELOCITY BODY Z", "Radians per second");
        AddDouble(connection, "PLANE TOUCHDOWN PITCH DEGREES", "Degrees");
        AddDouble(connection, "PLANE TOUCHDOWN BANK DEGREES", "Degrees");
        AddDouble(connection, "VELOCITY WORLD X", "Feet per second");
        AddDouble(connection, "VELOCITY WORLD Z", "Feet per second");
        AddDouble(connection, "VELOCITY BODY X", "Feet per second");
        AddDouble(connection, "VELOCITY BODY Z", "Feet per second");
        AddDouble(connection, "ACCELERATION WORLD X", "Feet per second squared");
        AddDouble(connection, "ACCELERATION WORLD Z", "Feet per second squared");
        AddDouble(connection, "ACCELERATION BODY X", "Feet per second squared");
        AddDouble(connection, "ACCELERATION BODY Z", "Feet per second squared");
        AddDouble(connection, "ROTATION ACCELERATION BODY X", "Radians per second squared");
        AddDouble(connection, "ROTATION ACCELERATION BODY Y", "Radians per second squared");
        AddDouble(connection, "ROTATION ACCELERATION BODY Z", "Radians per second squared");
        AddDouble(connection, "SEMIBODY LOADFACTOR X", "Number");
        AddDouble(connection, "SEMIBODY LOADFACTOR Z", "Number");
        AddDouble(connection, "SEMIBODY LOADFACTOR YDOT", "Number");
        AddDouble(connection, "PLANE HEADING DEGREES TRUE", "Degrees");
        AddDouble(connection, "AIRSPEED TRUE", "Knots");
        AddDouble(connection, "AIRSPEED MACH", "Mach");
        AddDouble(connection, "INCIDENCE ALPHA", "Degrees");
        AddDouble(connection, "INCIDENCE BETA", "Degrees");
        AddDouble(connection, "AMBIENT WIND VELOCITY", "Knots");
        AddDouble(connection, "AMBIENT WIND DIRECTION", "Degrees");
        AddDouble(connection, "ELEVATOR POSITION", "Position");
        AddDouble(connection, "ELEVATOR TRIM POSITION", "Radians");
        AddDouble(connection, "AILERON POSITION", "Position");
        AddDouble(connection, "RUDDER POSITION", "Position");
        AddDouble(connection, "ELEVATOR DEFLECTION PCT", "Percent Over 100");
        AddDouble(connection, "AILERON LEFT DEFLECTION PCT", "Percent Over 100");
        AddDouble(connection, "AILERON RIGHT DEFLECTION PCT", "Percent Over 100");
        AddDouble(connection, "RUDDER DEFLECTION PCT", "Percent Over 100");
        AddDouble(connection, "SPOILERS LEFT POSITION", "Percent Over 100");
        AddDouble(connection, "SPOILERS RIGHT POSITION", "Percent Over 100");
        AddDouble(connection, "FLAPS HANDLE PERCENT", "Percent Over 100");
        AddDouble(connection, "TRAILING EDGE FLAPS LEFT PERCENT", "Percent Over 100");
        AddDouble(connection, "TRAILING EDGE FLAPS RIGHT PERCENT", "Percent Over 100");
        AddDouble(connection, "BRAKE LEFT POSITION", "Position");
        AddDouble(connection, "BRAKE RIGHT POSITION", "Position");
        AddDouble(connection, "GEAR HANDLE POSITION", "Percent Over 100");
        AddDouble(connection, "GEAR TOTAL PCT EXTENDED", "Percent");
        AddDouble(connection, "GEAR CENTER POSITION", "Percent Over 100");
        AddDouble(connection, "GEAR LEFT POSITION", "Percent Over 100");
        AddDouble(connection, "GEAR RIGHT POSITION", "Percent Over 100");
        AddDouble(connection, "TOTAL WEIGHT", "Pounds");
        AddDouble(connection, "CG PERCENT", "Percent");
        AddInt(connection, "ON ANY RUNWAY", "Bool");
        AddInt(connection, "SURFACE TYPE", "Enum");
        AddInt(connection, "SURFACE CONDITION", "Enum");
        AddInt(connection, "SPOILERS ARMED", "Bool");
        AddInt(connection, "NUMBER OF ENGINES", "Number");
        AddDouble(connection, "YOKE X POSITION", "Percent");
        AddDouble(connection, "YOKE Y POSITION", "Percent");
        AddDouble(connection, "RUDDER PEDAL POSITION", "Percent");

        for (var index = 1; index <= TelemetrySample.CapturedEngineCount; index++)
        {
            AddDouble(connection, $"GENERAL ENG THROTTLE LEVER POSITION:{index}", "Percent");
        }

        for (var index = 1; index <= TelemetrySample.CapturedEngineCount; index++)
        {
            AddDouble(connection, $"TURB ENG N1:{index}", "Percent");
        }

        for (var index = 1; index <= TelemetrySample.CapturedEngineCount; index++)
        {
            AddDouble(connection, $"GENERAL ENG RPM:{index}", "RPM");
        }

        for (var index = 1; index <= TelemetrySample.CapturedEngineCount; index++)
        {
            AddDouble(connection, $"TURB ENG REVERSE NOZZLE PERCENT:{index}", "Percent");
        }

        for (var index = 0; index < TelemetrySample.CapturedContactPointCount; index++)
        {
            AddDouble(connection, $"CONTACT POINT COMPRESSION:{index}", "Percent");
        }

        for (var index = 0; index < TelemetrySample.CapturedContactPointCount; index++)
        {
            AddDouble(connection, $"CONTACT POINT POSITION:{index}", "Percent");
        }

        for (var index = 0; index < TelemetrySample.CapturedContactPointCount; index++)
        {
            AddInt(connection, $"CONTACT POINT IS ON GROUND:{index}", "Bool");
        }
    }

    private static void AddDouble(SimConnect connection, string name, string unit)
    {
        connection.AddToDataDefinition(Definitions.Frame, name, unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
    }

    private static void AddInt(SimConnect connection, string name, string unit)
    {
        connection.AddToDataDefinition(Definitions.Frame, name, unit, SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
    }

    private static void AddMetadataString(SimConnect connection, string name)
    {
        connection.AddToDataDefinition(Definitions.Metadata, name, null, SIMCONNECT_DATATYPE.STRING256, 0, SimConnect.SIMCONNECT_UNUSED);
    }

    private static void RequestAircraftMetadata(SimConnect connection)
    {
        connection.RequestDataOnSimObject(
            Requests.Metadata,
            Definitions.Metadata,
            SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
            0,
            0,
            0);
    }

    private static void RequestAirportFacilities(SimConnect connection)
    {
        connection.RequestFacilitiesList_EX1(
            SIMCONNECT_FACILITY_LIST_TYPE.AIRPORT,
            Requests.Airports);
    }

    private void RegisterControlEvents(SimConnect connection)
    {
        TrackOperation(
            connection,
            "map AXIS_ELEVATOR_SET",
            () => connection.MapClientEventToSimEvent(ControlEvents.AxisElevatorSet, "AXIS_ELEVATOR_SET"));
        TrackOperation(
            connection,
            "subscribe AXIS_ELEVATOR_SET",
            () => connection.AddClientEventToNotificationGroup(
                NotificationGroups.ControlEvents,
                ControlEvents.AxisElevatorSet,
                false));
        TrackOperation(
            connection,
            "set AXIS_ELEVATOR_SET notification priority",
            () => connection.SetNotificationGroupPriority(
                NotificationGroups.ControlEvents,
                SimConnect.SIMCONNECT_GROUP_PRIORITY_STANDARD));
    }

    private void TrackOperation(SimConnect connection, string name, Action action)
    {
        action();
        _simConnectOperations[connection.GetLastSentPacketID()] = name;
    }

    private static double NormalizeElevatorEvent(uint rawValue)
    {
        var signed = unchecked((int)rawValue);
        var normalized = signed >= 0
            ? signed / 16384.0
            : signed / 16383.0;
        return Math.Max(-100, Math.Min(100, normalized * 100.0));
    }

    private enum Definitions
    {
        Frame,
        Metadata,
    }

    private enum Requests
    {
        Frame,
        Metadata,
        Airports,
    }

    private enum NotificationGroups
    {
        ControlEvents,
    }

    private enum ControlEvents : uint
    {
        AxisElevatorSet = 1,
    }
}
