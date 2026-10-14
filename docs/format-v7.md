# Landing record format v7

Each landing remains an independent `*.landing.json.gz` file. The gzip member contains strict JSON and is the canonical, portable detail record; `landing-index.json.gz` is a rebuildable summary cache.

## Layout

The root object contains a discriminator and four payload members:

- `layout`: integer `7`;
- `summary`: scalar landing metadata and measurement results;
- `series`: time-aligned flight-data columns;
- `engines`: one column set per active engine;
- `contacts`: one column set per active contact point.

The `summary` object may also contain the optional closure-reconstruction scalar
set: model identifier, availability, modeled closure and its inertial/terrain/
pitch components, raw-minus-model residual, uncertainty band, fit-point count,
signed gear arm, geometry quality, and arm provenance. These are ordinary
`LandingRecord` data members; they do not add or change any time-series column.
Older v7 files omit them and are read as reconstruction unavailable.
Arm provenance is an optional string (`FlightModelConfig`, `Telemetry`, or the
legacy `Provided` value). Older v7 records without the string retain the prior
boolean telemetry-provenance fallback.

New recordings also include optional adaptive-impact summary members:
`ImpactAnalysisVersion` (currently `1`), nullable `ImpactPeakG`,
`ImpactPeakOffsetSeconds`, `ImpactEndOffsetSeconds`, `ImpactWindowSeconds`, and `ImpactStatus`.
Offsets use the same contact-relative origin as the saved series. Window duration
is measured from the first observed on-ground frame, not from the earlier
estimated contact used as the series origin. Status is
`Settled`, `NoDistinctImpulse`, `Liftoff`, `Incomplete`, `InvalidTelemetry`, or
`Unavailable`. Only the first three statuses with valid peak/timing fields can
receive a feel category. The G value is the original sampled peak, not rounded
to the precision of the saved time-series columns. These additive members do
not change the v7 column layout.

Older records without these fields are not recalculated or migrated. The UI
uses their existing `PeakG150Milliseconds` value. A missing or invalid adaptive
result in a new record does not fall back to the legacy peak. The rebuildable
summary index simply copies the saved adaptive members alongside other scalars.

Series are stored column-wise. For example, `t`, `iv`, `vsi`, and `g` are parallel arrays for time, inertial vertical rate, indicated vertical rate, and vertical load. The complete key-to-property mapping is defined by the `DataMember(Name=...)` declarations in `LandingRecordFile.cs`.

All columns inside one series must have exactly the same length. Writers validate this invariant before serialization; readers validate it before constructing point objects. A mismatched file is rejected as damaged rather than partially interpreted.

## Non-finite values

JSON has no standard representation for `NaN` or infinity. Format v7 stores any non-finite `double` as the single numeric value exposed by `LandingRecord.NonFiniteStorageSentinel` (`-1.7976931348623157E+308`) and restores it to `NaN` on read. The sentinel is a storage encoding only and must never be exposed to analysis or UI code.

Regression tests enforce both sides of the rule:

- serialized v7 JSON contains no `NaN` or infinity literal;
- a decoded record contains no storage sentinel in any serialized scalar or column.

## Compatibility

### Reproducible analysis geometry

New native records optionally include `summary.AnalysisGeometry` (version 1).
It stores the exact geometry inputs supplied to the analyzer: source, signed
longitudinal arm and quality, every configured main-wheel index and arm, nose
indices, and whether telemetry recovery is enabled. Arms use the velocity-reference
longitudinal frame in feet (`velocity_reference_longitudinal_feet`), after both
the configuration reference-datum translation and the telemetry datum correction.
This is input provenance, not a claim that configured roles passed the analyzer's
contact-evidence checks or that reconstructed closure is available.

The snapshot identifies the prepared, deduplicated physical segment by its sample
count, first/last sequence and simulator time, plus SHA-256 of each canonical
`TelemetryCsv.Format` row followed by LF (UTF-8 without BOM). Version 1 uses
`InputFingerprintFormat=telemetry_csv_v7`. Restore requires that exact input;
altered interior samples, other segments and unsupported coordinate frames are
rejected. Reproduction requires the full raw capture and compatible analyzer
version, not only the compact history. Snapshots contain no file paths or CFG text.
Absent snapshots on older recordings remain absent; replay cannot invent the
original installed aircraft's options. Older v7 readers ignore this additive field.

### Discrete contact phases

The normal 50 ms history schedule also preserves the real frames immediately
before and after every aircraft `OnGround` transition and the accepted contact's
raw witness. Extra frames do not shift that schedule. All pose, input, engine and
contact columns use the same retained rows. No synthetic ground state is inserted
at estimated contact zero. This affects new saves; old files are not rewritten.

### Capture diagnostics

`decisions.json` keeps format version 1, with a limit of 8,192 entries and 4 MiB
expanded on the server. The client uses a conservative 3,600,000-byte estimate
to leave serialization headroom. Truncation remains explicit. Deploy the expanded
server limits before distributing a client that can exceed the previous limits.

