using System;
using System.Collections;
using System.Collections.Generic;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

internal sealed class PreRollBuffer : IEnumerable<TelemetrySample>
{
    private readonly double _durationSeconds;
    private readonly int _maximumSamples;
    private readonly Queue<Entry> _entries = new Queue<Entry>();
    private double _lastReceivedHostSeconds = double.NaN;

    public PreRollBuffer(double durationSeconds, int maximumSamples)
    {
        if (durationSeconds <= 0 || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        if (maximumSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSamples));
        }

        _durationSeconds = durationSeconds;
        _maximumSamples = maximumSamples;
    }

    public int Count => _entries.Count;

    public void Add(TelemetrySample sample, double receivedHostSeconds)
    {
        if (sample == null)
        {
            throw new ArgumentNullException(nameof(sample));
        }

        if (double.IsNaN(receivedHostSeconds) || double.IsInfinity(receivedHostSeconds))
        {
            Clear();
            return;
        }

        // Stopwatch time is monotonic inside a connection. A backwards jump means a new clock epoch;
        // retaining entries from the previous epoch would make them impossible to age out.
        if (!double.IsNaN(_lastReceivedHostSeconds) && receivedHostSeconds < _lastReceivedHostSeconds)
        {
            Clear();
        }

        _lastReceivedHostSeconds = receivedHostSeconds;
        _entries.Enqueue(new Entry(sample, receivedHostSeconds));
        while (_entries.Count > 1 &&
               receivedHostSeconds - _entries.Peek().ReceivedHostSeconds > _durationSeconds)
        {
            _entries.Dequeue();
        }

        // The time window is the primary limit. The count limit protects the UI process if MSFS
        // emits an unexpectedly high frame rate or a malformed stream repeats one timestamp forever.
        while (_entries.Count > _maximumSamples)
        {
            _entries.Dequeue();
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _lastReceivedHostSeconds = double.NaN;
    }

    public TelemetrySample[] ToArray()
    {
        var result = new TelemetrySample[_entries.Count];
        var index = 0;
        foreach (var entry in _entries)
        {
            result[index++] = entry.Sample;
        }

        return result;
    }

    public IEnumerator<TelemetrySample> GetEnumerator()
    {
        foreach (var entry in _entries)
        {
            yield return entry.Sample;
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private readonly struct Entry
    {
        public Entry(TelemetrySample sample, double receivedHostSeconds)
        {
            Sample = sample;
            ReceivedHostSeconds = receivedHostSeconds;
        }

        public TelemetrySample Sample { get; }

        public double ReceivedHostSeconds { get; }
    }
}
