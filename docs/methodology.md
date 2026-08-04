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

The application makes the first value its primary pilot-performance measurement,
shows the second as the simulator's surface-relative result, and keeps VSI as a
diagnostic trace.

## 2. Capture window and timing

The recorder uses SimConnect and captures full telemetry at
`SIMCONNECT_PERIOD_SIM_FRAME` near the ground. A small guard definition remains
active throughout the flight; the full definition is enabled below 3,000 ft AGL
and disabled above 3,500 ft AGL. This hysteresis avoids repeatedly switching at
one threshold and removes the full telemetry cost during cruise.

A landing episode begins at the descending 500 ft AGL gate and ends after 15
seconds of rollout. A bounded pre-roll is retained so enabling diagnostic capture
shortly before an event does not lose the immediately preceding samples.

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
is used. This fallback also applies when a bounce leaves less than 0.15 seconds of
continuous airborne data for a stable fit.

## 5. Aircraft vertical rate

The primary value uses `VELOCITY WORLD Y`, the aircraft's vertical velocity in
the simulator world frame. Internally the analyzer uses a descent-positive
convention:

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

If no attributable update is detected, surface closure and every value derived
from it are stored as unavailable. The independent terrain measurement remains
valid. This prevents a stale latch from being presented as the result of the
current landing.

On the vertical-rate chart, the closure value is shown by both a horizontal
dashed line and a matching vertical dashed contact marker. Together they mean
"this surface-relative value belongs to this contact"; they are not a time series
of constant aircraft velocity.

## 7. Terrain contribution and unresolved remainder

The difference between surface closure and aircraft vertical rate is useful, but
calling all of it "runway slope" would overstate what is known. The internal
descent-positive decomposition is:

```text
surface_delta = surface_closure - aircraft_rate
surface_delta = terrain_contribution + unresolved
```

`GROUND ALTITUDE` is fitted against time over the final approximately 0.20
seconds of airborne motion. Its time derivative, multiplied by 60, is the local
terrain contribution in fpm. This is equivalent to local terrain slope multiplied
by groundspeed, but avoids assuming a globally planar runway. An isolated ground-
altitude sample more than five feet from its local median is replaced by a
time-interpolated value before the fit.

The remainder can include collision-mesh sampling under a wheel rather than the
aircraft reference point, local surface curvature, surface-normal geometry,
rotation about the reference point, and physics-substep timing. It is therefore
labelled **unresolved**, not noise and not terrain.

Example in the descent-positive convention:

```text
aircraft rate       165 fpm
terrain contribution +7 fpm
unresolved           -7 fpm
surface closure     165 fpm
```

Equal headline values are correct here because the two explanatory terms cancel.

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
254 fpm of surface closure. The remaining few fpm are within the unresolved
geometry and sampling term described above.

The analysis pipeline is additionally covered by synthetic traces with known
vertical motion, terrain profiles, duplicate frames, timing irregularity,
outliers, same-frame latch updates, stale latches, and bounced contacts. Golden
tests verify decomposition, per-contact window boundaries, storage round trips,
and compatibility with older capture schemas.

## 12. Known limitations

- SimConnect publishes frame snapshots, not every internal physics substep.
- `GROUND ALTITUDE` and the collision surface under a wheel need not sample the
  same scenery triangle.
- Contact-point numbering and fidelity are aircraft-dependent.
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
