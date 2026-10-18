import os
import json
import re
import sqlite3
import stat
import threading
import time
from contextlib import contextmanager
from pathlib import Path


class InstallationRegistryFullError(RuntimeError):
    pass


class Store:
    def __init__(self, root: Path):
        self.root = root
        self.database_path = root / "telemetry.sqlite3"
        self._schema_lock = threading.Lock()
        self._rejection_lock = threading.Lock()
        self._maintenance_lock = threading.Lock()
        self._next_orphan_sweep = 0.0

    def initialize(self) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        incoming = self.root / "incoming"
        incoming.mkdir(exist_ok=True)
        (self.root / "accepted").mkdir(exist_ok=True)
        for stale in incoming.glob("*.part"):
            if stale.is_file():
                stale.unlink()
        with self._schema_lock, self.connect() as connection:
            connection.executescript(
                """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS installations (
                    install_id TEXT PRIMARY KEY,
                    modulus TEXT NOT NULL,
                    exponent TEXT NOT NULL,
                    status TEXT NOT NULL CHECK(status IN ('active', 'revoked')),
                    created_at INTEGER NOT NULL,
                    last_seen INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS nonces (
                    install_id TEXT NOT NULL,
                    nonce TEXT NOT NULL,
                    seen_at INTEGER NOT NULL,
                    PRIMARY KEY (install_id, nonce)
                );
                CREATE TABLE IF NOT EXISTS captures (
                    capture_id TEXT PRIMARY KEY,
                    install_id TEXT NOT NULL,
                    sha256 TEXT NOT NULL UNIQUE,
                    relative_path TEXT NOT NULL UNIQUE,
                    received_at INTEGER NOT NULL,
                    compressed_bytes INTEGER NOT NULL,
                    uncompressed_bytes INTEGER NOT NULL,
                    sample_count INTEGER NOT NULL,
                    schema_version INTEGER NOT NULL,
                    app_version TEXT NOT NULL,
                    capture_kind TEXT NOT NULL DEFAULT 'raw_debug',
                    source_address_hash TEXT NOT NULL,
                    FOREIGN KEY (install_id) REFERENCES installations(install_id)
                );
                CREATE TABLE IF NOT EXISTS rate_limits (
                    scope TEXT NOT NULL,
                    subject TEXT NOT NULL,
                    window_start INTEGER NOT NULL,
                    count INTEGER NOT NULL,
                    PRIMARY KEY (scope, subject, window_start)
                );
                CREATE TABLE IF NOT EXISTS byte_budgets (
                    scope TEXT NOT NULL,
                    subject TEXT NOT NULL,
                    window_start INTEGER NOT NULL,
                    bytes INTEGER NOT NULL,
                    PRIMARY KEY (scope, subject, window_start)
                );
                CREATE TABLE IF NOT EXISTS capture_deletions (
                    relative_path TEXT PRIMARY KEY,
                    scheduled_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_nonces_seen_at ON nonces(seen_at);
                CREATE INDEX IF NOT EXISTS idx_rate_limits_window_start ON rate_limits(window_start);
                CREATE INDEX IF NOT EXISTS idx_byte_budgets_window_start ON byte_budgets(window_start);
                CREATE INDEX IF NOT EXISTS idx_captures_received_at ON captures(received_at);
                """
            )
            capture_columns = {
                row["name"] for row in connection.execute("PRAGMA table_info(captures)").fetchall()
            }
            if "capture_kind" not in capture_columns:
                connection.execute(
                    "ALTER TABLE captures ADD COLUMN capture_kind TEXT NOT NULL DEFAULT 'raw_debug'"
                )

    @contextmanager
    def connect(self, *, immediate: bool = False):
        connection = sqlite3.connect(str(self.database_path), timeout=10)
        connection.row_factory = sqlite3.Row
        try:
            if immediate:
                connection.execute("BEGIN IMMEDIATE")
            yield connection
            connection.commit()
        except Exception:
            connection.rollback()
            raise
        finally:
            connection.close()

    def rate_limit(self, scope: str, subject: str, seconds: int, maximum: int) -> bool:
        now = int(time.time())
        window = now - (now % seconds)
        with self.connect(immediate=True) as connection:
            connection.execute(
                "DELETE FROM rate_limits WHERE window_start < ?", (now - max(seconds * 3, 3600),)
            )
            cursor = connection.execute(
                """
                INSERT INTO rate_limits(scope, subject, window_start, count)
                VALUES(?, ?, ?, 1)
                ON CONFLICT(scope, subject, window_start)
                DO UPDATE SET count=count+1
                WHERE count < ?
                """,
                (scope, subject, window, maximum),
            )
            return cursor.rowcount == 1

    def use_nonce(self, install_id: str, nonce: str) -> bool:
        now = int(time.time())
        with self.connect() as connection:
            connection.execute("DELETE FROM nonces WHERE seen_at < ?", (now - 86400,))
            try:
                connection.execute(
                    "INSERT INTO nonces(install_id, nonce, seen_at) VALUES(?, ?, ?)",
                    (install_id, nonce, now),
                )
            except sqlite3.IntegrityError:
                return False
            return True

    def reserve_byte_budgets(
        self, entries: list[tuple[str, str, int, int, int]]
    ) -> bool:
        """Atomically reserve declared request bytes in fixed fail-closed windows."""
        now = int(time.time())
        windows = [
            (scope, subject, now - (now % seconds), amount, maximum)
            for scope, subject, seconds, amount, maximum in entries
        ]
        with self.connect(immediate=True) as connection:
            connection.execute(
                "DELETE FROM byte_budgets WHERE window_start < ?", (now - 3 * 86400,)
            )
            for scope, subject, window, amount, maximum in windows:
                row = connection.execute(
                    "SELECT bytes FROM byte_budgets WHERE scope=? AND subject=? AND window_start=?",
                    (scope, subject, window),
                ).fetchone()
                used = 0 if row is None else int(row["bytes"])
                if amount <= 0 or used + amount > maximum:
                    return False
            for scope, subject, window, amount, _ in windows:
                connection.execute(
                    """
                    INSERT INTO byte_budgets(scope, subject, window_start, bytes)
                    VALUES(?, ?, ?, ?)
                    ON CONFLICT(scope, subject, window_start)
                    DO UPDATE SET bytes=bytes+excluded.bytes
                    """,
                    (scope, subject, window, amount),
                )
            return True

    def installation(self, install_id: str):
        with self.connect() as connection:
            return connection.execute(
                "SELECT * FROM installations WHERE install_id=?", (install_id,)
            ).fetchone()

    def enroll(
        self,
        install_id: str,
        modulus: str,
        exponent: str,
        *,
        maximum_installations: int = 100_000,
        idle_retention_days: int = 30,
    ) -> bool:
        """Atomically refresh or add an identity without exceeding the registry cap."""
        now = int(time.time())
        with self.connect(immediate=True) as connection:
            existing = connection.execute(
                "SELECT modulus, exponent, status FROM installations WHERE install_id=?", (install_id,)
            ).fetchone()
            if existing is not None:
                if existing["modulus"] != modulus or existing["exponent"] != exponent:
                    raise ValueError("installation identity already exists with a different key")
                if existing["status"] != "active":
                    raise PermissionError("installation identity is revoked")
                connection.execute(
                    "UPDATE installations SET last_seen=? WHERE install_id=?", (now, install_id)
                )
                return False

            self._prune_installations(connection, now - idle_retention_days * 86400)
            count = connection.execute("SELECT COUNT(*) FROM installations").fetchone()[0]
            if count >= maximum_installations:
                # Unused registrations must not reserve the complete registry
                # for thirty days. Keep recent enrollment/upload races intact;
                # captured identities and revoked keys are never evicted here.
                self._prune_installations(connection, now - 3600, use_created_at=True)
                count = connection.execute("SELECT COUNT(*) FROM installations").fetchone()[0]
            if count >= maximum_installations:
                raise InstallationRegistryFullError("installation registry capacity is active")

            connection.execute(
                """
                INSERT INTO installations(install_id, modulus, exponent, status, created_at, last_seen)
                VALUES(?, ?, ?, 'active', ?, ?)
                """,
                (install_id, modulus, exponent, now, now),
            )
            return True

    def prune_installations(self, idle_retention_days: int) -> int:
        cutoff = int(time.time()) - idle_retention_days * 86400
        with self.connect(immediate=True) as connection:
            return self._prune_installations(connection, cutoff)

    @staticmethod
    def _prune_installations(connection: sqlite3.Connection, cutoff: int, *, use_created_at: bool = False) -> int:
        # Under pressure, refresh requests cannot retain unused slots forever.
        age_column = "created_at" if use_created_at else "last_seen"
        stale = f"""
            SELECT install_id
            FROM installations
            WHERE status='active'
              AND {age_column} < ?
              AND NOT EXISTS (
                  SELECT 1 FROM captures WHERE captures.install_id=installations.install_id
              )
        """
        connection.execute(
            f"DELETE FROM nonces WHERE install_id IN ({stale})",
            (cutoff,),
        )
        cursor = connection.execute(
            f"DELETE FROM installations WHERE install_id IN ({stale})",
            (cutoff,),
        )
        return cursor.rowcount

    def capture_by_hash(self, sha256: str):
        with self.connect(immediate=True) as connection:
            return self._retained_capture(connection, sha256)

    def _retained_capture(self, connection, sha256: str):
        row = connection.execute(
            "SELECT capture_id, relative_path, compressed_bytes FROM captures WHERE sha256=?", (sha256,)
        ).fetchone()
        if row is None:
            return None
        path = self.root / row["relative_path"]
        try:
            info = path.stat()
            if (not path.is_symlink() and path.resolve().is_relative_to((self.root / "accepted").resolve())
                    and stat.S_ISREG(info.st_mode) and info.st_size == row["compressed_bytes"]):
                return row
        except FileNotFoundError:
            pass
        # Reconcile older interrupted prune runs (and missing/damaged files) so
        # the same digest can be accepted again rather than falsely receipted.
        self._schedule_deletion(connection, row["relative_path"])
        connection.execute("DELETE FROM captures WHERE capture_id=?", (row["capture_id"],))
        return None

    @staticmethod
    def _schedule_deletion(connection, relative_path: str):
        connection.execute(
            "INSERT OR IGNORE INTO capture_deletions(relative_path, scheduled_at) VALUES(?, ?)",
            (relative_path, int(time.time())),
        )

    def quarantine_capture(self, incoming: Path, sha256: str, metadata: dict,
                           retention_days: int, quota_bytes: int) -> str:
        """Retain authenticated, hash-verified bytes as evidence, never as a landing."""
        if not re.fullmatch(r"[a-f0-9]{64}", sha256):
            raise ValueError("invalid quarantine digest")
        with self._rejection_lock:
            directory = self.root / "rejected"
            directory.mkdir(exist_ok=True)
            destination = directory / f"{sha256}.zip"
            if destination.exists():
                return "already_retained"
            size = incoming.stat().st_size
            if size > quota_bytes:
                return "over_quarantine_quota"
            self._prune_rejected(retention_days, quota_bytes - size, maximum_files=1999)
            os.replace(incoming, destination)
            temporary = directory / f"{sha256}.json.tmp"
            try:
                temporary.write_text(json.dumps(metadata, separators=(",", ":")), encoding="utf-8")
                os.replace(temporary, directory / f"{sha256}.json")
            finally:
                temporary.unlink(missing_ok=True)
            return "retained"

    def prune_rejected(self, retention_days: int, quota_bytes: int) -> None:
        with self._rejection_lock:
            self._prune_rejected(retention_days, quota_bytes)

    def _prune_rejected(self, retention_days: int, quota_bytes: int, maximum_files: int = 2000) -> None:
        directory = self.root / "rejected"
        cutoff = time.time() - retention_days * 86400
        files = []
        for path in directory.glob("*.zip"):
            if re.fullmatch(r"[a-f0-9]{64}\.zip", path.name) and not path.is_symlink() and path.is_file():
                files.append((path, path.stat()))
        files.sort(key=lambda pair: pair[1].st_mtime)
        total = sum(info.st_size for _, info in files)
        count = len(files)
        for path, info in files:
            if info.st_mtime >= cutoff and total <= quota_bytes and count <= maximum_files:
                continue
            path.unlink()
            path.with_suffix(".json").unlink(missing_ok=True)
            total -= info.st_size
            count -= 1

    def accepted_bytes_since(self, install_id: str, since: int) -> int:
        with self.connect() as connection:
            row = connection.execute(
                "SELECT COALESCE(SUM(compressed_bytes), 0) AS total FROM captures WHERE install_id=? AND received_at>=?",
                (install_id, since),
            ).fetchone()
            return int(row["total"])

    def add_capture(self, values: tuple) -> str:
        """Return the winning capture identity, including concurrent duplicates."""
        with self.connect(immediate=True) as connection:
            self._retained_capture(connection, values[2])
            connection.execute(
                """
                INSERT INTO captures(
                    capture_id, install_id, sha256, relative_path, received_at,
                    compressed_bytes, uncompressed_bytes, sample_count,
                    schema_version, app_version, capture_kind, source_address_hash)
                VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(sha256) DO NOTHING
                """,
                values,
            )
            connection.execute(
                "UPDATE installations SET last_seen=? WHERE install_id=?",
                (int(time.time()), values[1]),
            )
            return connection.execute("SELECT capture_id FROM captures WHERE sha256=?", (values[2],)).fetchone()["capture_id"]

    def prune_orphans(self, grace_seconds: int = 86400) -> int:
        # Only old, server-generated archives qualify. The grace interval keeps
        # an in-flight rename/INSERT pair out of reconciliation.
        cutoff = time.time() - grace_seconds
        with self.connect(immediate=True) as connection:
            known = {row["relative_path"] for row in connection.execute("SELECT relative_path FROM captures")}
            removed = 0
            for path in (self.root / "accepted").glob("*/*/*/*.zip"):
                if not re.fullmatch(r"[a-f0-9]{32}\.zip", path.name) or path.is_symlink():
                    continue
                if path.relative_to(self.root).as_posix() in known:
                    continue
                try:
                    if path.stat().st_mtime < cutoff:
                        path.unlink(missing_ok=True)
                        removed += 1
                except FileNotFoundError:
                    pass
            return removed

    def installation_count(self) -> int:
        with self.connect() as connection:
            return connection.execute("SELECT COUNT(*) FROM installations").fetchone()[0]

    def prune(self, retention_days: int, quota_bytes: int) -> None:
        # A crash followed by an immediate restart leaves fresh orphan files
        # inside the grace interval. Revisit them on later accepted uploads.
        with self._maintenance_lock:
            if time.monotonic() >= self._next_orphan_sweep:
                self.prune_orphans()
                self._next_orphan_sweep = time.monotonic() + 3600
            cutoff = int(time.time()) - retention_days * 86400
            with self.connect(immediate=True) as connection:
                rows = connection.execute(
                    "SELECT capture_id, relative_path, compressed_bytes, received_at FROM captures ORDER BY received_at"
                ).fetchall()
                total = sum(row["compressed_bytes"] for row in rows)
                for row in rows:
                    if row["received_at"] >= cutoff and total <= quota_bytes:
                        continue
                    self._schedule_deletion(connection, row["relative_path"])
                    connection.execute("DELETE FROM captures WHERE capture_id=?", (row["capture_id"],))
                    total -= row["compressed_bytes"]
            # First durably withdraw receipts and journal every deletion. An
            # unlink or later DB failure can only leave retryable garbage, never
            # a rollback-restored receipt for a file we already deleted.
            self._finish_capture_deletions()

    def _finish_capture_deletions(self) -> None:
        with self.connect() as connection:
            pending = connection.execute("SELECT relative_path FROM capture_deletions").fetchall()
        for row in pending:
            path = self.root / row["relative_path"]
            if (not path.is_symlink() and path.resolve().is_relative_to((self.root / "accepted").resolve())):
                path.unlink(missing_ok=True)
            with self.connect(immediate=True) as connection:
                connection.execute("DELETE FROM capture_deletions WHERE relative_path=?", (row["relative_path"],))
