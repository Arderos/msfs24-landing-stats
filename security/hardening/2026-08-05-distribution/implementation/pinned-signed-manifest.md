# Implementation Plan: Pinned Signed Release Manifest

## Selected Design And Constraints

GitHub Actions signs an eight-line format-3 manifest with RSA PKCS#1
v1.5/SHA-256. It binds the release version plus the exact name, size, and
SHA-256 of `MSFS-Landing-Stats.exe` and the temporary
`MSFS-Landing-Stats.Updater.exe`. The clients contain only the public key.

The application verifies the manifest and updater before launch. The updater
then fetches the same manifest from the immutable versioned release URL,
verifies its own signed size/hash, streams and verifies the new single-file
application, validates its PE bundle trailer and assembly version, and replaces
the old public executable only after the requesting process exits.

## Source Revision And Drift Check

The implementation covers the shared protocol, application handoff, standalone
updater, one-file bootstrap, workflow, and regression tests. The release signing
secret is provisioned in GitHub Actions; the private key is absent from the
repository and application binaries.

## Affected Components

`.github/workflows/build.yml`, `src/LandingStats.UpdateProtocol`,
`src/LandingStats.App/Updates/ReleaseUpdater.cs`,
`src/LandingStats.App.Updater`, `src/LandingStats.App.Launcher`,
`build-app.ps1`, and regression tests.

## Update Sequence

1. The running application verifies the latest signed manifest.
2. It downloads the updater with the signed size and SHA-256, verifies the PE,
   launches it with a target and PID, waits for the identity handshake, and
   exits.
3. The updater independently verifies the immutable versioned manifest and its
   own signed hash.
4. It downloads and verifies the new single-file application, including bundle
   marker and assembly version.
5. It moves the old EXE to a same-directory backup, moves the new EXE into its
   exact path, re-verifies it, restores the backup on any failure, and otherwise
   starts the new EXE.
6. The new application removes the bounded temporary updater directory.

## Protections

The workflow fails closed when the signing secret is absent. Neither process
falls back to unsigned installation. Redirects remain on HTTPS GitHub-owned
hosts, downloads are size-bounded and streaming-hashed, the updater confirms
the requesting PID/path and private runtime relationship, and replacement is
limited to the exact `MSFS-Landing-Stats.exe` target.

## Tests And Validation

Verify a known signed fixture; reject manifest tampering, size/hash mismatch,
wrong executable version, incomplete bundle marker, wrong parent PID/path, and
unsafe cleanup paths. Exercise rollback after an invalid executable
replacement. Test both EXEs with Internet Zone metadata under Defender and run
the bundle self-verification before publication.

## Performance And Resource Bounds

Manifest and updater checks happen at startup; the application download happens
only for a newer version. Streaming buffers are 64 KiB, the application is
bounded at 128 MiB, and the updater at 16 MiB.

## Rollout And Rollback

v0.7.3 is the only supported public bootstrap. Older GitHub releases are
removed rather than migrated. A failed helper restarts the preserved EXE. For a
bad signed release, remove it from `latest` and publish a corrected higher
version; never silently replace the bytes behind a signed version.

## Acceptance Criteria

The user downloads one EXE. Unsigned or modified metadata and bytes are
rejected; target identity is confirmed before exit; failed replacement restores
the previous EXE; a valid update restarts into the signed assembly version; and
the new process deletes the helper without invoking a shell.

## Open Decisions

Acquire Authenticode signing when project scale justifies reputation-based
Windows signing, and document release-key rotation and offline backup recovery.
