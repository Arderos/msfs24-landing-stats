# Update compatibility contract

`releases/latest/download/update-manifest.txt` is a permanent compatibility
surface, not an implementation detail. Every issued v0.7.x client must be able
to consume it without reinstalling, clearing a runtime cache, or replacing its
original executable.

The oldest issued v0.7.3 verifier accepts exactly:

- manifest `format=2` with eight ordered fields;
- package name `MSFS-Landing-Stats.zip`;
- updater name `MSFS-Landing-Stats.Updater.exe`;
- the original release-signing public key and GitHub-hosted HTTPS downloads.

The ZIP is transport-only. It contains exactly one root-level
`MSFS-Landing-Stats.exe`, byte-for-byte identical to the public one-file
download. Users still download only `MSFS-Landing-Stats.exe`.

`build-update-manifest.ps1` creates this lowest-common-denominator manifest.
`verify-issued-update-compatibility.ps1` reproduces the issued parser's strict
shape checks and also binds the manifest to the package, updater, public EXE,
and their assembly versions. `build-app.ps1` runs the check on every build, so a
protocol-breaking release fails before it can be tagged or published.

New update protocols may be introduced only as parallel assets until every
already-issued client has a proven migration path. They must not replace the
format-2 `latest` contract.
