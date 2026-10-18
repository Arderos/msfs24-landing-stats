import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

from app.bounded_json import JsonScanner, check_json_graph
from app.validation import validate_capture
from test_capture_archive import encode


class ValidationBudgetTests(unittest.TestCase):
    def test_scanner_matches_json_syntax_across_chunk_boundaries(self):
        values = [None, True, False, -10, 1.2e-50, [], {},
                  {"a": [None, {"b": "x" * 65533 + '\\\"\n\u1234'}]},
                  {"unicode": "\U0001f4a9", "key\\\"": "\\u1234"}]
        for value in values:
            data = json.dumps(value).encode()
            scanner = JsonScanner(io.BytesIO(data))
            scanner.value()
            scanner.finish()
        for data in (b"", b"[1,]", b'{"a":}', b'{"a" 1}', b"01", b"1e", b"true false",
                     b'"bad\\x"', b'"bad\\u123z"', b'"bad\n"', b'"unterminated', b'{}tail'):
            with self.subTest(data=data), self.assertRaises(ValueError):
                scanner = JsonScanner(io.BytesIO(data))
                scanner.value()
                scanner.finish()

    def test_structure_bounds_apply_without_json_materialization(self):
        for data, options in ((b"[" * 66 + b"0" + b"]" * 66, {}),
                              (b"[{}, {}, {}]", {"maximum_containers": 3}),
                              (b"[0,0,0]", {"maximum_values": 3})):
            with self.assertRaisesRegex(ValueError, "structure exceeds"):
                JsonScanner(io.BytesIO(data), **options).value()
        with self.assertRaisesRegex(ValueError, "structure exceeds"):
            check_json_graph(b"[" + b"{}," * 100000 + b"{}]")

    def test_excessive_results_rejected_before_graph_allocation_legacy_and_zstd(self):
        header = (Path(__file__).resolve().parents[1] / "schema-v7.header").read_text().strip()
        members = {"session.txt": b"telemetry_schema=7\nsource_period=SIM_FRAME\ncapture_kind=automatic_landing\nlanding_count=4000000\nepisode_id=1\nbug_report_format=1\n",
                   "landing-results.json": b'{"format_version":1,"episode_id":1,"records":[' + b"{}," * 3999999 + b"{}]}",
                   "telemetry.csv": (header + "\n").encode()}
        with tempfile.TemporaryDirectory() as root:
            for version in (1, 2):
                path = Path(root) / f"v{version}.zip"
                with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
                    for name, data in (members if version == 1 else encode(members)).items():
                        archive.writestr(name, data)
                with mock.patch("app.validation.json.loads", side_effect=AssertionError("JSON graph was allocated")):
                    with self.assertRaisesRegex(ValueError, "result metadata is invalid"):
                        validate_capture(path, header, 64 * 1024 * 1024, 7)

    def test_result_record_count_and_envelope_are_checked(self):
        for content in (b'{"format_version":1,"episode_id":1,"records":[{},{}]}',
                        b'{"format_version":1,"records":[1]}',
                        b'{"records":[],"records":[]}'):
            with self.assertRaises(ValueError):
                JsonScanner(io.BytesIO(content)).result_envelope(1)
        data = b'{"format_version":1,"episode_id":42,"records":[{"series":[1,2,3]}]}'
        self.assertEqual({"format_version": 1, "episode_id": 42, "records": 1},
                         JsonScanner(io.BytesIO(data)).result_envelope(1))

    def test_full_retained_controller_columns_are_streamed_without_graph_allocation(self):
        # A v7 record stores three legacy Y columns and three full-axis input
        # columns as per-sample arrays. 65,536 rows legitimately exceed 262k
        # containers even with only one controller axis.
        column = b"[" + b"[0]," * 65535 + b"[0]]"
        record = b'{"series":{"rawY":' + column + b',"rawYOk":' + column + b',"rawYAge":' + column
        record += b'},"inputs":{"value":' + column + b',"valid":' + column + b',"age":' + column + b'}}'
        scanner = JsonScanner(io.BytesIO(b'{"format_version":1,"episode_id":42,"records":[' + record + b']}'))
        self.assertEqual(1, scanner.result_envelope(1)["records"])
        self.assertGreater(scanner.containers, 6 * 65536)


if __name__ == "__main__":
    unittest.main()
