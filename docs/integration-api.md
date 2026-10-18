# Local integration API v1

Landing Stats exposes a read-only local API for flight trackers, dashboards,
analysis tools and other Windows applications. Clients can query saved landings,
contact metrics and recorded series, inspect recorder and component status, read
the latest available simulator observation, and subscribe to durable landing
results. No Landing Stats assembly, SimConnect subscription or ZIP-file watcher
is required in the client.

The API runs in a separate supervised worker process. A disconnected or slow
client does not own the recording, analysis or archive lifecycle. Landing Stats
and client applications can start and stop independently.

## Enable or disable

Open **Settings → Local API → Allow local applications to read landing data**.
The Russian label is **Настройки → Локальный API → Разрешить локальным приложениям
читать данные посадок**. The setting is off by default, including upgrades from
versions without an API setting. Changes take effect without restarting the app.
Disabling stops the endpoint and disconnects its clients.

This is a preference for this computer. It is not synchronized through Google
Drive. Clients must treat an absent endpoint as normal: the app may be closed,
the setting may be off, or its worker may be restarting. Retry with backoff.

## Connect and negotiate

Use a duplex Windows Named Pipe in byte mode:

```text
\\.\pipe\LandingStats.Api.v1.<current Windows user SID>
```

For example, a SID has the form `S-1-5-21-…-1001`. Obtain the current process
token's `TokenUser` SID; in .NET use `WindowsIdentity.GetCurrent().User.Value`.
The [Python example](examples/integration_client.py) discovers it with the Windows
API. The name is stable across app and API-worker restarts for the same account.

Every frame is a four-byte **unsigned little-endian byte count**, followed by that
many bytes of strict UTF-8 JSON. There is no newline delimiter, byte-order mark or
NUL terminator. Reads and writes can be partial: loop until the entire prefix
and payload have transferred. Count encoded bytes, not Unicode characters.

Send `hello` first on every connection:

```json
{"id":"hello-1","protocolVersion":1,"method":"hello","params":{}}
```

Normal request and response envelopes:

```json
{"id":"status-1","protocolVersion":1,"method":"status.get","params":{}}
```

```json
{"id":"status-1","result":{"observedUtc":"2026-10-18T12:00:00.0000000Z","revision":42,"recorder":{},"components":[],"recentEpisodes":[]}}
```

```json
{"id":"status-1","error":{"code":"TemporarilyUnavailable","message":"The requested data is temporarily unavailable. Retry later."}}
```

`id` is a nonempty string of at most 128 characters; `method` is a nonempty string
of at most 64 characters; `params`, when present, is an object. Use one
outstanding request per query connection. Responses repeat the request ID.
Malformed requests can end the connection. Each request carries
`protocolVersion: 1`, not only the handshake.

`hello.result` contains `application`, `protocolVersion`, `schemaVersion`,
`readOnly`, `capabilities`, `methods`, `limits` and `events`. Inspect the method
and capability lists before using optional functionality. Public protocol and
schema versions are independent of internal worker IPC and recording-file
versions. Additive fields may appear within v1; clients should ignore unknown
fields and recognize missing optional data. A breaking protocol change uses a
new major version and endpoint.

## Methods

All timestamps accepted by filters are ISO 8601 UTC strings ending in `Z`.
All methods are read-only. Unknown methods return `MethodNotFound`.

