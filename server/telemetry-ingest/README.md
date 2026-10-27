# Telemetry ingress

The service accepts signed schema-v5/v6/v7/v8 DEBUG RAW captures, explicit
last-landing bug-report bundles, and automatic landing telemetry. It is intended to run
from `/opt/msfs-landing-telemetry` with capture data bind-mounted from
`/srv/msfs-landing-telemetry/data`.

The API is not published on a host port. A remotely managed Cloudflare Tunnel
must route the public hostname to `http://telemetry-api:8080` on the Compose
`ingress` network.

Secrets live only on the server:

- `secrets/cloudflared-token` — the rotated tunnel token;
- `secrets/server-pepper` — at least 32 random bytes.

Start the stack with `docker compose up -d --build`. Registration is automatic:
each installation creates an anonymous random ID and a Windows-protected RSA
key, then proves possession of that key for enrollment and every upload. Enrollment uses the random installation ID instead of a hardware identifier;
no invitation code is required.

An accepted archive is never extracted to disk. The service validates its
signature, replay nonce, capture-kind-specific ZIP members, expansion bounds, session metadata,
schema-specific header, row width, finite numeric values, boolean fields, sequence,
declared sample count, and replay-like kinematic inconsistencies before
atomically moving it from `incoming` to `accepted`. The replay check mirrors
the conservative compound desktop detector: position must move at flight speed
while reported horizontal, air, and vertical velocities remain inert.
Bug-report bundles additionally contain a bounded, validated JSON result for
the calculated landing, and are marked as `bug_report` in the capture index.
Automatic landing bundles use the same result format and are marked as
`automatic_landing`; they may contain no calculated landings so rejected
contacts remain diagnosable. Both kinds may include `decisions.json`: a
versioned decision journal tied to the same episode ID. Its envelope and entry
fields are validated, numerical details must be finite and flat, and it is
limited to 8,192 entries and 4 MiB expanded. Legacy bundles without a journal
remain supported.
Result JSON is scanned as a stream; only its format, episode and record count
are kept in memory. It retains the 64 MiB expanded ceiling, permits up to 65,536
records, and bounds nesting at 64 levels, values at 32 million and containers at
8 million. The generous container budget includes the per-sample controller
arrays in full-size reports; it does not allocate those arrays on the receiver.
Auxiliary journal/controller JSON is checked before object allocation with a
600,000-value / 100,000-container budget, in addition to its 4/8 MiB byte limits.
One archive validation runs at a time in the single-worker ingress process;
waiting uploads retain their already received files on disk. Rejections still
preserve authenticated, hash-verified original bytes in quarantine.
Deploy these limits before distributing a desktop client with the expanded
journal; older servers can reject journals above their previous limits.

Journal entries contain `time_seconds`, `component`, `code`, `reason`, and a flat
numeric `details` object. Analysis/recorder timestamps use the simulator clock.
Per-contact runway and replay decisions explicitly set
`details.relative_contact_time=1` and `details.contact_number`; their timestamps
are relative to that contact. Repeated visual-suspension decisions are aggregated
over the recorded frames instead of emitting one entry per frame. The top-level
`truncated` flag indicates the journal's count/size bound was reached. Decision
logging does not change landing measurements or overwrite historical records.
Replay-like automatic episodes are retained only when they claim zero
calculated landings, so the rejection decision can be inspected; replay-like
archives that claim a landing, and legacy/manual captures, remain rejected.

`/v1/config` advertises `capture_kinds` and `decision_journal_formats` in
addition to telemetry schemas. Deploy this receiver before enabling the new
client upload format; clients retain unsupported bundles locally until the
receiver advertises the required capabilities.

Schema v6 appends five physical controller axes (X/Z/R/U/V) to the v5 prefix;
the existing Y columns are unchanged. The legacy `telemetry_schema: 5` config
field is retained for older clients; `telemetry_schemas: [5, 6, 7, 8]` advertises all
accepted versions to newer clients.

Schema v7 appends `analysis_time_s`, `simulator_state`, `pause_flags`, and
`flight_segment`. The raw simulator and host clocks are unchanged. State values
are 0 (legacy/unknown), 1 (running), 2 (confirmed pause/frozen resume), and 3
(inactive lifecycle). Pause flags retain the SDK `Pause_EX1` bitmask, or -1 when
unknown. Analysis time excludes confirmed pauses; flight segments prevent joining
loads, teleports, replay, or separate flights. An unexplained `motion_simulation=0`
remains a physical discontinuity. Recorder lifecycle decisions describe the clock
adjustments, suspension, resumption, and partial-capture completion. Deploy this
receiver before a v7 client; queued schema-v5/v6 uploads remain supported.

