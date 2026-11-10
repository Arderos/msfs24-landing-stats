import io
import csv
import json
import unittest
import zipfile
import zlib
import time
import copy
import struct
import base64
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch

from app.dashboard_analytics import build_analytics, contact_intervals, interpolate, physical_series, project, quantiles, runway_frame, source_states
from app.capture_archive import CaptureArchive
from pathlib import Path
from tempfile import TemporaryDirectory
from app.dashboard_index import DashboardIndex


class FlightAnalyticsTests(unittest.TestCase):
    def record(self):
        return {'summary': {'Id': 'flight', 'AircraftTitle': 'Test aircraft', 'ContactNumber': 1,
                            'EstimatedContactTimeSeconds': 100, 'RawControllerAxesSourceIndices': [1]},
                'series': {'t': [-.4, -.2, 0, .2], 'agl': [30, 20, 10, 10], 'ground': [False, False, True, True],
                           'pitch': [-2, -3, -4, -4]},
                'inputs': {'value': [[10], [20], [30], [40]], 'valid': [[True]] * 4, 'age': [[.01]] * 4}}

    def test_one_crossing_per_flight_and_descending_only(self):
        points = [{'t': -.4 + i * .1, 'agl': h, 'cmdP': v, 'ground': 0} for i, (h, v) in enumerate([(30, 10), (20, 20), (25, 30), (10, 40)])]
        values = interpolate(points, 'agl', [15, 20, 25, 30, 35], ['cmdP'], True)['cmdP']
        self.assertAlmostEqual(36.6666667, values[0], places=4)
        self.assertAlmostEqual(33.3333333, values[1], places=4)
        self.assertIsNone(values[-1])

    def test_no_interpolation_across_missing_channels_gaps_or_segments(self):
        for last in ({'t': 1, 'run': 0, 'x': 20}, {'t': .2, 'run': 1, 'x': 20}, {'t': .2, 'run': 0, 'x': None}):
            values = interpolate([{'t': 0, 'run': 0, 'x': 10}, last], 't', [.1], ['x'])['x']
            self.assertEqual([None], values)

    def test_last_crossing_replaces_every_channel_including_unavailable_values(self):
        for missing in ('first', 'last', 'both', 'catalog', 'none'):
            with self.subTest(missing=missing):
                points = [{'t': t, 'agl': height, 'ground': 0, 'cmdP': command,
                           'stickP': stick, 'input_run': 0}
                          for t, height, command, stick in
                          [(-1, 40, 1, 10), (-.75, 20, 2, 20), (-.5, 40, 3, 30), (-.25, 20, 4, 40)]]
                if missing in ('first', 'both'):
                    points[2]['stickP'] = None
                if missing in ('last', 'both'):
                    points[3]['stickP'] = None
                if missing == 'catalog':
                    points[3]['input_run'] = 1
                values = interpolate(points, 'agl', [20, 30, 40], ['stickP', 'cmdP'], True)
                self.assertEqual([4, 3.5, 3], values['cmdP'])
                self.assertEqual([points[3]['stickP'], 35 if missing == 'none' else None,
                                  points[2]['stickP']], values['stickP'])

    def test_exact_observations_survive_adjacent_missing_input_and_catalog_boundaries(self):
        points = [{'t': 0, 'stickP': 10, 'input_run': 0},
                  {'t': .25, 'stickP': 20, 'input_run': 1},
                  {'t': .5, 'stickP': None, 'input_run': 1}]
        self.assertEqual([10, None, 20, None, None],
                         interpolate(points, 't', [0, .125, .25, .375, .5], ['stickP'])['stickP'])

    def test_final_height_crossing_in_a_physics_gap_clears_earlier_values(self):
        for gap in ('time', 'run'):
            with self.subTest(gap=gap):
                points = [{'t': t, 'agl': height, 'ground': 0, 'run': 0, 'cmdP': command}
                          for t, height, command in
                          [(-2, 40, 10), (-1.75, 20, 20), (-1.5, 40, 30), (-1.25, 20, 40)]]
                if gap == 'time':
                    points[-1]['t'] = -.25
                else:
                    points[-1]['run'] = 1
                # Exact final observations remain available; the unobserved
                # crossing must not inherit the first descent's command of 15.
                self.assertEqual([40, None, 30],
                                 interpolate(points, 'agl', [20, 30, 40], ['cmdP'], True)['cmdP'])

    def test_replay_and_cohort_tracks_keep_first_observation_after_missing_channel(self):
        record = self.record()
        record['series']['g'] = [1, None, 3, 4]
        data = self.build_record(record, {'g_force': [1, '', 3, 4],
                                         'velocity_world_y_fps': [-1, '', -3, -4]})
        self.assertEqual([1, None, 3, 4], data['series']['g'])
        self.assertEqual([60, None, 180, 240], data['series']['sink'])
        with TemporaryDirectory() as directory:
            base = Path(directory)
            index = DashboardIndex(base / 'data', base / 'cache')
            index._initialize_cache()
            with index._connect_cache() as connection:
                connection.execute('INSERT INTO flight_analytics VALUES(?,?)',
                                   ('hash', zlib.compress(json.dumps([data]).encode())))
            index._rows = [{'capture_id': 'capture', 'sha256': 'hash', 'received_at': time.time(),
                            'install_id': 'user', 'analysis_metadata': [data['meta']]}]
            self.assertEqual([60, None, 180, 240], index.analytics_cohort()['tracks'][0]['sink'])

    def test_percentiles_are_per_approach_not_per_sample(self):
        self.assertEqual([3, 10, 12.0, 20.0, 84.0, 100], quantiles([10, 20, 100]))
        self.assertIsNone(quantiles([]))

    def test_decimated_replay_and_cohort_keep_observations_after_selected_gaps(self):
        record = self.record()
        count = 1400
        record['series'] = {'t': [(i - 1200) / 100 for i in range(count)],
                            'agl': [100] * count, 'ground': [False] * count}
        sink = [-1] * count
        sink[6:10] = [''] * 4
        data = self.build_record(record, {'velocity_world_y_fps': sink})
        series = data['series']
        self.assertLessEqual(len(series['t']), 700)
        self.assertIsNone(series['sink'][series['t'].index(-11.92)])
        self.assertEqual(60, series['sink'][series['t'].index(-11.85)])
        # The second reduction is independent: retain a selected missing sample,
        # a missing interior sample, and a valid sample immediately afterward.
        for key in series:
            series[key] = [series[key][0]] * 240
        series['t'] = [i / 10 for i in range(240)]
        series['sink'] = [60] * 240
        series['sink'][2:5] = [None] * 3
        with TemporaryDirectory() as directory:
            base = Path(directory)
            index = DashboardIndex(base / 'data', base / 'cache'); index._initialize_cache()
            with index._connect_cache() as connection:
                connection.execute('INSERT INTO flight_analytics VALUES(?,?)',
                                   ('hash', zlib.compress(json.dumps([data]).encode())))
            index._rows = [{'capture_id': 'capture', 'sha256': 'hash', 'received_at': time.time(),
                            'install_id': 'user', 'analysis_metadata': [data['meta']]}]
            track = index.analytics_cohort()['tracks'][0]
            self.assertIsNone(track['sink'][track['t'].index(.3)])
            self.assertEqual(60, track['sink'][track['t'].index(.6)])

    def test_catalog_pages_bound_response_preserve_duplicates_without_aggregate_contention(self):
        with TemporaryDirectory() as directory:
            base = Path(directory)
            index = DashboardIndex(base / 'data', base / 'cache')
            metas = [{'id': f'flight-{i}', 'aircraft': f'Aircraft {i}'} for i in range(503)]
            now = time.time()
            index._rows = [{'capture_id': 'one', 'received_at': now, 'analysis_metadata': metas},
                           {'capture_id': 'copy', 'received_at': now - 1, 'analysis_metadata': metas[:10]}]
            pages = [index.analytics_catalog(page=page) for page in (1, 2, 3, 4)]
            self.assertEqual([250, 250, 3, 0], [len(page['landings']) for page in pages])
            self.assertEqual([2, 3, None, None], [page['next_page'] for page in pages])
            self.assertTrue(all(page['total'] == 503 for page in pages))
            self.assertEqual([meta['id'] for meta in metas],
                             [meta['id'] for page in pages for meta in page['landings']])
            with index._analytics_lock:
                with ThreadPoolExecutor(max_workers=2) as workers:
                    concurrent = list(workers.map(lambda _: index.analytics_catalog(), range(2)))
                self.assertTrue(all(result and result['total'] == 503 for result in concurrent))
            with self.assertRaises(ValueError):
                index.analytics_catalog(page_size=251)
            self.assertEqual(1, len(index.analytics_catalog(page_size=1)['landings']))

    def test_catalog_filters_cover_all_pages_and_periods_without_rescanning_metadata(self):
        class CountedMetadata(list):
            reads = 0

            def __iter__(self):
                self.reads += 1
                return super().__iter__()

        with TemporaryDirectory() as directory:
            base = Path(directory)
            index = DashboardIndex(base / 'data', base / 'cache')
            now = time.time()
            recent = CountedMetadata({'id': f'recent-{i}', 'aircraft': 'Recent', 'contact': 1,
                                      'airport': 'AAAA', 'runway': '27', 'devices': ['Stick']} for i in range(501))
            older = CountedMetadata([{'id': 'older', 'aircraft': 'Older', 'contact': 1,
                                       'airport': 'BBBB', 'runway': '09', 'devices': ['Pedals']}])
            index._rows = [{'capture_id': 'recent', 'received_at': now - 86400, 'creator_version': '1', 'analysis_metadata': recent},
                           {'capture_id': 'older', 'received_at': now - 10 * 86400, 'creator_version': '2',
                            'analytics_error': 'unavailable', 'analysis_metadata': older}]
            with patch('app.dashboard_index.time.time', return_value=now):
                first = index.analytics_catalog()
                self.assertEqual({'Recent': 501, 'Older': 1}, dict(first['filters']['aircraft']))
                self.assertEqual(['AAAA', 'BBBB'], first['filters']['airports'])
                self.assertEqual(['Pedals', 'Stick'], first['filters']['devices'])
                self.assertEqual(['1', '2'], first['filters']['creators'])
                self.assertIn(['BBBB', '09'], first['filters']['runways'])
                self.assertEqual(1, first['analytics_errors'])
                self.assertEqual(2, len(index.analytics_catalog(page=3)['landings']))
                week = index.analytics_catalog(days=7)
                self.assertEqual(501, week['total'])
                self.assertEqual([['Recent', 501]], week['filters']['aircraft'])
                self.assertEqual(0, week['analytics_errors'])
                self.assertEqual(1, len(index.analytics_catalog(days=7, page=3)['landings']))
                self.assertEqual(502, index.analytics_catalog(days=0, page=4)['total'])
                self.assertEqual((1, 1), (recent.reads, older.reads))
                # A published index replacement invalidates the materialized catalog.
                index._rows = index._rows[:1]
                self.assertEqual(501, index.analytics_catalog()['total'])
                self.assertEqual((2, 1), (recent.reads, older.reads))

    def test_physical_assignments_match_client_conflict_and_missing_input_semantics(self):
        def catalog(devices, bindings):
            def text(value):
                data = value.encode()
                return struct.pack('<i', len(data)) + data
            identities = {key: bytes([key]) * 16 for key in (1, 2, 3)}
            payload = bytearray(struct.pack('<I', 0x3243434C) + b'a' * 16 + b'b' * 16)
            payload.extend(struct.pack('<qqqqi', 1, 2, 100, 1, -len(devices) - 1))
            for slot, identity in enumerate(devices):
                payload.extend(struct.pack('<ii', slot, slot) + text(f'Device {slot}') + text('product'))
                payload.extend(struct.pack('<Bi', 255, 8) + identities[identity] + b'p' * 16)
                payload.extend(struct.pack('<II', 0, 65535) * 8)
            payload.extend(text('profile') + struct.pack('<i', len(bindings)))
            for role, identity, axis, inverted in bindings:
                payload.extend(struct.pack('<Bi16siB', role, -1, identities[identity], axis, inverted) + text('assignment'))
            return base64.b64encode(payload).decode()

        scenarios = [
            ('direct', [1, 2], [(0, 1, 1, 1)], [-10, -20, -30, -40], [None] * 4),
            ('within role', [1, 2], [(0, 1, 1, 0), (0, 2, 1, 0)], [11, 21, 31, 41], [None] * 4),
            ('across roles', [1, 2], [(0, 1, 1, 0), (1, 1, 1, 0)], [11, 21, 31, 41], [-10, -20, -30, -40]),
            ('duplicate identity', [1, 1], [(0, 1, 1, 0)], [-10, -20, -30, -40], [None] * 4),
            ('absent hardware', [2], [(0, 1, 1, 0)], [None] * 4, [None] * 4),
            ('missing measurements', [1, 2], [(0, 1, 2, 0)], [None] * 4, [None] * 4),
            ('duplicate assignment', [1, 2], [(0, 1, 1, 1), (0, 1, 1, 1)], [-10, -20, -30, -40], [None] * 4),
        ]
        for name, devices, bindings, pitch, roll in scenarios:
            with self.subTest(name=name):
                record = self.record()
                record['summary'].update(ControllerAxisCount=8, RawControllerAxesSourceIndices=[1, 9])
                record['inputs'] = {'value': [[10, 11], [20, 21], [30, 31], [40, 41]],
                                    'valid': [[True, True]] * 4, 'age': [[.01, .01]] * 4}
                record['controller_catalogs'] = {'version': 1, 'samples': 4,
                                                 'catalogs': [catalog(devices, bindings)], 'transitions': [[0, 0]]}
                entries = [{'component': 'replay.inputs', 'code': 'axis_selected',
                            'details': {'role': role, 'source_index': source, 'correlation': correlation,
                                        'contact_number': 1, 'catalog_index': 0}}
                           for role, source, correlation in [((0, 1, -1) if name == 'duplicate identity' else (0, 9, 1)),
                                                             *([(1, 1, -1)] if name == 'across roles' else [])]]
                values, _ = physical_series(record, entries)
                self.assertEqual(pitch, values['stickP'])
                self.assertEqual(roll, values['stickR'])
                if name in ('within role', 'across roles', 'duplicate identity'):
                    without_decision, _ = physical_series(record, [])
                    self.assertEqual([None] * 4, without_decision['stickP'])

    def test_physical_axis_keeps_amplitude_polarity_and_freshness(self):
        record = self.record()
        record['inputs']['age'][1] = [.26]
        entries = [{'component': 'replay.inputs', 'code': 'axis_selected', 'details': {'role': 0, 'source_index': 1, 'correlation': -.9, 'contact_number': 1}}]
        values, _ = physical_series(record, entries)
        self.assertEqual([-10, None, -30, -40], values['stickP'])
        self.assertEqual([None] * 4, values['stickR'])

    def test_device_labels_keep_valid_slots_and_axis_values_for_all_json_types(self):
        entries = [{'component': 'replay.inputs', 'code': 'axis_selected',
                    'details': {'role': role, 'source_index': source, 'correlation': 1, 'contact_number': 1}}
                   for role, source in ((0, 1), (1, 7))]
        valid = '  Pedals Δ\t'
        malformed = (None, True, False, 7, 0, .5, [], [1], {}, {'name': 'Stick'})
        for legacy in (False, True):
            for label in (*malformed, '', '  ', 'Stick Δ', 'x' * 1024):
                with self.subTest(legacy=legacy, label=label):
                    record = self.record()
                    record['summary'].update(ControlInputSources=[label, valid],
                                             RawControllerAxesSourceIndices=[1, 7])
                    values = [[10, 11], [20, 21], [30, 31], [40, 41]]
                    record['inputs'] = {'value': values, 'valid': [[True, True]] * 4, 'age': [[.01, .01]] * 4}
                    if legacy:
                        record['summary'].pop('RawControllerAxesSourceIndices')
                        record['summary']['RawControllerSourceIndices'] = [0, 1]
                        record['series'].update(rawY=values, rawYOk=[[True, True]] * 4, rawYAge=[[.01, .01]] * 4)
                    physical, labels = physical_series(record, entries)
                    self.assertEqual([10, 20, 30, 40], physical['stickP'])
                    self.assertEqual([11, 21, 31, 41], physical['stickR'])
                    expected = [valid, label] if isinstance(label, str) and label else [valid]
                    self.assertEqual(sorted(expected), labels)
            for container in (None, True, 7, .5, 'Stick', {0: 'Stick'}, {'0': 'Stick'}):
                with self.subTest(legacy=legacy, container=container):
                    record['summary']['ControlInputSources'] = container
                    physical, labels = physical_series(record, entries)
                    self.assertEqual([10, 20, 30, 40], physical['stickP'])
                    self.assertEqual([], labels)

    def test_catalog_device_labels_keep_exact_text_and_take_precedence_across_transitions(self):
        def catalog(label):
            def encoded_text(value):
                data = value.encode('utf-8')
                return struct.pack('<i', len(data)) + data
            payload = bytearray(struct.pack('<I', 0x3143434C) + b'a' * 16 + b'b' * 16)
            payload.extend(struct.pack('<qqqqi', 1, 2, 100, 1, 1))
            payload.extend(struct.pack('<ii', 0, 0) + encoded_text(label) + encoded_text('product'))
            payload.extend(bytes([63]) + struct.pack('<II', 0, 65535) * 6)
            return base64.b64encode(payload).decode('ascii')

        for label in ('  Stick Δ\t', '  ', 'x' * 512, ''):
            with self.subTest(label=label):
                record = self.record()
                record['summary']['ControlInputSources'] = [{'invalid': 'label'}]
                record['controller_catalogs'] = {'version': 1, 'samples': 4, 'catalogs': [catalog(label)],
                                                 'transitions': [[0, 0], [2, -1]]}
                entries = [{'component': 'replay.inputs', 'code': 'axis_selected',
                            'details': {'role': 0, 'source_index': 1, 'correlation': 1, 'catalog_index': context}}
                           for context in (0, -1)]
                physical, labels = physical_series(record, entries)
                self.assertEqual([10, 20, 30, 40], physical['stickP'])
                self.assertEqual([label] if label else [], labels)
                record['summary']['ControlInputSources'] = ['Summary fallback']
                self.assertEqual(sorted({label, 'Summary fallback'} - {''}), physical_series(record, entries)[1])

    def test_axis_binding_from_other_contact_is_not_reused(self):
        values, _ = physical_series(self.record(), [{'component': 'replay.inputs', 'code': 'axis_selected', 'details': {'role': 0, 'source_index': 1, 'correlation': 1, 'contact_number': 2}}])
        self.assertEqual([None] * 4, values['stickP'])

    def test_runway_direction_displacement_and_dateline(self):
        summary = {'Runway': '27', 'RunwayGeometry': {'LatitudeDegrees': 0, 'LongitudeDegrees': 179.999,
                   'HeadingTrueDegrees': 90, 'LengthMeters': 2000, 'WidthMeters': 45,
                   'PrimaryName': '09', 'SecondaryName': '27', 'SecondaryThresholdOffsetMeters': 200}}
        frame = runway_frame(summary)
        self.assertEqual(270, frame['heading'])
        along, cross = project(0, -179.999, frame)
        self.assertAlmostEqual(800 - 222.39016, along, places=3)
        self.assertAlmostEqual(0, cross, places=5)
        del summary['RunwayGeometry']['SecondaryThresholdOffsetMeters']
        self.assertIsNone(runway_frame(summary))

    def build(self, inactive=False, interrupted=False, source=None, records=None):
        with TemporaryDirectory() as directory:
            path = Path(directory) / 'capture.zip'
            with zipfile.ZipFile(path, 'w') as archive:
                header = 'simulation_time_s,analysis_time_s,agl_ft,on_ground,motion_simulation,simulator_state,pause_flags,flight_segment,pitch_deg,velocity_world_y_fps,ground_speed_kt\n'
                rows = []
                for i, t in enumerate((99.6, 99.8, 100, 100.2)):
                    active = not inactive or i != 2
                    rows.append(f'{t},{t},{30 - min(i,2)*10},{int(i>=2)},{int(active)},1,0,{2 if interrupted and i>=2 else 1},-4,-5,120\n')
                archive.writestr('telemetry.csv', header + ''.join(rows))
                if source is not None:
                    archive.writestr('source-state.bin', source)
            with CaptureArchive(path, 128 * 1024 * 1024) as archive:
                return build_analytics(archive, {'records': records or [self.record()]}, [])

    def build_record(self, record, raw_columns=None, entries=()):
        columns = {'analysis_time_s': [100 + t for t in record['series']['t']],
                   'agl_ft': record['series']['agl'],
                   'on_ground': [int(value) for value in record['series']['ground']]}
        columns.update(raw_columns or {})
        rows = [{'motion_simulation': 1, 'simulator_state': 1, 'pause_flags': 0,
                 'flight_segment': 1, **{key: values[i] for key, values in columns.items()}}
                for i in range(len(columns['analysis_time_s']))]
        text = io.StringIO(newline='')
        writer = csv.DictWriter(text, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
        with TemporaryDirectory() as directory:
            path = Path(directory) / 'capture.zip'
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('telemetry.csv', text.getvalue())
            with CaptureArchive(path, 128 * 1024 * 1024) as archive:
                return build_analytics(archive, {'records': [record]}, entries)[0]

    def test_raw_and_saved_control_percentages_share_the_writer_contract(self):
        record = self.record()
        saved = {'flap': 50, 'defElev': 25, 'defAilL': -20, 'defRud': 10,
                 'cmdP': -30, 'cmdR': 40, 'cmdY': 15}
        record['series'].update({key: [value] * 4 for key, value in saved.items()})
        raw = {'flaps_left_percent': .25, 'flaps_right_percent': .75,
               'brake_left_position': .4, 'brake_right_position': .8,
               'elevator_deflection_percent_over_100': .25,
               'aileron_left_deflection_percent_over_100': -.2,
               'rudder_deflection_percent_over_100': .1,
               'pilot_pitch_input_percent': -30, 'pilot_roll_input_percent': 40,
               'rudder_pedal_input_percent': 15}
        expected = {'flap': 50, 'elev': 25, 'ail': -20, 'rud': 10,
                    'cmdP': -30, 'cmdR': 40, 'cmdY': 15}
        for legacy in (False, True):
            with self.subTest(legacy=legacy):
                if legacy:
                    record['summary'].pop('EstimatedContactTimeSeconds')
                built = self.build_record(record, {key: [value] * 4 for key, value in raw.items()})
                self.assertEqual('legacy-state-unavailable' if legacy else 'recorded-physics', built['meta']['quality'])
                for key, value in expected.items():
                    self.assertEqual([value] * 4, built['series'][key], key)
                    self.assertEqual(value, built['profiles']['t'][key][300], key)
                    self.assertEqual(value, built['profiles']['agl'][key][2], key)
                self.assertEqual(50, built['meta']['flap'])
                if not legacy:
                    self.assertEqual([60] * 4, built['series']['brake'])
                    self.assertEqual(60, built['profiles']['t']['brake'][300])

    def test_raw_flaps_require_both_sides_and_brakes_keep_available_side_mean(self):
        for left, right, flap, brake in ((0, 0, 0, 0), (.25, .75, 50, 50),
                                          (None, .75, None, 75), (.25, None, None, 25),
                                          (None, None, None, None)):
            with self.subTest(left=left, right=right):
                built = self.build_record(self.record(), {
                    'flaps_left_percent': [left] * 4, 'flaps_right_percent': [right] * 4,
                    'brake_left_position': [left] * 4, 'brake_right_position': [right] * 4})
                self.assertEqual([flap] * 4, built['series']['flap'])
                self.assertEqual(flap, built['meta']['flap'])
                self.assertEqual(flap, built['profiles']['t']['flap'][300])
                self.assertEqual([brake] * 4, built['series']['brake'])

    def test_missing_raw_and_saved_percent_channels_stay_missing(self):
        for legacy in (False, True):
            for missing in (None, -1.7976931348623157e308):
                with self.subTest(legacy=legacy, missing=missing):
                    record = self.record()
                    if legacy:
                        record['summary'].pop('EstimatedContactTimeSeconds')
                    record['series'].update({key: [missing] * 4 for key in ('flap', 'defElev', 'defAilL', 'defRud')})
                    raw = {key: [missing] * 4 for key in
                           ('flaps_left_percent', 'flaps_right_percent', 'brake_left_position', 'brake_right_position',
                            'elevator_deflection_percent_over_100', 'aileron_left_deflection_percent_over_100',
                            'rudder_deflection_percent_over_100')}
                    built = self.build_record(record, raw)
                    for key in ('flap', 'brake', 'elev', 'ail', 'rud'):
                        self.assertEqual([None] * 4, built['series'][key], key)
                        self.assertIsNone(built['profiles']['t'][key][300], key)
                        self.assertNotIn(key, built['meta']['channels'])
                    self.assertIsNone(built['meta']['flap'])

    def test_public_profiles_keep_final_crossing_inputs_aligned(self):
        for legacy in (False, True):
            for unavailable in ('missing', 'stale', 'catalog', 'none'):
                with self.subTest(legacy=legacy, unavailable=unavailable):
                    record = self.record()
                    if legacy:
                        record['summary'].pop('EstimatedContactTimeSeconds')
                    times, heights = [-1, -.75, -.5, -.25, 0], [40, 20, 40, 20, 5]
                    record['series'] = {'t': times, 'agl': heights, 'ground': [False] * 4 + [True],
                                        'cmdP': [1, 2, 3, 4, 5], 'defElev': [10, 20, None, None, 50]}
                    record['inputs'] = {'value': [[10], [20], [30], [40], [50]],
                                        'valid': [[True]] * 5, 'age': [[0]] * 5}
                    if unavailable == 'missing':
                        record['inputs']['valid'][2:4] = [[False], [False]]
                    elif unavailable == 'stale':
                        record['inputs']['age'][2:4] = [[.251], [.251]]
                    contexts = [-1]
                    if unavailable == 'catalog':
                        record['controller_catalogs'] = {'version': 1, 'samples': 5,
                                                         'catalogs': ['', ''], 'transitions': [[0, 0], [3, 1]]}
                        contexts = [0, 1]
                    entries = [{'component': 'replay.inputs', 'code': 'axis_selected',
                                'details': {'role': 0, 'source_index': 1, 'correlation': 1,
                                            'contact_number': 1, 'catalog_index': context}} for context in contexts]
                    built = self.build_record(record, {
                        'pilot_pitch_input_percent': [1, 2, 3, 4, 5],
                        'elevator_deflection_percent_over_100': [.1, .2, None, None, .5]}, entries)
                    self.assertEqual(3.5, built['profiles']['agl']['cmdP'][3])
                    self.assertEqual(35 if unavailable == 'none' else None, built['profiles']['agl']['stickP'][3])
                    self.assertIsNone(built['profiles']['agl']['elev'][3])
                    self.assertEqual([10, 20, None, None] if unavailable in ('missing', 'stale') else [10, 20, 30, 40],
                                     built['series']['stickP'][:4])
                    # The display series marks missing intervals separately;
                    # the time profile retains the observed contact sample.
                    self.assertEqual(50, built['profiles']['t']['stickP'][300])
                    self.assertEqual([None] * 5, built['series']['stickR'])
                    self.assertIsNone(built['profiles']['agl']['stickR'][3])

    def test_raw_frames_interpolate_only_within_current_input_catalog(self):
        record = self.record()
        record['series'] = {'t': [-.5, -.25, 0, .25], 'agl': [40, 20, 5, 5],
                            'ground': [False, False, True, True]}
        record['controller_catalogs'] = {'version': 1, 'samples': 4,
                                         'catalogs': ['', ''], 'transitions': [[0, 0], [2, 1]]}
        entries = [{'component': 'replay.inputs', 'code': 'axis_selected',
                    'details': {'role': 0, 'source_index': 1, 'correlation': 1,
                                'contact_number': 1, 'catalog_index': context}} for context in (0, 1)]
        built = self.build_record(record, {
            'analysis_time_s': [99.5, 99.625, 99.75, 99.875, 100, 100.125, 100.25],
            'agl_ft': [40, 30, 20, 12.5, 5, 5, 5], 'on_ground': [0, 0, 0, 0, 1, 1, 1],
            'pilot_pitch_input_percent': [1, 1.5, 2, 2.5, 3, 3.5, 4]}, entries)
        self.assertEqual([10, 15, 20, None], built['series']['stickP'][:4])
        self.assertEqual(15, built['profiles']['agl']['stickP'][3])
        self.assertIsNone(built['profiles']['t']['stickP'][299])
        self.assertEqual(2.6, built['profiles']['t']['cmdP'][299])
        self.assertEqual(30, built['profiles']['t']['stickP'][300])
        self.assertEqual(34, built['profiles']['t']['stickP'][301])

    @staticmethod
    def source_frame(mask=0, slew=0, sdk=0, age=0, availability=2):
        return b'\1' + struct.pack('<BbH16sqqqqqBbbBII7d', 1, sdk, 0, b'w' * 16,
                                 1, 1, 10000, 10000 + round(age * 1000), 1000, availability,
                                 mask if availability == 2 else -1, slew if availability == 2 else -1,
                                 0 if availability == 2 else 6, 1, 0, *([1.] * 7))

    def test_confirmed_freeze_slew_and_sdk_replay_excluded_at_contact(self):
        for invalid in (self.source_frame(mask=1), self.source_frame(mask=7), self.source_frame(slew=1), self.source_frame(sdk=1)):
            with self.subTest(state=invalid):
                source = b'LSR1' + struct.pack('<i', 4) + self.source_frame() * 2 + invalid + self.source_frame()
                self.assertEqual([], self.build(source=source))

    def test_stale_missing_and_gap_provenance_stays_unknown(self):
        for unknown in (b'\0', self.source_frame(mask=7, age=1.501), self.source_frame(mask=7, age=-.1), self.source_frame(sdk=1, availability=4)):
            source = b'LSR1' + struct.pack('<i', 4) + unknown * 4
            result = self.build(source=source)
            self.assertEqual(1, len(result))
            self.assertEqual(0, result[0]['meta']['source_checked_frames'])
        released = b'LSR1' + struct.pack('<i', 4) + self.source_frame(age=1.5) * 4
        self.assertEqual(4, self.build(source=released)[0]['meta']['source_checked_frames'])

    def test_source_row_count_must_match_even_with_unknown_observations(self):
        with self.assertRaisesRegex(ValueError, 'does not match telemetry rows'):
            self.build(source=b'LSR1' + struct.pack('<i', 3) + b'\0' * 3)

    def test_contact_sequence_keeps_episode_clock_and_saved_window(self):
        first, bounce = self.record(), self.record()
        first['summary'].update(RecordingEpisodeId='episode', ContactCount=2, ImpactWindowSeconds=.15)
        bounce['summary'].update(Id='bounce', RecordingEpisodeId='episode', ContactNumber=2,
                                 ContactCount=2, EstimatedContactTimeSeconds=100.2, ImpactWindowSeconds=.07)
        meta = self.build(records=[first, bounce])[0]['meta']
        self.assertEqual([1, 2], [event['contact'] for event in meta['contact_events']])
        self.assertAlmostEqual(.2, meta['contact_events'][1]['t'])
        self.assertEqual(.15, meta['impact_window'])

    def test_result_metadata_accepts_strings_without_coercing_other_json_types(self):
        fields = {'Id': 'id', 'RecordingEpisodeId': 'episode', 'AircraftTitle': 'aircraft',
                  'Airport': 'airport', 'Runway': 'runway', 'Simulator': 'simulator',
                  'TimestampUtc': 'timestamp'}
        for value in (None, True, False, 27, 1.5, [], ['x'], {}, {'name': 'x'}):
            record = self.record()
            record['summary'].update(dict.fromkeys(fields, value))
            with self.subTest(value=value):
                result = self.build(records=[record, self.record()])
                self.assertEqual(2, len(result))
                self.assertTrue(all(result[0]['meta'][key] is None for key in fields.values()))
                self.assertEqual([], result[0]['meta']['contact_events'])
                self.assertEqual('flight', result[1]['meta']['id'])
                self.assertEqual([1], [event['contact'] for event in result[1]['meta']['contact_events']])
        for value in ('', '  A320 / Аэропорт 27  ', 'x' * 1024):
            record = self.record()
            record['summary'].update(dict.fromkeys(fields, value))
            with self.subTest(value=value):
                meta = self.build(records=[record])[0]['meta']
                self.assertTrue(all(meta[key] == value for key in fields.values()))

    def test_malformed_episode_falls_back_to_record_identity_and_contact_counts_stay_typed(self):
        record = self.record()
        for value in (None, True, 1.0, '1', 0, -1, [], {'count': 1}):
            record['summary'].update(RecordingEpisodeId=['x'], ContactNumber=value, ContactCount=value)
            with self.subTest(value=value):
                meta = self.build(records=[record])[0]['meta']
                self.assertEqual('flight', meta['episode'])
                self.assertIsNone(meta['contact'])
                self.assertIsNone(meta['contacts'])
                self.assertIsNone(meta['contact_events'][0]['contact'])
        record['summary'].update(ContactNumber=2, ContactCount=3)
        meta = self.build(records=[record])[0]['meta']
        self.assertEqual((2, 3), (meta['contact'], meta['contacts']))

    def test_contact_states_are_discrete_and_break_at_physics_gaps(self):
        contact = {'n': 2, 't': [-.2, 0, .2, .4, 1, 1.2], 'g': [False, True, False, True, True, True]}
        points = [{'t': t, 'run': 0 if t <= 0 else 1} for t in contact['t']]
        value = contact_intervals(contact, points)
        self.assertEqual(2, value['gear'])
        self.assertEqual([{'start': -.2, 'end': 0, 'ground': False},
                          {'start': .2, 'end': .4, 'ground': False},
                          {'start': 1, 'end': 1.2, 'ground': True}], value['intervals'])

    def test_real_raw_channels_and_polarity_are_preserved(self):
        result = self.build()
        self.assertEqual(1, len(result))
        self.assertEqual(4, result[0]['meta']['frames'])
        self.assertEqual([4] * 4, result[0]['series']['pitch'])
        self.assertEqual([300] * 4, result[0]['series']['sink'])
        self.assertEqual([None] * 4, result[0]['series']['ias'])
        self.assertEqual('recorded-physics', result[0]['meta']['quality'])

    def test_paused_contact_and_segment_change_do_not_enter_cohort(self):
        self.assertEqual([], self.build(inactive=True))
        self.assertEqual([], self.build(interrupted=True))

    def test_catalog_transition_does_not_blend_physical_axes(self):
        points = [{'t': 0, 'stickP': -90, 'input_run': 0}, {'t': .1, 'stickP': 90, 'input_run': 1}]
        self.assertEqual([None], interpolate(points, 't', [.05], ['stickP'])['stickP'])

    def test_cache_cohort_deduplication_filters_and_read_only_source(self):
        with TemporaryDirectory() as directory:
            base = Path(directory)
            data = base / 'data'; data.mkdir()
            witness = data / 'archive.zip'; witness.write_bytes(b'original bytes')
            index = DashboardIndex(data, base / 'cache'); index._initialize_cache()
            first = self.build()[0]
            first['meta'].update(episode='episode', contact=1, aircraft='Test aircraft', crosswind=10)
            second = copy.deepcopy(first); second['meta'].update(id='bounce', contact=2)
            independent = copy.deepcopy(first); independent['meta'].update(id='other', episode='other-episode', crosswind=20)
            for capture, digest, records in [('one', 'hash-one', [first, second, independent]), ('duplicate', 'hash-two', [first])]:
                with index._connect_cache() as connection:
                    connection.execute('INSERT INTO flight_analytics VALUES(?,?)', (digest, zlib.compress(json.dumps(records).encode())))
                index._rows.append({'capture_id': capture, 'sha256': digest, 'received_at': time.time(), 'install_id': 'user',
                                    'creator_version': '1.0', 'app_version': '2.0', 'analysis_metadata': [r['meta'] for r in records]})
            self.assertEqual(3, len(index.analytics_catalog()['landings']))
            cohort = index.analytics_cohort(aircraft='Test aircraft', axis='t')
            self.assertEqual(2, cohort['matched'])
            self.assertEqual(2, cohort['included'])
            self.assertEqual(2, next(v for v in cohort['bands']['pitch'] if v)[0])
            self.assertEqual(1, index.analytics_cohort(wind_max=15)['included'])
            self.assertEqual(0, index.analytics_cohort(creator='missing')['included'])
            self.assertEqual(1, index.analytics_cohort(ids='other')['included'])
            self.assertIsNotNone(index.analytics_landing('flight'))
            self.assertIsNone(index.analytics_landing('../outside'))
            with index._analytics_lock:
                self.assertIsNone(index.analytics_cohort())
            self.assertEqual(b'original bytes', witness.read_bytes())
            self.assertEqual(['archive.zip'], [p.name for p in data.iterdir()])

    def test_display_profiles_are_bounded_without_changing_cohort_statistics(self):
        with TemporaryDirectory() as directory:
            base = Path(directory)
            index = DashboardIndex(base / 'data', base / 'cache'); index._initialize_cache()
            first = self.build()[0]
            records = []
            for i in range(62):
                record = copy.deepcopy(first)
                record['meta'].update(id=f'flight-{i}', episode=f'episode-{i}')
                records.append(record)
            # Display reduction must preserve endpoints and missing intervals.
            series = records[0]['series']
            for key in series:
                series[key] = [series[key][0]] * 240
            series['t'] = [i / 10 for i in range(240)]
            series['lat'] = [45] * 240; series['lat'][17] = None
            series['sink'] = [300] * 240; series['sink'][40] = None
            with index._connect_cache() as connection:
                connection.execute('INSERT INTO flight_analytics VALUES(?,?)', ('hash', zlib.compress(json.dumps(records).encode())))
            index._rows = [{'capture_id': 'one', 'sha256': 'hash', 'received_at': time.time(), 'install_id': 'user', 'analysis_metadata': [r['meta'] for r in records]}]
            plain = index.analytics_cohort()
            display = index.analytics_cohort(channels='pitch,sink')
            self.assertEqual(62, display['included'])
            self.assertEqual(plain['bands'], display['bands'])
            self.assertEqual([], plain['individuals'])
            self.assertEqual(50, len(display['individuals']))
            self.assertEqual({'pitch', 'sink'}, set(display['individuals'][0]['channels']))
            self.assertEqual(records[0]['profiles'], index.analytics_landing('flight-0')['profiles'])
            track = display['tracks'][0]
            self.assertLessEqual(len(track['t']), 100)
            self.assertEqual(23.9, track['t'][-1])
            self.assertIsNone(track['lat'][track['t'].index(1.8)])
            self.assertIsNone(track['sink'][track['t'].index(4.2)])
            with self.assertRaises(ValueError):
                index.analytics_cohort(channels='invalid')
            self.assertIsNotNone(index.analytics_cohort())  # Validation releases the query lock.


if __name__ == '__main__':
    unittest.main()
