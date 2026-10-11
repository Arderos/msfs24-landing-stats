using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Settings;
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
    public RecorderStatusEventArgs(RecorderState state, string messageKey, params object[] messageArguments)
    {
        State = state;
        MessageKey = messageKey;
        MessageArguments = messageArguments ?? Array.Empty<object>();
    }

    public RecorderState State { get; }

    public string MessageKey { get; }

    public object[] MessageArguments { get; }

    public string Message => LocalizationManager.Format(MessageKey, MessageArguments);
}

internal sealed class LandingEpisodeEventArgs : EventArgs
{
    public LandingEpisodeEventArgs(
        long episodeId,
        IReadOnlyList<TelemetrySample> samples,
        string simulator,
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<AirportFacility> airportFacilities,
        IReadOnlyList<string> controlInputSources,
        TelemetryDecisionJournal? decisionJournal = null,
        Task<IReadOnlyList<RunwayFacility>>? runwayFacilitiesTask = null,
        bool isDiagnosticOnly = false,
        Task<EpisodeFacilitySnapshot>? facilitiesTask = null,
        EpisodeFacilitySnapshot? initialFacilities = null)
    {
        EpisodeId = episodeId;
        Samples = samples;
        Simulator = simulator;
        AircraftTitle = aircraftTitle;
        AircraftType = aircraftType;
        AircraftModel = aircraftModel;
        AirportFacilities = airportFacilities;
        ControlInputSources = controlInputSources;
        DecisionJournal = decisionJournal;
        IsDiagnosticOnly = isDiagnosticOnly;
        RunwayFacilitiesTask = runwayFacilitiesTask ?? Task.FromResult<IReadOnlyList<RunwayFacility>>(Array.Empty<RunwayFacility>());
        FacilitiesTask = facilitiesTask ?? CreateLegacyFacilitySnapshotAsync(episodeId, airportFacilities, RunwayFacilitiesTask);
        InitialFacilities = initialFacilities ?? new EpisodeFacilitySnapshot(episodeId, Array.Empty<RunwayFacility>(),
            Array.Empty<AirportFacility>(), Array.Empty<TelemetrySample>(), Array.Empty<RunwayFacility>(), airportFacilities, false);
    }

    public long EpisodeId { get; }

    public IReadOnlyList<TelemetrySample> Samples { get; }

    public string Simulator { get; }

    public string AircraftTitle { get; }

    public string AircraftType { get; }

    public string AircraftModel { get; }

    public IReadOnlyList<AirportFacility> AirportFacilities { get; }

    public IReadOnlyList<string> ControlInputSources { get; }

    public TelemetryDecisionJournal? DecisionJournal { get; }

    public bool IsDiagnosticOnly { get; }

    public Task<IReadOnlyList<RunwayFacility>> RunwayFacilitiesTask { get; }

    public Task<EpisodeFacilitySnapshot> FacilitiesTask { get; }
    public EpisodeFacilitySnapshot InitialFacilities { get; }

    private static async Task<EpisodeFacilitySnapshot> CreateLegacyFacilitySnapshotAsync(long episodeId,
        IReadOnlyList<AirportFacility> airports, Task<IReadOnlyList<RunwayFacility>> runways)
    {
        return new EpisodeFacilitySnapshot(episodeId, Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(),
            Array.Empty<TelemetrySample>(), await runways.ConfigureAwait(false), airports, true);
    }
}

internal sealed class LandingEpisodeStartedEventArgs : EventArgs
{
    public LandingEpisodeStartedEventArgs(long episodeId)
    {
        EpisodeId = episodeId;
    }

    public long EpisodeId { get; }
}

internal sealed class AirportFacilitiesEventArgs : EventArgs
{
    public AirportFacilitiesEventArgs(IReadOnlyList<AirportFacility> facilities)
    {
        Facilities = facilities;
    }

    public IReadOnlyList<AirportFacility> Facilities { get; }
}

internal sealed class RawDebugCaptureStartedEventArgs : EventArgs
{
    public RawDebugCaptureStartedEventArgs(
        IReadOnlyList<TelemetrySample> initialSamples,
        DateTime startedUtc,
        string simulator,
        string aircraftTitle,
        string aircraftType,
        string aircraftModel,
        IReadOnlyList<string> controlInputSources)
    {
        InitialSamples = initialSamples;
        StartedUtc = startedUtc;
        Simulator = simulator;
        AircraftTitle = aircraftTitle;
        AircraftType = aircraftType;
        AircraftModel = aircraftModel;
        ControlInputSources = controlInputSources;
    }

    public IReadOnlyList<TelemetrySample> InitialSamples { get; }

    public DateTime StartedUtc { get; }

    public string Simulator { get; }

    public string AircraftTitle { get; }

    public string AircraftType { get; }

    public string AircraftModel { get; }

    public IReadOnlyList<string> ControlInputSources { get; }
}

internal sealed class RawDebugSampleEventArgs : EventArgs
{
    public RawDebugSampleEventArgs(TelemetrySample sample)
    {
        Sample = sample;
    }

    public TelemetrySample Sample { get; }
}

internal sealed class AircraftGroundStateEventArgs : EventArgs
{
    public AircraftGroundStateEventArgs(bool onGround)
    {
        OnGround = onGround;
    }

    public bool OnGround { get; }
}

internal sealed partial class SimConnectLandingRecorder : IDisposable
{
    public const int WindowMessage = 0x0403;