Schema v8 appends `controller_data_v2`: a base64-encoded, versioned binary
field carrying every captured device and its six legacy or eight DirectInput
axes. The v7 numeric prefix remains readable by existing tools; an empty field
uses that legacy controller layout. Device count follows the encoded dimensions
and the existing 1 MiB payload budget. LCC2 controller metadata retains DirectInput
instance/product GUIDs and the MSFS profile bindings observed during recording,
including assignments to disconnected devices. The dashboard reads these full
axis observations without interpreting the binary field as a numeric column.
Deploy the v8 receiver before distributing a v8 client; schemas v5/v6/v7 remain
accepted.

The optional LCC2 `PEF1` footer carries UTF-8 JSON diagnostic evidence from the
profile reader. It retains the available/read profile inventory, device GUIDs,
axis assignments, saved General/category/aircraft selections, source hashes and
read/resolve errors. A `storeEncoding: "deflate-base64"` snapshot is raw-DEFLATE
compressed in `storeDeflateBase64`; verify its decoded length against `storeBytes`
and SHA-256 against `storeSha256`. The resolution remains ordinary JSON. Original
profile XML and the saved selection file are retained inside the snapshot, with
individual compression and SHA-256 fields, for independent re-analysis. These
fields include local paths and device/profile names and follow the existing
telemetry upload consent. Catalog timelines deduplicate the snapshot across rows.
Old LCC1/LCC2 catalogs without this footer remain valid.

Capacity is fail-closed: 16 MiB per request, 128 MiB expanded in the Compose
deployment, 512 MiB per
installation per rolling day, and the same 512 MiB fixed-day budget counts all
signed attempts, including invalid archives. Source addresses are capped at
1 GiB/day and the entire ingress at 4 GiB/day. At rest, no more than 20 GiB is
retained and a 2 GiB free-space reserve is preserved on the dedicated
filesystem. Stale partial uploads are removed on startup and container logs
rotate at 3 x 10 MiB.

The expanded archive limit accommodates full 30,000-sample v7 diagnostic
captures with controller and source-state metadata. CSV validation streams
rows; sample-count, per-member, JSON-structure and compressed-upload bounds
remain independent. The result JSON member still has its separate 64 MiB
limit. Standalone runs use a conservative 64 MiB archive limit unless
`MAX_UNCOMPRESSED_BYTES` is set as in Compose.

New enrollment has its own identity-independent bounds: 10 requests/source/hour,
1,000 requests globally/hour, an atomic maximum of 100,000 retained
installations, and a 30-day expiry for active identities that have no retained
capture. Referenced and revoked identities are never pruned. A new identity is
rejected when the registry is full or the 2 GiB free-space reserve is active;
an existing identity can still refresh its signed registration.

Idempotent refresh of an active identity with the same key has a separate
6 requests/source/minute pre-verification bound and must still pass timestamp
and signature verification. It does not spend new-identity quotas. This supports
released clients which refresh registration before each automatic report.

Uploads retain the 6 requests/source/minute and 3 archives/installation/minute
bounds. A signed, size/hash-checked upload exceeding the latter may wait for one
minute-window boundary instead of returning 429. The complete body is received
to the quota-bounded temporary file first, avoiding proxy write timeouts. At most
one request per identity and eight globally may wait; waits are asynchronous,
bounded to about 60 seconds, and released on cancellation. Full queues or a lost
admission race still return 429. All 429 responses include `Retry-After` (older
clients may ignore it). Daily byte budgets and archive validation are unchanged;
byte-budget checks/reservations are serialized atomically in SQLite.

API requests produce structured `telemetry_ingress` JSON lines in the existing
rotated container logs: status, bounded reason, duration/pacing time, validated
installation ID, archive SHA, client version, Cloudflare ray ID, and a keyed
source-address pseudonym. No raw IP, body, public key, signature or nonce is
logged. These logs identify future 422 rejections; they cannot recover reasons
for rejections that occurred before this change. Temporary archive I/O failures
return 503 instead of telling old clients to permanently quarantine the archive.

Authenticated archives whose signed size and SHA match but whose contents fail
validation are retained separately under `/data/rejected/<sha256>.zip` with a
small `.json` reason sidecar. They still return 422 and are never counted as
accepted captures or landings. Quarantine cleanup on startup/new rejections keeps
at most 256 MiB / 2,000 compressed archives and expires entries older than seven
days, evicting the oldest quarantine entries first; accepted
archives are not affected. Existing invalid ZIP/expansion rules remain enforced,
and quarantined archives must never be extracted or executed. If quarantine
storage fails, the structured request log says `retention_failed`; the client's
local rejected archive remains the recovery source.
The API has a 90-second graceful stop allowance so routine redeployments can
finish a paced upload; the tunnel and dashboard are not restarted with the API.

