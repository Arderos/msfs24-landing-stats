# Measurement methodology

This document defines what MSFS Landing Stats measures, how each displayed value
is calculated, and why the application deliberately does not report a single
unqualified "landing FPM" number.

## 1. The measurement problem

A landing has several vertical rates that answer different questions:

1. **Aircraft vertical rate** — how fast the aircraft reference point is moving
   vertically in the simulator's world coordinate system.
2. **Surface closure rate** — how fast the clearance between the aircraft and the
   contacted surface is closing along the surface normal.
3. **Indicated vertical speed (VSI)** — the simulator's filtered cockpit-style
   vertical-speed indication.

They are not interchangeable. A runway surface can rise or fall under a moving
aircraft, the local collision normal need not be exactly vertical, the contact
point is displaced from the aircraft reference point, and VSI can lag a rapidly
changing flare. The same touchdown can therefore legitimately produce different
numbers for all three.

The application keeps the raw surface latch as the simulator-reported headline,
shows aircraft vertical rate as the independent pilot-motion metric, and keeps
VSI as a diagnostic trace. The reconstructed closure is explanatory detail; it
does not become a fourth competing headline number.

The names used by the analyzer are easy to confuse, so the short version is:

| Value | Meaning | Source |
| --- | --- | --- |
| `InertialVerticalFpm` | aircraft-reference descent rate at contact; the primary pilot metric | fitted `VELOCITY WORLD Y` |
| `LatchedNormalFpm` | surface-normal touchdown value published by MSFS | `PLANE TOUCHDOWN NORMAL VELOCITY` |
| `UnresolvedSurfaceDeltaFpm` | remainder of the original, terrain-only comparison | latch minus the primary rate and legacy terrain fit |
| `ReconstructedClosureFpm` | independent reconstruction of what the latch should report | inertial, terrain, timing, rotation, and telemetry-derived geometry |
| `ClosureReconstructionResidualFpm` | what remains after the new reconstruction | latch minus reconstructed closure |

The raw MSFS latch and the reconstructed value are deliberately kept separate.
The reconstruction is an explanation and a cross-check; it never replaces or
silently rewrites the value published by the simulator.

## 2. Capture window and timing

The recorder uses SimConnect and captures full telemetry at
`SIMCONNECT_PERIOD_SIM_FRAME` near the ground. A small guard definition remains
active throughout the flight; the full definition is enabled below 3,000 ft AGL
and disabled above 3,500 ft AGL. This hysteresis avoids repeatedly switching at
one threshold and removes the full telemetry cost during cruise.

A landing episode begins at the descending 500 ft AGL gate and ends after 15
seconds of rollout. A bounded pre-roll is retained so enabling diagnostic capture
shortly before an event does not lose the immediately preceding samples. Pre-roll
age uses the recorder's monotonic receipt clock, not simulation time, because the
simulation clock can freeze in a pause or jump during loading. A separate 4,096-
sample ceiling bounds memory even if the incoming clock or frame rate is malformed.

Simulator telemetry is treated as an **unevenly timed series**. Frame intervals
can change during a landing, and duplicate messages can share the same simulation
time. All fits and windows use simulation timestamps rather than sample indexes.
Before analysis, duplicate timestamps are collapsed by keeping the last message;
this retains same-frame updates such as a touchdown latch that changes in a later
message for the same simulation instant.

## 3. Contact detection and bounces

A contact candidate is an air-to-ground transition of `SIM ON GROUND` after at
least one airborne sample. Contacts separated by no more than ten seconds belong
to the same landing episode and are numbered independently.

Every contact is analyzed and stored. A bounce does not overwrite the first
impact with the final gentle touchdown, and later-contact data cannot leak into
the first contact's G-load or latch windows. All post-contact windows end strictly
before the timestamp of the next contact in the same episode.

The history is displayed newest-first, including contacts within one bounced
landing. This is a presentation rule only; the stored contact number preserves
the physical sequence.

## 4. Estimating the contact time

`SIM ON GROUND` is sampled at frame boundaries, while the physical contact occurs
inside a physics interval. Calling the first ground frame the exact impact time
would introduce a frame-sized timing error.

