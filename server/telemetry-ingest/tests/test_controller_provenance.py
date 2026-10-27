import base64
import json
import os
import struct
import tempfile
import unittest
import uuid
import zipfile
from pathlib import Path

from app.validation import _validate_controller_timeline, validate_capture


ROOT = Path(__file__).resolve().parents[1]
HEADER = (ROOT / "schema-v7.header").read_text(encoding="utf-8").strip()


def catalog():
    body = bytearray(struct.pack("<qi", 1, 1))
    body.extend(struct.pack("<ii", 3, 7))
    for text in ("Stick A", "model"):
        encoded = text.encode("utf-8")
        body.extend(struct.pack("<i", len(encoded)) + encoded)
    body.append(63)
    body.extend(struct.pack("<II", 0, 65535) * 6)
    header = bytearray(134)
    struct.pack_into("<IHHI", header, 0, 0x5452534C, 1, 12, len(body))
    header[12:28] = uuid.uuid4().bytes_le
    header[28:44] = uuid.uuid4().bytes_le
    struct.pack_into("<qBqqq", header, 44, 1, 2, 0, 2, 100)
    return header + body


class ControllerProvenanceTests(unittest.TestCase):
    def timeline(self):
        return {"version": 1, "samples": 3, "catalogs": [base64.b64encode(catalog()).decode("ascii"), ""],
                "transitions": [[0, 0], [1, 1], [2, -1]]}

    def test_scoped_catalog_unavailable_and_unknown_are_distinct(self):
        _validate_controller_timeline(json.dumps(self.timeline()).encode(), 3)

    def test_invalid_timeline_is_rejected(self):
        cases = []
        for key, value in (("version", True), ("version", 2), ("samples", 4), ("samples", True),
                           ("catalogs", ["not base64"]), ("catalogs", ["a" * 22000]),
                           ("transitions", []), ("transitions", [[1, 0]]),
                           ("transitions", [[0, 0], [0, 1]]), ("transitions", [[0, 2]]),
                           ("transitions", [[0, -2]]), ("transitions", [[False, 0]])):
            value_case = self.timeline()
            value_case[key] = value
            cases.append(value_case)
        for value in cases:
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    _validate_controller_timeline(json.dumps(value).encode(), 3)
        with self.assertRaises(ValueError):
            _validate_controller_timeline(b'{"version":1,"version":1}', 3)

    def test_binary_catalog_bounds_role_identity_and_truncation(self):
        original = catalog()
        mutations = []
        for offset, value in ((0, 0), (4, 2), (6, 2), (52, 1), (133, 3)):
            changed = bytearray(original)
            changed[offset] = value
            mutations.append(changed)
        changed = bytearray(original)
        changed[12:28] = bytes(16)
        mutations.append(changed)
        mutations.extend(original[:length] for length in range(1, len(original), 7))
        for changed in mutations:
            timeline = self.timeline()
            timeline["catalogs"][0] = base64.b64encode(changed).decode("ascii")
            with self.assertRaises(ValueError):
                _validate_controller_timeline(json.dumps(timeline).encode(), 3)

    def test_raw_archive_accepts_optional_metadata_and_keeps_legacy(self):
        with tempfile.TemporaryDirectory() as directory:
            for metadata in (None, self.timeline()):
                path = Path(directory) / ("new.zip" if metadata else "old.zip")
                with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
                    columns = HEADER.split(",")
                    rows = []
                    for i in range(3):
                        row = dict.fromkeys(columns, "0")
                        row.update(sequence=str(i + 1), simulation_time_s=str(i), analysis_time_s="NaN", pause_flags="-1")
                        rows.append(",".join(row[column] for column in columns))
                    archive.writestr("telemetry.csv", HEADER + "\n" + "\n".join(rows) + "\n")
                    archive.writestr("session.txt", "telemetry_schema=7\nsource_period=SIM_FRAME\nsample_count=3\n")
                    if metadata:
                        archive.writestr("controller-catalogs.json", json.dumps(metadata))
                self.assertEqual(3, validate_capture(path, HEADER, 64 * 1024 * 1024, 7).sample_count)

    @unittest.skipUnless(os.environ.get("MSFSLS_PROVENANCE_FIXTURE"), "explicit .NET writer fixture required")
    def test_actual_net48_writer_archive(self):
        header = (Path(__file__).resolve().parents[1] / "schema-v8.header").read_text().strip()
        facts = validate_capture(Path(os.environ["MSFSLS_PROVENANCE_FIXTURE"]), header, 64 * 1024 * 1024, 8)
        self.assertEqual(140, facts.sample_count)


if __name__ == "__main__":
    unittest.main()
