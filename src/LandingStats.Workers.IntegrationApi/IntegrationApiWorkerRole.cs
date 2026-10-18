using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LandingStats.Workers.IntegrationApi;

/// <summary>Clients and their deadlines never own the worker's command/health loop.</summary>
internal sealed class IntegrationApiWorkerRole : IWorkerRole
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _pending = new ConcurrentDictionary<Guid, TaskCompletionSource<string>>();
    private readonly ConcurrentQueue<IntegrationRequestMessage> _requests = new ConcurrentQueue<IntegrationRequestMessage>();
    private readonly string? _testPipeName;
    private long _completed;

    internal IntegrationApiWorkerRole(string? testPipeName = null) { _testPipeName = testPipeName; }

    public async Task RunAsync(WorkerExecutionContext context)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        using var user = WindowsIdentity.GetCurrent();
        var sid = user.User ?? throw new InvalidOperationException("The API worker has no Windows user identity.");
        var name = _testPipeName ?? IntegrationApiProtocol.PipeNameForUser(sid.Value);
        var listeners = Enumerable.Range(0, IntegrationApiProtocol.MaximumClients)
            .Select(_ => ServeSlotAsync(name, sid, lifetime.Token)).ToArray();
        var owner = new WorkerOwner(context);
        long observed = 0;
        try
        {
            while (true)
            {
                // An unexpected listener failure is a role failure; the supervisor replaces it.
                foreach (var listener in listeners) if (listener.IsCompleted) await listener.ConfigureAwait(false);
                var completed = Interlocked.Read(ref _completed);
                owner.Progress(completed != observed, DependencyWait.Idle, HandleCommand);
                observed = completed;
                if (owner.Stopping) return;
                while (_requests.TryDequeue(out var request))
                    if (_pending.ContainsKey(request.RequestId)) owner.Publish(request, Stopwatch.GetTimestamp());
                await Task.Delay(20, lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            lifetime.Cancel();
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
            // Disposing a slot's pipe aborts an overlapped read/write, including on net48.
            try { await Task.WhenAll(listeners).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private CommandResultMessage HandleCommand(RuntimeEnvelope envelope)
    {
        if (!(envelope.Payload is IntegrationResponseCommand response)) return new CommandResultMessage(CommandOutcome.Rejected, "unsupported_integration_command");
        if (_pending.TryGetValue(response.RequestId, out var pending)) pending.TrySetResult(response.Json);
        // A timed-out or disconnected client must not make the parent's response command fail.
        return new CommandResultMessage(CommandOutcome.Completed, "integration_response_received");
    }

    private async Task ServeSlotAsync(string name, SecurityIdentifier user, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var pipe = CreatePipe(name, user);
            using var cancellation = token.Register(() => pipe.Dispose());
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                await ServeClientAsync(pipe, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ObjectDisposedException || error is OperationCanceledException ||
                error is JsonException || error is DecoderFallbackException || error is ArgumentException)
            {
                // A broken, malformed, slow or cancelled external connection is isolated to its slot.
                token.ThrowIfCancellationRequested();
            }
        }
    }

    internal static NamedPipeServerStream CreatePipe(string name, SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(name, PipeDirection.InOut, IntegrationApiProtocol.MaximumClients,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, security, HandleInheritability.None);
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var negotiated = false;
        var dispatch = Stopwatch.StartNew();
        while (true)
        {
            string? json;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(negotiated ? 60 : 10));
                using var abort = deadline.Token.Register(() => pipe.Dispose());
                json = await IntegrationApiProtocol.ReadAsync(pipe, IntegrationApiProtocol.MaximumRequestBytes, deadline.Token).ConfigureAwait(false);
            }
            if (json == null) return;
            JObject request;
            try { request = ParseObject(json); }
            catch (JsonException)
            {
                await WriteAsync(pipe, Error(null, "InvalidRequest", "A JSON object is required."), token).ConfigureAwait(false);
                return;
            }
            var id = request["id"]?.Type == JTokenType.String ? (string?)request["id"] : null;
            var method = request["method"]?.Type == JTokenType.String ? (string?)request["method"] : null;
            if (string.IsNullOrEmpty(id) || id!.Length > 128 || string.IsNullOrEmpty(method) || method!.Length > 64 ||
                (request["params"] != null && request["params"]!.Type != JTokenType.Object))
            {
                await WriteAsync(pipe, Error(null, "InvalidRequest", "id and method must be bounded nonempty strings; params must be an object."), token).ConfigureAwait(false);
                return;
            }
            if (request["protocolVersion"]?.Type != JTokenType.Integer || request["protocolVersion"]!.ToString() != "1")
            {
                await WriteAsync(pipe, Error(id, "UnsupportedProtocol", "This endpoint requires protocolVersion 1."), token).ConfigureAwait(false);
                return;
            }
            if (!negotiated && method != "hello")
            {
                await WriteAsync(pipe, Error(id, "HandshakeRequired", "Send hello before other requests."), token).ConfigureAwait(false);
                return;
            }
            // A local client can otherwise repeatedly deserialize large history
            // records as quickly as the private IPC can answer. Bound each
            // connection to ten queries/second without queuing ahead of it.
            var throttle = 100 - (int)Math.Min(100, dispatch.ElapsedMilliseconds);
            if (negotiated && throttle > 0) await Task.Delay(throttle, token).ConfigureAwait(false);
            dispatch.Restart();
            if (method == "events.subscribe")
            {
                await SubscribeAsync(pipe, request, token).ConfigureAwait(false);
                return; // The connection remains dedicated to this subscription until it ends.
            }
            var response = await RequestAsync(request, token).ConfigureAwait(false);
            await WriteAsync(pipe, response, token).ConfigureAwait(false);
            if (method == "hello" && ParseObject(response)["error"] == null) negotiated = true;
        }
    }

    private async Task SubscribeAsync(NamedPipeServerStream pipe, JObject request, CancellationToken token)
    {
        var id = (string)request["id"]!;
        var arguments = (JObject?)request["params"]?.DeepClone() ?? new JObject();
        arguments["limit"] = 32;
        var poll = new JObject { ["id"] = id, ["method"] = "events.read", ["protocolVersion"] = IntegrationApiProtocol.Version, ["params"] = arguments };
        var first = true;
        var heartbeat = Stopwatch.StartNew();
        while (true)
        {
            var response = ParseObject(await RequestAsync(poll, token).ConfigureAwait(false));
            if (response["error"] != null)
            {
                await WriteAsync(pipe, response.ToString(Formatting.None), token).ConfigureAwait(false);
                return;
            }
            var result = response["result"] as JObject ?? throw new InvalidDataException("Missing integration event result.");
            var streamId = (string?)result["streamId"] ?? throw new InvalidDataException("Missing integration stream identity.");
            var events = result["events"] as JArray ?? throw new InvalidDataException("Missing integration events.");
            var next = (long?)result["nextSequence"] ?? throw new InvalidDataException("Missing integration event cursor.");
            if (first)
            {
                var acknowledgement = new JObject { ["id"] = id, ["result"] = new JObject { ["subscribed"] = true, ["streamId"] = streamId,
                    ["afterSequence"] = arguments["afterSequence"]?.DeepClone() ?? new JValue(((long?)result["oldestSequence"] ?? 1) - 1) } };
                await WriteAsync(pipe, acknowledgement.ToString(Formatting.None), token).ConfigureAwait(false);
                first = false;
            }
            foreach (var item in events)
            {
                await WriteAsync(pipe, new JObject { ["type"] = "event", ["subscriptionId"] = id, ["streamId"] = streamId,
                    ["event"] = item.DeepClone() }.ToString(Formatting.None), token).ConfigureAwait(false);
            }
            arguments["streamId"] = streamId; arguments["afterSequence"] = next;
            if (heartbeat.Elapsed >= TimeSpan.FromSeconds(15))
            {
                await WriteAsync(pipe, new JObject { ["type"] = "heartbeat", ["subscriptionId"] = id, ["streamId"] = streamId,
                    ["nextSequence"] = next }.ToString(Formatting.None), token).ConfigureAwait(false);
                heartbeat.Restart();
            }
            if ((bool?)result["hasMore"] != true) await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    private async Task<string> RequestAsync(JObject request, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("API request identity collision.");
        try
        {
            _requests.Enqueue(new IntegrationRequestMessage(id, request.ToString(Formatting.None)));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var cancellation = deadline.Token.Register(() => completion.TrySetCanceled());
            try { return await completion.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return Error((string?)request["id"], "Timeout", "The runtime did not answer within the request deadline."); }
        }
        finally { _pending.TryRemove(id, out _); Interlocked.Increment(ref _completed); }
    }

    private static async Task WriteAsync(NamedPipeServerStream pipe, string json, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var abort = deadline.Token.Register(() => pipe.Dispose());
        await IntegrationApiProtocol.WriteAsync(pipe, json, deadline.Token).ConfigureAwait(false);
    }

    internal static JObject ParseObject(string json)
    {
        using var text = new StringReader(json);
        using var reader = new JsonTextReader(text) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
        var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new JsonReaderException("Trailing JSON content is not allowed.");
        return value;
    }

    private static string Error(string? id, string code, string message) => new JObject { ["id"] = id == null ? JValue.CreateNull() : new JValue(id),
        ["error"] = new JObject { ["code"] = code, ["message"] = message } }.ToString(Formatting.None);
}