When contact-point compression is available, the analyzer estimates the zero-
compression crossing for every newly compressed point from the first two usable
ground samples. Estimates outside the interval between the last airborne sample
and first ground sample are rejected. The median of the valid estimates is used
as the contact time, and their spread is retained as a quality diagnostic.

If compression cannot produce a reliable estimate, the last airborne timestamp
is used and the contact-time source is recorded explicitly. A short airborne
phase can separately force the primary-rate estimator to use its last-sample
fallback, or leave the experimental reconstruction without enough fit points; it
does not retroactively change a valid compression-derived contact time.

## 5. Aircraft vertical rate

The aircraft-rate metric uses `VELOCITY WORLD Y`, the aircraft's vertical
velocity in the simulator world frame. Internally the analyzer uses a descent-
positive convention:

```text
aircraft_rate_fpm = -60 × VELOCITY_WORLD_Y_fps
```

For the last approximately 0.20 seconds of a continuous airborne phase, the
analyzer fits velocity against the actual timestamps and extrapolates that
airborne trend to the estimated contact time. It intentionally does **not**
interpolate through the first ground sample: that sample can already include
deceleration from the landing-gear impact, which did not exist before contact.

The extrapolation is used only when the contact time came from compression and at
least 0.15 seconds of continuous airborne data exists. Otherwise the raw last-
airborne `VELOCITY WORLD Y` value is used and the UI marks it as a fallback.

The dashboard presents signed motion in the conventional graph form: climb is
positive and descent is negative. Thus an internal descent-positive value of
`165 fpm` is displayed as `-165 fpm`.

## 6. Surface closure rate

MSFS exposes `PLANE TOUCHDOWN NORMAL VELOCITY`, a value latched at touchdown. The
analyzer converts its magnitude to feet per minute:

```text
surface_closure_fpm = abs(60 × PLANE_TOUCHDOWN_NORMAL_VELOCITY_fps)
```

The latch can update on the first ground frame or on a following frame, and its
old value can survive a flight reload or airport change. The analyzer therefore
does not trust the first value it sees. It searches for a change relative to the
last airborne value, beginning with the contact frame and ending at the earlier
of two seconds after contact or the next contact in the episode.

In the captured corpus the event was triggered by the first gear contact that
established the on-ground state, while the changed value was normally visible on
that published simulation time or the following frame. A later wheel or nose-
gear contact did not replace it unless a new airborne phase created a new contact
episode. This describes observed SimVar behavior, not a guarantee about the
simulator's undocumented internal contact solver.

If no attributable update is detected, surface closure and every value derived
from it are stored as unavailable. The independent terrain measurement remains
valid. This prevents a stale latch from being presented as the result of the
current landing.

On the vertical-rate chart, the closure value is shown by both a horizontal
dashed line and a matching vertical dashed contact marker. Together they mean
"this surface-relative value belongs to this contact"; they are not a time series
of constant aircraft velocity.

## 7. From the legacy `unresolved` value to reconstructed closure

There are two different residuals in current records. They must not be read as
the same quantity.

### 7.1 The original terrain-only remainder

The first implementation compared the contact-time aircraft rate with the raw
surface latch and explained only the approach-side terrain trend:

```text
surface_delta = LatchedNormalFpm - InertialVerticalFpm
legacy_terrain = 60 d(GROUND_ALTITUDE)/dt
UnresolvedSurfaceDeltaFpm = surface_delta - legacy_terrain
```

`GROUND ALTITUDE` is fitted against time over the final approximately 0.20
seconds of airborne motion. This is equivalent to local terrain slope multiplied
by groundspeed, but it does not assume that the whole runway is planar. An
isolated ground-altitude sample more than five feet from its local median is
replaced by a time-interpolated value before fitting.

This `UnresolvedSurfaceDeltaFpm` field is retained for compatibility and for
diagnosing the older two-term model. It mixes effective latch timing, flare
acceleration, rotation, geometry, and sampling error. It is **not** the error of
the newer reconstruction, and it is no longer interpreted as an unknown physical
force.

### 7.2 Fixed temporal reconstruction

