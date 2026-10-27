# Documentation

These pages describe the current application and supported data contracts.
Start with the [project README](../README.md) for installation, building and settings.

| Reference | Contents |
| --- | --- |
| [Architecture](architecture.md) | Process ownership, recording, storage and controller profiles |
| [Measurement methodology](methodology.md) | Timing, landing rates, G-load, reconstruction and measurement limits |
| [Landing record format](format-v7.md) | Saved history, replay data, control assignments and backwards compatibility |
| [Capture archive format](capture-archive-format.md) | Diagnostic ZIP/Zstandard members, CSV versions and reader limits |
| [Replay recording policy](replay-recording-policy.md) | Recording boundaries during simulator and external replay |
| [Local integration API](integration-api.md) | Discovery, queries, subscriptions and the Python client |
| [Update compatibility](UpdateCompatibility.md) | Signed manifests, the legacy bridge and publication checks |

[GitHub Issues](https://github.com/Arderos/msfs24-landing-stats/issues) tracks current work.
[CHANGELOG.md](../CHANGELOG.md) records published changes;
[PRIVACY.md](../PRIVACY.md) describes data handling.
Contributor rules, including branches and documentation-only CI skips, are in
[CONTRIBUTING.md](../CONTRIBUTING.md). Server deployment is documented in the
[receiver README](../server/telemetry-ingest/README.md).
