import base64
import csv
import hashlib
import io
import json
import math
import os
import struct
import tempfile
import unittest
import uuid
import zipfile
from pathlib import Path

from app.capture_archive import CaptureArchive, MAXIMUM_SESSION_METADATA_BYTES
from app.dashboard_index import _telemetry, _index_archive
from app.validation import (_validate_controller_catalog, _validate_controller_samples,
                            _validate_controller_timeline, _session_values, validate_capture)
from tests.test_capture_archive import encode

ROOT = Path(__file__).resolve().parents[1]
HEADER = (ROOT / "schema-v8.header").read_text(encoding="utf-8").strip()


def observations(count=12, axes=8):
    payload = bytearray(struct.pack("<Hii", 2, count, axes))
    for device in range(count):
        payload.extend(struct.pack("<idBd", device, 12.5, 1, 0.01))
        for axis in range(axes):
            payload.extend(struct.pack("<dBd", -100.0 + axis * 25, 1, 0.02))
    return payload


def text(value):
    encoded = value.encode("utf-8")
    return struct.pack("<i", len(encoded)) + encoded


def catalog(count=12):
    payload = bytearray(struct.pack("<I", 0x3243434C) + uuid.uuid4().bytes_le + uuid.uuid4().bytes_le)
    payload.extend(struct.pack("<qqqqi", 1, 2, 100, 1, -count - 1))
    identities = [uuid.uuid4().bytes_le for _ in range(count)]
    for device, identity in enumerate(identities):
        payload.extend(struct.pack("<ii", device, device))
        payload.extend(text("Controller " + str(device)) + text("product"))
        payload.extend(struct.pack("<Bi", 255, 8) + identity + uuid.uuid4().bytes_le)
        payload.extend(struct.pack("<II", 0, 65535) * 8)
    payload.extend(text("SimObjects\\Airplanes\\Example\\config") + struct.pack("<i", 2 if count else 0))
    if count:
        # One physical lever controls two engines; GUID and final slider survive.
        for engine in (0, 1):
            payload.extend(struct.pack("<Bi16siB", 3, engine, identities[-1], 7, 1))
            payload.extend(text("Category profile / KEY_THROTTLE_AXIS_SET_EX1"))
    return payload


