# Capture archive formats

Version 1 is the existing ZIP of logical capture members. Version 2 keeps the ZIP
transport (`application/zip`, `.zip`) and uses Zstandard level 9 inside it. CSV,
metadata and JSON bytes are unchanged. Upload signing, hashing, nonces and server
deduplication still cover the complete original archive bytes.

The only logical members are `telemetry.csv`, `session.txt`,
`landing-results.json`, `decisions.json`, `controller-catalogs.json` and the optional
`source-state.bin` observation timeline.
Capture-kind validation decides which optional members are required. Both formats
remain readable. This is independent of CSV schema 5, 6 or 7.

In format 2, `session.txt` retains its original physical name and ZIP compression.
Each other member is stored as `<logical-name>.zst`, containing exactly one
ordinary, dictionary-free Zstd frame. An additional `archive-format.bin` member
contains this little-endian binary table:

| Field | Encoding |
| --- | --- |
| Magic | 8 ASCII bytes `LSAR0002` |
| Member count | unsigned byte, 2 through 6 |
| Each member name length | unsigned byte, 1 through 32 |
| Each member name | that many ASCII bytes, one of the names above |
| Each member codec | unsigned byte; 0 for session, 1 otherwise |
| Each decoded byte length | signed Int64, nonnegative |
| Each content checksum | 32 SHA-256 bytes over the exact decoded member |

No duplicate names, extra physical entries, descriptor tails, second Zstd frames,
skippable frames or trailing frame data are accepted. `session.txt` and
`telemetry.csv` are mandatory. The normal Zstd library handles entropy decoding;
a bounded framing check verifies the declared window, frame length and complete
last block before decoding. Framing follows the
[Zstandard specification](https://github.com/facebook/zstd/blob/v1.5.7/doc/zstd_compression_format.md).

Writers request ZIP `NoCompression` for encoded members. .NET Framework emits
DEFLATE stored blocks for that level; newer runtimes may use ZIP method 0.
Readers accept ZIP methods 0 and 8. The encoded Zstd member's expanded ZIP length
is limited to its declared decoded length plus 1% plus 1024 bytes. This also
bounds a compressed outer wrapper. No ZIP64, encryption, split archives or
central-directory padding is accepted. The directory is inspected before
materializing entries: at most seven entries and 32 KiB of directory data.

Readers require seekable input no larger than 256 MiB. Total decoded bytes are
bounded by the caller (receiver default 64 MiB, dashboard 128 MiB, library ceiling
256 MiB); session is at most 16 KiB, decisions 4 MiB, controller metadata 8 MiB,
source-state member 16 MiB (its version-1 structure has a tighter 8,454,152-byte bound).
The Zstd window is at most 128 MiB, reads are at most 64 KiB, and accumulated
decoder-read time is at most ten seconds per member. The independent frame scan
accepts at most 65,536 blocks. Decoded length and SHA-256 are checked at EOF:
callers must consume a member completely to claim content integrity.

Legacy receiver ratio rules remain unchanged. Format 2 uses explicit encoded,
decoded, window, block and time limits instead of the legacy ratio threshold.
For example, highly repetitive valid telemetry may exceed 25:1 without exceeding
any of these resource limits.

## Compatibility and rollout

Deploy readers before enabling writers. `/v1/config` advertises
`archive_formats: [1, 2]`; absence means legacy support only. The upload worker
reports the observed capability with its normal claim-bound result. Only the
parent stores it, tied to the exact configured endpoint with a one-hour lifetime.
Unknown, expired, corrupt or changed-endpoint observations choose legacy ZIP for
new captures. There is no independent network request on the recording path.

RAW chunk manifests freeze the chosen format before publication. Published
automatic/manual reports and chunks always reuse their recorded hash and bytes.
If an already-created format-2 archive meets an old or rolled-back receiver, the
client retains it pending and does not POST or recompress it. Capability loss
changes future captures to legacy format. A retry uses a new signed nonce over
the same original archive. Server ingestion and dashboard indexing never rewrite
source archives.

Server/controller-metadata compatibility remains an independent capability gate.
The same applies to `source_state_formats: [1]`: only an archive actually carrying
`source-state.bin` waits for that capability. Existing ZIP/CSV 5–7 archives without
this member follow their previous validation and delivery paths. Missing evidence
means unknown; it does not mean a malformed capture, replay, or a reason to defer.
An older application has no format-2 reader; retained new-runtime archives must
remain available to the compatible runtime on application rollback. No legacy
queue/archive migration rewrites their format.

These changes are local and unsigned. No receiver deployment or public format
activation was performed as part of the runtime refactor.

## Optional source observation timeline

`source-state.bin` begins with four ASCII bytes `LSR1`, then an Int32 little-endian
CSV row count (1–65,536). Each row has a byte presence tag (0/1); a present row has
exactly 128 bytes. There are no trailing bytes. The same binary block, Base64
encoded, is optional `source_state` in layout-7 history detail. It is absent from
the eagerly loaded history index and from numeric CSV/fingerprint inputs.

The row's header is version byte 1, signed SDK replay byte (-1 unknown, 0 ended,
1 started), zero UInt16, the worker GUID in .NET byte order, then five Int64s:
worker generation, connection generation, observation QPC, frame QPC and QPC
frequency. Generations/frequency are positive and QPC times are nonnegative.

The remaining 68 bytes contain availability byte (0 unknown, 1 awaiting reply,
2 available, 3 unavailable, 4 gap), signed freeze mask and signed slew state,
reason byte, UInt32 request ID, UInt32 SDK exception, and seven binary64 values:
raw simulator time, latitude/longitude degrees, altitude feet, pitch/bank/heading
degrees. Freeze bits 0/1/2 mean latitude-longitude/altitude/attitude. Available
values require mask 0–7, slew 0/1, reason 0 and a nonzero request ID. Unavailable
fields use -1, never fabricated zeros. Reason codes 1–7 mean awaiting reply,
request rejection, definition rejection, timeout, invalid data, connection gap,
and initial unknown state. Raw nonfinite physics values remain representable;
classification must treat them as unavailable evidence.

The source uses a separate optional USER definition and distinct ONCE requests.
An SDK replay event does not refresh the age of freeze observations. Any external
pose-control classification is diagnostic; these flags alone do not prove replay.

## Verification

`LandingStats.Archives.RegressionTests` runs on net48 and net10. It tests byte
roundtrip, damaged descriptors/checksums, truncated/trailing/concatenated frames,
oversized windows and expansion, forged directory counts, both ZIP methods and
unfinished writer retirement. Application tests exercise production report/RAW
writers, endpoint/expiry/downgrade handling and immutable upload retries.
Python tests cover the same framing policy, real net48 output, server validation
and dashboard indexing. Full-corpus evidence is recorded separately in
`telemetry-compression-validation.md`.
