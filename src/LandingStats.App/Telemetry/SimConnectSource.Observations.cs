using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectSource
{
    private readonly DispatcherTimer _sourceObservationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Action<SimConnect> _registerSourceObservation;
    private readonly Func<SimConnect, uint, uint?> _requestSourceObservation;
    private readonly HashSet<uint> _sourceDefinitionPackets = new HashSet<uint>();
    private uint _nextSourceObservationRequest = 0x60000000, _sourceObservationRequest;
    private uint? _sourceObservationPacket;
    private bool _sourceDefinitionReady, _sourceDefinitionCreated;
    private double _sourceObservationDeadline, _sourceObservationRetry;

    private void StartSourceObservation()
    {
        _sourceObservationRetry = 0;
        SourceObservationReceived?.Invoke(new ReplaySourceObservation(SourceObservationAvailability.Pending, SourceObservationReason.AwaitingReply));
        _sourceObservationTimer.Start();
    }
    private void StopSourceObservation()
    {
        _sourceObservationTimer.Stop(); _sourceDefinitionReady = false; _sourceDefinitionCreated = false; _sourceDefinitionPackets.Clear();
        _sourceObservationRequest = 0; _sourceObservationPacket = null;
    }
    private void MaintainSourceObservation()
    {
        var connection = _simConnect;
        if (connection == null || !_connectionSetupComplete || _disposed || _dispatchDepth != 0) return;
        var now = _subscriptionClock();
        if (_sourceObservationRequest != 0)
        {
            if (now >= _sourceObservationDeadline) FailSourceObservation(SourceObservationReason.RequestTimeout);
            return;
        }
        if (now < _sourceObservationRetry) return;
        if (!_sourceDefinitionReady)
        {
            try { _registerSourceObservation(connection); _sourceDefinitionReady = true; }
            catch (Exception) { FailSourceObservation(SourceObservationReason.DefinitionRejected); return; }
        }
        try
        {
            if (_nextSourceObservationRequest >= 0x80000000) throw new InvalidOperationException("Optional observation request identity exhausted.");
            _sourceObservationRequest = _nextSourceObservationRequest++;
            _sourceObservationDeadline = now + 1;
            _sourceObservationPacket = _requestSourceObservation(connection, _sourceObservationRequest);
        }
        catch (Exception) { FailSourceObservation(SourceObservationReason.RequestRejected); }
    }
    private void FailSourceObservation(SourceObservationReason reason, uint exception = 0)
    {
        var request = _sourceObservationRequest;
        _sourceObservationRequest = 0; _sourceObservationPacket = null;
        // Optional variables never disable Guard/Full or force a recording gap.
        // Even unsupported definitions are retried on this owner, with bounded rate.
        if (reason == SourceObservationReason.DefinitionRejected) _sourceDefinitionReady = false;
        _sourceObservationRetry = _subscriptionClock() + (reason == SourceObservationReason.DefinitionRejected ? 30 : 5);
        SourceObservationReceived?.Invoke(new ReplaySourceObservation(SourceObservationAvailability.Unavailable, reason, requestId: request, sdkException: exception));
    }
    private bool HandleSourceObservationException(uint packet, uint exception)
    {
        if (_sourceDefinitionPackets.Contains(packet))
        { FailSourceObservation(SourceObservationReason.DefinitionRejected, exception); return true; }
        if (packet == _sourceObservationPacket)
        { FailSourceObservation(SourceObservationReason.RequestRejected, exception); return true; }
        // Late optional errors must not change the status of a healthy required stream.
        return _simConnectOperations.Name(packet)?.StartsWith("Optional USER source observation", StringComparison.Ordinal) == true;
    }
    private bool ReceiveSourceObservation(SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID < 0x40000000 || data.dwRequestID >= 0x80000000) return false;
        if (data.dwRequestID != _sourceObservationRequest) return true; // Timed-out or prior request.
        if (!(data.dwData[0] is SourceObservationData value) || !value.ValidFlags)
        { FailSourceObservation(SourceObservationReason.InvalidData); return true; }
        var request = _sourceObservationRequest; _sourceObservationRequest = 0; _sourceObservationPacket = null;
        SourceObservationReceived?.Invoke(new ReplaySourceObservation(SourceObservationAvailability.Available, SourceObservationReason.None,
            value.FreezePosition | (value.FreezeAltitude << 1) | (value.FreezeAttitude << 2), value.Slew, request,
            simulationTime: value.Time, latitude: value.Latitude, longitude: value.Longitude,
            altitudeFeet: value.Altitude, pitch: value.Pitch, bank: value.Bank, heading: value.Heading));
        return true;
    }
    private void RegisterSourceObservationNative(SimConnect connection)
    {
        _sourceDefinitionPackets.Clear();
        if (_sourceDefinitionCreated) { connection.ClearDataDefinition(Definitions.SourceObservation); RememberDefinition(); }
        _sourceDefinitionCreated = true;
        Add("SIMULATION TIME", "seconds"); Add("PLANE LATITUDE", "degrees"); Add("PLANE LONGITUDE", "degrees");
        Add("PLANE ALTITUDE", "feet"); Add("PLANE PITCH DEGREES", "degrees"); Add("PLANE BANK DEGREES", "degrees"); Add("PLANE HEADING DEGREES TRUE", "degrees");
        Add("IS LATITUDE LONGITUDE FREEZE ON", "bool", SIMCONNECT_DATATYPE.INT32);
        Add("IS ALTITUDE FREEZE ON", "bool", SIMCONNECT_DATATYPE.INT32);
        Add("IS ATTITUDE FREEZE ON", "bool", SIMCONNECT_DATATYPE.INT32); Add("IS SLEW ACTIVE", "bool", SIMCONNECT_DATATYPE.INT32);
        connection.RegisterDataDefineStruct<SourceObservationData>(Definitions.SourceObservation);
        void Add(string name, string unit, SIMCONNECT_DATATYPE type = SIMCONNECT_DATATYPE.FLOAT64)
        {
            connection.AddToDataDefinition(Definitions.SourceObservation, name, unit, type, 0, SimConnect.SIMCONNECT_UNUSED);
            RememberDefinition();
        }
        void RememberDefinition()
        {
            var packet = connection.GetLastSentPacketID(); _sourceDefinitionPackets.Add(packet);
            _simConnectOperations.Remember(packet, "Optional USER source observation definition", true, definition: (uint)Definitions.SourceObservation);
        }
    }
    private uint? RequestSourceObservationNative(SimConnect connection, uint request)
    {
        connection.RequestDataOnSimObject((Requests)request, Definitions.SourceObservation, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        var packet = connection.GetLastSentPacketID();
        _simConnectOperations.Remember(packet, "Optional USER source observation", true, request, (uint)Definitions.SourceObservation,
            period: (uint)SIMCONNECT_PERIOD.ONCE, flags: (uint)SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT);
        return packet;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SourceObservationData
    {
        public double Time, Latitude, Longitude, Altitude, Pitch, Bank, Heading;
        public int FreezePosition, FreezeAltitude, FreezeAttitude, Slew;
        public bool ValidFlags => (FreezePosition == 0 || FreezePosition == 1) && (FreezeAltitude == 0 || FreezeAltitude == 1) &&
            (FreezeAttitude == 0 || FreezeAttitude == 1) && (Slew == 0 || Slew == 1);
    }
}
