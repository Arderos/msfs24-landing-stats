# Telemetry ingress

The service accepts signed schema-v5/v6 DEBUG RAW captures, explicit
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
key, then proves possession of that key for enrollment and every upload. No
hardware identifier or invitation code is collected.

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
limited to 2,048 entries and 1 MiB expanded. Legacy bundles without a journal
remain supported.

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
field is retained for older clients; `telemetry_schemas: [5, 6]` advertises both
accepted versions to newer clients. Deploy the receiver before a v6 client release.

Capacity is fail-closed: 16 MiB per request, 64 MiB expanded, 512 MiB per
installation per rolling day, and the same 512 MiB fixed-day budget counts all
signed attempts, including invalid archives. Source addresses are capped at
1 GiB/day and the entire ingress at 4 GiB/day. At rest, no more than 20 GiB is
retained and a 2 GiB free-space reserve is preserved on the dedicated
filesystem. Stale partial uploads are removed on startup and container logs
rotate at 3 x 10 MiB.

Enrollment has its own identity-independent bounds: 10 requests/source/hour,
1,000 requests globally/hour, an atomic maximum of 100,000 retained
installations, and a 30-day expiry for active identities that have no retained
capture. Referenced and revoked identities are never pruned. A new identity is
rejected when the registry is full or the 2 GiB free-space reserve is active;
an existing identity can still refresh its signed registration.

Read the live SQLite database through `docker compose exec -T telemetry-api python -`
from the deployment directory, using `file:/data/telemetry.sqlite3?mode=ro` and
`PRAGMA query_only=ON`. This retains the receiver's UID. Even read-only SQLite
queries can create WAL/SHM sidecars; reading as a different host user can leave
files that prevent the service from writing. Do not delete live WAL/SHM files or
use `immutable=1` to bypass this: it can omit transactions still in the WAL.