The reconstruction asks a different question: can the telemetry independently
reproduce the number that MSFS latched? It does not start with the legacy
aircraft-rate estimate. Instead, let `t_c` be the compression-derived contact
time, or the last airborne timestamp when compression timing is unavailable.
For each of the following signals the analyzer fits a separate quadratic in
actual simulation time over the last 250 ms of the continuous airborne phase:

- `VELOCITY WORLD Y`, written `VY` below;
- `GROUND ALTITUDE`, written `G`;
- raw body-X angular velocity, written `q`;
- pitch angle `theta` and bank angle `phi`.

At least five distinct pre-contact timestamps are required. Three points make a
quadratic algebraically solvable, but three or four simulator frames provide no
useful protection against quantization or a single irregular frame. The model
therefore becomes unavailable instead of reporting a confident extrapolation at
low sample density. The effective time must also lie between the earliest and
latest fit samples; five densely packed frames that do not reach 75 ms before
contact are not enough. Every polynomial is evaluated at the same frozen
effective time:

```text
t_* = t_c - 0.075 seconds
```

Using a descent-positive convention, the three reconstructed components are:

```text
inertial_* = -60 VY(t_*)
terrain_*  =  60 dG/dt(t_*)
pitch_*    =  60 q(t_*) L cos(theta(t_*)) cos(phi(t_*))

ReconstructedClosureFpm = inertial_* + terrain_* + pitch_*
ClosureReconstructionResidualFpm = LatchedNormalFpm
                                           - ReconstructedClosureFpm
```

All velocities are in feet per second before multiplication by 60, angular rate
is in radians per second, and the signed longitudinal arm `L` is in feet. A main
gear behind the velocity reference has negative `L`. The simulator's raw body-X
rate is negative during the nose-up motion seen in the corpus, so nose-up motion
and an aft main gear produce a positive closure contribution without an extra
sign inversion.

The `-75 ms` value is an **effective sampling time** inferred from the captured
input/output relationship. It says that the published latch behaves most like
the state reconstructed 75 ms before `t_c`; it does not move the physical contact
backward and is not a claim about an undocumented Asobo physics substep. The
offset, window, polynomial order, terms, and signs are fixed for every contact.
The analyzer never scans alternatives against the current landing's latch.

Calibration and reconstruction share one implementation of the rigid-body
projection onto world vertical. Model v1 deliberately evaluates its pitch term
with `omega_y = 0`, preserving the validated equation above. The omitted
`omega_y sin(phi)` yaw-by-bank contribution can be of order 1--2 fpm on a
banked de-crab, but adding it would define a new model version and requires a
new out-of-sample validation rather than a silent refactor.

The terrain fit uses approach-side samples only. A clipped five-sample median
detector covers the ends of short captures as well as their interior; isolated or
adjacent altitude spikes are replaced from the nearest valid time neighbors before
the quadratic is fitted. A centered fit including rollout
samples fixed one large error in the legacy terrain-only comparison, but worsened
the frozen full reconstruction. Likewise, a lateral roll/half-track term and a
global multiplicative correction were tested and rejected. Their apparent gains
were not stable across the corpus.

### 7.3 Establishing the signed gear arm

