import os
import hashlib
import struct
import tempfile
import unittest
import uuid
import zipfile
from pathlib import Path

from app.validation import _validate_source_timeline, validate_capture
from app.dashboard_index import _index_archive


ROOT = Path(__file__).resolve().parents[1]


def evidence(availability=2, mask=7, slew=0, reason=0):
    return struct.pack("<BbH16sqqqqqBbbBII7d", 1, -1, 0, uuid.uuid4().bytes_le,
                       1, 1, 1000, 1100, 1000, availability, mask, slew, reason, 1, 0,
                       12, 42, 23, 1000, 3, -0.0, 180)


class SourceObservationTests(unittest.TestCase):
    def test_missing_unknown_unavailable_and_raw_nonfinite_are_distinct(self):
        body = b"LSR1" + struct.pack("<i", 3) + b"\x00\x01" + evidence() + b"\x01" + evidence(3, -1, -1, 4)
        _validate_source_timeline(body, 3)
        raw = bytearray(evidence())
        struct.pack_into("<d", raw, 80, float("nan"))
        _validate_source_timeline(b"LSR1" + struct.pack("<i", 1) + b"\x01" + raw, 1)

    def test_rejects_truncation_tail_bad_identity_state_and_count(self):
        body = b"LSR1" + struct.pack("<i", 1) + b"\x01" + evidence()
        for length in range(len(body)):
            with self.subTest(length=length), self.assertRaises(ValueError):
                _validate_source_timeline(body[:length], 1)
        for offset in (0, 4, 8, 9, 10, 11, 69, 70, 71, 72):
            changed = bytearray(body)
            changed[offset] = 128
            with self.subTest(offset=offset), self.assertRaises(ValueError):
                _validate_source_timeline(changed, 1)
        with self.assertRaises(ValueError):
            _validate_source_timeline(body + b"\x00", 1)
        with self.assertRaises(ValueError):
            _validate_source_timeline(body, 2)

    def test_legacy_csv_5_6_7_are_accepted_without_new_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            for schema in (5, 6, 7):
                header = (ROOT / f"schema-v{schema}.header").read_text(encoding="utf-8").strip()
                columns = header.split(",")
                row = dict.fromkeys(columns, "0")
                row.update(sequence="1", simulation_time_s="1", analysis_time_s="NaN", pause_flags="-1")
                for include in (False, True):
                    path = Path(directory) / f"schema-{schema}-{include}.zip"
                    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
                        archive.writestr("telemetry.csv", header + "\n" + ",".join(row[column] for column in columns) + "\n")
                        archive.writestr("session.txt", f"telemetry_schema={schema}\nsource_period=SIM_FRAME\nsample_count=1\n")
                        if include:
                            archive.writestr("source-state.bin", b"LSR1" + struct.pack("<i", 1) + b"\x01" + evidence())
                    facts = validate_capture(path, header, 64 * 1024 * 1024, schema)
                    self.assertEqual(1, facts.sample_count)

    @unittest.skipUnless(os.environ.get("MSFSLS_SOURCE_FIXTURE"), "explicit .NET writer fixture required")
    def test_actual_net48_writer_with_source_metadata(self):
        path = Path(os.environ["MSFSLS_SOURCE_FIXTURE"])
        header = (ROOT / "schema-v8.header").read_text(encoding="utf-8").strip()
        self.assertEqual(40, validate_capture(path, header, 64 * 1024 * 1024, 8).sample_count)
        row = {"compressed_bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "sample_count": 40}
        self.assertEqual(40, _index_archive(path, row)["telemetry"]["rows"])


if __name__ == "__main__":
    unittest.main()
