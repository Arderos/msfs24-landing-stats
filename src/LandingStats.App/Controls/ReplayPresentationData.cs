using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LandingStats.App.ReplayMath;
using LandingStats.App.Models;

namespace LandingStats.App.Controls;

/// <summary>Prepared once away from the dispatcher. Frozen geometry can cross threads.</summary>
internal sealed class ReplayPresentationData
{
    private static readonly ConditionalWeakTable<LandingRecord, ReplayPresentationData> Cache = new ConditionalWeakTable<LandingRecord, ReplayPresentationData>();
    private static readonly object Gate = new object();
    private readonly RunwayFacility? _runway;
    private readonly int _seriesStamp;
    private readonly LandingRecord[] _contacts;
    internal LandingReplayTrack? Track { get; }
    internal ReplayControlInputs Inputs { get; }
    internal LandingReplaySurface? Surface { get; }
    internal IReadOnlyList<double> ContactTimes { get; }
    internal IReadOnlyList<Point3D> GroundPositions { get; }
    internal IReadOnlyDictionary<LandingSeriesPoint, double> Pitch { get; }

    private ReplayPresentationData(LandingRecord record, LandingRecord[] contacts)
    {
        _runway = record.RunwayGeometry; _contacts = contacts; _seriesStamp = SeriesStamp(record);
        Inputs = ReplayControlInputs.Resolve(record, contacts);
        var pitch = new Dictionary<LandingSeriesPoint, double>();
        if (record.ControllerTimeline != null)
        {
            var selected = record.HasRawPitchInput || !record.ControllerTimeline.IsSupported;
            foreach (var point in record.Series)
            {
                pitch[point] = Inputs.Sample(point.TimeSeconds).PitchPercent ?? double.NaN;
                selected |= Inputs.Pitch != null;
            }
            // Catalog presence alone does not select a physical pitch source.
            // Once selected, unavailable samples must remain gaps.
            if (!selected) pitch.Clear();
        }
        Pitch = pitch;
        if (LandingReplayTrack.TryCreate(record, out var track))
        {
            Track = track;
            Surface = LandingReplaySurface.Build(track!, record.RunwayGeometry);
            Surface.Terrain.Freeze(); Surface.Pavement.Freeze(); Surface.Markings.Freeze();
            ContactTimes = LandingReplayContacts.ContactTimes(record, track!, contacts);
            GroundPositions = LandingReplayContacts.GroundPositions(record, track!, contacts);
        }
        else { ContactTimes = Array.Empty<double>(); GroundPositions = Array.Empty<Point3D>(); }
    }
    internal static ReplayPresentationData Prepare(LandingRecord record, IEnumerable<LandingRecord> records)
    {
        var contacts = LandingReplayContacts.ForEpisode(record, records).ToArray();
        lock (Gate)
            if (Cache.TryGetValue(record, out var cached) && ReferenceEquals(cached._runway, record.RunwayGeometry) && cached._seriesStamp == SeriesStamp(record) && cached._contacts.SequenceEqual(contacts)) return cached;
        var prepared = new ReplayPresentationData(record, contacts);
        lock (Gate) { Cache.Remove(record); Cache.Add(record, prepared); }
        return prepared;
    }
    // Records are normally immutable after detail loading, but callers may
    // replace or repair a sample. Never reuse a scene with stale pose data.
    private static int SeriesStamp(LandingRecord record)
    {
        unchecked
        {
            var hash = record.Series.Count;
            foreach (var p in record.Series)
            {
                hash = hash * 31 + RuntimeHelpers.GetHashCode(p);
                hash = hash * 31 + p.TimeSeconds.GetHashCode();
                hash = hash * 31 + p.LatitudeDegrees.GetHashCode(); hash = hash * 31 + p.LongitudeDegrees.GetHashCode();
                hash = hash * 31 + p.PlaneAltitudeFeet.GetHashCode(); hash = hash * 31 + p.GroundAltitudeFeet.GetHashCode();
                hash = hash * 31 + p.HeadingTrueDegrees.GetHashCode(); hash = hash * 31 + p.PitchDegrees.GetHashCode();
                hash = hash * 31 + p.BankDegrees.GetHashCode(); hash = hash * 31 + p.OnGround.GetHashCode();
            }
            return hash;
        }
    }
    internal static IReadOnlyDictionary<LandingSeriesPoint, double>? PitchValues(LandingRecord record)
    {
        lock (Gate) if (Cache.TryGetValue(record, out var cached)) return cached.Pitch.Count == 0 ? null : cached.Pitch;
        var pitch = Prepare(record, new[] { record }).Pitch;
        return pitch.Count == 0 ? null : pitch;
    }
}
