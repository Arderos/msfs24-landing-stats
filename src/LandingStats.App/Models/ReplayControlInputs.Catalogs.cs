using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Runtime;
using LandingStats.Core;

namespace LandingStats.App.Models;

public sealed partial class ReplayControlInputs
{
    private CatalogSegment[]? _catalogSegments;
    private int _catalogSelection = -1;
    public int BindingRevision { get; private set; }

    private static ReplayControlInputs ResolveCatalogs(LandingRecord record, IEnumerable<LandingRecord> contacts, TelemetryDecisionJournal? journal)
    {
        var timeline = record.ControllerTimeline!;
        if (timeline.Version > 1) return new ReplayControlInputs(record.Series.ToArray());
        var contexts = timeline.Validate(record.Series.Count);
        var episode = LandingReplayContacts.ForEpisode(record, contacts).Where(value => !value.IsSummaryOnly && value.ControllerTimeline?.IsSupported == true).ToArray();
        var bindings = new Dictionary<string, (ReplayControlInputs Inputs, LandingRecord Record)>(StringComparer.Ordinal);
        var segments = new List<CatalogSegment>();
        var validated = new HashSet<LandingRecord> { record };
        for (var i = 0; i < timeline.Transitions.Length; i++)
        {
            var start = timeline.Transitions[i][0]; var end = i + 1 == timeline.Transitions.Length ? record.Series.Count : timeline.Transitions[i + 1][0];
            var context = timeline.Transitions[i][1]; var key = context < 0 ? string.Empty : timeline.Catalogs[context];
            var points = record.Series.Skip(start).Take(end - start).ToArray();
            var local = new ReplayControlInputs(points);
            if (!string.IsNullOrEmpty(key) && ControllerProvenanceCodec.Decode(contexts[context]) != null)
            {
                if (!bindings.TryGetValue(key, out var binding))
                {
                    // Reuse the first contact's evidence within this exact source
                    // generation/catalog only. A reused WinMM slot is a new scope.
                    var canonical = episode.FirstOrDefault(value => value.ControllerTimeline!.Catalogs.Contains(key)) ?? record;
                    if (validated.Add(canonical)) canonical.ControllerTimeline!.Validate(canonical.Series.Count);
                    var labels = ControllerCatalogTimeline.Labels(ControllerProvenanceCodec.Decode(contexts[context]));
                    var projected = ForCatalog(canonical, key, labels);
                    var catalogJournal = journal == null ? null : new TelemetryDecisionJournal();
                    binding = (Resolve(projected, catalogJournal), projected); bindings.Add(key, binding);
                    if (catalogJournal != null)
                        foreach (var entry in catalogJournal.Snapshot().Entries)
                        {
                            var details = entry.Details.ToDictionary(pair => pair.Key, pair => pair.Value);
                            details["catalog_index"] = context;
                            details["binding_contact_number"] = canonical.ContactNumber;
                            journal!.Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, details);
                        }
                }
                local.Pitch = Remap(binding.Inputs.Pitch, record);
                local.Roll = Remap(binding.Inputs.Roll, record);
                local.Yaw = Remap(binding.Inputs.Yaw, record);
            }
            segments.Add(new CatalogSegment(points[0].TimeSeconds, local));
        }
        var result = new ReplayControlInputs(record.Series.ToArray()) { _catalogSegments = segments.ToArray() };
        result.SelectCatalog(0); return result;
    }
    private static LandingRecord ForCatalog(LandingRecord source, string key, List<string> labels)
    {
        var timeline = source.ControllerTimeline!; var points = new List<LandingSeriesPoint>(source.Series.Count); var transition = 0;
        for (var i = 0; i < source.Series.Count; i++)
        {
            if (transition + 1 < timeline.Transitions.Length && timeline.Transitions[transition + 1][0] == i) transition++;
            var index = timeline.Transitions[transition][1]; var point = source.Series[i];
            // Keep time/command boundaries but expose no physical measurements
            // from another catalog to this catalog's correlation window.
            points.Add(index >= 0 && timeline.Catalogs[index] == key ? point : new LandingSeriesPoint
            { TimeSeconds = point.TimeSeconds, OnGround = point.OnGround, PilotPitchPercent = point.PilotPitchPercent,
                PilotRollPercent = point.PilotRollPercent, PilotYawPercent = point.PilotYawPercent });
        }
        return new LandingRecord { Series = points, ControlInputSources = labels,
            RawControllerSourceIndices = source.RawControllerSourceIndices, RawControllerAxesSourceIndices = source.RawControllerAxesSourceIndices,
            RawPitchInputSourceIndex = source.RawPitchInputSourceIndex, RawPitchInputCorrelation = source.RawPitchInputCorrelation, RawPitchInputLagSeconds = source.RawPitchInputLagSeconds };
    }
    private static ReplayControlAxis? Remap(ReplayControlAxis? axis, LandingRecord record)
    {
        if (axis == null) return null;
        var legacy = record.RawControllerAxesSourceIndices.Count == 0;
        var sources = legacy ? record.RawControllerSourceIndices.Count == 0 ? Enumerable.Range(0, TelemetrySample.CapturedControllerCount).ToArray() : record.RawControllerSourceIndices.ToArray()
            : record.RawControllerAxesSourceIndices.ToArray();
        var source = legacy ? axis.SourceIndex / TelemetrySample.CapturedControllerAxisCount : axis.SourceIndex;
        var column = Array.IndexOf(sources, source);
        return column >= 0 && sources.Count(value => value == source) == 1 && (!legacy || axis.SourceIndex % 6 == 1)
            ? new ReplayControlAxis(axis.SourceIndex, column, axis.SourceLabel, axis.Correlation, axis.LagSeconds, legacy) : null;
    }
    private ReplayControlSample SampleCatalog(double time)
    {
        var segments = _catalogSegments!; var low = 0; var high = segments.Length - 1;
        while (low < high) { var middle = (low + high + 1) / 2; if (segments[middle].Start <= time) low = middle; else high = middle - 1; }
        SelectCatalog(low); return segments[low].Inputs.Sample(time);
    }
    private void SelectCatalog(int index)
    {
        if (_catalogSelection == index) return;
        _catalogSelection = index; var selected = _catalogSegments![index].Inputs;
        Pitch = selected.Pitch; Roll = selected.Roll; Yaw = selected.Yaw; BindingRevision++;
    }
    private sealed class CatalogSegment
    {
        internal CatalogSegment(double start, ReplayControlInputs inputs) { Start = start; Inputs = inputs; }
        internal double Start { get; }
        internal ReplayControlInputs Inputs { get; }
    }
}