| Method | Parameters | Result |
| --- | --- | --- |
| `hello` | None | Version, capabilities, methods, limits, event-stream identity and latest sequence. |
| `schema.get` | None | Units, timestamp and availability conventions, supported event types and revision meanings. |
| `status.get` | None | Recorder state, active episode, airborne state, simulator-exit observation, component health/generation and recent episode summaries. |
| `telemetry.get` | None | Latest observations from the recorder's existing subscriptions, with freshness and availability. |
| `landings.list` | Optional `cursor`; `limit` (default 50, 1–200), optional `fromUtc`, `toUtc`, `aircraft`, `airport`; legacy-style `offset` also supported | Landing/contact summaries and keyset pagination. Aircraft is a case-insensitive title substring; airport is a case-insensitive exact match. UTC bounds are inclusive. |
| `landings.get` | `id` | One detailed landing/contact, metrics, reconstruction, runway geometry and series availability. |
| `episodes.get` | `id` | A coherent episode snapshot with all contacts and result readiness. |
| `landings.series` | `id`, `offset` (default 0), `limit` (default 50, 1–100), optional `fromSeconds`, `toSeconds` | Main recorded sample series; time bounds are inclusive. |
| `landings.engines` | `id`, `engineNumber` (default 1), `offset` (default 0), `limit` (default 100, 1–200) | Samples for the selected engine. |
| `landings.contactPoints` | `id`, `contactPointIndex` (default 0), `offset` (default 0), `limit` (default 100, 1–200) | Samples for the selected recorded contact point. |
| `landings.provenance` | `id`; optional `section`, `offset`, `limit` (see below) | Controller/source mapping, availability/correlation, controller timeline, analysis geometry and available engine/contact-point indices. |
| `events.read` | Optional `streamId`, `afterSequence`; `limit` (default 16, 1–32) | Replay a page of durable events. |
| `events.subscribe` | Optional `streamId`, `afterSequence` | Replay, then continue streaming on this connection. Close it to unsubscribe. |

`id` is a landing/contact ID for `landings.*` and an episode ID for
`episodes.get`. Do not derive IDs from file paths or aircraft names. Modern
episode IDs are stable opaque strings, not necessarily GUIDs. Older recordings without an episode ID use
`legacy:<landingId>`; no unsupported cross-contact grouping is inferred.

History is ordered by descending contact UTC and ID. For a multi-page read,
start without `cursor` and pass each returned opaque `nextCursor` until
`hasMore` is false. Keep filters unchanged. Do not combine `cursor` with a
nonzero `offset`. Each page includes `streamId`; a change requires restarting a
mirror rebuild. Keyset pagination avoids shifting page offsets as new landings
arrive. `total` counts matching rows remaining after that page's cursor, not a
fixed total for an entire walk. Rows are not held in a database snapshot between
requests; use event replay to catch concurrent result updates. The list also
includes committed result contacts whose history compression is still pending,
so these can be queried immediately.

### Landing and episode results

Landing projections include:

| Field | Meaning |
| --- | --- |
| `id`, `episodeId`, `timestampUtc`, `sourceFormatVersion` | Stable identities, contact time and source recording format. |
| `aircraft` | Aircraft `title`, `type` and `simulator`. |
| `site` | Airport/runway identifiers, contact latitude/longitude in degrees and airport distance in nautical miles. |
| `contactNumber`, `contactCount` | Contact position and count within the recorded landing. |
| `metrics` | Inertial vertical rate, surface closure/delta, terrain and unresolved rates, peak/impact G, impact window, pitch, bank, angle of attack, indicated/ground speed, weight, CG and wind. |
| `quality` | Surface-latch availability, fit/extrapolation evidence, latch-update evidence, contact-time estimation and impact-analysis status/version. |
| `detailAvailable` | Whether this response includes the detailed projection. A list summary can be expanded with `landings.get`. |

A metric has an explicit value, unit and availability flag:

```json
{"value":-138.0,"unit":"ft/min","available":true}
```

```json
{"value":null,"unit":"ft/min","available":false}
```

Vertical rates retain their sign; a descent is negative. Units are `ft/min`,
`ft`, `ft/s^2`, `g`, `kt`, `deg`, `deg/s`, `s`, `lb`, `%` or `rpm`, as applicable.
Do not substitute surface closure rate for inertial vertical rate: they describe
different quantities. Read the metric's `unit` and `schema.get.seriesUnits`
instead of depending on display preferences or converting every number to the
same unit system.

Detailed projections add `series` counts/ranges and available source indices,
`runwayGeometry`, `approachGateSeconds`, `flareStartSeconds`,
`estimatedContactTimeSeconds` and `reconstruction`. Reconstruction is explicitly
experimental and has its own availability, model, component rates, uncertainty,
fit count and geometry evidence. Its absence must not invalidate a normal
landing result.

An episode contains `episodeId`, `revision`, `timestampUtc`, `metricsReady`,
`metadataReady` and a `contacts` array. Published results also include `captureId`
and `captureEpisodeCount`: a capture may contain multiple independent physical
episodes, which are emitted separately. Contacts belonging to the same physical
episode, including its bounces, are delivered together. Event revision 1 means durable analysis
is ready; revision 2 means final metadata/report is ready. Readiness does not
guarantee an airport/runway was identified. Historical projections may have
`revision: null` and `metadataReady: null` when those facts are not recorded.

