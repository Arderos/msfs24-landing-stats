using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LandingStats.App.Runtime;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

internal sealed class PreRollBuffer : IEnumerable<TelemetrySample>
{
    private readonly double _durationSeconds;
    private readonly int _maximumSamples;
    private readonly LinkedList<Entry> _entries = new LinkedList<Entry>();
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
    internal TelemetrySample? OldestSample => _entries.First?.Value.Sample;

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
        // Preserve both ends of each paused source state without allowing a long
        // pause to evict the active ground/approach history by its frame count.
        var last = _entries.Last;
        if (sample.SimulatorState == TelemetrySimulatorState.Paused && last?.Previous != null &&
            last.Value.Sample.SimulatorState == TelemetrySimulatorState.Paused &&
            last.Previous.Value.Sample.SimulatorState == TelemetrySimulatorState.Paused &&
            TelemetryDeduplicator.SameSourceControlState(last.Previous.Value.Sample, last.Value.Sample) &&
            TelemetryDeduplicator.SameSourceControlState(last.Value.Sample, sample))
            last.Value = new Entry(sample, receivedHostSeconds);
        else _entries.AddLast(new Entry(sample, receivedHostSeconds));
        while (_entries.Count > 1 &&
               receivedHostSeconds - _entries.First!.Value.ReceivedHostSeconds > _durationSeconds)
        {
            _entries.RemoveFirst();
        }

        // The time window is the primary limit. The count limit protects the UI process if MSFS
        // emits an unexpectedly high frame rate or a malformed stream repeats one timestamp forever.
        while (_entries.Count > _maximumSamples)
        {
            _entries.RemoveFirst();
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _lastReceivedHostSeconds = double.NaN;
    }

    internal void WriteCheckpoint(BinaryWriter writer)
    {
        writer.Write(_lastReceivedHostSeconds); writer.Write(-_entries.Count - 1);
        foreach (var entry in _entries) writer.Write(entry.ReceivedHostSeconds);
        RuntimeCheckpointCodec.WriteSamples(writer, ToArray(), _maximumSamples);
    }
    internal void RestoreCheckpoint(BinaryReader reader)
    {
        var last = reader.ReadDouble(); var encodedCount = reader.ReadInt32(); var annotated = encodedCount < 0;
        var count = annotated ? -(long)encodedCount - 1 : encodedCount;
        if (count < 0 || count > _maximumSamples) throw new InvalidDataException("Pre-roll checkpoint exceeds its sample bound.");
        var entries = new Entry[count]; var times = new double[count]; var previous = double.NegativeInfinity;
        for (var i = 0; i < count; i++)
        {
            var time = reader.ReadDouble();
            if (double.IsNaN(time) || double.IsInfinity(time) || time < previous || time > last) throw new InvalidDataException("Invalid pre-roll receipt clock.");
            previous = times[i] = time;
            if (!annotated) entries[i] = new Entry(TelemetryBinaryCodec.Read(reader), time);
        }
        if (annotated)
        {
            var samples = RuntimeCheckpointCodec.ReadSamples(reader, _maximumSamples);
            if (samples.Length != count) throw new InvalidDataException("Pre-roll receipt and sample counts differ.");
            for (var i = 0; i < count; i++) entries[i] = new Entry(samples[i], times[i]);
        }
        if ((count == 0 && !double.IsNaN(last)) || (count != 0 && (double.IsNaN(last) || double.IsInfinity(last) || last != previous)))
            throw new InvalidDataException("Invalid pre-roll checkpoint boundary.");
        _entries.Clear(); foreach (var entry in entries) _entries.AddLast(entry); _lastReceivedHostSeconds = last;
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
