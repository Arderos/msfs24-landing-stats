using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Runtime.Contracts;
using LandingStats.Workers.IntegrationApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LandingStats.App.RegressionTests;

internal static class IntegrationApiTransportRegressionTests
{
    public static void RunAll() => Task.Run(async () => { await Framing(); await Clients(); }).GetAwaiter().GetResult();

    private static async Task Framing()
    {
        using var stream = new MemoryStream();
        const string value = "{\"aircraft\":\"Самолёт ✈\"}";
        await IntegrationApiProtocol.WriteAsync(stream, value, CancellationToken.None);
        using var chunks = new ChunkedStream(stream.ToArray());
        Check(await IntegrationApiProtocol.ReadAsync(chunks, 65536, CancellationToken.None) == value, "Fragmented UTF-8 frame changed.");
        Check(await IntegrationApiProtocol.ReadAsync(chunks, 65536, CancellationToken.None) == null, "Clean EOF was not recognized.");
        foreach (var bytes in new[] { new byte[] { 255, 255, 255, 127 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 2, 0, 0, 0, 32 },
            new byte[] { 1, 0 }, new byte[] { 1, 0, 0, 0, 255 } })
            await Reject(async () => { using var input = new MemoryStream(bytes); await IntegrationApiProtocol.ReadAsync(input, 65536, CancellationToken.None); });
        await Reject(() => IntegrationApiProtocol.WriteAsync(stream, new string('x', IntegrationApiProtocol.MaximumMessageBytes + 1), CancellationToken.None));
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.IntegrationApi);
        var request = new IntegrationRequestMessage(Guid.NewGuid(), value);
        var envelope = new RuntimeEnvelope(worker, 0, 1, Stopwatch.GetTimestamp(), request);
        var restored = (IntegrationRequestMessage)RuntimeWire.Decode(RuntimeWire.Encode(envelope)).Payload;
        Check(restored.RequestId == request.RequestId && restored.Json == request.Json, "API IPC request roundtrip changed.");
        var response = new IntegrationResponseCommand(request.RequestId, value);
        var restoredResponse = (IntegrationResponseCommand)RuntimeWire.Decode(RuntimeWire.Encode(new RuntimeEnvelope(worker, 0, 2,
            Stopwatch.GetTimestamp(), response, Guid.NewGuid(), 1))).Payload;
        Check(restoredResponse.Json == value && restoredResponse.RequestId == request.RequestId, "API IPC response roundtrip changed.");
        await Reject(() => { _ = new RuntimeEnvelope(new WorkerIdentity(worker.AppSessionId, Guid.NewGuid(), 1, ComponentRole.Upload), 0, 1,
            Stopwatch.GetTimestamp(), request); return Task.CompletedTask; });
        foreach (var json in new[] { "{\"id\":1,\"id\":2}", "{} {}", new string('[', 40) + new string(']', 40) })
            await Reject(() => { IntegrationApiWorkerRole.ParseObject(json); return Task.CompletedTask; });
    }

    private static async Task Clients()
    {
        var name = "ls-api-regression-" + Guid.NewGuid().ToString("N");
        using var user = WindowsIdentity.GetCurrent();
        using (var securityPipe = IntegrationApiWorkerRole.CreatePipe(name + "-acl", user.User!))
        {
            var rules = securityPipe.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            Check(rules.Any(rule => rule.AccessControlType == AccessControlType.Deny && rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null))),
                "External API does not deny network identities.");
            Check(rules.Where(rule => rule.AccessControlType == AccessControlType.Allow).All(rule => rule.IdentityReference.Equals(user.User)),
                "External API grants access to a different user.");
        }
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.IntegrationApi);
        var epoch = new RuntimeClockEpoch(Guid.NewGuid(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new TestTransport(worker);
        var run = new IntegrationApiWorkerRole(name).RunAsync(new WorkerExecutionContext(worker, epoch, transport, cancellation.Token));
        var spareConnections = new List<NamedPipeClientStream>();
        try
        {
            using var slow = await Connect(name);
            await slow.WriteAsync(new byte[] { 100, 0 }, 0, 2); // An unfinished frame must not own the command loop.
            using (var bad = await Connect(name))
            {
                await IntegrationApiProtocol.WriteAsync(bad, "{\"id\":\"b\",\"method\":\"hello\",\"protocolVersion\":999999999999999999999999999999999}", cancellation.Token);
                Check((string?)((await Read(bad))["error"]?["code"]) == "UnsupportedProtocol", "An unbounded protocol number was accepted.");
            }
            using (var malformed = await Connect(name))
            {
                await malformed.WriteAsync(new byte[] { 255, 255, 255, 127 }, 0, 4);
                try { Check(await IntegrationApiProtocol.ReadAsync(malformed, IntegrationApiProtocol.MaximumMessageBytes, cancellation.Token) == null,
                    "Oversized request received data."); }
                catch (IOException) { }
            }
            using var client = await Connect(name);
            await Send(client, "hello", "hello", new JObject());
            Check((bool?)(await Read(client))["result"]?["ready"] == true, "Healthy client did not negotiate while a peer stalled.");
            await Send(client, "status", "status.get", new JObject());
            Check((string?)(await Read(client))["id"] == "status", "Query response lost correlation.");
            var burst = Stopwatch.StartNew();
            for (var index = 0; index < 5; index++) { await Send(client, "bounded", "status.get", new JObject()); await Read(client); }
            Check(burst.Elapsed >= TimeSpan.FromMilliseconds(350), "Query burst bypassed the per-client dispatch rate bound.");
            var probe = transport.Command(new WorkerCommandMessage(WorkerCommand.Probe));
            await Until(() => transport.Results.ContainsKey(probe), "Client I/O blocked owner probe.");

            using var subscriber = await Connect(name);
            await Send(subscriber, "hello", "hello", new JObject()); await Read(subscriber);
            await Send(subscriber, "sub", "events.subscribe", new JObject { ["streamId"] = "test-stream", ["afterSequence"] = 0 });
            Check((bool?)(await Read(subscriber))["result"]?["subscribed"] == true, "Subscription was not acknowledged.");
            var initial = await Read(subscriber);
            Check((long?)initial["event"]?["sequence"] == 1 && (string?)initial["type"] == "event", "Replay event was lost.");
            transport.LatestSequence = 2;
            Check((long?)(await Read(subscriber))["event"]?["sequence"] == 2, "Replay/live handoff skipped the new event.");

            using var slowWriter = await Connect(name);
            await Send(slowWriter, "hello", "hello", new JObject()); await Read(slowWriter);
            await Send(slowWriter, "blob", "blob", new JObject()); // Do not read the half-megabyte response.
            await Send(client, "responsive", "status.get", new JObject());
            Check((string?)(await Read(client))["id"] == "responsive", "A slow response reader blocked another client's request.");
            for (var index = 0; index < 4; index++) spareConnections.Add(await Connect(name));
            using (var ninth = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try { await ninth.ConnectAsync(150); throw new InvalidOperationException("API accepted more than eight clients."); }
                catch (TimeoutException) { }
                catch (IOException error) when ((error.HResult & 0xffff) == 121) { }
            }
            Check(transport.MaximumOutstanding <= IntegrationApiProtocol.MaximumClients, "Pending API requests exceeded client bound.");
            var stop = Stopwatch.StartNew(); transport.Command(new WorkerCommandMessage(WorkerCommand.Stop));
            Check(await Task.WhenAny(run, Task.Delay(2000)) == run, "Stop waited for idle external clients.");
            await run; Check(stop.Elapsed < TimeSpan.FromSeconds(2), "API shutdown exceeded the bounded stop time.");
        }
        finally
        {
            foreach (var connection in spareConnections) connection.Dispose();
            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    private static async Task<NamedPipeClientStream> Connect(string name)
    { var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous); await pipe.ConnectAsync(2000); return pipe; }
    private static Task Send(Stream stream, string id, string method, JObject arguments) => IntegrationApiProtocol.WriteAsync(stream,
        new JObject { ["id"] = id, ["method"] = method, ["params"] = arguments, ["protocolVersion"] = 1 }.ToString(Formatting.None), CancellationToken.None);
    private static async Task<JObject> Read(Stream stream)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var abort = deadline.Token.Register(() => stream.Dispose());
        return IntegrationApiWorkerRole.ParseObject(await IntegrationApiProtocol.ReadAsync(stream, IntegrationApiProtocol.MaximumMessageBytes, deadline.Token) ?? throw new IOException("Unexpected EOF."));
    }
    private static async Task Until(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException(message); await Task.Delay(5); } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException || error is JsonException) { return; }
        throw new InvalidOperationException("Malformed integration protocol input was accepted.");
    }

    private sealed class TestTransport : IWorkerTransport
    {
        private readonly WorkerIdentity _worker;
        private readonly ConcurrentQueue<RuntimeEnvelope> _commands = new ConcurrentQueue<RuntimeEnvelope>();
        private long _sequence;
        public readonly ConcurrentDictionary<Guid, CommandResultMessage> Results = new ConcurrentDictionary<Guid, CommandResultMessage>();
        public long LatestSequence = 1;
        public int MaximumOutstanding;
        public TestTransport(WorkerIdentity worker) { _worker = worker; }
        public int PendingCount => 0;
        public bool TryTakeCommand(out RuntimeEnvelope? command) => _commands.TryDequeue(out command);
        public Guid Command(RuntimeCommand payload)
        {
            var id = Guid.NewGuid(); var sequence = Interlocked.Increment(ref _sequence);
            _commands.Enqueue(new RuntimeEnvelope(_worker, 0, sequence, Stopwatch.GetTimestamp(), payload, id, sequence)); return id;
        }
        public bool TryPublish(RuntimePayload payload, long connectionGeneration, long captureQpc, Guid correlationId = default, long revision = 0,
            Guid episodeId = default, Guid lookupId = default, LookupStage stage = LookupStage.None)
        {
            if (payload is CommandResultMessage command) Results[correlationId] = command;
            if (payload is IntegrationRequestMessage request)
            {
                MaximumOutstanding = Math.Max(MaximumOutstanding, _commands.Count + 1);
                var input = IntegrationApiWorkerRole.ParseObject(request.Json); JObject result;
                if ((string?)input["method"] == "events.read")
                {
                    var after = (long?)input["params"]?["afterSequence"] ?? 0; var latest = Interlocked.Read(ref LatestSequence);
                    result = new JObject { ["streamId"] = "test-stream", ["nextSequence"] = latest, ["hasMore"] = false,
                        ["events"] = new JArray(Enumerable.Range((int)after + 1, (int)(latest - after)).Select(sequence => new JObject { ["sequence"] = sequence })) };
                }
                else if ((string?)input["method"] == "blob") result = new JObject { ["payload"] = new string('x', 500000) };
                else result = new JObject { ["ready"] = true };
                Command(new IntegrationResponseCommand(request.RequestId, new JObject { ["id"] = input["id"]!.DeepClone(), ["result"] = result }.ToString(Formatting.None)));
            }
            return true;
        }
    }

    private sealed class ChunkedStream : MemoryStream
    {
        public ChunkedStream(byte[] bytes) : base(bytes) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => base.ReadAsync(buffer, offset, Math.Min(1, count), cancellationToken);
    }
}
