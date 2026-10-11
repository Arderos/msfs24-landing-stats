"""Small networkless metadata exporter. Never writes the source SQLite database."""

import hashlib
import json
import logging
import os
import shutil
import sqlite3
import time
from pathlib import Path

LOG = logging.getLogger(__name__)


def export_snapshot(root: Path, output: Path) -> dict:
    # The directory is writable only for SQLite's WAL/SHM sidecars. Docker also
    # mounts the database itself and the accepted archive directory read-only.
    source = (root / "telemetry.sqlite3").resolve()
    connection = sqlite3.connect(source.as_uri() + "?mode=ro", uri=True, timeout=2)
    connection.row_factory = sqlite3.Row
    deadline = time.monotonic() + 10
    connection.set_progress_handler(lambda: int(time.monotonic() > deadline), 1000)
    try:
        connection.execute("PRAGMA query_only=ON")
        connection.execute("BEGIN")
        rows = connection.execute("""
            SELECT capture_id,install_id,sha256,relative_path,received_at,
                   compressed_bytes,uncompressed_bytes,sample_count,schema_version,
                   app_version,capture_kind
            FROM captures ORDER BY received_at DESC
        """).fetchall()
        captures = []
        for row in rows:
            record = dict(row)
            record["install_id"] = hashlib.sha256(
                ("dashboard-installation:" + record["install_id"]).encode("utf-8")
            ).hexdigest()
            captures.append(record)
        registry = dict(connection.execute(
            "SELECT status,COUNT(*) FROM installations GROUP BY status"
        ).fetchall())
        budgets = [dict(row) for row in connection.execute(
            "SELECT window_start,bytes FROM byte_budgets "
            "WHERE scope='capture-global-day' AND subject='global' ORDER BY window_start"
        )]
    finally:
        connection.close()
    snapshot = {
        "format_version": 1,
        "generated_at": int(time.time()),
        "captures": captures,
        "registry": {"active": registry.get("active", 0), "revoked": registry.get("revoked", 0)},
        "storage": {
            "free_bytes": shutil.disk_usage(root).free,
            "quota_bytes": int(os.environ.get("STORAGE_QUOTA_BYTES", "21474836480")),
            "retention_days": int(os.environ.get("RETENTION_DAYS", "30")),
        },
        # This is bytes reserved by signed attempts, NOT a count of rejected requests.
        "signed_attempt_byte_budgets": budgets,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(".tmp")
    try:
        with temporary.open("w", encoding="utf-8") as destination:
            json.dump(snapshot, destination, ensure_ascii=True, allow_nan=False, separators=(",", ":"))
            destination.flush()
            os.fsync(destination.fileno())
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)
    return snapshot


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    root = Path(os.environ.get("DATA_ROOT", "/data"))
    output = Path(os.environ.get("DASHBOARD_SNAPSHOT_PATH", "/snapshot/metadata.json"))
    while True:
        started = time.monotonic()
        try:
            snapshot = export_snapshot(root, output)
            LOG.info("Metadata snapshot refreshed: %d retained captures", len(snapshot["captures"]))
        except Exception:
            # Keep the previous complete snapshot; the UI reports its age as stale.
            LOG.exception("Metadata snapshot refresh failed")
        time.sleep(max(1, 30 - (time.monotonic() - started)))


if __name__ == "__main__":
    main()
