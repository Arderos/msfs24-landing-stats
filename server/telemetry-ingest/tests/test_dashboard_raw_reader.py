"""CSV projection keeps the raw analytics reader's established data contract."""

import csv
import io
import unittest
import zipfile
from unittest.mock import patch

from app.dashboard_analytics import RAW, SENTINEL, read_raw


class DashboardRawReaderTests(unittest.TestCase):
    def read(self, header, rows, states=None):
        csv_text = io.StringIO(newline='')
        writer = csv.writer(csv_text)
        writer.writerow(header)
        writer.writerows(rows)
        content = io.BytesIO()
        with zipfile.ZipFile(content, 'w') as archive:
            archive.writestr('telemetry.csv', '\ufeff' + csv_text.getvalue())
        with zipfile.ZipFile(io.BytesIO(content.getvalue())) as archive, \
                patch('app.dashboard_analytics.source_states', return_value=states):
            return read_raw(archive)

    def test_selected_channels_scaling_and_engine_limits_ignore_unused_fields(self):
        header = list(RAW.values())
        values = list(range(10, 10 + len(header)))
        extra = {'simulation_time_s': 17, 'on_ground': 1, 'flight_segment': 3,
                 'motion_simulation': 1, 'simulator_state': 1, 'pause_flags': 0,
                 'number_of_engines': 5, 'flaps_left_percent': .4, 'flaps_right_percent': .6,
                 'brake_left_position': .3, 'brake_right_position': .5,
                 'acceleration_body_x_fps2': 32.17404856}
        for engine in range(1, 6):
            extra[f'engine_{engine}_throttle_percent'] = 10 * engine
            extra[f'engine_{engine}_n1_percent'] = 20 * engine
        extra.update({f'ignored_device_{column}': 'not numeric' for column in range(200)})
        point, = self.read(header + list(extra), [values + list(extra.values())])
        factors = {'sink': -60, 'pitch': -1, 'elev': 100, 'ail': 100, 'rud': 100}
        for key, value in zip(RAW, values):
            self.assertEqual(value * factors.get(key, 1), point[key], key)
        for key, value in {'t': 17, 'ground': 1, 'segment': 3, 'valid': True,
                           'source_checked': False, 'throttle': 25, 'n1': 50,
                           'flap': 50, 'brake': 40, 'latG': 1}.items():
            self.assertEqual(value, point[key], key)
        self.assertFalse(any(key.startswith('ignored') for key in point))

    def test_analysis_time_falls_back_for_invalid_nonfinite_and_sentinel_values(self):
        rows = [[str(index), value] for index, value in enumerate(
            ('', 'NaN', 'Infinity', str(SENTINEL), 'not a time'), start=1)]
        rows.append(['6', '7', 'excess cell'])
        self.assertEqual([1, 2, 3, 4, 5, 7],
                         [point['t'] for point in self.read(['simulation_time_s', 'analysis_time_s'], rows)])

    def test_duplicate_headers_use_last_column_even_when_short_row_omits_it(self):
        points = self.read(['simulation_time_s', 'g_force', 'simulation_time_s', 'g_force'],
                           [['1', '3', '2', '4'], ['3', '9']], states=[True, False])
        self.assertEqual(1, len(points))
        self.assertEqual(2, points[0]['t'])
        self.assertEqual(4, points[0]['g'])
        self.assertTrue(points[0]['valid'])

    def test_blank_records_quotes_and_invalid_times_keep_source_row_alignment(self):
        points = self.read(['simulation_time_s', 'pause_flags', 'g_force', 'unused'],
                           [['1', '0', '1', 'quoted, value\nsecond line'], [],
                            ['1', '0', '2', 'last duplicate'], ['not a time', '0', '9', ''],
                            ['2', '0', '3', '']], states=[True, False, True, None])
        self.assertEqual([1, 2], [point['t'] for point in points])
        self.assertEqual([2, 3], [point['g'] for point in points])
        self.assertEqual([False, True], [point['valid'] for point in points])
        self.assertEqual([True, False], [point['source_checked'] for point in points])

    def test_missing_cells_and_unavailable_values_stay_missing(self):
        points = self.read(['simulation_time_s', 'g_force', 'on_ground', 'pause_flags',
                            'motion_simulation', 'simulator_state'],
                           [['1', '2'], ['2', 'NaN', '', '', '1', '1'],
                            ['3', str(SENTINEL), '0', '1', '1', '1'],
                            ['4', '1', '0', '0', '0', '1'],
                            ['5', '1', '0', '0', '1', '3']])
        self.assertEqual([2, None, None, 1, 1], [point['g'] for point in points])
        self.assertEqual([True, True, False, False, False], [point['valid'] for point in points])
        self.assertIsNone(points[0]['ground'])
        self.assertTrue(all(point['flap'] is None and point['brake'] is None for point in points))

    def test_no_selected_columns_and_invalid_times_still_validate_source_count(self):
        self.assertEqual([], self.read(['unused'], [['x'], [], ['y']], states=[True, None]))
        self.assertEqual([], self.read([], [], states=[]))
        with self.assertRaisesRegex(ValueError, 'source metadata does not match telemetry rows'):
            self.read(['unused'], [['x'], [], ['y']], states=[True])


if __name__ == '__main__':
    unittest.main()
