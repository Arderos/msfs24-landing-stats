# Security Hardening Proposal: Authenticate Self-Updates Independently Of Transport

## Decision

Choose whether the updater merely detects transfer corruption or independently authenticates the publisher.

## Executive Recommendation

Option 1, **HTTPS plus release hash**, is operationally simple. Option 2, **Pinned signed release manifest**, keeps the private release key outside clients and fails closed. I recommend Option 2.

## Evidence

| Evidence | Finding or document | What it establishes |
| --- | --- | --- |
| `E004` | Single-file launcher lifecycle | The launcher exits after starting the extracted child, so the original EXE can be replaced. |
| `E005` | v0.6 release workflow | The workflow published no independently signed metadata. |

I inspected both paths. The compromise scenarios below are threat-model reasoning; no GitHub compromise was observed.

## Current Design And Failure Mode

v0.6 requires a manual download. A naïve updater that trusts an EXE and hash from the same GitHub publisher account detects truncation but gives an account compromise authority over both values.

## Desired Invariants

Only a manifest signed by the pinned release key may authorize installation. It must bind version, exact asset name, size, and SHA-256. The bundle must verify, replacement must be atomic, and failure must preserve the current launcher.

## Constraints And Non-Goals

The app targets .NET Framework 4.8. Authenticode reputation is useful future work but is not required for the updater's cryptographic decision.

## Before Architecture

```mermaid
flowchart LR
  G["GitHub release account"] --> E["Unsigned EXE"]
  E --> U["Manual user download"]
  U --> L["Single-file launcher"]
```

## Options

### Option 1: HTTPS plus release hash

This option is attractive because it adds only streaming SHA-256 and a manifest. It catches network or storage corruption. It does not separate asset authority from hash authority.

```mermaid
flowchart LR
  G["GitHub release account"] --> E["EXE"]
  G --> H["SHA-256"]
  E --> U["Updater"]
  H --> U
  U --> L["Launcher replacement"]
```

| Change | Before | After | Security consequence | Cost |
| --- | --- | --- | --- | --- |
| Integrity | Manual | Colocated SHA-256 | Detects corruption | Tiny hash cost |

Its rollback is simply disabling checks, but publisher compromise remains unchanged.

### Option 2: Pinned signed release manifest

CI signs a canonical five-line manifest. The client embeds only the RSA public parameters, verifies before download installation, hashes while streaming, executes the launcher's bundle verifier, copies to a same-directory temporary path, and atomically replaces the exited launcher while retaining `.previous`.

```mermaid
flowchart LR
  K["CI release private key"] --> M["Signed canonical manifest"]
  G["GitHub Releases"] --> M
  G --> E["EXE"]
  P["Pinned public key"] --> U["Updater verifier"]
  M --> U
  E --> U
  U -->|"signature + size + hash + bundle"| L["Atomic launcher replacement"]
  U -->|"failure"| X["Keep current launcher"]
```

| Change | Before | After | Security consequence | Cost |
| --- | --- | --- | --- | --- |
| Publisher authority | GitHub account | GitHub plus separate release key | Asset substitution alone fails | Key backup/rotation |
| Installation | Manual | Background verified replacement | Partial downloads never replace | One manual bootstrap |

The key operational risk moves to secret custody. A semantically broken signed release is still trusted, so `.previous` and a rollback runbook remain necessary.

## Comparison

| Dimension | Option 1 | Option 2 |
| --- | --- | --- |
| Security | Corruption detection | Independent publisher authentication |
| Performance | Hash | Signature plus hash; both off the UI-critical path |
| Reliability | Reject corrupt bytes | Also verifies bundle and atomic replacement |
| Operability | Minimal | Key secret, backup, rotation |
| Migration | Simple | Manual updater bootstrap |

## Recommendation

I recommend Option 2. If the project cannot custody the private key reliably, Option 1 plus manual user confirmation is preferable to pretending an unsigned automatic replacement is secure.

## Evidence Coverage And Residual Risk

`E004 — Single-file launcher lifecycle` enables safe replacement. `E005 — v0.6 release workflow` is directly addressed by publishing manifest and signature assets. Compromise of both GitHub authority and the signing key remains residual.

## Migration And Rollout

Provision the secret, publish one manual bootstrap build, then validate bootstrap-to-next update. Keep stable asset names for `releases/latest/download`.

## Validation Plan

Use a known signed fixture; tamper manifest, signature, size, hash, and bundle; interrupt download and replacement; test unwritable install directories and rollback.

## Implementation Work Packages

Release key provisioning, workflow signing, launcher-path handoff, background client updater, atomic replacement, and release runbook.

## Open Questions

Where is the offline private-key backup, and who is authorized to rotate it?
