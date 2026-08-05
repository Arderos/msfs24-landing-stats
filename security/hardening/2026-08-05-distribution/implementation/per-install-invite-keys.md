# Implementation: Automatic Per-Installation Keys

## Selected Design

After explicit telemetry consent, each Windows account creates a random
installation ID and RSA key. DPAPI protects the private key locally. Enrollment
is automatic and proves possession of that key; every later upload is signed.
No hardware UUID, MachineGuid, account name, or shared client secret is sent.

This identity provides stable attribution and revocation for honest clients. It
does not attest real simulator execution: a modified public client can create
new identities or fabricate valid telemetry. Abuse resistance therefore comes
from independent server limits that a new installation ID cannot bypass:
source-address rate/byte budgets, the global daily byte budget, the 20 GiB
retained-storage ceiling, and the 2 GiB free-space reserve. Enrollment itself
has a 1,000/hour global budget, an atomic 100,000-identity registry cap, and
30-day cleanup only for unreferenced active identities.

## Data And Trust Boundaries

- `DEBUG RAW` gates full-rate local collection; separate consent gates upload.
- Registration or upload failure never stops local collection.
- The private key never leaves Windows DPAPI storage.
- Enrollment is limited per source and globally, requires a fresh signed
  request, respects the disk reserve, and cannot exceed the registry cap.
- Captures require an active enrolled key, fresh timestamp, unused nonce,
  declared size/hash, and matching signature.
- ZIP member names, schema, row width, finite values, sample count, compressed
  size, expanded size, and disk budgets are validated before acceptance.
- Revocation prevents the same installation identity from re-enrolling.

## Operational Policy

Keep 16 MiB compressed and 64 MiB expanded request bounds, 512 MiB per
installation/day, 1 GiB per source/day, 4 GiB globally/day, 20 GiB retained,
2 GiB free-space reserve, 1,000 enrollments/hour globally, 100,000 retained
identities, and 30-day expiry for unreferenced active identities. The API
remains reachable only through the Cloudflare Tunnel network and has no
host-published port.

## Validation

Run automatic enrollment and signed public E2E; invalid signature, replay,
duplicate, malformed ZIP, schema mismatch, source/install/global quota,
storage ceiling, and free-space-reserve tests. Confirm a revoked key cannot
restore itself.

## Rollout And Rollback

Enable DEBUG RAW first on maintainer-owned installs, then monitor budget and
rejection metrics. Roll back by disabling the client feature and stopping the
isolated Compose project; queued ZIPs remain recoverable.
