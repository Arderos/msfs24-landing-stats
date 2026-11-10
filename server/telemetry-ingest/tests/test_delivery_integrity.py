import concurrent.futures
import os
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest import mock
from contextlib import contextmanager

from app.store import InstallationByteQuotaExceededError, Store
from app.validation import _validate_controller_catalog
import struct
import base64


class DeliveryIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.store = Store(self.root)
        self.store.initialize()
        self.store.enroll("a" * 32, "key", "exponent")

    def tearDown(self):
        self.temp.cleanup()

    def capture(self, identity, digest="b" * 64, received=None):
        relative = f"accepted/2026/10/17/{identity}.zip"
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"archive")
        return (identity, "a" * 32, digest, relative, received or int(time.time()), 7, 14, 1, 7, "0.9.5", "raw_debug", "source")

    def test_concurrent_duplicate_insert_returns_one_winner(self):
        values = [self.capture(f"{i:032x}") for i in range(12)]
        with concurrent.futures.ThreadPoolExecutor(max_workers=12) as workers:
            winners = list(workers.map(lambda value: self.store.add_capture(value, daily_install_quota_bytes=7), values))
        self.assertEqual(1, len(set(winners)))
        self.assertEqual(winners[0], self.store.capture_by_hash("b" * 64)["capture_id"])

    def test_concurrent_stores_enforce_rolling_quota_across_utc_days(self):
        now = int(time.time())
        prior = self.capture("1" * 32, "1" * 64, now - now % 86400 - 1)
        self.store.add_capture(prior, daily_install_quota_bytes=14)
        values = [self.capture(f"{i:032x}", f"{i:064x}") for i in (2, 3)]
        barrier = threading.Barrier(2)
        def accept(value):
            # Independent Store instances must still serialize via SQLite.
            store = Store(self.root)
            barrier.wait(timeout=10)
            try:
                return store.add_capture(value, daily_install_quota_bytes=14)
            except InstallationByteQuotaExceededError:
                return None
        with mock.patch("app.store.time.time", return_value=now), \
                concurrent.futures.ThreadPoolExecutor(max_workers=2) as workers:
            results = list(workers.map(accept, values))
        self.assertEqual(1, sum(value is not None for value in results))
        self.assertEqual(14, self.store.accepted_bytes_since("a" * 32, now - 86400))
        rejected = values[results.index(None)]
        self.assertIsNone(self.store.capture_by_hash(rejected[2]))

    def test_rolling_quota_excludes_expired_and_other_installation_captures(self):
        now = int(time.time())
        expired = self.capture("1" * 32, "1" * 64, now - 86401)
        self.store.add_capture(expired, daily_install_quota_bytes=7)
        self.store.enroll("f" * 32, "other-key", "exponent")
        other = list(self.capture("2" * 32, "2" * 64))
        other[1] = "f" * 32
        self.store.add_capture(tuple(other), daily_install_quota_bytes=7)
        current = self.capture("3" * 32, "3" * 64)
        self.assertEqual(current[0], self.store.add_capture(current, daily_install_quota_bytes=7))
        with self.assertRaises(InstallationByteQuotaExceededError):
            self.store.add_capture(self.capture("4" * 32, "4" * 64), daily_install_quota_bytes=7)

    def test_acceptance_rollback_leaves_no_receipt_or_quota_charge(self):
        value = self.capture("c" * 32)
        connect = self.store.connect
        @contextmanager
        def fail_commit(**kwargs):
            with connect(**kwargs) as connection:
                yield connection
                raise RuntimeError("commit failed")
        with mock.patch.object(self.store, "connect", fail_commit), self.assertRaisesRegex(RuntimeError, "commit failed"):
            self.store.add_capture(value, daily_install_quota_bytes=7)
        self.assertIsNone(self.store.capture_by_hash(value[2]))
        self.assertEqual(0, self.store.accepted_bytes_since("a" * 32, 0))
        self.assertEqual(value[0], self.store.add_capture(value, daily_install_quota_bytes=7))

    def test_prune_is_serialized_and_tolerates_missing_file(self):
        values = self.capture("c" * 32, received=1)
        self.store.add_capture(values, daily_install_quota_bytes=1024)
        (self.root / values[3]).unlink()
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as workers:
            list(workers.map(lambda _: self.store.prune(1, 100), range(8)))
        self.assertIsNone(self.store.capture_by_hash("b" * 64))

    def test_prune_unlink_failure_does_not_restore_receipts_and_restart_finishes(self):
        values = [self.capture(f"{i:032x}", f"{i:064x}", int(time.time()) - 10 + i) for i in (1, 2, 3)]
        for value in values:
            self.store.add_capture(value, daily_install_quota_bytes=1024)
        unlink = Path.unlink
        def fail_second(path, *args, **kwargs):
            if path == self.root / values[1][3]:
                raise PermissionError("second eviction failed")
            return unlink(path, *args, **kwargs)
        with mock.patch.object(Path, "unlink", fail_second), self.assertRaises(PermissionError):
            self.store.prune(30, 7)
        self.assertFalse((self.root / values[0][3]).exists())
        self.assertTrue((self.root / values[1][3]).exists())
        for value in values[:2]:
            self.assertIsNone(self.store.capture_by_hash(value[2]))
        restarted = Store(self.root)
        restarted.initialize()
        restarted.prune(30, 7)
        self.assertFalse((self.root / values[1][3]).exists())
        self.assertIsNotNone(restarted.capture_by_hash(values[2][2]))
        with restarted.connect() as connection:
            self.assertEqual(0, connection.execute("SELECT COUNT(*) FROM capture_deletions").fetchone()[0])

    def test_prune_database_commit_failure_leaves_bytes_and_receipt(self):
        value = self.capture("c" * 32, received=1)
        self.store.add_capture(value, daily_install_quota_bytes=1024)
        self.store._next_orphan_sweep = float("inf")
        connect = self.store.connect
        @contextmanager
        def fail_commit(**kwargs):
            with connect(**kwargs) as connection:
                yield connection
                raise RuntimeError("commit failed")
        with mock.patch.object(self.store, "connect", fail_commit), self.assertRaises(RuntimeError):
            self.store.prune(1, 100)
        self.assertTrue((self.root / value[3]).exists())
        self.assertIsNotNone(self.store.capture_by_hash(value[2]))

    def test_unlink_before_journal_cleanup_failure_recovers(self):
        value = self.capture("c" * 32, received=1)
        self.store.add_capture(value, daily_install_quota_bytes=1024)
        self.store._next_orphan_sweep = float("inf")
        connect = self.store.connect
        @contextmanager
        def fail_after_unlink(**kwargs):
            with connect(**kwargs) as connection:
                yield connection
                if not (self.root / value[3]).exists():
                    raise RuntimeError("journal cleanup commit failed")
        with mock.patch.object(self.store, "connect", fail_after_unlink), self.assertRaises(RuntimeError):
            self.store.prune(1, 100)
        self.assertFalse((self.root / value[3]).exists())
        self.assertIsNone(self.store.capture_by_hash(value[2]))
        self.store.prune(1, 100)
        with self.store.connect() as connection:
            self.assertEqual(0, connection.execute("SELECT COUNT(*) FROM capture_deletions").fetchone()[0])

    def test_missing_or_truncated_duplicate_can_be_received_again(self):
        for truncate in (False, True):
            value = self.capture("c" * 32)
            self.store.add_capture(value, daily_install_quota_bytes=1024)
            path = self.root / value[3]
            if truncate:
                path.write_bytes(b"bad")
            else:
                path.unlink()
            self.assertIsNone(self.store.capture_by_hash(value[2]))
            replacement = self.capture("d" * 32)
            self.assertEqual(replacement[0], self.store.add_capture(replacement, daily_install_quota_bytes=1024))
            self.assertEqual(replacement[0], self.store.capture_by_hash(value[2])["capture_id"])
            self.store.prune(30, 100)
            with self.store.connect() as connection:
                connection.execute("DELETE FROM captures")
            (self.root / replacement[3]).unlink()

    def test_transient_artifact_read_error_retains_receipt_for_retry(self):
        value = self.capture("c" * 32)
        self.store.add_capture(value, daily_install_quota_bytes=1024)
        with mock.patch.object(Path, "stat", side_effect=PermissionError("temporary storage failure")):
            with self.assertRaises(PermissionError):
                self.store.capture_by_hash(value[2])
        self.assertIsNotNone(self.store.capture_by_hash(value[2]))

    def test_orphan_collection_keeps_recent_and_referenced_files(self):
        stored = self.capture("c" * 32)
        self.store.add_capture(stored, daily_install_quota_bytes=1024)
        orphan = self.capture("d" * 32)
        recent = self.capture("e" * 32)
        for value in (stored, orphan):
            os.utime(self.root / value[3], (1, 1))
        self.assertEqual(1, self.store.prune_orphans())
        self.assertTrue((self.root / stored[3]).exists())
        self.assertTrue((self.root / recent[3]).exists())
        self.assertFalse((self.root / orphan[3]).exists())

    def test_full_registry_recovers_unused_old_enrollments(self):
        with self.store.connect() as connection:
            connection.execute("UPDATE installations SET created_at=1")
        # A refresh cannot hold a never-used registry slot indefinitely.
        self.assertFalse(self.store.enroll("a" * 32, "key", "exponent", maximum_installations=1))
        self.assertTrue(self.store.enroll("f" * 32, "other", "exponent", maximum_installations=1))
        self.assertIsNone(self.store.installation("a" * 32))
        self.assertEqual(1, self.store.installation_count())

    def test_lcc1_is_independent_from_ipc_and_rejects_trailing_data(self):
        header = struct.pack("<I", 0x3143434C) + b"a" * 16 + b"b" * 16 + struct.pack("<qqqqi", 1, 2, 3, 4, 0)
        _validate_controller_catalog(header)
        with self.assertRaises(ValueError):
            _validate_controller_catalog(header + b"x")
        with self.assertRaises(ValueError):
            _validate_controller_catalog(header[:40])

    def test_lcc1_real_dotnet_catalog_and_every_truncated_prefix(self):
        value = base64.b64decode("TENDMREREREiIjMzRERVVVVVVVVmZmZmd3eIiJmZqqqqqqqqAgAAAAAAAAADAAAAAAAAAKAPAAAAAAAABQAAAAAAAAABAAAAAAAAAAcAAAANAAAARml4dHVyZSBzdGljaw8AAAAxMjM0OjU2Nzg6bW9kZWwDAAAAAP//AAAAAAAA//8AAAAAAAD//wAAAAAAAP//AAAAAAAA//8AAAAAAAD//wAA")
        _validate_controller_catalog(value)
        # Empty payload is the version-1 wrapper's explicit unavailable marker.
        for length in range(1, len(value)):
            with self.assertRaises(ValueError, msg=f"Accepted truncated C# catalog length {length}"):
                _validate_controller_catalog(value[:length])


if __name__ == "__main__":
    unittest.main()
