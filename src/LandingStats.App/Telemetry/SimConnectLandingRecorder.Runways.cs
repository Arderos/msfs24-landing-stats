using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectLandingRecorder
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
    private long _runwayEpisodeId;
    private IReadOnlyList<RunwayFacility> _publishedRunways = Array.Empty<RunwayFacility>();

    public event EventHandler<RunwayFacilitiesEventArgs>? RunwayFacilitiesUpdated;
    public IReadOnlyList<RunwayFacility> RunwayFacilities => _publishedRunways;
    public IReadOnlyList<RunwayFacility> GetRunwayFacilities(long episodeId) =>
        episodeId == _runwayEpisodeId ? _publishedRunways : Array.Empty<RunwayFacility>();

    private void BeginRunwayEpisode(long episodeId)
    {
        // Keep definitions for this connection, but never reuse scenery data
        // from a previous landing (including successful replies without thresholds).
        foreach (var id in _runwayRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        foreach (var id in _ilsRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        _runways.Clear();
        _papiReplies.Clear();
        _publishedRunways = Array.Empty<RunwayFacility>();
        _runwayBatchKeys = Array.Empty<string>();
        _runwayBatchActive = _runwayBatchFailed = _runwayLookupStarted = false;
        _runwayLookupExpires = 0;
        _runwayEpisodeId = episodeId;
    }

    private void RequestEpisodeRunways(IReadOnlyList<TelemetrySample> samples)
    {
        // The episode may contain several bounces, all near the same landing site.
        TelemetrySample? contact = null;
        for (var index = 1; index < samples.Count; index++)
            if (samples[index].OnGround && !samples[index - 1].OnGround) contact = samples[index];
        if (contact == null || !RunwayNumber.IsFinite(contact.LatitudeDegrees) ||
            !RunwayNumber.IsFinite(contact.LongitudeDegrees) || Math.Abs(contact.LatitudeDegrees) > 90 ||
            Math.Abs(contact.LongitudeDegrees) > 180) return;
        _runwayLookupLatitude = contact.LatitudeDegrees;
        _runwayLookupLongitude = contact.LongitudeDegrees;
        _runwayLookupExpires = _hostClock.Elapsed.TotalSeconds + 60;
        foreach (var id in _runwayRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        foreach (var id in _ilsRequests.Keys.ToArray()) RemoveRunwayRequest(id);
        _runwayBatchActive = false;
        RequestNearbyRunways();
    }

    private void RequestNearbyRunways()
    {
        var connection = _simConnect;
        if (connection == null || _disposed || _hostClock.Elapsed.TotalSeconds >= _runwayLookupExpires) return;
        ExpireRunwayRequests();
        if (_runwayLookupStarted || _runwayRequests.Count > 0 || _ilsRequests.Count > 0) return;
        var candidates = _airportFacilities.Values
            .Select(airport => new { Airport = airport, Distance = GeoDistance.GreatCircleNauticalMiles(
                _runwayLookupLatitude, _runwayLookupLongitude, airport.LatitudeDegrees, airport.LongitudeDegrees) })
            .Where(item => item.Distance <= 12).OrderBy(item => item.Distance).Take(3).ToArray();
        _publishedRunways = Array.Empty<RunwayFacility>();
        _runwayBatchKeys = candidates.Select(item => item.Airport.Key).ToArray();
        _runwayBatchActive = candidates.Length > 0;
        _runwayLookupStarted = _runwayBatchActive;
        _runwayBatchFailed = false;
        foreach (var candidate in candidates)
        {
            var airport = candidate.Airport;
            if (_runways.ContainsKey(airport.Key) || _runwayRequests.Values.Any(request => request.Airport.Key == airport.Key)) continue;
            if (_runwayRequests.Count >= 3 || !PrepareRunwayDefinition(connection)) { _runwayBatchFailed = true; break; }
            // Request IDs are not reused after timeout or reconnect; late replies cannot attach to a new request.
            if (_nextRunwayRequestId == int.MaxValue) { _runwayBatchFailed = true; break; }
            var id = _nextRunwayRequestId++;
            _runwayRequests.Add(id, new RunwayFacilityRequest(airport, _hostClock.Elapsed.TotalSeconds + 10));
            try
            {
                connection.RequestFacilityData(Definitions.Runways, (Requests)id, airport.Ident, airport.Region);
                _runwayPackets[connection.GetLastSentPacketID()] = id;
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException || exception is ArgumentException)
            {
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
        if (data.Data == null || data.Data.Length != 1) { request.Invalidate(); return; }
        if ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type == SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT && data.Data[0] is RunwayAirportData airport)
            request.AddAirport(data.UniqueRequestId, airport.RunwayCount);
        else if ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type == SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY && data.Data[0] is RunwayFacilityData runway)
            request.AddRunway(data.ItemIndex, data.ParentUniqueRequestId, data.ListSize, runway.ToFacility(request.Airport), data.UniqueRequestId, runway);
        else if ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type == SIMCONNECT_FACILITY_DATA_TYPE.PAVEMENT && data.Data[0] is RunwayThresholdData threshold)
            request.AddThreshold(data.ParentUniqueRequestId, data.ItemIndex, data.ListSize, threshold);
        else if ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type == SIMCONNECT_FACILITY_DATA_TYPE.VASI && data.Data[0] is RunwayVasiData vasi)
            request.AddVasi(data.ParentUniqueRequestId, data.ItemIndex, data.ListSize, vasi);
        else request.Invalidate();
    }

    private void OnRecvRunwayFacilityDataEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (!ReferenceEquals(sender, _simConnect)) return;
        ExpireRunwayRequests();
        if (_ilsRequests.TryGetValue(data.RequestId, out var ils))
        {
            RemoveRunwayRequest(data.RequestId);
            if (ils.TryComplete(out var slope))
                foreach (var target in ils.Targets)
                    if (target.Item2) target.Item1.PrimaryIlsGlideSlope = slope;
                    else target.Item1.SecondaryIlsGlideSlope = slope;
            TryPublishRunwayBatch();
            return;
        }
        if (!_runwayRequests.TryGetValue(data.RequestId, out var request)) return;
        RemoveRunwayRequest(data.RequestId);
        if (request.TryComplete(out var runways))
        {
            if (request.PrimaryThreshold.HasValue) MergeRunwayThresholds(request.Airport.Key, runways, request.PrimaryThreshold.Value);
            else if (request.PapiSide.HasValue) MergeRunwayPapi(request.Airport.Key, runways, request.PapiSide.Value);
            else
            {
                _runways[request.Airport.Key] = runways;
                if (runways.Count > 0)
                {
                    RequestRunwayThresholds(sender, request.Airport);
                    RequestRunwayPapi(sender, request.Airport);
                    RequestRunwayIls(sender, request.IlsTargets);
                }
            }
        }
        else if (!request.IsOptional) _runwayBatchFailed = true;
        TryPublishRunwayBatch();
    }

    private void RequestRunwayThresholds(SimConnect connection, AirportFacility airport)
    {
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
                        _runwayPackets[connection.GetLastSentPacketID()] = uint.MaxValue - (uint)side;
                    }
                    connection.RegisterFacilityDataDefineStruct<RunwayThresholdData>(SIMCONNECT_FACILITY_DATA_TYPE.PAVEMENT);
                    _thresholdDefinitionReady[side] = true;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException) { }
            }
            if (!_thresholdDefinitionReady[side] || _nextRunwayRequestId == int.MaxValue) continue;
            var id = _nextRunwayRequestId++;
            _runwayRequests.Add(id, new RunwayFacilityRequest(airport, _hostClock.Elapsed.TotalSeconds + 10, side == 0));
            try
            {
                connection.RequestFacilityData(definition, (Requests)id, airport.Ident, airport.Region);
                _runwayPackets[connection.GetLastSentPacketID()] = id;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException) { RemoveRunwayRequest(id); }
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
                        _runwayPackets[connection.GetLastSentPacketID()] = uint.MaxValue - 2 - (uint)side;
                    }
                    connection.RegisterFacilityDataDefineStruct<RunwayVasiData>(SIMCONNECT_FACILITY_DATA_TYPE.VASI);
                    _papiDefinitionReady[side] = true;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException) { }
            }
            if (!_papiDefinitionReady[side] || _nextRunwayRequestId == int.MaxValue) continue;
            var id = _nextRunwayRequestId++;
            _runwayRequests.Add(id, new RunwayFacilityRequest(airport, _hostClock.Elapsed.TotalSeconds + 10, papiSide: side));
            try
            {
                connection.RequestFacilityData(definition, (Requests)id, airport.Ident, airport.Region);
                _runwayPackets[connection.GetLastSentPacketID()] = id;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException) { RemoveRunwayRequest(id); }
        }
    }

    private static bool SameRunwayGeometry(RunwayFacility a, RunwayFacility b) =>
        a.PrimaryName == b.PrimaryName && a.SecondaryName == b.SecondaryName &&
        a.LatitudeDegrees == b.LatitudeDegrees && a.LongitudeDegrees == b.LongitudeDegrees &&
        a.AltitudeMeters == b.AltitudeMeters && a.HeadingTrueDegrees == b.HeadingTrueDegrees &&
        a.LengthMeters == b.LengthMeters && a.WidthMeters == b.WidthMeters && a.SurfaceType == b.SurfaceType;

    private void RequestRunwayIls(SimConnect connection, IReadOnlyList<Tuple<RunwayFacility, bool, string, string>> targets)
    {
        if (targets.Count == 0) return;
        if (!_ilsDefinitionAttempted)
        {
            _ilsDefinitionAttempted = true;
            try
            {
                foreach (var field in new[] { "OPEN VOR", "GS_LATITUDE", "GS_LONGITUDE", "GS_ALTITUDE", "HAS_GLIDE_SLOPE", "GLIDE_SLOPE", "CLOSE VOR" })
                {
                    connection.AddToFacilityDefinition(Definitions.RunwayIlsGlideSlope, field);
                    _runwayPackets[connection.GetLastSentPacketID()] = uint.MaxValue - 6;
                }
                connection.RegisterFacilityDataDefineStruct<RunwayGlideSlopeData>(SIMCONNECT_FACILITY_DATA_TYPE.VOR);
                _ilsDefinitionReady = true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException) { }
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
            try
            {
                connection.RequestFacilityData(Definitions.RunwayIlsGlideSlope, (Requests)id, request.Ident, request.Region);
                _runwayPackets[connection.GetLastSentPacketID()] = id;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException) { RemoveRunwayRequest(id); }
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
        foreach (var id in _runwayRequests.Where(item => item.Value.DeadlineSeconds <= _hostClock.Elapsed.TotalSeconds).Select(item => item.Key).ToArray())
        {
            if (!_runwayRequests[id].IsOptional) _runwayBatchFailed = true;
            RemoveRunwayRequest(id);
        }
        foreach (var id in _ilsRequests.Where(item => item.Value.DeadlineSeconds <= _hostClock.Elapsed.TotalSeconds).Select(item => item.Key).ToArray()) RemoveRunwayRequest(id);
        TryPublishRunwayBatch();
    }

    private void TryPublishRunwayBatch()
    {
        if (!_runwayBatchActive || _runwayRequests.Count != 0 || _ilsRequests.Count != 0) return;
        _runwayBatchActive = false;
        if (_runwayBatchFailed || _runwayBatchKeys.Any(key => !_runways.ContainsKey(key))) return;
        _publishedRunways = _runwayBatchKeys.SelectMany(key => _runways[key]).ToArray();
        RunwayFacilitiesUpdated?.Invoke(this, new RunwayFacilitiesEventArgs(_publishedRunways, _runwayEpisodeId));
    }

    private void RemoveRunwayRequest(uint id)
    {
        _runwayRequests.Remove(id);
        _ilsRequests.Remove(id);
        foreach (var packet in _runwayPackets.Where(item => item.Value == id).Select(item => item.Key).ToArray()) _runwayPackets.Remove(packet);
    }

    private void ResetRunwayConnection()
    {
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
        _runwayEpisodeId = 0;
        _runwayBatchActive = false;
        _runwayBatchFailed = false;
        _runwayBatchKeys = Array.Empty<string>();
        _publishedRunways = Array.Empty<RunwayFacility>();
    }
}
