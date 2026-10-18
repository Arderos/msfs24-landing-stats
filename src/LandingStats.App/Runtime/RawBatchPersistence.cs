using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class RawCaptureGap
{
    public RawCaptureGap(long droppedInputs, string reason, JournalCursor? last = null)
    {
        if (droppedInputs < 1 || reason == null || reason.Length > 1024) throw new InvalidDataException("Invalid RAW gap.");
        DroppedInputs = droppedInputs; Reason = reason; Segment = last?.Segment ?? 0; Sequence = last?.Sequence ?? 0;
    }
    [DataMember(Order = 1)] public long DroppedInputs { get; private set; }
    [DataMember(Order = 2)] public string Reason { get; private set; }
    [DataMember(Order = 3)] public long Segment { get; private set; }
    [DataMember(Order = 4)] public long Sequence { get; private set; }
    public JournalCursor? Cursor => Segment == 0 ? null : new JournalCursor(Segment, Sequence);
}
internal sealed class RawPendingEffect
{
    public RawPendingEffect(DurableRuntimeInput input, IReadOnlyList<object> effects)
    { Id = input.Input.Id; Cursor = input.Cursor; Bytes = RawEffectStore.Encode(input, effects); }
    public Guid Id { get; }
    public JournalCursor Cursor { get; }
    public byte[] Bytes { get; }
}

/// <summary>Optional RAW persistence never blocks the recording producer on
/// storage. The accepted main journal is its replay source until the recording
/// checkpoint flushes this batch. Quota/I/O failures explicitly end the RAW
/// fragment; durable landing effects and already published chunks survive.</summary>
internal sealed class RawBatchPersistence
{
    internal const int MaximumPendingInputs = 4096;
    internal const int MaximumPendingBytes = 12 * 1024 * 1024;
    private readonly object _gate = new object(), _flushGate = new object();
    private readonly RawEffectStore _store;
    private readonly List<RawPendingEffect> _pending = new List<RawPendingEffect>();
    private int _bytes;
    private JournalCursor? _lastCursor;
    private RawDebugCaptureStartedEventArgs? _capture;
    private RawCaptureGap? _gap, _diagnostic, _savedDiagnostic;
    public RawBatchPersistence(RawEffectStore store) { _store = store; }
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }
    internal RawCaptureGap? LastGap { get { lock (_gate) return _diagnostic; } }

    public void Commit(DurableRuntimeInput input, IReadOnlyList<object> effects)
    {
        if (effects.Count == 0) return;
        lock (_gate)
        {
            _lastCursor = input.Cursor;
            var start = effects.OfType<RawDebugCaptureStartedEventArgs>().LastOrDefault();
            if (start != null) _capture = start;
            var values = new List<object>();
            if (_gap != null)
            {
                values.Add(_gap);
                if (start == null && _capture != null && effects.Any(e => e is RawDebugSampleEventArgs))
                    values.Add(new RawDebugCaptureStartedEventArgs(Array.Empty<TelemetrySample>(), input.Input.TimestampUtc,
                        _capture.Simulator, _capture.AircraftTitle, _capture.AircraftType, _capture.AircraftModel, _capture.ControlInputSources));
            }
            values.AddRange(effects);
            try
            {
                var pending = new RawPendingEffect(input, values);
                if (_pending.Count == MaximumPendingInputs || pending.Bytes.Length > MaximumPendingBytes - _bytes)
                    throw new IOException("RAW batch capacity exceeded; diagnostic fragment ended.");
                _pending.Add(pending); _bytes += pending.Bytes.Length; _gap = null;
            }
            catch (Exception error) when (OptionalFailure(error)) { Drop(_pending.Count + 1, error); }
            if (effects.Any(e => ReferenceEquals(e, EventArgs.Empty))) _capture = null;
        }
    }
    // Checkpoint calls this before the main journal can collect the replay tail.
    public Task FlushAtCheckpointAsync() => Task.Run((Action)Flush);
    public void Flush()
    {
        lock (_flushGate)
        {
            RawPendingEffect[] pending;
            lock (_gate) { pending = _pending.ToArray(); _pending.Clear(); _bytes = 0; }
            if (pending.Length != 0)
            {
                try { _store.CommitBatch(pending); }
                catch (Exception error) when (OptionalFailure(error))
                { lock (_gate) Drop(pending.Length + _pending.Count, error); }
            }
            RawCaptureGap? diagnostic; lock (_gate) diagnostic = _diagnostic;
            if (diagnostic != null && !ReferenceEquals(diagnostic, _savedDiagnostic))
            {
                try { DurableFile.WriteJson(Path.Combine(_store.Root, "gap.json"), diagnostic); _savedDiagnostic = diagnostic; }
                catch (Exception error) when (OptionalFailure(error)) { Debug.WriteLine("RAW gap could not be saved: " + error.Message); }
            }
        }
    }
    private void Drop(int count, Exception error)
    {
        _pending.Clear(); _bytes = 0;
        var message = error.GetType().Name + ": " + error.Message;
        _gap = _diagnostic = new RawCaptureGap(Math.Min(long.MaxValue - count, _diagnostic?.DroppedInputs ?? 0) + count,
            message.Length > 1024 ? message.Substring(0, 1024) : message, _lastCursor);
        Debug.WriteLine("RAW capture fragment ended: " + _gap.Reason);
    }
    private static bool OptionalFailure(Exception error) => error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is SerializationException;
}
