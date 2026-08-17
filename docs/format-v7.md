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

Series are stored column-wise. For example, `t`, `iv`, `vsi`, and `g` are parallel arrays for time, inertial vertical rate, indicated vertical rate, and vertical load. The complete key-to-property mapping is defined by the `DataMember(Name=...)` declarations in `LandingRecordFile.cs`.

All columns inside one series must have exactly the same length. Writers validate this invariant before serialization; readers validate it before constructing point objects. A mismatched file is rejected as damaged rather than partially interpreted.

## Non-finite values

JSON has no standard representation for `NaN` or infinity. Format v7 stores any non-finite `double` as the single numeric value exposed by `LandingRecord.NonFiniteStorageSentinel` (`-1.7976931348623157E+308`) and restores it to `NaN` on read. The sentinel is a storage encoding only and must never be exposed to analysis or UI code.

Regression tests enforce both sides of the rule:

- serialized v7 JSON contains no `NaN` or infinity literal;
- a decoded record contains no storage sentinel in any serialized scalar or column.

## Compatibility

The application reads object-oriented landing records v1–v6 and columnar v7. The summary index is reconciled with the independent detail files on load, so it is rebuilt when missing or damaged and repaired when a detail commit succeeded but the following index update did not. New v7 writes do not rewrite old records unless that record is explicitly saved again.
