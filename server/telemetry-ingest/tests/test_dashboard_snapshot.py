"""Snapshot isolation and atomic-publication checks, independent of the web UI."""

import hashlib
import json
import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from app.dashboard_snapshot import export_snapshot
from app.store import Store


class DashboardSnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name) / "receiver"
        self.output = Path(self.temporary.name) / "snapshot" / "metadata.json"
        self.store = Store(self.root)
        self.store.initialize()
        self.writer = sqlite3.connect(self.root / "telemetry.sqlite3")
        self.writer.execute("PRAGMA wal_autocheckpoint=0")
        self.add_capture("a")

    def tearDown(self):
        self.writer.close()
        self.temporary.cleanup()

    def add_capture(self, character):
        identity = character * 32
        self.writer.execute("INSERT INTO installations VALUES(?,?,?,?,?,?)", (
            identity, "private-registry-modulus", "private-registry-exponent", "active", 100, 200,
        ))
        self.writer.execute("INSERT INTO captures VALUES(?,?,?,?,?,?,?,?,?,?,?,?)", (
            identity, identity, character * 64, f"accepted/{identity}.zip", 1000,
            10, 20, 3, 6, "0.9.1", "automatic_landing", "private-source-address-hash",
        ))
        self.writer.commit()

    def source_state(self):
        return {table: self.writer.execute(f"SELECT * FROM {table} ORDER BY 1").fetchall()
                for table in ("sqlite_master", "installations", "captures", "byte_budgets")}

    def test_reads_new_commits_in_live_wal_without_mutating_receiver(self):
        self.assertTrue(Path(str(self.root / "telemetry.sqlite3") + "-wal").exists())
        original = self.source_state()
        first = export_snapshot(self.root, self.output)
        self.assertEqual(1, len(first["captures"]))
        self.assertEqual(original, self.source_state())
        self.add_capture("b")
        second_state = self.source_state()
        second = export_snapshot(self.root, self.output)
        self.assertEqual(2, len(second["captures"]))
        self.assertEqual(second_state, self.source_state())
        self.assertEqual(second, json.loads(self.output.read_text(encoding="utf-8")))

    def test_capture_and_registry_totals_share_one_sqlite_snapshot(self):
        original_connect = sqlite3.connect
        injected = []

        def connect(*args, **kwargs):
            connection = original_connect(*args, **kwargs)

            def on_query(sql):
                if "SELECT status,COUNT(*)" in sql and not injected:
                    injected.append(True)
                    self.add_capture("b")

            connection.set_trace_callback(on_query)
            return connection

        with mock.patch("app.dashboard_snapshot.sqlite3.connect", side_effect=connect):
            snapshot = export_snapshot(self.root, self.output)
        self.assertEqual([True], injected)
        self.assertEqual(1, len(snapshot["captures"]))
        self.assertEqual(1, snapshot["registry"]["active"])
        following = export_snapshot(self.root, self.output)
        self.assertEqual(2, len(following["captures"]))
        self.assertEqual(2, following["registry"]["active"])

    def test_export_contains_no_enrollment_key_or_source_address_material(self):
        snapshot = export_snapshot(self.root, self.output)
        content = self.output.read_text(encoding="utf-8")
        for value in ("private-registry-modulus", "private-registry-exponent", "private-source-address-hash"):
            self.assertNotIn(value, content)
        row = snapshot["captures"][0]
        self.assertEqual(hashlib.sha256(("dashboard-installation:" + "a" * 32).encode()).hexdigest(), row["install_id"])
        self.assertNotEqual("a" * 32, row["install_id"])
        self.assertNotIn("modulus", row)
        self.assertNotIn("exponent", row)
        self.assertNotIn("source_address_hash", row)

    def test_atomic_replace_failure_keeps_previous_complete_snapshot(self):
        export_snapshot(self.root, self.output)
        previous = self.output.read_bytes()
        self.add_capture("b")
        with mock.patch("app.dashboard_snapshot.os.replace", side_effect=OSError("simulated replace failure")):
            with self.assertRaises(OSError):
                export_snapshot(self.root, self.output)
        self.assertEqual(previous, self.output.read_bytes())
        self.assertFalse(self.output.with_suffix(".tmp").exists())

    def test_source_failure_keeps_previous_complete_snapshot(self):
        export_snapshot(self.root, self.output)
        previous = self.output.read_bytes()
        absent = self.root.parent / "absent"
        absent.mkdir()
        with self.assertRaises(sqlite3.Error):
            export_snapshot(absent, self.output)
        self.assertEqual(previous, self.output.read_bytes())
        self.assertFalse((absent / "telemetry.sqlite3").exists())

    def test_only_global_signed_attempt_bytes_are_exported(self):
        self.writer.executemany("INSERT INTO byte_budgets VALUES(?,?,?,?)", [
            ("capture-global-day", "global", 100, 500),
            ("capture-install-day", "a" * 32, 100, 50),
            ("capture-source-day", "source-secret", 100, 75),
        ])
        self.writer.commit()
        snapshot = export_snapshot(self.root, self.output)
        self.assertEqual([{"window_start": 100, "bytes": 500}], snapshot["signed_attempt_byte_budgets"])
        self.assertNotIn("source-secret", self.output.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
