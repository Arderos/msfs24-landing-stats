# Implementation Plan: Pinned Signed Release Manifest

## Selected Design And Constraints

GitHub Actions signs an eight-line format-2 manifest with RSA PKCS#1
v1.5/SHA-256. It binds the release version plus the exact name, size, and
SHA-256 of both `MSFS-Landing-Stats.zip` and
`MSFS-Landing-Stats.Updater.exe`. The client contains only the public key.

The application verifies the manifest and updater before launch. The updater
then fetches the same signed manifest from the immutable versioned release URL,
verifies its own signed size/hash, streams and verifies the package, validates
the exact five-file archive, and checks the assembly version before replacing
anything.

## Source Revision And Drift Check

The evidence digest in `../context.md` covers the shared protocol, application
handoff, standalone updater, workflow, and telemetry ingress. The release
signing secret is provisioned in GitHub Actions; the private key is absent from
the repository and application binaries.

## Affected Components

`.github/workflows/build.yml`, `src/LandingStats.UpdateProtocol`,
`src/LandingStats.App/Updates/ReleaseUpdater.cs`,
`src/LandingStats.App.Updater`, `build-app.ps1`, and regression tests.

## Ordered Work Packages

Build a normal portable ZIP and standalone updater, publish both under stable
asset names, generate and sign the canonical manifest, perform a one-time manual
bootstrap from the removed format-1 SFX build, and exercise format-2 to format-2
automatic update and restart.

## Compatibility And Migration

v0.7.0 cannot consume format 2 and its custom self-extracting launcher can
trigger Defender ML. v0.7.1 therefore requires one manual ZIP extraction.
Subsequent releases update automatically through the standalone helper.

## Tactical Protections During Migration

The workflow fails closed when the signing secret is absent. The app never
falls back to unsigned installation. The helper and package are fetched only
over HTTPS from GitHub, all redirects remain on GitHub-owned hosts, and every
payload is bounded and streaming-hashed.

## Tests And Security Validation

Verify a known signed fixture; reject manifest tampering, unexpected archive
entries, paths, duplicates, missing files, size/hash mismatch, wrong executable
version, wrong parent PID/path, and unsafe cleanup paths. Exercise rollback after
an interrupted multi-file replacement. Test both EXEs with Internet Zone
metadata under Defender real-time protection and a no-remediation custom scan.

## Performance And Resource Benchmarks

Manifest and updater checks happen at startup; the package download happens
only for a newer version. Buffers are bounded at 64 KiB, the compressed package
at 128 MiB, the updater at 16 MiB, and expanded package data at 64 MiB.

## Rollout And Rollback

Publish v0.7.1 as the clean manual bootstrap. Each future update stages only the
five managed files, moves the prior set into a transaction backup, and restores
them in reverse order on failure. A failed helper restarts the preserved app.
For a bad signed release, remove it from `latest` and publish a corrected higher
version; do not reuse or silently replace a signed version.

## Acceptance Criteria

Both distributed EXEs scan clean with Internet Zone metadata; unsigned or
modified metadata and bytes are rejected; target identity is confirmed before
the app exits; partial replacement rolls back; a valid update restarts into the
signed assembly version; and the new process deletes the bounded helper folder
without invoking a shell.

## Open Decisions

Acquire Authenticode signing when project scale justifies reputation-based
Windows signing, and document release-key rotation and offline backup recovery.
