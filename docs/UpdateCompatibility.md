# Update compatibility contract

The update path has two signed stages:

1. `releases/latest/download/update-manifest.txt` is the immutable bootstrap
   channel. It always describes v0.7.6, the first bridge client that understands
   the permanent current-release channel.
2. `releases/latest/download/update-channel.txt` is the moving channel. It
   describes the newest supported release.

This gives every issued client a durable route to the newest version:

```text
v0.7.5 -> v0.7.6 bridge -> current release
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
