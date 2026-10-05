using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LandingStats.App.Models;

namespace LandingStats.App.Telemetry;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RunwayGlideSlopeData
{
    public double Latitude, Longitude, Altitude;
    public int HasGlideSlope;
    public float Angle;
}

// A VOR query is optional: no GS, invalid data or timeout cannot discard runway
// geometry. Its monotonically allocated request ID also rejects late replies.
internal sealed class RunwayIlsRequest
{
    private bool _received, _invalid;
    private RunwayGlideSlope? _value;
    internal RunwayIlsRequest(string ident, string region, double deadlineSeconds)
    { Ident = ident; Region = region; DeadlineSeconds = deadlineSeconds; }
    internal string Ident { get; }
    internal string Region { get; }
    internal double DeadlineSeconds { get; }
    internal List<Tuple<RunwayFacility, bool>> Targets { get; } = new List<Tuple<RunwayFacility, bool>>();

    internal void Invalidate() => _invalid = true;
    internal void Add(uint parentId, uint index, uint listSize, RunwayGlideSlopeData data)
    {
        if (_received || parentId != 0 || !((index == uint.MaxValue && listSize == uint.MaxValue) || (index == 0 && listSize == 1)) ||
            (data.HasGlideSlope != 0 && data.HasGlideSlope != 1)) _invalid = true;
        _received = true;
        if (data.HasGlideSlope == 0) return;
        _value = new RunwayGlideSlope { Ident = Ident, Region = Region, LatitudeDegrees = data.Latitude,
            LongitudeDegrees = data.Longitude, AltitudeMeters = data.Altitude, AngleDegrees = data.Angle };
        if (!_value.HasValidGeometry) _invalid = true;
    }

    internal bool TryComplete(out RunwayGlideSlope? value)
    { value = _received && !_invalid ? _value : null; return _received && !_invalid; }
}
