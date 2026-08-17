# Telemetry ingress

The service accepts signed schema-v5 DEBUG RAW captures. It is intended to run
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
signature, replay nonce, exact ZIP members, expansion bounds, session metadata,
schema-v5 header, row width, finite numeric values, boolean fields, sequence,
declared sample count, and replay-like kinematic inconsistencies before
atomically moving it from `incoming` to `accepted`. The replay check mirrors
the conservative compound desktop detector: position must move at flight speed
while reported horizontal, air, and vertical velocities remain inert.

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
