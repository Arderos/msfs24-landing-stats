import io
import json
import unittest
import zipfile
import zlib
import time
import copy
import struct

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

    def test_percentiles_are_per_approach_not_per_sample(self):
        self.assertEqual([3, 10, 12.0, 20.0, 84.0, 100], quantiles([10, 20, 100]))
        self.assertIsNone(quantiles([]))

    def test_physical_axis_keeps_amplitude_polarity_and_freshness(self):
        record = self.record()
        record['inputs']['age'][1] = [.26]
        entries = [{'component': 'replay.inputs', 'code': 'axis_selected', 'details': {'role': 0, 'source_index': 1, 'correlation': -.9, 'contact_number': 1}}]
        values, _ = physical_series(record, entries)
        self.assertEqual([-10, None, -30, -40], values['stickP'])
        self.assertEqual([None] * 4, values['stickR'])

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