The API uses the existing recording readers: old supported recordings remain
queryable. Missing optional and non-finite numeric values are `null`, never JSON
`NaN` or infinity. Some older formats have established reader defaults for
fields they did not record; `sourceFormatVersion` and validity/provenance fields
remain relevant. Do not treat every zero as missing or every legacy default as
a measured observation.

### Series and provenance

Series pages contain `id`, `points`, `offset`, `nextOffset`, `total`, `hasMore`,
`timeOrigin: "contact"` and `units`. Use returned offsets until `hasMore` is
false. Samples use seconds relative to that contact; negative values are before
contact. Do not compare those offsets between contacts as absolute timestamps.

Main series cover vertical motion, G, AGL, speed, attitude, sideslip, pilot and
aircraft control positions, spoilers/flaps/brakes, accelerations and body rates,
wind, on-ground state, position/altitude/heading when recorded, and physical
controller inputs with validity and age. See `schema.get.seriesUnits` for the
field names available in the running version. Invalid physical-axis values are
`null`; use the associated validity/age arrays and source mapping.

Engine series use `timeSeconds`, `throttlePercent`, `n1Percent`, `rpm` and
`reversePercent`. Contact-point series expose their recorded compression and
position. Request available indices from a detailed landing or
`landings.provenance`; do not assume every aircraft has the same engines,
landing gear or controller layout. An unrecorded selection returns an empty
series.

Provenance may contain controller descriptions, source mappings and analysis
geometry. These explain recorded evidence; they do not authorize or provide
control of simulator devices. Detailed provenance objects are optional and may
gain fields. They retain the stored model's field names; main sample point
fields use lower camel case.

`controllerTimeline` uses its stored `version`, `samples`, `catalogs` and
`transitions` envelope. Each transition is `[sampleRow, catalogIndex]`; an index
of `-1` means unknown legacy provenance. Catalogs are base64-encoded records,
with an empty catalog meaning unavailable. The catalog and source-evidence
encodings are described in [the recording format documentation](format-v7.md).
Consumers that only need labels and valid physical input values can use
`controlInputSources`, source-index arrays and sample validity arrays directly.

### Latest telemetry and status

`telemetry.get` reuses existing recorder observations; it does not start a second
SimConnect subscription or promise a continuous full-flight track. The latest
full sample is retained at most four times per second. When the recorder is
waiting for an approach and full capture is inactive, the current implementation
may know the aircraft but return `not_observed` and no full frame. Its local
ground guard is not forwarded into the public telemetry feed.

The result reports `availability` (`disconnected`, `not_observed`, `stale`,
`guard` or `full`), `connected`, `ageSeconds`, `stale`, `observedUtc`,
`pauseFlags`, `aircraft`, on-ground/motion state, simulation time, AGL and
vertical velocity. An observation more than two seconds old is stale. A
separately timestamped `fullFrame` may contain position, speed, attitude, wind,
weight, CG and aircraft controls. Always check **that frame's** age and `stale`
flag: a fresh observation does not make an old full frame fresh. `guard` is
supported when such source observations are available; it is not a promise of a
continuous ground feed in the current source implementation.

`status.get` is an observation of runtime state, not a durable event. Component
states and generation counts help detect recovery without interpreting internal
logs. Poll status/telemetry at a modest rate; durable result subscriptions are
the preferred way to detect a completed landing.

## Subscriptions, replay and crash recovery

A landing's final metrics become available after the normal capture and
analysis window (normally 15 seconds after the last contact), not at the instant
of first touchdown. `LandingResultAvailable` is published from the committed
analysis before history compression or diagnostic ZIP creation. It contains a
complete contact group. `LandingResultUpdated` replaces the same episode with a
higher revision when final metadata is committed. Do not block on archive
creation to consume either event.

```json
{"id":"landings-1","protocolVersion":1,"method":"events.subscribe","params":{"streamId":"<saved-stream>","afterSequence":42}}
```

The first response acknowledges the subscription:

```json
{"id":"landings-1","result":{"subscribed":true,"streamId":"<saved-stream>","afterSequence":42}}
```