Read the live SQLite database through `docker compose exec -T telemetry-api python -`
from the deployment directory, using `file:/data/telemetry.sqlite3?mode=ro` and
`PRAGMA query_only=ON`. This retains the receiver's UID. Even read-only SQLite
queries can create WAL/SHM sidecars; reading as a different host user can leave
files that prevent the service from writing. Do not delete live WAL/SHM files or
use `immutable=1` to bypass this: it can omit transactions still in the WAL.

## Private telemetry dashboard

The optional `docker-compose.dashboard.yml` adds a LAN-only web dashboard at
`http://astr-scad-01:8091` (`http://172.16.20.32:8091`). It has no connection to
the public ingress network or Cloudflare Tunnel. No application release or code
signing is involved. The existing API and tunnel must not be restarted to deploy it:

```sh
docker compose -f docker-compose.yml -f docker-compose.dashboard.yml build telemetry-dashboard
docker compose -f docker-compose.yml -f docker-compose.dashboard.yml up -d --no-deps telemetry-dashboard-snapshot telemetry-dashboard
```

There are two isolated companion containers:

- `telemetry-dashboard`: read-only archive and metadata mounts; its own writable
  hash-keyed index cache; a background indexer; 0.5 CPU and 384 MiB limit. The HTTP
  listener binds only to `172.16.20.32:8091` and accepts actual socket peers in
  `172.16.0.0/16` plus loopback. Host validation and a self-only CSP are enabled;
  forwarded headers are not trusted. It provides no mutation or archive download
  endpoints and receives no ingestion secrets.
- `telemetry-dashboard-snapshot`: no network, no HTTP server, 0.1 CPU and 128 MiB
  limit. Every 30 seconds it reads whitelisted metadata with SQLite `mode=ro` and
  `query_only`, then atomically replaces a separate JSON snapshot. The database
  file and accepted archive directory are bind-mounted read-only. Only their parent
  directory is writable so SQLite can manage WAL/SHM sidecars as receiver UID 10001.
  It does not change SQLite locking modes, checkpoint the database, or copy live
  database files. Registration keys and source-address hashes are never exported;
  installation IDs are pseudonymized before export.

This split is intentional: read-only access to a WAL database may need to create
sidecars while the receiver is idle. Giving that permission to the web process is
unnecessary. A failed snapshot refresh keeps the previous complete snapshot and
the dashboard reports stale data after 120 seconds. Archive indexing also preserves
the previous complete view until a refresh finishes. Bad or expired archives are
isolated and listed as indexing errors, not silently dropped.

The UI has date filters, airport/aircraft/version breakdowns, retained-upload and
deduplicated landing counts, simulator/delivery timing diagnostics, sample rates,
compression/storage statistics, landing metric distributions, physical-axis and
3D reconstruction decision summaries, and searchable per-capture journals.
Daily buckets use UTC; individual timestamps display in the browser's local time.
A landing ID uploaded in multiple reports is
counted once in landing statistics. Archives, samples and decision entries are
counted per uploaded archive. The receiver's retention is currently 30 days/20 GiB:
these are retained-data statistics, **not lifetime usage or unique-person counts**.
Historical ingress rejection counts are not stored in the dashboard and are explicitly unavailable;
indexing failures and reserved signed-attempt bytes are not labelled as rejections.

Override `DASHBOARD_BIND_ADDRESS`, `DASHBOARD_ALLOWED_HOSTS`, and
`DASHBOARD_ALLOWED_NETWORKS` together when moving hosts. Never bind the dashboard
to a public interface or connect it to the `ingress` network. Source retention/quota
values in the snapshot service must follow the receiver configuration if changed.

Health and rollback (leave the receiver and its data running):

```sh
docker compose -f docker-compose.yml -f docker-compose.dashboard.yml ps
docker compose -f docker-compose.yml -f docker-compose.dashboard.yml logs --tail 50 telemetry-dashboard telemetry-dashboard-snapshot
docker compose -f docker-compose.yml -f docker-compose.dashboard.yml stop telemetry-dashboard telemetry-dashboard-snapshot
```

Do not use `compose down` for dashboard rollback. `dashboard-cache` and
`dashboard-snapshot` are separate disposable volumes; neither contains the original
archives or receiver database. Tests run alongside the existing service tests:
`python -m unittest discover -s tests -p 'test_*.py' -v`.
