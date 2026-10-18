using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectSource
{
    private readonly Dictionary<uint, RunwayFacilityRequest> _runwayRequests = new Dictionary<uint, RunwayFacilityRequest>();
    private readonly Dictionary<uint, uint> _runwayPackets = new Dictionary<uint, uint>();
    private readonly Dictionary<string, IReadOnlyList<RunwayFacility>> _runways =
        new Dictionary<string, IReadOnlyList<RunwayFacility>>(StringComparer.OrdinalIgnoreCase);
    private uint _nextRunwayRequestId = 0x40000000;
    private bool _runwayDefinitionAttempted;
    private bool _runwayDefinitionReady;
    private readonly bool[] _thresholdDefinitionAttempted = new bool[2];
    private readonly bool[] _thresholdDefinitionReady = new bool[2];
    private readonly bool[] _papiDefinitionAttempted = new bool[4];
    private readonly bool[] _papiDefinitionReady = new bool[4];
    private readonly Dictionary<string, IReadOnlyList<RunwayFacility>?[]> _papiReplies =
        new Dictionary<string, IReadOnlyList<RunwayFacility>?[]>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, RunwayIlsRequest> _ilsRequests = new Dictionary<uint, RunwayIlsRequest>();
    private bool _ilsDefinitionAttempted, _ilsDefinitionReady;
    private double _runwayLookupLatitude, _runwayLookupLongitude, _runwayLookupExpires;
    private string[] _runwayBatchKeys = Array.Empty<string>();
    private bool _runwayBatchActive, _runwayBatchFailed;
    private bool _runwayLookupStarted;
    private bool _runwayAwaitingAirportFacilities;
    private bool _runwayFreshAirportsReceived;
    private IReadOnlyList<AirportFacility> _stageAirports = Array.Empty<AirportFacility>();
    private int _runwayDiagnosticEntries;

    private void ResetNativeQuery()
    {
        _nativeQueryEpoch++;
        SceneryRevision++;
        // Keep definitions for this connection, but never reuse scenery data
        // from another episode or reuse the preliminary response for the final query.
        foreach (var id in _runwayRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        foreach (var id in _ilsRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        _runways.Clear();
        _papiReplies.Clear();
        _runwayBatchKeys = Array.Empty<string>();
        _runwayBatchActive = _runwayBatchFailed = _runwayLookupStarted = false;
        _runwayAwaitingAirportFacilities = false;
        _runwayFreshAirportsReceived = false;
        _stageAirports = Array.Empty<AirportFacility>();
        _airportFacilityRequestPending = false;
        _pendingAirportFacilities = null;
        _airportFacilityRequestPacket = null;
        _runwayLookupExpires = 0;
        _runwayDiagnosticEntries = 0;
    }

    private void RequestRunwaysAt(double latitude, double longitude)
    {
        if (DeferNativeEffect(() => RequestRunwaysAt(latitude, longitude), scenery: true)) return;
        if (!RunwayNumber.IsFinite(latitude) || !RunwayNumber.IsFinite(longitude) ||
            Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
        {
            _scenery.FinishRunwayLookupStage(Array.Empty<RunwayFacility>());
            return;
        }
        _runwayLookupLatitude = latitude;
        _runwayLookupLongitude = longitude;
        _runwayLookupExpires = _hostClock.Elapsed.TotalSeconds + 60;
        foreach (var id in _runwayRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        foreach (var id in _ilsRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        _runwayBatchActive = false;
        // A nearby persisted ARP says nothing about whether this airport/scenery
        // is missing from that cache. Refresh every landing before ranking candidates.
        _runwayAwaitingAirportFacilities = true;
        _scenery.DecisionJournal?.Record(_scenery.DecisionTime, "runway.episode", _scenery.IsPreliminary ? "preliminary_requested" : "final_requested",
            _scenery.IsPreliminary ? "Request this approach's scenery while still in flight; retain it if the post-contact lookup is unavailable."
                : "Request fresh scenery after capture using the actual contact position; this result takes precedence over approach intent.",
            new Dictionary<string, double> { ["latitude"] = latitude, ["longitude"] = longitude });
        _scenery.DecisionJournal?.Record(_scenery.DecisionTime, "runway.airports", "fresh_list_wait",
            "Refresh simulator airport facilities before this episode's runway candidate selection.");
        _airportFacilityRequestPending = false; // Supersede older requests; IDs reject their late replies.
        try
        {
            if (_simConnect == null || _disposed) { CompleteAirportLookupFailure("Simulator is disconnected."); return; }
            RequestAirportFacilities(_simConnect);
        }
        catch (Exception exception)
        {
            CompleteAirportLookupFailure("Fresh airport-list request failed: " + exception.GetType().Name);
        }
    }

    private void CompleteAirportLookupFailure(string reason)
    {
        _airportFacilityRequestPending = false;
        _pendingAirportFacilities = null;
        _airportFacilityRequestPacket = null;
        if (!_runwayAwaitingAirportFacilities) return;
        _runwayAwaitingAirportFacilities = false;
        _runwayLookupStarted = true;
        _scenery.DecisionJournal?.Record(_scenery.DecisionTime, "runway.airports", "fresh_list_unavailable", reason);
        _scenery.FinishRunwayLookupStage(Array.Empty<RunwayFacility>());
    }

    private RunwayFacility[] CompletedBaseRunways() => _runwayBatchKeys.Where(key => _runways.ContainsKey(key))
        .SelectMany(key => _runways[key]).Select(value => value.Snapshot()).ToArray();

    private bool BaseRunwayCandidatesComplete() => !_runwayAwaitingAirportFacilities && !_runwayBatchFailed &&
        (_runwayBatchKeys.Length > 0 || _runwayFreshAirportsReceived) && _runwayBatchKeys.All(key => _runways.ContainsKey(key));

    private void CaptureFreshEpisodeAirports()
    {
        if (!_runwayAwaitingAirportFacilities) return;
        SceneryRevision++;
        _stageAirports = _airportFacilities.Values.Select(value => new AirportFacility
        {
            Ident = value.Ident, Region = value.Region, LatitudeDegrees = value.LatitudeDegrees,
            LongitudeDegrees = value.LongitudeDegrees, AltitudeMeters = value.AltitudeMeters
        }).ToArray();
        _runwayFreshAirportsReceived = true;
    }

    private void RequestNearbyRunways()
    {
        if (DeferNativeEffect(RequestNearbyRunways, scenery: true)) return;
        var connection = _simConnect;
        if (connection == null || _disposed || _hostClock.Elapsed.TotalSeconds >= _runwayLookupExpires) return;
        ExpireRunwayRequests();
        if (_runwayAwaitingAirportFacilities || _runwayLookupStarted || _runwayRequests.Count > 0 || _ilsRequests.Count > 0) return;
        var candidates = _airportFacilities.Values
            .Select(airport => new { Airport = airport, Distance = GeoDistance.GreatCircleNauticalMiles(
                _runwayLookupLatitude, _runwayLookupLongitude, airport.LatitudeDegrees, airport.LongitudeDegrees) })
            .Where(item => item.Distance <= 12).OrderBy(item => item.Distance).Take(3).ToArray();
        _runwayBatchKeys = candidates.Select(item => item.Airport.Key).ToArray();
        _runwayBatchActive = candidates.Length > 0;
        _runwayLookupStarted = _runwayBatchActive;
        _runwayBatchFailed = false;
        RecordRunwayDiagnostic("airport_candidates", "Fresh airport list ranked by distance to this stage's recorded position.",
            new Dictionary<string, double> { ["available_airports"] = _airportFacilities.Count,
                ["requested_airports"] = candidates.Length, ["latitude"] = _runwayLookupLatitude,
                ["longitude"] = _runwayLookupLongitude });
        if (candidates.Length == 0)
        {
            _runwayLookupStarted = true;
            _scenery.FinishRunwayLookupStage(Array.Empty<RunwayFacility>());
            return;
        }
        foreach (var candidate in candidates)
        {
            var airport = candidate.Airport;
            RecordRunwayDiagnostic("airport_candidate", airport.Ident + " / " + airport.Region,
                new Dictionary<string, double> { ["distance_nm"] = candidate.Distance,
                    ["latitude"] = airport.LatitudeDegrees, ["longitude"] = airport.LongitudeDegrees,
                    ["altitude_m"] = airport.AltitudeMeters });
            if (_runways.ContainsKey(airport.Key) || _runwayRequests.Values.Any(request => request.Airport.Key == airport.Key)) continue;
            if (_runwayRequests.Count >= 3 || !PrepareRunwayDefinition(connection))
            {
                RecordRunwayDiagnostic("base_request_unavailable", airport.Ident + ": base definition unavailable or request capacity reached.");
                _runwayBatchFailed = true; break;
            }
            // Request IDs are not reused after timeout or reconnect; late replies cannot attach to a new request.
            if (_nextRunwayRequestId == int.MaxValue) { _runwayBatchFailed = true; break; }
            var id = _nextRunwayRequestId++;
            _runwayRequests.Add(id, new RunwayFacilityRequest(airport, _hostClock.Elapsed.TotalSeconds + 10));
            RecordRunwayRequest(id, _runwayRequests[id], "request_started", "Base runway geometry requested.");
            try
            {
                connection.RequestFacilityData(Definitions.Runways, (Requests)id, airport.Ident, airport.Region);
                RememberOperation(connection, "Request runway facility", true, id, (uint)Definitions.Runways);
                _runwayPackets[connection.GetLastSentPacketID()] = id;
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException || exception is ArgumentException)
            {
                RecordRunwayRequest(id, _runwayRequests[id], "request_failed", exception.GetType().Name);
                _runwayRequests.Remove(id);
                _runwayBatchFailed = true;
            }
        }
        TryPublishRunwayBatch();
    }

    private bool PrepareRunwayDefinition(SimConnect connection)
    {
        if (_runwayDefinitionAttempted) return _runwayDefinitionReady;
        _runwayDefinitionAttempted = true;
        try
        {
            foreach (var field in new[] { "OPEN AIRPORT", "N_RUNWAYS", "OPEN RUNWAY", "LATITUDE", "LONGITUDE", "ALTITUDE",
                         "HEADING", "LENGTH", "WIDTH", "PRIMARY_NUMBER", "PRIMARY_DESIGNATOR", "SECONDARY_NUMBER", "SECONDARY_DESIGNATOR", "SURFACE",
                         "PRIMARY_ILS_ICAO", "PRIMARY_ILS_REGION", "SECONDARY_ILS_ICAO", "SECONDARY_ILS_REGION",
                         "CLOSE RUNWAY", "CLOSE AIRPORT" })
            {
                connection.AddToFacilityDefinition(Definitions.Runways, field);
                RememberOperation(connection, "Runway definition: " + field, true, definition: (uint)Definitions.Runways);
                _runwayPackets[connection.GetLastSentPacketID()] = 0;
            }
            connection.RegisterFacilityDataDefineStruct<RunwayAirportData>(SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT);
            connection.RegisterFacilityDataDefineStruct<RunwayFacilityData>(SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY);
            _runwayDefinitionReady = true;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException || exception is ArgumentException)
        {
            // Optional scene metadata must never interrupt flight capture or force a reconnect.
            _runwayDefinitionReady = false;
            RecordRunwayDiagnostic("base_definition_failed", exception.GetType().Name);
        }
        return _runwayDefinitionReady;
    }

    private void OnRecvRunwayFacilityData(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (!ReferenceEquals(sender, _simConnect)) return;
        ExpireRunwayRequests();
        if (_ilsRequests.TryGetValue(data.UserRequestId, out var ils))
        {
            if ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type == SIMCONNECT_FACILITY_DATA_TYPE.VOR &&
                data.Data != null && data.Data.Length == 1 && data.Data[0] is RunwayGlideSlopeData slope)
                ils.Add(data.ParentUniqueRequestId, data.ItemIndex, data.ListSize, slope);
            else ils.Invalidate();
            return;
        }
        if (!_runwayRequests.TryGetValue(data.UserRequestId, out var request)) return;
        if (data.Data == null || data.Data.Length != 1)
        {
            RecordRunwayRequest(data.UserRequestId, request, "request_invalid_packet", "Facility packet has an unexpected payload count.");
            request.Invalidate(); return;
        }
        if ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type == SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT && data.Data[0] is RunwayAirportData airport)
        {
            request.AddAirport(data.UniqueRequestId, airport.RunwayCount);
            RecordRunwayRequest(data.UserRequestId, request, "airport_header_received", "Simulator's declared runway count.", airport.RunwayCount);
        }
        else if ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type == SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY && data.Data[0] is RunwayFacilityData runway)
            request.AddRunway(data.ItemIndex, data.ParentUniqueRequestId, data.ListSize, runway.ToFacility(request.Airport), data.UniqueRequestId, runway);
        else if ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type == SIMCONNECT_FACILITY_DATA_TYPE.PAVEMENT && data.Data[0] is RunwayThresholdData threshold)
            request.AddThreshold(data.ParentUniqueRequestId, data.ItemIndex, data.ListSize, threshold);
        else if ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type == SIMCONNECT_FACILITY_DATA_TYPE.VASI && data.Data[0] is RunwayVasiData vasi)
            request.AddVasi(data.ParentUniqueRequestId, data.ItemIndex, data.ListSize, vasi);
        else
        {
            RecordRunwayRequest(data.UserRequestId, request, "request_invalid_packet", "Unexpected facility native type " + data.Type + ".");
            request.Invalidate();
        }
    }

    private void OnRecvRunwayFacilityDataEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (ReferenceEquals(sender, _simConnect)) SceneryRevision++;
        if (!ReferenceEquals(sender, _simConnect)) return;
        ExpireRunwayRequests();
        if (_ilsRequests.TryGetValue(data.RequestId, out var ils))
        {
            RemoveRunwayRequest(data.RequestId);
            if (ils.TryComplete(out var slope))
            {
                RecordRunwayDiagnostic("ils_request_completed", ils.Ident + " / " + ils.Region,
                    new Dictionary<string, double> { ["request_id"] = data.RequestId });
                foreach (var target in ils.Targets)
                    if (target.Item2) target.Item1.PrimaryIlsGlideSlope = slope;
                    else target.Item1.SecondaryIlsGlideSlope = slope;
            }
            else RecordRunwayDiagnostic("ils_request_invalid", ils.Ident + " / " + ils.Region,
                new Dictionary<string, double> { ["request_id"] = data.RequestId });
            TryPublishRunwayBatch();
            return;
        }
        if (!_runwayRequests.TryGetValue(data.RequestId, out var request)) return;
        RemoveRunwayRequest(data.RequestId);
        if (request.TryComplete(out var runways))
        {
            RecordRunwayRequest(data.RequestId, request, "request_completed", "Validated complete response.", runways.Count);
            if (request.PrimaryThreshold.HasValue) MergeRunwayThresholds(request.Airport.Key, runways, request.PrimaryThreshold.Value);
            else if (request.PapiSide.HasValue) MergeRunwayPapi(request.Airport.Key, runways, request.PapiSide.Value);
            else
            {
                _runways[request.Airport.Key] = runways;
                foreach (var runway in runways) RecordRunwayGeometry(runway);
                if (runways.Count > 0)
                {
                    RequestRunwayThresholds(sender, request.Airport);
                    RequestRunwayPapi(sender, request.Airport);
                    RequestRunwayIls(sender, request.IlsTargets);
                }
            }
        }
        else
        {
            RecordRunwayRequest(data.RequestId, request, "request_invalid", "Missing, malformed, or inconsistent facility response.");
            if (!request.IsOptional) _runwayBatchFailed = true;
        }
        TryPublishRunwayBatch();
    }

    private void RequestRunwayThresholds(SimConnect connection, AirportFacility airport)
    {
        if (DeferNativeEffect(() => RequestRunwayThresholds(connection, airport), scenery: true)) return;
        // Separate requests disambiguate the two PAVEMENT children: their native
        // type and ItemIndex are identical. Join by parent ID, never reply order.
        for (var side = 0; side < 2; side++)
        {
            var definition = side == 0 ? Definitions.RunwayPrimaryThresholds : Definitions.RunwaySecondaryThresholds;
            if (!_thresholdDefinitionAttempted[side])
            {
                _thresholdDefinitionAttempted[side] = true;
                try
                {
                    var child = side == 0 ? "PRIMARY_THRESHOLD" : "SECONDARY_THRESHOLD";
                    foreach (var field in new[] { "OPEN AIRPORT", "N_RUNWAYS", "OPEN RUNWAY", "LATITUDE", "LONGITUDE", "ALTITUDE",
                        "HEADING", "LENGTH", "WIDTH", "PRIMARY_NUMBER", "PRIMARY_DESIGNATOR", "SECONDARY_NUMBER", "SECONDARY_DESIGNATOR", "SURFACE",
                        "PRIMARY_ILS_ICAO", "PRIMARY_ILS_REGION", "SECONDARY_ILS_ICAO", "SECONDARY_ILS_REGION",
                        "OPEN " + child, "LENGTH", "WIDTH", "ENABLE", "CLOSE " + child, "CLOSE RUNWAY", "CLOSE AIRPORT" })
                    {
                        connection.AddToFacilityDefinition(definition, field);
                        RememberOperation(connection, "Threshold definition: " + field, true, definition: (uint)definition);
                        _runwayPackets[connection.GetLastSentPacketID()] = uint.MaxValue - (uint)side;
                    }
                    connection.RegisterFacilityDataDefineStruct<RunwayThresholdData>(SIMCONNECT_FACILITY_DATA_TYPE.PAVEMENT);
                    _thresholdDefinitionReady[side] = true;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
                { RecordRunwayDiagnostic("threshold_definition_failed", ex.GetType().Name); }
            }
            if (!_thresholdDefinitionReady[side] || _nextRunwayRequestId == int.MaxValue) continue;
            var id = _nextRunwayRequestId++;
            _runwayRequests.Add(id, new RunwayFacilityRequest(airport, _hostClock.Elapsed.TotalSeconds + 10, side == 0));
            RecordRunwayRequest(id, _runwayRequests[id], "request_started", "Threshold metadata requested.");
            try
            {
                connection.RequestFacilityData(definition, (Requests)id, airport.Ident, airport.Region);
                RememberOperation(connection, "Request threshold facility", true, id, (uint)definition);
                _runwayPackets[connection.GetLastSentPacketID()] = id;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
            { RecordRunwayRequest(id, _runwayRequests[id], "request_failed", ex.GetType().Name); RemoveRunwayRequest(id); }
        }
    }

    private void MergeRunwayThresholds(string key, IReadOnlyList<RunwayFacility> incoming, bool primary)
    {
        if (!_runways.TryGetValue(key, out var cached) || incoming.Count != cached.Count) return;
        // A scenery change/partial reply must not attach offsets to another runway.
        for (var i = 0; i < cached.Count; i++)
        {
            var a = cached[i]; var b = incoming[i];
            if (a.PrimaryName != b.PrimaryName || a.SecondaryName != b.SecondaryName ||
                a.LatitudeDegrees != b.LatitudeDegrees || a.LongitudeDegrees != b.LongitudeDegrees ||
                a.AltitudeMeters != b.AltitudeMeters || a.HeadingTrueDegrees != b.HeadingTrueDegrees ||
                a.LengthMeters != b.LengthMeters || a.WidthMeters != b.WidthMeters || a.SurfaceType != b.SurfaceType) return;
            var p = primary ? b.PrimaryThresholdOffsetMeters : a.PrimaryThresholdOffsetMeters;
            var s = primary ? a.SecondaryThresholdOffsetMeters : b.SecondaryThresholdOffsetMeters;
            if ((p ?? 0) + (s ?? 0) >= a.LengthMeters) return;
        }
        for (var i = 0; i < cached.Count; i++)
            if (primary) cached[i].PrimaryThresholdOffsetMeters = incoming[i].PrimaryThresholdOffsetMeters;
            else cached[i].SecondaryThresholdOffsetMeters = incoming[i].SecondaryThresholdOffsetMeters;
    }

    private void RequestRunwayPapi(SimConnect connection, AirportFacility airport)
    {
        if (DeferNativeEffect(() => RequestRunwayPapi(connection, airport), scenery: true)) return;
        var children = new[] { "PRIMARY_LEFT_VASI", "PRIMARY_RIGHT_VASI", "SECONDARY_LEFT_VASI", "SECONDARY_RIGHT_VASI" };
        for (var side = 0; side < 4; side++)
        {
            var definition = (Definitions)((int)Definitions.RunwayPrimaryLeftPapi + side);
            if (!_papiDefinitionAttempted[side])
            {
                _papiDefinitionAttempted[side] = true;
                try
                {
                    var child = children[side];
                    foreach (var field in new[] { "OPEN AIRPORT", "N_RUNWAYS", "OPEN RUNWAY", "LATITUDE", "LONGITUDE", "ALTITUDE",
                        "HEADING", "LENGTH", "WIDTH", "PRIMARY_NUMBER", "PRIMARY_DESIGNATOR", "SECONDARY_NUMBER", "SECONDARY_DESIGNATOR", "SURFACE",
                        "PRIMARY_ILS_ICAO", "PRIMARY_ILS_REGION", "SECONDARY_ILS_ICAO", "SECONDARY_ILS_REGION",
                        "OPEN " + child, "TYPE", "BIAS_X", "BIAS_Z", "SPACING", "ANGLE", "CLOSE " + child, "CLOSE RUNWAY", "CLOSE AIRPORT" })
                    {
                        connection.AddToFacilityDefinition(definition, field);
                        RememberOperation(connection, "VASI definition: " + field, true, definition: (uint)definition);
                        _runwayPackets[connection.GetLastSentPacketID()] = uint.MaxValue - 2 - (uint)side;
                    }
                    connection.RegisterFacilityDataDefineStruct<RunwayVasiData>(SIMCONNECT_FACILITY_DATA_TYPE.VASI);
                    _papiDefinitionReady[side] = true;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
                { RecordRunwayDiagnostic("papi_definition_failed", ex.GetType().Name); }
            }
            if (!_papiDefinitionReady[side] || _nextRunwayRequestId == int.MaxValue) continue;
            var id = _nextRunwayRequestId++;
            _runwayRequests.Add(id, new RunwayFacilityRequest(airport, _hostClock.Elapsed.TotalSeconds + 10, papiSide: side));
            RecordRunwayRequest(id, _runwayRequests[id], "request_started", "PAPI metadata requested.");
            try
            {
                connection.RequestFacilityData(definition, (Requests)id, airport.Ident, airport.Region);
                RememberOperation(connection, "Request VASI facility", true, id, (uint)definition);
                _runwayPackets[connection.GetLastSentPacketID()] = id;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
            { RecordRunwayRequest(id, _runwayRequests[id], "request_failed", ex.GetType().Name); RemoveRunwayRequest(id); }
        }
    }

    private static bool SameRunwayGeometry(RunwayFacility a, RunwayFacility b) =>
        a.PrimaryName == b.PrimaryName && a.SecondaryName == b.SecondaryName &&
        a.LatitudeDegrees == b.LatitudeDegrees && a.LongitudeDegrees == b.LongitudeDegrees &&
        a.AltitudeMeters == b.AltitudeMeters && a.HeadingTrueDegrees == b.HeadingTrueDegrees &&
        a.LengthMeters == b.LengthMeters && a.WidthMeters == b.WidthMeters && a.SurfaceType == b.SurfaceType;

    private void RequestRunwayIls(SimConnect connection, IReadOnlyList<Tuple<RunwayFacility, bool, string, string>> targets)
    {
        if (DeferNativeEffect(() => RequestRunwayIls(connection, targets), scenery: true)) return;
        if (targets.Count == 0) return;
        if (!_ilsDefinitionAttempted)
        {
            _ilsDefinitionAttempted = true;
            try
            {
                foreach (var field in new[] { "OPEN VOR", "GS_LATITUDE", "GS_LONGITUDE", "GS_ALTITUDE", "HAS_GLIDE_SLOPE", "GLIDE_SLOPE", "CLOSE VOR" })
                {
                    connection.AddToFacilityDefinition(Definitions.RunwayIlsGlideSlope, field);
                    RememberOperation(connection, "ILS definition: " + field, true, definition: (uint)Definitions.RunwayIlsGlideSlope);
                    _runwayPackets[connection.GetLastSentPacketID()] = uint.MaxValue - 6;
                }
                connection.RegisterFacilityDataDefineStruct<RunwayGlideSlopeData>(SIMCONNECT_FACILITY_DATA_TYPE.VOR);
                _ilsDefinitionReady = true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
            { RecordRunwayDiagnostic("ils_definition_failed", ex.GetType().Name); }
        }
        if (!_ilsDefinitionReady) return;
        foreach (var target in targets)
        {
            var pending = _ilsRequests.Values.FirstOrDefault(r => r.Ident == target.Item3 && r.Region == target.Item4);
            if (pending != null) { pending.Targets.Add(Tuple.Create(target.Item1, target.Item2)); continue; }
            if (_nextRunwayRequestId == int.MaxValue || _ilsRequests.Count >= 768) break; // 3 airports * 128 runways * 2 ends
            var id = _nextRunwayRequestId++;
            var request = new RunwayIlsRequest(target.Item3, target.Item4, _hostClock.Elapsed.TotalSeconds + 10);
            request.Targets.Add(Tuple.Create(target.Item1, target.Item2));
            _ilsRequests.Add(id, request);
            RecordRunwayDiagnostic("ils_request_started", request.Ident + " / " + request.Region,
                new Dictionary<string, double> { ["request_id"] = id });
            try
            {
                connection.RequestFacilityData(Definitions.RunwayIlsGlideSlope, (Requests)id, request.Ident, request.Region);
                RememberOperation(connection, "Request ILS facility", true, id, (uint)Definitions.RunwayIlsGlideSlope);
                _runwayPackets[connection.GetLastSentPacketID()] = id;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
            {
                RecordRunwayDiagnostic("ils_request_failed", request.Ident + " / " + request.Region + ": " + ex.GetType().Name,
                    new Dictionary<string, double> { ["request_id"] = id });
                RemoveRunwayRequest(id);
            }
        }
    }

    private void MergeRunwayPapi(string key, IReadOnlyList<RunwayFacility> incoming, int side)
    {
        if (side < 0 || side > 3 || !_runways.TryGetValue(key, out var cached) || incoming.Count != cached.Count ||
            cached.Where((runway, i) => !SameRunwayGeometry(runway, incoming[i]) || !incoming[i].HasValidGuidanceMetadata).Any()) return;
        if (!_papiReplies.TryGetValue(key, out var replies))
            _papiReplies[key] = replies = new IReadOnlyList<RunwayFacility>?[4];
        replies[side] = incoming;
        var first = side / 2 * 2;
        var left = replies[first]; var right = replies[first + 1];
        if (left == null || right == null) return;
        var primary = first == 0;
        for (var i = 0; i < cached.Count; i++)
        {
            var a = primary ? left[i].PrimaryPapi : left[i].SecondaryPapi;
            var b = primary ? right[i].PrimaryPapi : right[i].SecondaryPapi;
            var consistent = a == null || b == null || (a.StationFromEndMeters == b.StationFromEndMeters && a.AngleDegrees == b.AngleDegrees);
            if (primary) { cached[i].PrimaryPapi = consistent ? a ?? b : null; cached[i].PrimaryPapiQueried = consistent; }
            else { cached[i].SecondaryPapi = consistent ? a ?? b : null; cached[i].SecondaryPapiQueried = consistent; }
        }
    }

    private bool HandleRunwayException(SimConnect sender, uint packetId)
    {
        if (!ReferenceEquals(sender, _simConnect) || !_runwayPackets.TryGetValue(packetId, out var requestId)) return false;
        if (_runwayRequests.TryGetValue(requestId, out var failedRequest))
            RecordRunwayRequest(requestId, failedRequest, "request_sdk_exception", "Simulator rejected request packet " + packetId + ".");
        else RecordRunwayDiagnostic("definition_or_ils_sdk_exception", "Simulator rejected facility packet.",
            new Dictionary<string, double> { ["packet_id"] = packetId, ["request_id"] = requestId });
        if (requestId >= uint.MaxValue - 1)
        {
            var side = (int)(uint.MaxValue - requestId);
            _thresholdDefinitionReady[side] = false;
            foreach (var id in _runwayRequests.Where(p => p.Value.PrimaryThreshold == (side == 0)).Select(p => p.Key).ToArray()) RemoveRunwayRequest(id);
        }
        else if (requestId >= uint.MaxValue - 5)
        {
            var side = (int)(uint.MaxValue - 2 - requestId);
            _papiDefinitionReady[side] = false;
            foreach (var id in _runwayRequests.Where(p => p.Value.PapiSide == side).Select(p => p.Key).ToArray()) RemoveRunwayRequest(id);
        }
        else if (requestId == uint.MaxValue - 6)
        {
            _ilsDefinitionReady = false;
            foreach (var id in _ilsRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        }
        else if (_ilsRequests.ContainsKey(requestId)) RemoveRunwayRequest(requestId);
        else if (_runwayRequests.TryGetValue(requestId, out var optional) && optional.IsOptional)
            RemoveRunwayRequest(requestId);
        else
        {
            _runwayBatchFailed = true;
            if (requestId == 0)
            {
                _runwayDefinitionReady = false;
                _runwayRequests.Clear();
            }
            else RemoveRunwayRequest(requestId);
        }
        TryPublishRunwayBatch();
        return true;
    }

    private void ExpireRunwayRequests()
    {
        if (_airportFacilityRequestPending && _hostClock.Elapsed.TotalSeconds >= _airportFacilityRequestDeadline)
            CompleteAirportLookupFailure("Fresh airport-list lookup timed out; cached facilities are not substituted.");
        foreach (var id in _runwayRequests.Where(item => item.Value.DeadlineSeconds <= _hostClock.Elapsed.TotalSeconds).Select(item => item.Key).ToArray())
        {
            RecordRunwayRequest(id, _runwayRequests[id], "request_timeout", "Facility response deadline elapsed.");
            if (!_runwayRequests[id].IsOptional) _runwayBatchFailed = true;
            RemoveRunwayRequest(id);
        }
        foreach (var id in _ilsRequests.Where(item => item.Value.DeadlineSeconds <= _hostClock.Elapsed.TotalSeconds).Select(item => item.Key).ToArray())
        {
            RecordRunwayDiagnostic("ils_request_timeout", _ilsRequests[id].Ident + " / " + _ilsRequests[id].Region,
                new Dictionary<string, double> { ["request_id"] = id });
            RemoveRunwayRequest(id);
        }
        TryPublishRunwayBatch();
    }

    private void TryPublishRunwayBatch()
    {
        if (!_runwayBatchActive || _runwayRequests.Count != 0 || _ilsRequests.Count != 0 || _nativeEffects.Count != 0) return;
        SceneryRevision++;
        _runwayBatchActive = false;
        var completed = CompletedBaseRunways();
        _scenery.FinishRunwayLookupStage(completed);
    }

    private void RecordRunwayRequest(uint id, RunwayFacilityRequest request, string code, string reason, int? runwayCount = null)
    {
        var kind = request.PrimaryThreshold.HasValue ? "threshold" : request.PapiSide.HasValue ? "papi" : "base";
        var details = new Dictionary<string, double> { ["request_id"] = id, ["optional"] = request.IsOptional ? 1 : 0 };
        if (runwayCount.HasValue) details["runways"] = runwayCount.Value;
        if (request.PrimaryThreshold.HasValue) details["primary"] = request.PrimaryThreshold.Value ? 1 : 0;
        if (request.PapiSide.HasValue) details["papi_side"] = request.PapiSide.Value;
        RecordRunwayDiagnostic(code, request.Airport.Ident + " / " + request.Airport.Region + " / " + kind + ": " + reason, details);
    }

    private void RecordRunwayGeometry(RunwayFacility runway) => RecordRunwayDiagnostic("base_geometry",
        runway.AirportIdent + " / " + runway.AirportRegion + " / " + runway.PrimaryName + "-" + runway.SecondaryName,
        new Dictionary<string, double> { ["latitude"] = runway.LatitudeDegrees, ["longitude"] = runway.LongitudeDegrees,
            ["altitude_m"] = runway.AltitudeMeters, ["heading_true_deg"] = runway.HeadingTrueDegrees,
            ["length_m"] = runway.LengthMeters, ["width_m"] = runway.WidthMeters,
            ["surface"] = runway.SurfaceType ?? -1 });

    private void RecordRunwayDiagnostic(string code, string reason, Dictionary<string, double>? details = null)
    {
        // At most 256 detailed entries per lookup stage, independently of the
        // journal's global bound. A malformed scenery cannot fill the journal
        // with thousands of optional facility messages and hide flight events.
        if (_scenery.DecisionJournal == null || _runwayDiagnosticEntries > 256) return;
        if (_runwayDiagnosticEntries++ == 256)
        {
            _scenery.DecisionJournal.Record(_scenery.DecisionTime, "runway.requests", "diagnostics_limited",
                "Detailed facility diagnostics reached the per-stage bound; stage outcome is still recorded.");
            return;
        }
        details ??= new Dictionary<string, double>();
        details["preliminary"] = _scenery.IsPreliminary ? 1 : 0;
        details["episode_id"] = _scenery.EpisodeId;
        _scenery.DecisionJournal.Record(_scenery.DecisionTime, "runway.requests", code, reason, details);
    }

    private void RemoveRunwayRequest(uint id)
    {
        _runwayRequests.Remove(id);
        _ilsRequests.Remove(id);
        foreach (var packet in _runwayPackets.Where(item => item.Value == id).Select(item => item.Key).ToArray()) _runwayPackets.Remove(packet);
    }

    private void ResetRunwayConnection()
    {
        _scenery.ResetConnection();
        _runwayRequests.Clear();
        _ilsRequests.Clear();
        _runwayPackets.Clear();
        _runways.Clear();
        _papiReplies.Clear();
        _runwayDefinitionAttempted = false;
        _runwayDefinitionReady = false;
        _ilsDefinitionAttempted = _ilsDefinitionReady = false;
        Array.Clear(_thresholdDefinitionAttempted, 0, 2);
        Array.Clear(_thresholdDefinitionReady, 0, 2);
        Array.Clear(_papiDefinitionAttempted, 0, 4);
        Array.Clear(_papiDefinitionReady, 0, 4);
        _runwayLookupExpires = 0;
        _runwayLookupStarted = false;
        _runwayAwaitingAirportFacilities = false;
        _runwayBatchActive = false;
        _runwayBatchFailed = false;
        _runwayBatchKeys = Array.Empty<string>();
        _stageAirports = Array.Empty<AirportFacility>();
    }
    private void CancelNativeQuery(string reason)
    {
        foreach (var item in _runwayRequests) RecordRunwayRequest(item.Key, item.Value, "request_cancelled", reason);
        foreach (var id in _runwayRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        foreach (var id in _ilsRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        _runwayBatchActive = false;
        _airportFacilityRequestPending = false;
        _pendingAirportFacilities = null;
        _airportFacilityRequestPacket = null;
        _runwayAwaitingAirportFacilities = false;
        _runwayLookupStarted = true;
    }

}
