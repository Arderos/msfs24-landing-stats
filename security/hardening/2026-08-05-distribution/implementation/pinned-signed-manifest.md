# Implementation Plan: Pinned Signed Release Manifest

## Selected Design And Constraints

GitHub Actions signs a five-line canonical manifest with RSA PKCS#1 v1.5/SHA-256. The app pins only the public key, verifies the signed size/hash, verifies the embedded application bundle, and replaces the exited launcher atomically.

## Source Revision And Drift Check

Evidence digest: `00a602196a04684087c9dd0e67a16bf03c2874926ec10e6c60c95cf0894a7ce2`. The launcher handoff and workflow were rechecked, and `RELEASE_SIGNING_KEY_PKCS8_B64` is provisioned as a GitHub Actions repository secret.

## Affected Components

`.github/workflows/build.yml`, `src/LandingStats.App/Updates/ReleaseUpdater.cs`, `src/LandingStats.App.Launcher/Program.cs`, and release tests.

## Ordered Work Packages

Provision `RELEASE_SIGNING_KEY_PKCS8_B64`, back up the private key offline, build the bootstrap release, verify all three assets, and then exercise an update from that bootstrap to a newer signed test release.

## Compatibility And Migration

v0.6 has no updater and requires one final manual install. Later builds check the stable GitHub `releases/latest/download` asset URLs.

## Tactical Protections During Migration

The workflow must fail closed when the signing secret is absent. Never fall back to unsigned installation; network errors leave the current launcher untouched.

## Tests And Security Validation

Verify a known signed fixture and reject manifest, signature, size, hash, and bundle tampering. Test writable and non-writable launcher directories and interrupted replacement.

## Performance And Resource Benchmarks

Measure startup metadata latency and streaming hash time for the current EXE. Metadata failure must remain non-blocking for normal application startup.

## Rollout And Rollback

Retain `<launcher>.previous`. For a bad signed release, remove it from `latest`, restore the previous launcher, and publish a corrected higher version.

## Acceptance Criteria

The bootstrap build runs normally without metadata, a newer signed release installs for the next start, and every tampering case leaves the current executable unchanged.

## Open Decisions

Choose the offline backup location and document release-key rotation.