Subsequent frames use these shapes:

```json
{"type":"event","subscriptionId":"landings-1","streamId":"<saved-stream>","event":{"sequence":43,"type":"LandingResultAvailable","episodeId":"<episode>","revision":1,"timestampUtc":"2026-10-18T12:00:00Z","publishedUtc":"2026-10-18T12:00:16Z","payload":{"episodeId":"<episode>","revision":1,"metricsReady":true,"metadataReady":false,"contacts":[]}}}
```

```json
{"type":"heartbeat","subscriptionId":"landings-1","streamId":"<saved-stream>","nextSequence":43}
```

The connection becomes dedicated to that subscription. Open another connection
for queries. There is no unsubscribe command; close the subscription pipe.
Heartbeats arrive approximately every 15 seconds, and new events are checked
approximately every 250 ms. A heartbeat is not a client processing ACK.

Delivery is **at least once**. The durable consumer identity is the pair
`(streamId, sequence)`. Within the stream, sequence numbers increase; gaps may
occur after retention. The payload also has an episode identity and revision.
Upsert episode/contact results by stable ID, never append a new landing merely
because a repeated event arrived.

Commit each consumed event, its derived result rows, and the cursor **in one
client database transaction**. If the client crashes before commit, the event
replays. If it crashes after commit, its saved cursor resumes after that event.
No ACK request is needed. A client may reconnect, send `hello`, and subscribe
from its last committed cursor after either application or the API worker
restarts.

`events.read` returns `streamId`, `events`, `nextSequence`, `oldestSequence`,
`latestSequence` and `hasMore`. Omit the cursor to read from the oldest retained
event, or supply the saved pair to continue. `afterSequence` is exclusive. Use
the returned `nextSequence` for the next page.

### Resync and deletion

The journal is bounded to 4,096 events and 32 MiB. A stale/invalid cursor, changed
stream, or retained-history deletion can produce `ResyncRequired`. Deletions
reset the stream instead of replaying a stale result that would recreate the
deleted landing. A consumer maintaining a mirror must replace its local
history view during resync; retaining old rows defeats deletion propagation.

The example uses this recovery sequence:

1. Capture the current `hello.events` stream and latest sequence **before**
   reading history.
2. Build a staging table from `landings.list` using `nextCursor`. Require an
   unchanged `streamId` on every page.
3. Validate the captured cursor with `events.read`. Obtain `oldestSequence`,
   then replay the retained events starting at `oldestSequence - 1` into staging.
   This includes a result whose analysis event was already published before the
   rebuild began but whose compressed history file is not ready yet.
4. Validate the resulting cursor, then atomically replace the mirror and its
   saved cursor. Subscribe after the last committed sequence.

If the stream changes or any cursor expires during the rebuild, discard staging
and repeat. New events after the last staged event are replayed on subscription.
Do not obtain a new cursor after rebuilding and skip intervening events. A
consumer interested only in future results can instead start at the negotiated
latest sequence without maintaining a history mirror.

## Provenance pagination

An omitted `section` preserves the complete provenance response for small records.
Long recordings can exceed the response bound; use `section: "metadata"` to obtain
controller/source mapping, geometry, availability and `sections` totals without
embedding the large timelines. `schema.get.provenancePaging` advertises these
sections, and `hello.limits` advertises their page bounds.

| Section | Offset/limit units | Default / maximum limit | Response content |
| --- | --- | --- | --- |
| `sourceEvidenceTimeline` | Base64 text characters | 65536 / 65536 | `data`, `available`, `encoding: "base64"` |
| `controllerCatalogs` | Catalog entries | 8 / 16 | `items`, timeline `version` and `samples` |
| `controllerTransitions` | Transition entries | 100 / 200 | `items`, timeline `version` and `samples` |

Each page includes `offset`, `nextOffset`, `total` and `hasMore`; offset defaults to
zero. Continue from `nextOffset` until `hasMore` is false. Concatenate all source
text chunks **before** base64 decoding: a requested page length need not align
with a base64 quantum. Concatenate catalog/transition arrays in order, preserving
their indices; they reconstruct the original controller timeline with `version`
and `samples`. A missing source timeline has `available: false`, total zero and
empty data. Metadata section totals are characters or entries in these same units.
These sections retain opaque encoded provenance without reinterpreting it.

