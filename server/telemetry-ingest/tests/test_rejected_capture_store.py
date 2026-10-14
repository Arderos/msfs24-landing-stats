import concurrent.futures
import hashlib
import os
import tempfile
import time
import unittest
from pathlib import Path

from app.store import Store


class RejectedCaptureStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.store = Store(self.root)
        self.store.initialize()

    def tearDown(self):
        self.temp.cleanup()

    def retain(self, body, name="upload.part", quota=1024):
        path = self.root / "incoming" / name
        path.write_bytes(body)
        digest = hashlib.sha256(body).hexdigest()
        result = self.store.quarantine_capture(path, digest, {"reason": "test"}, 7, quota)
        return digest, result

    def test_retains_exact_bytes_and_deduplicates_without_overwriting_metadata(self):
        digest, status = self.retain(b"evidence")
        self.assertEqual("retained", status)
        self.assertEqual(b"evidence", (self.root / "rejected" / f"{digest}.zip").read_bytes())
        _, status = self.retain(b"evidence")
        self.assertEqual("already_retained", status)
        self.assertEqual(1, len(list((self.root / "rejected").glob("*.zip"))))
        self.assertIsNone(self.store.capture_by_hash(digest))

    def test_quota_and_age_prune_only_quarantine_not_accepted_archives(self):
        protected = self.root / "accepted" / "original.zip"
        protected.write_bytes(b"keep")
        first, _ = self.retain(b"1234", quota=6)
        first_zip = self.root / "rejected" / f"{first}.zip"
        os.utime(first_zip, (time.time() - 100, time.time() - 100))
        second, _ = self.retain(b"5678", quota=6)
        self.assertFalse(first_zip.exists())
        self.assertFalse(first_zip.with_suffix(".json").exists())
        second_zip = self.root / "rejected" / f"{second}.zip"
        os.utime(second_zip, (0, 0))
        self.store.prune_rejected(7, 6)
        self.assertFalse(second_zip.exists())
        self.assertEqual(b"keep", protected.read_bytes())

    def test_oversize_and_bad_digest_do_not_move_incoming_file(self):
        _, status = self.retain(b"12345", quota=4)
        self.assertEqual("over_quarantine_quota", status)
        path = self.root / "incoming" / "upload.part"
        with self.assertRaises(ValueError):
            self.store.quarantine_capture(path, "../bad", {}, 7, 1024)
        self.assertEqual(b"12345", path.read_bytes())

    def test_concurrent_signed_byte_reservations_never_exceed_quota(self):
        def reserve(_):
            return self.store.reserve_byte_budgets([("test", "same", 86400, 1, 5)])
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            results = list(pool.map(reserve, range(20)))
        self.assertEqual(5, sum(results))

    def test_concurrent_quarantine_reservations_preserve_total_bound(self):
        def reserve(index):
            return self.retain(str(index).encode(), name=f"{index}.part", quota=5)
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            list(pool.map(reserve, range(20)))
        files = list((self.root / "rejected").glob("*.zip"))
        self.assertLessEqual(sum(path.stat().st_size for path in files), 5)
        self.assertEqual(len(files), len(list((self.root / "rejected").glob("*.json"))))
