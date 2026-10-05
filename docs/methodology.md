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
seconds of rollout. A bounded pre-roll ensures the episode includes the samples
immediately preceding the gate crossing. Pre-roll age uses the recorder's
monotonic receipt clock, not simulation time, because the
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
an early main cluster from a later nose/helper cluster. That established path
retains priority. If it refuses, the analyzer also tries multiple main points
followed by a single delayed nose point, with the same sustained-contact,
derotation, and foreign-contact checks as the simpler layout. Weak or mixed
evidence makes reconstruction unavailable rather than triggering an index-based
guess.

The last point is accepted as the nose only when it arrives at least 0.20 seconds
after the latest main and pitch moves at least 0.5 degrees in the normal nose-
lowering direction. The analyzer then finds rollout intervals where all inferred
mains are compressed and the nose is not. With `H = PLANE ALTITUDE -
GROUND ALTITUDE`, pitch `theta`, and mean main compression `c_bar`, it solves:

```text
H(t) = b0 + A_theta theta(t) + A_c c_bar(t) + error(t)
L = A_theta + D
```

The additional multi-main/single-nose path requires at least 12 usable main-only
observations and a stability check before accepting even the inferred topology.
It refits the pitch arm on the first and last 70% of elapsed observation time,
then with each of five contiguous time blocks omitted. Every subset must omit
some observations, retain at least six, and pass the existing pitch/compression
identifiability check. Each refitted arm must differ from the full fit by no
more than 5 ft. The datum offset is held fixed, so these are also differences
in final longitudinal arm. These are conservative engineering acceptance limits,
not a statistical confidence interval or proof of physical gear roles. This
additional inference retains the multi-bogie `+/-50 fpm` reporting band when the
arm is recovered from telemetry. Existing topology paths and verified configured
geometry do not acquire this new gate.

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

The clustered-wheel classification is retained independently of the size of its
first contact group. The iniBuilds A380 capture has only two early channels,
followed by four later channels and an isolated final channel. That sequence
still uses the clustered fallback; two early channels must not select the
ordinary two-main `+/-10 fpm` band. These inferred groups do not establish the
physical role of every channel when readable configuration geometry is absent.

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
records instantaneous `G FORCE` every simulator frame. **Impact G** is the maximum
original sample within the selected contact's adaptive impact window. It is not
an average, a velocity derivative, a vector magnitude including braking loads,
or a maximum over the whole landing sequence.

### Adaptive impact window (analysis version 1)

The window starts at the first observed on-ground frame of the accepted contact.
It closes when the load response and vertical motion have settled, or immediately
before the first airborne frame. Each bounce has its own window. No wheel count,
gear geometry, or all-gears-on-ground condition is required; the same logic also
applies to water contacts. A later rollout bump after a confirmed quiet interval
is not part of the initial impact.

Settling is evaluated using time-weighted linear fits over at least 0.30 s
(extended to 3.1 frame intervals at a low cadence), with at least four samples.
All conditions must remain satisfied for 0.15 s:

- G slope at most 0.10 G/s, maximum fit residual 0.015 G, and range 0.05 G;
- absolute vertical motion relative to the local terrain trend at most
  `clamp(0.20 * abs(incoming_vertical_speed), 0.35, 0.75)` ft/s, with maximum
  velocity-fit residual 0.12 ft/s;
- when terrain altitude is available, its maximum fit residual at most 0.10 ft;
- when pitch/roll rate channels are available, each maximum linear-fit residual
  at most 0.003 rad/s.

The incoming vertical speed is estimated from the last contiguous airborne
half-second. Without a usable estimate, the conservative 0.35 ft/s band is used.
These short intervals confirm a state, **not** a fixed deadline after touchdown.
The impact can last longer. Smooth derotation can keep the window open; the
algorithm does not claim an exact physical end time of a damped response.

Native analysis uses simulation timestamps and keeps the last duplicate frame.
An interruption over 0.15 s, backward time, slew, missing required G/vertical
velocity, an all-zero G window, or an unfinished recording cannot establish a
complete result. Fully absent optional terrain/rate channels are supported;
an intermittent invalid channel is not silently replaced by zero. A valid
liftoff ends this contact without including airborne samples or the next impact.
An incomplete observation can retain its observed peak, but receives no
SMOOTH/NORMAL/FIRM label.

### Landing feel

The unrounded peak determines the category:

| Recorded adaptive peak | Label |
| --- | --- |
| below 1.20 G | SMOOTH |
| 1.20 G to below 1.50 G | NORMAL |
| 1.50 G and above | FIRM |