    private const double CaptureStartAglFeet = 500.0;
    private const double CaptureStartDescentFps = -0.5;
    private const double PreRollSeconds = 15.0;
    private const double PostContactSeconds = 15.0;
    private const double GoAroundResetAglFeet = 1000.0;
    private const double MaximumApproachSeconds = 300.0;
    private const double FullTelemetryEnableAglFeet = 3000.0;
    private const double FullTelemetryDisableAglFeet = 3500.0;
    private const int PreRollMaximumSamples = 4096;
    private const int EpisodeMaximumSamples = 65536;
    private static readonly TimeSpan JoystickRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IntPtr _windowHandle;
    private readonly Stopwatch _hostClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    private readonly PreRollBuffer _preRoll = new PreRollBuffer(PreRollSeconds, PreRollMaximumSamples);
    private readonly Dictionary<string, AirportFacility> _airportFacilities =
        new Dictionary<string, AirportFacility>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AirportFacility>? _pendingAirportFacilities;
    private readonly Dictionary<uint, string> _simConnectOperations = new Dictionary<uint, string>();
    private readonly WindowsJoystickReader _windowsJoystickReader = new WindowsJoystickReader();
    private readonly Func<SimConnect> _openConnection;
    private readonly CancellationTokenSource _connectionCancellation = new CancellationTokenSource();
    private Task _connectionTask = Task.CompletedTask;
    private int _pendingConnectionMessages;

    private SimConnect? _simConnect;
    private List<TelemetrySample>? _episodeSamples;
    private TelemetryDecisionJournal? _episodeDecisionJournal;
    private int _episodeCoalescedFrameCount;
    private int _episodeNonMotionFrameCount;
    private bool _rawDebugEnabled;
    private DateTime _lastConnectAttemptUtc = DateTime.MinValue;
    private DateTime _nextJoystickRefreshUtc = DateTime.MinValue;
    private long _sequence;
    private long _nextEpisodeId;
    private long _activeEpisodeId;
    private double _lastSimulationTime = double.NaN;
    private double _lastGuardAglFeet = double.NaN;
    private double _episodeStartTime;
    private double? _lastContactTime;
    private readonly TelemetryContactContinuity _contactContinuity = new TelemetryContactContinuity();
    private bool _previousOnGround = true;
    private bool _armed = true;
    private string _simulator = "MSFS";
    private string _aircraftTitle = "Unknown aircraft";
    private string _aircraftType = "unknown";
    private string _aircraftModel = "unknown";
    private double _axisElevatorSetPercent;
    private double _axisElevatorSetReceivedHostSeconds = double.NaN;
    private bool _recoverStatusAfterFrame;
    private bool _fullTelemetryEnabled;
    private bool _airportFacilityRequestPending;
    private uint _nextAirportFacilityRequestId = 0x30000000;
    private uint _airportFacilityRequestId;
    private uint? _airportFacilityRequestPacket;
    private double _airportFacilityRequestDeadline;
    private bool _replayActive;
    private bool? _aircraftOnGround;
    private bool _disposed;

    public SimConnectLandingRecorder(IntPtr windowHandle) : this(windowHandle, null)
    {
    }

    internal SimConnectLandingRecorder(IntPtr windowHandle, Func<SimConnect>? openConnection)
    {
        _windowHandle = windowHandle;
        _openConnection = openConnection ?? (() => new SimConnect(
            "MSFS Landing Stats", _windowHandle, WindowMessage, null, 0));
        _retryTimer.Tick += OnRetryTimerTick;
    }

    public event EventHandler<RecorderStatusEventArgs>? StatusChanged;

    public event EventHandler? SimulatorExited;

    public event EventHandler<LandingEpisodeEventArgs>? EpisodeCompleted;

    public event EventHandler<LandingEpisodeStartedEventArgs>? EpisodeStarted;

    public event EventHandler<AirportFacilitiesEventArgs>? AirportFacilitiesUpdated;

    public event EventHandler<RawDebugCaptureStartedEventArgs>? RawDebugCaptureStarted;

    public event EventHandler<RawDebugSampleEventArgs>? RawDebugSampleReceived;

    public event EventHandler? RawDebugCaptureStopped;

    public event EventHandler<AircraftGroundStateEventArgs>? AircraftGroundStateChanged;

    public bool RawDebugEnabled => _rawDebugEnabled;

    public bool IsAircraftAirborne => _aircraftOnGround == false;

    public void SeedAirportFacilities(IEnumerable<AirportFacility> facilities)
    {
        ThrowIfDisposed();
        foreach (var facility in facilities ?? Array.Empty<AirportFacility>())
        {
            if (!string.IsNullOrWhiteSpace(facility.Ident))
            {
                _airportFacilities[facility.Key] = facility;
            }
        }
    }

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
            if (_episodeSamples == null && _windowsJoystickReader.Refresh())
            {
                _preRoll.Clear();
            }

            _nextJoystickRefreshUtc = DateTime.UtcNow + JoystickRefreshInterval;
            _rawDebugEnabled = true;
            SetFullTelemetryEnabled(true);
            RawDebugCaptureStarted?.Invoke(
                this,
                new RawDebugCaptureStartedEventArgs(
                    _preRoll.ToArray(),
                    DateTime.UtcNow,
                    _simulator,
                    _aircraftTitle,
                    _aircraftType,
                    _aircraftModel,
                    ControlInputSources()));

