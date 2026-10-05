using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LandingStats.App.Models;

namespace LandingStats.App.Telemetry;

internal sealed class RunwayFacilitiesEventArgs : EventArgs
{
    public RunwayFacilitiesEventArgs(IReadOnlyList<RunwayFacility> runways, long episodeId = 0)
    {
        Runways = runways;
        EpisodeId = episodeId;
    }
    public IReadOnlyList<RunwayFacility> Runways { get; }
    public long EpisodeId { get; }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RunwayAirportData
{
    public int RunwayCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RunwayThresholdData
{
    public float Length, Width;
    public int Enable;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RunwayVasiData
{
    public int Type;
    public float BiasX, BiasZ, Spacing, Angle;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RunwayFacilityData
{
    public double Latitude, Longitude, Altitude;
    public float Heading, Length, Width;
    public int PrimaryNumber, PrimaryDesignator, SecondaryNumber, SecondaryDesignator;
    public int Surface;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string PrimaryIls;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string PrimaryIlsRegion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string SecondaryIls;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string SecondaryIlsRegion;

    public RunwayFacility ToFacility(AirportFacility airport) => new RunwayFacility
    {
        AirportIdent = airport.Ident,
        AirportRegion = airport.Region,
        LatitudeDegrees = Latitude,
        LongitudeDegrees = Longitude,
        AltitudeMeters = Altitude,
        HeadingTrueDegrees = Heading == 360 ? 0 : Heading,
        LengthMeters = Length,
        WidthMeters = Width,
        PrimaryName = Name(PrimaryNumber, PrimaryDesignator),
        SecondaryName = Name(SecondaryNumber, SecondaryDesignator),
        SurfaceType = Surface,
        RetrievedUtc = DateTime.UtcNow,
    };

    private static string Name(int number, int designator)
    {
        var name = number >= 1 && number <= 36 ? number.ToString("00", System.Globalization.CultureInfo.InvariantCulture) :
            number >= 37 && number <= 44 ? new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[number - 37] : string.Empty;
        if (name.Length == 0 || designator < 0 || designator > 6) return string.Empty;
        return name + new[] { "", "L", "R", "C", "W", "A", "B" }[designator];
    }
}

// One small, bounded transaction. Partial replies never replace the cache.
internal sealed class RunwayFacilityRequest
{
    private const int MaximumRunways = 128;
    private readonly Dictionary<uint, Tuple<uint, uint, RunwayFacility>> _rows = new Dictionary<uint, Tuple<uint, uint, RunwayFacility>>();
    private uint? _airportUniqueId;
    private int? _expectedCount;
    private bool _invalid;
    private readonly Dictionary<uint, uint> _runwayIds = new Dictionary<uint, uint>();
    private readonly Dictionary<uint, RunwayThresholdData> _thresholds = new Dictionary<uint, RunwayThresholdData>();
    private readonly Dictionary<uint, RunwayVasiData> _papis = new Dictionary<uint, RunwayVasiData>();

    public RunwayFacilityRequest(AirportFacility airport, double deadlineSeconds, bool? primaryThreshold = null, int? papiSide = null)
    {
        Airport = airport;
        DeadlineSeconds = deadlineSeconds;
        PrimaryThreshold = primaryThreshold;
        PapiSide = papiSide;
        if (papiSide.HasValue && (papiSide < 0 || papiSide > 3 || primaryThreshold.HasValue)) _invalid = true;
    }
    public AirportFacility Airport { get; }
    public double DeadlineSeconds { get; }
    public bool? PrimaryThreshold { get; }
    // Primary left/right, then secondary left/right. Each is a separate native
    // request because VASI children have identical native type and item indices.
    public int? PapiSide { get; }
    public bool IsOptional => PrimaryThreshold.HasValue || PapiSide.HasValue;
    public List<Tuple<RunwayFacility, bool, string, string>> IlsTargets { get; } = new List<Tuple<RunwayFacility, bool, string, string>>();

    public void AddAirport(uint uniqueId, int count)
    {
        if (_airportUniqueId.HasValue || count < 0 || count > MaximumRunways) _invalid = true;
        _airportUniqueId = uniqueId;
        _expectedCount = count;
    }

    public void AddRunway(uint index, uint parentId, uint listSize, RunwayFacility value, uint uniqueId = 0, RunwayFacilityData? native = null)
    {
        if (index >= MaximumRunways || listSize > MaximumRunways || index >= listSize ||
            _rows.ContainsKey(index) || !value.HasValidGeometry ||
            string.IsNullOrEmpty(value.PrimaryName) || string.IsNullOrEmpty(value.SecondaryName))
        {
            _invalid = true;
            return;
        }
        _rows.Add(index, Tuple.Create(parentId, listSize, value));
        if (!IsOptional && native.HasValue)
        {
            var row = native.Value;
            if (!string.IsNullOrWhiteSpace(row.PrimaryIls))
                IlsTargets.Add(Tuple.Create(value, true, row.PrimaryIls.Trim(), (row.PrimaryIlsRegion ?? "").Trim()));
            if (!string.IsNullOrWhiteSpace(row.SecondaryIls))
                IlsTargets.Add(Tuple.Create(value, false, row.SecondaryIls.Trim(), (row.SecondaryIlsRegion ?? "").Trim()));
        }
        if (IsOptional)
        {
            if (_runwayIds.ContainsKey(uniqueId)) _invalid = true;
            else _runwayIds.Add(uniqueId, index);
        }
    }

    public void AddThreshold(uint parentId, uint index, uint listSize, RunwayThresholdData value)
    {
        // MSFS marks non-list children with UINT_MAX/UINT_MAX (observed in
        // live EDDM/EGLL replies). Also accept an explicit singleton list.
        var singleton = (index == uint.MaxValue && listSize == uint.MaxValue) || (index == 0 && listSize == 1);
        if (!PrimaryThreshold.HasValue || !singleton || _thresholds.Count >= MaximumRunways ||
            _thresholds.ContainsKey(parentId) || (value.Enable != 0 && value.Enable != 1) ||
            !RunwayNumber.IsFinite(value.Length) || value.Length < 0 || !RunwayNumber.IsFinite(value.Width) || value.Width < 0)
        { _invalid = true; return; }
        _thresholds.Add(parentId, value);
    }

    public void Invalidate() => _invalid = true;

    public void AddVasi(uint parentId, uint index, uint listSize, RunwayVasiData value)
    {
        var singleton = (index == uint.MaxValue && listSize == uint.MaxValue) || (index == 0 && listSize == 1);
        if (!PapiSide.HasValue || !singleton || _papis.Count >= MaximumRunways || _papis.ContainsKey(parentId) ||
            value.Type < 0 || value.Type > 13 ||
            ((value.Type == 7 || value.Type == 8) && (!RunwayNumber.IsFinite(value.BiasZ) ||
                !RunwayNumber.IsFinite(value.Angle) || value.Angle <= 0 || value.Angle >= 90)))
        { _invalid = true; return; }
        _papis.Add(parentId, value);
    }

    public bool TryComplete(out IReadOnlyList<RunwayFacility> runways)
    {
        runways = Array.Empty<RunwayFacility>();
        if (_invalid || !_expectedCount.HasValue || _rows.Count != _expectedCount.Value ||
            _rows.Any(row => row.Key >= _expectedCount.Value || row.Value.Item1 != _airportUniqueId ||
                             row.Value.Item2 != _expectedCount.Value)) return false;
        if (PrimaryThreshold.HasValue)
        {
            if (_thresholds.Count != _rows.Count || _thresholds.Keys.Any(id => !_runwayIds.ContainsKey(id))) return false;
            foreach (var item in _thresholds)
            {
                var runway = _rows[_runwayIds[item.Key]].Item3;
                var offset = item.Value.Enable == 0 ? 0 : item.Value.Length;
                if (offset >= runway.LengthMeters) return false;
                if (PrimaryThreshold.Value) runway.PrimaryThresholdOffsetMeters = offset;
                else runway.SecondaryThresholdOffsetMeters = offset;
            }
        }
        if (PapiSide.HasValue)
        {
            if (_papis.Count != _rows.Count || _papis.Keys.Any(id => !_runwayIds.ContainsKey(id))) return false;
            foreach (var item in _papis)
            {
                var runway = _rows[_runwayIds[item.Key]].Item3;
                // Only PAPI2/PAPI4. VASI and other visual systems are not PAPI.
                var papi = item.Value.Type == 7 || item.Value.Type == 8 ? new RunwayPapi
                    { StationFromEndMeters = runway.LengthMeters / 2 - item.Value.BiasZ, AngleDegrees = item.Value.Angle } : null;
                if (papi != null && !papi.HasValidGeometry(runway.LengthMeters)) return false;
                if (PapiSide.Value < 2) { runway.PrimaryPapi = papi; runway.PrimaryPapiQueried = true; }
                else { runway.SecondaryPapi = papi; runway.SecondaryPapiQueried = true; }
            }
        }
        runways = _rows.OrderBy(row => row.Key).Select(row => row.Value.Item3).ToArray();
        return true;
    }
}
