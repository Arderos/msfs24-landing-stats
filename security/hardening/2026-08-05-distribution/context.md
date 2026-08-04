# Local evidence context

Analysis date: 2026-08-05. Source root: `D:\Users\ezaytsev\Documents\git\fallensky\msfs-landing-stats`.
Baseline revision: `0648d11` (`Release v0.6.0 closure reconstruction`) plus the updater and telemetry-ingress working tree.

The evidence collection digest is SHA-256 `00a602196a04684087c9dd0e67a16bf03c2874926ec10e6c60c95cf0894a7ce2` over these canonical `path sha256` lines:

```text
.github/workflows/build.yml ef9356063ea739046888818c53280c9450ddc47a017e984c5c9484c552fb4645
src/LandingStats.App.Launcher/Program.cs 2064f8ee1a669848dd4b80c446750e67fd1c97e0657c31fd7c00b8fda6b1c160
src/LandingStats.App/Storage/RawCaptureRepository.cs 4c9725235586df66365b8cc3255af1b60118f394a4290070e6fb36d13bd3856f
src/LandingStats.App/TelemetryUpload/TelemetryUploadClient.cs 3f48c63ac1224a8643a5188a0203649c68e9140a7deac9c31e655c17f3feeb69
src/LandingStats.App/TelemetryUpload/TelemetryUploadIdentityStore.cs f722cd68d1de72983931b8dd72b3f6ef138e056557f28db54f002d1ea7000299
src/LandingStats.App/Updates/ReleaseUpdater.cs 14632bfddf0880c897b03ae898267c36ae8b91fdd524c9b91125f110514c14ab
server/telemetry-ingest/Dockerfile bc5556e5eca5e4deb204ccf30334d69d28c800cbfdf42d2a4a8a2f71127b9262
server/telemetry-ingest/docker-compose.yml d404fa6d115776092941bc0f76917d75acab166399f9cc5b9738ba0461c9fa00
server/telemetry-ingest/app/main.py 95a8f40797bd0a1cd3fed15cc77da84c133b75c2d4d366e3a6cbbccd3f5beebe
server/telemetry-ingest/app/store.py cf014b6ff967d2d90bed57ee78037fc1c50929b6767eda0d7de2f4f113439f0e
server/telemetry-ingest/app/validation.py 8f6e1de48696b4dc0af1e6ef4040eb4d3754799b94f93439b35b38943f9e9771
```

Deployment observations used in the review: Ubuntu 24.04 host `astr-scad-01`; Docker Engine 29.7.1 and Compose 5.4.0; a dedicated 30 GB ext4 filesystem mounted at `/srv/msfs-landing-telemetry`; Compose project at `/opt/msfs-landing-telemetry`; no host-published API port; Cloudflare Tunnel public hostname `msfsls.fallensky.us`. Tokens and secret material are intentionally excluded.
