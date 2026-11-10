# Recorded grounded numerical noise

`ground-noise-prefix.csv` contains 40 consecutive grounded observations extracted
from a privately supplied simulator recording. The fixture preserves the
round-trip double values of the time, altitude, velocity, attitude, angular-rate,
G, and acceleration channels used by ground-departure filtering. It contains no
capture, installation, aircraft, controller, or source-evidence identifiers.
Horizontal coordinates are omitted; the test supplies a stationary synthetic
position at latitude/longitude zero. Sample sequence numbers are assigned 1–40.

Extraction used `TelemetryAnalysisFrames.Prepare` followed by
`TelemetryDeduplicator.Deduplicate`, then retained the 40 grounded observations
immediately preceding the investigated departure. The CSV time is the prepared
analysis clock; the test supplies that same clock as simulation time. All rows
are active, running observations in one synthetic flight segment. Values were
written with the invariant-culture `R` format, without rounding. The file is a
compact regression fixture, not a full `TelemetryCsv` recording.

The largest prefix-altitude difference from the final grounded observation is
`0.0000067956479259578374` metres (approximately 6.796 micrometres). The causal
control changes only those prefix altitudes to the final value. It retains every
other fixture value and the identical continuation.

The test appends two controlled vertical paths with 50 ms sampling: a 2-second,
1-metre excursion and a 20-second, 10-metre excursion. Their height is
`A / 2 * (1 - cos(2 * pi * t / T))`; WORLD Y is its exact time derivative.
Each path has one airborne interval ending in one ground contact. The old
cumulative-motion calculation rejected both paths after the recorded prefix,
while the altitude-only controls retained their contacts. The synthetic
horizontal position also reproduces this failure.

The fixture establishes that grounded numerical noise must preserve these
coherent later departures. It makes no claim about the physical meaning of the
original recording's later ground transition, which is intentionally absent.
Regression coverage compares live continuity, public analysis, same-time numeric
revisions, checkpoint round trips, and the recorder's saved captures. Raw input
observations must remain unchanged by each operation.
