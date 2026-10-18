using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.Storage;
using LandingStats.Runtime.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LandingStats.App.RegressionTests;

internal static class IntegrationApiLifecycleRegressionTests
{
    public static void RunAll() => Task.Run(Run).GetAwaiter().GetResult();
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ls-api-lifecycle-" + Guid.NewGuid().ToString("N"));
        var pipeName = "integration-api-" + Guid.NewGuid().ToString("N");
        var repository = new LandingRepository(Path.Combine(root, "history"));
        var escalations = new ConcurrentQueue<string>();
        var options = new ComponentRecoveryOptions { CheckInterval = TimeSpan.FromMilliseconds(40), InitialBackoff = TimeSpan.FromMilliseconds(20),
            MaximumBackoff = TimeSpan.FromMilliseconds(100), OperationDeadline = TimeSpan.FromSeconds(30), Startup = TimeSpan.FromSeconds(10) };
        using var runtime = new LandingRuntime(Path.Combine(root, "runtime"), AppDomain.CurrentDomain.BaseDirectory,
            Path.Combine(root, "identity"), repository, new object(), (_, __) => Task.CompletedTask,
            (component, _) => escalations.Enqueue(component), (role, _) => role == ComponentRole.IntegrationApi ? pipeName : "normal", options);
        var done = false;
        try
        {
            await runtime.StartAsync(false);
            Check(!runtime.IntegrationApiEnabled && runtime.IntegrationApi.CurrentHost == null && !Directory.Exists(Path.Combine(runtime.Root, "integration")),
                "Disabled API created its data store or process.");
            await runtime.SetIntegrationApiEnabledAsync(true);
            await Until(() => runtime.IntegrationApi.CurrentHost?.LastHealth != null, "Enabled API process did not become ready.", runtime);
            var firstHost = runtime.IntegrationApi.CurrentHost!;
            using (var first = await Connect(pipeName))
            {
                await Request(first, "hello");
                var status = await Request(first, "status.get");
                Check(status["result"] != null && status["error"] == null, "Actual API process did not return status.");
                await firstHost.TerminateAsync(TimeSpan.FromSeconds(3));
                await Until(() => runtime.IntegrationApi.CurrentHost != null && runtime.IntegrationApi.CurrentHost != firstHost &&
                    runtime.IntegrationApi.CurrentHost.LastHealth != null, "API process did not recover after exit.", runtime);
                Check(firstHost.Exit.IsCompleted, "Replacement API overlapped the terminated generation.");
            }
            var replacement = runtime.IntegrationApi.CurrentHost!;
            using (var active = await Connect(pipeName))
            {
                await Request(active, "hello"); await Request(active, "status.get");
                await runtime.SetIntegrationApiEnabledAsync(false);
                Check(!runtime.IntegrationApiEnabled && runtime.IntegrationApi.CurrentHost == null && replacement.Exit.IsCompleted,
                    "Disabling API did not retire its owned process.");
                using var eofDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var abort = eofDeadline.Token.Register(() => active.Dispose());
                try { Check(await IntegrationApiProtocol.ReadAsync(active, IntegrationApiProtocol.MaximumMessageBytes, eofDeadline.Token) == null,
                    "Disabled API kept its client connection usable."); }
                catch (IOException) { }
            }
            await runtime.SetIntegrationApiEnabledAsync(true);
            await Until(() => runtime.IntegrationApi.CurrentHost?.LastHealth != null, "API did not restart after re-enable.", runtime);
            var final = runtime.IntegrationApi.CurrentHost!;
            Check(final.Identity.Generation > replacement.Identity.Generation, "Re-enable reused a stale worker generation.");
            using (var active = await Connect(pipeName)) { await Request(active, "hello"); await Request(active, "landings.list"); }
            await Stop(runtime);
            Check(final.Exit.IsCompleted && runtime.Health.All(item => item.State == SupervisionState.Stopped), "Runtime shutdown left API ownership alive.");
            Check(escalations.IsEmpty, "A recoverable API failure escalated the whole runtime: " + string.Join(", ", escalations));
            done = true;
        }
        finally
        {
            await Stop(runtime);
            if (done && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    private static async Task<NamedPipeClientStream> Connect(string name)
    { var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous); await pipe.ConnectAsync(3000); return pipe; }
    private static async Task<JObject> Request(Stream pipe, string method)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var abort = deadline.Token.Register(() => pipe.Dispose());
        var id = Guid.NewGuid().ToString("N");
        await IntegrationApiProtocol.WriteAsync(pipe, new JObject { ["protocolVersion"] = 1, ["id"] = id, ["method"] = method,
            ["params"] = new JObject() }.ToString(Formatting.None), deadline.Token);
        var text = await IntegrationApiProtocol.ReadAsync(pipe, IntegrationApiProtocol.MaximumMessageBytes, deadline.Token) ?? throw new IOException("API disconnected before answering.");
        var response = JObject.Parse(text);
        Check((string?)response["id"] == id && response["error"] == null, "API request failed: " + response.ToString(Formatting.None));
        return response;
    }
    private static async Task Until(Func<bool> condition, string message, LandingRuntime runtime)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(message + " " + string.Join("; ", runtime.Health.Select(item => item.Component + ":" + item.State + ":" + item.Reason)));
            await Task.Delay(20);
        }
    }
    private static async Task Stop(LandingRuntime runtime)
    {
        var stop = runtime.StopAsync();
        Check(await Task.WhenAny(stop, Task.Delay(15000)) == stop, "API test runtime failed to stop in its deadline.");
        await stop;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
