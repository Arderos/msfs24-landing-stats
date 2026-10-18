using System;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Runtime;

// Test-only factory; this assembly is never included in a published payload.
public static class FixtureRoleFactory
{
    // Fault-injection uses the production role and transport implementation with
    // an in-memory HTTP peer. It cannot contact any external service.
    public static void RunChild() => Thread.Sleep(Timeout.Infinite);
    public static IWorkerRole CreateFixture(ComponentRole role, string fixture)
    {
        if (role == ComponentRole.IntegrationApi && fixture.StartsWith("integration-api-", StringComparison.Ordinal))
            return new LandingStats.Workers.IntegrationApi.IntegrationApiWorkerRole(fixture);
        if (role == ComponentRole.Controllers && fixture == "controllers-local")
            return new ControllersWorkerRole(() => new Telemetry.WindowsJoystickReader(new FixtureJoystick()));
        if (!fixture.StartsWith("upload-", StringComparison.Ordinal) && !fixture.StartsWith("cloud-", StringComparison.Ordinal) && !fixture.StartsWith("analysis-", StringComparison.Ordinal)) return new TransportFixtureRole(fixture);
        if (role == ComponentRole.Analysis && fixture == "analysis-local")
            return new AnalysisWorkerRole(() => new Telemetry.FlightModelGeometryResolver(Array.Empty<string>(), false));
        if (role == ComponentRole.CloudSync && (fixture == "cloud-auth" || fixture == "cloud-auth-hang" || fixture == "cloud-auth-late"))
            return new CloudWorkerRole(authorize: async (tokens, _) =>
            {
                if (fixture == "cloud-auth-hang") return await new TaskCompletionSource<string?>().Task.ConfigureAwait(false);
                if (fixture == "cloud-auth-late") await Task.Delay(600).ConfigureAwait(false);
                tokens.Save("fixture-authorized", "fixture@example.invalid"); return "fixture@example.invalid";
            });
        if (role == ComponentRole.CloudSync && (fixture == "cloud-sync" || fixture == "cloud-sync-lost"))
            return new CloudWorkerRole(tokens => new CloudSyncFixture(tokens, fixture == "cloud-sync-lost"));
        if (role == ComponentRole.CloudSync && (fixture == "cloud-ok" || fixture == "cloud-hang" || fixture == "cloud-late" || fixture == "cloud-refresh" || fixture == "cloud-invalid" || fixture == "cloud-unconfigured"))
            return new CloudWorkerRole(tokens => new CloudApiFixture(fixture, tokens));
        if (role != ComponentRole.Upload || fixture != "upload-ok" && fixture != "upload-hang" && fixture != "upload-late" && fixture != "upload-reject" && fixture != "upload-defer" && fixture != "upload-codec")
            throw new ArgumentException("Unknown isolated role fixture.");
        return new UploadWorkerRole(() => new FixtureHttpHandler(fixture));
    }
    private sealed class FixtureJoystick : Telemetry.WindowsJoystickReader.IJoystickApi
    {
        private uint _sample;
        public uint DeviceCount => 1;
        public bool TryGetState(uint joystickId, out Telemetry.WindowsJoystickReader.JoyInfoEx state)
        { state = new Telemetry.WindowsJoystickReader.JoyInfoEx { XPosition = 200, YPosition = ++_sample % 1000 }; return joystickId == 0; }
        public bool TryGetCapabilities(uint joystickId, out Telemetry.WindowsJoystickReader.JoyCaps capabilities)
        { capabilities = new Telemetry.WindowsJoystickReader.JoyCaps { ProductId = 1, ProductName = "Fixture stick", AxisCount = 2,
            XMaximum = 1000, YMaximum = 1000, RegistryKey = "", OemDriver = "" }; return joystickId == 0; }
    }
    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        private readonly string _mode;
        public FixtureHttpHandler(string mode) { _mode = mode; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/config", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"protocol\":1,\"registration_mode\":\"open\",\"telemetry_schema\":" +
                    TelemetryCsv.SchemaVersion + ",\"capture_kinds\":[\"automatic_landing\",\"bug_report\"],\"decision_journal_formats\":[1],\"controller_catalog_formats\":[1],\"source_state_formats\":[1]" +
                    (_mode == "upload-codec" ? ",\"archive_formats\":[1,2]" : "") + "}") };
            if (request.RequestUri.AbsolutePath.EndsWith("/enroll", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"accepted\",\"capture_id\":\"0123456789abcdef0123456789abcdef\"}") };
            if (!request.RequestUri.AbsolutePath.EndsWith("/captures", StringComparison.Ordinal)) throw new InvalidOperationException("Unknown fixture HTTP request.");
            if (_mode == "upload-hang") return await new TaskCompletionSource<HttpResponseMessage>().Task.ConfigureAwait(false);
            if (_mode == "upload-late") await Task.Delay(600).ConfigureAwait(false);
            if (_mode == "upload-codec")
            {
                using var input = await request.Content!.ReadAsStreamAsync().ConfigureAwait(false);
                using var archive = new LandingStats.Archives.CaptureArchiveReader(input);
                if (archive.Format != 2) throw new System.IO.InvalidDataException("Codec fixture requires a versioned capture.");
                foreach (var entry in archive.Entries) { using var decoded = archive.Open(entry.Name); decoded.CopyTo(System.IO.Stream.Null); }
            }
            return new HttpResponseMessage(_mode == "upload-reject" ? (HttpStatusCode)422 : _mode == "upload-defer" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent("{\"status\":\"accepted\",\"capture_id\":\"0123456789abcdef0123456789abcdef\"}") };
        }
    }
}
