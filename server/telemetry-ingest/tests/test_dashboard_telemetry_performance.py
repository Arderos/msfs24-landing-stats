"""Summary parsing retains its diagnostics when processing numeric rows in bulk."""

import base64
import csv
import io
import struct
import unittest
import zipfile

from app.dashboard_index import _telemetry


class TelemetrySummaryTests(unittest.TestCase):
    def summary(self, header, rows):
        text = io.StringIO(newline="")
        writer = csv.writer(text)
        writer.writerow(header)
        writer.writerows(rows)
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as archive:
            archive.writestr("telemetry.csv", text.getvalue())
        data.seek(0)
        with zipfile.ZipFile(data) as archive:
            return _telemetry(archive)

    def controller(self, count=1):
        device = struct.pack("<i", 0) + b"".join(
            struct.pack("<dBd", 12.5 if axis == 2 else 0, axis == 2, .1)
            for axis in range(7))
        return base64.b64encode(struct.pack("<Hii", 2, count, 6) + device * count).decode("ascii")

    def test_numeric_rows_preserve_clock_motion_and_channel_diagnostics(self):
        result = self.summary(
            ["sequence", "simulation_time_s", "host_elapsed_s", "motion_simulation", "g_force"],
            [[1, 1, 1, 1, .9], [2, 1, 1, 0, 1], [2, .5, 1.0005, -1, 1.2], [3, 2, 2, 1, 1.1]])
        self.assertEqual(4, result["rows"])
        self.assertEqual(3, result["unique_timestamps"])
        self.assertEqual(1, result["duplicate_timestamps"])
        self.assertEqual(1, result["backward_timestamps"])
        self.assertEqual(1, result["gaps_over_1s"])
        self.assertEqual(1, result["sequence_errors"])
        self.assertEqual(1, result["duplicate_delivery_timestamps"])
        self.assertEqual(2, result["delivery_intervals_under_1ms"])
        self.assertEqual((2, 1, 1), tuple(result[key] for key in
                         ("active_motion_frames", "inactive_motion_frames", "unknown_motion_frames")))
        self.assertEqual({"min": .9, "max": 1.2}, result["channel_ranges"]["g_force"])
        self.assertEqual((0, 0), (result["malformed_values"], result["nonfinite_values"]))

    def test_missing_markers_and_invalid_values_keep_distinct_counts(self):
        result = self.summary(
            ["simulation_time_s", "analysis_time_s", "axis_elevator_set_valid",
             "axis_elevator_set_age_s", "g_force", "other", "another"],
            [[0, "NaN", 0, "NaN", 1, 0, 0],
             [1, "NaN", 1, "NaN", "Infinity", "", "bad"],
             [2, 2, 0, "NaN", "1e9999", "-Infinity", "NaN"],
             [3, 3, 1, .1, -1.7976931348623157e308, 0, 0]])
        self.assertEqual(2, result["malformed_values"])
        self.assertEqual(5, result["nonfinite_values"])
        self.assertEqual({"min": -1.7976931348623157e308, "max": 1}, result["channel_ranges"]["g_force"])

    def test_controller_blob_is_excluded_at_every_column_position(self):
        for position in range(4):
            for extra in ("2", "bad", "NaN"):
                with self.subTest(position=position, extra=extra):
                    header = ["simulation_time_s", "g_force", "other"]
                    row = ["0", "1", extra]
                    header.insert(position, "controller_data_v2")
                    row.insert(position, self.controller())
                    result = self.summary(header, [row])
                    self.assertEqual(["controller_0_raw_y"], result["physical_axis_channels"])
                    self.assertEqual(int(extra == "bad"), result["malformed_values"])
                    self.assertEqual(int(extra == "NaN"), result["nonfinite_values"])

    def test_empty_controller_blob_retains_legacy_axis_validity(self):
        result = self.summary(
            ["simulation_time_s", "controller_data_v2", "controller_3_raw_z_valid"], [[0, "", 1]])
        self.assertEqual(["controller_3_raw_z"], result["physical_axis_channels"])
        self.assertEqual(0, result["malformed_values"])

    def test_large_valid_controller_blob_does_not_consume_numeric_row_budget(self):
        blob = self.controller(150)
        self.assertGreater(len(blob), 16384)
        result = self.summary(["simulation_time_s", "controller_data_v2"], [[0, blob]])
        self.assertEqual(150, len(result["physical_axis_channels"]))
        self.assertEqual(0, result["malformed_values"])

    def test_numeric_row_budget_and_shape_remain_enforced(self):
        header = ["simulation_time_s", "other"]
        self.assertEqual(1, self.summary(header, [[0, "0" * 16383]])["rows"])
        for row in ([0, "0" * 16384], [0], [0, 1, 2]):
            with self.subTest(width=len(row)):
                with self.assertRaisesRegex(ValueError, "invalid telemetry row width or size"):
                    self.summary(header, [row])

    def test_invalid_controller_blob_still_fails_validation(self):
        with self.assertRaisesRegex(ValueError, "controller observations are not valid base64"):
            self.summary(["controller_data_v2", "simulation_time_s"], [["invalid!", 0]])


if __name__ == "__main__":
    unittest.main()