class ControllerSchema8Tests(unittest.TestCase):
    def test_large_controller_labels_survive_both_archive_formats(self):
        columns = HEADER.split(",")
        row = dict.fromkeys(columns, "0")
        row.update(sequence="1", analysis_time_s="NaN", pause_flags="-1",
                   controller_data_v2=base64.b64encode(observations(40)).decode())
        labels = ["".join(uuid.uuid4().hex for _ in range(16)) + " [DirectInput " + str(uuid.uuid4()) + "]"
                  for _ in range(40)]
        session = "telemetry_schema=8\nsource_period=SIM_FRAME\nsample_count=1\ncontrol_input_source_count=40\n"
        session += "".join(f"control_input_source_{index}={label}\n" for index, label in enumerate(labels))
        self.assertGreater(len(session.encode()), 16384)
        members = {"session.txt": session.encode(),
                   "telemetry.csv": (HEADER + "\n" + ",".join(row[column] for column in columns) + "\n").encode()}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.zip"
            for version in (1, 2):
                with self.subTest(version=version):
                    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
                        for name, content in (members if version == 1 else encode(members)).items():
                            archive.writestr(name, content)
                    self.assertEqual(1, validate_capture(path, HEADER, 64 * 1024 * 1024, 8).sample_count)
                    with CaptureArchive(path) as archive:
                        values = _session_values(archive.read("session.txt"))
                    self.assertEqual(labels, [values[f"control_input_source_{index}"] for index in range(40)])
                    content = path.read_bytes()
                    indexed = _index_archive(path, {"compressed_bytes": len(content),
                        "sha256": hashlib.sha256(content).hexdigest(), "sample_count": 1})
                    self.assertEqual(labels[-1], indexed["session"]["control_input_source_39"])
            with self.assertRaisesRegex(ValueError, "too large"):
                _session_values(b"x" * (MAXIMUM_SESSION_METADATA_BYTES + 1))
            with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
                archive.writestr("session.txt", b"x" * (MAXIMUM_SESSION_METADATA_BYTES + 1))
                archive.writestr("telemetry.csv", members["telemetry.csv"])
            with self.assertRaisesRegex(ValueError, "too large"):
                CaptureArchive(path)

    def test_all_devices_and_standard_axes_are_validated(self):
        for count, axes in ((0, 8), (1, 6), (12, 8), (40, 8), (700, 8)):
            with self.subTest(count=count, axes=axes):
                _validate_controller_samples(base64.b64encode(observations(count, axes)).decode())
        _validate_controller_samples("")

    def test_dynamic_dimensions_and_observations_are_strict(self):
        original = observations()
        invalid = [b"", original[:-1], original + b"x"]
        for offset, layout, value in ((0, "<H", 3), (2, "<i", -1), (2, "<i", 2**31-1),
                                      (6, "<i", 9), (10, "<i", -2), (22, "<B", 2),
                                      (14, "<d", math.nan), (14, "<d", 101), (23, "<d", -1),
                                      (23, "<d", math.inf), (10, "<i", -1)):
            changed = bytearray(original)
            struct.pack_into(layout, changed, offset, value)
            invalid.append(changed)
        for payload in invalid:
            # Empty text means legacy; an empty binary payload is not an encoding.
            if not payload:
                continue
            with self.subTest(length=len(payload)), self.assertRaises(ValueError):
                _validate_controller_samples(base64.b64encode(payload).decode())
        for encoded in ("!", "====", "a" * (2 * 1024 * 1024)):
            with self.assertRaises(ValueError):
                _validate_controller_samples(encoded)

    def test_lcc2_accepts_more_than_eight_devices_and_shared_engine_binding(self):
        for count in (0, 12, 200):
            payload = catalog(count)
            _validate_controller_catalog(payload)
            timeline = {"version": 1, "samples": 1, "catalogs": [base64.b64encode(payload).decode()], "transitions": [[0, 0]]}
            _validate_controller_timeline(json.dumps(timeline).encode(), 1)

    def test_profile_evidence_footer_is_optional_and_strict(self):
        original = catalog()
        evidence = json.dumps({"version": 1, "profiles": [{"name": "General", "xml": "<Profile/>"}]}).encode()
        footer = struct.pack("<Ii", 0x31464550, len(evidence)) + evidence
        _validate_controller_catalog(original)
        _validate_controller_catalog(original + footer)
        invalid = [footer[:-1], footer + b"x", b"BAD!" + footer[4:],
                   struct.pack("<Ii", 0x31464550, -1),
                   struct.pack("<Ii", 0x31464550, 0)]
        for value in (b"[]", b"null", b"1", b"{bad}", b'{"x":"\xff"}', b"[" * 2000):
            invalid.append(struct.pack("<Ii", 0x31464550, len(value)) + value)
        for value in invalid:
            with self.subTest(footer=value[:16]), self.assertRaises(ValueError):
                _validate_controller_catalog(original + value)

    def test_known_binding_survives_disconnected_hardware(self):
        payload = catalog()
        start = len(payload) - len(text("Category profile / KEY_THROTTLE_AXIS_SET_EX1")) - 26
        payload[start + 5:start + 21] = uuid.uuid4().bytes_le
        _validate_controller_catalog(payload)

    def test_lcc2_rejects_bad_binding_and_truncated_catalog(self):
        original = catalog()
        for length in range(1, len(original), 31):
            with self.assertRaises(ValueError):
                _validate_controller_catalog(original[:length])
        invalid = [original + b"x"]
        changed = bytearray(original)
        struct.pack_into("<i", changed, 68, 12)  # LCC2 must use extended records.
        invalid.append(changed)
        # Locate the final binding by its source string length.
        start = len(original) - len(text("Category profile / KEY_THROTTLE_AXIS_SET_EX1")) - 26
        for offset, layout, value in ((start, "<B", 4), (start + 1, "<i", 4),
                                      (start + 21, "<i", 8), (start + 25, "<B", 2)):
            changed = bytearray(original)
            struct.pack_into(layout, changed, offset, value)
            invalid.append(changed)
        changed = bytearray(original)
        changed[start + 5:start + 21] = bytes(16)
        invalid.append(changed)
        for changed in invalid:
            with self.assertRaises(ValueError):
                _validate_controller_catalog(changed)

    def test_schema8_archive_preserves_numeric_prefix_and_complete_controller_field(self):
        with tempfile.TemporaryDirectory() as directory:
            # The 700-device fixture exceeds the csv module's original 128 KiB
            # field limit; acceptance is governed by the actual payload budget.
            for count in (0, 12, 700):
                path = Path(directory) / f"capture-{count}.zip"
                columns = HEADER.split(",")
                row = dict.fromkeys(columns, "0")
                row.update(sequence="1", analysis_time_s="NaN", pause_flags="-1",
                           controller_data_v2=base64.b64encode(observations(count)).decode())
                with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
                    archive.writestr("session.txt", "telemetry_schema=8\nsource_period=SIM_FRAME\nsample_count=1\n")
                    archive.writestr("telemetry.csv", HEADER + "\n" + ",".join(row[column] for column in columns) + "\n")
                self.assertEqual(1, validate_capture(path, HEADER, 64 * 1024 * 1024, 8).sample_count)

    def test_dashboard_indexes_complete_axes_without_false_malformed_values(self):
        with tempfile.TemporaryDirectory() as directory:
            for axes in (6, 8):
                path = Path(directory) / f"dashboard-{axes}.zip"
                columns = HEADER.split(",")
                row = dict.fromkeys(columns, "0")
                row.update(sequence="1", controller_data_v2=base64.b64encode(observations(200, axes)).decode())
                with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
                    archive.writestr("telemetry.csv", HEADER + "\n" + ",".join(row[column] for column in columns) + "\n")
                with zipfile.ZipFile(path) as archive:
                    summary = _telemetry(archive)
                self.assertEqual(0, summary["malformed_values"])
                self.assertEqual(200 * axes, len(summary["physical_axis_channels"]))
                self.assertIn("controller_199_raw_" + ("slider1" if axes == 8 else "v"), summary["physical_axis_channels"])

    @unittest.skipUnless(os.environ.get("MSFSLS_DYNAMIC_CONTROLLER_FIXTURE"), "explicit .NET schema8 writer fixture required")
    def test_actual_net48_writer_archive(self):
        path = Path(os.environ["MSFSLS_DYNAMIC_CONTROLLER_FIXTURE"])
        facts = validate_capture(path, HEADER, 256 * 1024 * 1024, 8)
        self.assertEqual(3, facts.sample_count)
        with CaptureArchive(path, 256 * 1024 * 1024) as archive:
            with archive.open("telemetry.csv") as stream:
                rows = list(csv.DictReader(io.TextIOWrapper(stream, encoding="utf-8-sig", newline="")))
            self.assertEqual(3, len(rows))
            for row in rows:
                payload = base64.b64decode(row["controller_data_v2"], validate=True)
                self.assertEqual((2, 17, 8), struct.unpack_from("<Hii", payload))
                last_axis_offset = 10 + 16 * (21 + 17 * 8) + 21 + 7 * 17
                self.assertEqual((49.75, 1, .01), struct.unpack_from("<dBd", payload, last_axis_offset))
            timeline = json.loads(archive.read("controller-catalogs.json"))
            payload = base64.b64decode(timeline["catalogs"][0], validate=True)
            self.assertEqual(b"LCC2", payload[:4])
            self.assertEqual(-18, struct.unpack_from("<i", payload, 68)[0])
            offset = 72
            identities = []
            for slot in range(17):
                self.assertEqual((slot, slot), struct.unpack_from("<ii", payload, offset))
                offset += 8
                for _ in range(2):
                    offset += 4 + struct.unpack_from("<i", payload, offset)[0]
                self.assertEqual((255, 8), struct.unpack_from("<Bi", payload, offset))
                offset += 5
                identities.append(payload[offset:offset + 16])
                offset += 32 + 8 * 8
            offset += 4 + struct.unpack_from("<i", payload, offset)[0]
            self.assertEqual(3, struct.unpack_from("<i", payload, offset)[0])
            offset += 4
            bindings = []
            for _ in range(3):
                bindings.append(struct.unpack_from("<Bi16siB", payload, offset))
                offset += 26
                offset += 4 + struct.unpack_from("<i", payload, offset)[0]
            if offset < len(payload):
                marker, size = struct.unpack_from("<Ii", payload, offset)
                self.assertEqual(0x31464550, marker)
                offset += 8
                self.assertEqual(size, len(payload) - offset)
                self.assertIsInstance(json.loads(payload[offset:]), dict)
                offset += size
            self.assertEqual(len(payload), offset)
            self.assertEqual((3, 0, identities[-1], 7, 1), bindings[0])
            self.assertEqual((3, 1, identities[-1], 7, 1), bindings[1])
            self.assertEqual((0, -1), bindings[2][:2])
            self.assertNotIn(bindings[2][2], identities)
            dashboard = _telemetry(archive)
            self.assertEqual(136, len(dashboard["physical_axis_channels"]))
            self.assertEqual(0, dashboard["malformed_values"])


if __name__ == "__main__":
    unittest.main()
