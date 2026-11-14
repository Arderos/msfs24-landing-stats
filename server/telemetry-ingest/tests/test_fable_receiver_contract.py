"""Receiver checks against real net48 recording/writer output, also required by CI."""
import csv
import hashlib
import io
import os
from pathlib import Path
import unittest
import zipfile

from app.capture_archive import CaptureArchive
from app.dashboard_index import _index_archive
from app.validation import validate_capture


class RecordingReceiverContractTests(unittest.TestCase):
    def validate_writer_output(self, variable):
        path = Path(os.environ[variable])
        original = path.read_bytes()
        header = (Path(__file__).resolve().parents[1] / "schema-v8.header").read_text().strip()
        facts = validate_capture(path, header, 64 * 1024 * 1024, 8)
        indexed = _index_archive(path, {
            "compressed_bytes": len(original), "sha256": hashlib.sha256(original).hexdigest(),
            "sample_count": facts.sample_count,
        })
        self.assertEqual(facts.sample_count, indexed["telemetry"]["rows"])
        self.assertEqual(0, indexed["telemetry"]["nonfinite_values"])
        self.assertEqual(0, indexed["telemetry"]["malformed_values"])
        self.assertEqual(original, path.read_bytes())
        return path, facts

    @unittest.skipUnless(os.environ.get("MSFSLS_AXIS_BOUNDARY_FIXTURE"), "explicit net48 flight-boundary fixture required")
    def test_actual_flight_boundary_archive_is_accepted(self):
        path, facts = self.validate_writer_output("MSFSLS_AXIS_BOUNDARY_FIXTURE")
        invalid = 0
        with CaptureArchive(path) as archive:
            with io.TextIOWrapper(archive.open("telemetry.csv"), encoding="utf-8-sig", newline="") as source:
                for row in csv.DictReader(source):
                    if row["axis_elevator_set_valid"] == "0":
                        invalid += 1
                        self.assertEqual(0.0, float(row["axis_elevator_set_age_s"]))
        self.assertGreater(invalid, 0, "The fixture must include invalidated elevator events.")
        self.assertGreater(facts.sample_count, 1)

    @unittest.skipUnless(os.environ.get("MSFSLS_PAUSED_RAW_FIXTURE"), "explicit net48 paused RAW fixture required")
    def test_actual_full_paused_raw_chunk_is_accepted(self):
        path, facts = self.validate_writer_output("MSFSLS_PAUSED_RAW_FIXTURE")
        self.assertEqual(30000, facts.sample_count)
        self.assertEqual("raw_debug", facts.capture_kind)
        with zipfile.ZipFile(path) as archive:
            telemetry = archive.getinfo("telemetry.csv")
            self.assertGreater(telemetry.file_size, 25 * telemetry.compress_size)


if __name__ == "__main__":
    unittest.main()
