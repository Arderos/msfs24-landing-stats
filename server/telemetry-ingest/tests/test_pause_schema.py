import tempfile
import unittest
import zipfile
from pathlib import Path

from app.validation import validate_capture


ROOT = Path(__file__).resolve().parents[1]
HEADER_V6 = (ROOT / "schema-v6.header").read_text(encoding="utf-8").strip()
HEADER = (ROOT / "schema-v7.header").read_text(encoding="utf-8").strip()
COLUMNS = HEADER.split(",")


class PauseSchemaTests(unittest.TestCase):
    def _validate(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "pause.zip"
            with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
                lines = []
                for sequence, overrides in enumerate(rows, 1):
                    values = {column: "0" for column in COLUMNS}
                    values.update(sequence=str(sequence), analysis_time_s="NaN", pause_flags="-1")
                    values.update({key: str(value) for key, value in overrides.items()})
                    lines.append(",".join(values[column] for column in COLUMNS))
                archive.writestr("telemetry.csv", HEADER + "\n" + "\n".join(lines) + "\n")
                archive.writestr("session.txt", "telemetry_schema=7\nsource_period=SIM_FRAME\n"
                                 f"sample_count={len(rows)}\n")
            return validate_capture(path, HEADER, 64 * 1024 * 1024, 7)

    @staticmethod
    def _replay_rows():
        return [dict(simulation_time_s=i, analysis_time_s=i, motion_simulation=1,
                     simulator_state=1, pause_flags=0, on_ground=int(i == 17),
                     latitude_deg=42 - i / 1800, longitude_deg=23,
                     plane_altitude_ft=1200 - 10 * i, airspeed_indicated_kt=5)
                for i in range(18)]

    def test_v7_extends_v6_without_changing_existing_columns(self):
        self.assertEqual(HEADER_V6 + ",analysis_time_s,simulator_state,pause_flags,flight_segment", HEADER)
        self.assertEqual(284, len(COLUMNS))

    def test_default_legacy_metadata_and_known_states_are_accepted(self):
        rows = [{}] + [dict(simulator_state=state, pause_flags=flags, analysis_time_s=12.5,
                            flight_segment=2**63 - 1)
                       for state, flags in ((1, 0), (2, 1), (2, 4), (2, 8), (3, -1), (2, 2**31 - 1))]
        self.assertEqual(len(rows), self._validate(rows).sample_count)

    def test_state_fields_reject_invalid_values(self):
        invalid = {
            "simulator_state": ["-1", "4", "0.5", "NaN", "Infinity"],
            "pause_flags": ["-2", str(2**31), "1.5", "NaN"],
            "flight_segment": ["-1", str(2**63), "0.5", "NaN"],
            "analysis_time_s": ["Infinity", "-Infinity", "invalid"],
            "g_force": ["NaN"],
        }
        for field, values in invalid.items():
            for value in values:
                with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                    self._validate([{field: value}])

    def test_paused_frames_are_not_replay_kinematic_evidence(self):
        rows = self._replay_rows()
        for row in rows[1:16]:
            row.update(simulator_state=2, pause_flags=4, motion_simulation=0)
        self.assertEqual(18, self._validate(rows).sample_count)

    def test_unknown_physics_stops_and_flight_segments_break_replay_evidence(self):
        for interruption in ("motion", "inactive", "segment"):
            rows = self._replay_rows()
            if interruption == "motion":
                rows[12]["motion_simulation"] = 0
            elif interruption == "inactive":
                rows[12]["simulator_state"] = 3
            else:
                for row in rows[12:]:
                    row["flight_segment"] = 1
            with self.subTest(interruption=interruption):
                self.assertEqual(18, self._validate(rows).sample_count)

    def test_confirmed_pause_does_not_hide_real_replay_after_time_stitching(self):
        rows = self._replay_rows()
        for row in rows[9:]:
            row["simulation_time_s"] += 600
        rows.insert(9, dict(rows[8], simulation_time_s=400, simulator_state=2,
                            motion_simulation=0, pause_flags=1))
        with self.assertRaisesRegex(ValueError, "replay-like telemetry"):
            self._validate(rows)

    def test_legacy_metadata_falls_back_to_original_clock(self):
        rows = self._replay_rows()
        for row in rows:
            row.update(analysis_time_s="NaN", simulator_state=0, pause_flags=-1)
        with self.assertRaisesRegex(ValueError, "replay-like telemetry"):
            self._validate(rows)

    def test_frozen_time_resume_cannot_create_contact_for_replay_detection(self):
        rows = self._replay_rows()
        rows[-1]["analysis_time_s"] = rows[-2]["analysis_time_s"]
        rows.insert(17, dict(rows[16], simulator_state=2, motion_simulation=0, pause_flags=1))
        self.assertEqual(19, self._validate(rows).sample_count)


if __name__ == "__main__":
    unittest.main()
