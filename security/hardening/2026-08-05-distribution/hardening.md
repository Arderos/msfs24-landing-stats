# Security Hardening Review: Updates And Community Telemetry

## Evidence Basis

I inspected the release updater, the DEBUG RAW lifecycle, and the client/server tree. Enrollment is automatic and anonymous; every capture is still bound to its installation key. Enrollment has a global request budget, atomic registry cap, stale-unreferenced retention, and a free-space gate. Capture admission retains independent source/global byte budgets and hard storage/free-space ceilings because a public client cannot prove genuine hardware identity.

## Constraints

The client is public and runs on an untrusted Windows machine, so it cannot prove that telemetry came from a genuine simulator. We can provide consent, attribution, revocation, integrity, replay resistance, bounded ingestion, and quarantine-by-rejection. The app remains .NET Framework 4.8, release delivery remains GitHub Releases, and SCAD's existing tunnel/service must remain independent.

## Opportunity Portfolio

| Opportunity | Evidence | Options | Recommendation | Proposal |
| --- | --- | --- | --- | --- |
| Own the telemetry-ingress trust boundary | RAW ZIP lifecycle, public hostile endpoint, signed E2E experiment | Shared binary credential; automatic per-install key | Per-install DPAPI key, automatic enrollment, source/global quotas, strict bounded validator | [Telemetry ingress](proposals/telemetry-ingress-trust.md) |
| Authenticate self-updates independently of transport | v0.7.0 signed format-1 SFX path, removed after Defender ML detection | HTTPS plus hash; signed canonical two-asset manifest | Pinned release key, independently verifying helper, transactional five-file replacement | [Release authenticity](proposals/release-authenticity.md) |

## Recommendation Summary

I recommend the two selected structural options together. They use asymmetric keys in the direction where they help: the release private key stays outside every client, while each telemetry installation owns a different private key. A shared GPG/private key embedded in the EXE would only delay extraction and would turn one reverse-engineering event into universal upload authority.

## Next Decisions

Monitor enrollment count/rate, source/global capture-attempt budgets, SQLite/WAL size, and disk reserve. Publish the privacy/retention statement and rotate the tunnel token that appeared in chat after deployment verification.
