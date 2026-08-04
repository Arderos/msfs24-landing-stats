# Security Hardening Review: Updates And Community Telemetry

## Evidence Basis

I inspected the released v0.7.0 path, the replacement format-2 updater, the DEBUG RAW lifecycle, and the client/server tree. I also exercised a signed enrollment and capture through the public Cloudflare hostname and removed the synthetic record. The v0.7.0 custom SFX launcher was independently isolated as the Defender ML trigger; the normal application and standalone updater scan clean with Internet Zone metadata.

## Constraints

The client is public and runs on an untrusted Windows machine, so it cannot prove that telemetry came from a genuine simulator. We can provide consent, attribution, revocation, integrity, replay resistance, bounded ingestion, and quarantine-by-rejection. The app remains .NET Framework 4.8, release delivery remains GitHub Releases, and SCAD's existing tunnel/service must remain independent.

## Opportunity Portfolio

| Opportunity | Evidence | Options | Recommendation | Proposal |
| --- | --- | --- | --- | --- |
| Own the telemetry-ingress trust boundary | RAW ZIP lifecycle, public hostile endpoint, signed E2E experiment | Shared binary credential; per-install key with invitation | Per-install DPAPI key, one-time invitation, strict bounded validator | [Telemetry ingress](proposals/telemetry-ingress-trust.md) |
| Authenticate self-updates independently of transport | v0.7.0 signed format-1 SFX path, removed after Defender ML detection | HTTPS plus hash; signed canonical two-asset manifest | Pinned release key, independently verifying helper, transactional five-file replacement | [Release authenticity](proposals/release-authenticity.md) |

## Recommendation Summary

I recommend the two selected structural options together. They use asymmetric keys in the direction where they help: the release private key stays outside every client, while each telemetry installation owns a different private key. A shared GPG/private key embedded in the EXE would only delay extraction and would turn one reverse-engineering event into universal upload authority.

## Next Decisions

Publish v0.7.1 as the one-time clean ZIP bootstrap, exercise the first format-2-to-format-2 update, issue invitation codes deliberately, publish the privacy/retention statement, and rotate the tunnel token that appeared in chat after deployment verification.
