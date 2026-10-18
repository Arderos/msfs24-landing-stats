using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.App.Runtime;
using LandingStats.App.TelemetryUpload;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class OperationLifetimeRegressionTests
{
    internal static void RunAll()
    {
        var lifetime = new OperationLifetime();
        var first = lifetime.TryEnter()!;
        var second = lifetime.TryEnter()!;
        var stopped = lifetime.Stop();
        Require(lifetime.TryEnter() == null && !stopped.IsCompleted, "Stop rejects new work and waits for every accepted owner.");
        first.Dispose(); first.Dispose();
        Require(!stopped.IsCompleted, "A duplicate release cannot hide another live owner.");
        second.Dispose();
        Require(stopped.Wait(TimeSpan.FromSeconds(2)) && ReferenceEquals(stopped, lifetime.Stop()), "Stop completion is shared and final.");

        foreach (var stage in new[] { "config", "enroll", "captures" })
        foreach (var outcome in new[] { "success", "error", "cancel" })
            DelayedOperation(stage, outcome, external: false, pending: false, useDispose: stage == "config" && outcome == "success");
        DelayedOperation("config", "success", external: true, pending: false, useDispose: false);
        DelayedOperation("enroll", "success", external: true, pending: false, useDispose: false);
        DelayedOperation("config", "success", external: true, pending: true, useDispose: false);
    }

    private static void DelayedOperation(string stage, string outcome, bool external, bool pending, bool useDispose)
    {
        var root = Path.Combine(Path.GetTempPath(), "lifetime-" + Guid.NewGuid().ToString("N"));
        var queue = Path.Combine(root, "queue");
        Directory.CreateDirectory(queue);
        var path = Path.Combine(queue, "fixture_bug_raw.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("session.txt").Open()))
                writer.WriteLine("telemetry_schema=" + TelemetryCsv.SchemaVersion);
            using (var writer = new StreamWriter(archive.CreateEntry("telemetry.csv").Open()))
                writer.WriteLine(TelemetryCsv.Header);
        }
        var original = File.ReadAllBytes(path);
        var oldEndpoint = Environment.GetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL");
        Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", "https://lifetime.example.test/");
        var handler = new DelayedHandler(stage, outcome);
        var client = new TelemetryUploadClient(queue, handler, Path.Combine(root, "identity"));
        Task caller = Task.CompletedTask;
        try
        {
            if (external)
                caller = pending ? client.PreparePendingReportsUntilReadyAsync(CancellationToken.None)
                    : client.PrepareAsync(CancellationToken.None);
            else Require(client.Enqueue(path), "Capture is accepted before shutdown.");
            Require(handler.Entered.Wait(TimeSpan.FromSeconds(5)), "The selected HTTP phase must be in flight.");

            if (useDispose) client.Dispose();
            var stop = client.StopAsync();
            Require(ReferenceEquals(stop, client.StopAsync()), "Concurrent stop callers share completion.");
            Require(!stop.IsCompleted && handler.DisposeCount == 0, "A live operation retains HttpClient beyond the shutdown deadline.");
            Require(!client.Enqueue(path), "Shutdown rejects new uploads.");
            Require(client.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult().State == TelemetryPreparationState.Unavailable,
                "Shutdown rejects external preparation without touching disposed state.");
            client.SetAutomaticUploadEnabled(false);
            client.EnqueueExisting();
            handler.Release.TrySetResult(true);
            Require(stop.Wait(TimeSpan.FromSeconds(5)), "All accepted operations eventually drain.");
            try { caller.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            var worker = (Task)typeof(TelemetryUploadClient).GetField("_worker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
            Require(worker.IsCompleted && !worker.IsFaulted, "Late HTTP completion cannot fault the upload loop with disposed resources.");
            Require(handler.DisposeCount == 1, "The last owner releases the HTTP handler exactly once.");
            Require(File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(original), "Late results after stop leave the exact archive available for retry.");
        }
        finally
        {
            handler.Release.TrySetResult(true);
            client.StopAsync().GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("MSFS_LANDING_STATS_TELEMETRY_URL", oldEndpoint);
            Directory.Delete(root, true);
        }
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        private readonly string _stage;
        private readonly string _outcome;
        internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        internal readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount;
        internal DelayedHandler(string stage, string outcome) { _stage = stage; _outcome = outcome; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/v1/" + _stage)
            {
                Entered.Set();
                await Release.Task.ConfigureAwait(false); // Deliberately ignores cancellation until released.
                if (_outcome == "cancel") throw new OperationCanceledException(token);
                if (_outcome == "error") throw new HttpRequestException("Injected late error.");
            }
            var json = route == "/v1/config"
                ? "{\"protocol\":1,\"registration_mode\":\"open\",\"telemetry_schema\":" + TelemetryCsv.SchemaVersion + "}"
                : "{\"status\":\"accepted\",\"capture_id\":\"0123456789abcdef0123456789abcdef\"}";
            return new HttpResponseMessage(route == "/v1/config" ? HttpStatusCode.OK : HttpStatusCode.Created)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { Interlocked.Increment(ref DisposeCount); base.Dispose(disposing); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
