# Share a landing

Hover a landing in the history list and select **Share**, immediately to the left
of Delete. The standard Windows save dialog writes one `.lslanding` file. The
file includes all available contacts belonging to that landing episode, including
bounces, and the saved data for charts, 3D playback and controller assignments.
Older recordings retain the data they originally contained.
Sharing preserves each contact's original measurement version, quality indicators
and API `sourceFormatVersion` in its recorded measurement feature. The exchange
container is versioned independently of the local history layout.

Select **Import** above the history list to choose one or several files. Dropping
the files onto the application window or opening them from Windows Explorer uses
the same import operation. Opening a file brings the application forward and
selects the imported landing, even if it was already imported.

Imported contacts have a distinct history shade and an **Imported** label. They
are excluded from the monthly average. Native recordings remain separate even
when a file contains a copy of one of the user's own landings. Saved charts and
3D playback work without a running simulator or connected controllers.

## Identity and persistence

Import validates a complete package before publishing its contacts. Each contact
gets a local identity derived from the package format, revision and original
contact ID. Its original identity and package hash remain in the saved record
and summary index. Local API projections expose `isImported` as well.

Importing the same content again selects the existing contact without creating
a duplicate. The revision is the SHA-256 of the contents framing
specified below, with contacts sorted by original source ID and feature blocks
sorted by type, both using ordinal UTF-16 ordering. Core and feature payloads
retain their exact original bytes. The hash does not serialize the current
application model, so adding a field to that model cannot change an imported
package's identity. ZIP timestamps, compression and incoming block order do not
affect this revision; changes to the original payload bytes do.

Re-exporting an import uses those retained source bytes, including known
features and unsupported optional features. They survive history reload and
backup/restore. Local enrichment can change the displayed view without changing
the imported source or its re-export. A different subset of contacts produces a
new revision, so it cannot join the original revision's replay episode.

Deleting an imported contact retains the ordinary deletion tombstone. An explicit
later file import can restore the contact under a new local identity without
clearing that tombstone. Repeated imports then find the restored contact. Imports
never replace a native recording. Each contact uses the repository's durable
publication and index recovery; a storage failure may leave completed contacts
in place, and retry imports only the missing contacts.

Each selected file has an independent result. A damaged file is reported while
other selected files continue importing. The history panel shows the imported,
duplicate and failed counts, with expandable error details.

## File format

The `.lslanding` format versions its container independently of core, feature
and historical measurement versions. Each file is a ZIP archive with exactly
two members:

| Member | Contents |
| --- | --- |
| `manifest.json` | Container identity, version, contact count and payload checksum |
| `contents.bin` | The framed contacts described below |

The manifest has `format: "MSFS Landing Stats landing"`, integer `version: 1`,
the contact count in `contacts`, and a hexadecimal SHA-256 of
the exact payload member bytes in `sha256`. The reader rejects any other
container version. This integrity checksum precedes the revision normalization
described above.

### Contents framing

`contents.bin` starts with eight ASCII bytes `LSLAND01`, followed by an Int32
contact count. Each contact contains a length-prefixed core payload, an Int32
feature count, and that many feature blocks. Each feature block contains, in
order:

1. A length-prefixed UTF-8 type identifier, such as `org.landingstats.pose`.
2. An Int32 feature version, at least 1.
3. One flags byte: `0` means optional; `1` means required. Other values are invalid.
4. A length-prefixed opaque payload.

Every integer is signed little-endian. Each length prefix is an Int32 byte count
and must be non-negative and fit within the remaining payload. Counts are
validated against the available bytes. Type identifiers are non-empty, at most
512 characters, contain no control characters and must be unique per contact.
UTF-8 decoding is strict. Trailing bytes are invalid. The normative framing and
revision ordering are implemented by `ReadExtensible` and `EncodeContents` in
[`LandingShareArchive.Contents.cs`](../src/LandingStats.App/Storage/LandingShareArchive.Contents.cs).

### Frozen core and independent features

The core payload is one UTF-8 JSON object with a frozen `version: 1` schema.
Its fields are:

| Members | Meaning and units |
| --- | --- |
| `sourceId`, `episodeId` | Original contact and episode identities; non-empty, at most 512 characters, without control characters |
| `timestampUtc` | UTC timestamp in .NET round-trip (`O`) format |
| `simulator`, `aircraftTitle`, `aircraftType`, `airport`, `runway` | Recorded display strings |
| `contactNumber`, `contactCount` | One-based contact number and episode contact count; count is at least the contact number |
| `inertialFpm`, `surfaceFpm`, `surfaceDeltaFpm`, `terrainFpm`, `unresolvedFpm` | Recorded vertical rates or rate components, feet per minute |
| `peakG150ms`, `peakG2s` | Recorded peak normal loads, in G |
| `pitchDeg`, `bankDeg` | Recorded attitude, degrees |
| `airspeedKt` | Recorded airspeed, knots |

