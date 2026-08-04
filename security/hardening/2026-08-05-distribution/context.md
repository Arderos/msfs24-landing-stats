# Local evidence context

Analysis date: 2026-08-05. Source root: `D:\Users\ezaytsev\Documents\git\fallensky\msfs-landing-stats`.
Baseline revision: `6bfd769` (`Release v0.7.0`) plus the format-2 updater remediation working tree.

The evidence collection digest is SHA-256 `c79b70529ffee0f0d89d216eae67d666e0d01e4e282a1dac29140b9bd8d13b9c` over these canonical `path sha256` lines:

```text
.github/workflows/build.yml ffab63064a1e7fc0d11dc71feace194573e1c1cf7677222dd277956347a529ad
build-app.ps1 a2672e619a7750ed9ba9ea67f32c350a2c8681685c431a3c6546b455fe37425d
src/LandingStats.App.Updater/Program.cs b204fb53efcf3510ad24936b22207b7c27f87337b13c873c5b495043c55e8627
src/LandingStats.UpdateProtocol/UpdateProtocol.cs a9a714b2d8470eed6987e1d2e0f225bfa703d16d67d2c548da666858385d4e39
src/LandingStats.App/MainWindow.xaml.cs c9d102dc6f41d6e6fed3f18ee6d6cb7df4e8ad2c965a513c3972bfadaa7b770a
src/LandingStats.App/Storage/RawCaptureRepository.cs 4c9725235586df66365b8cc3255af1b60118f394a4290070e6fb36d13bd3856f
src/LandingStats.App/TelemetryUpload/TelemetryUploadClient.cs 3f48c63ac1224a8643a5188a0203649c68e9140a7deac9c31e655c17f3feeb69
src/LandingStats.App/TelemetryUpload/TelemetryUploadIdentityStore.cs f722cd68d1de72983931b8dd72b3f6ef138e056557f28db54f002d1ea7000299
src/LandingStats.App/Updates/ReleaseUpdater.cs fb947bf3998bf5953acdd78119a375bc59e91541baa03a2a9e2595faca073451
server/telemetry-ingest/Dockerfile bc5556e5eca5e4deb204ccf30334d69d28c800cbfdf42d2a4a8a2f71127b9262
server/telemetry-ingest/docker-compose.yml d404fa6d115776092941bc0f76917d75acab166399f9cc5b9738ba0461c9fa00
server/telemetry-ingest/app/main.py 95a8f40797bd0a1cd3fed15cc77da84c133b75c2d4d366e3a6cbbccd3f5beebe
server/telemetry-ingest/app/store.py cf014b6ff967d2d90bed57ee78037fc1c50929b6767eda0d7de2f4f113439f0e
server/telemetry-ingest/app/validation.py 8f6e1de48696b4dc0af1e6ef4040eb4d3754799b94f93439b35b38943f9e9771
```

Deployment observations used in the review: Ubuntu 24.04 host `astr-scad-01`; Docker Engine 29.7.1 and Compose 5.4.0; a dedicated 30 GB ext4 filesystem mounted at `/srv/msfs-landing-telemetry`; Compose project at `/opt/msfs-landing-telemetry`; no host-published API port; Cloudflare Tunnel public hostname `msfsls.fallensky.us`. Tokens and secret material are intentionally excluded.
