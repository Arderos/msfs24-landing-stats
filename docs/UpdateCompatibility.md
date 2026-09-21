# Update compatibility contract

The update path has two signed stages:

1. `releases/latest/download/update-manifest.txt` is the immutable bootstrap
   channel. It always describes v0.7.6, the first bridge client that understands
   the permanent current-release channel.
2. `releases/latest/download/update-channel.txt` is the moving channel. It
   describes the newest supported release.

This gives every issued client a durable route to the newest version:

```text
v0.7.3 / v0.7.4 / v0.7.5 -> v0.7.6 bridge -> current release
v0.7.6 and newer -> current release
```

v0.7.5 reads the bootstrap manifest. It downloads the signed v0.7.6 updater
from the immutable `v0.7.6` release and invokes it using the legacy argument
shape. The v0.7.6 updater therefore defaults to `update-manifest.txt`, verifies
the versioned bridge manifest again, installs v0.7.6, and restarts it.

v0.7.6 and later read `update-channel.txt`. They explicitly pass that fixed
manifest name to the updater, which verifies the same channel manifest again
from the immutable versioned release before installing the current executable.
No arbitrary manifest name is accepted.

Both manifests use format 3 and authorize one `MSFS-Landing-Stats.exe` plus one
`MSFS-Landing-Stats.Updater.exe` by exact filename, size, and SHA-256. The
manifest signature is verified with the public key embedded in both the client
and updater. ZIP transport is no longer part of the release protocol.

## Release gate

`verify-update-chain.ps1` is mandatory for every tagged release after v0.7.5.
It refuses publication unless:

- the bootstrap manifest has a valid signature and describes the exact v0.7.6
  bridge files;
- the channel manifest has a valid signature and describes the exact files and
  version being released;
- both use the strict format-3 shape;
- the current version is not older than the bridge;
- on v0.7.6, bootstrap and channel manifests are byte-identical.

For v0.7.7 and later, CI downloads the already signed bootstrap manifest and
its exact authorized files from the `v0.7.6` release, verifies them, and then
publishes the unchanged bootstrap manifest beside the newly signed channel
manifest. A future release therefore cannot silently move or remove the bridge
without failing before `gh release create` is reached.

Releases are assembled as drafts. All six uploaded asset sizes and GitHub
SHA-256 digests must match the tested local files before the draft becomes
public/latest, so clients never observe a partially uploaded update.

## Authenticode and CI signing

Starting with v0.8.7, the application, embedded Core DLL, outer single-file
launcher, and standalone updater are signed with the developer's Certum
certificate and an RFC3161 timestamp. The embedded files are signed before
packaging; the outer EXE is signed after packaging. Update sizes and SHA-256
hashes are calculated only after all Authenticode signatures are final.

`BundlePayload` reads the PE security directory to locate the bundle before
the certificate table, allowing only the documented zero alignment padding.
It also accepts previous unsigned bundles. This is structural validation, not
certificate verification: the existing signed update manifest still
authenticates the complete download, including its certificate table.

An old client downloads the updater from the *target* release. Consequently
the fixed updater installs a signed EXE even when the old application itself
does not understand Authenticode bundle layout. The unchanged v0.7.6 bridge
remains unsigned so legacy updaters can still install it.

`verify-published-client-updates.ps1` downloads every supported published
baseline (v0.7.3 through v0.8.6), executes its embedded manifest verifier, checks
the authorized bytes, and invokes the target updater's real replacement
transaction on a disposable copy. It then runs the installed launcher's bundle
verification. This is an offline installation/compatibility gate, not a claim
that a not-yet-published URL was exercised end-to-end. The separate hosted-runner
smoke test launches the signed candidate and requires a responsive main window.

The `code-signing` GitHub environment holds `CERTUM_USERNAME`, `CERTUM_OTP_URI`
and `CERTUM_KEY_ID`. Restrict it to `main` and version tags; an exact temporary
test branch can be allowed during setup and removed afterwards. The OTP URI is
a long-lived signing credential: never commit it, print it, or upload diagnostic
screenshots of authentication. The existing `RELEASE_SIGNING_KEY_PKCS8_B64`
continues to sign update metadata and must not be rotated without a separate
compatibility plan.

Normal pushes and pull requests build and test without code-signing access.
Version tags require signing, startup, regression, and all previous-client
gates before publication. A manual **Build** run with `sign=true` runs the same
signing and compatibility checks, but does not create a GitHub release. The
SimplySign setup action is pinned to a reviewed commit and its MSI is checked
by SHA-256 and Authenticode before installation. Signing jobs are serialized.