Replay summaries are emitted for every contact before detailed candidates.
Constant/missing input candidates share records containing `role`, `window`
(0 approach, 1 complete sequence, 2 ground fallback), `source_count`, and
`source_0` through `source_15`. Repeated canonical episode bindings refer to the
owning contact; contact-local mapping and suspension outcomes remain separate.
`relative_contact_time` and `contact_time_seconds` disambiguate the clocks.

`geometry.cfg` records lookup, parsing, ambiguity, datum or arm-validation outcomes.
An unmatched readable geometry is not proof that a CFG was absent or encrypted.
`simconnect_request_rejected` includes known operation identity, optional/required
capability, available request/definition/event IDs, connection generation and
SDK/application versions. Unknown or expired packet associations stay unknown.

Rejected transitions without recent active airborne evidence in the same flight
segment do not start automatic captures. Interrupted flights retain diagnostic
captures even when no contact can be measured; this is not a filter on result count.
Manual raw capture remains available independently of the automatic capture gate.

Only short diagnostic-only rejected transitions with flight evidence are batched: at most 16 captures
or 30 seconds, with earlier flush at a new aircraft, physical segment, rejection
context, real landing or disconnect. Original samples and rejection details are
retained, with `diagnostic_index` and `source_episode_id`; full detail fields or
journal limits trigger an earlier flush instead of data loss. Normal landing
captures are not delayed. A forced process termination can lose a pending batch,
as it can any active capture; normal shutdown flushes it.

### Optional 3D replay and physical inputs

New recordings add two optional root members without changing `layout: 7`:

- `pose`: `lat`, `lon`, `altFt`, `groundFt`, `headingTrueDeg` arrays aligned with
  `series.t`. Coordinates are degrees; both altitudes are feet MSL; heading is true,
  not magnetic. Values are nullable numbers, with `null` for unavailable or
  non-finite observations. Zero latitude/longitude is valid, not a sentinel.
- `inputs`: `value`, `valid`, `age` arrays aligned with `series.t`. Every row has
  exactly `summary.RawControllerAxesSourceIndices.Count` entries. The source mapping
  uses `deviceSlot * 6 + axis`, where the axis order is X, Y, Z, R, U, V and slots
  correspond to `ControlInputSources`. Values are normalized physical travel
  from -100 to +100 percent; age is seconds. Unsupported/disconnected axes are
  unavailable, never inferred from a centered value. Only axes with a valid sample
  are included. These new numeric arrays use JSON `null` for non-finite values.

The optional `summary.RunwayGeometry` is a snapshot from the installed simulator
scenery: airport identity, runway center in degrees, altitude/length/width in metres,
true heading, end designations, surface type, provenance, and retrieval UTC. It is
not an airport reference point or a heading inferred from the aircraft's nose.
Missing or ambiguous geometry does not prevent replay of the actual flight track.

Optional `summary.EstimatedContactTimeSeconds` records the original simulator
clock used as the contact's series zero. It aligns contacts from the same episode
even when their stored samples have different downsampling phases. Older files
omit it; zero is a valid clock value, not a missing-data marker. No historical
measurements or series are recalculated when this field is absent.

Two additive nullable members, `PrimaryThresholdOffsetMeters` and
`SecondaryThresholdOffsetMeters`, store the displacement inward from each
physical pavement end. Zero is a verified undisplaced threshold; absent/null
means unknown, not measured zero. Values must be finite, non-negative, shorter
than the runway, and together leave a positive distance between thresholds.
These are not declared LDA values. A missing optional facility reply leaves the
original dimensions usable. Cloud metadata merging retains known offsets but
does not silently resolve conflicting known values. Old files need no rewrite.

Optional `PrimaryPapi` / `SecondaryPapi` contain `StationFromEndMeters` and
`AngleDegrees`. The station is the PAPI installation reference, not a measured
glide-path intersection. `PrimaryPapiQueried` / `SecondaryPapiQueried` distinguish
a complete pair of side queries with no PAPI (true plus null) from missing,
legacy, partial or conflicting data. Optional `PrimaryIlsGlideSlope` /
`SecondaryIlsGlideSlope` contain the GS `Ident`, `Region`, latitude/longitude,
altitude in metres and angle in degrees. Each end is independent. Optional
guidance validation does not invalidate base geometry; merging preserves known
guidance and does not silently replace conflicting values. These are additive
snapshot fields, with no historical recalculation or automatic file migration.

An absent optional block is supported; a present block with mismatched column
lengths/row widths or an invalid source mapping is rejected. Old files are not
rewritten or supplied with invented coordinates. Old v7 readers can still read
the original landing metrics, but old software may drop these additive blocks
if it resaves a new file; use the new version on devices sharing these recordings.

The 3D view uses recorded position/attitude, with interpolation only across short
continuous sample intervals, and a generic aircraft rather than measured model
geometry. Camera zoom never truncates the recorded trail. New native bounce
records retain the episode's final rollout even when viewing its first contact.

The application reads object-oriented landing records v1–v6 and columnar v7. The summary index is reconciled with the independent detail files on load, so it is rebuilt when missing or damaged and repaired when a detail commit succeeded but the following index update did not. New v7 writes do not rewrite old records unless that record is explicitly saved again.
