import hashlib
import io
import struct
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

from app.capture_archive import CaptureArchive, Member, _Verified
from app.dashboard_index import _index_archive
from app.validation import validate_capture
from test_capture_archive import encode


ROOT = Path(__file__).resolve().parents[1]


def corrupt_deflate_member(body, name):
    with zipfile.ZipFile(io.BytesIO(body)) as archive:
        entry = archive.getinfo(name)
        assert entry.compress_type == zipfile.ZIP_DEFLATED
        offset = entry.header_offset
    changed = bytearray(body)
    name_size, extra_size = struct.unpack_from("<HH", changed, offset + 26)
    position = offset + 30 + name_size + extra_size
    # BTYPE=3 is reserved. Keep the directory, lengths and CRC untouched so the
    # actual DEFLATE reader must classify this corrupted compressed payload.
    changed[position] = (changed[position] & 0xf8) | 7
    return bytes(changed)


class CaptureContractRegressionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / "capture.zip"

    def tearDown(self):
        self.temp.cleanup()

    def write(self, schema=8, version=1, overrides=None, count=1):
        self.header = (ROOT / f"schema-v{schema}.header").read_text().strip()
        columns = self.header.split(",")
        values = dict.fromkeys(columns, "0")
        values.update(simulation_time_s="100", axis_elevator_set_valid="0")
        if schema >= 7:
            values.update(analysis_time_s="NaN", simulator_state="2", pause_flags="1")
        if schema >= 8:
            values["controller_data_v2"] = ""
        values.update(overrides or {})
        rows = []
        for sequence in range(1, count + 1):
            values.update(sequence=str(sequence), host_elapsed_s=str(sequence / 30))
            rows.append(",".join(values[column] for column in columns))
        members = {
            "session.txt": f"telemetry_schema={schema}\nsource_period=SIM_FRAME\nsample_count={count}\n".encode(),
            "telemetry.csv": (self.header + "\n" + "\n".join(rows) + "\n").encode(),
        }
        with zipfile.ZipFile(self.path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, data in (members if version == 1 else encode(members)).items():
                archive.writestr(name, data)
        return self.path

    def validate(self, schema=8, maximum=64 * 1024 * 1024):
        return validate_capture(self.path, self.header, maximum, schema)

    def test_corrupt_deflate_is_permanent_validation_failure_for_every_member(self):
        for version, names in ((1, ("session.txt", "telemetry.csv")),
                               (2, ("session.txt", "telemetry.csv.zst", "archive-format.bin"))):
            for name in names:
                with self.subTest(version=version, member=name):
                    self.write(version=version)
                    self.path.write_bytes(corrupt_deflate_member(self.path.read_bytes(), name))
                    with self.assertRaisesRegex(ValueError, "malformed or unsupported"):
                        self.validate()

    def test_unsupported_zip_extraction_features_are_permanent_validation_failures(self):
        for feature in ("version", "patched-data"):
            with self.subTest(feature=feature):
                self.write()
                data = bytearray(self.path.read_bytes())
                central = data.index(b"PK\x01\x02")
                if feature == "version":
                    struct.pack_into("<H", data, central + 6, 100)
                else:
                    struct.pack_into("<H", data, 6, struct.unpack_from("<H", data, 6)[0] | 0x20)
                    struct.pack_into("<H", data, central + 8, struct.unpack_from("<H", data, central + 8)[0] | 0x20)
                self.path.write_bytes(data)
                with self.assertRaisesRegex(ValueError, "malformed or unsupported"):
                    self.validate()

    def test_storage_read_errors_are_not_reclassified_as_bad_uploads(self):
        member = Member("session.txt", 1, zipfile.ZipInfo("session.txt"))
        with _Verified(io.BytesIO(b"x"), member) as source:
            with patch.object(source.source, "read", side_effect=OSError("disk read failed")):
                with self.assertRaisesRegex(OSError, "disk read failed"):
                    source.readinto(bytearray(1))

    def test_missing_elevator_age_is_compatible_across_schemas_and_formats(self):
        for schema in (5, 6, 7, 8):
            for version in (1, 2):
                for age in ("NaN", "0", "0.25"):
                    with self.subTest(schema=schema, version=version, age=age):
                        self.write(schema, version, {"axis_elevator_set_age_s": age})
                        original = self.path.read_bytes()
                        self.assertEqual(1, self.validate(schema).sample_count)
                        self.assertEqual(original, self.path.read_bytes())

    def test_missing_age_exception_does_not_relax_validity_or_numeric_contract(self):
        invalid = (
            {"axis_elevator_set_valid": "1", "axis_elevator_set_age_s": "NaN"},
            {"axis_elevator_set_valid": "2", "axis_elevator_set_age_s": "NaN"},
            {"axis_elevator_set_age_s": "Infinity"},
            {"axis_elevator_set_age_s": "-Infinity"},
            {"axis_elevator_set_age_s": "invalid"},
            {"axis_elevator_set_percent": "NaN"},
            {"g_force": "NaN"},
        )
        for schema in (5, 6, 7, 8):
            for version in (1, 2):
                for values in invalid:
                    with self.subTest(schema=schema, version=version, values=values):
                        self.write(schema, version, values)
                        with self.assertRaises(ValueError):
                            self.validate(schema)

    def test_bounded_pause_telemetry_is_accepted_regardless_of_compression_ratio(self):
        for schema in (5, 6, 7, 8):
            for version in (1, 2):
                with self.subTest(schema=schema, version=version):
                    self.write(schema, version, count=1000)
                    with CaptureArchive(self.path) as archive:
                        telemetry = archive.getinfo("telemetry.csv")
                        self.assertGreater(telemetry.file_size / telemetry.compress_size, 25)
                    self.assertEqual(1000, self.validate(schema).sample_count)

    def test_high_compression_still_obeys_expanded_bytes_rows_and_row_width(self):
        for version in (1, 2):
            with self.subTest(version=version):
                self.write(version=version, count=1000)
                with self.assertRaisesRegex(ValueError, "byte limit"):
                    self.validate(maximum=10000)
                self.write(version=version, count=30001)
                with self.assertRaisesRegex(ValueError, "more than 30000"):
                    self.validate()
                self.write(version=version, overrides={"g_force": "0" * 8193}, count=100)
                with self.assertRaisesRegex(ValueError, "row is too large"):
                    self.validate()

    def test_dashboard_does_not_flag_expected_missing_values_but_keeps_real_errors(self):
        cases = (({"axis_elevator_set_age_s": "NaN"}, 0, 0),
                 ({"axis_elevator_set_age_s": "NaN", "axis_elevator_set_valid": "1"}, 1, 0),
                 ({"analysis_time_s": "Infinity", "axis_elevator_set_age_s": "-Infinity"}, 2, 0),
                 ({"g_force": "NaN"}, 1, 0),
                 ({"g_force": "invalid"}, 0, 1))
        for version in (1, 2):
            for values, nonfinite, malformed in cases:
                with self.subTest(version=version, values=values):
                    self.write(version=version, overrides=values)
                    data = self.path.read_bytes()
                    indexed = _index_archive(self.path, {"capture_id": "fixture", "compressed_bytes": len(data),
                        "sha256": hashlib.sha256(data).hexdigest(), "sample_count": 1})
                    self.assertEqual(nonfinite, indexed["telemetry"]["nonfinite_values"])
                    self.assertEqual(malformed, indexed["telemetry"]["malformed_values"])
                    self.assertEqual(bool(nonfinite), any(w.startswith("nonfinite_values:") for w in indexed["warnings"]))


if __name__ == "__main__":
    unittest.main()
