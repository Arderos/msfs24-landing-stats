# Landing record format v7

Each landing remains an independent `*.landing.json.gz` file. The gzip member contains strict JSON and is the canonical, portable detail record; `landing-index.json.gz` is a rebuildable summary cache.

## Layout

The root object contains a discriminator and four payload members:

- `layout`: integer `7`;
- `summary`: scalar landing metadata and measurement results;
- `series`: time-aligned flight-data columns;
- `engines`: one column set per active engine;
- `contacts`: one column set per active contact point.

Series are stored column-wise. For example, `t`, `iv`, `vsi`, and `g` are parallel arrays for time, inertial vertical rate, indicated vertical rate, and vertical load. The complete key-to-property mapping is defined by the `DataMember(Name=...)` declarations in `LandingRecordFile.cs`.

All columns inside one series must have exactly the same length. Writers validate this invariant before serialization; readers validate it before constructing point objects. A mismatched file is rejected as damaged rather than partially interpreted.

## Non-finite values

JSON has no standard representation for `NaN` or infinity. Format v7 stores any non-finite `double` as the single numeric value exposed by `LandingRecord.NonFiniteStorageSentinel` (`-1.7976931348623157E+308`) and restores it to `NaN` on read. The sentinel is a storage encoding only and must never be exposed to analysis or UI code.

Regression tests enforce both sides of the rule:

- serialized v7 JSON contains no `NaN` or infinity literal;
- a decoded record contains no storage sentinel in any serialized scalar or column.

## Compatibility

The application reads object-oriented landing records v1–v6 and columnar v7. If the summary index is missing or damaged, it is rebuilt from the independent detail files. New v7 writes do not rewrite old records unless that record is explicitly saved again.
