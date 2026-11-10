# Ground pause and settling

Numeric excerpt from capture `a235d7aacb4288cbf9d7e6ee38d5f3e8`, client 0.9.9,
received 2026-11-08. Source ZIP SHA-256:
`60571023bec9b14876f753d26f0276a01cbffbb61b2e661fa41c7e44e68728c0`.

The 106 retained rows contain grounded history, a clock offset from a confirmed
Active Pause, one airborne row, and settling. The source-pose columns come from
the separate `source-state.bin`, decoded using its published binary layout and
cross-checked with `ReplaySourceTimelineCodec`. The observation carried by the
first airborne row belongs inside the pause and has AGL 10.4399995803833 ft.
Coordinates, controller information and source identity are omitted. Tests use
fixed synthetic coordinates/identity and retain the recorded clocks, heights,
velocities, attitude and source observation times.

This fixture must produce zero contacts. A distinct post-relocation airborne
interval must allow a subsequent approach without requiring a ground reset.

The companion `ground-pause-then-bounce.csv` is an anonymized excerpt from
`bbd651a00f5b2692ed0d6a9b0c170498` (C172SP G1000 Passengers). After a pause the
aircraft continues moving on the ground for about 15 seconds, then has a short
airborne interval ending at sequence 22244. That contact must remain accepted:
the resumed ground interval establishes a new departure origin. Both files use
the same projection and omit source identifiers and coordinates.