The application prefers an installed, readable `flight_model.cfg`. It resolves
the active aircraft by its exact SimConnect `TITLE`, follows ordinary
`base_container` references, and merges the MSFS 2024 modular common,
attachment, and preset configuration layers that can contribute flight-model
data. Indexed `point.N` values from auto-merge layers are appended and reindexed
in child-attachment, parent-attachment, then preset order; they are not treated
as overrides merely because two source files reuse the same index. Manual merge
rules or unresolved dynamic contact-point parameters are not guessed: that
configuration path fails closed and the telemetry geometry fallback is used.
Cross-package airplane attachments are resolved by their full VFS-relative path;
an absent or ambiguous layer also fails closed. Non-airplane cabin and passenger
attachments are excluded from flight-model geometry. These rules follow the
[MSFS 2024 modular SimObject merge order](https://docs.flightsimulator.com/msfs2024/html/5_Content_Configuration/Modular_SimObjects/Modular_SimObject_Merging.htm).
Microsoft Store/Xbox and Steam installations are both discovered through
their `UserCfg.opt` `InstalledPackagesPath`; packages may live on any drive.

From `[CONTACT_POINTS]` the parser uses only wheel (`type 1`) points. The
forward one- or two-point cluster is separated from the laterally spaced main
gear. Scrape, helper, and non-wheel points are ignored. The parser retains each
wheel's final auto-merged index and longitudinal coordinate as well as the
median main-gear coordinate. When those indices explain an observed sustained
touchdown, they provide the main/nose roles directly and the arm is selected
from the main wheel or wheels that actually made that contact. This matters on
aircraft such as the A380, whose four main contact points have two distinct
longitudinal coordinates.

Complex add-ons can expose extra visual-bogie compression channels that do not
map one-to-one to the readable `point.N` list. The configured indices are
therefore evidence, not an unconditional assertion: if they do not explain any
sustained touchdown transition, the analyzer retains the configured median arm
but infers contact roles from telemetry. This is the observed ToLiss A340 path.

The altitude datum and `VELOCITY WORLD Y` reference need not be the same point.
On many approximately one-second airborne windows, the analyzer integrates
world-vertical velocity and the vertical velocity produced by one foot of body-
axis offset. For the recorded attitude and body rates:

```text
rigid_vertical_per_ft = -sin(phi) omega_y
                        - cos(theta) cos(phi) omega_x

Delta(PLANE ALTITUDE) - integral(VY dt)
    = D integral(rigid_vertical_per_ft dt) + error
```

The slope `D` is the signed longitudinal offset between the altitude datum and
the velocity reference. Overlapping windows are interleaved across four start
phases; at least three phase estimates must agree. They are not statistically
independent samples. With readable configuration geometry, each configured
main-wheel arm and the median fallback are corrected as:

```text
L = A_cfg + D
```

If the flight model is encrypted, missing, ambiguous, or cannot be matched
safely, the application falls back to target-independent telemetry geometry.
Neither path reads `PLANE TOUCHDOWN NORMAL VELOCITY`.

Contact-point numbers are not assigned semantic roles in that fallback. The
analyzer finds the final sustained contact run of every active point and orders
the runs by start time. With three through five settled wheel points, every
point before the final delayed point is treated as main gear and the final point
as nose gear, subject to the independent derotation checks below. This covers
ordinary tricycle layouts, a center main gear, and four-main-plus-nose layouts;
there is no special rejection at five points. Aircraft with six or more settled
wheel points may use a clustered path where a dominant derotation gap separates
an early main cluster from a later nose/helper cluster. Weak or mixed evidence
makes reconstruction unavailable rather than triggering an index-based guess.

The last point is accepted as the nose only when it arrives at least 0.20 seconds
after the latest main and pitch moves at least 0.5 degrees in the normal nose-
lowering direction. The analyzer then finds rollout intervals where all inferred
mains are compressed and the nose is not. With `H = PLANE ALTITUDE -
GROUND ALTITUDE`, pitch `theta`, and mean main compression `c_bar`, it solves:

```text
H(t) = b0 + A_theta theta(t) + A_c c_bar(t) + error(t)
L = A_theta + D
```

The fallback quality value summarizes pitch/compression separation, phase
coverage, and agreement across interleaved estimates. It is an identifiability
score, not a probability. Low-quality calibration is rejected. Because the
touchdown latch appears nowhere in either geometry path, rollout data cannot
tune the reconstruction to its target. Encrypted Marketplace aircraft remain
supported; they simply take the telemetry fallback.

### 7.4 Worked example

Consider an illustrative primary contact where the fixed fits give:

```text
VY(t_*)       = -4.00 ft/s                 -> inertial_* = 240.0 fpm
dG/dt(t_*)    = +0.10 ft/s                 -> terrain_*  =   6.0 fpm
q(t_*)        = -0.015 rad/s
L             = -12.0 ft
pitch / bank  = 5 deg / 0 deg             -> pitch_*    =  10.8 fpm
```

The independent reconstruction is therefore `256.8 fpm`. If MSFS publishes a
raw latch of `260.0 fpm`, the reconstruction residual is `+3.2 fpm`. The app
keeps `260.0 fpm` as the simulator-reported headline value; the three components,
`256.8 fpm` model result, and `+3.2 fpm` residual belong in the explanatory
detail. The model is not a correction that changes the raw latch.

### 7.5 Availability and accuracy

The model result is available only when a supported gear sequence, a credible
gear arm, and at least five usable pre-contact samples exist. If the raw latch
cannot be attributed safely, the independent model can still exist, but its
latch-minus-model residual is
unavailable. The result records whether contact time came from compression, how
many fit points were used, whether the arm was recovered, and the geometry
quality score.

The frozen model was evaluated on 20 recorded contacts from 17 captures: 17
primary contacts and three bounce contacts across A320 and A350 aircraft.

| Population | MAE before | MAE after | P90 after | Maximum after |
| --- | ---: | ---: | ---: | ---: |
| all 20 contacts | 11.38 fpm | 3.27 fpm | 5.45 fpm | 11.74 fpm |
| 17 primary contacts | -- | 2.86 fpm | 5.07 fpm | 8.86 fpm |
| three bounce contacts | -- | 5.63 fpm | -- | 11.74 fpm |

"Before" is the absolute legacy terrain-only remainder; "after" is the absolute
raw-latch-minus-reconstruction residual. These are development-corpus figures,
not guaranteed accuracy. Timing and model structure were investigated on much of
the same material. Leave-one-capture-out and nested model-selection checks imply
a more honest expectation of roughly 4--5 fpm for an ordinary primary contact.

The experimental detail therefore uses a conservative `+/-10 fpm` band only for
a non-bouncing primary contact with compression-derived timing and the ordinary
two-main topology. It uses `+/-15 fpm` for a bounce, last-airborne timing
fallback, ordinary three-main topology, or a multi-bogie topology whose readable
flight model supplied the arm. A clustered multi-bogie topology whose arm had to
be recovered from telemetry retains `+/-50 fpm`: the rollout fit is sensitive to
which truck settles during derotation.

The configuration-first path was checked on four live ToLiss A340 captures. Its
raw-latch-minus-model residuals were `-6.16`, `+2.41`, `+3.41`, and `+2.97 fpm`;
the corresponding arms occupied `-4.79` to `-2.89 ft`, instead of the telemetry-
only range `-10.30` to `-0.17 ft`. The worst telemetry-only residual in the same
current pipeline was `+36.22 fpm`. This supports the narrower configured-geometry
band, but remains a small same-aircraft development sample. These are engineering
reporting bands, not formal 95% confidence intervals. A350 coverage is likewise
too small for a separate aircraft-type accuracy claim.

## 8. VSI and sampling rate

`VERTICAL SPEED` is recorded for comparison but is not used as the primary
touchdown rate. In captured MSFS traces it behaved like a strongly smoothed
signal; fits to multiple aircraft produced an approximately one-second first-
order lag. During a flare this can substantially overstate the current descent
rate, while during a rapidly worsening descent it can understate it.

Polling rate alone does not explain every disagreement between landing trackers:

- a correctly attributed touchdown latch remains the same when sampled at
  different ordinary rates;
- an instantaneous or lagged VSI result depends on which pre-contact sample a
  client selects;
- a bounce can replace the latch before a slow client observes the earlier
  contact;
- a G peak depends directly on cadence, phase, and window length.

For that reason the chart labels VSI as lagged and never silently substitutes it
for aircraft vertical rate.

## 9. G-load

There is no single touchdown-scoped G value in the captured telemetry. The app
records instantaneous `G FORCE` every simulator frame and reports explicit peak
windows:

- maximum G from contact through 150 ms;
- maximum G from contact through 2 seconds.

Both windows are strictly cut off before the next contact in a bounce. The short
window describes the immediate impact response; the longer window exposes a later
airframe or gear response without pretending that it occurred at the exact
contact instant.

These values describe simulator telemetry. They are not aircraft-specific hard-
landing inspection thresholds and must not be used as a maintenance decision.

## 10. Controls, power, attitude, and gear

The saved black-box trace includes only data used by the dashboard: pitch, bank,
angle of attack, body rates, longitudinal and lateral loads, pilot commands,
control-surface positions, spoilers, flaps, brakes, engine power, throttles,
reverse, wind, and active contact-point compression.

Some add-on aircraft process or delay the SimConnect `YOKE Y POSITION` value. On
Windows, the app also samples available joystick Y axes directly. For each live
source it searches a bounded set of time lags and correlates the raw axis with the
aircraft-processed pitch command during the airborne portion. A sufficiently
correlated source is selected, its direction is normalized, and only live raw
controller columns are retained in the landing record. The compact UI label
identifies the selected controller index and measured lag.

This logic affects the controls graph only; it does not affect the landing-rate
calculation.

## 11. Validation evidence

The method was developed against full-rate traces in which several third-party
trackers ran during the same landings. Representative results were:

| Landing | Surface latch | Last-air VSI | GEES / FSiPanel | Volanta |
| --- | ---: | ---: | ---: | ---: |
| clean 1 | 176.59 | 214.66 | 177 | 215 |
| clean 2 | 212.02 | 239.56 | 212 | 240 |
| deliberately firm | 254.42 | 449.48 | 254 | 452 |

The matches show that applications can report different, internally consistent
families of measurements from the same simulator event. They do not prove the
private implementation or fixed polling cadence of any closed-source product.

The deliberately firm trace also demonstrated the terrain term. The aircraft
reference point descended at roughly 347 fpm while the local surface elevation
fell at roughly 89 fpm along the flight path; the simulator latched approximately
254 fpm of surface closure. The few-fpm difference was the old terrain-only
remainder; under the frozen temporal reconstruction it is interpreted and tested
as a latch-minus-model residual rather than assigned wholesale to runway slope.

The analysis pipeline is additionally covered by synthetic traces with known
vertical motion, terrain profiles, duplicate frames, timing irregularity,
outliers, same-frame latch updates, stale latches, and bounced contacts. Golden
tests verify decomposition, per-contact window boundaries, storage round trips,
and compatibility with older capture schemas.

The temporal reconstruction was also reimplemented independently from the raw
CSV files. It reproduced the 20-contact result exactly. In the telemetry fallback
validation, replacing each capture's own recovered arm with the median arm from
other captures of the same aircraft type changed primary-contact MAE from 2.86
to 3.25 fpm, which checks that post-touchdown geometry calibration is not
supplying the target latch value.

## 12. Known limitations

- SimConnect publishes frame snapshots, not every internal physics substep.
- `GROUND ALTITUDE` and the collision surface under a wheel need not sample the
  same scenery triangle.
- Contact-point numbering has no semantic meaning in the calculation. The
  topology gate accepts two or three inferred main points followed by one nose,
  plus a conservative clustered path for six-or-more settled wheel points when
  a dominant derotation gap separates main and nose/helper clusters. Other
  ambiguous layouts remain unavailable.
- Nose-first and mixed main/nose contacts deliberately fall back to the legacy
  metrics; an explicitly supplied arm does not bypass the per-contact gate.
- The surface latch is unavailable when no update can be attributed safely.
- Older stored formats can lack terrain, horizontal-load, controller, or latch-
  quality fields; the UI identifies legacy quality where applicable.
- Airport and runway identification is proximity-based and depends on the cached
  simulator facility data.
- The application measures simulator behavior, not certified real-aircraft
  structural loads or maintenance criteria.

## 13. Data and reproducibility

Landing records are local gzip-compressed JSON. Current records use a documented
columnar v7 layout; earlier object-based records remain readable. Full-rate raw
capture is opt-in because it can consume substantial disk space, and it is stored
separately from the compact landing history.

See [format-v7.md](format-v7.md) for the storage contract. The simulator variables
and delivery semantics are documented by the MSFS 2024 SDK:

- [Aircraft miscellaneous variables](https://docs.flightsimulator.com/msfs2024/flighting/programming-apis/simvars/aircraft-simvars/aircraft-misc-variables/)
- [Aircraft flight-model variables](https://docs.flightsimulator.com/msfs2024/flighting/programming-apis/simvars/aircraft-simvars/aircraft-flightmodel-variables/)
- [SimConnect RequestDataOnSimObject](https://docs.flightsimulator.com/msfs2024/flighting/programming-apis/simconnect/api-reference/events-and-data/simconnect_requestdataonsimobject/)
