# Repeated-contact geometry fixtures

The original eight CSVs are column projections of captured telemetry, not synthetic
aircraft geometry and not raw diagnostic CSVs. Each header records the full
capture ID, original ZIP SHA-256, and the three actual contact indices.

The extraction uses `TelemetryKinematicDiscontinuity.SplitPhysicalSegments`,
`TelemetryAnalysisFrames.Prepare`, then `TelemetryDeduplicator.Deduplicate`.
It retains all samples from three seconds before the first accepted contact to
the end, with round-trip double precision. Columns retain the prepared physics
clock, ground/physics flags, attitude, altitudes, WORLD Y, body X/Y rates, raw
touchdown latch, G, and contact states/compressions. The bit mask refers to the
three indices listed in the header. Locations and aircraft/user metadata are
omitted. Tests read this projection directly as analysis input; it does not
exercise the diagnostic CSV parser or reproduce a full-capture datum estimate.

| Capture prefix | Aircraft | Regression evidence |
| --- | --- | --- |
| `0ca2ec0e` | SR22T | Repeated main unloads shorten the final derotation window; the earlier phase supports mains 1/2 and nose 0. |
| `790d1cd7` | Fenix A321 | Main recontacts occur after the nose. Earlier phase supports mains 1/2 and nose 0, independently matching the installed flight-model configuration. |
| `13dc14ff` | Vision Jet | Timing phases suggest nose 1 or 2; loaded-wheel spatial evidence instead identifies nose 0 and mains 1/2. Every recorded touchdown starts on point 0. Roles resolve, main-gear reconstruction remains unavailable. |
| `e78d3f80` | C172 | Timing phases suggest nose 8 or 9; spatial evidence identifies nose 7 and mains 8/9. Nose/contact and unstable-arm guards remain effective. |
| `cb48a7b6` | C172 | Spatial evidence identifies nose 7 and a stable main axis; the nose-loaded contact still cannot produce a main-gear reconstruction. |
| `c365f369` | C172 | Spatial roles and stable loaded-wheel arm recover reconstruction without a sufficient main-only interval. |
| `0b4d614d` | C172 | Credible spatial evidence contradicts timing, but temporal refits cannot establish stable replacement roles. Reject the timing hypothesis. |
| `a54ff64e` | C172 | Stable roles do not imply a usable arm: main compression slopes become nonphysical on temporal subsets. |

For `790d1cd7`, the inspected `Function_Exterior_A321_CFM_SL/config/flight_model.cfg`
had SHA-256 `fc6e4470f894bdaf4fd75a89eee66126404a549e0fa6b7b30f5820e4425f0dc2`:
nose point 0 at longitudinal 42 ft, mains 1/2 at -14.230456 ft. The injected
C172 configuration in the priority test is deliberately hypothetical and tests
the configured path, not a claim about encrypted aircraft geometry.

Spatial fixture expectations are supported by multiple independent recorded
channels and consistent cross-capture roles; they are not independently read
C172/SF50 configuration coordinates. `SpatialGearGeometryRegressionTests` also
uses an independent rigid-body generator with specified 3D wheel positions and
an airborne datum, plus taildragger, observability, noise, compression-sign,
physics-boundary, extension, numbering, clock and latch controls.

Full original archives belong to the 874-archive server snapshot documented in
`BACKLOG.md` (2026-10-14). The full-corpus replay compares all current results,
including configured geometry where available; these small fixtures keep the
critical cases reproducible without access to that private corpus.

## Contact-point reconstruction fixtures

The `geometry-ls001-point-*.csv` files additionally retain ground speed,
true heading, latitude/longitude, body-Z angular rate, and each point's extension.
They cover up to 20 seconds before the first accepted contact and 15.1 seconds
after it. Their prefix distinguishes them from the original eight projections,
whose missing position/speed channels intentionally cannot enable the new
surface model. Every new header still identifies its original capture and ZIP
SHA-256; values use round-trip precision and the same normalization pipeline.

These channels are needed to recover the ground trajectory and distinguish
rolling from sideways sliding. No aircraft/user identity fields are included.
The cropped approach can change the independently recovered datum relative to
the complete archive; full-corpus numerical comparisons use the original ZIPs.

The four selection fixtures `4178026c`, `d2d27342`, `838a087e`, and `2d13e52c`
use the same extraction and include contacts for which the legacy and new
reporting bands did not overlap. The first two are Fenix geometry controls;
the latter two expose the effect of evaluating accelerating vertical motion
75 ms before contact. They verify default point-model priority even when the
longitudinal model also succeeds, while explicitly supplied geometry still wins.
The original `c365f369` projection verifies that absence of point-model evidence
retains the supported longitudinal calculation with explicit journal provenance.

`ContactPointGeometryRegressionTests` covers the three actual Vision Jet nose
contacts, the formerly unstable C172 predictions, ambiguous simultaneous points,
short bounces and explicit refusals. The synthetic control independently specifies
a point in space, angular motion, a sloped plane, suspension and a datum offset;
its known clearance closes at exactly 4 ft/s. Additional controls perturb physics,
extension, observability, height noise, heading/course agreement, point numbering,
clock origin, frame delivery and the raw latch. Storage tests preserve the new
point metadata and full-rotation component without relabeling it as pitch.

The bounded-geometry refinement additionally resolves the simultaneous points
7 and 9 in `e78d3f80` without discarding either point. Its later one-frame bounce
remains unavailable; the third contact is recovered by v4 after independently
checking recorded angular rates and the integral of WORLD Y against attitude
and altitude. Models v2, v3 and v4 are covered by latch,
numbering, time-origin, duplicate-frame, and history-storage checks. Independent
polytope vertices exercise the optimization certificate. The physical generator
also includes banks up to about 23 degrees to distinguish rotation order.

The v4 controls additionally prescribe quadratic vertical velocity and cubic
altitude with a known clearance derivative. They reject missing body-Z rates,
biases in each body angular-rate channel, and altitude drift. The unresolved
weak `3f8c37b6` second contact retains its explicit refusal; a small central
estimate alone is not sufficient to establish a positive closing speed.
