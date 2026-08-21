import os
import sqlite3
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
        with self.connect() as connection:
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
    def _prune_installations(connection: sqlite3.Connection, cutoff: int) -> int:
        stale = """
            SELECT install_id
            FROM installations
            WHERE status='active'
              AND last_seen < ?
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
        with self.connect() as connection:
            return connection.execute(
                "SELECT capture_id FROM captures WHERE sha256=?", (sha256,)
            ).fetchone()

    def accepted_bytes_since(self, install_id: str, since: int) -> int:
        with self.connect() as connection:
            row = connection.execute(
                "SELECT COALESCE(SUM(compressed_bytes), 0) AS total FROM captures WHERE install_id=? AND received_at>=?",
                (install_id, since),
            ).fetchone()
            return int(row["total"])

    def add_capture(self, values: tuple) -> None:
        with self.connect() as connection:
            connection.execute(
                """
                INSERT INTO captures(
                    capture_id, install_id, sha256, relative_path, received_at,
                    compressed_bytes, uncompressed_bytes, sample_count,
                    schema_version, app_version, capture_kind, source_address_hash)
                VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                values,
            )
            connection.execute(
                "UPDATE installations SET last_seen=? WHERE install_id=?",
                (int(time.time()), values[1]),
            )

    def prune(self, retention_days: int, quota_bytes: int) -> None:
        cutoff = int(time.time()) - retention_days * 86400
        with self.connect() as connection:
            rows = connection.execute(
                "SELECT capture_id, relative_path, compressed_bytes, received_at FROM captures ORDER BY received_at"
            ).fetchall()
            total = sum(row["compressed_bytes"] for row in rows)
            for row in rows:
                if row["received_at"] >= cutoff and total <= quota_bytes:
                    continue
                candidate = (self.root / row["relative_path"]).resolve()
                accepted_root = (self.root / "accepted").resolve()
                if candidate.is_relative_to(accepted_root) and candidate.is_file():
                    candidate.unlink()
                connection.execute("DELETE FROM captures WHERE capture_id=?", (row["capture_id"],))
                total -= row["compressed_bytes"]
