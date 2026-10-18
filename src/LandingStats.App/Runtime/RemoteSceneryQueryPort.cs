using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

internal sealed class SceneryLookupContext
{
    public SceneryLookupContext(Guid episode, long ordinal, Guid lookup, LookupStage stage, long witnessQpc = 0)
    { Episode = episode; Ordinal = ordinal; Lookup = lookup; Stage = stage; WitnessQpc = witnessQpc; }
    public Guid Episode { get; }
    public long Ordinal { get; }
    public Guid Lookup { get; }
    public LookupStage Stage { get; }
    public long WitnessQpc { get; }
}

/// <summary>Recording-owner port. Only complete, hash-checked snapshots from the
/// current source/connection/episode/stage can settle the facility coordinator.</summary>
internal sealed class RemoteSceneryQueryPort : ISceneryQueryPort
{
    private readonly Action<SceneryCommandMessage> _send;
    private readonly Func<SceneryLookupContext> _context;
    private readonly Action<IReadOnlyList<RunwayFacility>> _completed;
    private WorkerIdentity? _worker;
    private long _connection;
    private bool _connected;
    private SceneryCommandMessage? _request;
    private SceneryQuerySnapshot _snapshot = Empty();
    private byte[]? _transfer, _hash;
    private Guid _transferId;
    private int _received;
    private bool _transferComplete, _settled;
    public RemoteSceneryQueryPort(Action<SceneryCommandMessage> send, Func<SceneryLookupContext> context, Action<IReadOnlyList<RunwayFacility>> completed)
    { _send = send; _context = context; _completed = completed; }
    public bool CanQuery => _connected;
    public IReadOnlyList<AirportFacility> CachedAirports => _snapshot.Airports;
    public SceneryQuerySnapshot Snapshot => _snapshot;
    public bool MatchesDiagnostic(RuntimeEnvelope envelope) => _request != null && _worker != null && _worker.Equals(envelope.Worker) &&
        envelope.ConnectionGeneration == _connection && envelope.EpisodeId == _request.Episode && envelope.LookupId == _request.Lookup && envelope.LookupStage == _request.Stage;
    public void Connect(WorkerIdentity worker, long connection)
    {
        if (worker.Role != ComponentRole.Simulator || connection <= 0) throw new ArgumentException("Invalid scenery source.");
        if (_connected && (!_worker!.Equals(worker) || _connection != connection)) throw new InvalidOperationException("Retire the former scenery source first.");
        _worker = worker; _connection = connection; _connected = true;
    }
    public void Retire(WorkerIdentity worker)
    {
        if (_worker == null || !_worker.Equals(worker)) return;
        _connected = false; ClearTransfer();
        // The last fully received snapshot remains available to the episode's
        // gap finalization. A source restart never clears accepted evidence.
    }
    public void Request(double latitude, double longitude)
    {
        var context = _context();
        if (!CanQuery) { Cancel("unavailable"); _snapshot = Empty(); _completed(Array.Empty<RunwayFacility>()); return; }
        if (double.IsNaN(latitude) || double.IsInfinity(latitude) || Math.Abs(latitude) > 90 ||
            double.IsNaN(longitude) || double.IsInfinity(longitude) || Math.Abs(longitude) > 180)
        { Cancel("invalid_coordinates"); _snapshot = Empty(); _settled = true; _completed(Array.Empty<RunwayFacility>()); return; }
        Cancel("superseded"); _snapshot = Empty(); _settled = false;
        _request = new SceneryCommandMessage(_connection, context.Episode, context.Ordinal, context.Lookup, context.Stage, latitude, longitude, witnessQpc: context.WitnessQpc);
        _send(_request);
    }
    public void Reset() { Cancel("reset"); _snapshot = Empty(); _settled = false; }
    public void Cancel(string reason)
    {
        if (_request != null && _connected && !_settled)
            _send(new SceneryCommandMessage(_request.Connection, _request.Episode, _request.EpisodeOrdinal, _request.Lookup, _request.Stage,
                _request.Latitude, _request.Longitude, true, _request.WitnessQpc));
        _request = null; ClearTransfer();
    }
    public bool HasExpired(long nowQpc, long frequency) => _connected && _request != null && !_settled &&
        nowQpc >= _request.WitnessQpc && (nowQpc - _request.WitnessQpc) / (double)frequency >= 60;
    public bool FailedCurrent(RuntimeCommandOutcome outcome) => _connected && !_settled && _worker!.Equals(outcome.Request.Worker) &&
        _request != null && outcome.Request.Payload is SceneryCommandMessage command && !command.Cancel &&
        command.Connection == _connection && command.Episode == _request.Episode && command.Lookup == _request.Lookup && command.Stage == _request.Stage &&
        (outcome.Result.Outcome == CommandOutcome.Rejected || outcome.Result.Outcome == CommandOutcome.Failed);
    public bool Accept(RuntimeEnvelope envelope)
    {
        if (!(envelope.Payload is SceneryChunkMessage chunk)) throw new ArgumentException("Not a scenery reply.");
        if (!_connected || !_worker!.Equals(envelope.Worker) || envelope.ConnectionGeneration != _connection || _request == null || _settled ||
            envelope.EpisodeId != _request.Episode || envelope.LookupId != _request.Lookup || envelope.LookupStage != _request.Stage) return false;
        if (_transfer == null)
        {
            if (chunk.Index != 0) throw new InvalidDataException("Scenery transfer is missing its first chunk.");
            _transferId = chunk.Transfer; _transfer = new byte[chunk.Length]; _hash = chunk.CopyHash(); _received = 0; _transferComplete = chunk.Completed;
        }
        if (_transferId != chunk.Transfer || _transfer.Length != chunk.Length || chunk.Index * SceneryChunkMessage.ChunkBytes != _received ||
            chunk.Completed != _transferComplete || !_hash!.SequenceEqual(chunk.CopyHash())) throw new InvalidDataException("Scenery chunks crossed transfer boundaries.");
        var bytes = chunk.CopyBytes(); Array.Copy(bytes, 0, _transfer, _received, bytes.Length); _received += bytes.Length;
        if (_received != _transfer.Length) return true;
        using (var sha = SHA256.Create()) if (!sha.ComputeHash(_transfer).SequenceEqual(_hash!)) throw new InvalidDataException("Scenery transfer hash mismatch.");
        using (var memory = new MemoryStream(_transfer, false))
        using (var reader = new BinaryReader(memory))
        {
            var snapshot = RuntimeCheckpointCodec.ReadJson<SceneryCheckpointData>(reader).Restore();
            if (memory.Position != memory.Length) throw new InvalidDataException("Scenery transfer has an unexpected suffix.");
            _snapshot = snapshot;
        }
        var complete = _transferComplete; ClearTransfer();
        if (complete) { _settled = true; _completed(_snapshot.Runways); }
        return true;
    }
    public byte[] CaptureCheckpoint()
    {
        using var memory = new MemoryStream(); using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
        {
            writer.Write(0x31515352); writer.Write(_worker != null);
            if (_worker != null)
            {
                writer.Write(_worker.AppSessionId.ToByteArray()); writer.Write(_worker.InstanceId.ToByteArray()); writer.Write(_worker.Generation);
                writer.Write(_connection); writer.Write(_connected);
            }
            RuntimeCheckpointCodec.WriteJson(writer, SceneryCheckpointData.Capture(_snapshot));
            writer.Write(_settled); writer.Write(_request != null);
            if (_request != null)
            {
                var bytes = RuntimeWire.Encode(new RuntimeEnvelope(_worker!, 0, 1, 0, _request, Guid.Parse("00000000-0000-0000-0000-000000000001"), 1));
                writer.Write(bytes.Length); writer.Write(bytes);
            }
            writer.Write(_transfer != null);
            if (_transfer != null)
            {
                writer.Write(_transferId.ToByteArray()); writer.Write(_transfer.Length); writer.Write(_hash!); writer.Write(_transferComplete);
                writer.Write(_received); writer.Write(_transfer, 0, _received);
            }
        }
        return memory.ToArray();
    }
    public void RestoreCheckpoint(byte[] bytes)
    {
        try { RestoreCheckpointCore(bytes); }
        catch (Exception error) when (error is EndOfStreamException || error is ArgumentException || error is System.Runtime.Serialization.SerializationException)
        { throw new InvalidDataException("Malformed scenery checkpoint.", error); }
    }
    private void RestoreCheckpointCore(byte[] bytes)
    {
        if (bytes == null || bytes.Length > 2 * SceneryChunkMessage.MaximumTransferBytes + 4096) throw new InvalidDataException("Scenery port checkpoint exceeds its bound.");
        using var memory = new MemoryStream(bytes, false); using var reader = new BinaryReader(memory);
        if (reader.ReadInt32() != 0x31515352) throw new InvalidDataException("Unsupported scenery port checkpoint.");
        _worker = null; _connection = 0; _connected = false; _request = null; ClearTransfer();
        if (RuntimeCheckpointCodec.ReadBoolean(reader))
        {
            _worker = new WorkerIdentity(new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)), new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)), reader.ReadInt64(), ComponentRole.Simulator);
            _connection = reader.ReadInt64(); _connected = RuntimeCheckpointCodec.ReadBoolean(reader);
            if (_connection <= 0) throw new InvalidDataException("Invalid checkpoint connection.");
        }
        _snapshot = RuntimeCheckpointCodec.ReadJson<SceneryCheckpointData>(reader).Restore(); _settled = RuntimeCheckpointCodec.ReadBoolean(reader);
        if (RuntimeCheckpointCodec.ReadBoolean(reader))
        {
            var request = RuntimeWire.Decode(RuntimeCheckpointCodec.ReadBytes(reader, RuntimeCheckpointCodec.ReadCount(reader, 4096)));
            if (_worker == null || !_worker.Equals(request.Worker) || !(request.Payload is SceneryCommandMessage command) || command.Cancel || command.Connection != _connection)
                throw new InvalidDataException("Checkpoint scenery context differs from its source.");
            _request = command;
        }
        if (RuntimeCheckpointCodec.ReadBoolean(reader))
        {
            _transferId = new Guid(RuntimeCheckpointCodec.ReadBytes(reader, 16)); _transfer = new byte[RuntimeCheckpointCodec.ReadCount(reader, SceneryChunkMessage.MaximumTransferBytes)];
            _hash = RuntimeCheckpointCodec.ReadBytes(reader, 32); _transferComplete = RuntimeCheckpointCodec.ReadBoolean(reader);
            _received = RuntimeCheckpointCodec.ReadCount(reader, _transfer.Length);
            if (_request == null || !_connected || _settled || _transferId == Guid.Empty || _received == 0 || _received >= _transfer.Length || _received % SceneryChunkMessage.ChunkBytes != 0)
                throw new InvalidDataException("Invalid partial scenery transfer checkpoint.");
            var prefix = RuntimeCheckpointCodec.ReadBytes(reader, _received); Array.Copy(prefix, _transfer, prefix.Length);
        }
        if (memory.Position != memory.Length) throw new InvalidDataException("Scenery port checkpoint has an unexpected suffix.");
    }
    private void ClearTransfer() { _transfer = null; _hash = null; _received = 0; _transferId = Guid.Empty; _transferComplete = false; }
    private static SceneryQuerySnapshot Empty() => new SceneryQuerySnapshot(Array.Empty<RunwayFacility>(), Array.Empty<AirportFacility>(), false, false, 0, 0);
}