The numeric measurement members are optional nullable finite numbers; absent or
`null` means unavailable. Other listed members, including `version`, are required.
The core remains sufficient to identify and display the contact when optional
features are unavailable. New capabilities belong in separately named and
versioned features; they must not redefine this core.

The built-in feature namespace is `org.landingstats.`. The current schemas are
version 1; their JSON members and serializers are defined in
[`LandingShareContent.cs`](../src/LandingStats.App/Storage/LandingShareContent.cs):

| Type suffix | Version 1 payload |
| --- | --- |
| `metrics` | Sanitized `LandingRecord` scalar metadata, including source `FormatVersion`, quality/provenance and optional geometry. Core identity and measurement members are authoritative when constructing the view. |
| `series` | `columns`: `LandingSeriesColumns`; `sourceIndices`: recorded legacy controller Y-column mapping. |
| `engines` | `columns`: an array of `LandingEngineColumns`, with each engine's recorded timeline. |
| `contact-points` | `columns`: an array of `LandingContactColumns`, with each contact point's recorded timeline. |
| `pose` | `time`, `columns` (`LandingPoseColumns`), nullable degree arrays `pitch`/`bank`, and boolean `onGround`. |
| `inputs` | `time`, `columns` (`LandingPhysicalInputColumns`), `sourceIndices` and `axisCount` (`6` or `8`). |
| `controllers` | `time` and `timeline` (`ControllerCatalogTimeline`), including recorded assignments. |
| `source-state` | `time` and `data`, the Base64 replay-source timeline decoded by `ReplaySourceTimelineCodec`. |

All `time` values are finite seconds relative to contact zero. Features sharing
that timeline must agree on every time value. The version 1 column names, units,
missing-value encodings and source mappings are the existing
[v7 contracts](format-v7.md), with their exact `DataMember` declarations in
[`LandingRecordFile.cs`](../src/LandingStats.App/Storage/LandingRecordFile.cs) and
[`LandingReplayColumns.cs`](../src/LandingStats.App/Storage/LandingReplayColumns.cs).
The metric members are defined in
[`LandingRecord.cs`](../src/LandingStats.App/Models/LandingRecord.cs).
Controller and source-state payloads use
[`ControllerCatalogTimeline`](../src/LandingStats.App/Models/ControllerCatalogTimeline.cs)
and [`ReplaySourceTimelineCodec`](../src/LandingStats.Core/ReplaySourceTimelineCodec.cs).
These version 1 contracts must not acquire incompatible changes through edits
to a shared application model: an incompatible feature schema needs a new
feature version or type.

The writer marks its features optional. A reader uses each supported type/version
independently and retains all feature bytes. Unknown optional types or versions
remain opaque; unknown required types or versions reject the package. A malformed
supported feature also rejects the package. Missing optional data does not create
invented charts, controller assignments or analysis results.

### Validation and resource limits

The reader checks ZIP structure, bounded expansion, manifest version, checksum,
framing, supported core/required features, known feature columns and episode
consistency before writing history. No archive entry is extracted to a filesystem
path.

Encoded archive size and total decoded content each have a 256 MiB limit; the
manifest is limited to 4 KiB. The writer reserves those 4 KiB from the decoded
payload budget. A retained contact's uncompressed history detail, including
Base64 original bytes and its decoded view, must also fit the 512 MiB history
detail limit before publication. Existing ZIP expansion and JSON nesting limits
still apply. Extensibility does not remove these resource bounds.

Checksums, canonical revision hashing and ZIP output use streams. Retained bytes
use bounded binary segments in memory and chunked Base64 in history, so an opaque
feature does not require simultaneous whole-file and whole-string copies. This
storage representation does not change the exchange framing or package identity.

Each package also has a 256 MiB allocation allowance for metadata, decoded JSON
objects and materialized chart/control points. The reader reserves conservative
object, string, array and point costs before allocating their decoded projections;
the allowance is shared by every contact and supported feature. A package that
exceeds it is rejected before history publication. Opaque payload bytes use the
independent decoded-byte limit and do not consume a JSON-object allowance.

Incomplete or ambiguous episodes are rejected. The package contains compact
history rather than the original raw diagnostic capture. Importing it does not
rerun landing analysis.

## Windows integration

On each ordinary launch, the portable launcher checks the current user's
`Software\Classes` association for `.lslanding`. The `MSFSLandingStats.Landing`
ProgID points its open command and icon to the actual portable executable. Moving
the executable and launching it refreshes those paths; the extracted runtime
does not replace them. Windows' protected `UserChoice` setting is left intact.

The launcher writes file-open requests to a durable inbox before checking whether
an application instance already exists. The current user's application in the
same Windows session handles queued requests after startup and when notified.
It acknowledges a request after import handling, so a request arriving during
startup or recovery survives the process handoff. Retried requests use the same
duplicate detection as an explicit import.

Export and import are local file operations. Sharing the exported file with
another person is a separate action. Imported records participate in optional
history backup with their imported origin preserved.
