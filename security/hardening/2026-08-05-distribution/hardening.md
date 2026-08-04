# Security Hardening Review: Updates And Community Telemetry

## Evidence Basis

I inspected the v0.6 launcher/release path, the DEBUG RAW lifecycle, and the new client/server working tree. I also exercised a signed enrollment and capture through the public Cloudflare hostname. The synthetic record was removed after the test. This review distinguishes code present in the working tree from a released control: the first updater-capable release and its GitHub signing secret are still rollout gates.

## Constraints

The client is public and runs on an untrusted Windows machine, so it cannot prove that telemetry came from a genuine simulator. We can provide consent, attribution, revocation, integrity, replay resistance, bounded ingestion, and quarantine-by-rejection. The app remains .NET Framework 4.8, release delivery remains GitHub Releases, and SCAD's existing tunnel/service must remain independent.

## Opportunity Portfolio

| Opportunity | Evidence | Options | Recommendation | Proposal |
| --- | --- | --- | --- | --- |
| Own the telemetry-ingress trust boundary | RAW ZIP lifecycle, public hostile endpoint, signed E2E experiment | Shared binary credential; per-install key with invitation | Per-install DPAPI key, one-time invitation, strict bounded validator | [Telemetry ingress](proposals/telemetry-ingress-trust.md) |
| Authenticate self-updates independently of transport | v0.6 manual release path and unsigned workflow assets | HTTPS plus hash; signed canonical manifest | Pinned release public key and atomic launcher replacement | [Release authenticity](proposals/release-authenticity.md) |

## Recommendation Summary

I recommend the two selected structural options together. They use asymmetric keys in the direction where they help: the release private key stays outside every client, while each telemetry installation owns a different private key. A shared GPG/private key embedded in the EXE would only delay extraction and would turn one reverse-engineering event into universal upload authority.

## Next Decisions

Configure the GitHub `RELEASE_SIGNING_KEY_PKCS8_B64` secret, issue invitation codes deliberately, publish the privacy/retention statement, and treat the first updater-capable build as a manual bootstrap release. Rotate the tunnel token that appeared in chat after deployment verification.
