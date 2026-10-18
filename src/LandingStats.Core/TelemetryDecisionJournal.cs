using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace LandingStats.Core;

/// <summary>A bounded, capture-local account of decisions actually made by the analyzer.</summary>
public sealed class TelemetryDecisionJournal
{
    public const int MaximumEntries = 8192;
    private const int MaximumEstimatedJsonBytes = 3600000;
    private readonly object _gate = new object();
    private readonly List<TelemetryDecisionEntry> _entries = new List<TelemetryDecisionEntry>();
    private int _droppedEntryCount;
    private int _estimatedJsonBytes;

    public TelemetryDecisionJournal(TelemetryDecisionSnapshot? seed = null)
    {
        if (seed == null) return;
        _droppedEntryCount = seed.DroppedEntryCount;
        foreach (var entry in seed.Entries)
            Record(entry.TimeSeconds, entry.Component, entry.Code, entry.Reason, entry.Details);
    }

    public void Record(
        double timeSeconds,
        string component,
        string code,
        string reason,
        IReadOnlyDictionary<string, double>? details = null)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaximumEntries)
            {
                if (_droppedEntryCount < int.MaxValue) _droppedEntryCount++;
                return;
            }

            var entry = new TelemetryDecisionEntry(timeSeconds, component, code, reason, details);
            // Six bytes per UTF-16 code unit covers JSON unicode escaping;
            // fixed overhead also reserves room for keys and finite doubles.
            var estimatedBytes = 256 + 6 * (entry.Component.Length + entry.Code.Length + entry.Reason.Length);
            foreach (var item in entry.Details) estimatedBytes += 48 + 6 * item.Key.Length;
            if (estimatedBytes > MaximumEstimatedJsonBytes - _estimatedJsonBytes)
            {
                if (_droppedEntryCount < int.MaxValue) _droppedEntryCount++;
                return;
            }
            _estimatedJsonBytes += estimatedBytes;
            _entries.Add(entry);
        }
    }

    public TelemetryDecisionSnapshot Snapshot()
    {
        lock (_gate)
            return new TelemetryDecisionSnapshot(_entries.ToArray(), _droppedEntryCount);
    }

    /// <summary>Restore the bounded journal's loss counter from its durable snapshot.</summary>
    public void RestoreDroppedEntryCount(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate) _droppedEntryCount = Math.Max(_droppedEntryCount, count);
    }
}

public sealed class TelemetryDecisionSnapshot
{
    internal TelemetryDecisionSnapshot(TelemetryDecisionEntry[] entries, int droppedEntryCount)
    {
        Entries = Array.AsReadOnly(entries);
        DroppedEntryCount = droppedEntryCount;
    }

    public IReadOnlyList<TelemetryDecisionEntry> Entries { get; }
    public int DroppedEntryCount { get; }
    public bool Truncated => DroppedEntryCount > 0;
}

public sealed class TelemetryDecisionEntry
{
    internal TelemetryDecisionEntry(
        double timeSeconds,
        string component,
        string code,
        string reason,
        IReadOnlyDictionary<string, double>? details)
    {
        TimeSeconds = Finite(timeSeconds) ? timeSeconds : 0;
        Component = Bound(component, 48);
        Code = Bound(code, 80);
        Reason = Bound(reason, 256);
        var copied = new Dictionary<string, double>(StringComparer.Ordinal);
        if (details != null)
        {
            foreach (var item in details)
            {
                if (copied.Count == 24) break;
                if (Finite(item.Value)) copied[Bound(item.Key, 64)] = item.Value;
            }
        }
        Details = new ReadOnlyDictionary<string, double>(copied);
    }

    /// <summary>
    /// Source event time, not wall-clock processing time. Analyzer/recorder
    /// events use the telemetry clock; replay adapters explicitly mark their
    /// contact-relative clock in Details. Zero may also denote an untimed event.
    /// </summary>
    public double TimeSeconds { get; }
    public string Component { get; }
    public string Code { get; }
    public string Reason { get; }
    public IReadOnlyDictionary<string, double> Details { get; }

    private static bool Finite(double value) => AnalysisNumericPolicy.IsFinite(value);
    private static string Bound(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var result = new System.Text.StringBuilder(Math.Min(value.Length, maximum));
        for (var i = 0; i < value.Length && result.Length < maximum; i++)
        {
            var character = value[i];
            if (char.IsHighSurrogate(character) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                if (result.Length + 2 > maximum) break;
                result.Append(character); result.Append(value[++i]);
            }
            else result.Append(char.IsSurrogate(character) || character == '\0' ? '\uFFFD' : character);
        }
        return result.Length == 0 ? "unknown" : result.ToString();
    }
}
