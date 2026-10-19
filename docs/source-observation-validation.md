# Source observations and LS-008 validation

Status, 2026-10-17: production instrumentation and diagnostic classification are
implemented locally. Controlled simulator validation and primary measurement
quality policy remain open. Nothing in this report closes LS-008 or claims that
external position control proves replay. No signing, deployment or publication.

## Confirmed implementation

The isolated SimConnect owner reads all three USER freeze flags, slew state,
raw simulator time and position/attitude through a separate optional definition.
It requests ONCE at 250 ms intervals with one outstanding request, a one-second
reply deadline and unique request IDs. A missing or rejected request is retried
after five seconds; a failed optional definition is retried after thirty seconds.
These calls share the native owner and its supervisor. They do not change the
required Guard/Full subscriptions, their freshness checks, or landing state.

Availability and its reason are explicit. The ordered recording journal retains
each observation. Frames carry its original QPC time, their own QPC time, worker
instance/generation and connection generation. SDK replay lifecycle is tracked
separately and cannot refresh an old freeze observation. Recovery restores this
assignment; reconnect and source retirement discard the old observation context.

Evidence survives individual samples, array/checkpoint serialization, pre-roll,
episode transactions, durable RAW effects, RAW/report archives and history detail.
The numeric telemetry format, CSV schema 7, fingerprints and stable contact IDs
remain unchanged. History's eager summary index does not contain these timelines.
The optional archive member and its bounds are specified in
[capture-archive-format.md](capture-archive-format.md).

The source assessor distinguishes `confirmed_sdk_replay`, `externally_driven_pose`,
`suspicious_kinematics` and `unknown`. Confirmed replay requires the SDK lifecycle
signal. Observed external pose control requires all three freeze flags and pose
movement across two fresh observations in the same source context; pause can
retain that observed state, while flag changes, stale data and gaps clear it.
One freeze flag, slew, an aircraft name, signed FPM, G or a single mismatch does
not establish replay. The existing compound kinematic detector is unchanged.
Source counts are recorded in the episode analysis decision journal, without
changing landing acceptance or numerical metrics.

The 1.5-second freshness limit and small pose movement tolerances are diagnostic
parameters, not validated thresholds for rejecting a landing. Primary vertical
rate/G confidence and their user presentation still require the negative controls
below. The existing anomalous Fenix captures do not become ground truth simply
because they have a kinematic mismatch.

## Compatibility and automated evidence

Old ZIP and CSV 5/6/7 inputs are accepted as before. They do not need the new
metadata or capability, and missing evidence stays unknown. New archives that
actually contain `source-state.bin` require `source_state_formats: [1]` from the
receiver. Until support is observed, their original bytes remain pending; the
client does not strip the metadata or rewrite the archive. Receiver support is
additive and independent of archive compression and controller provenance.

Evidence in `artifacts/runtime-refactor-20261016/`:

- `p9-source-final-tests.log`: the full application suite passes all 280 checks,
  including the production analysis journal. `p9-unsigned-bundle-tests.log` checks
  bundle topology, extraction and damaged/missing dependency recovery.
- `p9-source-tests.log`: optional failure/timeout/retry isolation, malformed flags,
  late reply rejection, source-generation fencing, checkpoint/pre-roll/history/
  episode/RAW roundtrip, legacy upload and immutable capability waiting pass.
- `p9-contracts-net48.log` and `p9-contracts-net10.log`: strict binary roundtrips,
  truncation/invalid-state rejection, unknown legacy inputs, freeze/slew/stale/
  replacement controls and identical numeric fingerprint/contact IDs pass.
- `p9-archives-net48.log` and `p9-archives-net10.log`: both container formats,
  six logical/seven physical members, corruption and decode limits pass.
- `p9-server-tests.log`: 125 receiver/dashboard tests pass, including a real
  net48 archive with 40 source-annotated rows and valid schema 5/6/7 legacy archives.
- `p9-legacy-intake-results.json`: all 1,496 original hashes unchanged; 1,478
  accepted (42 schema 5, 529 schema 6, 907 schema 7), the same acceptance count
  as the P10 baseline. Eighteen inputs remain outside prior acceptance policy;
  individual hashes and reasons are recorded. These include older schema-4
  research captures and intentionally malformed/boundary fixtures. This work
  does not add a new source-metadata rejection to old captures.

These fixture, codec and corpus results do not replace a controlled live flight.
Native availability for a ground Fenix scenario was subsequently verified below.

