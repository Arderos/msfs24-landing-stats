import hashlib
import io
import json
import os
import struct
import tempfile
import unittest
from unittest.mock import patch
import zipfile
from pathlib import Path

import zstandard

from app.capture_archive import CaptureArchive, MANIFEST, Member, _Verified
from app.validation import validate_capture
from app.dashboard_index import _index_archive


def encode(members):
    descriptor = bytearray(b"LSAR0002" + bytes([len(members)]))
    encoded = {}
    for name, value in members.items():
        codec = 0 if name == "session.txt" else 1
        descriptor.extend(bytes([len(name)]) + name.encode("ascii") + struct.pack("<Bq", codec, len(value)) + hashlib.sha256(value).digest())
        encoded[name if codec == 0 else name + ".zst"] = value if codec == 0 else zstandard.ZstdCompressor(level=9).compress(value)
    encoded[MANIFEST] = descriptor
    return encoded


class CaptureArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / "capture.zip"
        header = (Path(__file__).resolve().parents[1] / "schema-v7.header").read_text().strip()
        columns = header.split(",")
        row = {column: "0" for column in columns}
        row.update(sequence="1", simulation_time_s="1", analysis_time_s="NaN", pause_flags="-1")
        self.header = header
        self.members = {"session.txt": b"telemetry_schema=7\nsource_period=SIM_FRAME\nsample_count=1\n",
                        "telemetry.csv": (header + "\n" + ",".join(row[column] for column in columns) + "\n").encode()}

    def tearDown(self):
        self.temp.cleanup()

    def write(self, members, compression=zipfile.ZIP_STORED):
        with zipfile.ZipFile(self.path, "w", compression=compression) as output:
            for name, data in members.items():
                output.writestr(name, data)

    def read_all(self):
        with CaptureArchive(self.path) as archive:
            return {member.filename: archive.read(member.filename) for member in archive.infolist()}

    def test_roundtrip_and_validation_for_legacy_and_both_zip_wrappers(self):
        for version in (1, 2):
            for compression in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                self.write(self.members if version == 1 else encode(self.members), compression)
                before = self.path.read_bytes()
                self.assertEqual(self.members, self.read_all())
                self.assertEqual(1, validate_capture(self.path, self.header, 64 * 1024 * 1024, 7).sample_count)
                self.assertEqual(before, self.path.read_bytes())

    def test_descriptor_and_frame_corruption(self):
        mutations = []
        original = encode(self.members)
        for length in range(len(original[MANIFEST])):
            mutations.append({**original, MANIFEST: original[MANIFEST][:length]})
        for key in (MANIFEST, "telemetry.csv.zst"):
            mutations.extend([{**original, key: original[key] + b"tail"}, {**original, key: original[key][:-1]}])
        checksum = bytearray(original[MANIFEST]); checksum[-1] ^= 1
        mutations.append({**original, MANIFEST: checksum})
        mutations.append({**original, "telemetry.csv.zst": original["telemetry.csv.zst"] * 2})
        window = b"\x28\xb5\x2f\xfd\x00\xff\x01\x00\x00"
        mutations.append({**original, "telemetry.csv.zst": window})
        mutations.append({**original, "telemetry.csv.zst": b"x" * 65536})
        for i, value in enumerate(mutations):
            with self.subTest(case=i):
                self.write(value)
                with self.assertRaises((ValueError, zipfile.BadZipFile)):
                    self.read_all()

    def test_decoded_budget_and_forged_directory_counts(self):
        self.write(encode(self.members))
        with self.assertRaisesRegex(ValueError, "byte limit"):
            CaptureArchive(self.path, 100)
        data = bytearray(self.path.read_bytes()); data[-12] = 0
        self.path.write_bytes(data)
        with self.assertRaisesRegex(ValueError, "directory"):
            CaptureArchive(self.path)

    def test_zip64_directory_cannot_override_the_bounded_directory(self):
        # Tiny members only: exercise two different directory interpretations,
        # without allocating a decompression bomb or contacting the receiver.
        self.write({"session.txt": b"session", "telemetry.csv": b"row"}, zipfile.ZIP_BZIP2)
        data = self.path.read_bytes()
        start = struct.unpack_from("<I", data, len(data) - 6)[0]
        directory = data[start:-22]
        name = b"session.txt"
        header = bytearray(46)
        header[:4] = b"PK\x01\x02"
        struct.pack_into("<HHH", header, 28, len(name), 0, len(directory) + 76)
        real_start = start + len(header) + len(name)
        end64 = struct.pack("<4sQHHIIQQQQ", b"PK\x06\x06", 44, 45, 45, 0, 0, 2, 2, len(directory), real_start)
        locator = struct.pack("<4sIQI", b"PK\x06\x07", 0, real_start + len(directory), 1)
        bounded_directory = header + name + directory + end64 + locator
        end = struct.pack("<4sHHHHIIH", b"PK\x05\x06", 0, 0, 1, 1, len(bounded_directory), start, 0)
        self.path.write_bytes(data[:start] + bounded_directory + end)
        with self.assertRaisesRegex(ValueError, "ZIP64"):
            CaptureArchive(self.path)

    def test_zip64_extra_and_ambiguous_comment_are_rejected(self):
        for compression in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
            self.write(self.members, compression)
            with zipfile.ZipFile(self.path, "a") as archive:
                archive.comment = b"ordinary comment"
            self.assertEqual(self.members, self.read_all())
            with zipfile.ZipFile(self.path, "a") as archive:
                archive.comment = b"ambiguous PK\x05\x06 suffix"
            with self.assertRaises((ValueError, zipfile.BadZipFile)):
                CaptureArchive(self.path)
            self.write(self.members, compression)
            with zipfile.ZipFile(self.path, "a") as archive:
                archive.comment = b"\x05\x06" + bytes(19278)
            with self.assertRaisesRegex(ValueError, "ambiguous"):
                CaptureArchive(self.path)
        with zipfile.ZipFile(self.path, "w") as archive:
            entry = zipfile.ZipInfo("session.txt")
            entry.extra = struct.pack("<HH", 1, 0)
            archive.writestr(entry, b"small")
        with self.assertRaisesRegex(ValueError, "ZIP64"):
            CaptureArchive(self.path)

    def test_decoding_deadline_is_retryable(self):
        member = Member("session.txt", 1, zipfile.ZipInfo("session.txt"))
        with _Verified(io.BytesIO(b"x"), member) as source:
            with patch("app.capture_archive.time.monotonic", side_effect=[0, 11]):
                with self.assertRaises(TimeoutError):
                    source.readinto(bytearray(1))

    @unittest.skipUnless(os.environ.get("MSFSLS_ARCHIVE_FIXTURE"), "explicit net48 writer fixture required")
    def test_actual_net48_writer_ingestion_and_dashboard(self):
        path = Path(os.environ["MSFSLS_ARCHIVE_FIXTURE"])
        before = path.read_bytes()
        facts = validate_capture(path, self.header, 64 * 1024 * 1024, 7)
        self.assertEqual(40, facts.sample_count)
        row = {"compressed_bytes": len(before), "sha256": hashlib.sha256(before).hexdigest(), "sample_count": 40}
        indexed = _index_archive(path, row)
        self.assertEqual(40, indexed["telemetry"]["rows"])
        self.assertIn("controller-catalogs.json", {entry["name"] for entry in indexed["members"]})
        self.assertEqual(before, path.read_bytes())


if __name__ == "__main__":
    unittest.main()