            return;
        }

        _rawDebugEnabled = false;
        RawDebugCaptureStopped?.Invoke(this, EventArgs.Empty);
        SetFullTelemetryForAgl(double.NaN);
    }

    public IntPtr HandleWindowMessage(int message, ref bool handled)
    {
        if (message != WindowMessage)
        {
            return IntPtr.Zero;
        }

        if (_simConnect == null)
        {
            // The native connection can post notifications before the worker
            // hands it to this dispatcher. Replay each notification after adoption.
            if (!_disposed && !_connectionTask.IsCompleted)
            {
                _pendingConnectionMessages++;
                handled = true;
            }
            return IntPtr.Zero;
        }

        handled = true;
        try
        {
            _simConnect.ReceiveMessage();
        }
        catch (Exception exception)
        {
            SetStatus(RecorderState.Error, "Recorder.ReceiveFailedFormat", exception.Message);
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
        // Do not wait for a native connection attempt on the window's dispatcher.
        _connectionCancellation.Cancel();
        _connectionCancellation.Dispose();
        if (_lastContactTime.HasValue)
        {
            CompleteEpisodeWithReason("recorder_disposed", false);
        }

        if (_rawDebugEnabled)
        {
            _rawDebugEnabled = false;
            RawDebugCaptureStopped?.Invoke(this, EventArgs.Empty);
        }

        CloseSimConnect();
        _hostClock.Stop();
    }

    private void OnRetryTimerTick(object? sender, EventArgs eventArgs)
    {
        ExpireRunwayRequests();
        var now = DateTime.UtcNow;
        if (_episodeSamples == null && !RawDebugEnabled && now >= _nextJoystickRefreshUtc)
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
        if (_simConnect != null || _disposed || !_connectionTask.IsCompleted)
        {
            return;
        }

        _lastConnectAttemptUtc = DateTime.UtcNow;
        _pendingConnectionMessages = 0;
        SetStatus(RecorderState.Waiting, "Recorder.WaitingRetry");
        _connectionTask = ConnectAsync(_connectionCancellation.Token);
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var dispatcher = _retryTimer.Dispatcher;
        try
        {
            await Task.Run(async () =>
            {
                // SimConnect_Open can take hundreds of milliseconds with no simulator.
                // Only construction runs in the worker; all SDK calls and callbacks after
                // the hand-off remain serialized on the window's dispatcher.
                SimConnect? connection = _openConnection();
                try
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (_disposed) return;
                        connection.OnRecvOpen += OnRecvOpen;
                        connection.OnRecvQuit += OnRecvQuit;
                        connection.OnRecvException += OnRecvException;
                        connection.OnRecvSimobjectData += OnRecvSimobjectData;
                        connection.OnRecvAirportList += OnRecvAirportList;
                        connection.OnRecvFacilityData += OnRecvRunwayFacilityData;
                        connection.OnRecvFacilityDataEnd += OnRecvRunwayFacilityDataEnd;
                        connection.OnRecvEvent += OnRecvEvent;
                        connection.OnRecvEventEx1 += OnRecvEventEx1;
                        connection.OnRecvFlowEvent += OnRecvFlowEvent;
                        connection.OnRecvSystemState += OnRecvSystemState;
                        _simConnect = connection;
                        connection = null;
                        // Recover early OPEN/QUIT notifications, not just the first
                        // message. A callback can close the connection while draining.
                        var pendingMessages = _pendingConnectionMessages;
                        _pendingConnectionMessages = 0;
                        for (var index = 0; index < pendingMessages && _simConnect != null; index++)
                        {
                            var handled = false;
                            HandleWindowMessage(WindowMessage, ref handled);
                        }
                    }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
                }
                finally
                {
                    // Cancellation or dispatcher shutdown must not leak a late connection.
                    try { connection?.Dispose(); }
                    catch { /* A dead connection must not prevent shutdown. */ }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested || dispatcher.HasShutdownStarted) return;
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (_disposed) return;
                    if (exception is COMException)
                        SetStatus(RecorderState.Waiting, "Recorder.WaitingRetry");
                    else
                        SetStatus(RecorderState.Error, "Recorder.ConnectionFailedFormat", exception.Message);
                }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* The window closed before reporting. */ }
        }
    }

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        try
        {
            RegisterGuardDefinition(sender);
            sender.RegisterDataDefineStruct<SimGuardData>(Definitions.Guard);
            RegisterFrameDefinition(sender);
            sender.RegisterDataDefineStruct<SimFrameData>(Definitions.Frame);
            sender.AddToDataDefinition(Definitions.Metadata, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0, SimConnect.SIMCONNECT_UNUSED);
            AddMetadataString(sender, "ATC TYPE");
            AddMetadataString(sender, "ATC MODEL");
            AddMetadataString(sender, "CATEGORY");
            sender.RegisterDataDefineStruct<AircraftMetadata>(Definitions.Metadata);
            sender.RequestDataOnSimObject(
                Requests.Guard,
                Definitions.Guard,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SIM_FRAME,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0,
                0,
                0);
            _fullTelemetryEnabled = RawDebugEnabled;
            sender.RequestDataOnSimObject(
                Requests.Frame,
                Definitions.Frame,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                _fullTelemetryEnabled ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.NEVER,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0,
                0,
                0);
            RequestAircraftMetadata(sender);
            if (_airportFacilities.Count == 0)
            {
                RequestAirportFacilities(sender);
            }
            RegisterControlEvents(sender);
            TrySubscribeToFlowEvents(sender);
            SubscribeToLifecycle(sender);

            _simulator = string.IsNullOrWhiteSpace(data.szApplicationName) ? "MSFS" : data.szApplicationName.Trim();
            _recoverStatusAfterFrame = false;
            SetStatus(RecorderState.Connected, "Recorder.ConnectedGate");
        }
        catch (Exception exception)
        {
            SetStatus(RecorderState.Error, "Recorder.SetupFailedFormat", exception.Message);
            Disconnect();
        }
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        _episodeDecisionJournal?.Record(_lastSimulationTime, "recorder", "simulator_exit",
            "Simulator exit notification received; finish the recorded contact before disconnecting.");
        Disconnect();
        SimulatorExited?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        if (!ReferenceEquals(sender, _simConnect)) return;
        (_episodeDecisionJournal ?? _pendingLifecycleJournal).Record(_lastSimulationTime,
            "recorder", "simconnect_request_rejected", "Simulator rejected a request; retain its SDK error and packet identity.",
            new Dictionary<string, double> { ["exception_code"] = data.dwException,
                ["packet_id"] = data.dwSendID, ["parameter_index"] = data.dwIndex });
        if (ReferenceEquals(sender, _simConnect) && _airportFacilityRequestPending && data.dwSendID == _airportFacilityRequestPacket)
        {
            CompleteAirportLookupFailure("Simulator rejected the fresh airport-list request.");
            return;
        }
        if (HandleRunwayException(sender, data.dwSendID)) return;
        var operation = _simConnectOperations.TryGetValue(data.dwSendID, out var value)
            ? $" · {value}"
            : string.Empty;
        _recoverStatusAfterFrame = true;
        SetStatus(
            RecorderState.Error,
            "Recorder.SimConnectErrorFormat",
            data.dwException,
            data.dwSendID,
            data.dwIndex,
            operation);
    }

    private void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        if (!ReferenceEquals(sender, _simConnect)) return;
        if (ProcessLifecycleEvent(data.uEventID, data.dwData)) return;
        ProcessControlEvent(data.uEventID, data.dwData);
    }

    private void OnRecvEventEx1(SimConnect sender, SIMCONNECT_RECV_EVENT_EX1 data)
    {
        if (!ReferenceEquals(sender, _simConnect)) return;
        ProcessControlEvent(data.uEventID, data.dwData0);
    }

    private void OnRecvFlowEvent(SimConnect sender, SIMCONNECT_RECV_FLOW_EVENT data)
    {
        if (!ReferenceEquals(sender, _simConnect)) return;
        HandleSdkFlowEvent(data.FlowEvent);
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
        if (!ReferenceEquals(sender, _simConnect) || !_airportFacilityRequestPending || data.dwRequestID != _airportFacilityRequestId)
        {
            return;
        }

        if (data.dwEntryNumber == 0)
        {
            _pendingAirportFacilities = new Dictionary<string, AirportFacility>(StringComparer.OrdinalIgnoreCase);
        }

        var target = _pendingAirportFacilities;
        if (target == null)
        {
            return;
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
            target[facility.Key] = facility;
        }

        if (data.dwOutOf == 0 || data.dwEntryNumber + 1 >= data.dwOutOf)
        {
            _airportFacilities.Clear();
            foreach (var facility in target.Values)
            {
                _airportFacilities[facility.Key] = facility;
            }

            _pendingAirportFacilities = null;
            _airportFacilityRequestPending = false;
            _airportFacilityRequestPacket = null;
            var episodeLookup = _runwayAwaitingAirportFacilities;
            CaptureFreshEpisodeAirports();
            _runwayAwaitingAirportFacilities = false;
            if (episodeLookup)
                _runwayDecisionJournal?.Record(_runwayDecisionTime, "runway.airports", "fresh_list_ready",
                    "This episode's runway lookup uses a freshly completed simulator airport list, not the persisted cache.",
                    new Dictionary<string, double> { ["airports"] = _airportFacilities.Count });
            AirportFacilitiesUpdated?.Invoke(
                this,
                new AirportFacilitiesEventArgs(new List<AirportFacility>(_airportFacilities.Values)));
            RequestNearbyRunways();
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

        if ((Requests)data.dwRequestID == Requests.Guard)
        {
            var guard = (SimGuardData)data.dwData[0];
            RecoverStatusAfterFrame();
            SetFullTelemetryForAgl(guard.AboveGroundLevelFeet);
            return;
        }

        if ((Requests)data.dwRequestID == Requests.Frame)
        {
            RecoverStatusAfterFrame();
            ProcessSample(ToSample((SimFrameData)data.dwData[0]));
        }
    }

    private TelemetrySample? _lastKinematicFrame;

    private void ProcessSample(TelemetrySample sample)
    {
        if (_sdkStateKnown && !double.IsNaN(_lastSimulationTime) &&
            sample.SimulationTimeSeconds < _lastSimulationTime - 0.001)
            EndFlightSegment("simulation_time_reversed");
        AnnotateSdkFrame(sample);
        var sampleTime = TimeOf(sample);
        if (_replayActive)
        {
            _lastSimulationTime = sample.SimulationTimeSeconds;
            _previousOnGround = sample.OnGround;
            return;
        }

        // Replay frames describe the replay camera/aircraft, not the live
        // aircraft. They must not close the replay warning or enter the raw
        // community telemetry stream without provenance.
        if (_rawDebugEnabled)
        {
            RawDebugSampleReceived?.Invoke(this, new RawDebugSampleEventArgs(sample));
        }

        if (sample.SimulatorState == TelemetrySimulatorState.Paused ||
            sample.SimulatorState == TelemetrySimulatorState.Inactive)
        {
            _lastSimulationTime = sample.SimulationTimeSeconds;
            if (sample.SimulatorState == TelemetrySimulatorState.Paused)
                AppendEpisodeSample(sample);
            else
            {
                _contactContinuity.Reset();
                _lastKinematicFrame = null;
            }
            return;
        }
        UpdateAircraftGroundState(sample.OnGround);

        if (!double.IsNaN(_lastSimulationTime) && sample.SimulationTimeSeconds < _lastSimulationTime - 0.001)
        {
            if (_lastContactTime.HasValue)
            {
                CompleteEpisodeWithReason("simulation_time_reversed");
            }
            else
            {
                DiscardEpisode();
            }

            ResetFlightDetection();
        }

        if (_lastKinematicFrame != null &&
            TelemetryKinematicDiscontinuity.TryDetect(_lastKinematicFrame, sample, out var discontinuity))
        {
            (_episodeDecisionJournal ?? _pendingLifecycleJournal).Record(sampleTime,
                "recorder.lifecycle", "position_discontinuity",
                "Position replacement exceeds the motion explained by active time, speeds and acceleration; close the old capture before this frame.",
                discontinuity!.Measurements());
            EndFlightSegment("position_discontinuity");
            AnnotateSdkFrame(sample);
            sampleTime = TimeOf(sample);
        }
        _lastKinematicFrame = sample.MotionSimulation ? sample.CopyForAnalysis() : null;
        _lastSimulationTime = sample.SimulationTimeSeconds;
        AddToPreRoll(sample);

        if (sample.MotionSimulation && !sample.OnGround)
        {
            if (sample.AboveGroundLevelFeet >= GoAroundResetAglFeet)
            {
                _armed = true;
            }
        }

        var isContact = _contactContinuity.Observe(sample);
        var rejectedContact = sample.OnGround && !_previousOnGround && !isContact;
        var wasRecording = _episodeSamples != null;
        var startedNow = false;
        if (_episodeSamples == null && _armed && (ShouldStartEpisode(sample) || isContact || rejectedContact))
        {
            StartEpisode(sample, isContact, rejectedContact);
            startedNow = true;
        }

        if (_episodeSamples != null && !startedNow)
        {
            AppendEpisodeSample(sample);
        }

        if (isContact)
        {
            _lastContactTime = sampleTime;
            _episodeDecisionJournal?.Record(sampleTime, "recorder", "contact_flag_transition",
                "Observed an airborne-to-ground flag transition; restart post-contact capture from this contact.",
                new Dictionary<string, double>
                {
                    ["post_contact_seconds"] = PostContactSeconds,
                    ["vertical_velocity_fps"] = sample.VelocityWorldYFps
                });
            SetStatus(RecorderState.Recording, "Recorder.ContactRollout");
        }

        _previousOnGround = sample.OnGround;

        if (rejectedContact)
        {
            _episodeDecisionJournal?.Record(sampleTime, "recorder", "physics_discontinuity_rejected",
                "Ground transition has no airborne evidence in the current active-physics segment; retain raw diagnostics, not a landing.");
            if (!_lastContactTime.HasValue)
            {
                CompleteEpisodeWithReason("rejected_contact_diagnostics", false);
                if (wasRecording) SetStatus(RecorderState.Connected, "Recorder.ConnectedGate");
                return;
            }
        }

        if (_episodeSamples != null && _episodeSamples.Count >= EpisodeMaximumSamples)
        {
            if (_lastContactTime.HasValue)
            {
                CompleteEpisodeWithReason("episode_sample_limit");
            }
            else
            {
                DiscardEpisode();
                _armed = true;
                SetStatus(RecorderState.Connected, "Recorder.ApproachRestarted");
            }
            return;
        }

        if (_episodeSamples != null && _lastContactTime.HasValue && sampleTime - _lastContactTime.Value >= PostContactSeconds)
        {
            CompleteEpisodeWithReason("post_contact_capture_complete");
        }
        else if (_episodeSamples != null && !_lastContactTime.HasValue &&
                 sample.AboveGroundLevelFeet >= GoAroundResetAglFeet && sample.VelocityWorldYFps > 0)
        {
            DiscardEpisode();
            _armed = true;
            SetStatus(RecorderState.Connected, "Recorder.GoAround");
        }
        else if (_episodeSamples != null && !_lastContactTime.HasValue && sampleTime - _episodeStartTime >= MaximumApproachSeconds)
        {
            DiscardEpisode();
            _armed = true;
            SetStatus(RecorderState.Connected, "Recorder.ApproachRestarted");
        }
    }

    private void UpdateAircraftGroundState(bool onGround)
    {
        if (_aircraftOnGround == onGround)
        {
            return;
        }

        _aircraftOnGround = onGround;
        AircraftGroundStateChanged?.Invoke(this, new AircraftGroundStateEventArgs(onGround));
    }

    private void StartEpisode(TelemetrySample trigger, bool contactTriggered, bool diagnosticOnly = false)
    {
        _activeEpisodeId = checked(_nextEpisodeId + 1);
        _nextEpisodeId = _activeEpisodeId;
        _episodeSamples = new List<TelemetrySample>(_preRoll.Count + 4096);
        foreach (var sample in _preRoll)
        {
            _episodeSamples.Add(sample);
        }

        _episodeStartTime = TimeOf(trigger);
        _lastContactTime = null;
        _armed = false;
        _episodeDecisionJournal = new TelemetryDecisionJournal(_pendingLifecycleJournal.Snapshot());
        _pendingLifecycleJournal = new TelemetryDecisionJournal();
        _episodeCoalescedFrameCount = 0;
        _episodeNonMotionFrameCount = trigger.MotionSimulation ? 0 : 1;
        _episodeDecisionJournal.Record(_episodeStartTime, "recorder",
            diagnosticOnly ? "capture_started_for_rejected_contact" : contactTriggered ? "capture_started_at_contact" : "capture_started_at_approach_gate",
            diagnosticOnly ? "Retain pre-roll and the rejected ground transition for diagnostics only."
                : contactTriggered ? "Started capture from an observed contact, retaining the available pre-roll."
                : "Started capture on the descending, airborne approach gate, retaining the available pre-roll.",
            new Dictionary<string, double>
            {
                ["pre_roll_sample_count"] = _episodeSamples.Count,
                ["agl_feet"] = trigger.AboveGroundLevelFeet,
                ["vertical_velocity_fps"] = trigger.VelocityWorldYFps,
                ["gate_agl_feet"] = CaptureStartAglFeet,
                ["gate_descent_fps"] = CaptureStartDescentFps
            });
        if (diagnosticOnly) return;
        BeginRunwayEpisode(_activeEpisodeId);
        _runwayDecisionJournal = _episodeDecisionJournal;
        _runwayDecisionTime = _episodeStartTime;
        RequestPreliminaryRunways(trigger, _episodeSamples);
        EpisodeStarted?.Invoke(this, new LandingEpisodeStartedEventArgs(_activeEpisodeId));
        if (_simConnect != null)
        {
            RequestAircraftMetadata(_simConnect);
        }

        SetStatus(
            RecorderState.Recording,
            contactTriggered ? "Recorder.LateStart" : "Recorder.BelowGate");
    }

    private void AppendEpisodeSample(TelemetrySample sample)
    {
        if (_episodeSamples == null)
        {
            return;
        }

        if (!sample.MotionSimulation && _episodeNonMotionFrameCount < int.MaxValue) _episodeNonMotionFrameCount++;

        // Keep both ends of a confirmed pause, irrespective of stale motion flag
        // chatter or a clock which continues running for hours in the menu.
        if (sample.SimulatorState == TelemetrySimulatorState.Paused && _episodeSamples.Count >= 2 &&
            _episodeSamples[_episodeSamples.Count - 1].SimulatorState == TelemetrySimulatorState.Paused &&
            _episodeSamples[_episodeSamples.Count - 2].SimulatorState == TelemetrySimulatorState.Paused)
        {
            _episodeSamples[_episodeSamples.Count - 1] = sample;
            if (_episodeCoalescedFrameCount < int.MaxValue) _episodeCoalescedFrameCount++;
            return;
        }

        if (sample.SimulatorState != TelemetrySimulatorState.Paused && _episodeSamples.Count > 0 &&
            _episodeSamples[_episodeSamples.Count - 1].MotionSimulation == sample.MotionSimulation &&
            _episodeSamples[_episodeSamples.Count - 1].SimulatorState == sample.SimulatorState &&
            _episodeSamples[_episodeSamples.Count - 1].FlightSegment == sample.FlightSegment &&
            Math.Abs(TimeOf(_episodeSamples[_episodeSamples.Count - 1]) - TimeOf(sample)) <= 0.000000001)
        {
            // SIM_FRAME can continue while the simulator clock is paused. Keep the
            // newest state within each physics state, preserving pause/resume boundaries.
            _episodeSamples[_episodeSamples.Count - 1] = sample;
            if (_episodeCoalescedFrameCount < int.MaxValue) _episodeCoalescedFrameCount++;
            return;
        }

        _episodeSamples.Add(sample);
    }

    private void CompleteEpisode(bool refreshAirportFacilities = true)
    {
        var samples = _episodeSamples;
        var episodeId = _activeEpisodeId;
        var decisionJournal = _episodeDecisionJournal;
        var diagnosticOnly = !_lastContactTime.HasValue;
        _episodeSamples = null;
        _episodeDecisionJournal = null;
        _activeEpisodeId = 0;
        _lastContactTime = null;
        _armed = true;
        _preRoll.Clear();
        if (samples == null || samples.Count == 0)
        {
            return;
        }

        decisionJournal?.Record(TimeOf(samples[samples.Count - 1]), "recorder", "capture_completed",
            "Completed capture; same-time deliveries kept their latest state without erasing physics boundaries. Non-motion observations are retained for diagnostics.",
            new Dictionary<string, double>
            {
                ["retained_sample_count"] = samples.Count,
                ["same_time_frames_coalesced"] = _episodeCoalescedFrameCount,
                ["non_motion_frames_observed_after_start"] = _episodeNonMotionFrameCount
            });

        // Keep this approach's snapshot, but never inherit another episode's
        // scenery when a late-start or salvaged capture completes.
        if (!diagnosticOnly)
        {
            if (_runwayEpisodeId != episodeId) BeginRunwayEpisode(episodeId);
            _runwayDecisionJournal = decisionJournal;
            _runwayDecisionTime = TimeOf(samples[samples.Count - 1]);
            if (!refreshAirportFacilities || _simConnect == null || _disposed)
                CompleteRunwayLookupWithoutRefresh("Capture ended without a final scenery lookup; retain only this approach's preliminary facilities.");
        }
        EpisodeCompleted?.Invoke(
            this,
            new LandingEpisodeEventArgs(
                episodeId,
                samples.ToArray(),
                _simulator,
                _aircraftTitle,
                _aircraftType,
                _aircraftModel,
                new List<AirportFacility>(_airportFacilities.Values),
                ControlInputSources(),
                decisionJournal: decisionJournal,
                runwayFacilitiesTask: diagnosticOnly ? null : _runwaySnapshotCompletion?.Task,
                isDiagnosticOnly: diagnosticOnly,
                facilitiesTask: diagnosticOnly ? null : _episodeFacilityCompletion?.Task,
                initialFacilities: diagnosticOnly ? null : CurrentEpisodeFacilitySnapshot()));

        if (!diagnosticOnly && refreshAirportFacilities && _simConnect != null && !_disposed) RequestEpisodeRunways(samples);
    }

    private void DiscardEpisode()
    {
        if (_activeEpisodeId != 0 && _activeEpisodeId == _runwayEpisodeId)
        {
            CompleteRunwayLookupWithoutRefresh("Approach discarded; cancel its native requests before another flight can reply.");
            ResetRunwayLookupStage();
            _runwayEpisodeId = 0;
            _runwayDecisionJournal = null;
        }
        _episodeSamples = null;
        _episodeDecisionJournal = null;
        _activeEpisodeId = 0;
        _lastContactTime = null;
    }

    private void CompleteEpisodeWithReason(string reason, bool refreshAirportFacilities = true)
    {
        _episodeDecisionJournal?.Record(_episodeSamples != null && _episodeSamples.Count > 0
            ? TimeOf(_episodeSamples[_episodeSamples.Count - 1]) : _lastSimulationTime, "recorder", reason,
            "This condition ended the capture; existing samples are passed to landing analysis.");
        CompleteEpisode(refreshAirportFacilities);
    }

    private void AddToPreRoll(TelemetrySample sample)
    {
        // SDK-classified pauses do not consume approach history. Legacy captures
        // retain their original monotonic-receipt policy.
        _preRoll.Add(sample, sample.SimulatorState == TelemetrySimulatorState.Legacy
            ? _hostClock.Elapsed.TotalSeconds : TimeOf(sample));
    }

    private void SetFullTelemetryForAgl(double aglFeet)
    {
        if (!double.IsNaN(aglFeet) && !double.IsInfinity(aglFeet))
        {
            _lastGuardAglFeet = aglFeet;
        }

        var enabled = RawDebugEnabled ||
                      _episodeSamples != null ||
                      (!double.IsNaN(_lastGuardAglFeet) &&
                       (_fullTelemetryEnabled
                           ? _lastGuardAglFeet <= FullTelemetryDisableAglFeet
                           : _lastGuardAglFeet <= FullTelemetryEnableAglFeet));
        SetFullTelemetryEnabled(enabled);
    }

    private void SetFullTelemetryEnabled(bool enabled)
    {
        if (_fullTelemetryEnabled == enabled)
        {
            return;
        }

        _fullTelemetryEnabled = enabled;
        if (_simConnect == null)
        {
            return;
        }

        try
        {
            _simConnect.RequestDataOnSimObject(
                Requests.Frame,
                Definitions.Frame,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                enabled ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.NEVER,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0,
                0,
                0);
        }
        catch (Exception exception)
        {
            _fullTelemetryEnabled = false;
            SetStatus(RecorderState.Error, "Recorder.ModeChangeFailedFormat", exception.Message);
            Disconnect();
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
            CompleteEpisodeWithReason("connection_closed", false);
        }
        else
        {
            DiscardEpisode();
        }

        CloseSimConnect();
        _fullTelemetryEnabled = false;
        _airportFacilityRequestPending = false;
        _pendingAirportFacilities = null;
        _replayActive = false;
        _recoverStatusAfterFrame = false;
        ResetFlightDetection();
        ResetSdkLifecycle();
        if (!_disposed)
        {
            SetStatus(RecorderState.Waiting, "Recorder.DisconnectedRetry");
        }
    }

    private void ResetFlightDetection()
    {
        _lastKinematicFrame = null;
        _preRoll.Clear();
        _contactContinuity.Reset();
        _previousOnGround = true;
        _armed = true;
        _lastSimulationTime = double.NaN;
        _lastGuardAglFeet = double.NaN;
        _axisElevatorSetPercent = 0;
        _axisElevatorSetReceivedHostSeconds = double.NaN;
    }

    private void CloseSimConnect()
    {
        ResetRunwayConnection();
        var connection = _simConnect;
        _simConnect = null;
        try
        {
            connection?.Dispose();
        }
        catch
        {
            // A dead simulator connection is already unusable; shutdown and retry must continue.
        }
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
            GForce = frame.GForce,
            AboveGroundLevelFeet = frame.AboveGroundLevelFeet,
            PitchDegrees = frame.PitchDegrees,
            BankDegrees = frame.BankDegrees,
            LatitudeDegrees = frame.LatitudeDegrees,
            LongitudeDegrees = frame.LongitudeDegrees,
            IndicatedAirspeedKnots = frame.IndicatedAirspeedKnots,
            GroundSpeedKnots = frame.GroundSpeedKnots,
            PlaneAltitudeFeet = frame.PlaneAltitudeFeet,
            GroundAltitudeFeet = frame.GroundAltitudeFeet,
            RotationVelocityBodyXRadiansPerSecond = frame.RotationVelocityBodyXRadiansPerSecond,
            RotationVelocityBodyYRadiansPerSecond = frame.RotationVelocityBodyYRadiansPerSecond,
            RotationVelocityBodyZRadiansPerSecond = frame.RotationVelocityBodyZRadiansPerSecond,
            AccelerationBodyXFps2 = frame.AccelerationBodyXFps2,
            AccelerationBodyZFps2 = frame.AccelerationBodyZFps2,
            HeadingTrueDegrees = frame.HeadingTrueDegrees,
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
            FlapsLeftPercent = frame.FlapsLeftPercent,
            FlapsRightPercent = frame.FlapsRightPercent,
            BrakeLeftPosition = frame.BrakeLeftPosition,
            BrakeRightPosition = frame.BrakeRightPosition,
            TotalWeightPounds = frame.TotalWeightPounds,
            CgPercent = frame.CgPercent,
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

        _windowsJoystickReader.ReadInto(sample);

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
        if (!double.IsNaN(sample.AnalysisTimeSeconds) && !double.IsInfinity(sample.AnalysisTimeSeconds))
            return sample.AnalysisTimeSeconds;
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

    private void SetStatus(RecorderState state, string messageKey, params object[] messageArguments)
    {
        StatusChanged?.Invoke(this, new RecorderStatusEventArgs(state, messageKey, messageArguments));
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
                    ? "Recorder.ContactRollout"
                    : "Recorder.BelowGate");
        }
        else
        {
            SetStatus(RecorderState.Connected, "Recorder.ConnectedGate");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SimConnectLandingRecorder));
        }
    }

    private static void RegisterGuardDefinition(SimConnect connection)
    {
        connection.AddToDataDefinition(Definitions.Guard, "SIMULATION TIME", "seconds", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        connection.AddToDataDefinition(Definitions.Guard, "PLANE ALT ABOVE GROUND", "Feet", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        connection.AddToDataDefinition(Definitions.Guard, "SIM ON GROUND", "Bool", SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
        connection.AddToDataDefinition(Definitions.Guard, "VELOCITY WORLD Y", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        connection.AddToDataDefinition(Definitions.Guard, "MOTION SIMULATION", "Bool", SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
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
        AddDouble(connection, "G FORCE", "GForce");
        AddDouble(connection, "PLANE ALT ABOVE GROUND", "Feet");
        AddDouble(connection, "PLANE PITCH DEGREES", "Degrees");
        AddDouble(connection, "PLANE BANK DEGREES", "Degrees");
        AddDouble(connection, "PLANE LATITUDE", "Degrees");
        AddDouble(connection, "PLANE LONGITUDE", "Degrees");
        AddDouble(connection, "AIRSPEED INDICATED", "Knots");
        AddDouble(connection, "GROUND VELOCITY", "Knots");
        AddDouble(connection, "PLANE ALTITUDE", "Feet");
        AddDouble(connection, "GROUND ALTITUDE", "Feet");
        AddDouble(connection, "ROTATION VELOCITY BODY X", "Radians per second");
        AddDouble(connection, "ROTATION VELOCITY BODY Y", "Radians per second");
        AddDouble(connection, "ROTATION VELOCITY BODY Z", "Radians per second");
        AddDouble(connection, "ACCELERATION BODY X", "Feet per second squared");
        AddDouble(connection, "ACCELERATION BODY Z", "Feet per second squared");
        AddDouble(connection, "PLANE HEADING DEGREES TRUE", "Degrees");
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
        AddDouble(connection, "TRAILING EDGE FLAPS LEFT PERCENT", "Percent Over 100");
        AddDouble(connection, "TRAILING EDGE FLAPS RIGHT PERCENT", "Percent Over 100");
        AddDouble(connection, "BRAKE LEFT POSITION", "Position");
        AddDouble(connection, "BRAKE RIGHT POSITION", "Position");
        AddDouble(connection, "TOTAL WEIGHT", "Pounds");
        AddDouble(connection, "CG PERCENT", "Percent");
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
            SIMCONNECT_DATA_REQUEST_FLAG.CHANGED,
            0,
            0,
            0);
    }

    private void RequestAirportFacilities(SimConnect connection)
    {
        if (_airportFacilityRequestPending)
        {
            return;
        }

        _airportFacilityRequestPending = true;
        _pendingAirportFacilities = null;
        _airportFacilityRequestId = _nextAirportFacilityRequestId++;
        _airportFacilityRequestPacket = null;
        _airportFacilityRequestDeadline = _hostClock.Elapsed.TotalSeconds + 10;
        try
        {
            connection.RequestFacilitiesList_EX1(
                SIMCONNECT_FACILITY_LIST_TYPE.AIRPORT,
                (Requests)_airportFacilityRequestId);
            _airportFacilityRequestPacket = connection.GetLastSentPacketID();
        }
        catch
        {
            _airportFacilityRequestPending = false;
            throw;
        }
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

    private static void TrySubscribeToFlowEvents(SimConnect connection)
    {
        try
        {
            connection.SubscribeToFlowEvent();
        }
        catch
        {
            // Flow events are an MSFS 2024 addition. Older SimConnect servers can
            // still use the kinematic replay detector when an episode completes.
        }
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
        Guard,
        Frame,
        Metadata,
        Runways,
        RunwayPrimaryThresholds,
        RunwaySecondaryThresholds,
        RunwayPrimaryLeftPapi,
        RunwayPrimaryRightPapi,
        RunwaySecondaryLeftPapi,
        RunwaySecondaryRightPapi,
        RunwayIlsGlideSlope,
    }

    private enum Requests
    {
        Guard,
        Frame,
        Metadata,
        Airports,
        SimulationState,
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