The current unsigned package is `unsigned-runtime-current/MSFS-Landing-Stats.exe`,
1,911,527 bytes, SHA-256
`6171e2beb491a326ab78e20ac1d3b4965cb892c4f729e5c8fffc52427de1957e`.
It retains the restored guardian camera/timeline handlers. This package was built
and checked without launching a second user application or publishing it.

## Live ground evidence, 2026-10-17

The user started MSFS and confirmed an unpaused aircraft on the ground. Six
read-only runs identified FenixA320 CFM SL and received 138 available USER
observations, with zero unavailable observations or SDK exceptions. Guard/Full
continued normally (1,879/1,876 frames). All six runs used one connection.
The raw flags, pose, request IDs and timestamps are retained in
`p11-live-ground-fixed/source-starts.tsv`. This establishes native availability
in that ground scenario, not detector recall or measurement confidence in flight.

The first live run exposed a signed enum conversion error in the new stream
watchdog: the SDK could not convert high-bit request IDs to UInt32, causing
unnecessary reconnections. `Requests : uint` fixes the actual call boundary;
an explicit SDK-compatible conversion regression covers the high-bit range.
The six clean runs and independent Guard/Full subscription recovery were then
repeated using the corrected source. Failed pre-fix evidence is retained.

An opt-in production-composition probe also passed: two forced exits each of
the real Simulator and Controllers helpers, automatic replacement, continued
peer-source progress, annotated RAW samples after each replacement, six confirmed
helper exits and no fabricated landing. Its journal and checkpoint use a private
directory; no aircraft control, user history or upload is involved. Evidence:
`p11-live-runtime/runtime-recovery.tsv` and its retained runtime directory.

## Remaining closure criteria

The user subsequently performed a live Fenix landing at LBSF RWY 27 and closed
MSFS. The ordinary final application saved exactly one record and completed the
episode transaction without a failure. All 2,737 input frames retained source
and controller provenance; USER observations were available with freeze/slew
zero, median age 0.1148 s, P95 0.2287 s and maximum 0.2624 s. The existing replay
detector returned false. The source assessor remained unknown, as intended:
absence of an SDK replay signal is not a positive proof of every possible source.
This is one verified live Fenix negative control, not the full matrix below.
The archived exact inputs and analysis are under `p11-normal-app/flight/`.

The read-only probe and Sky Dolly source investigation remain under
`artifacts/ls008-replay-20261016/`; the controlled replay/flight experiment is
still open. A ground recovery test does not supply those observations.

Before enabling rejection or a primary-quality label, retain paired recordings
and exact tool/aircraft versions for:

1. Live Fenix including normal landing, bounce, pause and an external flight model;
   helicopters/skids, slopes and a genuinely ascending reference point.
2. Sky Dolly recording without playback; normal replay, pause/seek/stop; starting
   Landing Stats while replay is already running; formation/manual-control modes.
3. SDK replay entry/exit and simulator loading/quit/reconnect: an already recorded
   landing survives, and replay-to-live-ground never creates a contact without a
   new live airborne witness.
4. The full positive and negative corpus: report observed availability, missed
   detections and false positives separately. Old captures without flags remain
   unknown and cannot measure the new detector's recall.
5. Independent primary-channel consistency validation and the user-facing quality
   rule. Preserve the distinction between uncertain measurement and replay.

P11 still requires final resource/flight/update/rollback acceptance and removal
of the remaining legacy production-only compatibility entry points after their
test coverage is migrated. LS-001 remains closed best effort and is outside this work.

## Controlled normal-replay comparison, 2026-10-19

The user performed one manual Fenix A320 landing with a bounce, then replayed it
using FlightEcho 3.0.1.184 and SkyDolly 0.19.2. Retained Core version
`0.9.5+6f6eff789e0bc4811974f1c6e406940702184436` reproduced the saved diagnostics:
live 0/2857 external frames, FlightEcho 3127/3137, SkyDolly 2996/3004. Both replay
phases had all three freeze flags, fresh available observations, slew zero, and
movement; the initial observation-pair delay was 0.224/0.147 seconds. SDK replay
state remained unknown. All six saved contacts retained exact source evidence.

This supersedes the earlier statement that no controlled replay comparison has
been performed. It does not close the remaining pause/seek/stop, replay-to-live,
manual-control/formation, external-flight-model and lifecycle matrix. Acceptance
still uses the old kinematic filter and saved both replays as ordinary contacts;
no production filtering policy was changed in this investigation. Full evidence:
[controlled replay validation](ls008-controlled-replay-validation-2026-10-19.md).