For example:

```json
{"id":"provenance-1","protocolVersion":1,"method":"landings.provenance","params":{"id":"<landing-id>","section":"sourceEvidenceTimeline","offset":0,"limit":65536}}
```

As with series queries, concurrent replacement of the underlying landing can
change subsequent pages. Clients needing a stable export should retry if section
totals or timeline headers change, and validate the reconstructed timeline.

## Limits and error handling

| Limit/deadline | v1 |
| --- | --- |
| Concurrent pipe clients | 8 |
| Query dispatch rate per connection | At most 10 requests per second |
| Request payload | 64 KiB |
| Response/event payload | 512 KiB |
| First request after connecting | 10 seconds |
| Idle time between normal queries | 60 seconds |
| Runtime query deadline | 15 seconds |
| Server write deadline for slow clients | 5 seconds |
| JSON nesting depth | 32 |

A subscription has its heartbeat loop rather than the normal 60-second request
idle timeout. Each client has bounded work; falling behind may disconnect it.
Reconnect from the committed cursor. The app does not wait for external clients
to acknowledge events when shutting down or disabling the API.

The runtime currently leaves envelope headroom with a 480 KiB response ceiling,
450 KiB event-page budget and 240 KiB complete episode payload budget. Event
pages may therefore contain fewer items than the requested count. Consult the
negotiated limits and `hasMore`; do not infer end of data from a short page.

Relevant error codes include `InvalidRequest`, `InvalidParams`,
`UnsupportedProtocol`, `HandshakeRequired`, `MethodNotFound`, `NotFound`,
`ResponseTooLarge`, `Timeout`, `TemporarilyUnavailable` and `ResyncRequired`.
Match codes, not English messages. Invalid input or version mismatch requires a
client correction. `NotFound` can mean a user deleted the result. Reduce page
size on `ResponseTooLarge`. Retry transient errors with bounded exponential
backoff. Treat broken pipes and endpoint absence as normal reconnect cases.

## Security and scope

The endpoint accepts the current Windows user, denies network logons, and does
not listen on TCP or expose an internet service. It offers no simulator control,
settings mutations, deletion, uploads or arbitrary filesystem access. Internal
worker tokens, OAuth credentials, storage paths and raw internal failure
messages are not public data.

The Windows account is the access boundary: enabling the API lets **any process
running under that account** request the exposed landing data. There is no
per-application API key or allowlist. Position, aircraft, timestamps and recorded
controller provenance are intentionally queryable. Disable the setting when
that local access is unwanted.

## Standalone Python example

[integration_client.py](examples/integration_client.py) requires Windows and
Python 3.9+ with its standard library. It discovers the SID, negotiates `hello`,
uses byte-correct framing and overlapped I/O deadlines, and can query any method.
Its `watch` mode maintains client-owned SQLite tables for landings, episodes,
recent raw events and a committed cursor. It rebuilds on `ResyncRequired`,
deduplicates repeated events and ignores stale episode revisions. Database
updates and cursor advancement are in the same transaction.

From the repository root in PowerShell 7:

```powershell
py -3 -X utf8 docs/examples/integration_client.py hello
py -3 -X utf8 docs/examples/integration_client.py query status.get
py -3 -X utf8 docs/examples/integration_client.py query schema.get
py -3 -X utf8 docs/examples/integration_client.py query landings.list --params '{"limit":10}'
py -3 -X utf8 docs/examples/integration_client.py query landings.get --params '{"id":"<landing-id>"}'
py -3 -X utf8 docs/examples/integration_client.py query landings.series --params '{"id":"<landing-id>","fromSeconds":-6,"toSeconds":8,"limit":50}'
py -3 -X utf8 docs/examples/integration_client.py watch --db .\landing-stats-client.sqlite3
```

Stop `watch` with Ctrl+C and run it again with the same database to resume.
`--max-events 2` stops after two new subscription events, which is useful for an
integration test. The initial history rebuild is logged to stderr; received
events are printed to stdout only after their transaction commits. The database
parent directory must already exist. Use a dedicated client database, not a
Landing Stats storage file.

Use `--pipe <name>` before the subcommand only when connecting to an explicitly
configured isolated test endpoint. No installation of a Landing Stats client
library or Python package is required.
