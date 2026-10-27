using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
using LandingStats.Archives;

namespace LandingStats.App.Runtime;

[DataContract]
internal sealed class RawExportCheckpoint
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public long Segment { get; set; }
    [DataMember(Order = 3)] public long Sequence { get; set; }
    [DataMember(Order = 4)] public Guid Input { get; set; }
    [DataMember(Order = 5)] public int NextEffect { get; set; }
    [DataMember(Order = 6)] public int NextSample { get; set; }
    [DataMember(Order = 7)] public Guid Capture { get; set; }
    [DataMember(Order = 8)] public int Chunk { get; set; } = 1;
    [DataMember(Order = 9)] public RawCaptureMetadata? Metadata { get; set; }
    [DataMember(Order = 10)] public long GapSegment { get; set; }
    [DataMember(Order = 11)] public long GapSequence { get; set; }
    public JournalCursor? Cursor => Segment == 0 ? null : new JournalCursor(Segment, Sequence);
}

/// <summary>One supervised RAW exporter. Only a published, registered chunk
/// advances its durable cursor; a restart rebuilds the unfinished buffer from
/// immutable effects, including a chunk boundary inside the initial pre-roll.</summary>
internal sealed class RawExportOwner
{
    private readonly RawEffectStore _inputs;
    private readonly RawChunkStore _chunks;
    private readonly string _checkpoint;
    private readonly Action<string, int, bool> _published;
    private readonly int _chunkSize;
    private readonly long _maximumChunkBytes;
    private readonly List<TelemetrySample> _samples = new List<TelemetrySample>();
    private readonly HashSet<byte[]> _catalogReferences = new HashSet<byte[]>();
    private readonly HashSet<byte[]> _catalogs = new HashSet<byte[]>(ControllerProvenanceCodec.BytesComparer.Instance);
    private long _sampleBytes, _catalogBytes;
    private int? _committedSampleCount;
    private bool _boundaryKnown;
    private RawExportCheckpoint? _state;
    private JournalCursor? _readCursor;
    private bool _resume;
    private ManagedComponentContext? _owner;
    public RawExportOwner(RawEffectStore inputs, RawChunkStore chunks, Action<string, int, bool> published, int chunkSize = RawCaptureRepository.ChunkMaximumSamples,
        long maximumChunkBytes = RawChunkStore.MaximumDecodedBytes)
    {
        if (chunkSize < 1 || chunkSize > RawCaptureRepository.ChunkMaximumSamples) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (maximumChunkBytes <= MetadataReserve || maximumChunkBytes > CaptureArchiveFormat.MaximumDecodedBytes) throw new ArgumentOutOfRangeException(nameof(maximumChunkBytes));
        _maximumChunkBytes = maximumChunkBytes;
        _inputs = inputs; _chunks = chunks; _published = published; _chunkSize = chunkSize; _checkpoint = Path.Combine(inputs.Root, "export.json");
    }
    public void Wake() => Volatile.Read(ref _owner)?.SignalWork();
    internal bool CaughtUp => _state != null && _state.Capture == Guid.Empty && _samples.Count == 0 && _inputs.After(_readCursor, _resume).Count == 0;
    public async Task RunAsync(ManagedComponentContext owner)
    {
        if (Interlocked.CompareExchange(ref _owner, owner, null) != null) throw new InvalidOperationException("RAW exporter already has an owner.");
        try
        {
            Reset(); owner.MarkReady();
            while (true)
            {
                owner.CancellationToken.ThrowIfCancellationRequested();
                var worked = Advance(owner.CancellationToken, id => owner.Progress(false, DependencyWait.Storage, oldestPendingQpc: Stopwatch.GetTimestamp(), operationId: id));
                owner.Progress(worked, DependencyWait.Idle); await owner.WaitForWorkAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _owner, null); _state = null; _samples.Clear(); }
    }
    internal bool Advance(CancellationToken cancellationToken = default, Action<Guid>? processing = null)
    {
        try
        {
            if (_state == null) Reset(); var worked = false;
            var gapPath = Path.Combine(_inputs.Root, "gap.json");
            var gap = File.Exists(gapPath) ? DurableFile.ReadJson<RawCaptureGap>(gapPath, 8192) : null;
            if (gap != null && (gap.DroppedInputs < 1 || gap.Segment < 1 || gap.Sequence < 1 || gap.Reason == null || gap.Reason.Length > 1024))
                throw new InvalidDataException("Invalid RAW gap diagnostic.");
            foreach (var id in _inputs.After(_readCursor, _resume))
            {
                cancellationToken.ThrowIfCancellationRequested(); processing?.Invoke(id);
                var batch = _inputs.Read(id);
                if (gap?.Cursor != null && batch.Cursor.CompareTo(gap.Cursor) > 0) ApplyGap(gap);
                var same = _state!.Cursor != null && batch.Cursor.CompareTo(_state.Cursor) == 0;
                if (same && _state.Input != id) throw new InvalidDataException("RAW export checkpoint refers to another input.");
                if (!same)
                {
                    _state.Segment = batch.Cursor.Segment; _state.Sequence = batch.Cursor.Sequence; _state.Input = batch.Id;
                    _state.NextEffect = 0; _state.NextSample = 0;
                }
                if (_state.NextEffect > batch.Effects.Count) throw new InvalidDataException("RAW export checkpoint exceeds its batch.");
                for (var i = _state.NextEffect; i < batch.Effects.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var effect = batch.Effects[i];
                    if (effect is RawDebugCaptureStartedEventArgs start)
                    {
                        var capture = StableRuntimeIdentity.Command(id, i);
                        if (_state.NextSample == 0)
                        {
                            if (_state.Capture != Guid.Empty) throw new InvalidDataException("RAW capture started before its predecessor stopped.");
                            _state.Capture = capture; _state.Chunk = 1; _state.Metadata = RawCaptureMetadata.From(start);
                        }
                        else if (_state.Capture != capture) throw new InvalidDataException("RAW pre-roll recovery belongs to another capture.");
                        Append(start.InitialSamples, cancellationToken);
                    }
                    else if (effect is RawDebugSampleEventArgs sample) Append(new[] { sample.Sample }, cancellationToken);
                    else if (ReferenceEquals(effect, EventArgs.Empty) || effect is RawCaptureGap)
                    {
                        Flush(false); _state.Capture = Guid.Empty; _state.Metadata = null; _state.Chunk = 1;
                        _state.NextEffect = i + 1; _state.NextSample = 0; SaveCheckpoint(); continue;
                    }
                    else throw new InvalidDataException("Unsupported RAW effect.");
                    _state.NextEffect = i + 1; _state.NextSample = 0;
                }
                _readCursor = batch.Cursor; _resume = false; worked = true;
            }
            if (gap?.Cursor != null) ApplyGap(gap);
            return worked;
        }
        catch { _state = null; _samples.Clear(); throw; }
    }
    private void ApplyGap(RawCaptureGap gap)
    {
        var cursor = gap.Cursor!;
        if (_state!.GapSegment != 0 && cursor.CompareTo(new JournalCursor(_state.GapSegment, _state.GapSequence)) <= 0) return;
        // A crash after a failed RAW batch can precede its next in-stream gap
        // event. The durable diagnostic still closes that incomplete fragment.
        if (_readCursor == null || _readCursor.CompareTo(cursor) <= 0)
        { Flush(false); _state.Capture = Guid.Empty; _state.Metadata = null; _state.Chunk = 1; }
        _state.GapSegment = cursor.Segment; _state.GapSequence = cursor.Sequence;
        if (_state.Cursor != null && _samples.Count == 0) SaveCheckpoint();
    }
    private void Append(IReadOnlyList<TelemetrySample> samples, CancellationToken cancellationToken)
    {
        if (_state!.Capture == Guid.Empty || _state.Metadata == null || _state.NextSample > samples.Count) throw new InvalidDataException("RAW samples have no matching capture context.");
        for (var i = _state.NextSample; i < samples.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = samples[i];
            var bytes = Encoding.UTF8.GetByteCount(TelemetryCsv.Format(sample)) + Environment.NewLine.Length +
                1L + (sample.SourceEvidence == null ? 0 : ReplaySourceEvidenceCodec.EvidenceSize);
            if (!_boundaryKnown)
            { _committedSampleCount = _chunks.ExistingSampleCount(_state.Capture, _state.Chunk); _boundaryKnown = true; }
            var catalog = sample.ControllerCatalog;
            var additionalCatalog = catalog != null && !_catalogReferences.Contains(catalog) && !_catalogs.Contains(catalog);
            if (!_committedSampleCount.HasValue && _samples.Count != 0 &&
                (bytes > _maximumChunkBytes - MetadataReserve - _sampleBytes || additionalCatalog &&
                    (_catalogs.Count == 4096 || catalog!.Length > ControllerProvenanceCodec.MaximumTableBytes - _catalogBytes)))
            {
                // NextSample still points at this row. The flushed checkpoint
                // therefore resumes it once, in the following immutable chunk.
                Flush(true); additionalCatalog = catalog != null;
                _committedSampleCount = _chunks.ExistingSampleCount(_state.Capture, _state.Chunk); _boundaryKnown = true;
            }
            if (bytes > _maximumChunkBytes - MetadataReserve && !_committedSampleCount.HasValue)
                throw new InvalidDataException("A RAW sample exceeds the decoded chunk budget.");
            _samples.Add(sample); _sampleBytes += bytes;
            if (catalog != null)
            {
                _catalogReferences.Add(catalog);
                if (additionalCatalog) { _catalogs.Add(catalog); _catalogBytes += catalog.Length; }
            }
            _state.NextSample = i + 1;
            if (_samples.Count == (_committedSampleCount ?? _chunkSize)) Flush(true);
        }
    }
    private static readonly long MetadataReserve = CaptureArchiveFormat.MaximumSessionMetadataBytes + ControllerCatalogArchive.MaximumBytes +
        Encoding.UTF8.GetByteCount(TelemetryCsv.Header) + Environment.NewLine.Length + 8;
    private void ClearChunk()
    {
        _samples.Clear(); _sampleBytes = 0; _catalogBytes = 0; _catalogReferences.Clear(); _catalogs.Clear();
        _committedSampleCount = null; _boundaryKnown = false;
    }
    private void Flush(bool continues)
    {
        if (_samples.Count == 0) return;
        var count = _samples.Count;
        var path = _chunks.Publish(_state!.Capture, _state.Chunk, _state.Metadata!, _samples);
        _state.Chunk = checked(_state.Chunk + 1); ClearChunk(); SaveCheckpoint();
        _published(path, count, continues);
    }
    private void SaveCheckpoint()
    {
        if (_samples.Count != 0) throw new InvalidOperationException("An unfinished RAW buffer cannot be checkpointed without its inputs.");
        DurableFile.WriteJson(_checkpoint, _state!); _inputs.CollectBefore(_state!.Cursor!);
    }
    private void Reset()
    {
        // Sources use the same dynamic catalog metadata budget as recording checkpoints.
        var state = File.Exists(_checkpoint) ? DurableFile.ReadJson<RawExportCheckpoint>(_checkpoint, 8 * 1024 * 1024) : new RawExportCheckpoint();
        if (state.Version != 1 || state.Segment < 0 || state.Sequence < 0 || (state.Segment == 0) != (state.Sequence == 0) ||
            (state.Segment == 0) != (state.Input == Guid.Empty) || state.NextEffect < 0 || state.NextEffect > 8 || state.NextSample < 0 || state.NextSample > 65536 ||
            state.Chunk < 1 || (state.Capture == Guid.Empty) != (state.Metadata == null)) throw new InvalidDataException("Invalid RAW export checkpoint.");
        state.Metadata?.Validate(); _state = state; ClearChunk(); _readCursor = state.Cursor; _resume = state.Cursor != null;
        if (state.Cursor != null) _inputs.CollectBefore(state.Cursor);
    }
}
