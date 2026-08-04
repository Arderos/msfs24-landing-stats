import os
import sqlite3
import threading
import time
from contextlib import contextmanager
from pathlib import Path


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
                CREATE TABLE IF NOT EXISTS invite_codes (
                    code_hash TEXT PRIMARY KEY,
                    created_at INTEGER NOT NULL,
                    used_at INTEGER,
                    used_by TEXT
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
                """
            )

    @contextmanager
    def connect(self):
        connection = sqlite3.connect(str(self.database_path), timeout=10)
        connection.row_factory = sqlite3.Row
        try:
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
        with self.connect() as connection:
            connection.execute(
                "DELETE FROM rate_limits WHERE window_start < ?", (now - max(seconds * 3, 3600),)
            )
            row = connection.execute(
                "SELECT count FROM rate_limits WHERE scope=? AND subject=? AND window_start=?",
                (scope, subject, window),
            ).fetchone()
            if row is not None and row["count"] >= maximum:
                return False
            connection.execute(
                """
                INSERT INTO rate_limits(scope, subject, window_start, count)
                VALUES(?, ?, ?, 1)
                ON CONFLICT(scope, subject, window_start)
                DO UPDATE SET count=count+1
                """,
                (scope, subject, window),
            )
            return True

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

    def enroll(self, install_id: str, modulus: str, exponent: str, invite_hash: str | None) -> None:
        now = int(time.time())
        with self.connect() as connection:
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
                return

            if invite_hash is not None:
                invite = connection.execute(
                    "SELECT used_at FROM invite_codes WHERE code_hash=?", (invite_hash,)
                ).fetchone()
                if invite is None or invite["used_at"] is not None:
                    raise PermissionError("invitation code is invalid or already used")
                connection.execute(
                    "UPDATE invite_codes SET used_at=?, used_by=? WHERE code_hash=? AND used_at IS NULL",
                    (now, install_id, invite_hash),
                )
                if connection.total_changes != 1:
                    raise PermissionError("invitation code was consumed concurrently")

            connection.execute(
                """
                INSERT INTO installations(install_id, modulus, exponent, status, created_at, last_seen)
                VALUES(?, ?, ?, 'active', ?, ?)
                """,
                (install_id, modulus, exponent, now, now),
            )

    def add_invite(self, code_hash: str) -> None:
        with self.connect() as connection:
            connection.execute(
                "INSERT INTO invite_codes(code_hash, created_at) VALUES(?, ?)",
                (code_hash, int(time.time())),
            )

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
                    schema_version, app_version, source_address_hash)
                VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
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
