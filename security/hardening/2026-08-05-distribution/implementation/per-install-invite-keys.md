# Implementation Plan: Per-install Keys With Invitation Enrollment

## Selected Design And Constraints

The selected design uses a DPAPI-protected RSA key per Windows account, invitation enrollment, signed request metadata, replay state, bounded streaming, exact schema-v5 validation, and an isolated Docker Compose ingress. It cannot attest genuine simulator execution.

## Source Revision And Drift Check

Evidence digest: `00a602196a04684087c9dd0e67a16bf03c2874926ec10e6c60c95cf0894a7ce2`. The final inventory in `../context.md` includes the client identity, byte-budget store, container, and deployed ingress policy; no relevant source drift remains.

## Affected Components

`src/LandingStats.App/TelemetryUpload`, `src/LandingStats.App/Storage/RawCaptureRepository.cs`, `src/LandingStats.App/MainWindow.xaml.cs`, and `server/telemetry-ingest`.

## Ordered Work Packages

Complete consent/enrollment UI, keep network work off SimConnect callbacks, provision Compose secrets, route Cloudflare to `telemetry-api:8080`, issue invitations, and add operational metrics/backup.

## Compatibility And Migration

Only new DEBUG RAW chunks use the telemetry queue. Historical `Raw Captures` directories are not scanned or uploaded.

## Tactical Protections During Migration

Keep registration in `invite` mode, retain 16 MB compressed and 64 MB expanded bounds, reject unknown schemas, and do not publish an API port.

## Tests And Security Validation

Run unit ZIP/signature tests, public signed E2E, invalid signature, replay, duplicate hash, oversized body, unexpected entry, NaN, bad row width, revoked key, and queue interruption cases.

## Performance And Resource Benchmarks

Measure 1/8/16 MB upload latency, API RSS under concurrent uploads, client UI responsiveness, and disk retention behavior. The acceptance threshold is no SimConnect callback delay and API RSS below the 256 MB container limit.

## Rollout And Rollback

Start with maintainer-owned test installs, then a small invitation cohort. Roll back by disabling the client feature and stopping this isolated Compose project; preserve queued files until a deliberate recovery decision.

## Acceptance Criteria

Public E2E succeeds and is cleaned from the corpus; rejects never enter `accepted`; ACK removes the local queue copy; offline files retry; accepted and attempted-byte quotas, retention, and disk reserve are enforced; a revoked key cannot upload.

## Open Decisions

Publish privacy/retention text and choose monitoring/alert destinations.
