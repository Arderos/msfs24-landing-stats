using System;
using System.Collections.Generic;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

/// <summary>Packet identities are meaningful only inside the current native connection.</summary>
internal sealed class SimConnectOperationJournal
{
    private const int MaximumOperations = 4096;
    private readonly Dictionary<uint, Operation> _operations = new Dictionary<uint, Operation>();
    private readonly Queue<uint> _order = new Queue<uint>();
    private readonly Dictionary<string, double> _versions = new Dictionary<string, double>();
    private long _connection;

    internal void BeginConnection(IReadOnlyDictionary<string, double>? versions = null)
    {
        Clear(); _connection++;
        if (versions != null) foreach (var pair in versions) _versions[pair.Key] = pair.Value;
    }

    internal void Clear() { _operations.Clear(); _order.Clear(); _versions.Clear(); }

    internal void Remember(uint packet, string name, bool optional, uint? request = null, uint? definition = null, uint? eventId = null,
        uint? period = null, uint? flags = null)
    {
        if (!_operations.ContainsKey(packet))
        {
            while (_order.Count >= MaximumOperations) _operations.Remove(_order.Dequeue());
            _order.Enqueue(packet);
        }
        _operations[packet] = new Operation(name, optional, request, definition, eventId, period, flags);
    }

    internal string? Name(uint packet) => _operations.TryGetValue(packet, out var op) ? op.Name : null;
    internal bool IsOptional(uint packet) => _operations.TryGetValue(packet, out var op) && op.Optional;
    internal bool IsRequiredDefinition(uint packet) => _operations.TryGetValue(packet, out var op) && !op.Optional &&
        op.Definition.HasValue && !op.Request.HasValue;

    internal void RecordRejection(TelemetryDecisionJournal journal, double time, uint exception, uint packet, uint parameter)
    {
        var known = _operations.TryGetValue(packet, out var operation);
        var details = new Dictionary<string, double>(_versions)
        {
            ["exception_code"] = exception, ["packet_id"] = packet, ["parameter_index"] = parameter,
            ["connection_generation"] = _connection, ["operation_known"] = known ? 1 : 0,
        };
        if (known)
        {
            details["optional_capability"] = operation!.Optional ? 1 : 0;
            if (operation.Request.HasValue) details["request_id"] = operation.Request.Value;
            if (operation.Definition.HasValue) details["definition_id"] = operation.Definition.Value;
            if (operation.Event.HasValue) details["event_id"] = operation.Event.Value;
            if (operation.Period.HasValue) details["period"] = operation.Period.Value;
            if (operation.Flags.HasValue) details["flags"] = operation.Flags.Value;
        }
        journal.Record(time, "recorder", "simconnect_request_rejected",
            known ? "Simulator rejected operation: " + operation!.Name + "." : "Simulator rejected an unknown operation in this connection; no packet association is available.", details);
    }

    private sealed class Operation
    {
        internal Operation(string name, bool optional, uint? request, uint? definition, uint? eventId, uint? period, uint? flags)
        { Name = name; Optional = optional; Request = request; Definition = definition; Event = eventId; Period = period; Flags = flags; }
        internal string Name { get; }
        internal bool Optional { get; }
        internal uint? Request { get; }
        internal uint? Definition { get; }
        internal uint? Event { get; }
        internal uint? Period { get; }
        internal uint? Flags { get; }
    }
}