These are application categories for the **load response of one contact**, not
pilot scores or aircraft-specific damage/inspection thresholds. The 1.20 G
boundary is an engineering/UI choice. The 1.50 G boundary has published
operational precedents in studies of [B737-800 flights](https://onlinelibrary.wiley.com/doi/10.1111/risa.13680)
and [A320 flights](https://www.jksaa.org/archive/view_article?pid=jksaa-32-2-135),
but those studies do not validate our exact sampling/window method or establish
a universal passenger-comfort standard. FIRM does not mean a structural hard
landing. Sampling can miss a peak, especially close to a category boundary.

The former 150 ms and 2 s peaks remain in saved records as legacy diagnostics,
strictly cut off before the next contact. For newly recorded landings they no
longer control the feel label.
Aircraft vertical rate and surface closure are unchanged and are not substituted
for G when an impact cannot be classified.

Older landings are **not reanalysed or rewritten**. Their existing saved 150 ms
peak is displayed and classified using the same numeric thresholds. This is not
equivalent to an adaptive-window measurement. The new adaptive analysis applies
only to newly recorded contacts.
A new contact with an incomplete adaptive result never falls back to its 150 ms
peak to obtain a label.

## 10. 3D replay, controls, power, attitude, and gear

The saved black-box trace includes only data used by the dashboard: pitch, bank,
angle of attack, body rates, longitudinal and lateral loads, pilot commands,
control-surface positions, spoilers, flaps, brakes, engine power, throttles,
reverse, wind, and active contact-point compression. New recordings also retain
latitude, longitude, altitude, ground altitude and true heading for the 3D view.

Some add-on aircraft process or delay the SimConnect `YOKE Y POSITION` value. On
Windows, the app samples all available X/Y/Z/R/U/V axes from up to eight devices
directly, including separate pedal devices. Device capability flags and ranges
determine availability; an unplugged or unsupported axis is not zero input.
Names are physical axis names, not inferred simulator assignments. For each live Y
source it searches a bounded set of time lags and correlates the raw axis with the
aircraft-processed pitch command during the airborne portion. A sufficiently
correlated source is selected, its direction is normalized, and only live raw
controller columns are retained in the landing record. This legacy source-match
metadata remains available for older Y-only recordings.

The 3D view identifies pitch, roll and yaw roles by correlating the recorded
physical axes with their corresponding simulator pilot-command channels, allowing
for processing lag and inverted axes. Windows axis letters do not establish these
roles. Pitch and roll use the entire available approach before the first contact,
not individually selected short windows. Each candidate's delay is fitted over that
whole interval. All contacts in a bounce sequence use this same approach. Ground
samples are a fallback only when no approach correlation can be computed; a ground
mode cannot veto or reverse an available approach match. Yaw uses the complete saved
sequence because directional input may first move during rollout. Lag interpolation
never crosses a telemetry gap or an airborne/ground transition.

Candidates are ranked by absolute Pearson correlation, without a minimum correlation,
minimum winner margin, amplitude cutoff or statistical-confidence veto. Pitch and roll
are ranked jointly as distinct axes. Their pair score is
`abs(r_pitch) + abs(r_roll) + 0.5 * min(abs(r_pitch), abs(r_roll))` when both axes belong
to the same recorded device; otherwise the score is just the first two terms. This
is a strong but bounded device preference, not a hard binding constraint. A sufficiently
better cross-device pair can win. The weight is an explicit heuristic, not a calibrated
probability. Device names, aircraft types and axis letters do not determine roles.
Yaw is ranked independently among channels not already assigned to pitch or roll.

Constant channels have undefined correlation; exactly zero covariance cannot supply
a polarity, and exact assignment ties remain unresolved. Missing/invalid/stale input
and malformed duplicate source IDs are data-validity failures, not score thresholds.
This is relative inference: it does not prove a simulator binding if none of the
recorded physical signals actually controls the corresponding simulator channel.
Legacy Y-only recordings remain pitch-only, including sparse Y-only CSV imports.
The selected axis is
displayed at its original recorded time and amplitude, before simulator curves;
the matching lag does not shift the animation. This is not a control-surface
animation and does not claim to recover bindings when the add-on's command channel
is unavailable. Previously selected legacy pitch metadata remains supported.

The stick square uses down for pull, up for push and right for right roll. A
single available stick axis moves a dot along the centre guide, with the missing
axis explicitly labelled. The centred display coordinate is only a visual fallback;
the missing measurement remains unknown, not zero. The bar beneath it shows the single centred physical yaw
axis (pedals or stick twist), not independent toe brakes. Tooltips identify the
recorded source. Missing physical input is never replaced with simulator input.
Translucent mini charts share replay time: pitch, bank and yaw in the left column;
ground speed, vertical speed and vertical G in the right. Yaw is the signed nose
angle relative to the selected runway direction (positive right), unavailable without
resolved runway geometry. VS is recorded world vertical speed, positive climbing,
not surface closure. All saved contacts in the same episode are marked together
on the surface from each contact's own analysed zero time. The markers are visible
throughout playback; extra OnGround transitions do not create additional contacts.

This input-capture logic does not affect the landing-rate calculation.

History without usable recorded position and heading retains the Controls tab and
its original input/surface charts. Records with a usable 3D track show reconstruction
in the same tab position. Selection follows the tab when switching between these
record types; other selected charts stay selected. Availability is based on the
actual series, not the file version or recording date; no history files are rewritten.

The 3D aircraft follows recorded geodetic positions, projected to a local
east/up/south frame in metres. Orientation uses true heading and the simulator's
pitch/bank convention; quaternion interpolation avoids a long spin at north.
Gaps longer than 0.5 seconds are not bridged with invented motion. The generic
model is not the installed aircraft mesh. A single visual height registration
keeps the recorded body trajectory intact. Its symbolic landing gear is registered
to the exact selected contact and adapts to the displayed surface during support;
the nose/high-side wheel is not forced down by the global ground flag. Across
airborne intervals the visual strut datum is interpolated between support anchors,
with up to 1 cm of clearance protection for the generic tyres, tapering to zero at
contact. Only gear transforms change: neither the body nor its airborne arc is
clamped to the surface. This does not claim measured wheel locations or suspension
travel, particularly for hull/water contacts. The cross marks the interpolated
aircraft-reference projection at exact contact time, not individual wheel coordinates.
The trail preserves the complete saved recording, independent of chart zoom.

Runways are requested afresh from the installed MSFS scenery after each landing
episode completes (one batch shared by all its contacts),
not from a global third-party runway database. A complete facility response is
required. Selection uses recorded ground track and touchdown position inside the
runway footprint, not aircraft heading during a crab. Ambiguous associations,
water strips and missing data leave the scene without a runway. The ground strip
is a visualization of the recorded terrain-height profile, not scenery terrain.
Facility altitude is a single reference height, not a runway elevation mesh.
Where the recording passes over the runway footprint, pavement and its markings
follow the measured longitudinal ground-height profile; outside that observed
span the nearest measured height is held constant. With no observations inside
the footprint, facility altitude is the fallback. No cross-slope is inferred.
The surrounding ground strip is cut out beneath the pavement so the two surfaces
cannot intersect. This display geometry never changes recorded aircraft heights
or any landing measurements.

The pavement uses the **actual SimConnect length and width in metres**, including
non-round scenery values, with no aspect-ratio exaggeration. Paint fits inside
that footprint. Primary and secondary displaced thresholds are read in separate,
bounded facility requests and associated by native parent IDs, not arrival order.
Optional metadata failure keeps the base runway. Native displaced-threshold
metadata is preserved, but does not position the schematic paint described below.
Four optional PAPI-side queries and bounded ILS glide-slope queries collect
guidance for each end. A missing, invalid or timed-out optional query never
discards the base runway dimensions.

Scenery snapshots are not reused across landing episodes. A missed threshold
request is retried with the next landing's fresh batch. Replies are associated
with the requesting episode, including when analysis finishes later than the
facility query; they never enrich unrelated historical records. Published
snapshots already saved with older landings are left unchanged.

Runway markings are a **schematic reference**, not extracted airport textures or
a claim about its certified approach category. Each direction chooses ILS first,
then PAPI; without usable guidance the centreline and known runway designation
remain, but no aiming-point, threshold-stripe or TDZ pattern is invented.
The aiming rectangles begin at the nominal glide-path intersection. ILS uses the
transmitter's along-runway station, GS altitude and actual GS angle, intersected
with the horizontal plane at facility altitude. This is not an intersection with
the telemetry-derived ground profile or a simulation of the radio field.
SimConnect provides PAPI position and angle but no optical reference elevation.
The approved PAPI-only approximation puts that reference on the runway plane,
so its longitudinal station is the nominal intersection. It is not a measured
PAPI intercept. Only PAPI-2/PAPI-4 lights qualify, not other VASI types.

The display threshold is inferred **290.7965 m before the aiming start**:
15.24 m (50 ft) divided by tan(3 degrees). This is an explicit product convention,
not a claim that every airport has exactly this threshold-crossing height.
For other glide-path angles the actual angle still determines the ILS intercept;
the threshold offset is normalized to the same 3-degree reference. Native
threshold offsets and recorded contact positions never move this paint lattice.
If the inferred threshold lies outside the footprint, its piano keys are omitted,
not moved to the physical end. All individual paint rectangles must fit completely
inside the actual pavement and their direction's non-overlapping region.

ICAO/EASA TDZ slots are 150 m apart from the inferred threshold. Slot count follows
the geometric usable length categories, using the distance between inferred
thresholds when both ends have guidance. Pattern B uses 3/3/2/2/1/1 stripes per
side at the six slots, excludes groups within 50 m of the aiming rectangles, and
ends with a single stripe per side when truncated. Narrow strips use permitted
pattern A instead of shrinking pattern B below its 1.8 m width and 1.5 m spacing.
ICAO threshold stripes begin 6 m after the inferred threshold and are 30 m long.

US facility regions (including K1–K7 local identifiers) and explicit US ICAO
families select FAA conventions; Canada and unknown regions do not automatically
become FAA. FAA TDZ slots start at 520/1520/2020/2520/3020 ft, with 3/2/2/1/1
stripes per side. There is no extra small group in the 1020 ft aiming slot even
when the aiming point is relocated by guidance. Small bars are 75 by 6 ft, with
5 ft gaps. Aim rectangles are 30 ft wide with a 72 ft inner gap, scaled laterally
on runways no wider than 150 ft. Aim length is 150 ft, or 100 ft below 4200 ft
between thresholds. The 160 ft aiming exclusion and reciprocal 900 ft midpoint
clearance apply. For a single guided end the FAA opposite-aim clearance geometry
is retained; extensions below the published table range remain a reconstruction.
FAA threshold stripes start 20 ft after the inferred threshold, are 150 ft long,
and use 5.75 ft stripes/gaps with explicit standard-width counts. Other widths use
the specified edge clearance. Known runway numbers and L/C/R face each approach;
FAA numbers omit a leading zero. Their footprints follow the inferred threshold
when guidance exists, otherwise the saved native displacement (or physical end
if unknown). Designations follow the pavement profile and leave the centreline
and piano keys clear. Their vector outlines are constructed from the dimensioned
EASA CS ADR-DSN Figure L-2 and FAA AC 150/5340-1M Figure A-6, not an installed
computer font. ICAO glyphs use a uniform enlargement of the minimum 9 m drawing
to 18 m nominal height; FAA glyphs use 60 ft. The longer 6/9 tips retain their
row alignment, and FAA uses the separate footed 1 for a single-digit designation.
Spacing is measured against the nominal row, excluding the extra 6/9 tips
(FAA paragraph 2.3.6), while the complete ink contour is retained for rendering.
Undimensioned outline joins remain schematic. EASA's dimensioned 10/11 gaps are
retained; other ICAO pairs use a 1.5 m clear-gap convention at the 9 m scale,
since L-2 does not explicitly dimension their spacing.
Narrow pavement reduces FAA ordinary digit spacing from 15 to 10 ft first, then
uniformly fits the whole designation if necessary; ICAO is uniformly fitted too.
Extremely narrow reconstructed strips can therefore be below the standard's
minimum glyph size, but never acquire distorted character proportions. Letter
and number share the same scale. Layout uses the L-1/A-1 threshold-stripe and
letter/number gaps. Displaced-area arrows are not drawn.

Declared LDA, approach classification, local exceptions and actual scenery paint
are not collected. Geometric available pavement is a **layout proxy only**, never
reported as LDA. Known unpaved/water surfaces get no white runway paint; unknown
surface retains a schematic paved fallback. The runway-axis tooltip states the
convention, actual dimensions and guidance/threshold assumptions. All paint meshes
share pavement profile breakpoints to prevent clipping on longitudinal slopes.
Contact crosses are added independently after the markings. This display does not
grade touchdown distance, recalculate historical landings or change measurements.

References: [EASA CS ADR-DSN.L.525–545](https://www.easa.europa.eu/en/document-library/easy-access-rules/online-publications/easy-access-rules-aerodromes-regulation-eu?erules-id=ERULES-1963177438-2468)
and [FAA AC 150/5340-1M, Change 1](https://www.faa.gov/documentLibrary/media/Advisory_Circular/150-5340-1M-Chg-1-Airport-Markings.pdf).

The runway-axis indicator is the horizontal distance from the recorded aircraft
reference point to the real facility centreline (extended along the approach), in
metres. Left/right follows the selected runway end, not the camera or crabbed nose.
If an old record lacks the end name, unambiguous continuous approach motion may
resolve its direction. Without valid land-runway geometry/direction the distance
is unavailable; the flight trail is never substituted for a runway axis.

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
  topology gate accepts two through four inferred main points followed by one nose,
  plus a conservative clustered path for six-or-more settled wheel points when
  a dominant derotation gap separates main and nose/helper clusters. A further
  multi-main/single-nose fallback requires stable temporal refits as described
  in section 7.3. Other ambiguous layouts remain unavailable.
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
