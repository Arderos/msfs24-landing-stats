using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectSource : ISimulatorSource, IAircraftConfigurationSource
{
    public const int WindowMessage = 0x0403;

    private const double FullTelemetryEnableAglFeet = 3000.0;
    private const double FullTelemetryDisableAglFeet = 3500.0;
    private readonly ISimulatorCaptureContext _capture;
    private readonly ISceneryQueryObserver _scenery;
    public ISceneryQueryPort SceneryQuery { get; }
    public bool IsConnected => _simConnect != null && _connectionSetupComplete;
    public long SceneryRevision { get; private set; }
    public uint ApplicationVersionMajor { get; private set; }
    public event Action<string>? Connected;
    public event Action<SourceDisconnectionReason>? Disconnected;
    public event Action<TelemetrySample>? SampleReceived;
    public event Action<GuardFrameMessage>? GuardReceived;
    public event Action<ReplaySourceObservation>? SourceObservationReceived;
    public event Action<string, string, string>? MetadataReceived;
    public event Action<SourceLifecycle, int>? LifecycleReceived;
    private readonly IntPtr _windowHandle;
    private readonly Stopwatch _hostClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, AirportFacility> _airportFacilities =
        new Dictionary<string, AirportFacility>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AirportFacility>? _pendingAirportFacilities;
    private readonly SimConnectOperationJournal _simConnectOperations = new SimConnectOperationJournal();
    private readonly Func<SimConnect> _openConnection;
    private readonly Action<SimConnect> _receiveConnection;
    private readonly Action<SimConnect> _disposeConnection;
    private readonly Func<SimConnect, uint, uint, bool, uint?> _requestTelemetry;
    private readonly Func<double> _subscriptionClock;
    private readonly Action<SimConnect, uint, uint> _requestHealthProbe;
    private readonly Action<SimConnect, bool> _requestMetadata;
    private readonly SimulatorStreamWatchdog _streamWatchdog = new SimulatorStreamWatchdog();
    private readonly RequiredTelemetrySubscription _guardSubscription = new RequiredTelemetrySubscription();
    private readonly RequiredTelemetrySubscription _fullSubscription = new RequiredTelemetrySubscription();
    private readonly Queue<SimConnect> _retiredConnections = new Queue<SimConnect>();
    private int _dispatchDepth;
    private bool _closingConnections;
    private SimConnect? _simConnect;
    private bool _connectionSetupComplete;
    private double _lastConnectAttemptSeconds = double.NegativeInfinity;
    private double _connectionOpenedSeconds;
    private long _sequence;
    private double _lastGuardAglFeet = double.NaN;
    private double _axisElevatorSetPercent;
    private double _axisElevatorSetReceivedHostSeconds = double.NaN;
    private bool _recoverStatusAfterFrame;
    private bool _fullTelemetryEnabled;
    private bool _airportFacilityRequestPending;
    private uint _nextAirportFacilityRequestId = 0x30000000;
    private uint _airportFacilityRequestId;
    private uint? _airportFacilityRequestPacket;
    private double _airportFacilityRequestDeadline;
    private bool _disposed;

    internal SimConnectSource(IntPtr windowHandle, ISimulatorCaptureContext capture, ISceneryQueryObserver scenery,
        Func<SimConnect>? openConnection = null, Action<SimConnect>? receiveConnection = null, Action<SimConnect>? disposeConnection = null,
        Func<SimConnect, uint, uint, bool, uint?>? requestTelemetry = null, Func<double>? subscriptionClock = null,
        Action<SimConnect, uint, uint>? requestHealthProbe = null,
        Action<SimConnect>? registerSourceObservation = null, Func<SimConnect, uint, uint?>? requestSourceObservation = null,
        Action<SimConnect, bool>? requestMetadata = null)
    {
        _capture = capture; _scenery = scenery;
        SceneryQuery = new LocalSceneryQueryPort(this);
        _windowHandle = windowHandle;
        _openConnection = openConnection ?? (() => new SimConnect(
            "MSFS Landing Stats", _windowHandle, WindowMessage, null, 0));
        _receiveConnection = receiveConnection ?? (connection => connection.ReceiveMessage());
        _disposeConnection = disposeConnection ?? (connection => connection.Dispose());
        _requestTelemetry = requestTelemetry ?? RequestTelemetryNative;
        _subscriptionClock = subscriptionClock ?? (() => _hostClock.Elapsed.TotalSeconds);
        _requestHealthProbe = requestHealthProbe ?? RequestHealthProbeNative;
        _requestMetadata = requestMetadata ?? RequestAircraftMetadata;
        _registerSourceObservation = registerSourceObservation ?? RegisterSourceObservationNative;
        _requestSourceObservation = requestSourceObservation ?? RequestSourceObservationNative;
        _sourceObservationTimer.Tick += (_, _) => MaintainSourceObservation();
        _guardSubscription.Reset(true);
        _fullSubscription.Reset(false);
        _retryTimer.Tick += OnRetryTimerTick;
    }

    public event EventHandler<RecorderStatusEventArgs>? StatusChanged;
    public event EventHandler? SimulatorExited;
    public bool RawDebugEnabled => _capture.RawDebugEnabled;

    public void Start()
    {
        ThrowIfDisposed();
        _retryTimer.Start();
        TryConnect();
    }

    public void RefreshTelemetryMode() { ThrowIfDisposed(); SetFullTelemetryForAgl(double.NaN); }
    public void RefreshMetadata()
    {
        ThrowIfDisposed(); TryOptionalRefresh("metadata_refresh_failed", connection => _requestMetadata(connection, true));
        _aircraftConfigurationPath = string.Empty; RequestAircraftConfiguration();
    }
    private void TryOptionalRefresh(string code, Action<SimConnect> refresh)
    {
        if (DeferNativeEffect(() => TryOptionalRefresh(code, refresh))) return;
        var connection = _simConnect;
        if (connection == null) return;
        try { refresh(connection); }
        catch (Exception error)
        { _capture.DecisionJournal.Record(_capture.LastSimulationTime, "source", code, error.Message); }
    }
    public void ResetControlContext()
    {
        _lastGuardAglFeet = double.NaN; _axisElevatorSetPercent = 0; _axisElevatorSetReceivedHostSeconds = double.NaN;
    }

    public IntPtr HandleWindowMessage(int message, ref bool handled)
    {
        if (message != WindowMessage)
        {
            return IntPtr.Zero;
        }

        if (_simConnect == null)
        {
            return IntPtr.Zero;
        }

        handled = true;
        var connection = _simConnect;
        _dispatchDepth++;
        try
        {
            _receiveConnection(connection);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(connection, _simConnect))
            {
                SetStatus(RecorderState.Error, "Recorder.ReceiveFailedFormat", exception.Message);
                DisconnectWithReason(SourceDisconnectionReason.ReceiveFailed);
            }
        }
        finally
        {
            _dispatchDepth--;
            DrainNativeEffects();
            DisposeRetiredConnections();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer.Stop();
        CloseSimConnect(); _hostClock.Stop();
    }

    private void OnRetryTimerTick(object? sender, EventArgs eventArgs)
    {
        ExpireRunwayRequests();
        MaintainStreamHealth();
        RetryRequiredSubscriptions();
        var now = _subscriptionClock();
        if (_simConnect != null && !_connectionSetupComplete && now - _connectionOpenedSeconds >= 10)
        {
            _capture.DecisionJournal.Record(_capture.LastSimulationTime, "source", "open_timeout", "Simulator did not acknowledge the opened connection within ten seconds.");
            DisconnectWithReason(SourceDisconnectionReason.OpenTimeout);
        }
        if (_simConnect == null && now - _lastConnectAttemptSeconds >= 2)
        {
            TryConnect();
        }
    }

    private void TryConnect()
    {
        if (_simConnect != null || _disposed || _dispatchDepth != 0 || _closingConnections || _retiredConnections.Count != 0)
        {
            return;
        }

        _lastConnectAttemptSeconds = _subscriptionClock();
        _connectionOpenedSeconds = _lastConnectAttemptSeconds;
        SetStatus(RecorderState.Waiting, "Recorder.WaitingRetry");
        {
            // Worker owns SDK construction, setup, dispatch and disposal on one
            // dedicated dispatcher thread. A blocked native call is terminated
            // only with the whole generation by the parent supervisor.
            SimConnect? pending = null;
            try { pending = _openConnection(); AdoptConnection(pending); pending = null; }
            catch (COMException) { SetStatus(RecorderState.Waiting, "Recorder.WaitingRetry"); }
            catch (Exception exception)
            {
                CloseSimConnect();
                _capture.DecisionJournal.Record(_capture.LastSimulationTime, "source", "open_failed", exception.Message);
                SetStatus(RecorderState.Waiting, "Recorder.WaitingRetry");
            }
            finally { if (pending != null) _disposeConnection(pending); }
        }
    }

    private void AdoptConnection(SimConnect connection)
    {
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
        connection.OnRecvEventFilename += OnRecvAircraftFilename;
        _simConnect = connection;
    }

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        if (!ReferenceEquals(sender, _simConnect)) return;
        _streamWatchdog.Reset(_subscriptionClock());
        _simConnectOperations.BeginConnection(new Dictionary<string, double>
        {
            ["application_version_major"] = data.dwApplicationVersionMajor, ["application_version_minor"] = data.dwApplicationVersionMinor,
            ["application_build_major"] = data.dwApplicationBuildMajor, ["application_build_minor"] = data.dwApplicationBuildMinor,
            ["sdk_version_major"] = data.dwSimConnectVersionMajor, ["sdk_version_minor"] = data.dwSimConnectVersionMinor,
            ["sdk_build_major"] = data.dwSimConnectBuildMajor, ["sdk_build_minor"] = data.dwSimConnectBuildMinor,
        });
        try
        {
            RegisterGuardDefinition(sender);
            sender.RegisterDataDefineStruct<SimGuardData>(Definitions.Guard);
            RegisterFrameDefinition(sender);
            sender.RegisterDataDefineStruct<SimFrameData>(Definitions.Frame);
            AddMetadataString(sender, "TITLE");
            AddMetadataString(sender, "ATC TYPE");
            AddMetadataString(sender, "ATC MODEL");
            AddMetadataString(sender, "CATEGORY");
            sender.RegisterDataDefineStruct<AircraftMetadata>(Definitions.Metadata);
            _guardSubscription.Reset(true);
            RequestRequiredSubscription(sender, _guardSubscription, Requests.Guard, Definitions.Guard, "Request guard telemetry");
            _fullTelemetryEnabled = RawDebugEnabled;
            _fullSubscription.Reset(_fullTelemetryEnabled);
            RequestRequiredSubscription(sender, _fullSubscription, Requests.Frame, Definitions.Frame, "Request full telemetry");
            _requestMetadata(sender, false);
            RegisterControlEvents(sender);
            TrySubscribeToFlowEvents(sender);
            SubscribeToLifecycle(sender);

            ApplicationVersionMajor = data.dwApplicationVersionMajor;
            _connectionSetupComplete = true;
            Connected?.Invoke(string.IsNullOrWhiteSpace(data.szApplicationName) ? "MSFS" : data.szApplicationName.Trim());
            StartAircraftConfiguration();
            StartSourceObservation();
            _recoverStatusAfterFrame = false;
            SetStatus(RecorderState.Connected, "Recorder.ConnectedGate");
        }
        catch (Exception exception)
        {
            SetStatus(RecorderState.Error, "Recorder.SetupFailedFormat", exception.Message);
            DisconnectWithReason(SourceDisconnectionReason.SetupFailed);
        }
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        if (_disposed || !ReferenceEquals(sender, _simConnect)) return;
        _capture.DecisionJournal.Record(_capture.LastSimulationTime, "recorder", "simulator_exit",
            "Simulator exit notification received; finish the recorded contact before disconnecting.");
        DisconnectWithReason(SourceDisconnectionReason.SimulatorQuit);
        SimulatorExited?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        if (!ReferenceEquals(sender, _simConnect)) return;
        _simConnectOperations.RecordRejection(_capture.DecisionJournal,
            _capture.LastSimulationTime, data.dwException, data.dwSendID, data.dwIndex);
        if (HandleSourceObservationException(data.dwSendID, data.dwException)) return;
        if (ReferenceEquals(sender, _simConnect) && _airportFacilityRequestPending && data.dwSendID == _airportFacilityRequestPacket)
        {
            CompleteAirportLookupFailure("Simulator rejected the fresh airport-list request.");
            return;
        }
        if (HandleRunwayException(sender, data.dwSendID)) return;
        if (_simConnectOperations.IsRequiredDefinition(data.dwSendID))
        {
            // A rejected struct field makes offsets unreliable. Fence this
            // connection before any later native payload can be interpreted.
            DisconnectWithReason(SourceDisconnectionReason.DefinitionRejected);
            return;
        }
        if (_simConnectOperations.IsOptional(data.dwSendID)) return;
        _guardSubscription.Reject(data.dwSendID, _subscriptionClock());
        _fullSubscription.Reject(data.dwSendID, _subscriptionClock());
        var operation = _simConnectOperations.Name(data.dwSendID) is string value
            ? $" Â· {value}"
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
                _scenery.DecisionJournal?.Record(_scenery.DecisionTime, "runway.airports", "fresh_list_ready",
                    "This episode's runway lookup uses a freshly completed simulator airport list, not the persisted cache.",
                    new Dictionary<string, double> { ["airports"] = _airportFacilities.Count });
            RequestNearbyRunways();
        }
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (_disposed || !ReferenceEquals(sender, _simConnect)) return;
        if (data.dwData.Length == 0)
        {
            return;
        }

        var now = _subscriptionClock();
        if (ReceiveSourceObservation(data)) return;
        if (_streamWatchdog.FrameRequest != 0 && data.dwRequestID == _streamWatchdog.FrameRequest && data.dwData[0] is SimGuardData probe)
        {
            _streamWatchdog.ObserveProbe(data.dwRequestID, probe.SimulationTimeSeconds, probe.MotionSimulation != 0, now);
            return; // Liveness evidence never enters the recording/AGL stream.
        }

        if ((Requests)data.dwRequestID == Requests.Metadata || (Requests)data.dwRequestID == Requests.MetadataRefresh)
        {
            var metadata = (AircraftMetadata)data.dwData[0];
            ObserveAircraftCategory(metadata.Category);
            MetadataReceived?.Invoke(ValueOrUnknown(metadata.Title, "Unknown aircraft"),
                ValueOrUnknown(metadata.AtcType, "unknown"), ValueOrUnknown(metadata.AtcModel, "unknown"));
            return;
        }

        if ((Requests)data.dwRequestID == Requests.Guard)
        {
            var guard = (SimGuardData)data.dwData[0];
            _guardSubscription.Observe(now);
            _streamWatchdog.ObserveData(guard.SimulationTimeSeconds, guard.MotionSimulation != 0, now);
            RecoverStatusAfterFrame();
            GuardReceived?.Invoke(new GuardFrameMessage(guard.SimulationTimeSeconds, guard.AboveGroundLevelFeet,
                guard.OnGround != 0, guard.VelocityWorldYFps, guard.MotionSimulation != 0));
            SetFullTelemetryForAgl(guard.AboveGroundLevelFeet);
            return;
        }

        if ((Requests)data.dwRequestID == Requests.Frame)
        {
            var frame = (SimFrameData)data.dwData[0];
            _fullSubscription.Observe(now);
            _streamWatchdog.ObserveData(frame.SimulationTimeSeconds, frame.MotionSimulation != 0, now);
            RecoverStatusAfterFrame();
            SampleReceived?.Invoke(ToSample(frame));
        }
    }


    private void SetFullTelemetryForAgl(double aglFeet)
    {
        if (!double.IsNaN(aglFeet) && !double.IsInfinity(aglFeet))
        {
            _lastGuardAglFeet = aglFeet;
        }

        var enabled = RawDebugEnabled ||
                      _capture.IsRecording ||
                      (!double.IsNaN(_lastGuardAglFeet) &&
                       (_fullTelemetryEnabled
                           ? _lastGuardAglFeet <= FullTelemetryDisableAglFeet
                           : _lastGuardAglFeet <= FullTelemetryEnableAglFeet));
        SetFullTelemetryEnabled(enabled);
    }

    private void SetFullTelemetryEnabled(bool enabled)
    {
        _fullTelemetryEnabled = enabled;
        _fullSubscription.SetDesired(enabled);
        if (DeferNativeEffect(() => SetFullTelemetryEnabled(_fullSubscription.Desired))) return;
        if (_simConnect == null || !_connectionSetupComplete || !_fullSubscription.ShouldRequest(_subscriptionClock()))
        {
            return;
        }

        try
        {
            RequestRequiredSubscription(_simConnect, _fullSubscription, Requests.Frame, Definitions.Frame,
                enabled ? "Enable full telemetry" : "Disable full telemetry");
        }
        catch (Exception exception)
        {
            _fullTelemetryEnabled = false;
            SetStatus(RecorderState.Error, "Recorder.ModeChangeFailedFormat", exception.Message);
            DisconnectWithReason(SourceDisconnectionReason.ModeChangeFailed);
        }
    }

    private static uint? RequestTelemetryNative(SimConnect connection, uint request, uint definition, bool enabled)
    {
        connection.RequestDataOnSimObject((Requests)request, (Definitions)definition, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            enabled ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        try { return connection.GetLastSentPacketID(); }
        catch { return null; }
    }

    private void RequestRequiredSubscription(SimConnect connection, RequiredTelemetrySubscription subscription,
        Requests request, Definitions definition, string name)
    {
        if (!subscription.ShouldRequest(_subscriptionClock())) return;
        var packet = _requestTelemetry(connection, (uint)request, (uint)definition, subscription.Desired);
        subscription.Submitted(packet, _subscriptionClock());
        if (packet.HasValue)
            _simConnectOperations.Remember(packet.Value, name, false, (uint)request, (uint)definition,
                period: (uint)(subscription.Desired ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.NEVER),
                flags: (uint)SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT);
    }

    private void RetryRequiredSubscriptions()
    {
        var connection = _simConnect;
        if (connection == null || !_connectionSetupComplete || _disposed || _dispatchDepth != 0) return;
        try
        {
            var now = _subscriptionClock();
            if (_guardSubscription.ShouldRequest(now))
                RequestRequiredSubscription(connection, _guardSubscription, Requests.Guard, Definitions.Guard, "Retry guard telemetry");
            if (_fullSubscription.ShouldRequest(now))
                RequestRequiredSubscription(connection, _fullSubscription, Requests.Frame, Definitions.Frame, "Retry full telemetry");
        }
        catch (Exception exception)
        {
            SetStatus(RecorderState.Error, "Recorder.SetupFailedFormat", exception.Message);
            Disconnect();
        }
    }

    private void Disconnect() => DisconnectWithReason(SourceDisconnectionReason.ConnectionClosed);
    private void DisconnectWithReason(SourceDisconnectionReason reason)
    {
        try { Disconnected?.Invoke(reason); }
        finally
        {
            CloseSimConnect();
            _fullTelemetryEnabled = false;
            _airportFacilityRequestPending = false;
            _pendingAirportFacilities = null;
            _recoverStatusAfterFrame = false;
        }
        if (!_disposed) SetStatus(RecorderState.Waiting, "Recorder.DisconnectedRetry");
    }

    private void CloseSimConnect()
    {
        ResetControlContext();
        var connection = _simConnect;
        // Fence all later callbacks immediately, but retain the native handle
        // until every nested ReceiveMessage invocation has returned.
        _simConnect = null;
        _connectionSetupComplete = false;
        ApplicationVersionMajor = 0;
        StopSourceObservation();
        try
        {
            _simConnectOperations.Clear();
            _streamWatchdog.Reset(_subscriptionClock());
            _guardSubscription.Reset(true);
            _fullSubscription.Reset(false);
            ResetRunwayConnection();
        }
        finally
        {
            if (connection != null) _retiredConnections.Enqueue(connection);
            DisposeRetiredConnections();
        }
    }

    private void DisposeRetiredConnections()
    {
        if (_dispatchDepth != 0 || _closingConnections) return;
        _closingConnections = true;
        try
        {
            while (_retiredConnections.Count != 0)
            {
                var connection = _retiredConnections.Dequeue();
                try { _disposeConnection(connection); }
                catch
                {
                    // A dead connection must not prevent shutdown or the next attempt.
                }
            }
        }
        finally { _closingConnections = false; }
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

    private static string ValueOrUnknown(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
    }

    private void SetStatus(RecorderState state, string messageKey, params object[] messageArguments)
    {
        StatusChanged?.Invoke(this, new RecorderStatusEventArgs(state, messageKey, messageArguments));
    }

    private void RecoverStatusAfterFrame()
    {
        if (!_recoverStatusAfterFrame || _guardSubscription.RecoveryPending || _fullSubscription.RecoveryPending)
        {
            return;
        }

        _recoverStatusAfterFrame = false;
        if (_capture.IsRecording)
        {
            SetStatus(
                RecorderState.Recording,
                _capture.HasContact
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
            throw new ObjectDisposedException(nameof(SimConnectSource));
        }
    }

    private void RegisterGuardDefinition(SimConnect connection)
    {
        connection.AddToDataDefinition(Definitions.Guard, "SIMULATION TIME", "seconds", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        RememberOperation(connection, "Guard definition: SIMULATION TIME", false, definition: (uint)Definitions.Guard);
        connection.AddToDataDefinition(Definitions.Guard, "PLANE ALT ABOVE GROUND", "Feet", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        RememberOperation(connection, "Guard definition: PLANE ALT ABOVE GROUND", false, definition: (uint)Definitions.Guard);
        connection.AddToDataDefinition(Definitions.Guard, "SIM ON GROUND", "Bool", SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
        RememberOperation(connection, "Guard definition: SIM ON GROUND", false, definition: (uint)Definitions.Guard);
        connection.AddToDataDefinition(Definitions.Guard, "VELOCITY WORLD Y", "Feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        RememberOperation(connection, "Guard definition: VELOCITY WORLD Y", false, definition: (uint)Definitions.Guard);
        connection.AddToDataDefinition(Definitions.Guard, "MOTION SIMULATION", "Bool", SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
        RememberOperation(connection, "Guard definition: MOTION SIMULATION", false, definition: (uint)Definitions.Guard);
    }

    private void RegisterFrameDefinition(SimConnect connection)
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

    private void AddDouble(SimConnect connection, string name, string unit)
    {
        connection.AddToDataDefinition(Definitions.Frame, name, unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        RememberOperation(connection, "Frame definition: " + name, false, definition: (uint)Definitions.Frame);
    }

    private void AddInt(SimConnect connection, string name, string unit)
    {
        connection.AddToDataDefinition(Definitions.Frame, name, unit, SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
        RememberOperation(connection, "Frame definition: " + name, false, definition: (uint)Definitions.Frame);
    }

    private void AddMetadataString(SimConnect connection, string name)
    {
        connection.AddToDataDefinition(Definitions.Metadata, name, null, SIMCONNECT_DATATYPE.STRING256, 0, SimConnect.SIMCONNECT_UNUSED);
        RememberOperation(connection, "Metadata definition: " + name, true, definition: (uint)Definitions.Metadata);
    }

    private void RequestAircraftMetadata(SimConnect connection, bool once = false)
    {
        connection.RequestDataOnSimObject(
            once ? Requests.MetadataRefresh : Requests.Metadata,
            Definitions.Metadata,
            SimConnect.SIMCONNECT_OBJECT_ID_USER,
            once ? SIMCONNECT_PERIOD.ONCE : SIMCONNECT_PERIOD.SECOND,
            once ? SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT : SIMCONNECT_DATA_REQUEST_FLAG.CHANGED,
            0,
            0,
            0);
        RememberOperation(connection, "Request aircraft metadata", true, (uint)(once ? Requests.MetadataRefresh : Requests.Metadata), (uint)Definitions.Metadata);
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
            RememberOperation(connection, "Request airport facilities list", true, _airportFacilityRequestId);
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
            () => connection.MapClientEventToSimEvent(ControlEvents.AxisElevatorSet, "AXIS_ELEVATOR_SET"), (uint)ControlEvents.AxisElevatorSet);
        TrackOperation(
            connection,
            "subscribe AXIS_ELEVATOR_SET",
            () => connection.AddClientEventToNotificationGroup(
                NotificationGroups.ControlEvents,
                ControlEvents.AxisElevatorSet,
                false), (uint)ControlEvents.AxisElevatorSet);
        TrackOperation(
            connection,
            "set AXIS_ELEVATOR_SET notification priority",
            () => connection.SetNotificationGroupPriority(
                NotificationGroups.ControlEvents,
                SimConnect.SIMCONNECT_GROUP_PRIORITY_STANDARD));
    }

    private void TrySubscribeToFlowEvents(SimConnect connection)
    {
        try
        {
            connection.SubscribeToFlowEvent();
            RememberOperation(connection, "Subscribe to flow events", true);
        }
        catch
        {
            // Flow events are an MSFS 2024 addition. Older SimConnect servers can
            // still use the kinematic replay detector when an episode completes.
        }
    }

    private void TrackOperation(SimConnect connection, string name, Action action, uint? eventId = null)
    {
        action();
        RememberOperation(connection, name, true, eventId: eventId);
    }

    private void RememberOperation(SimConnect connection, string name, bool optional, uint? request = null, uint? definition = null, uint? eventId = null)
    {
        // Packet diagnostics must not change a successful subscription when
        // an older SDK cannot report its last packet identity.
        try { _simConnectOperations.Remember(connection.GetLastSentPacketID(), name, optional, request, definition, eventId); }
        catch { }
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
        SourceObservation,
    }

    // The managed SDK converts Enum request IDs to UInt32. Liveness probes
    // use the high-bit range, which must not become negative Int32 values.
    private enum Requests : uint
    {
        Guard,
        Frame,
        Metadata,
        Airports,
        SimulationState,
        MetadataRefresh,
        AircraftConfiguration,
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
